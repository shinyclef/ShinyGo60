#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <shinygo60/protocol.h>

static const uint8_t hello_request[] = {
#include "vectors/hello-request.bytes"
};
static const uint8_t hello_result[] = {
#include "vectors/hello-result.bytes"
};
static const uint8_t get_state[] = {
#include "vectors/get-state.bytes"
};
static const uint8_t state_snapshot[] = {
#include "vectors/state-snapshot.bytes"
};
static const uint8_t layer_changed[] = {
#include "vectors/layer-changed.bytes"
};
static const uint8_t set_persistent_layer[] = {
#include "vectors/set-persistent-layer.bytes"
};
static const uint8_t press_momentary_layer[] = {
#include "vectors/press-momentary-layer.bytes"
};
static const uint8_t renew_momentary_layer[] = {
#include "vectors/renew-momentary-layer.bytes"
};
static const uint8_t release_momentary_layer[] = {
#include "vectors/release-momentary-layer.bytes"
};
static const uint8_t command_result[] = {
#include "vectors/command-result.bytes"
};
static const uint8_t error_message[] = {
#include "vectors/error.bytes"
};

static void assert_round_trip(
    const uint8_t packet[SHINYGO60_PACKET_SIZE], enum shinygo60_message_type expected_type)
{
    struct shinygo60_message message;
    assert(shinygo60_protocol_decode(packet, SHINYGO60_PACKET_SIZE, &message) ==
           SHINYGO60_DECODE_OK);
    assert(message.type == expected_type);

    uint8_t encoded[SHINYGO60_PACKET_SIZE];
    assert(shinygo60_protocol_encode(&message, encoded));
    assert(memcmp(packet, encoded, SHINYGO60_PACKET_SIZE) == 0);
}

static void verify_golden_vectors(void)
{
    assert(sizeof(hello_request) == SHINYGO60_PACKET_SIZE);
    assert_round_trip(hello_request, SHINYGO60_MESSAGE_HELLO);
    assert_round_trip(hello_result, SHINYGO60_MESSAGE_HELLO_RESULT);
    assert_round_trip(get_state, SHINYGO60_MESSAGE_GET_STATE);
    assert_round_trip(state_snapshot, SHINYGO60_MESSAGE_STATE_SNAPSHOT);
    assert_round_trip(layer_changed, SHINYGO60_MESSAGE_LAYER_CHANGED);
    assert_round_trip(set_persistent_layer, SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER);
    assert_round_trip(press_momentary_layer, SHINYGO60_MESSAGE_PRESS_MOMENTARY_LAYER);
    assert_round_trip(renew_momentary_layer, SHINYGO60_MESSAGE_RENEW_MOMENTARY_LAYER);
    assert_round_trip(release_momentary_layer, SHINYGO60_MESSAGE_RELEASE_MOMENTARY_LAYER);
    assert_round_trip(command_result, SHINYGO60_MESSAGE_COMMAND_RESULT);
    assert_round_trip(error_message, SHINYGO60_MESSAGE_ERROR);

    struct shinygo60_message message;
    assert(shinygo60_protocol_decode(hello_request, sizeof(hello_request), &message) ==
           SHINYGO60_DECODE_OK);
    assert(message.payload.hello.client_nonce == 0x1234U);
    assert(message.payload.hello.requested_capabilities == 0x07U);
    assert(message.payload.hello.expected_layout[0] == 0xb4U);
    assert(message.payload.hello.expected_layout[7] == 0xf3U);

    assert(shinygo60_protocol_decode(layer_changed, sizeof(layer_changed), &message) ==
           SHINYGO60_DECODE_OK);
    assert(message.payload.state.session_id == 0x89abcdefU);
    assert(message.payload.state.state.revision == 43U);
    assert(message.payload.state.related_id == 0x11223344U);
    assert(message.payload.state.state.effective_layer == 4U);
    assert(message.payload.state.state.persistent_layer == 4U);
    assert(message.payload.state.state.momentary_count == 1U);
    assert(message.payload.state.state.indicators == 3U);

    assert(shinygo60_protocol_decode(error_message, sizeof(error_message), &message) ==
           SHINYGO60_DECODE_OK);
    assert(message.payload.error.code == SHINYGO60_ERROR_STALE_STATE);
    assert(message.payload.error.offending_message_type == SHINYGO60_MESSAGE_SET_PERSISTENT_LAYER);
    assert(message.payload.error.detail == 42U);
}

static void assert_decode_result(
    const uint8_t *packet, size_t length, enum shinygo60_decode_result expected)
{
    struct shinygo60_message message;
    assert(shinygo60_protocol_decode(packet, length, &message) == expected);
}

static void verify_malformed_packets(void)
{
    uint8_t packet[SHINYGO60_PACKET_SIZE];

    assert_decode_result(hello_request, SHINYGO60_PACKET_SIZE - 1U, SHINYGO60_DECODE_BAD_LENGTH);

    memcpy(packet, hello_request, sizeof(packet));
    packet[0] = 0U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_BAD_MAGIC);

    memcpy(packet, hello_request, sizeof(packet));
    packet[2] = 0x11U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_UNSUPPORTED_VERSION);

    memcpy(packet, hello_request, sizeof(packet));
    packet[3] = 0x7eU;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_UNKNOWN_TYPE);

    memcpy(packet, hello_request, sizeof(packet));
    packet[6] = 0x80U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);

    memcpy(packet, hello_request, sizeof(packet));
    packet[7] = 1U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);

    memcpy(packet, set_persistent_layer, sizeof(packet));
    packet[16] = SHINYGO60_NO_LAYER;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);

    memcpy(packet, press_momentary_layer, sizeof(packet));
    packet[17] = 0U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);
    packet[17] = SHINYGO60_MAXIMUM_LEASE_UNITS + 1U;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);

    memcpy(packet, state_snapshot, sizeof(packet));
    packet[19] = SHINYGO60_LAYER_STATE_PERSISTENT_ACTIVE;
    assert_decode_result(packet, sizeof(packet), SHINYGO60_DECODE_INVALID_PAYLOAD);
}

int main(void)
{
    verify_golden_vectors();
    verify_malformed_packets();
    puts("C protocol codec: 11 golden vectors and malformed packets passed");
    return 0;
}
