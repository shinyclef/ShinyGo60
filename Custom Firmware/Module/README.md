# ShinyGo60 ZMK module

This is the hand-maintained out-of-tree firmware module. Zephyr discovers it through `zephyr/module.yml`; the pinned MoErgo Nix build receives it through its
`extraModules` argument.

`CONFIG_SHINYGO60` enables the integration. Its internal `CONFIG_SHINYGO60_CENTRAL` option selects host-protocol and diagnostic runtime code only for the
left/central Go60 image. Step 11 also compiles a small cached-battery heartbeat on both halves; the right still has no host-facing protocol transport.

Step 5 added a deliberately harmless internal diagnostic. Corrected Step 6 firmware added the provisional fixed-size transport on the central image through USB
CDC/ACM and an encrypted custom Bluetooth GATT service. Bluetooth writes are accepted only from an encrypted host present in the firmware's bond table. Neither
transport changes keymap behavior. At Step 6, the right/peripheral image compiled no ShinyGo60 source.

Step 9 advances the feature version to `0.4.0-step9` and replaces the echo packet with the fixed 20-byte protocol-v1 frame and layout-bound, single-owner sessions.
Its clean build workspace supplies the layout ID and full SHA-256 of the exact keymap through generated Kconfig values. Both values remain present in the central
UF2 so the headless builder can reject a stale or mismatched compiler result before publication. The Step 9 firmware advertises no layer capabilities and cannot
change ZMK state; later steps fill in the locked message forms. Manual firmware builds retain explicit non-production defaults.

Step 10 advances the feature version to `0.5.0-step10` and enables only the read-only state-telemetry capability. The central observes ZMK's effective layer,
returns a session-bound snapshot after `GetState`, and sends revisioned change events over the owning USB or Bluetooth transport. Busy transports retain only the
newest complete layer state. Persistent and momentary command capabilities remain disabled, and no ShinyGo60 source calls a ZMK layer mutation API.

Step 11 advances the feature version to `0.6.0-step11` and protocol 1.1. It adds revisioned per-half battery snapshots and events with fresh, stale, and unavailable
states. Both halves re-publish ZMK's cached value once per active minute without an extra sensor sample; the peripheral also refreshes the standard Battery Service
notification used by wireless split. The required physical feasibility checks passed, so battery support is retained for version one; Windows sleep/resume is a
documented deferred check.

The adaptive-Bluetooth build advances the feature version to `0.8.1-adaptive-ble` and protocol 1.2. Its central requests peripheral latency 4 while Windows is
active and restores the existing power-saving latency 30 when Windows is locked or idle. It changes parameters on the existing bonded host connection rather
than opening another Bluetooth connection. Interactive mode has a 90-second firmware lease renewed by normal companion traffic, so closing or losing the
companion returns the connection to power saving. Connection negotiation is deliberately not tied to momentary-layer presses or releases.
The Bluetooth indication path also reserves one bounded response slot so a command arriving during a layer/battery indication is queued rather than rejected
with an ATT error.

The Step 6 packet, UUIDs, build evidence, and hardware validation checklist are recorded in
[`../BuildSupport/STEP6_DUAL_TRANSPORT.md`](../BuildSupport/STEP6_DUAL_TRANSPORT.md).
The matched keymap-to-firmware build contract is recorded in
[`../BuildSupport/STEP8_HEADLESS_PIPELINE.md`](../BuildSupport/STEP8_HEADLESS_PIPELINE.md).
The protocol-v1 bytes, state rules, error behavior, and shared verification are recorded in
[`../BuildSupport/STEP9_PROTOCOL_V1.md`](../BuildSupport/STEP9_PROTOCOL_V1.md).
Step 10 telemetry design, build evidence, and the pending physical checklist are recorded in
[`../BuildSupport/STEP10_LAYER_TELEMETRY.md`](../BuildSupport/STEP10_LAYER_TELEMETRY.md).
Step 11 battery design, candidate evidence, and physical feasibility checklist are recorded in
[`../BuildSupport/STEP11_BATTERY_FEASIBILITY.md`](../BuildSupport/STEP11_BATTERY_FEASIBILITY.md).
Adaptive connection policy, protocol changes, build evidence, and the physical test checklist are recorded in
[`../BuildSupport/ADAPTIVE_BLUETOOTH_LATENCY.md`](../BuildSupport/ADAPTIVE_BLUETOOTH_LATENCY.md).
