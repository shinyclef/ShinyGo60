#include <zephyr/init.h>

static int shinygo60_initialize(void)
{
    return 0;
}

SYS_INIT(shinygo60_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);
