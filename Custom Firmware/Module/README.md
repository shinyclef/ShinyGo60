# ShinyGo60 ZMK module

This is the hand-maintained out-of-tree firmware module. Zephyr discovers it through `zephyr/module.yml`; the pinned MoErgo Nix build receives it through its
`extraModules` argument.

`CONFIG_SHINYGO60` enables the integration. Its internal `CONFIG_SHINYGO60_CENTRAL` option selects runtime code only for the left/central Go60 image; the
right/peripheral image compiles no ShinyGo60 source.

Step 5 added a deliberately harmless internal diagnostic. Corrected Step 6 firmware added the provisional fixed-size transport on the central image through USB
CDC/ACM and an encrypted custom Bluetooth GATT service. Bluetooth writes are accepted only from an encrypted host present in the firmware's bond table. Neither
transport changes keymap behavior, and the right/peripheral image still compiles no ShinyGo60 source.

Step 9 advances the feature version to `0.4.0-step9` and replaces the echo packet with the fixed 20-byte protocol-v1 frame and layout-bound, single-owner sessions.
Its clean build workspace supplies the layout ID and full SHA-256 of the exact keymap through generated Kconfig values. Both values remain present in the central
UF2 so the headless builder can reject a stale or mismatched compiler result before publication. The Step 9 firmware advertises no layer capabilities and cannot
change ZMK state; later steps fill in the locked message forms. Manual firmware builds retain explicit non-production defaults.

Step 10 advances the feature version to `0.5.0-step10` and enables only the read-only state-telemetry capability. The central observes ZMK's effective layer,
returns a session-bound snapshot after `GetState`, and sends revisioned change events over the owning USB or Bluetooth transport. Busy transports retain only the
newest complete layer state. Persistent and momentary command capabilities remain disabled, and no ShinyGo60 source calls a ZMK layer mutation API.

The Step 6 packet, UUIDs, build evidence, and hardware validation checklist are recorded in
[`../BuildSupport/STEP6_DUAL_TRANSPORT.md`](../BuildSupport/STEP6_DUAL_TRANSPORT.md).
The matched keymap-to-firmware build contract is recorded in
[`../BuildSupport/STEP8_HEADLESS_PIPELINE.md`](../BuildSupport/STEP8_HEADLESS_PIPELINE.md).
The protocol-v1 bytes, state rules, error behavior, and shared verification are recorded in
[`../BuildSupport/STEP9_PROTOCOL_V1.md`](../BuildSupport/STEP9_PROTOCOL_V1.md).
Step 10 telemetry design, build evidence, and the pending physical checklist are recorded in
[`../BuildSupport/STEP10_LAYER_TELEMETRY.md`](../BuildSupport/STEP10_LAYER_TELEMETRY.md).
