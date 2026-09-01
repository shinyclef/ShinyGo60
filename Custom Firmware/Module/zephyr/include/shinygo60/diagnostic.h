#ifndef SHINYGO60_DIAGNOSTIC_H_
#define SHINYGO60_DIAGNOSTIC_H_

#include <stdint.h>

#define SHINYGO60_FEATURE_VERSION "0.2.1-step6"
#define SHINYGO60_TEST_LAYOUT_IDENTIFIER "00000000-0000-0000-0000-000000000005"

struct shinygo60_diagnostic {
    const char *feature_version;
    const char *layout_identifier;
    uint32_t identity_checksum;
};

const struct shinygo60_diagnostic *shinygo60_diagnostic_get(void);

#endif /* SHINYGO60_DIAGNOSTIC_H_ */
