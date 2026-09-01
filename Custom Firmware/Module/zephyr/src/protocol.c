#include <string.h>

#include <shinygo60/protocol.h>

bool shinygo60_protocol_handle(const uint8_t request[SHINYGO60_PACKET_SIZE], size_t request_length,
                               uint8_t response[SHINYGO60_PACKET_SIZE])
{
    if (request_length != SHINYGO60_PACKET_SIZE || request[0] != SHINYGO60_PACKET_MAGIC_0 ||
        request[1] != SHINYGO60_PACKET_MAGIC_1 || request[2] != SHINYGO60_PACKET_MAGIC_2 ||
        request[3] != SHINYGO60_PACKET_MAGIC_3 || request[4] != SHINYGO60_PROTOCOL_MAJOR ||
        request[5] != SHINYGO60_PROTOCOL_MINOR || request[6] != SHINYGO60_MESSAGE_HELLO ||
        request[7] != 0U) {
        return false;
    }

    memcpy(response, request, SHINYGO60_PACKET_SIZE);
    response[6] = SHINYGO60_MESSAGE_HELLO_RESULT;
    return true;
}
