#include <string.h>

#include <zephyr/kernel.h>
#include <zephyr/random/random.h>
#include <zephyr/sys/util.h>

#include <shinygo60/protocol.h>

#define LAYOUT_IDENTIFIER_PREFIX "sg60-v1-"
#define LAYOUT_IDENTIFIER_PREFIX_LENGTH 8U
#define LAYOUT_IDENTIFIER_HEX_LENGTH 32U
#define SUPPORTED_CAPABILITIES SHINYGO60_CAPABILITY_STATE_TELEMETRY
#define EVENT_RETRY_DELAY K_MSEC(20)

BUILD_ASSERT(sizeof(CONFIG_SHINYGO60_LAYOUT_IDENTIFIER) - 1U ==
                 LAYOUT_IDENTIFIER_PREFIX_LENGTH + LAYOUT_IDENTIFIER_HEX_LENGTH,
             "The generated ShinyGo60 layout identifier has an invalid length");

static struct k_spinlock session_lock;
static uint32_t active_session_id;
static enum shinygo60_transport active_transport;
static uint8_t selected_capabilities;
static bool snapshot_sent;
static bool layer_event_pending;
static struct shinygo60_layer_state current_state = {
    .revision = 1U,
    .effective_layer = 0U,
    .persistent_layer = SHINYGO60_NO_LAYER,
};

static void layer_event_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(layer_event_work, layer_event_work_handler);

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
    snapshot_sent = false;
    layer_event_pending = false;
    k_spin_unlock(&session_lock, key);
    return session_id;
}

static uint8_t classify_session(enum shinygo60_transport transport, uint32_t session_id)
{
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    uint8_t result = SHINYGO60_ERROR_CAPABILITY_UNAVAILABLE;
    if (active_session_id == 0U) {
        result = SHINYGO60_ERROR_NO_SESSION;
    } else if (active_session_id != session_id || active_transport != transport) {
        result = SHINYGO60_ERROR_WRONG_SESSION;
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
        snapshot_sent = true;
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

static bool request_context(
    const struct shinygo60_message *request,
    uint32_t *session_id,
    uint32_t *related_id,
    uint32_t *state_revision)
{
    switch (request->type) {
    case SHINYGO60_MESSAGE_GET_STATE:
        *session_id = request->payload.get_state.session_id;
        *related_id = request->payload.get_state.request_id;
        *state_revision = 0U;
        return true;
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
        *session_id = request->payload.layer_command.session_id;
        *related_id = request->payload.layer_command.command_id;
        *state_revision = request->payload.layer_command.expected_revision;
        return true;
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        *session_id = request->payload.momentary_command.session_id;
        *related_id = request->payload.momentary_command.command_id;
        *state_revision = 0U;
        return true;
    default:
        return false;
    }
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

    if (decoded.type == SHINYGO60_MESSAGE_HELLO) {
        return handle_hello(transport, &decoded, layout, response);
    }

    if (decoded.type == SHINYGO60_MESSAGE_GET_STATE) {
        return handle_get_state(transport, &decoded, response);
    }

    uint32_t session_id;
    uint32_t related_id;
    uint32_t state_revision;
    if (!request_context(&decoded, &session_id, &related_id, &state_revision)) {
        struct shinygo60_message error = create_error(
            0U, 0U, 0U, SHINYGO60_ERROR_UNSUPPORTED_MESSAGE, (uint8_t)decoded.type, 0U);
        return shinygo60_protocol_encode(&error, response);
    }

    uint8_t code = classify_session(transport, session_id);
    struct shinygo60_message error = create_error(
        session_id, related_id, state_revision, code, (uint8_t)decoded.type, 0U);
    return shinygo60_protocol_encode(&error, response);
}

void shinygo60_protocol_observe_effective_layer(uint8_t effective_layer)
{
    if (effective_layer == SHINYGO60_NO_LAYER) {
        return;
    }

    bool send_event = false;
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (current_state.effective_layer != effective_layer) {
        current_state.effective_layer = effective_layer;
        current_state.revision++;
        if (current_state.revision == 0U) {
            active_session_id = 0U;
            selected_capabilities = 0U;
            snapshot_sent = false;
            layer_event_pending = false;
            current_state.revision = 1U;
        } else if (active_session_id != 0U && snapshot_sent &&
                   (selected_capabilities & SHINYGO60_CAPABILITY_STATE_TELEMETRY) != 0U) {
            layer_event_pending = true;
            send_event = true;
        }
    }
    k_spin_unlock(&session_lock, key);

    if (send_event) {
        (void)k_work_reschedule(&layer_event_work, K_NO_WAIT);
    }
}

void shinygo60_protocol_transport_disconnected(enum shinygo60_transport transport)
{
    k_spinlock_key_t key = k_spin_lock(&session_lock);
    if (active_session_id != 0U && active_transport == transport) {
        active_session_id = 0U;
        selected_capabilities = 0U;
        snapshot_sent = false;
        layer_event_pending = false;
    }
    k_spin_unlock(&session_lock, key);
}

static bool send_layer_event(
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
    if (!layer_event_pending || active_session_id == 0U || !snapshot_sent) {
        k_spin_unlock(&session_lock, key);
        return;
    }

    event.payload.state.session_id = active_session_id;
    event.payload.state.state = current_state;
    transport = active_transport;
    k_spin_unlock(&session_lock, key);

    uint8_t packet[SHINYGO60_PACKET_SIZE];
    bool sent = shinygo60_protocol_encode(&event, packet) && send_layer_event(transport, packet);

    key = k_spin_lock(&session_lock);
    if (sent && layer_event_pending && active_session_id == event.payload.state.session_id &&
        active_transport == transport && current_state.revision == event.payload.state.state.revision) {
        layer_event_pending = false;
    }
    bool retry = layer_event_pending && active_session_id != 0U && snapshot_sent;
    k_spin_unlock(&session_lock, key);

    if (retry) {
        (void)k_work_reschedule(&layer_event_work, sent ? K_NO_WAIT : EVENT_RETRY_DELAY);
    }
}
