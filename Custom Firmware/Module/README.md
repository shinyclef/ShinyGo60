# ShinyGo60 ZMK module

This is the hand-maintained out-of-tree firmware module. Zephyr discovers it through `zephyr/module.yml`; the pinned MoErgo Nix build receives it through its
`extraModules` argument.

`CONFIG_SHINYGO60` enables the integration. Its internal `CONFIG_SHINYGO60_CENTRAL` option selects runtime code only for the left/central Go60 image; the
right/peripheral image compiles no ShinyGo60 source.

Step 5 added a deliberately harmless internal diagnostic. Corrected Step 6 firmware added the provisional fixed-size
`Hello -> HelloResult` exchange on the central image through USB CDC/ACM and an encrypted custom Bluetooth GATT service. Bluetooth writes are accepted only from an
encrypted host present in the firmware's bond table. Neither transport changes keymap behavior, and the right/peripheral image still compiles no ShinyGo60 source.

Step 8 advances the feature version to `0.3.0-step8`. Its clean build workspace supplies the layout ID and full SHA-256 of the exact keymap through generated
Kconfig values. Both values remain present in the central UF2 so the headless builder can reject a stale or mismatched compiler result before publication. Manual
firmware builds retain explicit non-production defaults.

The Step 6 packet, UUIDs, build evidence, and hardware validation checklist are recorded in
[`../BuildSupport/STEP6_DUAL_TRANSPORT.md`](../BuildSupport/STEP6_DUAL_TRANSPORT.md).
The matched keymap-to-firmware build contract is recorded in
[`../BuildSupport/STEP8_HEADLESS_PIPELINE.md`](../BuildSupport/STEP8_HEADLESS_PIPELINE.md).
