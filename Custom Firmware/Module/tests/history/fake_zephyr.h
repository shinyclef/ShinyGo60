#ifndef SHINYGO60_TEST_ZEPHYR_H
#define SHINYGO60_TEST_ZEPHYR_H
#include <stdbool.h>
#include <stdint.h>
#include <string.h>
#include <sys/types.h>
#define CONFIG_BT_MAX_CONN 2
#define CONFIG_SHINYGO60_CONNECTION_DIAGNOSTICS 1
#define IS_ENABLED(value) (value)
#define BUILD_ASSERT(condition, ...) _Static_assert(condition, #condition)
#define ARG_UNUSED(value) ((void)(value))
#define MIN(a, b) ((a) < (b) ? (a) : (b))
#define BT_ATT_ERR_INVALID_ATTRIBUTE_LEN 13
#define BT_ATT_ERR_INVALID_OFFSET 7
#define BT_ATT_ERR_VALUE_NOT_ALLOWED 19
#define BT_ATT_ERR_INSUFFICIENT_RESOURCES 17
#define BT_GATT_ERR(value) (-(value))
#define BT_CONN_TYPE_LE 1
#define BT_CONN_CB_DEFINE(name) struct bt_conn_cb name
#define SYS_INIT(...)
struct bt_conn { uint8_t index; };
struct bt_gatt_attr { int unused; };
struct k_spinlock { int unused; };
typedef int k_spinlock_key_t;
typedef uint8_t bt_security_t;
enum bt_security_err { BT_SECURITY_ERR_SUCCESS };
struct bt_conn_info { uint8_t role, type; struct { uint16_t interval, latency, timeout; } le; };
struct bt_conn_cb {
    void (*connected)(struct bt_conn *, uint8_t);
    void (*disconnected)(struct bt_conn *, uint8_t);
    void (*security_changed)(struct bt_conn *, bt_security_t, enum bt_security_err);
    void (*le_param_updated)(struct bt_conn *, uint16_t, uint16_t, uint16_t);
};
static uint32_t test_uptime;
static inline uint32_t k_uptime_get_32(void) { return test_uptime; }
static inline k_spinlock_key_t k_spin_lock(struct k_spinlock *lock) { (void)lock; return 0; }
static inline void k_spin_unlock(struct k_spinlock *lock, k_spinlock_key_t key) { (void)lock; (void)key; }
static inline uint8_t bt_conn_index(struct bt_conn *connection) { return connection->index; }
static inline int bt_conn_get_info(struct bt_conn *connection, struct bt_conn_info *info)
{ (void)connection; memset(info, 0, sizeof(*info)); info->role = 1; info->type = BT_CONN_TYPE_LE; return 0; }
static inline void sys_put_le16(uint16_t value, uint8_t *bytes)
{ bytes[0] = value; bytes[1] = value >> 8; }
static inline void sys_put_le32(uint32_t value, uint8_t *bytes)
{ for (int i = 0; i < 4; i++) { bytes[i] = value >> (8 * i); } }
static inline uint32_t sys_get_le32(const uint8_t *bytes)
{ return (uint32_t)bytes[0] | (uint32_t)bytes[1] << 8 | (uint32_t)bytes[2] << 16 | (uint32_t)bytes[3] << 24; }
static inline uint32_t sys_rand32_get(void) { return 42; }
static inline int hwinfo_get_reset_cause(uint32_t *cause) { *cause = 0; return 0; }
static inline ssize_t bt_gatt_attr_read(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
    void *buffer, uint16_t length, uint16_t offset, const void *value, uint16_t size)
{
    (void)connection; (void)attribute;
    if (offset > size) { return BT_GATT_ERR(BT_ATT_ERR_INVALID_OFFSET); }
    uint16_t count = MIN(length, size - offset);
    memcpy(buffer, (const uint8_t *)value + offset, count);
    return count;
}
#endif
