#ifndef SHINYGO60_PROTOCOL_H_
#define SHINYGO60_PROTOCOL_H_

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define SHINYGO60_PACKET_SIZE 16U

#define SHINYGO60_PACKET_MAGIC_0 0x53U
#define SHINYGO60_PACKET_MAGIC_1 0x47U
#define SHINYGO60_PACKET_MAGIC_2 0x36U
#define SHINYGO60_PACKET_MAGIC_3 0x30U

#define SHINYGO60_PROTOCOL_MAJOR 0U
#define SHINYGO60_PROTOCOL_MINOR 1U

enum shinygo60_message_type {
    SHINYGO60_MESSAGE_HELLO = 1,
    SHINYGO60_MESSAGE_HELLO_RESULT = 2,
};

bool shinygo60_protocol_handle(const uint8_t request[SHINYGO60_PACKET_SIZE], size_t request_length,
                               uint8_t response[SHINYGO60_PACKET_SIZE]);

#endif /* SHINYGO60_PROTOCOL_H_ */
