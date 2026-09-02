#include <errno.h>

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/uart.h>
#include <zephyr/init.h>
#include <zephyr/kernel.h>
#include <zephyr/sys/ring_buffer.h>
#include <zephyr/sys/util.h>

#include <zmk/event_manager.h>
#include <zmk/events/usb_conn_state_changed.h>

#include <shinygo60/protocol.h>

#define USB_UART_NODE DT_CHOSEN(zmk_studio_rpc_uart)
#define USB_REQUEST_QUEUE_DEPTH 4U
#define USB_TX_BUFFER_SIZE (SHINYGO60_PACKET_SIZE * 4U)

BUILD_ASSERT(DT_NODE_HAS_STATUS(USB_UART_NODE, okay), "The ShinyGo60 USB CDC UART is unavailable");
BUILD_ASSERT(IS_ENABLED(CONFIG_USB_CDC_ACM), "The ShinyGo60 USB transport requires CDC ACM");
BUILD_ASSERT(IS_ENABLED(CONFIG_ZMK_SPLIT_WIRED_UART_MODE_ASYNC), "Go60 TRRS must use asynchronous UART mode");
BUILD_ASSERT(IS_ENABLED(CONFIG_UART_0_ASYNC), "Go60 TRRS requires asynchronous UART0 support");
BUILD_ASSERT(!IS_ENABLED(CONFIG_UART_0_INTERRUPT_DRIVEN), "USB CDC must not change the physical TRRS UART API");

static const struct device *const usb_uart = DEVICE_DT_GET(USB_UART_NODE);
static const uint8_t packet_magic[] = {
    SHINYGO60_PACKET_MAGIC_0,
    SHINYGO60_PACKET_MAGIC_1,
};

RING_BUF_DECLARE(usb_tx_buffer, USB_TX_BUFFER_SIZE);
static struct k_spinlock usb_tx_lock;
K_MSGQ_DEFINE(
    usb_request_queue, SHINYGO60_PACKET_SIZE, USB_REQUEST_QUEUE_DEPTH, sizeof(uint32_t));

static uint8_t usb_request[SHINYGO60_PACKET_SIZE];
static size_t usb_request_length;

static void process_requests_work_handler(struct k_work *work);
static K_WORK_DEFINE(process_requests_work, process_requests_work_handler);

static void reset_request(uint8_t byte)
{
    usb_request_length = 0U;
    if (byte == packet_magic[0]) {
        usb_request[0] = byte;
        usb_request_length = 1U;
    }
}

static void receive_byte(uint8_t byte)
{
    if (usb_request_length < sizeof(packet_magic) && byte != packet_magic[usb_request_length]) {
        reset_request(byte);
        return;
    }

    usb_request[usb_request_length++] = byte;
    if (usb_request_length < SHINYGO60_PACKET_SIZE) {
        return;
    }

    if (k_msgq_put(&usb_request_queue, usb_request, K_NO_WAIT) == 0) {
        (void)k_work_submit(&process_requests_work);
    }

    usb_request_length = 0U;
}

static void process_requests_work_handler(struct k_work *work)
{
    ARG_UNUSED(work);

    uint8_t request[SHINYGO60_PACKET_SIZE];
    while (k_msgq_get(&usb_request_queue, request, K_NO_WAIT) == 0) {
        uint8_t response[SHINYGO60_PACKET_SIZE];
        if (shinygo60_protocol_handle(
                SHINYGO60_TRANSPORT_USB, request, sizeof(request), response)) {
            (void)shinygo60_usb_send(response);
        }
    }
}

static void usb_uart_callback(const struct device *device, void *user_data)
{
    ARG_UNUSED(user_data);

    if (!uart_irq_update(device)) {
        return;
    }

    while (uart_irq_rx_ready(device)) {
        uint8_t received[8];
        int received_count = uart_fifo_read(device, received, sizeof(received));
        if (received_count <= 0) {
            break;
        }

        for (int index = 0; index < received_count; index++) {
            receive_byte(received[index]);
        }
    }

    while (uart_irq_tx_ready(device)) {
        k_spinlock_key_t key = k_spin_lock(&usb_tx_lock);
        if (ring_buf_is_empty(&usb_tx_buffer)) {
            uart_irq_tx_disable(device);
            k_spin_unlock(&usb_tx_lock, key);
            break;
        }

        uint8_t *pending;
        uint32_t pending_length = ring_buf_get_claim(&usb_tx_buffer, &pending, ring_buf_size_get(&usb_tx_buffer));
        int sent = uart_fifo_fill(device, pending, pending_length);
        ring_buf_get_finish(&usb_tx_buffer, sent > 0 ? (uint32_t)sent : 0U);
        k_spin_unlock(&usb_tx_lock, key);
        if (sent <= 0) {
            break;
        }
    }
}

bool shinygo60_usb_send(const uint8_t packet[SHINYGO60_PACKET_SIZE])
{
    k_spinlock_key_t key = k_spin_lock(&usb_tx_lock);
    bool queued = ring_buf_space_get(&usb_tx_buffer) >= SHINYGO60_PACKET_SIZE &&
                  ring_buf_put(&usb_tx_buffer, packet, SHINYGO60_PACKET_SIZE) ==
                      SHINYGO60_PACKET_SIZE;
    k_spin_unlock(&usb_tx_lock, key);

    if (queued) {
        uart_irq_tx_enable(usb_uart);
    }
    return queued;
}

static int usb_connection_changed_listener(const zmk_event_t *event)
{
    const struct zmk_usb_conn_state_changed *changed = as_zmk_usb_conn_state_changed(event);
    if (changed != NULL && changed->conn_state != ZMK_USB_CONN_HID) {
        shinygo60_protocol_transport_disconnected(SHINYGO60_TRANSPORT_USB);
    }

    return ZMK_EV_EVENT_BUBBLE;
}

ZMK_LISTENER(shinygo60_usb_connection, usb_connection_changed_listener);
ZMK_SUBSCRIPTION(shinygo60_usb_connection, zmk_usb_conn_state_changed);

static int shinygo60_usb_initialize(void)
{
    if (!device_is_ready(usb_uart)) {
        return -ENODEV;
    }

    int result = uart_irq_callback_user_data_set(usb_uart, usb_uart_callback, NULL);
    if (result < 0) {
        return result;
    }

    uart_irq_rx_enable(usb_uart);
    return 0;
}

SYS_INIT(shinygo60_usb_initialize, POST_KERNEL, CONFIG_KERNEL_INIT_PRIORITY_DEFAULT);
