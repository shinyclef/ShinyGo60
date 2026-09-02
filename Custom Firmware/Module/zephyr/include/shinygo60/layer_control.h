#ifndef SHINYGO60_LAYER_CONTROL_H_
#define SHINYGO60_LAYER_CONTROL_H_

#include <stdbool.h>
#include <stdint.h>

#define SHINYGO60_MOMENTARY_ACTIVATION_CAPACITY 8U

enum shinygo60_layer_control_result {
    SHINYGO60_LAYER_CONTROL_APPLIED,
    SHINYGO60_LAYER_CONTROL_NO_CHANGE,
    SHINYGO60_LAYER_CONTROL_ALREADY_RELEASED,
    SHINYGO60_LAYER_CONTROL_STALE_STATE,
    SHINYGO60_LAYER_CONTROL_BUSY,
    SHINYGO60_LAYER_CONTROL_WRONG_SESSION,
    SHINYGO60_LAYER_CONTROL_INTERNAL,
};

bool shinygo60_layer_control_layer_is_valid(uint8_t layer);

void shinygo60_layer_control_begin_session(uint32_t session_id);

void shinygo60_layer_control_end_session(uint32_t session_id);

enum shinygo60_layer_control_result shinygo60_layer_control_set_persistent(
    uint8_t layer, uint32_t expected_revision, uint32_t source_command_id);

enum shinygo60_layer_control_result shinygo60_layer_control_press(
    uint32_t session_id,
    uint32_t activation_id,
    uint8_t layer,
    uint8_t lease_units,
    uint32_t expected_revision,
    uint32_t source_command_id);

enum shinygo60_layer_control_result shinygo60_layer_control_renew(
    uint32_t session_id, uint32_t activation_id, uint8_t lease_units);

enum shinygo60_layer_control_result shinygo60_layer_control_release(
    uint32_t session_id, uint32_t activation_id, uint32_t source_command_id);

void shinygo60_layer_control_observe_zmk_state(void);

#endif /* SHINYGO60_LAYER_CONTROL_H_ */
