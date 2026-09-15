#include <errno.h>
#include <limits.h>
#include <string.h>
#include <zephyr/bluetooth/conn.h>
#include <zephyr/drivers/hwinfo.h>
#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/random/random.h>
#include <zephyr/sys/byteorder.h>
#include <shinygo60/connection_history.h>
#include <shinygo60/diagnostic.h>

#define FIRST_FAILURE_CAPACITY 32U
#define RECENT_FAILURE_CAPACITY 192U
#define CRITICAL_CAPACITY (FIRST_FAILURE_CAPACITY + RECENT_FAILURE_CAPACITY)
#define ROUTINE_CAPACITY 128U
#define RECORD_SIZE 20U
#define INFO_SIZE 20U
#define CONTEXT_SIZE 20U
#define HISTORY_CAPACITY (CRITICAL_CAPACITY + ROUTINE_CAPACITY)

BUILD_ASSERT(CONFIG_BT_MAX_CONN < 63, "Connection history reserves index 63 for unknown connections");
BUILD_ASSERT(sizeof(SHINYGO60_FEATURE_VERSION) <= 12, "The diagnostic version field includes its terminator");
BUILD_ASSERT(CRITICAL_CAPACITY <= UINT8_MAX && ROUTINE_CAPACITY <= UINT8_MAX, "History capacities must fit metadata");

struct connection_records {
    uint8_t *records;
    uint16_t capacity;
    uint16_t count;
    uint16_t next;
    uint32_t sequence;
};

struct history_download {
    uint8_t records[HISTORY_CAPACITY][RECORD_SIZE];
    uint8_t info[INFO_SIZE];
    uint16_t count;
    uint16_t next;
    bool ready;
};

static uint32_t boot_id;
static uint32_t reset_cause;
static int reset_cause_result;
static uint8_t critical_records[RECENT_FAILURE_CAPACITY][RECORD_SIZE];
static uint8_t first_failures[FIRST_FAILURE_CAPACITY][RECORD_SIZE];
static uint16_t first_failure_count;
static uint8_t routine_records[ROUTINE_CAPACITY][RECORD_SIZE];
static struct connection_records critical = {.records = &critical_records[0][0], .capacity = RECENT_FAILURE_CAPACITY};
static struct connection_records routine = {.records = &routine_records[0][0], .capacity = ROUTINE_CAPACITY};
static struct k_spinlock history_lock;
/* A read never consumes the recorder itself. Each client downloads a frozen copy. */
static struct history_download downloads[CONFIG_BT_MAX_CONN];

void shinygo60_connection_record(enum shinygo60_connection_event event, struct bt_conn *connection,
                                int32_t result, uint16_t a, uint16_t b, uint16_t c, uint16_t d)
{
    struct bt_conn_info info;
    uint8_t role = connection != NULL && bt_conn_get_info(connection, &info) == 0 ? info.role : 3;
    uint8_t index = connection == NULL ? 63 : bt_conn_index(connection);
    bool failure = event == SHINYGO60_DISCONNECTED || event == SHINYGO60_RESPONSE_QUEUE_FAILED ||
                   event == SHINYGO60_INDICATION_FAILED || event == SHINYGO60_INDICATION_SUBMIT_FAILED ||
                   (result != 0 && result != -EALREADY);
    struct connection_records *history = failure ? &critical : &routine;
    k_spinlock_key_t key = k_spin_lock(&history_lock);
    uint8_t *record = history->records + history->next * RECORD_SIZE;
    history->next = (history->next + 1) % history->capacity;
    sys_put_le32(++history->sequence, record);
    sys_put_le32(k_uptime_get_32(), record + 4);
    record[8] = event | (failure ? 0x80 : 0);
    record[9] = (role << 6) | index;
    sys_put_le16((uint16_t)result, record + 10);
    sys_put_le16(a, record + 12);
    sys_put_le16(b, record + 14);
    sys_put_le16(c, record + 16);
    sys_put_le16(d, record + 18);
    history->count = MIN(history->count + 1, history->capacity);
    if (failure && first_failure_count < FIRST_FAILURE_CAPACITY) {
        memcpy(first_failures[first_failure_count++], record, RECORD_SIZE);
    }
    k_spin_unlock(&history_lock, key);
}

static void copy_records(struct history_download *download, const struct connection_records *history,
                         bool same_boot, uint32_t after)
{
    uint16_t recent_index = 0;
    uint16_t first_index = 0;
    uint16_t first_count = history == &critical ? first_failure_count : 0;
    bool copied = false;
    uint32_t previous = 0;
    while (recent_index < history->count || first_index < first_count) {
        const uint8_t *recent = recent_index < history->count ? history->records +
            ((history->next + history->capacity - history->count + recent_index) % history->capacity) * RECORD_SIZE : NULL;
        const uint8_t *first = first_index < first_count ? first_failures[first_index] : NULL;
        /* Merge in sequence order, including across uint32 wrap, without duplicating protected records. */
        bool use_first = first != NULL && (recent == NULL ||
            (uint32_t)(history->sequence - sys_get_le32(first)) >= (uint32_t)(history->sequence - sys_get_le32(recent)));
        const uint8_t *record = use_first ? first : recent;
        uint32_t sequence = sys_get_le32(record);
        if (first != NULL && sys_get_le32(first) == sequence) {
            first_index++;
        }
        if (recent != NULL && sys_get_le32(recent) == sequence) {
            recent_index++;
        }
        uint32_t distance = sequence - after;
        if (!same_boot || (distance != 0 && distance <= INT32_MAX)) {
            /* Format 2 requires a contiguous stream within each snapshot. Resume beyond a gap on the next download. */
            if (copied && sequence != previous + 1U) {
                break;
            }
            memcpy(download->records[download->count++], record, RECORD_SIZE);
            previous = sequence;
            copied = true;
        }
    }
}

ssize_t shinygo60_connection_history_start(struct bt_conn *connection, const void *buffer, uint16_t length, uint16_t offset)
{
    if (offset != 0 || length != 12) {
        return BT_GATT_ERR(BT_ATT_ERR_INVALID_ATTRIBUTE_LEN);
    }
    const uint8_t *request = buffer;
    struct history_download *download = &downloads[bt_conn_index(connection)];
    k_spinlock_key_t key = k_spin_lock(&history_lock);
    bool same_boot = sys_get_le32(request) == boot_id;
    if (same_boot) {
        /* Only a subsequent cursor acknowledges delivery. Starting or interrupting a download releases nothing. */
        uint32_t after = sys_get_le32(request + 4);
        uint16_t acknowledged = 0;
        while (acknowledged < first_failure_count &&
               (uint32_t)(after - sys_get_le32(first_failures[acknowledged])) <= INT32_MAX) {
            acknowledged++;
        }
        first_failure_count -= acknowledged;
        memmove(first_failures, first_failures + acknowledged, first_failure_count * RECORD_SIZE);
    }
    download->count = 0;
    download->next = 0;
    download->info[0] = 2; /* Format version, independent of the control protocol. */
    download->info[1] = RECORD_SIZE;
    download->info[2] = CRITICAL_CAPACITY;
    download->info[3] = ROUTINE_CAPACITY;
    sys_put_le32(boot_id, download->info + 4);
    sys_put_le32(k_uptime_get_32(), download->info + 8);
    sys_put_le32(critical.sequence, download->info + 12);
    sys_put_le32(routine.sequence, download->info + 16);
    copy_records(download, &critical, same_boot, sys_get_le32(request + 4));
    copy_records(download, &routine, same_boot, sys_get_le32(request + 8));
    download->ready = true;
    k_spin_unlock(&history_lock, key);
    return length;
}

ssize_t shinygo60_connection_history_read(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                        void *buffer, uint16_t length, uint16_t offset)
{
    ARG_UNUSED(attribute);
    struct history_download *download = &downloads[bt_conn_index(connection)];
    if (offset != 0) {
        return BT_GATT_ERR(BT_ATT_ERR_INVALID_OFFSET);
    }
    if (!download->ready) {
        return BT_GATT_ERR(BT_ATT_ERR_VALUE_NOT_ALLOWED);
    }
    if (download->next == download->count) {
        return 0;
    }
    /* One record fits the default ATT MTU: no long-read transaction or continuation. */
    if (length < RECORD_SIZE) {
        return BT_GATT_ERR(BT_ATT_ERR_INSUFFICIENT_RESOURCES);
    }
    memcpy(buffer, download->records[download->next++], RECORD_SIZE);
    return RECORD_SIZE;
}

ssize_t shinygo60_connection_history_info(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                        void *buffer, uint16_t length, uint16_t offset)
{
    struct history_download *download = &downloads[bt_conn_index(connection)];
    if (!download->ready) {
        /* Discovery can read the format without starting a download. */
        const uint8_t info[INFO_SIZE] = {2, RECORD_SIZE, CRITICAL_CAPACITY, ROUTINE_CAPACITY};
        return bt_gatt_attr_read(connection, attribute, buffer, length, offset, info, sizeof(info));
    }
    return bt_gatt_attr_read(connection, attribute, buffer, length, offset, download->info, INFO_SIZE);
}

ssize_t shinygo60_connection_history_context(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                           void *buffer, uint16_t length, uint16_t offset)
{
    uint8_t context[CONTEXT_SIZE] = {0};
    sys_put_le32(reset_cause, context);
    sys_put_le32((uint32_t)reset_cause_result, context + 4);
    memcpy(context + 8, SHINYGO60_FEATURE_VERSION, sizeof(SHINYGO60_FEATURE_VERSION));
    return bt_gatt_attr_read(connection, attribute, buffer, length, offset, context, sizeof(context));
}

static void report_connection(struct bt_conn *connection, enum shinygo60_connection_event event, uint8_t result)
{
    struct bt_conn_info info;
    bool available = bt_conn_get_info(connection, &info) == 0 && info.type == BT_CONN_TYPE_LE;
    shinygo60_connection_record(event, connection, result, available ? info.le.interval : 0,
                               available ? info.le.latency : 0, available ? info.le.timeout : 0, 0);
}

static void connection_connected(struct bt_conn *connection, uint8_t error)
{
    downloads[bt_conn_index(connection)].ready = false;
    report_connection(connection, SHINYGO60_CONNECTED, error);
}

static void connection_disconnected(struct bt_conn *connection, uint8_t reason)
{
    downloads[bt_conn_index(connection)].ready = false;
    report_connection(connection, SHINYGO60_DISCONNECTED, reason);
}

static void connection_security_changed(struct bt_conn *connection, bt_security_t level, enum bt_security_err error)
{
    shinygo60_connection_record(SHINYGO60_SECURITY_CHANGED, connection, error, level, 0, 0, 0);
}

static void connection_parameters_updated(struct bt_conn *connection, uint16_t interval, uint16_t latency, uint16_t timeout)
{
    shinygo60_connection_record(SHINYGO60_PARAMETERS_UPDATED, connection, 0, interval, latency, timeout, 0);
}

BT_CONN_CB_DEFINE(shinygo60_diagnostic_callbacks) = {
    .connected = connection_connected,
    .disconnected = connection_disconnected,
    .security_changed = connection_security_changed,
    .le_param_updated = connection_parameters_updated,
};

static int connection_diagnostics_initialize(void)
{
    boot_id = sys_rand32_get();
    reset_cause_result = hwinfo_get_reset_cause(&reset_cause);
    return 0;
}

SYS_INIT(connection_diagnostics_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);
