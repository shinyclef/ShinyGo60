#include <stdbool.h>
#include <stdint.h>

#include <zephyr/kernel.h>
#include <zephyr/sys/util.h>

#include <zmk/event_manager.h>
#include <zmk/events/battery_state_changed.h>

#include <shinygo60/protocol.h>

#define STALE_AFTER_MILLISECONDS ((int64_t)CONFIG_SHINYGO60_BATTERY_STALE_SECONDS * 1000)

struct battery_observation {
    int64_t observed_at;
    uint8_t level;
    bool available;
};

static K_MUTEX_DEFINE(observations_lock);
static struct battery_observation left_observation;
static struct battery_observation right_observation;

static void left_stale_work_handler(struct k_work *work);
static void right_stale_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(left_stale_work, left_stale_work_handler);
static K_WORK_DELAYABLE_DEFINE(right_stale_work, right_stale_work_handler);

static struct battery_observation *observation_for(enum shinygo60_battery_half half)
{
    return half == SHINYGO60_BATTERY_HALF_LEFT ? &left_observation : &right_observation;
}

static struct k_work_delayable *stale_work_for(enum shinygo60_battery_half half)
{
    return half == SHINYGO60_BATTERY_HALF_LEFT ? &left_stale_work : &right_stale_work;
}

static void observe_battery(enum shinygo60_battery_half half, uint8_t level)
{
    struct battery_observation *observation = observation_for(half);
    k_mutex_lock(&observations_lock, K_FOREVER);
    observation->observed_at = k_uptime_get();
    observation->level = level;
    observation->available = true;
    shinygo60_protocol_observe_battery(half, level, true, false);
    (void)k_work_reschedule(stale_work_for(half), K_MSEC(STALE_AFTER_MILLISECONDS));
    k_mutex_unlock(&observations_lock);
}

static void mark_battery_unavailable(enum shinygo60_battery_half half)
{
    struct battery_observation *observation = observation_for(half);
    k_mutex_lock(&observations_lock, K_FOREVER);
    observation->available = false;
    (void)k_work_cancel_delayable(stale_work_for(half));
    shinygo60_protocol_observe_battery(half, 0U, false, false);
    k_mutex_unlock(&observations_lock);
}

static void refresh_half(enum shinygo60_battery_half half)
{
    k_mutex_lock(&observations_lock, K_FOREVER);
    struct battery_observation observation = *observation_for(half);

    if (!observation.available) {
        shinygo60_protocol_observe_battery(half, 0U, false, false);
        k_mutex_unlock(&observations_lock);
        return;
    }

    int64_t elapsed = k_uptime_get() - observation.observed_at;
    bool stale = elapsed >= STALE_AFTER_MILLISECONDS;
    shinygo60_protocol_observe_battery(half, observation.level, true, stale);
    if (!stale) {
        (void)k_work_reschedule(stale_work_for(half), K_MSEC(STALE_AFTER_MILLISECONDS - elapsed));
    }
    k_mutex_unlock(&observations_lock);
}

static void left_stale_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);
    refresh_half(SHINYGO60_BATTERY_HALF_LEFT);
}

static void right_stale_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);
    refresh_half(SHINYGO60_BATTERY_HALF_RIGHT);
}

void shinygo60_battery_telemetry_refresh(void)
{
    refresh_half(SHINYGO60_BATTERY_HALF_LEFT);
    refresh_half(SHINYGO60_BATTERY_HALF_RIGHT);
}

static int battery_state_changed_listener(const zmk_event_t *event)
{
    const struct zmk_battery_state_changed *local = as_zmk_battery_state_changed(event);
    if (local != NULL) {
        if (local->state_of_charge <= 100U) {
            observe_battery(SHINYGO60_BATTERY_HALF_LEFT, local->state_of_charge);
        }
        return ZMK_EV_EVENT_BUBBLE;
    }

    const struct zmk_peripheral_battery_state_changed *peripheral =
        as_zmk_peripheral_battery_state_changed(event);
    if (peripheral == NULL || peripheral->source != 0U || peripheral->state_of_charge > 100U) {
        return ZMK_EV_EVENT_BUBBLE;
    }

    if (peripheral->state_of_charge == 0U) {
        mark_battery_unavailable(SHINYGO60_BATTERY_HALF_RIGHT);
    } else {
        observe_battery(SHINYGO60_BATTERY_HALF_RIGHT, peripheral->state_of_charge);
    }
    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(shinygo60_battery_telemetry, battery_state_changed_listener);
ZMK_SUBSCRIPTION(shinygo60_battery_telemetry, zmk_battery_state_changed);
ZMK_SUBSCRIPTION(shinygo60_battery_telemetry, zmk_peripheral_battery_state_changed);
