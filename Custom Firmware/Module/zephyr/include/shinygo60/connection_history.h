#ifndef SHINYGO60_CONNECTION_HISTORY_H_
#define SHINYGO60_CONNECTION_HISTORY_H_
#include <zephyr/bluetooth/gatt.h>
enum shinygo60_connection_event {
    SHINYGO60_CONNECTED = 1,
    SHINYGO60_DISCONNECTED,
    SHINYGO60_SECURITY_CHANGED,
    SHINYGO60_PARAMETERS_UPDATED,
    SHINYGO60_PARAMETERS_REQUESTED,
    SHINYGO60_LEASE_EXPIRED,
    SHINYGO60_SUBSCRIPTION_CHANGED,
    SHINYGO60_RESPONSE_QUEUE_FAILED,
    SHINYGO60_INDICATION_FAILED,
    SHINYGO60_INDICATION_SUBMIT_FAILED,
};
#if IS_ENABLED(CONFIG_SHINYGO60_CONNECTION_DIAGNOSTICS)
void shinygo60_connection_record(enum shinygo60_connection_event event, struct bt_conn *connection,
                                int32_t result, uint16_t a, uint16_t b, uint16_t c, uint16_t d);
ssize_t shinygo60_connection_history_read(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                        void *buffer, uint16_t length, uint16_t offset);
ssize_t shinygo60_connection_history_start(struct bt_conn *connection, const void *buffer, uint16_t length, uint16_t offset);
ssize_t shinygo60_connection_history_info(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                        void *buffer, uint16_t length, uint16_t offset);
ssize_t shinygo60_connection_history_context(struct bt_conn *connection, const struct bt_gatt_attr *attribute,
                                           void *buffer, uint16_t length, uint16_t offset);
#else
#define shinygo60_connection_record(...) ((void)0)
#endif
#endif
