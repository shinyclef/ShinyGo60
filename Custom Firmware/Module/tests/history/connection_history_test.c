#include <assert.h>
#include <stdio.h>
#include "fake_zephyr.h"
/* Compile the production recorder itself; only Zephyr/driver boundaries are replaced. */
#include "../../zephyr/src/connection_diagnostics.c"

static struct bt_conn client;

static void reset_history(uint32_t sequence)
{
    critical.count = critical.next = 0;
    critical.sequence = sequence;
    routine.count = routine.next = 0;
    routine.sequence = 0;
    first_failure_count = 0;
    memset(downloads, 0, sizeof(downloads));
    test_uptime = 0;
    assert(connection_diagnostics_initialize() == 0);
}

static void fail(unsigned count)
{
    for (unsigned i = 0; i < count; i++) {
        test_uptime++;
        shinygo60_connection_record(SHINYGO60_DISCONNECTED, &client, 62, 12, 4, 400, 0);
    }
}

static void start(uint32_t boot, uint32_t after)
{
    uint8_t request[12] = {0};
    sys_put_le32(boot, request);
    sys_put_le32(after, request + 4);
    assert(shinygo60_connection_history_start(&client, request, sizeof(request), 0) == sizeof(request));
    assert(downloads[0].info[0] == 2);
    assert(downloads[0].info[2] == 224 && downloads[0].info[3] == 128);
}

static void expect_critical(uint32_t first, unsigned count)
{
    assert(downloads[0].count == count);
    for (unsigned i = 0; i < count; i++) {
        uint8_t record[20];
        assert(shinygo60_connection_history_read(&client, NULL, record, sizeof(record), 0) == sizeof(record));
        assert(sys_get_le32(record) == first + i);
        assert(record[8] == (0x80 | SHINYGO60_DISCONNECTED));
    }
    uint8_t record[20];
    assert(shinygo60_connection_history_read(&client, NULL, record, sizeof(record), 0) == 0);
}

int main(void)
{
    reset_history(0);
    fail(1000);
    start(0, 0);
    assert(sys_get_le32(downloads[0].info + 12) == 1000);
    expect_critical(1, 32);
    start(0, 0); /* A replay or different boot cursor cannot release protected failures. */
    expect_critical(1, 32);
    assert(first_failure_count == 32);
    start(boot_id, 16); /* An interrupted download acknowledges only its received records. */
    expect_critical(17, 16);
    assert(first_failure_count == 16);
    fail(1); /* A newer protected record must merge after the recent ring, not before it. */
    start(boot_id, 32);
    expect_critical(810, 192);
    start(boot_id, 1001);
    expect_critical(0, 0);
    assert(first_failure_count == 0);
    fail(1000);
    start(boot_id, 1001);
    expect_critical(1002, 32); /* The next storm has its own protected beginning. */

    reset_history(0);
    fail(224); /* Adjacent protected and recent ranges fit one snapshot without duplicates. */
    start(0, 0);
    expect_critical(1, 224);
    start(boot_id, 100);
    expect_critical(101, 124);

    reset_history(UINT32_MAX - 15U);
    fail(224);
    start(0, 0);
    expect_critical(UINT32_MAX - 14U, 224);
    start(boot_id, 16);
    expect_critical(17, 192);

    reset_history(0);
    fail(10);
    start(0, 0);
    fail(1000); /* Live writes cannot alter an in-progress frozen download. */
    expect_critical(1, 10);
    start(boot_id, 10);
    expect_critical(11, 22);

    reset_history(0);
    fail(1000);
    for (unsigned i = 0; i < 500; i++) {
        shinygo60_connection_record(SHINYGO60_PARAMETERS_UPDATED, &client, 0, 12, 4, 400, 0);
    }
    start(0, 0);
    assert(downloads[0].count == 32 + 128);
    assert(sys_get_le32(downloads[0].records[32]) == 373);
    assert(sys_get_le32(downloads[0].records[159]) == 500);
    assert((downloads[0].records[32][8] & 0x80) == 0);
    puts("Connection history: storm retention, cursor acknowledgement, interrupted/frozen downloads, ordering, wrap and routine capacity passed");
    return 0;
}
