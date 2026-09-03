#include <string.h>

#include <shinygo60/protocol.h>

#define PAYLOAD_OFFSET 4U
#define KNOWN_CAPABILITIES 0x1fU
#define KNOWN_STATE_INDICATORS 0x03U
#define KNOWN_BATTERY_INDICATORS 0x0fU

static uint16_t read_u16_le(const uint8_t *bytes)
{
    return (uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8U);
}

static uint32_t read_u32_le(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8U) |
           ((uint32_t)bytes[2] << 16U) | ((uint32_t)bytes[3] << 24U);
}

static void write_u16_le(uint8_t *bytes, uint16_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8U);
}

static void write_u32_le(uint8_t *bytes, uint32_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8U);
    bytes[2] = (uint8_t)(value >> 16U);
    bytes[3] = (uint8_t)(value >> 24U);
}

static bool all_zero(const uint8_t *bytes, size_t length)
{
    for (size_t index = 0U; index < length; index++) {
        if (bytes[index] != 0U) {
            return false;
        }
    }

    return true;
}

static bool fingerprint_is_nonzero(const uint8_t *fingerprint)
{
    return !all_zero(fingerprint, SHINYGO60_LAYOUT_FINGERPRINT_SIZE);
}

static bool capabilities_are_valid(uint8_t capabilities)
{
    return (capabilities & ~KNOWN_CAPABILITIES) == 0U;
}

static bool lease_is_valid(uint8_t lease_units)
{
    return lease_units > 0U && lease_units <= SHINYGO60_MAXIMUM_LEASE_UNITS;
}

static bool layer_state_is_valid(const struct shinygo60_layer_state *state)
{
    bool persistent_active = (state->indicators & SHINYGO60_LAYER_STATE_PERSISTENT_ACTIVE) != 0U;
    bool momentary_active = (state->indicators & SHINYGO60_LAYER_STATE_MOMENTARY_ACTIVE) != 0U;

    return state->revision != 0U && state->effective_layer != SHINYGO60_NO_LAYER &&
           (state->indicators & ~KNOWN_STATE_INDICATORS) == 0U &&
           persistent_active == (state->persistent_layer != SHINYGO60_NO_LAYER) &&
           momentary_active == (state->momentary_count > 0U);
}

static bool battery_state_is_valid(const struct shinygo60_battery_state *state)
{
    bool left_available =
        (state->indicators & SHINYGO60_BATTERY_LEFT_AVAILABLE) != 0U;
    bool left_stale = (state->indicators & SHINYGO60_BATTERY_LEFT_STALE) != 0U;
    bool right_available =
        (state->indicators & SHINYGO60_BATTERY_RIGHT_AVAILABLE) != 0U;
    bool right_stale = (state->indicators & SHINYGO60_BATTERY_RIGHT_STALE) != 0U;

    return state->revision != 0U &&
           (state->indicators & ~KNOWN_BATTERY_INDICATORS) == 0U &&
           (!left_stale || left_available) && (!right_stale || right_available) &&
           (left_available ? state->left_level <= 100U : state->left_level == 0U) &&
           (right_available ? state->right_level <= 100U : state->right_level == 0U);
}

static bool hello_status_is_valid(uint8_t status)
{
    return status <= SHINYGO60_HELLO_UNSUPPORTED_VERSION;
}

static bool command_status_is_valid(uint8_t status)
{
    return status <= SHINYGO60_COMMAND_ALREADY_RELEASED;
}

static bool error_code_is_valid(uint8_t code)
{
    return code >= SHINYGO60_ERROR_MALFORMED_PACKET && code <= SHINYGO60_ERROR_INTERNAL;
}

static bool decode_hello(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.hello.client_nonce = read_u16_le(payload);
    message->payload.hello.requested_capabilities = payload[2];
    memcpy(message->payload.hello.expected_layout, &payload[4], SHINYGO60_LAYOUT_FINGERPRINT_SIZE);

    return message->payload.hello.client_nonce != 0U &&
           capabilities_are_valid(message->payload.hello.requested_capabilities) &&
           payload[3] == 0U && fingerprint_is_nonzero(message->payload.hello.expected_layout) &&
           all_zero(&payload[12], 4U);
}

static bool decode_hello_result(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.hello_result.client_nonce = read_u16_le(payload);
    message->payload.hello_result.status = payload[2];
    message->payload.hello_result.selected_capabilities = payload[3];
    message->payload.hello_result.session_id = read_u32_le(&payload[4]);
    memcpy(message->payload.hello_result.layout, &payload[8], SHINYGO60_LAYOUT_FINGERPRINT_SIZE);

    bool success = message->payload.hello_result.status == SHINYGO60_HELLO_SUCCESS;
    return message->payload.hello_result.client_nonce != 0U &&
           hello_status_is_valid(message->payload.hello_result.status) &&
           capabilities_are_valid(message->payload.hello_result.selected_capabilities) &&
           fingerprint_is_nonzero(message->payload.hello_result.layout) &&
           (success ? message->payload.hello_result.session_id != 0U
                    : message->payload.hello_result.session_id == 0U &&
                          message->payload.hello_result.selected_capabilities == 0U);
}

static bool decode_get_state(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.get_state.session_id = read_u32_le(payload);
    message->payload.get_state.request_id = read_u32_le(&payload[4]);
    return message->payload.get_state.session_id != 0U &&
           message->payload.get_state.request_id != 0U && all_zero(&payload[8], 8U);
}

static bool decode_state(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.state.session_id = read_u32_le(payload);
    message->payload.state.state.revision = read_u32_le(&payload[4]);
    message->payload.state.related_id = read_u32_le(&payload[8]);
    message->payload.state.state.effective_layer = payload[12];
    message->payload.state.state.persistent_layer = payload[13];
    message->payload.state.state.momentary_count = payload[14];
    message->payload.state.state.indicators = payload[15];
    return message->payload.state.session_id != 0U &&
           layer_state_is_valid(&message->payload.state.state);
}

static bool decode_get_battery(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.get_battery.session_id = read_u32_le(payload);
    message->payload.get_battery.request_id = read_u32_le(&payload[4]);
    return message->payload.get_battery.session_id != 0U &&
           message->payload.get_battery.request_id != 0U && all_zero(&payload[8], 8U);
}

static bool decode_battery(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.battery.session_id = read_u32_le(payload);
    message->payload.battery.state.revision = read_u32_le(&payload[4]);
    message->payload.battery.related_id = read_u32_le(&payload[8]);
    message->payload.battery.state.left_level = payload[12];
    message->payload.battery.state.right_level = payload[13];
    message->payload.battery.state.indicators = payload[14];
    bool related_id_is_valid = message->type == SHINYGO60_MESSAGE_BATTERY_CHANGED
                                   ? message->payload.battery.related_id == 0U
                                   : message->payload.battery.related_id != 0U;
    return message->payload.battery.session_id != 0U && related_id_is_valid &&
           payload[15] == 0U && battery_state_is_valid(&message->payload.battery.state);
}

static bool decode_layer_command(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.layer_command.session_id = read_u32_le(payload);
    message->payload.layer_command.command_id = read_u32_le(&payload[4]);
    message->payload.layer_command.expected_revision = read_u32_le(&payload[8]);
    message->payload.layer_command.layer = payload[12];
    message->payload.layer_command.lease_units = payload[13];

    bool lease_valid = message->type == SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER
                           ? lease_is_valid(message->payload.layer_command.lease_units)
                           : message->payload.layer_command.lease_units == 0U;
    return message->payload.layer_command.session_id != 0U &&
           message->payload.layer_command.command_id != 0U &&
           message->payload.layer_command.expected_revision != 0U &&
           message->payload.layer_command.layer != SHINYGO60_NO_LAYER && lease_valid &&
           all_zero(&payload[14], 2U);
}

static bool decode_momentary_command(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.momentary_command.session_id = read_u32_le(payload);
    message->payload.momentary_command.command_id = read_u32_le(&payload[4]);
    message->payload.momentary_command.activation_id = read_u32_le(&payload[8]);
    message->payload.momentary_command.lease_units = payload[12];

    bool lease_valid = message->type == SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER
                           ? lease_is_valid(message->payload.momentary_command.lease_units)
                           : message->payload.momentary_command.lease_units == 0U;
    return message->payload.momentary_command.session_id != 0U &&
           message->payload.momentary_command.command_id != 0U &&
           message->payload.momentary_command.activation_id != 0U && lease_valid &&
           all_zero(&payload[13], 3U);
}

static bool decode_bluetooth_mode_command(
    const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.bluetooth_mode_command.session_id = read_u32_le(payload);
    message->payload.bluetooth_mode_command.command_id = read_u32_le(&payload[4]);
    message->payload.bluetooth_mode_command.mode = payload[8];
    return message->payload.bluetooth_mode_command.session_id != 0U &&
           message->payload.bluetooth_mode_command.command_id != 0U &&
           message->payload.bluetooth_mode_command.mode <= SHINYGO60_BLUETOOTH_INTERACTIVE &&
           all_zero(&payload[9], 7U);
}

static bool decode_command_result(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.command_result.session_id = read_u32_le(payload);
    message->payload.command_result.command_id = read_u32_le(&payload[4]);
    message->payload.command_result.state.revision = read_u32_le(&payload[8]);
    message->payload.command_result.status = payload[12];
    message->payload.command_result.state.effective_layer = payload[13];
    message->payload.command_result.state.persistent_layer = payload[14];
    message->payload.command_result.state.momentary_count = payload[15];
    message->payload.command_result.state.indicators =
        (payload[14] == SHINYGO60_NO_LAYER ? 0U : SHINYGO60_LAYER_STATE_PERSISTENT_ACTIVE) |
        (payload[15] == 0U ? 0U : SHINYGO60_LAYER_STATE_MOMENTARY_ACTIVE);

    return message->payload.command_result.session_id != 0U &&
           message->payload.command_result.command_id != 0U &&
           command_status_is_valid(message->payload.command_result.status) &&
           layer_state_is_valid(&message->payload.command_result.state);
}

static bool decode_error(const uint8_t *payload, struct shinygo60_message *message)
{
    message->payload.error.session_id = read_u32_le(payload);
    message->payload.error.related_id = read_u32_le(&payload[4]);
    message->payload.error.state_revision = read_u32_le(&payload[8]);
    message->payload.error.code = payload[12];
    message->payload.error.offending_message_type = payload[13];
    message->payload.error.detail = read_u16_le(&payload[14]);
    return error_code_is_valid(message->payload.error.code);
}

enum shinygo60_decode_result shinygo60_protocol_decode(
    const uint8_t *packet, size_t packet_length, struct shinygo60_message *message)
{
    if (packet_length != SHINYGO60_PACKET_SIZE) {
        return SHINYGO60_DECODE_BAD_LENGTH;
    }

    if (packet[0] != SHINYGO60_PACKET_MAGIC_0 || packet[1] != SHINYGO60_PACKET_MAGIC_1) {
        return SHINYGO60_DECODE_BAD_MAGIC;
    }

    if (packet[2] != SHINYGO60_PROTOCOL_VERSION) {
        return SHINYGO60_DECODE_UNSUPPORTED_VERSION;
    }

    memset(message, 0, sizeof(*message));
    message->type = (enum shinygo60_message_type)packet[3];
    const uint8_t *payload = &packet[PAYLOAD_OFFSET];
    bool valid;

    switch (message->type) {
    case SHINYGO60_MESSAGE_HELLO:
        valid = decode_hello(payload, message);
        break;
    case SHINYGO60_MESSAGE_HELLO_RESULT:
        valid = decode_hello_result(payload, message);
        break;
    case SHINYGO60_MESSAGE_GET_STATE:
        valid = decode_get_state(payload, message);
        break;
    case SHINYGO60_MESSAGE_STATE_SNAPSHOT:
    case SHINYGO60_MESSAGE_LAYER_CHANGED:
        valid = decode_state(payload, message);
        break;
    case SHINYGO60_MESSAGE_GET_BATTERY:
        valid = decode_get_battery(payload, message);
        break;
    case SHINYGO60_MESSAGE_BATTERY_SNAPSHOT:
    case SHINYGO60_MESSAGE_BATTERY_CHANGED:
        valid = decode_battery(payload, message);
        break;
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
        valid = decode_layer_command(payload, message);
        break;
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        valid = decode_momentary_command(payload, message);
        break;
    case SHINYGO60_MESSAGE_SET_BLUETOOTH_CONNECTION_MODE:
        valid = decode_bluetooth_mode_command(payload, message);
        break;
    case SHINYGO60_MESSAGE_COMMAND_RESULT:
        valid = decode_command_result(payload, message);
        break;
    case SHINYGO60_MESSAGE_ERROR:
        valid = decode_error(payload, message);
        break;
    default:
        return SHINYGO60_DECODE_UNKNOWN_TYPE;
    }

    return valid ? SHINYGO60_DECODE_OK : SHINYGO60_DECODE_INVALID_PAYLOAD;
}

static bool encode_hello(const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.hello.client_nonce == 0U ||
        !capabilities_are_valid(message->payload.hello.requested_capabilities) ||
        !fingerprint_is_nonzero(message->payload.hello.expected_layout)) {
        return false;
    }

    write_u16_le(payload, message->payload.hello.client_nonce);
    payload[2] = message->payload.hello.requested_capabilities;
    memcpy(&payload[4], message->payload.hello.expected_layout, SHINYGO60_LAYOUT_FINGERPRINT_SIZE);
    return true;
}

static bool encode_hello_result(const struct shinygo60_message *message, uint8_t *payload)
{
    bool success = message->payload.hello_result.status == SHINYGO60_HELLO_SUCCESS;
    if (message->payload.hello_result.client_nonce == 0U ||
        !hello_status_is_valid(message->payload.hello_result.status) ||
        !capabilities_are_valid(message->payload.hello_result.selected_capabilities) ||
        !fingerprint_is_nonzero(message->payload.hello_result.layout) ||
        !(success ? message->payload.hello_result.session_id != 0U
                  : message->payload.hello_result.session_id == 0U &&
                        message->payload.hello_result.selected_capabilities == 0U)) {
        return false;
    }

    write_u16_le(payload, message->payload.hello_result.client_nonce);
    payload[2] = message->payload.hello_result.status;
    payload[3] = message->payload.hello_result.selected_capabilities;
    write_u32_le(&payload[4], message->payload.hello_result.session_id);
    memcpy(&payload[8], message->payload.hello_result.layout, SHINYGO60_LAYOUT_FINGERPRINT_SIZE);
    return true;
}

static bool encode_get_state(const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.get_state.session_id == 0U || message->payload.get_state.request_id == 0U) {
        return false;
    }

    write_u32_le(payload, message->payload.get_state.session_id);
    write_u32_le(&payload[4], message->payload.get_state.request_id);
    return true;
}

static bool encode_state(const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.state.session_id == 0U ||
        !layer_state_is_valid(&message->payload.state.state)) {
        return false;
    }

    write_u32_le(payload, message->payload.state.session_id);
    write_u32_le(&payload[4], message->payload.state.state.revision);
    write_u32_le(&payload[8], message->payload.state.related_id);
    payload[12] = message->payload.state.state.effective_layer;
    payload[13] = message->payload.state.state.persistent_layer;
    payload[14] = message->payload.state.state.momentary_count;
    payload[15] = message->payload.state.state.indicators;
    return true;
}

static bool encode_get_battery(const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.get_battery.session_id == 0U ||
        message->payload.get_battery.request_id == 0U) {
        return false;
    }

    write_u32_le(payload, message->payload.get_battery.session_id);
    write_u32_le(&payload[4], message->payload.get_battery.request_id);
    return true;
}

static bool encode_battery(const struct shinygo60_message *message, uint8_t *payload)
{
    bool related_id_is_valid = message->type == SHINYGO60_MESSAGE_BATTERY_CHANGED
                                   ? message->payload.battery.related_id == 0U
                                   : message->payload.battery.related_id != 0U;
    if (message->payload.battery.session_id == 0U || !related_id_is_valid ||
        !battery_state_is_valid(&message->payload.battery.state)) {
        return false;
    }

    write_u32_le(payload, message->payload.battery.session_id);
    write_u32_le(&payload[4], message->payload.battery.state.revision);
    write_u32_le(&payload[8], message->payload.battery.related_id);
    payload[12] = message->payload.battery.state.left_level;
    payload[13] = message->payload.battery.state.right_level;
    payload[14] = message->payload.battery.state.indicators;
    return true;
}

static bool encode_layer_command(const struct shinygo60_message *message, uint8_t *payload)
{
    bool lease_valid = message->type == SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER
                           ? lease_is_valid(message->payload.layer_command.lease_units)
                           : message->payload.layer_command.lease_units == 0U;
    if (message->payload.layer_command.session_id == 0U ||
        message->payload.layer_command.command_id == 0U ||
        message->payload.layer_command.expected_revision == 0U ||
        message->payload.layer_command.layer == SHINYGO60_NO_LAYER || !lease_valid) {
        return false;
    }

    write_u32_le(payload, message->payload.layer_command.session_id);
    write_u32_le(&payload[4], message->payload.layer_command.command_id);
    write_u32_le(&payload[8], message->payload.layer_command.expected_revision);
    payload[12] = message->payload.layer_command.layer;
    payload[13] = message->payload.layer_command.lease_units;
    return true;
}

static bool encode_momentary_command(const struct shinygo60_message *message, uint8_t *payload)
{
    bool lease_valid = message->type == SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER
                           ? lease_is_valid(message->payload.momentary_command.lease_units)
                           : message->payload.momentary_command.lease_units == 0U;
    if (message->payload.momentary_command.session_id == 0U ||
        message->payload.momentary_command.command_id == 0U ||
        message->payload.momentary_command.activation_id == 0U || !lease_valid) {
        return false;
    }

    write_u32_le(payload, message->payload.momentary_command.session_id);
    write_u32_le(&payload[4], message->payload.momentary_command.command_id);
    write_u32_le(&payload[8], message->payload.momentary_command.activation_id);
    payload[12] = message->payload.momentary_command.lease_units;
    return true;
}

static bool encode_bluetooth_mode_command(
    const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.bluetooth_mode_command.session_id == 0U ||
        message->payload.bluetooth_mode_command.command_id == 0U ||
        message->payload.bluetooth_mode_command.mode > SHINYGO60_BLUETOOTH_INTERACTIVE) {
        return false;
    }

    write_u32_le(payload, message->payload.bluetooth_mode_command.session_id);
    write_u32_le(&payload[4], message->payload.bluetooth_mode_command.command_id);
    payload[8] = message->payload.bluetooth_mode_command.mode;
    return true;
}

static bool encode_command_result(const struct shinygo60_message *message, uint8_t *payload)
{
    if (message->payload.command_result.session_id == 0U ||
        message->payload.command_result.command_id == 0U ||
        !command_status_is_valid(message->payload.command_result.status) ||
        !layer_state_is_valid(&message->payload.command_result.state)) {
        return false;
    }

    write_u32_le(payload, message->payload.command_result.session_id);
    write_u32_le(&payload[4], message->payload.command_result.command_id);
    write_u32_le(&payload[8], message->payload.command_result.state.revision);
    payload[12] = message->payload.command_result.status;
    payload[13] = message->payload.command_result.state.effective_layer;
    payload[14] = message->payload.command_result.state.persistent_layer;
    payload[15] = message->payload.command_result.state.momentary_count;
    return true;
}

static bool encode_error(const struct shinygo60_message *message, uint8_t *payload)
{
    if (!error_code_is_valid(message->payload.error.code)) {
        return false;
    }

    write_u32_le(payload, message->payload.error.session_id);
    write_u32_le(&payload[4], message->payload.error.related_id);
    write_u32_le(&payload[8], message->payload.error.state_revision);
    payload[12] = message->payload.error.code;
    payload[13] = message->payload.error.offending_message_type;
    write_u16_le(&payload[14], message->payload.error.detail);
    return true;
}

bool shinygo60_protocol_encode(
    const struct shinygo60_message *message, uint8_t packet[SHINYGO60_PACKET_SIZE])
{
    memset(packet, 0, SHINYGO60_PACKET_SIZE);
    packet[0] = SHINYGO60_PACKET_MAGIC_0;
    packet[1] = SHINYGO60_PACKET_MAGIC_1;
    packet[2] = SHINYGO60_PROTOCOL_VERSION;
    packet[3] = (uint8_t)message->type;
    uint8_t *payload = &packet[PAYLOAD_OFFSET];

    switch (message->type) {
    case SHINYGO60_MESSAGE_HELLO:
        return encode_hello(message, payload);
    case SHINYGO60_MESSAGE_HELLO_RESULT:
        return encode_hello_result(message, payload);
    case SHINYGO60_MESSAGE_GET_STATE:
        return encode_get_state(message, payload);
    case SHINYGO60_MESSAGE_STATE_SNAPSHOT:
    case SHINYGO60_MESSAGE_LAYER_CHANGED:
        return encode_state(message, payload);
    case SHINYGO60_MESSAGE_GET_BATTERY:
        return encode_get_battery(message, payload);
    case SHINYGO60_MESSAGE_BATTERY_SNAPSHOT:
    case SHINYGO60_MESSAGE_BATTERY_CHANGED:
        return encode_battery(message, payload);
    case SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER:
    case SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER:
        return encode_layer_command(message, payload);
    case SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER:
    case SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER:
        return encode_momentary_command(message, payload);
    case SHINYGO60_MESSAGE_SET_BLUETOOTH_CONNECTION_MODE:
        return encode_bluetooth_mode_command(message, payload);
    case SHINYGO60_MESSAGE_COMMAND_RESULT:
        return encode_command_result(message, payload);
    case SHINYGO60_MESSAGE_ERROR:
        return encode_error(message, payload);
    default:
        return false;
    }
}
