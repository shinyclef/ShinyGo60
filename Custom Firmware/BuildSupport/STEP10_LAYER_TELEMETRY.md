# Step 10 effective-layer telemetry

Status: complete; software, firmware build, and physical USB, Bluetooth, split, layer, reconnect, switching, and sleep/resume checks passed

Recorded: 2026-09-02 on Windows 11

## Scope

Step 10 enables only protocol-v1 state telemetry. The central/left firmware observes ZMK's effective layer and reports it without polling. Persistent-layer and
momentary-layer command capabilities remain disabled, and the module contains no call that activates, deactivates, toggles, or selects a ZMK layer.

The client follows the Step 9 session sequence: a successful exact-layout `Hello` negotiates the state-telemetry capability, then the client sends `GetState` and
waits for the correlated `StateSnapshot`. Unsolicited `LayerChanged` packets begin only after that snapshot. This preserves the locked 20-byte protocol instead of
adding an uncorrelated packet immediately after `Hello`.

## Firmware behavior

- `layer_telemetry.c` subscribes to `zmk_layer_state_changed` on the central and reads `zmk_keymap_highest_layer_active()`.
- Revision starts at one and advances only when the effective layer ID changes. Changes to lower active layers that do not change the effective layer are silent.
- Snapshot and event state use `255` for no persistent layer, zero momentary activations, and no state indicators because Step 12 control is still absent.
- One bounded pending event is retained. If a transport is busy, newer layer state replaces older pending state and the sender retries; a revision gap tells C# that
  an intermediate state was coalesced.
- USB uses a four-packet transmit ring protected for the response and event producers. Bluetooth retains one indication in flight and retries the latest event
  after the response indication completes.
- A Bluetooth disconnect invalidates its owning protocol session. A new `Hello` over either transport replaces the old session, and packets retain their session
  ID so delayed data cannot update the new C# state.
- The right/peripheral image still compiles no ShinyGo60 runtime source. Right-half keys reach the central through the normal BLE split or TRRS path and therefore
  produce the same central ZMK layer events.

## Windows behavior

`LayerStateTracker` validates the manifest schema and protocol, requires an exact firmware layout fingerprint and negotiated telemetry capability, and refuses
events until a snapshot establishes the session. It resolves numeric IDs to the manifest's `LayerDefinition` names and rejects wrong-session, stale,
same-revision-conflicting, malformed, and out-of-range state without replacing the last accepted value. A complete event after a revision gap is accepted because
it contains the full current state.

`IKeyboardTransport` now exposes unsolicited packets. The USB client has one background fixed-frame reader so responses and events can share the CDC stream. The
Bluetooth callback separates `LayerChanged` indications from the outstanding request response. The diagnostic client performs `Hello` plus `GetState` for normal
transport checks and adds `watch-usb` and `watch-bluetooth` modes that print live manifest-resolved names.

## Software verification and build

| Check | Result |
| --- | --- |
| C# Release solution | Success; zero warnings and errors |
| Offline checks | 9/9 passed, including effective-layer convergence |
| Shared protocol byte vectors | Existing 11 C/C# vectors unchanged |
| Pinned firmware compile | Success with container networking disabled |
| Reconstructed image | `sha256:dc4e878897c99fd48172dbb5ff32ee85f38649214d92e27a328aae7b8cbeac9a` |
| Protocol / layout | `1.0` / `sg60-v1-11804322b898ead8b189330754427a65` |
| Combined UF2 | 944,640 bytes; SHA-256 `0e70b07fc32a2f05fe3e8a75d8bc11e5dbbd2e421c2ed3e7bcdf837e313eb4a6` |
| Left flash / RAM | 282,008 / 68,856 bytes |
| Right flash / RAM | 190,116 / 37,148 bytes; unchanged from Step 9 |

The matched artifact set is `Output/Step10/ShinyGo60-20260902-043052-11804322`. The isolated Buildx construction helper and cache were removed after the build; the
verified reusable image remains installed. The earlier missing-image and compile-failure attempts retained only diagnostic logs under `Output/Step10/Failures` and
published no misleading UF2.

## Physical acceptance checklist

Flash `ShinyGo60-11804322.uf2` from the matched set to both halves using the established safe process, then verify normal typing and the right half before opening a
protocol client.

- [x] Normal HID behavior works with the companion absent.
- [x] The right half works over TRRS.
- [x] USB `Hello` plus `GetState` resolves the initial layer name from the matching manifest.
- [x] USB watches report `&mo`, `&to`, `&tog`, and return to the underlying layer.
- [x] Conditional-layer coverage is not applicable because the exported keymap defines no ZMK conditional layer.
- [x] A transparent binding produces its normal key behavior without a false layer change.
- [x] A layer change initiated on the right half is reported.
- [x] A fresh snapshot converges after disconnect/reconnect while a non-base layer is active.
- [x] With USB disconnected, Bluetooth completes the same snapshot and layer-event checks.
- [x] USB-to-Bluetooth-to-USB switching creates fresh sessions and converges each time.
- [x] Normal right-half operation still works over the keyboard's wireless split connection.
- [x] A fresh session converges after Windows sleep/resume.

The first physical USB run passed on 2026-09-02. Five fresh sessions resolved revision 7 as layer 0 (`Home`); round-trip latency was 0.92-7.16 ms with a 2.20 ms
mean. A subsequent live watch reported Navigation, Keypad, Gaming, NoHRM, and returns to Home through revisions 8-19. Revision 17 was coalesced while the keyboard
changed state quickly, and the C# tracker accepted the complete revision-18 state and reported convergence instead of retaining stale state. The detailed input
source checks above remain open until the operator confirms which bindings and halves produced those events.

The first physical Bluetooth run also passed with USB disconnected. Five fresh sessions resolved revision 27 as `Home`; round-trip latency was 59.52-232.71 ms with
a 108.48 ms mean. The subsequent live watch started at revision 29 and received every revision from 30 through 45, resolving Navigation, LeftIndex, LeftMiddy,
Shortcuts, Keypad, Mouse, and each return to Home. No Bluetooth event revision was missed.

For the active-layer reconnect check, a Bluetooth watch observed the transition from revision 49 `Home` to revision 50 `Keypad` and then closed. Two newly opened
Bluetooth sessions both obtained revision 50 `Keypad` from their initial snapshots, proving convergence without relying on a post-connect layer event.

The active-layer transport sequence then passed Bluetooth to USB, USB to Bluetooth, and Bluetooth to USB. Each transport opened two new sessions and every initial
snapshot retained revision 50 `Keypad`; USB snapshots took 1.02-6.52 ms and Bluetooth snapshots took 52.25-522.06 ms. A final USB watch began from that same Keypad
snapshot and received revision 51 `Home` when the operator returned to the base layer.

With TRRS fully removed, the user confirmed normal right-half input over the Go60 wireless split. While a USB watcher was active, holding right-half `H` activated
revision 64 `RightIndex`; releasing it produced revision 65 `Home`. Tapping the left-half `B` transparent position while held produced no additional layer event.
The operator confirmed that `B` typed normally. Earlier watches also observed the keymap's momentary LeftIndex/LeftMiddy paths, persistent Keypad path, Gaming toggle,
and returns to Home. The input keymap contains no `conditional_layers` node, so there is no conditional-layer behavior to exercise in this build.

After Windows sleep/resume, normal keyboard operation returned and two fresh USB sessions both obtained revision 103 `Home`; snapshots took 0.95-6.60 ms. With USB
then disconnected, two fresh Bluetooth sessions both obtained revision 107 `Home`; snapshots took 57.59 ms and 517.21 ms. The occasional roughly 500 ms Bluetooth
exchange is consistent with the earlier Step 6 observations and remains a soak-test measurement rather than a correctness failure. This completes Step 10.

Run a five-snapshot parity check with:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\Step10\ShinyGo60-20260902-043052-11804322\layout-manifest.json' both 5
```

Run a 60-second live watch with `watch-usb 60` or `watch-bluetooth 60` in place of `both 5`.
