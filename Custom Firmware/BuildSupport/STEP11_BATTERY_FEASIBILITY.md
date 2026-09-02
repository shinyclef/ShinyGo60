# Step 11 per-half battery feasibility

Status: complete; battery support retained for version one after Windows 11 software, firmware, wireless/TRRS split, stale-state, missing-half, reconnect, USB,
and active-heartbeat acceptance; Windows sleep/resume is explicitly deferred

Recorded: 2026-09-02 on Windows 11

## Decision gate

Battery reporting passed its feasibility gate and remains in version one. Physical testing proved that the left and right values are distinguishable and useful
while battery-powered over Bluetooth, correct across wireless and TRRS split modes, explicit when unavailable or stale, and inexpensive by design for normal
battery use. USB-powered charging accuracy is not required; a stable saturated reading in that mode is acceptable.

## Pinned-source findings

- Both Go60 halves use the `zmk,battery-nrf-vddh` sensor in the pinned MoErgo ZMK v25.11 source at
  `11454d23596afbdb06380a1125371b19ab65675c`.
- ZMK samples each local sensor every 60 seconds while active and stops its battery timer while idle or asleep.
- The left/central battery is available through `zmk_battery_state_changed` and `zmk_battery_state_of_charge()`.
- The right/peripheral battery reaches the central as `zmk_peripheral_battery_state_changed` with source zero. Wireless split uses the standard Bluetooth Battery
  Service; wired split serializes the normal battery event over TRRS.
- Pinned ZMK reports a wireless peripheral disconnect as a right-side battery event with level zero. A genuine zero-percent right battery is therefore
  indistinguishable from that disconnect in the upstream interface. The feasibility candidate treats right-side zero as unavailable; a functioning keyboard is
  expected to power down before that edge becomes useful.
- The sensor is a voltage-derived estimate rather than a fuel gauge. Charging accuracy and stability cannot be accepted from source inspection and must be tested
  on the hardware.

## Low-power freshness design

`battery_heartbeat.c` re-publishes only ZMK's cached percentage; it never samples the voltage sensor. The heartbeat runs once after activation and then once every
60 seconds while active. It stops when ZMK enters idle or sleep.

On the central, the heartbeat refreshes the local observation directly. On the peripheral, the same cached value follows the normal wired battery-event path. For
wireless split, the heartbeat also asks Zephyr's standard Battery Service to notify its existing subscriber even when the percentage has not changed. This is
needed because ZMK otherwise suppresses an unchanged value and the central cannot distinguish silence from a missing right half.

The central retains the last observation from each half. After 150 seconds without another observation, an available value becomes stale once. A later observation
makes it fresh again. No recurring stale work remains after that transition. Host-facing `BatteryChanged` packets are emitted only when a percentage or status
actually changes, so the one-minute freshness heartbeat is not forwarded to Windows when the visible state is unchanged.

The incremental implementation cost compared with Step 10 is 2,012 flash bytes and 264 RAM bytes on the left, plus 172 flash bytes and 48 RAM bytes on the right.
The recurring active-use cost is one cached-value work item per minute on each half and, on the right wireless link, one one-byte Battery Service notification per
minute. This bounded cost passed the feasibility gate; a longer discharge soak remains useful non-blocking observation.

## Protocol 1.1 addition

Battery support changes the manifest-bound wire contract, so the protocol moves from 1.0 (`0x10`) to 1.1 (`0x11`). The fixed frame remains 20 bytes and the same
bytes are used over USB CDC and encrypted Bluetooth GATT.

New message types are:

| Type | Value | Direction |
| --- | --- | --- |
| `GetBattery` | `0x06` | Host to central |
| `BatterySnapshot` | `0x07` | Central to host |
| `BatteryChanged` | `0x08` | Central to host |

`GetBattery` contains the session ID at payload bytes 0-3, a nonzero request ID at bytes 4-7, and zero-reserved bytes 8-15. Snapshot and event payloads contain the
session ID at bytes 0-3, battery revision at bytes 4-7, related ID at bytes 8-11, left and right percentages at bytes 12 and 13, flags at byte 14, and a zero-reserved
byte 15. A snapshot has the request ID as its nonzero related ID; an event has a zero related ID.

Battery flags are:

| Bit | Meaning |
| --- | --- |
| 0 | Left value available |
| 1 | Left value stale |
| 2 | Right value available |
| 3 | Right value stale |

Stale implies available and retains the last percentage. Unavailable has no percentage in C# and must encode level zero. Fresh means available without the stale
bit. Levels above 100, unknown flags, contradictory flags, wrong sessions, zero revisions, malformed correlation fields, and nonzero reserved bytes are rejected.

Battery revisions are separate from layer-state revisions. A successful exact-layout `Hello` must select both `StateTelemetry` and `BatteryTelemetry`; the client
then establishes independent state with correlated `GetState` and `GetBattery` snapshots before accepting unsolicited events. A revision gap is safe because every
battery event carries the complete two-half state.

## Windows behavior

`BatteryStateTracker` binds a successful session to the exact layout manifest, requires the negotiated battery capability, and rejects events before the initial
snapshot. It maintains separate left and right `Unavailable`, `Fresh`, or `Stale` readings and rejects wrong-session, stale, same-revision-conflicting, malformed,
and out-of-order state without replacing the last accepted value.

The diagnostic client now requests layer and battery telemetry together. Normal USB/Bluetooth runs perform `Hello`, `GetState`, and `GetBattery`; watch modes route
both `LayerChanged` and `BatteryChanged` packets without allowing an unsolicited event to consume a pending response.

## Software verification and build

| Check | Result |
| --- | --- |
| C# Release solution | Success; zero warnings and errors |
| Offline checks | 10/10 passed, including per-half battery convergence |
| Shared protocol byte vectors | 14 native C/C# vectors passed |
| Pinned firmware compile | Success with container networking disabled |
| Reconstructed image | `sha256:672e5a87f2eebbd85bcb4cd5c1f16d1b00badbe56bfbb34a4b76f2ba1618724b` |
| Protocol / layout | `1.1` / `sg60-v1-3fd12c2c4edf42e10f1a9e29e08557cb` |
| Combined UF2 | 949,248 bytes; SHA-256 `db965b1ae1951efaeb431fb5c56208f9b91cbbfff0ff55a0138e9c79764a4275` |
| Left flash / RAM | 284,020 / 69,120 bytes |
| Right flash / RAM | 190,288 / 37,196 bytes |

The sole flashable candidate is `Output/Step11/ShinyGo60-20260902-061754-3fd12c2c`. Earlier generated candidates were removed after source review found that an
unchanged right percentage needed an explicit standard Battery Service notification and that freshness transitions needed serialization against new observations.

## Physical acceptance checklist

Flash `ShinyGo60-3fd12c2c.uf2` from the matched set to both halves using the established safe process. Keep only one USB power source attached at a time during
charging checks, and never connect or disconnect TRRS while either half is powered.

- [x] Normal HID and right-half wireless input still work with the companion absent.
- [x] Bluetooth snapshots report distinct, plausible fresh left and right percentages.
- [x] The right value remains fresh beyond 150 seconds while wireless split is connected and its percentage is unchanged.
- [x] Powering off or losing the wireless right half reports it unavailable without corrupting the left value.
- [x] Reconnecting the wireless right half restores a fresh value without reflashing or rebonding.
- [x] Idle or sleep eventually marks silent cached values stale; renewed activity restores fresh values.
- [x] USB snapshots report the same per-half semantics.
- [x] With TRRS connected safely, both halves type and report distinct battery values while battery-powered over Bluetooth.
- [x] A missing or silent wired right half cannot remain falsely fresh.
- [x] USB-powered readings remain stable; observed 100% saturation is accepted outside the required Bluetooth accuracy scope.
- [x] Right-half reconnect and Bluetooth/USB transport switching converge from fresh snapshots.
- [ ] Windows sleep/resume converges from fresh snapshots. Explicitly deferred because the test PC does not sleep reliably; Step 10 already passed the equivalent
  layer-telemetry test.
- [x] The one-minute active heartbeat has no observable typing, split, or reconnect regression; its bounded cost is one cached-value task per active minute and one
  one-byte right-half notification per active wireless minute, with no additional sensor sample.

Run the initial wireless Bluetooth check with:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\Step11\ShinyGo60-20260902-061754-3fd12c2c\layout-manifest.json' bluetooth 5
```

Use `watch-bluetooth 240`, `usb 5`, or `watch-usb 240` in place of `bluetooth 5` for freshness, stale-state, and USB checks.

### Wireless observations

On 2026-09-02, normal Bluetooth typing and wireless right-half input passed after both halves were flashed. Five fresh diagnostic sessions consistently reported
left 98% and right 90% at battery revision 5. During a 190-second active watch, the right value remained fresh and made plausible 90-91% updates; a final fresh
snapshot reported left 98% and right 90% at revision 9.

With the right half switched off, revision 12 reported left 97% fresh and right unavailable. Switching it back on produced revision 13 with left 97% and right
91%, both fresh, without reflashing or rebonding. During a subsequent untouched idle watch, revision 15 marked only the right value stale, then revision 16 marked
both cached values stale while retaining 98% and 91%. One key press on each half restored the right at revision 17 and the left at revision 18; both ended fresh at
left 98% and right 90%.

### Initial USB and TRRS observations

With USB attached to the left half and TRRS connected, both halves typed normally and the USB protocol continued receiving layer changes. Five fresh USB
sessions consistently reported battery revision 21 with left 100% and right 100%; response time was 1.36-7.81 ms. A subsequent 120-second USB watch remained
stable at 100%/100% while normal layer events continued.

This passes USB transport and per-half status semantics, but not the distinct-value or charging-accuracy gates. Immediately before external power was connected,
wireless readings were left 98% and right 90-91%. The simultaneous 100% values are retained as an unresolved physical finding because these voltage-derived
sensors may saturate while charging. The user confirmed that accurate reporting is required only while battery-powered over Bluetooth, so this behavior is
accepted and does not block the feature.

After USB was removed safely while TRRS remained seated, both halves typed over Bluetooth through the wired split. Five early snapshots reported left 99% and
right unavailable; the right-side observation then arrived and the 90-second watch began with left 95% and right 90%, both fresh. It subsequently made plausible
updates through left 99% and right 90-92%.

With TRRS still seated, switching off only the right half left its cached 92% available briefly, then battery revision 30 marked only that value stale while the
left remained fresh at 99%. Switching the right half back on produced revision 31 with a fresh 91% reading. This proves a silent wired half cannot remain falsely
fresh and can recover without reflashing, rebonding, or changing the host connection.

A final battery-powered Bluetooth/TRRS session reported battery revision 37 with left 99% and right 91%, both fresh. Windows sleep/resume was explicitly deferred
because the test PC could not be made to sleep reliably. This does not block Step 11: the same transport/session recovery path passed sleep/resume during Step 10,
and Step 11 separately passed reconnect, stale-to-fresh recovery, and USB/Bluetooth switching. A longer discharge soak may still be useful as non-blocking runtime
observation, but the heartbeat adds no voltage samples and its recurring wireless work is limited to one byte per active minute.
