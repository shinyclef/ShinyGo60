#include <zephyr/sys/util.h>

#include <zmk/event_manager.h>
#include <zmk/events/layer_state_changed.h>
#include <shinygo60/layer_control.h>

static int layer_state_changed_listener(const zmk_event_t *event)
{
    ARG_UNUSED(event);

    shinygo60_layer_control_observe_zmk_state();
    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(shinygo60_layer_telemetry, layer_state_changed_listener);
ZMK_SUBSCRIPTION(shinygo60_layer_telemetry, zmk_layer_state_changed);
