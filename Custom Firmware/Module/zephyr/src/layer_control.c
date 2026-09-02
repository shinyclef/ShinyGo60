#include <errno.h>
#include <limits.h>

#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/util.h>

#include <zmk/keymap.h>

#include <shinygo60/layer_control.h>
#include <shinygo60/protocol.h>

struct momentary_activation {
    uint32_t session_id;
    uint32_t activation_id;
    int64_t expires_at;
    uint8_t layer;
    bool active;
};

extern int __real_zmk_keymap_layer_activate(zmk_keymap_layer_id_t layer);
extern int __real_zmk_keymap_layer_deactivate(zmk_keymap_layer_id_t layer);

static K_MUTEX_DEFINE(layer_control_mutex);
static struct momentary_activation activations[SHINYGO60_MOMENTARY_ACTIVATION_CAPACITY];
static zmk_keymap_layers_state_t keyboard_layers;
static uint32_t owning_session_id;
static uint8_t persistent_layer = SHINYGO60_NO_LAYER;
static uint8_t transaction_depth;

static void lease_expiry_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(lease_expiry_work, lease_expiry_work_handler);

bool shinygo60_layer_control_layer_is_valid(uint8_t layer)
{
    return layer < ZMK_KEYMAP_LAYERS_LEN;
}

static bool layer_is_externally_owned(uint8_t layer)
{
    if (persistent_layer == layer) {
        return true;
    }

    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (activations[index].active && activations[index].layer == layer) {
            return true;
        }
    }

    return false;
}

static bool layer_should_be_active(uint8_t layer)
{
    return (keyboard_layers & BIT(layer)) != 0U || layer_is_externally_owned(layer) ||
           layer == zmk_keymap_layer_default();
}

static int reconcile_layer(uint8_t layer)
{
    bool should_be_active = layer_should_be_active(layer);
    if (zmk_keymap_layer_active(layer) == should_be_active) {
        return 0;
    }

    int result = should_be_active ? __real_zmk_keymap_layer_activate(layer)
                                  : __real_zmk_keymap_layer_deactivate(layer);
    return zmk_keymap_layer_active(layer) == should_be_active ? 0 : result == 0 ? -EIO : result;
}

static int reconcile_layers(zmk_keymap_layers_state_t layers)
{
    int first_error = 0;
    for (uint8_t layer = 0U; layer < ZMK_KEYMAP_LAYERS_LEN; layer++) {
        if ((layers & BIT(layer)) == 0U) {
            continue;
        }

        int result = reconcile_layer(layer);
        if (first_error == 0 && result < 0) {
            first_error = result;
        }
    }

    return first_error;
}

static uint8_t active_momentary_count(void)
{
    uint8_t count = 0U;
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        count += activations[index].active ? 1U : 0U;
    }

    return count;
}

static void publish_state(uint32_t source_command_id)
{
    if (transaction_depth != 0U) {
        return;
    }

    zmk_keymap_layer_index_t effective_index = zmk_keymap_highest_layer_active();
    uint8_t effective_layer = zmk_keymap_layer_index_to_id(effective_index);
    shinygo60_protocol_observe_layer_state(
        effective_layer, persistent_layer, active_momentary_count(), source_command_id);
}

static void begin_transaction(void)
{
    __ASSERT_NO_MSG(transaction_depth < UINT8_MAX);
    transaction_depth++;
}

static void finish_transaction(uint32_t source_command_id)
{
    __ASSERT_NO_MSG(transaction_depth > 0U);
    transaction_depth--;
    publish_state(source_command_id);
}

static int64_t lease_duration(uint8_t lease_units)
{
    return (int64_t)lease_units * SHINYGO60_LEASE_UNIT_MILLISECONDS;
}

static void schedule_next_expiry(void)
{
    int64_t earliest = INT64_MAX;
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (activations[index].active) {
            earliest = MIN(earliest, activations[index].expires_at);
        }
    }

    if (earliest == INT64_MAX) {
        (void)k_work_cancel_delayable(&lease_expiry_work);
        return;
    }

    int64_t remaining = MAX(earliest - k_uptime_get(), 0);
    (void)k_work_reschedule(&lease_expiry_work, K_MSEC(remaining));
}

static bool expire_elapsed_activations(int64_t now)
{
    zmk_keymap_layers_state_t affected_layers = 0U;
    bool expired = false;
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (!activations[index].active || activations[index].expires_at > now) {
            continue;
        }

        affected_layers |= BIT(activations[index].layer);
        activations[index].active = false;
        expired = true;
    }

    if (expired) {
        (void)reconcile_layers(affected_layers);
    }
    return expired;
}

static struct momentary_activation *find_activation(uint32_t session_id, uint32_t activation_id)
{
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (activations[index].active && activations[index].session_id == session_id &&
            activations[index].activation_id == activation_id) {
            return &activations[index];
        }
    }

    return NULL;
}

static struct momentary_activation *find_free_activation(void)
{
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (!activations[index].active) {
            return &activations[index];
        }
    }

    return NULL;
}

static void clear_momentary_activations(void)
{
    zmk_keymap_layers_state_t affected_layers = 0U;
    for (size_t index = 0U; index < ARRAY_SIZE(activations); index++) {
        if (!activations[index].active) {
            continue;
        }

        affected_layers |= BIT(activations[index].layer);
        activations[index].active = false;
    }

    (void)reconcile_layers(affected_layers);
    schedule_next_expiry();
}

void shinygo60_layer_control_begin_session(uint32_t session_id)
{
    __ASSERT_NO_MSG(session_id != 0U);

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    clear_momentary_activations();
    owning_session_id = session_id;
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
}

void shinygo60_layer_control_end_session(uint32_t session_id)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    if (session_id != 0U && owning_session_id == session_id) {
        begin_transaction();
        owning_session_id = 0U;
        clear_momentary_activations();
        finish_transaction(0U);
    }
    k_mutex_unlock(&layer_control_mutex);
}

enum shinygo60_layer_control_result shinygo60_layer_control_set_persistent(
    uint8_t layer, uint32_t expected_revision, uint32_t source_command_id)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    if (shinygo60_protocol_layer_revision() != expected_revision) {
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_STALE_STATE;
    }

    if (persistent_layer == layer) {
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_NO_CHANGE;
    }

    begin_transaction();
    uint8_t previous_layer = persistent_layer;
    persistent_layer = layer;
    zmk_keymap_layers_state_t affected_layers = BIT(layer);
    if (previous_layer != SHINYGO60_NO_LAYER) {
        affected_layers |= BIT(previous_layer);
    }

    int result = reconcile_layers(affected_layers);
    if (result < 0) {
        persistent_layer = previous_layer;
        (void)reconcile_layers(affected_layers);
    }
    finish_transaction(result < 0 ? 0U : source_command_id);
    k_mutex_unlock(&layer_control_mutex);
    return result < 0 ? SHINYGO60_LAYER_CONTROL_INTERNAL : SHINYGO60_LAYER_CONTROL_APPLIED;
}

enum shinygo60_layer_control_result shinygo60_layer_control_press(
    uint32_t session_id,
    uint32_t activation_id,
    uint8_t layer,
    uint8_t lease_units,
    uint32_t expected_revision,
    uint32_t source_command_id)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    if (session_id == 0U || session_id != owning_session_id) {
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_WRONG_SESSION;
    }

    begin_transaction();
    (void)expire_elapsed_activations(k_uptime_get());
    finish_transaction(0U);
    if (shinygo60_protocol_layer_revision() != expected_revision) {
        schedule_next_expiry();
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_STALE_STATE;
    }

    begin_transaction();
    struct momentary_activation *activation = find_free_activation();
    if (activation == NULL) {
        schedule_next_expiry();
        finish_transaction(0U);
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_BUSY;
    }

    *activation = (struct momentary_activation) {
        .session_id = session_id,
        .activation_id = activation_id,
        .expires_at = k_uptime_get() + lease_duration(lease_units),
        .layer = layer,
        .active = true,
    };
    int result = reconcile_layer(layer);
    if (result < 0) {
        activation->active = false;
        (void)reconcile_layer(layer);
    }
    schedule_next_expiry();
    finish_transaction(result < 0 ? 0U : source_command_id);
    k_mutex_unlock(&layer_control_mutex);
    return result < 0 ? SHINYGO60_LAYER_CONTROL_INTERNAL : SHINYGO60_LAYER_CONTROL_APPLIED;
}

enum shinygo60_layer_control_result shinygo60_layer_control_renew(
    uint32_t session_id, uint32_t activation_id, uint8_t lease_units)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    if (session_id == 0U || session_id != owning_session_id) {
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_WRONG_SESSION;
    }

    begin_transaction();
    (void)expire_elapsed_activations(k_uptime_get());
    finish_transaction(0U);
    struct momentary_activation *activation = find_activation(session_id, activation_id);
    if (activation == NULL) {
        schedule_next_expiry();
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_ALREADY_RELEASED;
    }

    activation->expires_at = k_uptime_get() + lease_duration(lease_units);
    schedule_next_expiry();
    k_mutex_unlock(&layer_control_mutex);
    return SHINYGO60_LAYER_CONTROL_NO_CHANGE;
}

enum shinygo60_layer_control_result shinygo60_layer_control_release(
    uint32_t session_id, uint32_t activation_id, uint32_t source_command_id)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    if (session_id == 0U || session_id != owning_session_id) {
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_WRONG_SESSION;
    }

    begin_transaction();
    (void)expire_elapsed_activations(k_uptime_get());
    finish_transaction(0U);
    struct momentary_activation *activation = find_activation(session_id, activation_id);
    if (activation == NULL) {
        schedule_next_expiry();
        k_mutex_unlock(&layer_control_mutex);
        return SHINYGO60_LAYER_CONTROL_ALREADY_RELEASED;
    }

    begin_transaction();
    uint8_t layer = activation->layer;
    activation->active = false;
    int result = reconcile_layer(layer);
    if (result < 0) {
        activation->active = true;
        (void)reconcile_layer(layer);
    }
    schedule_next_expiry();
    finish_transaction(result < 0 ? 0U : source_command_id);
    k_mutex_unlock(&layer_control_mutex);
    return result < 0 ? SHINYGO60_LAYER_CONTROL_INTERNAL : SHINYGO60_LAYER_CONTROL_APPLIED;
}

void shinygo60_layer_control_observe_zmk_state(void)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    publish_state(0U);
    k_mutex_unlock(&layer_control_mutex);
}

static void lease_expiry_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    (void)expire_elapsed_activations(k_uptime_get());
    schedule_next_expiry();
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
}

static int shinygo60_layer_control_initialize(void)
{
    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    keyboard_layers |= zmk_keymap_layer_state();
    publish_state(0U);
    k_mutex_unlock(&layer_control_mutex);
    return 0;
}

SYS_INIT(shinygo60_layer_control_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);

int __wrap_zmk_keymap_layer_activate(zmk_keymap_layer_id_t layer)
{
    if (!shinygo60_layer_control_layer_is_valid(layer)) {
        return -EINVAL;
    }

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    keyboard_layers |= BIT(layer);
    int result = reconcile_layer(layer);
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
    return result;
}

int __wrap_zmk_keymap_layer_deactivate(zmk_keymap_layer_id_t layer)
{
    if (!shinygo60_layer_control_layer_is_valid(layer)) {
        return -EINVAL;
    }

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    keyboard_layers &= ~BIT(layer);
    int result = reconcile_layer(layer);
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
    return result;
}

int __wrap_zmk_keymap_layer_toggle(zmk_keymap_layer_id_t layer)
{
    if (!shinygo60_layer_control_layer_is_valid(layer)) {
        return -EINVAL;
    }

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    keyboard_layers ^= BIT(layer);
    int result = reconcile_layer(layer);
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
    return result;
}

int __wrap_zmk_keymap_layer_to(zmk_keymap_layer_id_t layer)
{
    if (!shinygo60_layer_control_layer_is_valid(layer)) {
        return -EINVAL;
    }

    k_mutex_lock(&layer_control_mutex, K_FOREVER);
    begin_transaction();
    zmk_keymap_layers_state_t previous_layers = keyboard_layers;
    uint8_t previous_persistent_layer = persistent_layer;
    keyboard_layers = BIT(layer);
    /* A physical &to replaces an earlier companion GoTo selection. */
    persistent_layer = SHINYGO60_NO_LAYER;

    zmk_keymap_layers_state_t affected_layers = previous_layers | keyboard_layers;
    if (previous_persistent_layer != SHINYGO60_NO_LAYER) {
        affected_layers |= BIT(previous_persistent_layer);
    }

    int result = reconcile_layers(affected_layers);
    if (result < 0) {
        keyboard_layers = previous_layers;
        persistent_layer = previous_persistent_layer;
        (void)reconcile_layers(affected_layers);
    }
    finish_transaction(0U);
    k_mutex_unlock(&layer_control_mutex);
    return result;
}
