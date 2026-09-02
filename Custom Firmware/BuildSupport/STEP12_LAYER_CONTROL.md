# Step 12 layer control

Status: implementation corrected; physical `&to` regression verification pending

Recorded: 2026-09-02 on Windows 11

## Goal

Add persistent and leased momentary layer control without erasing layer state created by the Go60. The companion and the keyboard are independent owners except
that a physical `&to` deliberately replaces a companion persistent selection. Normal keyboard behavior must continue when the companion is absent,
disconnected, restarted, or late to release a shortcut.

## Ownership model

The effective ZMK active set is the union of three sources:

- `K`: keyboard-owned state created by ZMK behaviors such as `&mo`, `&to`, `&tog`, and conditional layers;
- `P`: zero or one external persistent layer selected at runtime; and
- `M`: zero or more external momentary activations owned by the current protocol session and identified by their press-command IDs.

The firmware reconciles `K ∪ P ∪ layers(M)` into ZMK's real layer bitmask. The default layer remains implicitly active under ZMK's existing rule. Multiple
momentary activations may target the same layer; that layer remains active until its final owner is gone.

Pinned ZMK v25.11 stores only one bit per layer and does not track which caller set it. ShinyGo60 therefore wraps ZMK's `activate`, `deactivate`, `toggle`, and
`to` entry points. Calls from existing keyboard behaviors update `K`; ShinyGo60's own reconciliation bypasses those wrappers. This records a physical activation
even when `P` or `M` already keeps the same real bit active, which is necessary for release order to be safe.

The wrappers preserve ZMK's existing ownership granularity inside `K`. For example, two physical `&mo` keys targeting the same layer still have ZMK's native
single-bit behavior. Step 12 adds independent external ownership but does not redefine collisions between two existing keyboard behaviors.

## Interaction truth table

`A` and `B` are ordinary non-default layers. `C` is a conditional layer. “Active” describes the composed real ZMK set; the effective layer remains ZMK's
highest active layer.

| Keyboard action or state | External persistent `P` | External momentary `M` | Composed result and later behavior |
| --- | --- | --- | --- |
| Base only | none | none | Only the default layer is effective. |
| Hold `&mo A` | none | none | `A` is in `K` while held and is removed on physical release. |
| Toggle `&tog A` on | none | none | `A` is added to `K` until the next keyboard toggle/off action. |
| Press `&to A` | none | none | Keyboard-owned non-default bits are replaced by `A`, matching ordinary ZMK behavior. |
| Conditional prerequisites are active | none | none | ZMK adds `C` to `K`; removing a prerequisite lets ZMK remove `C`. |
| Base only | `A` | none | `A` stays active until another persistent command, a physical `&to`, or keyboard reboot replaces it. |
| Hold `&mo B` | `A` | none | Both are active. Physical release removes only `B`; `A` remains. |
| Press `&to B` | `A` | none | The physical `&to` clears external `A`, replaces `K`, and leaves keyboard-owned `B` active. |
| Toggle `&tog A` while external `A` is active | `A` | none | The keyboard-owned `A` bit toggles independently. Replacing `P` later reveals the toggled keyboard state. |
| Base only | `A` | one activation for `B` | `A` and `B` are active; releasing or expiring `B` reveals `A`. |
| Physical `&mo A` is held first | none | one activation for `A` | Releasing either owner leaves `A` active; releasing the final owner removes it. |
| External `A` is active first, then physical `&mo A` is held | none | one activation for `A` | The wrapped physical activation is recorded even though the real bit was already on. External release leaves `A` active until physical release. |
| Base only | none | two activations for `A` | The state reports two momentary activations. Releasing one keeps `A`; releasing the second removes it. |
| Conditional prerequisites include externally active layers | any | any | Conditional layers evaluate the composed real state, so external prerequisites can activate `C`; `C` remains keyboard/framework-owned. |
| A transparent binding is reached | any | any | Transparency uses ZMK's normal highest-to-lowest lookup over the composed state; ShinyGo60 does not duplicate binding resolution. |
| Owning session disconnects or is replaced | unchanged | any | Every activation owned by the old session is removed immediately; `K` and `P` remain. |
| A lease expires | unchanged | matching activation removed | Only that activation is removed. Other keyboard, persistent, conditional, or momentary owners remain. |
| Keyboard reboots | cleared | cleared | External state is runtime-only. No flash setting is written; startup returns to keymap-owned state. |

Selecting layer 0 is the persistent “go home” action. It replaces a previous persistent selection and is reported as persistent layer 0 even though ZMK already
treats its default layer as implicitly active. The fixed protocol deliberately has no separate clear-persistent command.

A physical `&to` is also a deliberate clear-persistent operation. This makes a keymap's existing `&to Home` key authoritative after a companion Go to layer
action, matching the behavior expected from two physical `&to` keys. External momentary activations remain independent and continue only while their leases are
owned and renewed.

## State publication

An external operation is one transaction. ZMK may internally raise several layer events while the composed set is reconciled, but ShinyGo60 publishes only the
final effective layer, persistent selection, and momentary activation count for that command. A physical ZMK change has source command ID zero. A successful set,
press, or release publishes its command ID. Lease expiry uses source zero.

The layer-state revision advances only when one of the four reported fields changes. A physical ownership change hidden by another owner does not advance it
because the protocol does not expose the complete owner set. A command result always carries the final complete state, so a missed or coalesced event remains
recoverable.

## Command rules

- A successful exact-layout session must select the matching control capability before its command is accepted.
- Command IDs strictly increase within a session. The press command ID is also its activation ID.
- Set and press require the current layer-state revision and a target present in the compiled keymap.
- Set to the current persistent target returns `NoChange`. A new press returns `Applied`, even if another owner already holds the same layer, because the reported
  momentary count changes.
- Renew extends an active lease from receipt time and returns `NoChange`. Renewing or releasing an activation that is already absent returns `AlreadyReleased`.
- Releasing an active activation returns `Applied`. Release is idempotent.
- An exact replay of the latest successful command returns `Duplicate` without another mutation. Reusing that ID with different bytes returns
  `DuplicateConflict`; an older ID returns `StaleCommand`.
- Rejected current-session commands reserve their command ID and replay the same error. This prevents a changed retry from reusing an ambiguous ID.
- If a higher-ID release arrives before its lower-ID press, the release records `AlreadyReleased`; the later press is stale and cannot activate a layer.
- At most eight external momentary activations may exist at once. A ninth press returns `Busy` without changing layer state.
- A lease is 1-50 units of 100 ms. Expiry is scheduled on firmware and does not depend on the Windows process.

## Windows command state

The C# state machine allocates increasing command IDs, knows an activation ID before the press acknowledgement, and serializes ordinary transport exchanges. It
forgets every held activation whenever a session ends or is replaced. A still-held Windows shortcut must be released and pressed again before another press is
created; reconnect never guesses that an old physical hold is still valid.

The state machine accepts only a response for its current session and pending command. Command results update the same manifest-backed layer tracker used for
events. Errors, timeouts, cancellation, transport loss, process termination, duplicate delivery, and reordered test responses cannot create a local activation
that survives into a new session.

## Implemented components

- `layer_control.c` owns the `K`, `P`, and `M` composition, wraps the four public ZMK layer mutations, lets physical `&to` replace `P`, bounds momentary ownership
  to eight activations, and expires leases on firmware work.
- `protocol.c` enables the persistent and momentary capabilities, serializes commands from USB and Bluetooth, enforces sessions/revisions/IDs, caches the latest
  request and response for exact retry, and publishes complete post-command state.
- USB command processing now leaves the UART interrupt and runs on the system work queue. USB connection-state changes, Bluetooth indication shutdown, and
  Bluetooth owner disconnects terminate the matching command session and remove its momentary activations.
- `LayerCommandStateMachine` queues one in-flight operation at a time, allocates press tokens before acknowledgement, sends the exact packet again on timeout, and
  forgets queued, pending, and held state at every session boundary.
- `ShinyGo60.TransportSpike` adds `control-usb` and `control-bluetooth` modes. Each performs persistent target/Home replacement, exact replay, momentary
  press/renew/release, two-owner release ordering, session replacement, short-lease expiry, and release-after-expiry checks against a manifest-selected layer.
  Interactive `ownership-usb` and `ownership-bluetooth` modes verify same-layer physical/external ownership in both release orders. `select-usb` and
  `select-bluetooth` leave an explicit runtime-persistent layer selected, while `control-switch` checks cleanup during USB/Bluetooth ownership handoff.
  `hold-usb` and `hold-bluetooth` preserve the current persistent selection while maintaining one leased activation for interruption and reboot tests.

The diagnostic intentionally leaves persistent layer 0 (`Home`) selected. This is behaviorally the base state, but the persistent indicator remains set until
another persistent selection or a keyboard reboot. If the persistent phase fails after selecting its target, the diagnostic opens a fresh session and attempts
an emergency Home restore. If transport loss prevents that restore, power-cycling clears all runtime-only external state.

## Offline verification

- The Windows Release solution builds with zero warnings and errors.
- Thirteen offline suites pass, including quick release before acknowledgement, exact retry, reordered and wrong-session responses, rejected presses, session
  replacement, transport loss, expiry-before-release, two independent momentary owners, and restoration of persistent state after their final release.
- The pinned ZMK v25.11 firmware builds with container networking disabled. The matching artifact is
  `Output/ShinyGo60-20260902-090655-3fd12c2c/ShinyGo60-3fd12c2c.uf2`, with SHA-256
  `50044fc332b4a46b200142324473a0d1c67ab3a107861ef5cafea12a7d1f25bc` and layout ID
  `sg60-v1-3fd12c2c4edf42e10f1a9e29e08557cb`.
- The physical-`&to` correction builds from the same keymap and layout identity with container networking disabled. Its matched artifact is
  `Output/ShinyGo60-20260902-155232-3fd12c2c/ShinyGo60-3fd12c2c.uf2`, with SHA-256
  `3f160ded753f2b2e0437a1d4ce5e72b13edff369182ce16920bbfc1c949b8088`. The build reused tagged image
  `sha256:f5fedc1e224a672db76f4b345583545a9c3a3b7053dd55b4d30162f19639c446` and created no image layers.

No Docker image rebuild is needed for the remaining Step 12 tests. Reuse the tagged `shinygo60-builder:v25.11` image; if its tag is lost, identify the existing
managed firmware-builder image by label and re-tag its Image ID instead of rebuilding it.

## Physical verification

- The final Step 12 UF2 was flashed to both halves. The user confirmed normal typing over USB and Bluetooth, each with and without TRRS, before control testing.
- `control-bluetooth 3` and `control-usb 3` both passed persistent target/Home replacement, exact replay, momentary press/renew/release, two simultaneous
  activations, session replacement, lease renewal, automatic expiry, and idempotent release after expiry. Each finished on persistent Home with zero momentary
  activations. In the two-owner case, the reported count rose to two, the first release retained Navigation, and the final release restored Home.
- USB same-layer ownership passed in both release orders with layer 4 (`Keypad`) and the physical Keypad/Delete thumb hold. The external-first case retained
  Keypad after external release until physical release. The physical-first case retained Keypad after physical release until external release. The final state
  was revision 96, effective Home, persistent Home, and zero momentary activations.
- Layer 3 (`Navigation`) is unsuitable for the external-first half of this test in the supplied keymap: the Navigation layer binds its own Navigation/Backspace
  thumb position to `&none`, so an externally active Navigation layer intentionally prevents that position from reaching the Home-layer hold behavior. The
  Keypad layer leaves its corresponding Keypad/Delete position transparent and therefore exercises the ownership collision correctly.
- A persistent Navigation selection survived fresh USB sessions and a USB-to-Bluetooth protocol handoff. While it remained selected, a physical Keypad/Delete
  hold changed the effective layer to Keypad; release revealed persistent Navigation again. Restoring persistent Home over Bluetooth passed.
- `control-switch 3` passed with both transports open: a Bluetooth session removed an active USB-owned hold, then a new USB session removed an active
  Bluetooth-owned hold. Both same-transport session-replacement checks also removed the prior hold. The sequence finished on Home with zero momentary owners.
- Terminating the USB hold process without a release allowed the five-second firmware lease to expire; a fresh snapshot reported Home at revision 158.
- Physically removing USB during a renewed Navigation hold terminated the client. After a seven-second disconnected interval and USB reconnection, a fresh
  snapshot reported Home at revision 160.
- Turning Windows Bluetooth off during a renewed Navigation hold made the next encrypted write fail as unreachable. After the radio was re-enabled and Bluetooth
  reconnected, a fresh snapshot reported Home at revision 162.
- The combined reboot test selected persistent Navigation over USB at revision 191, then held momentary Keypad over Bluetooth at revision 192. The resulting
  state was effective Keypad, persistent Navigation, and one momentary activation. USB was unplugged and both halves were switched off. After a full restart,
  the first USB snapshot began at revision 1 and reported effective Home, no persistent selection, and zero momentary activations. Both halves then typed
  normally over USB and TRRS.
- Windows sleep remains deferred because this test PC does not enter sleep reliably. The tested process, session, USB, Bluetooth-radio, transport-switch, lease,
  and full-keyboard-reboot paths cover the Step 12 state-safety contract without relying on that unavailable Windows test condition.

On 2026-09-03, Step 14 use exposed a precedence defect not covered by the original acceptance run: a companion persistent Navigation selection prevented the
physical `&to Home` binding on that layer from becoming effective. The wrapper now clears the external persistent selection as part of every physical `&to`
transaction and reconciles the prior external layer. A new matched firmware build and physical USB/Bluetooth regression test are required before closing this
correction.

Run the bounded diagnostic after flashing, without touching keyboard layer keys during its approximately two-second sequence:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\ShinyGo60-20260902-090655-3fd12c2c\layout-manifest.json' control-usb 3
```

Use `control-bluetooth 3` for Bluetooth. Layer 3 is `Navigation` in this build.

For the interactive same-layer test, use layer 4 and the physical Keypad/Delete thumb key because that position is transparent on the Keypad layer:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\ShinyGo60-20260902-090655-3fd12c2c\layout-manifest.json' ownership-usb 4
```

## Physical acceptance outline

- [ ] Confirm persistent selection and replacement over USB and Bluetooth, including physical `&to Home` clearing a companion selection.
- [x] Confirm momentary press, renewal, release, and automatic expiry over both transports.
- [x] Confirm same-layer physical/external release in both orders.
- [x] Confirm two simultaneous momentary activations and transparent behavior available in the current keymap; the exported keymap has no conditional layer.
- [x] Confirm USB removal, Bluetooth loss, session replacement, keyboard power cycle, and transport switching cannot leave a momentary layer active.
- [x] Confirm persistent state survives a transient session/transport loss but clears on keyboard reboot.
