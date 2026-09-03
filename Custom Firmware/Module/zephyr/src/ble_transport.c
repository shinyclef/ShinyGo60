#include <errno.h>
#include <string.h>

#include <zephyr/bluetooth/bluetooth.h>
#include <zephyr/bluetooth/conn.h>
#include <zephyr/bluetooth/gatt.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/util.h>

#include <shinygo60/protocol.h>

#define SHINYGO60_BT_UUID(number) BT_UUID_128_ENCODE(number, 0x7f76, 0x4c2a, 0x9c46, 0x9b7317f6a1e0)
#define SHINYGO60_BT_SERVICE_UUID SHINYGO60_BT_UUID(0x5a9c0000)
#define SHINYGO60_BT_MESSAGE_UUID SHINYGO60_BT_UUID(0x5a9c0001)
#define CONNECTION_PARAMETER_SETTLE_DELAY K_MSEC(250)
#define CONNECTION_PARAMETER_RETRY_DELAY K_MSEC(100)
#define CONNECTION_PARAMETER_RETRY_LIMIT 5U
#define INTERACTIVE_LEASE_DURATION K_SECONDS(CONFIG_SHINYGO60_BLE_INTERACTIVE_LEASE_SECONDS)

BUILD_ASSERT(IS_ENABLED(CONFIG_ZMK_BLE), "The ShinyGo60 Bluetooth transport requires ZMK BLE");
BUILD_ASSERT(CONFIG_SHINYGO60_BLE_INTERACTIVE_LATENCY <= CONFIG_BT_PERIPHERAL_PREF_LATENCY,
             "Interactive Bluetooth latency must not exceed the power-saving latency");

static bool indication_pending;
static bool response_queued;
static struct bt_conn *pending_connection;
static struct bt_conn *queued_response_connection;
static struct bt_conn *owner_connection;
static struct k_spinlock indication_lock;
static struct k_spinlock owner_lock;
static uint8_t indication_response[SHINYGO60_PACKET_SIZE];
static uint8_t queued_response[SHINYGO60_PACKET_SIZE];
static enum shinygo60_bluetooth_connection_mode connection_mode =
    SHINYGO60_BLUETOOTH_POWER_SAVING;
static bool interactive_lease_active;
static int64_t interactive_lease_expires_at;
static uint8_t connection_parameter_retry_count;

static void indicate_response_work_handler(struct k_work *work);
static K_WORK_DEFINE(indicate_response_work, indicate_response_work_handler);
static void connection_parameter_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(connection_parameter_work, connection_parameter_work_handler);
static void interactive_lease_work_handler(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(interactive_lease_work, interactive_lease_work_handler);

static uint16_t desired_peripheral_latency(void)
{
    return connection_mode == SHINYGO60_BLUETOOTH_INTERACTIVE && interactive_lease_active
               ? CONFIG_SHINYGO60_BLE_INTERACTIVE_LATENCY
               : CONFIG_BT_PERIPHERAL_PREF_LATENCY;
}

static void schedule_connection_parameter_update(void)
{
    (void)k_work_reschedule(&connection_parameter_work, CONNECTION_PARAMETER_SETTLE_DELAY);
}

static void set_owner_connection(struct bt_conn *connection)
{
    struct bt_conn *replacement = bt_conn_ref(connection);
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    struct bt_conn *previous = owner_connection;
    owner_connection = replacement;
    connection_mode = SHINYGO60_BLUETOOTH_POWER_SAVING;
    interactive_lease_active = false;
    interactive_lease_expires_at = 0;
    connection_parameter_retry_count = 0U;
    k_spin_unlock(&owner_lock, key);

    if (previous != NULL) {
        bt_conn_unref(previous);
    }

    schedule_connection_parameter_update();
    (void)k_work_cancel_delayable(&interactive_lease_work);
}

static struct bt_conn *get_owner_connection(void)
{
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    struct bt_conn *connection = owner_connection == NULL ? NULL : bt_conn_ref(owner_connection);
    k_spin_unlock(&owner_lock, key);
    return connection;
}

static bool should_retry_parameter_update(int result)
{
    return result == -EAGAIN || result == -EBUSY || result == -ENOMEM || result == -ENOBUFS;
}

static void connection_parameter_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    struct bt_conn *connection = get_owner_connection();
    if (connection == NULL) {
        return;
    }

    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    uint16_t requested_latency = desired_peripheral_latency();
    k_spin_unlock(&owner_lock, key);

    int result = bt_conn_le_param_update(
        connection,
        BT_LE_CONN_PARAM(CONFIG_BT_PERIPHERAL_PREF_MIN_INT,
                         CONFIG_BT_PERIPHERAL_PREF_MAX_INT,
                         requested_latency,
                         CONFIG_BT_PERIPHERAL_PREF_TIMEOUT));

    k_timeout_t retry_delay = K_NO_WAIT;
    bool retry = false;
    key = k_spin_lock(&owner_lock);
    if (owner_connection == connection && desired_peripheral_latency() != requested_latency) {
        connection_parameter_retry_count = 0U;
        retry_delay = CONNECTION_PARAMETER_SETTLE_DELAY;
        retry = true;
    } else if (owner_connection == connection && should_retry_parameter_update(result) &&
               connection_parameter_retry_count < CONNECTION_PARAMETER_RETRY_LIMIT) {
        connection_parameter_retry_count++;
        retry_delay = CONNECTION_PARAMETER_RETRY_DELAY;
        retry = true;
    } else {
        connection_parameter_retry_count = 0U;
    }
    k_spin_unlock(&owner_lock, key);
    bt_conn_unref(connection);

    if (retry) {
        (void)k_work_reschedule(&connection_parameter_work, retry_delay);
    }
}

static void interactive_lease_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    int64_t now = k_uptime_get();
    int64_t remaining = 0;
    bool expired = false;
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    if (connection_mode == SHINYGO60_BLUETOOTH_INTERACTIVE && interactive_lease_active) {
        remaining = interactive_lease_expires_at - now;
        if (remaining <= 0) {
            interactive_lease_active = false;
            interactive_lease_expires_at = 0;
            connection_parameter_retry_count = 0U;
            expired = true;
        }
    }
    k_spin_unlock(&owner_lock, key);

    if (expired) {
        schedule_connection_parameter_update();
    } else if (remaining > 0) {
        (void)k_work_reschedule(&interactive_lease_work, K_MSEC(remaining));
    }
}

enum shinygo60_bluetooth_mode_result shinygo60_ble_set_connection_mode(
    enum shinygo60_bluetooth_connection_mode mode)
{
    if (mode != SHINYGO60_BLUETOOTH_POWER_SAVING &&
        mode != SHINYGO60_BLUETOOTH_INTERACTIVE) {
        return SHINYGO60_BLUETOOTH_MODE_UNAVAILABLE;
    }

    int64_t now = k_uptime_get();
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    if (owner_connection == NULL) {
        k_spin_unlock(&owner_lock, key);
        return SHINYGO60_BLUETOOTH_MODE_UNAVAILABLE;
    }

    uint16_t previous_latency = desired_peripheral_latency();
    bool mode_changed = connection_mode != mode;
    connection_mode = mode;
    if (mode == SHINYGO60_BLUETOOTH_INTERACTIVE) {
        interactive_lease_active = true;
        interactive_lease_expires_at = now +
                                       (CONFIG_SHINYGO60_BLE_INTERACTIVE_LEASE_SECONDS * 1000LL);
    } else {
        interactive_lease_active = false;
        interactive_lease_expires_at = 0;
    }

    bool latency_changed = desired_peripheral_latency() != previous_latency;
    if (latency_changed) {
        connection_parameter_retry_count = 0U;
    }
    k_spin_unlock(&owner_lock, key);

    if (mode == SHINYGO60_BLUETOOTH_INTERACTIVE) {
        (void)k_work_reschedule(&interactive_lease_work, INTERACTIVE_LEASE_DURATION);
    } else {
        (void)k_work_cancel_delayable(&interactive_lease_work);
    }

    if (latency_changed) {
        schedule_connection_parameter_update();
    }

    return mode_changed || latency_changed ? SHINYGO60_BLUETOOTH_MODE_APPLIED
                                           : SHINYGO60_BLUETOOTH_MODE_NO_CHANGE;
}

void shinygo60_ble_note_companion_activity(void)
{
    int64_t now = k_uptime_get();
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    bool renew = owner_connection != NULL &&
                 connection_mode == SHINYGO60_BLUETOOTH_INTERACTIVE;
    bool latency_changed = renew && !interactive_lease_active;
    if (renew) {
        interactive_lease_active = true;
        interactive_lease_expires_at = now +
                                       (CONFIG_SHINYGO60_BLE_INTERACTIVE_LEASE_SECONDS * 1000LL);
        if (latency_changed) {
            connection_parameter_retry_count = 0U;
        }
    }
    k_spin_unlock(&owner_lock, key);

    if (renew) {
        (void)k_work_reschedule(&interactive_lease_work, INTERACTIVE_LEASE_DURATION);
    }

    if (latency_changed) {
        schedule_connection_parameter_update();
    }
}

void shinygo60_ble_reset_connection_mode(void)
{
    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    bool changed = desired_peripheral_latency() != CONFIG_BT_PERIPHERAL_PREF_LATENCY;
    connection_mode = SHINYGO60_BLUETOOTH_POWER_SAVING;
    interactive_lease_active = false;
    interactive_lease_expires_at = 0;
    if (changed) {
        connection_parameter_retry_count = 0U;
    }
    k_spin_unlock(&owner_lock, key);

    (void)k_work_cancel_delayable(&interactive_lease_work);

    if (changed) {
        schedule_connection_parameter_update();
    }
}

static void discard_indications(void)
{
    k_spinlock_key_t key = k_spin_lock(&indication_lock);
    struct bt_conn *discarded_pending = pending_connection;
    struct bt_conn *discarded_queued = queued_response_connection;
    pending_connection = NULL;
    queued_response_connection = NULL;
    indication_pending = false;
    response_queued = false;
    k_spin_unlock(&indication_lock, key);

    if (discarded_pending != NULL) {
        bt_conn_unref(discarded_pending);
    }

    if (discarded_queued != NULL) {
        bt_conn_unref(discarded_queued);
    }
}

static bool enqueue_indication(
    struct bt_conn *connection,
    const uint8_t packet[SHINYGO60_PACKET_SIZE],
    bool may_queue_response)
{
    struct bt_conn *connection_reference = bt_conn_ref(connection);
    if (connection_reference == NULL) {
        return false;
    }

    bool submit = false;
    bool accepted = false;
    k_spinlock_key_t key = k_spin_lock(&indication_lock);
    if (!indication_pending) {
        memcpy(indication_response, packet, SHINYGO60_PACKET_SIZE);
        pending_connection = connection_reference;
        indication_pending = true;
        submit = true;
        accepted = true;
    } else if (may_queue_response && !response_queued) {
        memcpy(queued_response, packet, SHINYGO60_PACKET_SIZE);
        queued_response_connection = connection_reference;
        response_queued = true;
        accepted = true;
    }
    k_spin_unlock(&indication_lock, key);

    if (!accepted) {
        bt_conn_unref(connection_reference);
        return false;
    }

    if (submit && k_work_submit(&indicate_response_work) < 0) {
        discard_indications();
        return false;
    }

    return true;
}

static void complete_indication(void)
{
    struct bt_conn *completed_connection;
    bool submit_next = false;
    k_spinlock_key_t key = k_spin_lock(&indication_lock);
    completed_connection = pending_connection;
    pending_connection = NULL;
    if (response_queued) {
        memcpy(indication_response, queued_response, SHINYGO60_PACKET_SIZE);
        pending_connection = queued_response_connection;
        queued_response_connection = NULL;
        response_queued = false;
        submit_next = true;
    } else {
        indication_pending = false;
    }
    k_spin_unlock(&indication_lock, key);

    if (completed_connection != NULL) {
        bt_conn_unref(completed_connection);
    }

    if (submit_next && k_work_submit(&indicate_response_work) < 0) {
        discard_indications();
    }
}

static void indication_configuration_changed(const struct bt_gatt_attr *attribute, uint16_t value)
{
    ARG_UNUSED(attribute);

    if (value != BT_GATT_CCC_INDICATE) {
        shinygo60_protocol_transport_disconnected(SHINYGO60_TRANSPORT_BLUETOOTH);
    }
}

struct bond_search {
    const bt_addr_le_t *address;
    bool found;
};

static void find_bond(const struct bt_bond_info *bond, void *user_data)
{
    struct bond_search *search = user_data;
    if (bt_addr_le_cmp(&bond->addr, search->address) == 0) {
        search->found = true;
    }
}

static bool is_encrypted_bonded_host(struct bt_conn *connection)
{
    struct bond_search search = {
        .address = bt_conn_get_dst(connection),
    };

    bt_foreach_bond(BT_ID_DEFAULT, find_bond, &search);
    return search.found && bt_conn_get_security(connection) >= BT_SECURITY_L2;
}

static ssize_t write_message(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                             const void *buffer, uint16_t length, uint16_t offset, uint8_t flags)
{
    ARG_UNUSED(attribute);
    ARG_UNUSED(flags);

    if (offset != 0U) {
        return BT_GATT_ERR(BT_ATT_ERR_INVALID_OFFSET);
    }

    if (length != SHINYGO60_PACKET_SIZE) {
        return BT_GATT_ERR(BT_ATT_ERR_INVALID_ATTRIBUTE_LEN);
    }

    if (!is_encrypted_bonded_host(connection)) {
        return BT_GATT_ERR(BT_ATT_ERR_VALUE_NOT_ALLOWED);
    }

    uint8_t response[SHINYGO60_PACKET_SIZE];
    if (!shinygo60_protocol_handle(
            SHINYGO60_TRANSPORT_BLUETOOTH, buffer, length, response)) {
        return BT_GATT_ERR(BT_ATT_ERR_VALUE_NOT_ALLOWED);
    }

    const uint8_t *request = buffer;
    if (request[3] == SHINYGO60_MESSAGE_HELLO &&
        response[3] == SHINYGO60_MESSAGE_HELLO_RESULT &&
        response[6] == SHINYGO60_HELLO_SUCCESS) {
        set_owner_connection(connection);
    }

    shinygo60_ble_note_companion_activity();

    if (!enqueue_indication(connection, response, true)) {
        return BT_GATT_ERR(BT_ATT_ERR_UNLIKELY);
    }

    return length;
}

BT_GATT_SERVICE_DEFINE(
    shinygo60_service, BT_GATT_PRIMARY_SERVICE(BT_UUID_DECLARE_128(SHINYGO60_BT_SERVICE_UUID)),
    BT_GATT_CHARACTERISTIC(BT_UUID_DECLARE_128(SHINYGO60_BT_MESSAGE_UUID),
                           BT_GATT_CHRC_WRITE | BT_GATT_CHRC_INDICATE, BT_GATT_PERM_WRITE_ENCRYPT,
                           NULL, write_message, NULL),
    BT_GATT_CCC(indication_configuration_changed,
                BT_GATT_PERM_READ_ENCRYPT | BT_GATT_PERM_WRITE_ENCRYPT));

static void indication_complete(struct bt_conn *connection,
                                struct bt_gatt_indicate_params *parameters, uint8_t error)
{
    ARG_UNUSED(connection);
    ARG_UNUSED(parameters);

    if (error == 0U) {
        complete_indication();
    } else {
        discard_indications();
    }
}

static struct bt_gatt_indicate_params indication_parameters = {
    .attr = &shinygo60_service.attrs[1],
    .func = indication_complete,
    .data = indication_response,
    .len = sizeof(indication_response),
};

static void indicate_response_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    k_spinlock_key_t key = k_spin_lock(&indication_lock);
    struct bt_conn *connection = pending_connection;
    k_spin_unlock(&indication_lock, key);
    if (connection == NULL || bt_gatt_indicate(connection, &indication_parameters) < 0) {
        discard_indications();
    }
}

bool shinygo60_ble_send(const uint8_t packet[SHINYGO60_PACKET_SIZE])
{
    struct bt_conn *connection = get_owner_connection();
    if (connection == NULL) {
        return false;
    }

    bool submitted = enqueue_indication(connection, packet, false);
    bt_conn_unref(connection);
    return submitted;
}

static void connection_disconnected(struct bt_conn *connection, uint8_t reason)
{
    ARG_UNUSED(reason);

    k_spinlock_key_t key = k_spin_lock(&owner_lock);
    bool was_owner = owner_connection == connection;
    struct bt_conn *released = was_owner ? owner_connection : NULL;
    if (was_owner) {
        owner_connection = NULL;
        connection_mode = SHINYGO60_BLUETOOTH_POWER_SAVING;
        interactive_lease_active = false;
        interactive_lease_expires_at = 0;
        connection_parameter_retry_count = 0U;
    }
    k_spin_unlock(&owner_lock, key);

    if (released != NULL) {
        (void)k_work_cancel_delayable(&connection_parameter_work);
        (void)k_work_cancel_delayable(&interactive_lease_work);
        bt_conn_unref(released);
        shinygo60_protocol_transport_disconnected(SHINYGO60_TRANSPORT_BLUETOOTH);
    }
}

BT_CONN_CB_DEFINE(shinygo60_connection_callbacks) = {
    .disconnected = connection_disconnected,
};
