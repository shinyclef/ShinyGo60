#ifndef SHINYGO60_PROTOCOL_H_
#define SHINYGO60_PROTOCOL_H_

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define SHINYGO60_PACKET_SIZE 20U
#define SHINYGO60_PACKET_MAGIC_0 0x53U
#define SHINYGO60_PACKET_MAGIC_1 0x47U
#define SHINYGO60_PROTOCOL_MAJOR 1U
#define SHINYGO60_PROTOCOL_MINOR 1U
#define SHINYGO60_PROTOCOL_VERSION ((SHINYGO60_PROTOCOL_MAJOR << 4U) | SHINYGO60_PROTOCOL_MINOR)
#define SHINYGO60_NO_LAYER UINT8_MAX
#define SHINYGO60_MAXIMUM_LEASE_UNITS 50U
#define SHINYGO60_LEASE_UNIT_MILLISECONDS 100U
#define SHINYGO60_LAYOUT_FINGERPRINT_SIZE 8U

enum shinygo60_message_type {
    SHINYGO60_MESSAGE_HELLO = 0x01,
    SHINYGO60_MESSAGE_HELLO_RESULT = 0x02,
    SHINYGO60_MESSAGE_GET_STATE = 0x03,
    SHINYGO60_MESSAGE_STATE_SNAPSHOT = 0x04,
    SHINYGO60_MESSAGE_LAYER_CHANGED = 0x05,
    SHINYGO60_MESSAGE_GET_BATTERY = 0x06,
    SHINYGO60_MESSAGE_BATTERY_SNAPSHOT = 0x07,
    SHINYGO60_MESSAGE_BATTERY_CHANGED = 0x08,
    SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER = 0x10,
    SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER = 0x11,
    SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER = 0x12,
    SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER = 0x13,
    SHINYGO60_MESSAGE_COMMAND_RESULT = 0x20,
    SHINYGO60_MESSAGE_ERROR = 0x7f,
};

enum shinygo60_capability {
    SHINYGO60_CAPABILITY_STATE_TELEMETRY = 1U << 0,
    SHINYGO60_CAPABILITY_PERSISTENT_LAYER = 1U << 1,
    SHINYGO60_CAPABILITY_MOMENTARY_LAYER = 1U << 2,
    SHINYGO60_CAPABILITY_BATTERY_TELEMETRY = 1U << 3,
};

enum shinygo60_hello_status {
    SHINYGO60_HELLO_SUCCESS = 0,
    SHINYGO60_HELLO_LAYOUT_MISMATCH = 1,
    SHINYGO60_HELLO_UNSUPPORTED_VERSION = 2,
};

enum shinygo60_layer_state_indicator {
    SHINYGO60_LAYER_STATE_PERSISTENT_ACTIVE = 1U << 0,
    SHINYGO60_LAYER_STATE_MOMENTARY_ACTIVE = 1U << 1,
};

enum shinygo60_battery_state_indicator {
    SHINYGO60_BATTERY_LEFT_AVAILABLE = 1U << 0,
    SHINYGO60_BATTERY_LEFT_STALE = 1U << 1,
    SHINYGO60_BATTERY_RIGHT_AVAILABLE = 1U << 2,
    SHINYGO60_BATTERY_RIGHT_STALE = 1U << 3,
};

enum shinygo60_battery_half {
    SHINYGO60_BATTERY_HALF_LEFT,
    SHINYGO60_BATTERY_HALF_RIGHT,
};

enum shinygo60_command_status {
    SHINYGO60_COMMAND_APPLIED = 0,
    SHINYGO60_COMMAND_NO_CHANGE = 1,
    SHINYGO60_COMMAND_DUPLICATE = 2,
    SHINYGO60_COMMAND_ALREADY_RELEASED = 3,
};

enum shinygo60_error_code {
    SHINYGO60_ERROR_MALFORMED_PACKET = 1,
    SHINYGO60_ERROR_UNSUPPORTED_VERSION = 2,
    SHINYGO60_ERROR_UNSUPPORTED_MESSAGE = 3,
    SHINYGO60_ERROR_NO_SESSION = 4,
    SHINYGO60_ERROR_WRONG_SESSION = 5,
    SHINYGO60_ERROR_LAYOUT_MISMATCH = 6,
    SHINYGO60_ERROR_CAPABILITY_UNAVAILABLE = 7,
    SHINYGO60_ERROR_INVALID_LAYER = 8,
    SHINYGO60_ERROR_STALE_STATE = 9,
    SHINYGO60_ERROR_STALE_COMMAND = 10,
    SHINYGO60_ERROR_DUPLICATE_CONFLICT = 11,
    SHINYGO60_ERROR_LEASE_OUT_OF_RANGE = 12,
    SHINYGO60_ERROR_BUSY = 13,
    SHINYGO60_ERROR_INTERNAL = 14,
};

enum shinygo60_decode_result {
    SHINYGO60_DECODE_OK,
    SHINYGO60_DECODE_BAD_LENGTH,
    SHINYGO60_DECODE_BAD_MAGIC,
    SHINYGO60_DECODE_UNSUPPORTED_VERSION,
    SHINYGO60_DECODE_UNKNOWN_TYPE,
    SHINYGO60_DECODE_INVALID_PAYLOAD,
};

enum shinygo60_transport {
    SHINYGO60_TRANSPORT_USB = 1,
    SHINYGO60_TRANSPORT_BLUETOOTH = 2,
};

struct shinygo60_layer_state {
    uint32_t revision;
    uint8_t effective_layer;
    uint8_t persistent_layer;
    uint8_t momentary_count;
    uint8_t indicators;
};

struct shinygo60_battery_state {
    uint32_t revision;
    uint8_t left_level;
    uint8_t right_level;
    uint8_t indicators;
};

struct shinygo60_message {
    enum shinygo60_message_type type;
    union {
        struct {
            uint16_t client_nonce;
            uint8_t requested_capabilities;
            uint8_t expected_layout[SHINYGO60_LAYOUT_FINGERPRINT_SIZE];
        } hello;
        struct {
            uint16_t client_nonce;
            uint8_t status;
            uint8_t selected_capabilities;
            uint32_t session_id;
            uint8_t layout[SHINYGO60_LAYOUT_FINGERPRINT_SIZE];
        } hello_result;
        struct {
            uint32_t session_id;
            uint32_t request_id;
        } get_state;
        struct {
            uint32_t session_id;
            uint32_t request_id;
        } get_battery;
        struct {
            uint32_t session_id;
            uint32_t related_id;
            struct shinygo60_layer_state state;
        } state;
        struct {
            uint32_t session_id;
            uint32_t related_id;
            struct shinygo60_battery_state state;
        } battery;
        struct {
            uint32_t session_id;
            uint32_t command_id;
            uint32_t expected_revision;
            uint8_t layer;
            uint8_t lease_units;
        } layer_command;
        struct {
            uint32_t session_id;
            uint32_t command_id;
            uint32_t activation_id;
            uint8_t lease_units;
        } momentary_command;
        struct {
            uint32_t session_id;
            uint32_t command_id;
            uint8_t status;
            struct shinygo60_layer_state state;
        } command_result;
        struct {
            uint32_t session_id;
            uint32_t related_id;
            uint32_t state_revision;
            uint8_t code;
            uint8_t offending_message_type;
            uint16_t detail;
        } error;
    } payload;
};

enum shinygo60_decode_result shinygo60_protocol_decode(
    const uint8_t *packet, size_t packet_length, struct shinygo60_message *message);

bool shinygo60_protocol_encode(
    const struct shinygo60_message *message, uint8_t packet[SHINYGO60_PACKET_SIZE]);

bool shinygo60_protocol_handle(
    enum shinygo60_transport transport,
    const uint8_t *request,
    size_t request_length,
    uint8_t response[SHINYGO60_PACKET_SIZE]);

void shinygo60_protocol_observe_layer_state(
    uint8_t effective_layer,
    uint8_t persistent_layer,
    uint8_t momentary_count,
    uint32_t source_command_id);

uint32_t shinygo60_protocol_layer_revision(void);

void shinygo60_protocol_observe_battery(
    enum shinygo60_battery_half half, uint8_t level, bool available, bool stale);

void shinygo60_battery_telemetry_refresh(void);

void shinygo60_protocol_transport_disconnected(enum shinygo60_transport transport);

bool shinygo60_usb_send(const uint8_t packet[SHINYGO60_PACKET_SIZE]);

bool shinygo60_ble_send(const uint8_t packet[SHINYGO60_PACKET_SIZE]);

#endif /* SHINYGO60_PROTOCOL_H_ */
