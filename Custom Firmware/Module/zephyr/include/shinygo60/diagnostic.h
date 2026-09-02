#ifndef SHINYGO60_DIAGNOSTIC_H_
#define SHINYGO60_DIAGNOSTIC_H_

#include <stdint.h>

#define SHINYGO60_FEATURE_VERSION "0.3.0-step8"

struct shinygo60_diagnostic {
    const char *feature_version;
    const char *layout_identifier;
    const char *keymap_sha256;
    uint32_t identity_checksum;
};

const struct shinygo60_diagnostic *shinygo60_diagnostic_get(void);

#endif /* SHINYGO60_DIAGNOSTIC_H_ */
