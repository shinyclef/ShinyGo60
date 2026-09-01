#include <zephyr/bluetooth/bluetooth.h>
#include <zephyr/bluetooth/conn.h>
#include <zephyr/bluetooth/gatt.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/atomic.h>
#include <zephyr/sys/util.h>

#include <shinygo60/protocol.h>

#define SHINYGO60_BT_UUID(number) BT_UUID_128_ENCODE(number, 0x7f76, 0x4c2a, 0x9c46, 0x9b7317f6a1e0)
#define SHINYGO60_BT_SERVICE_UUID SHINYGO60_BT_UUID(0x5a9c0000)
#define SHINYGO60_BT_MESSAGE_UUID SHINYGO60_BT_UUID(0x5a9c0001)

BUILD_ASSERT(IS_ENABLED(CONFIG_ZMK_BLE), "The ShinyGo60 Bluetooth transport requires ZMK BLE");

static atomic_t indication_pending;
static struct bt_conn *pending_connection;
static uint8_t indication_response[SHINYGO60_PACKET_SIZE];

static void indicate_response_work_handler(struct k_work *work);
static K_WORK_DEFINE(indicate_response_work, indicate_response_work_handler);

static void indication_configuration_changed(const struct bt_gatt_attr *attribute, uint16_t value)
{
    ARG_UNUSED(attribute);
    ARG_UNUSED(value);
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

    if (!atomic_cas(&indication_pending, 0, 1)) {
        return BT_GATT_ERR(BT_ATT_ERR_UNLIKELY);
    }

    if (!shinygo60_protocol_handle(buffer, length, indication_response)) {
        atomic_clear(&indication_pending);
        return BT_GATT_ERR(BT_ATT_ERR_VALUE_NOT_ALLOWED);
    }

    pending_connection = bt_conn_ref(connection);
    if (k_work_submit(&indicate_response_work) < 0) {
        bt_conn_unref(pending_connection);
        pending_connection = NULL;
        atomic_clear(&indication_pending);
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
    ARG_UNUSED(error);

    bt_conn_unref(pending_connection);
    pending_connection = NULL;
    atomic_clear(&indication_pending);
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

    if (bt_gatt_indicate(pending_connection, &indication_parameters) < 0) {
        bt_conn_unref(pending_connection);
        pending_connection = NULL;
        atomic_clear(&indication_pending);
    }
}
