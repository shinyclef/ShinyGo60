#include <string.h>

#include <zephyr/kernel.h>
#include <zephyr/random/random.h>
#include <zephyr/sys/util.h>

#include <shinygo60/layer_control.h>
#include <shinygo60/protocol.h>

#define LAYOUT_IDENTIFIER_PREFIX "sg60-v1-"
#define LAYOUT_IDENTIFIER_PREFIX_LENGTH 8U
#define LAYOUT_IDENTIFIER_HEX_LENGTH 32U
#define SUPPORTED_CAPABILITIES \
    (SHINYGO60_CAPABILITY_STATE_TELEMETRY | SHINYGO60_CAPABILITY_PERSISTENT_LAYER | \
     SHINYGO60_CAPABILITY_MOMENTARY_LAYER | SHINYGO60_CAPABILITY_BATTERY_TELEMETRY)
#define EVENT_RETRY_DELAY K_MSEC(20)

BUILD_ASSERT(sizeof(CONFIG_SHINYGO60_LAYOUT_IDENTIFIER) - 1U ==
                 LAYOUT_IDENTIFIER_PREFIX_LENGTH + LAYOUT_IDENTIFIER_HEX_LENGTH,
             "The generated ShinyGo60 layout identifier has an invalid length");

static struct k_spinlock session_lock;
static K_MUTEX_DEFINE(command_mutex);
static uint32_t active_session_id;
static enum shinygo60_transport active_transport;
static uint8_t selected_capabilities;
static bool layer_snapshot_sent;
static bool battery_snapshot_sent;
static bool layer_event_pending;
static bool battery_event_pending;
static uint32_t layer_event_source_command_id;
static bool command_cache_populated;
static uint32_t latest_command_id;
static uint8_t latest_command_request[SHINYGO60_PACKET_SIZE];
static uint8_t latest_command_response[SHINYGO60_PACKET_SIZE];
static struct shinygo60_layer_state current_state = {
    .revision = 1U,
    .effective_layer = 0U,
    .persistent_layer = SHINYGO60_NO_LAYER,
};
static struct shinygo60_battery_state current_battery_state = {
    .revision = 1U,
};

static void layer_event_work_handler(struct k_work *work);
static void battery_event_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(layer_event_work, layer_event_work_handler);
static K_WORK_DELAYABLE_DEFINE(battery_event_work, battery_event_work_handler);

static bool decode_hex_digit(char character, uint8_t *value)
{
    if (character >= '0' && character <= '9') {
        *value = (uint8_t)(character - '0');
        return true;
    }

    if (character >= 'a' && character <= 'f') {
        *value = (uint8_t)(character - 'a' + 10);
        return true;
    }

    return false;
}

static bool read_layout_fingerprint(uint8_t fingerprint[SHINYGO60_LAYOUT_FINGERPRINT_SIZE])
{
    const char *identifier = CONFIG_SHINYGO60_LAYOUT_IDENTIFIER;
    if (memcmp(identifier, LAYOUT_IDENTIFIER_PREFIX, LAYOUT_IDENTIFIER_PREFIX_LENGTH) != 0) {
        return false;
    }

    bool fingerprint_nonzero = false;
    for (size_t index = 0U; index < LAYOUT_IDENTIFIER_HEX_LENGTH; index++) {
        uint8_t ignored;
        if (!decode_hex_digit(identifier[LAYOUT_IDENTIFIER_PREFIX_LENGTH + index], &ignored)) {
            return false;
        }
    }

    for (size_t index = 0U; index < SHINYGO60_LAYOUT_FINGERPRINT_SIZE; index++) {
        uint8_t high;
        uint8_t low;
        size_t offset = LAYOUT_IDENTIFIER_PREFIX_LENGTH + (index * 2U);
        if (!decode_hex_digit(identifier[offset], &high) ||
            !decode_hex_digit(identifier[offset + 1U], &low)) {
            return false;
        }

        fingerprint[index] = (uint8_t)((high << 4U) | low);
        fingerprint_nonzero |= fingerprint[index] != 0U;
    }

    return fingerprint_nonzero;
}

static uint32_t start_session(enum shinygo60_transport transport, uint8_t capabilities)
{
    uint32_t session_id = sys_rand32_get();
    if (session_id == 0U) {
        session_id = 1U;
    }

    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (session_id == active_session_id) {
        session_id++;
        if (session_id == 0U) {
            session_id = 1U;
        }
    }

    active_session_id = session_id;
    active_transport = transport;
    selected_capabilities = capabilities;
    layer_snapshot_sent = false;
    battery_snapshot_sent = false;
    layer_event_pending = false;
    battery_event_pending = false;
    layer_event_source_command_id = 0U;
    k_spin_unlock(&session_lock, key);

    command_cache_populated = false;
    latest_command_id = 0U;
    shinygo60_layer_control_begin_session(session_id);
    return session_id;
}

static uint8_t classify_session(
    enum shinygo60_transport transport, uint32_t session_id, uint8_t required_capability)
{
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    uint8_t result = 0U;
    if (active_session_id == 0U) {
        result = SHINYGO60_ERROR_NO_SESSION;
    } else if (active_session_id != session_id || active_transport != transport) {
        result = SHINYGO60_ERROR_WRONG_SESSION;
    } else if (required_capability != 0U &&
               (selected_capabilities & required_capability) == 0U) {
        result = SHINYGO60_ERROR_CAPABILITY_UNAVAILABLE;
    }

    k_spin_unlock(&session_lock, key);
    return result;
}

static struct shinygo60_message create_error(
    uint32_t session_id,
    uint32_t related_id,
    uint32_t state_revision,
    uint8_t code,
    uint8_t offending_type,
    uint16_t detail)
{
    struct shinygo60_message response = {
        .type = SHINYGO60_MESSAGE_ERROR,
        .payload.error = {
            .session_id = session_id,
            .related_id = related_id,
            .state_revision = state_revision,
            .code = code,
            .offending_message_type = offending_type,
            .detail = detail,
        },
    };
    return response;
}

static struct shinygo60_layer_state read_layer_state(void)
{
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    struct shinygo60_layer_state state = current_state;
    k_spin_unlock(&session_lock, key);
    return state;
}

uint32_t shinygo60_protocol_layer_revision(void)
{
    return read_layer_state().revision;
}

static struct shinygo60_message create_command_result(
    uint32_t session_id, uint32_t command_id, uint8_t status)
{
    struct shinygo60_message response = {
        .type = SHINYGO60_MESSAGE_COMMAND_RESULT,
        .payload.command_result = {
            .session_id = session_id,
            .command_id = command_id,
            .status = status,
            .state = read_layer_state(),
        },
    };
    return response;
}

static bool handle_unsupported_version(
    const uint8_t *request,
    const uint8_t layout[SHINYGO60_LAYOUT_FINGERPRINT_SIZE],
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    if (request[3] == SHINYGO60_MESSAGE_HELLO) {
        uint16_t client_nonce = (uint16_t)request[4] | ((uint16_t)request[5] << 8U);
        if (client_nonce == 0U) {
            return false;
        }

        struct shinygo60_message result = {
            .type = SHINYGO60_MESSAGE_HELLO_RESULT,
            .payload.hello_result = {
                .client_nonce = client_nonce,
                .status = SHINYGO60_HELLO_UNSUPPORTED_VERSION,
            },
        };
        memcpy(result.payload.hello_result.layout, layout, SHINYGO60_LAYOUT_FINGERPRINT_SIZE);
        return shinygo60_protocol_encode(&result, response);
    }

    struct shinygo60_message error = create_error(
        0U, 0U, 0U, SHINYGO60_ERROR_UNSUPPORTED_VERSION, request[3], request[2]);
    return shinygo60_protocol_encode(&error, response);
}

static bool handle_hello(
    enum shinygo60_transport transport,
    const struct shinygo60_message *request,
    const uint8_t layout[SHINYGO60_LAYOUT_FINGERPRINT_SIZE],
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    struct shinygo60_message result = {
        .type = SHINYGO60_MESSAGE_HELLO_RESULT,
        .payload.hello_result = {
            .client_nonce = request->payload.hello.client_nonce,
        },
    };
    memcpy(result.payload.hello_result.layout, layout, SHINYGO60_LAYOUT_FINGERPRINT_SIZE);

    if (memcmp(request->payload.hello.expected_layout, layout,
               SHINYGO60_LAYOUT_FINGERPRINT_SIZE) != 0) {
        result.payload.hello_result.status = SHINYGO60_HELLO_LAYOUT_MISMATCH;
    } else {
        result.payload.hello_result.status = SHINYGO60_HELLO_SUCCESS;
        result.payload.hello_result.selected_capabilities =
            request->payload.hello.requested_capabilities & SUPPORTED_CAPABILITIES;
        result.payload.hello_result.session_id = start_session(
            transport, result.payload.hello_result.selected_capabilities);
    }

    return shinygo60_protocol_encode(&result, response);
}

static bool handle_get_state(
    enum shinygo60_transport transport,
    const struct shinygo60_message *request,
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    uint32_t session_id = request->payload.get_state.session_id;
    uint32_t request_id = request->payload.get_state.request_id;
    struct shinygo60_layer_state state;

    k_spinlock_key_t key = k_spin_lock(&session_lock);
    uint8_t code = SHINYGO60_ERROR_CAPABILITY_UNAVAILABLE;
    if (active_session_id == 0U) {
        code = SHINYGO60_ERROR_NO_SESSION;
    } else if (active_session_id != session_id || active_transport != transport) {
        code = SHINYGO60_ERROR_WRONG_SESSION;
    } else if ((selected_capabilities & SHINYGO60_CAPABILITY_STATE_TELEMETRY) != 0U) {
        code = 0U;
        state = current_state;
        layer_snapshot_sent = true;
        layer_event_pending = false;
    }
    uint32_t state_revision = current_state.revision;
    k_spin_unlock(&session_lock, key);

    if (code != 0U) {
        struct shinygo60_message error = create_error(
            session_id, request_id, state_revision, code, SHINYGO60_MESSAGE_GET_STATE, 0U);
        return shinygo60_protocol_encode(&error, response);
    }

    struct shinygo60_message snapshot = {
        .type = SHINYGO60_MESSAGE_STATE_SNAPSHOT,
        .payload.state = {
            .session_id = session_id,
            .related_id = request_id,
            .state = state,
        },
    };
    return shinygo60_protocol_encode(&snapshot, response);
}

static bool handle_get_battery(
    enum shinygo60_transport transport,
    const struct shinygo60_message *request,
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    uint32_t session_id = request->payload.get_battery.session_id;
    uint32_t request_id = request->payload.get_battery.request_id;
    struct shinygo60_battery_state state;

    shinygo60_battery_telemetry_refresh();

    k_spinlock_key_t key = k_spin_lock(&session_lock);
    uint8_t code = SHINYGO60_ERROR_CAPABILITY_UNAVAILABLE;
    if (active_session_id == 0U) {
        code = SHINYGO60_ERROR_NO_SESSION;
    } else if (active_session_id != session_id || active_transport != transport) {
        code = SHINYGO60_ERROR_WRONG_SESSION;
    } else if ((selected_capabilities & SHINYGO60_CAPABILITY_BATTERY_TELEMETRY) != 0U) {
        code = 0U;
        state = current_battery_state;
        battery_snapshot_sent = true;
        battery_event_pending = false;
    }
    uint32_t state_revision = current_battery_state.revision;
    k_spin_unlock(&session_lock, key);

    if (code != 0U) {
        struct shinygo60_message error = create_error(
            session_id, request_id, state_revision, code, SHINYGO60_MESSAGE_GET_BATTERY, 0U);
        return shinygo60_protocol_encode(&error, response);
    }

    struct shinygo60_message snapshot = {
        .type = SHINYGO60_MESSAGE_BATTERY_SNAPSHOT,
        .payload.battery = {
            .session_id = session_id,
            .related_id = request_id,
            .state = state,
        },
    };
    return shinygo60_protocol_encode(&snapshot, response);
}

struct command_context {
    uint32_t session_id;
    uint32_t command_id;
    uint8_t required_capability;
};

static bool read_command_context(
    const struct shinygo60_message *request, struct command_context *context)
{
    switch (request->type) {
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
        context->session_id = request->payload.layer_command.session_id;
        context->command_id = request->payload.layer_command.command_id;
        context->required_capability = SHINYGO60_CAPABILITY_PERSISTENT_LAYER;
        return true;
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
        context->session_id = request->payload.layer_command.session_id;
        context->command_id = request->payload.layer_command.command_id;
        context->required_capability = SHINYGO60_CAPABILITY_MOMENTARY_LAYER;
        return true;
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        context->session_id = request->payload.momentary_command.session_id;
        context->command_id = request->payload.momentary_command.command_id;
        context->required_capability = SHINYGO60_CAPABILITY_MOMENTARY_LAYER;
        return true;
    default:
        return false;
    }
}

static struct shinygo60_message create_command_error(
    const struct shinygo60_message *request,
    const struct command_context *context,
    uint8_t code,
    uint16_t detail)
{
    return create_error(
        context->session_id,
        context->command_id,
        shinygo60_protocol_layer_revision(),
        code,
        (uint8_t)request->type,
        detail);
}

static struct shinygo60_message execute_control_command(
    enum shinygo60_transport transport,
    const struct shinygo60_message *request,
    const struct command_context *context)
{
    uint8_t session_error = classify_session(
        transport, context->session_id, context->required_capability);
    if (session_error != 0U) {
        return create_command_error(request, context, session_error, context->required_capability);
    }

    enum shinygo60_layer_control_result result;
    switch (request->type) {
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
        if (!shinygo60_layer_control_layer_is_valid(request->payload.layer_command.layer)) {
            return create_command_error(
                request, context, SHINYGO60_ERROR_INVALID_LAYER, request->payload.layer_command.layer);
        }
        result = shinygo60_layer_control_set_persistent(
            request->payload.layer_command.layer,
            request->payload.layer_command.expected_revision,
            context->command_id);
        break;
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
        if (!shinygo60_layer_control_layer_is_valid(request->payload.layer_command.layer)) {
            return create_command_error(
                request, context, SHINYGO60_ERROR_INVALID_LAYER, request->payload.layer_command.layer);
        }
        result = shinygo60_layer_control_press(
            context->session_id,
            context->command_id,
            request->payload.layer_command.layer,
            request->payload.layer_command.lease_units,
            request->payload.layer_command.expected_revision,
            context->command_id);
        break;
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
        result = shinygo60_layer_control_renew(
            context->session_id,
            request->payload.momentary_command.activation_id,
            request->payload.momentary_command.lease_units);
        break;
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        result = shinygo60_layer_control_release(
            context->session_id,
            request->payload.momentary_command.activation_id,
            context->command_id);
        break;
    default:
        return create_command_error(request, context, SHINYGO60_ERROR_UNSUPPORTED_MESSAGE, 0U);
    }

    switch (result) {
    case SHINYGO60_LAYER_CONTROL_APPLIED:
        return create_command_result(
            context->session_id, context->command_id, SHINYGO60_COMMAND_APPLIED);
    case SHINYGO60_LAYER_CONTROL_NO_CHANGE:
        return create_command_result(
            context->session_id, context->command_id, SHINYGO60_COMMAND_NO_CHANGE);
    case SHINYGO60_LAYER_CONTROL_ALREADY_RELEASED:
        return create_command_result(
            context->session_id, context->command_id, SHINYGO60_COMMAND_ALREADY_RELEASED);
    case SHINYGO60_LAYER_CONTROL_STALE_STATE:
        return create_command_error(request, context, SHINYGO60_ERROR_STALE_STATE, 0U);
    case SHINYGO60_LAYER_CONTROL_BUSY:
        return create_command_error(
            request,
            context,
            SHINYGO60_ERROR_BUSY,
            SHINYGO60_MOMENTARY_ACTIVATION_CAPACITY);
    case SHINYGO60_LAYER_CONTROL_WRONG_SESSION:
        return create_command_error(request, context, SHINYGO60_ERROR_WRONG_SESSION, 0U);
    case SHINYGO60_LAYER_CONTROL_INTERNAL:
    default:
        return create_command_error(request, context, SHINYGO60_ERROR_INTERNAL, 0U);
    }
}

static bool handle_control_command(
    enum shinygo60_transport transport,
    const struct shinygo60_message *request,
    const uint8_t request_packet[SHINYGO60_PACKET_SIZE],
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    struct command_context context;
    if (!read_command_context(request, &context)) {
        return false;
    }

    uint8_t session_error = classify_session(transport, context.session_id, 0U);
    if (session_error != 0U) {
        struct shinygo60_message error = create_command_error(
            request, &context, session_error, 0U);
        return shinygo60_protocol_encode(&error, response);
    }

    if (command_cache_populated && context.command_id <= latest_command_id) {
        if (context.command_id < latest_command_id) {
            struct shinygo60_message error = create_command_error(
                request, &context, SHINYGO60_ERROR_STALE_COMMAND, 0U);
            return shinygo60_protocol_encode(&error, response);
        }

        if (memcmp(request_packet, latest_command_request, SHINYGO60_PACKET_SIZE) != 0) {
            struct shinygo60_message error = create_command_error(
                request, &context, SHINYGO60_ERROR_DUPLICATE_CONFLICT, 0U);
            return shinygo60_protocol_encode(&error, response);
        }

        if (latest_command_response[3] != SHINYGO60_MESSAGE_COMMAND_RESULT) {
            memcpy(response, latest_command_response, SHINYGO60_PACKET_SIZE);
            return true;
        }

        struct shinygo60_message duplicate = create_command_result(
            context.session_id, context.command_id, SHINYGO60_COMMAND_DUPLICATE);
        return shinygo60_protocol_encode(&duplicate, response);
    }

    struct shinygo60_message result = execute_control_command(transport, request, &context);
    if (!shinygo60_protocol_encode(&result, response)) {
        return false;
    }

    command_cache_populated = true;
    latest_command_id = context.command_id;
    memcpy(latest_command_request, request_packet, SHINYGO60_PACKET_SIZE);
    memcpy(latest_command_response, response, SHINYGO60_PACKET_SIZE);
    return true;
}

bool shinygo60_protocol_handle(
    enum shinygo60_transport transport,
    const uint8_t *request,
    size_t request_length,
    uint8_t response[SHINYGO60_PACKET_SIZE])
{
    uint8_t layout[SHINYGO60_LAYOUT_FINGERPRINT_SIZE];
    if (!read_layout_fingerprint(layout)) {
        return false;
    }

    struct shinygo60_message decoded;
    enum shinygo60_decode_result decode_result =
        shinygo60_protocol_decode(request, request_length, &decoded);
    if (decode_result == SHINYGO60_DECODE_BAD_LENGTH ||
        decode_result == SHINYGO60_DECODE_BAD_MAGIC) {
        return false;
    }

    if (decode_result == SHINYGO60_DECODE_UNSUPPORTED_VERSION) {
        return handle_unsupported_version(request, layout, response);
    }

    if (decode_result != SHINYGO60_DECODE_OK) {
        uint8_t code = decode_result == SHINYGO60_DECODE_UNKNOWN_TYPE
                           ? SHINYGO60_ERROR_UNSUPPORTED_MESSAGE
                           : SHINYGO60_ERROR_MALFORMED_PACKET;
        struct shinygo60_message error =
            create_error(0U, 0U, 0U, code, request[3], (uint16_t)decode_result);
        return shinygo60_protocol_encode(&error, response);
    }

    k_mutex_lock(&command_mutex, K_FOREVER);
    bool handled;
    switch (decoded.type) {
    case SHINYGO60_MESSAGE_HELLO:
        handled = handle_hello(transport, &decoded, layout, response);
        break;
    case SHINYGO60_MESSAGE_GET_STATE:
        handled = handle_get_state(transport, &decoded, response);
        break;
    case SHINYGO60_MESSAGE_GET_BATTERY:
        handled = handle_get_battery(transport, &decoded, response);
        break;
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        handled = handle_control_command(transport, &decoded, request, response);
        break;
    default: {
        struct shinygo60_message error = create_error(
            0U,
            0U,
            shinygo60_protocol_layer_revision(),
            SHINYGO60_ERROR_UNSUPPORTED_MESSAGE,
            (uint8_t)decoded.type,
            0U);
        handled = shinygo60_protocol_encode(&error, response);
        break;
    }
    }
    k_mutex_unlock(&command_mutex);
    return handled;
}

void shinygo60_protocol_observe_layer_state(
    uint8_t effective_layer,
    uint8_t persistent_layer,
    uint8_t momentary_count,
    uint32_t source_command_id)
{
    if (effective_layer == SHINYGO60_NO_LAYER) {
        return;
    }

    uint8_t indicators = (persistent_layer == SHINYGO60_NO_LAYER
                              ? 0U
                              : SHINYGO60_LAYER_STATE_PERSISTENT_ACTIVE) |
                         (momentary_count == 0U
                              ? 0U
                              : SHINYGO60_LAYER_STATE_MOMENTARY_ACTIVE);
    bool send_event = false;
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (current_state.effective_layer != effective_layer ||
        current_state.persistent_layer != persistent_layer ||
        current_state.momentary_count != momentary_count ||
        current_state.indicators != indicators) {
        current_state.effective_layer = effective_layer;
        current_state.persistent_layer = persistent_layer;
        current_state.momentary_count = momentary_count;
        current_state.indicators = indicators;
        current_state.revision++;
        if (current_state.revision == 0U) {
            active_session_id = 0U;
            selected_capabilities = 0U;
            layer_snapshot_sent = false;
            battery_snapshot_sent = false;
            layer_event_pending = false;
            battery_event_pending = false;
            layer_event_source_command_id = 0U;
            current_state.revision = 1U;
        } else if (active_session_id != 0U && layer_snapshot_sent &&
                   (selected_capabilities & SHINYGO60_CAPABILITY_STATE_TELEMETRY) != 0U) {
            layer_event_pending = true;
            layer_event_source_command_id = source_command_id;
            send_event = true;
        }
    }
    k_spin_unlock(&session_lock, key);

    if (send_event) {
        (void)k_work_reschedule(&layer_event_work, K_NO_WAIT);
    }
}

void shinygo60_protocol_observe_battery(
    enum shinygo60_battery_half half, uint8_t level, bool available, bool stale)
{
    if ((half != SHINYGO60_BATTERY_HALF_LEFT && half != SHINYGO60_BATTERY_HALF_RIGHT) ||
        (available && level > 100U)) {
        return;
    }

    if (!available) {
        level = 0U;
        stale = false;
    }

    uint8_t available_indicator = half == SHINYGO60_BATTERY_HALF_LEFT
                                      ? SHINYGO60_BATTERY_LEFT_AVAILABLE
                                      : SHINYGO60_BATTERY_RIGHT_AVAILABLE;
    uint8_t stale_indicator = half == SHINYGO60_BATTERY_HALF_LEFT
                                  ? SHINYGO60_BATTERY_LEFT_STALE
                                  : SHINYGO60_BATTERY_RIGHT_STALE;
    uint8_t *current_level = half == SHINYGO60_BATTERY_HALF_LEFT
                                 ? &current_battery_state.left_level
                                 : &current_battery_state.right_level;
    bool send_event = false;
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    uint8_t new_indicators = current_battery_state.indicators &
                             (uint8_t)~(available_indicator | stale_indicator);
    if (available) {
        new_indicators |= available_indicator;
    }
    if (stale) {
        new_indicators |= stale_indicator;
    }

    if (*current_level != level || current_battery_state.indicators != new_indicators) {
        *current_level = level;
        current_battery_state.indicators = new_indicators;
        current_battery_state.revision++;
        if (current_battery_state.revision == 0U) {
            active_session_id = 0U;
            selected_capabilities = 0U;
            layer_snapshot_sent = false;
            battery_snapshot_sent = false;
            layer_event_pending = false;
            battery_event_pending = false;
            current_battery_state.revision = 1U;
        } else if (active_session_id != 0U && battery_snapshot_sent &&
                   (selected_capabilities & SHINYGO60_CAPABILITY_BATTERY_TELEMETRY) != 0U) {
            battery_event_pending = true;
            send_event = true;
        }
    }
    k_spin_unlock(&session_lock, key);

    if (send_event) {
        (void)k_work_reschedule(&battery_event_work, K_NO_WAIT);
    }
}

void shinygo60_protocol_transport_disconnected(enum shinygo60_transport transport)
{
    k_mutex_lock(&command_mutex, K_FOREVER);
    uint32_t ended_session_id = 0U;
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (active_session_id != 0U && active_transport == transport) {
        ended_session_id = active_session_id;
        active_session_id = 0U;
        selected_capabilities = 0U;
        layer_snapshot_sent = false;
        battery_snapshot_sent = false;
        layer_event_pending = false;
        battery_event_pending = false;
        layer_event_source_command_id = 0U;
    }
    k_spin_unlock(&session_lock, key);

    if (ended_session_id != 0U) {
        command_cache_populated = false;
        latest_command_id = 0U;
        shinygo60_layer_control_end_session(ended_session_id);
    }
    k_mutex_unlock(&command_mutex);
}

static bool send_event_packet(
    enum shinygo60_transport transport,
    const uint8_t packet[SHINYGO60_PACKET_SIZE])
{
    switch (transport) {
    case SHINYGO60_TRANSPORT_USB:
        return shinygo60_usb_send(packet);
    case SHINYGO60_TRANSPORT_BLUETOOTH:
        return shinygo60_ble_send(packet);
    default:
        return false;
    }
}

static void layer_event_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    struct shinygo60_message event = {
        .type = SHINYGO60_MESSAGE_LAYER_CHANGED,
    };
    enum shinygo60_transport transport;

    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (!layer_event_pending || active_session_id == 0U || !layer_snapshot_sent) {
        k_spin_unlock(&session_lock, key);
        return;
    }

    event.payload.state.session_id = active_session_id;
    event.payload.state.related_id = layer_event_source_command_id;
    event.payload.state.state = current_state;
    transport = active_transport;
    k_spin_unlock(&session_lock, key);

    uint8_t packet[SHINYGO60_PACKET_SIZE];
    bool sent = shinygo60_protocol_encode(&event, packet) && send_event_packet(transport, packet);

    key = k_spin_lock(&session_lock);
    if (sent && layer_event_pending && active_session_id == event.payload.state.session_id &&
        active_transport == transport && current_state.revision == event.payload.state.state.revision &&
        layer_event_source_command_id == event.payload.state.related_id) {
        layer_event_pending = false;
    }
    bool retry = layer_event_pending && active_session_id != 0U && layer_snapshot_sent;
    k_spin_unlock(&session_lock, key);

    if (retry) {
        (void)k_work_reschedule(&layer_event_work, sent ? K_NO_WAIT : EVENT_RETRY_DELAY);
    }
}

static void battery_event_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    struct shinygo60_message event = {
        .type = SHINYGO60_MESSAGE_BATTERY_CHANGED,
    };
    enum shinygo60_transport transport;

    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (!battery_event_pending || active_session_id == 0U || !battery_snapshot_sent) {
        k_spin_unlock(&session_lock, key);
        return;
    }

    event.payload.battery.session_id = active_session_id;
    event.payload.battery.state = current_battery_state;
    transport = active_transport;
    k_spin_unlock(&session_lock, key);

    uint8_t packet[SHINYGO60_PACKET_SIZE];
    bool sent = shinygo60_protocol_encode(&event, packet) && send_event_packet(transport, packet);

    key = k_spin_lock(&session_lock);
    if (sent && battery_event_pending && active_session_id == event.payload.battery.session_id &&
        active_transport == transport &&
        current_battery_state.revision == event.payload.battery.state.revision) {
        battery_event_pending = false;
    }
    bool retry = battery_event_pending && active_session_id != 0U && battery_snapshot_sent;
    k_spin_unlock(&session_lock, key);

    if (retry) {
        (void)k_work_reschedule(&battery_event_work, sent ? K_NO_WAIT : EVENT_RETRY_DELAY);
    }
}
