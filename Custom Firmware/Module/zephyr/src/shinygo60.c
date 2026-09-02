#include <zephyr/init.h>
#include <zephyr/sys/util.h>

#include <shinygo60/diagnostic.h>

#define FNV1A_OFFSET_BASIS 2166136261U
#define FNV1A_PRIME 16777619U

BUILD_ASSERT(IS_ENABLED(CONFIG_BOARD_GO60_LH), "ShinyGo60 runtime code must only be linked into the Go60 central/left image");

static struct shinygo60_diagnostic diagnostic = {
    .feature_version = SHINYGO60_FEATURE_VERSION,
    .layout_identifier = CONFIG_SHINYGO60_LAYOUT_IDENTIFIER,
    .keymap_sha256 = CONFIG_SHINYGO60_KEYMAP_SHA256,
};

static uint32_t hash_text(uint32_t hash, const char *text)
{
    while (*text != '\0') {
        hash ^= (uint8_t)*text;
        hash *= FNV1A_PRIME;
        text++;
    }

    return hash;
}

static int shinygo60_initialize(void)
{
    uint32_t hash = hash_text(FNV1A_OFFSET_BASIS, diagnostic.feature_version);
    hash = hash_text(hash, diagnostic.layout_identifier);
    diagnostic.identity_checksum = hash_text(hash, diagnostic.keymap_sha256);

    return 0;
}

const struct shinygo60_diagnostic *shinygo60_diagnostic_get(void)
{
    return &diagnostic;
}

SYS_INIT(shinygo60_initialize, APPLICATION, CONFIG_APPLICATION_INIT_PRIORITY);
