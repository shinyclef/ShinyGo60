#include <zephyr/init.h>
#include <zephyr/sys/util.h>

#include <zmk/event_manager.h>
#include <zmk/events/layer_state_changed.h>
#include <zmk/keymap.h>

#include <shinygo60/protocol.h>

static void observe_effective_layer(void)
{
    shinygo60_protocol_observe_effective_layer(zmk_keymap_highest_layer_active());
}

static int layer_state_changed_listener(const zmk_event_t *event)
{
    ARG_UNUSED(event);

    observe_effective_layer();
    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(shinygo60_layer_telemetry, layer_state_changed_listener);
ZMK_SUBSCRIPTION(shinygo60_layer_telemetry, zmk_layer_state_changed);

static int shinygo60_layer_telemetry_initialize(void)
{
    observe_effective_layer();
    return 0;
}

SYS_INIT(shinygo60_layer_telemetry_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);
