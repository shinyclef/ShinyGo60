#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/bluetooth/services/bas.h>
#include <zephyr/sys/util.h>

#include <zmk/activity.h>
#include <zmk/battery.h>
#include <zmk/event_manager.h>
#include <zmk/events/activity_state_changed.h>
#include <zmk/events/battery_state_changed.h>

#define INITIAL_HEARTBEAT_DELAY K_SECONDS(2)
#define HEARTBEAT_INTERVAL K_SECONDS(CONFIG_SHINYGO60_BATTERY_HEARTBEAT_SECONDS)

static void battery_heartbeat_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(battery_heartbeat_work, battery_heartbeat_work_handler);

static void battery_heartbeat_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    if (zmk_activity_get_state() != ZMK_ACTIVITY_ACTIVE) {
        return;
    }

    uint8_t level = zmk_battery_state_of_charge();
#if IS_ENABLED(CONFIG_ZMK_SPLIT) && !IS_ENABLED(CONFIG_ZMK_SPLIT_ROLE_CENTRAL) && \
    IS_ENABLED(CONFIG_BT_BAS)
    (void)bt_bas_set_battery_level(level);
#endif
    (void)raise_zmk_battery_state_changed((struct zmk_battery_state_changed){
        .state_of_charge = level,
    });
    (void)k_work_reschedule(&battery_heartbeat_work, HEARTBEAT_INTERVAL);
}

static int activity_state_changed_listener(const zmk_event_t *event)
{
    const struct zmk_activity_state_changed *changed = as_zmk_activity_state_changed(event);
    if (changed == NULL) {
        return ZMK_EV_EVENT_BUBBLE;
    }

    if (changed->state == ZMK_ACTIVITY_ACTIVE) {
        (void)k_work_reschedule(&battery_heartbeat_work, INITIAL_HEARTBEAT_DELAY);
    } else {
        (void)k_work_cancel_delayable(&battery_heartbeat_work);
    }

    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(shinygo60_battery_heartbeat, activity_state_changed_listener);
ZMK_SUBSCRIPTION(shinygo60_battery_heartbeat, zmk_activity_state_changed);

static int battery_heartbeat_initialize(void)
{
    (void)k_work_reschedule(&battery_heartbeat_work, INITIAL_HEARTBEAT_DELAY);
    return 0;
}

SYS_INIT(battery_heartbeat_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);
