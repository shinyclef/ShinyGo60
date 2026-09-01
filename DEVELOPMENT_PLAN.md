# ShinyGo60 Development Plan

Status: Step 5 complete; the detailed Step 2 checklist remains; Step 6 is next

Last updated: 2026-09-01

This is the ordered execution plan for ShinyGo60. It turns the architecture in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) into development steps with explicit dependencies, deliverables, and completion gates.

The order is intentional. Hardware recovery, a practical build footprint, and full Bluetooth communication must be proven before substantial work is spent on
the polished builder, companion, or widget.

## Version-one outcome

Version one is complete when a non-developer on Windows 11 can:

1. Export a Go60 `.keymap` from the MoErgo Layout Editor.
2. Put it in the builder's `Input` folder and double-click the builder.
3. Receive one matched output set containing a UF2, layout manifest, and readable build log.
4. Flash the same UF2 to both Go60 halves using the documented manual process.
5. Configure mouse-generated shortcuts for persistent and momentary layers.
6. Use those shortcuts over either USB or the existing Go60 Bluetooth connection.
7. See the effective layer in a small Windows taskbar-adjacent widget.
8. Continue using the Go60 normally when the companion is not running.
9. Recover safely from a failed firmware experiment or mismatched layout.

Battery values are included only if reliable, distinguishable left- and right-half readings pass the feasibility gate. Otherwise battery support is absent from the
firmware protocol, companion, widget, and configuration.

## Blocking gates

| Gate | Must be proven | Blocks | Status |
| --- | --- | --- | --- |
| G0: Recovery readiness | The user can flash and recover both halves and retains a known-good UF2 | All custom firmware experiments | Accepted from existing user experience |
| G1: Baseline build | An unchanged exported keymap builds and runs correctly | Custom firmware integration | Hardware smoke test passed; detailed checklist pending |
| G2: Storage | The selected local build path meets an accepted measured footprint | One-click builder commitment | Passed: 4.46 GB persistent image; prebuilt pull selected |
| G3: Dual transport | The same message round-trips over USB and encrypted BLE GATT | Layer features and production UI | Pending |
| G4: State safety | Telemetry converges and momentary layers cannot become stuck | Daily-use beta | Pending |
| G5: Clean-machine acceptance | A non-developer completes the full workflow | Version 1.0 | Pending |

If a blocking gate fails, work stops at that gate while the architecture is corrected. The plan must not conceal a failed gate behind later UI work.

## Step 0: Lock the version-one contract (complete)

Goal: make all project-facing plans describe the same product before implementation starts.

Work:

- [x] Make `.keymap` the only version-one firmware input.
- [x] Make USB and Bluetooth feature parity mandatory rather than treating Bluetooth as a later enhancement.
- [x] Confirm C# for Windows tooling and native ZMK/Zephyr code for firmware.
- [x] Confirm one UF2 is flashed to both halves.
- [x] Keep flashing manual for the first release.
- [x] Keep Logitech integration shortcut-based; do not depend on a Logitech API.
- [x] Keep battery support behind a pass-or-remove feasibility gate.
- [x] Record Windows 11 as the supported version-one platform.
- [x] Align README.md with the decisions already captured in IMPLEMENTATION_PLAN.md.

Deliverables:

- Consistent requirements in README.md, IMPLEMENTATION_PLAN.md, and this plan.
- A short version-one acceptance checklist linked from the release work.

Done when:

- No project document lists JSON as a version-one input.
- No project document describes Bluetooth as optional or post-version-one.
- Every version-one requirement has a measurable acceptance condition.

Depends on: nothing.

## Step 1: Establish safe hardware recovery (skipped by user)

Disposition: skipped on 2026-09-01 at the user's direction. The user already knows how to use, flash, and recover the Go60, so repeating those introductory exercises would not reduce project risk enough to justify blocking development.

Existing prerequisites:

- [x] The active complex `.keymap` is present in `Key Configuration`.
- [x] A known-good v25.11 reference UF2 is present in `Key Configuration`.
- [x] The user has existing experience flashing both halves and recovering the keyboard.

The later flashing, upgrade, and rollback validation in Step 17 remains required for the released ShinyGo60 workflow. Skipping this preparation step does not remove release-level safety verification.

Depends on: Step 0.

## Step 2: Reproduce the unchanged official build

Goal: prove the supported Go60 build path before adding ShinyGo60 code.

Work:

- [x] Select and pin an exact MoErgo ZMK revision.
- [x] Pin the build image or environment by immutable digest or equivalent revision.
- [x] Build the representative keymap without custom firmware changes.
- [x] Record the exact source revisions, build command, configuration, and output hash.
- [x] Record cold-build and warm-build durations.
- [x] Flash the output to both halves.
- [ ] Verify ordinary typing, layers, macros, combos, hold-taps, touchpads, USB host operation, and Bluetooth host operation.
- [ ] Verify both BLE inter-half operation and TRRS inter-half operation.
- [ ] Restore the rollback UF2 once more after the experiment.

Current evidence is recorded in [Custom Firmware/BuildSupport/BASELINE_V25_11.md](Custom%20Firmware/BuildSupport/BASELINE_V25_11.md). The cold and warm builds produced the same structurally valid combined UF2, and the user reported that it works after flashing on 2026-09-01. Detailed transport, inter-half, behavior-regression, and rollback checks remain open.

Deliverables:

- Reproducible baseline build instructions.
- Baseline UF2, hash, and build log.
- A recorded behavior checklist for regression comparison.

Done when:

- A locally built, unchanged layout behaves like the known-good firmware on both halves.
- Repeating the pinned build produces the expected output without modifying generated files by hand.

Depends on: G0 acceptance and the recorded Step 1 disposition.

## Step 3: Select a practical build environment (complete)

Goal: keep local firmware compilation small enough for routine use.

Work:

- [x] Measure the official MoErgo Docker path rather than relying on estimates.
- [x] Record download size, peak cold-build disk use, post-build persistent use, warm-build use, and cache contents.
- [x] Create a single-revision, Go60-only variant that does not preload multiple firmware revisions.
- [x] Verify the ARM-target ZMK toolchain can build the pinned MoErgo fork correctly.
- [x] Compare the output with the hardware-tested baseline byte-for-byte.
- [x] Test a warm build and an offline build with a complete existing image.
- [x] Label controllable ShinyGo60 resources and isolate Buildx-owned cache resources by an exact project name.
- [x] Implement only scoped cleanup; never use global Docker pruning.

Accepted footprint:

- 4.46 GB of persistent storage for the installed ShinyGo60 build image.
- Approximately 1.04 MB for a normal UF2 and build log, plus a small manifest.
- Approximately 10 GB free for maintainers who construct the image locally, followed by scoped cache cleanup.
- Ordinary users receive a prebuilt image; the release gate will measure the published-image pull on a clean machine.

The local image construction peaked at 9,143,857,152 bytes of observed drive growth while its 4.496 GB BuildKit cache and completed image coexisted, so it did not
meet the provisional 8 GB cold-space target. The chosen user workflow pulls a prebuilt, digest-pinned image instead. The retained image is 4.46 GB unpacked and
949,287,144 bytes as Docker content. The final offline firmware build took 14.857 seconds and produced the exact 927,232-byte UF2 already hardware-tested in Step 2.

Measurements, pins, commands, and cleanup evidence are recorded in
[Custom Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md](Custom%20Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md).

Deliverables:

- [x] Selected and pinned local build backend.
- [x] Measured storage and timing report.
- [x] Scoped cleanup procedure.

Done when:

- [x] The selected environment produces the same bytes as the flash-tested baseline UF2.
- [x] Its measured footprint is accepted and recorded.
- [x] Cleanup targets only exact project resources and cannot prune unrelated Docker resources.

Depends on: Step 2.

## Step 4: Scaffold the development workspaces (complete)

Goal: create separable components that can be tested independently.

Work:

- [x] Create the C# solution and shared build settings.
- [x] Create `ShinyGo60.Protocol` for messages, manifests, validation, and transport contracts.
- [x] Create `ShinyGo60.Builder.Core` for the headless build pipeline.
- [x] Create `ShinyGo60.Builder` for the eventual double-click UI.
- [x] Create `ShinyGo60.Companion.Core` for sessions, state, shortcuts, and reconnection.
- [x] Create `ShinyGo60.Companion` for the WPF configuration and widget process.
- [x] Create test infrastructure for parser fixtures, protocol vectors, fake transports, and process orchestration.
- [x] Create the out-of-tree ShinyGo60 ZMK feature module.
- [x] Keep generated firmware workspaces and outputs separate from hand-maintained source.
- [x] Add structured diagnostic logging without placing secrets or key contents in ordinary logs.

Deliverables:

- [x] Building C# solution with empty or minimal components.
- [x] Firmware module skeleton accepted by the pinned Go60 build.
- [x] Test fixture directories and disposable generated-workspace convention.

Done when:

- [x] All empty components build in their intended environments.
- [x] The baseline firmware still builds with the no-op module included.
- [x] Generated files can be removed without losing hand-maintained work.

The Release solution build completed with zero warnings and all five offline scaffold checks passed. The pinned, network-disabled firmware build discovered the
module through `ZMK_EXTRA_MODULES` and produced the exact hardware-tested baseline hash. Evidence and commands are recorded in
[Custom Firmware/BuildSupport/STEP4_SCAFFOLD.md](Custom%20Firmware/BuildSupport/STEP4_SCAFFOLD.md).

Depends on: Step 3.

## Step 5: Integrate the smallest firmware feature (complete)

Goal: prove custom code can be added without disturbing normal keyboard behavior.

Work:

- [x] Compile a central-only ShinyGo60 feature from the out-of-tree module.
- [x] Embed a ShinyGo60 feature version and fixed test layout identifier.
- [x] Expose a harmless diagnostic value internally.
- [x] Confirm that the right/peripheral side does not attempt to serve host protocol traffic.
- [x] Flash both halves and confirm normal behavior; the user reported that the build works after a brief self-resolving startup anomaly.
- [x] Record flash and RAM growth before adding transport code.

Deliverables:

- [x] First customized UF2 containing the no-op ShinyGo60 module.
- [x] Firmware size and memory baseline.

Done when:

- [x] Normal typing and the checked layout behavior remain unchanged.
- [x] The module is built from separate source rather than inserted into the exported keymap.

Implementation, binary-isolation, size, flash, and hardware evidence are recorded in
[Custom Firmware/BuildSupport/STEP5_MINIMAL_FEATURE.md](Custom%20Firmware/BuildSupport/STEP5_MINIMAL_FEATURE.md). The user's overall working-build report completes
the Step 5 hardware smoke gate without claiming the item-by-item transport and rollback verification that remains open in Step 2.

Depends on: Step 4.

## Step 6: Complete the mandatory dual-transport spike

Goal: prove the central technical requirement before developing layer features or polished UI.

Use only a provisional, minimal message for this spike:

```text
Hello -> HelloResult
```

Work:

- [ ] Add a USB CDC/ACM endpoint on the central/left half.
- [ ] Add a custom encrypted BLE GATT service on the central/left half.
- [ ] Require a bonded/encrypted Bluetooth connection for writes and indications.
- [ ] Build a small C# diagnostic client using the common transport contract.
- [ ] Discover the correct USB endpoint without selecting unrelated serial devices.
- [ ] Discover the GATT service on the already paired Go60.
- [ ] Send identical application bytes over both transports and receive an acknowledgement.
- [ ] Test a fresh pairing and an existing pairing after a firmware update.
- [ ] Test Windows GATT caching, sleep/resume, radio disable/enable, range loss, keyboard power cycle, and all relevant Go60 host profiles.
- [ ] Test USB-to-Bluetooth and Bluetooth-to-USB switching.
- [ ] Measure round-trip latency, idle CPU use, and observable wireless power impact.
- [ ] Verify normal keyboard operation with the diagnostic client absent.

Deliverables:

- Minimal firmware transport implementation.
- Minimal C# diagnostic client.
- USB/BLE feasibility report with measured behavior and known limitations.

Done when:

- The same diagnostic client reliably round-trips the same message over USB and Bluetooth.
- Unauthorized or unpaired BLE access cannot issue a valid command.
- Reconnection produces a new working session without restarting Windows.

This is blocking gate G3. Do not begin production layer features or widget work if Bluetooth fails.

Depends on: Step 5.

## Step 7: Implement keymap inspection and the layout manifest

Goal: create a trustworthy link between the exported layout, firmware, and companion.

Work:

- [ ] Validate that the input resembles a complete Go60 Layout Editor `.keymap` export.
- [ ] Extract the ordered numeric layer IDs and generated layer names.
- [ ] Hash the exact keymap and other identity-defining build inputs.
- [ ] Generate a versioned layout identifier.
- [ ] Generate `layout-manifest.json` with schema version, protocol version, layer mapping, source revision, and keymap hash.
- [ ] Generate the corresponding firmware-side layout identifier.
- [ ] Preserve the keymap's behavior definitions byte-for-byte when copying it into the generated workspace.
- [ ] Add fixtures covering complex behaviors, Unicode paths, spaces, reordered layers, malformed exports, and future exporter variations.
- [ ] Do not build a general Devicetree rewriter.

Deliverables:

- Tested keymap inspector.
- Versioned manifest writer and reader.
- Representative parser fixtures.

Done when:

- The current complex layout produces the correct ordered name/ID mapping.
- Malformed or ambiguous inputs fail with clear, actionable errors.
- Reordering a layer changes the layout identifier.

Depends on: Step 4. May run in parallel with Steps 5 and 6 after the keymap format is available.

## Step 8: Build the headless firmware pipeline

Goal: make the complete build reliable before adding a graphical wrapper.

Work:

- [ ] Accept an explicit keymap path from a command-line development tool.
- [ ] Create a clean disposable generated workspace.
- [ ] Copy the keymap without rewriting its behavior definitions.
- [ ] Generate and embed the matching layout identifier.
- [ ] Invoke the pinned build environment selected in Step 3.
- [ ] Validate that the UF2 is newly produced, non-empty, and from the current build.
- [ ] Publish the UF2, manifest, and log as one atomic output set.
- [ ] Leave no apparently successful partial output after failure or cancellation.
- [ ] Surface Docker-not-running, insufficient-space, download, compiler, parser, and stale-output errors clearly.
- [ ] Test cold, warm, and cached-offline builds.
- [ ] Test paths containing spaces, Unicode, long names, and OneDrive synchronization.

Deliverables:

- Scriptable keymap-to-UF2 pipeline.
- Matched UF2/manifest/log output set.
- Automated orchestration tests where hardware is not required.

Done when:

- One command produces the complete matched output set without manual source edits.
- A failed build never leaves a UF2 that could be mistaken for a successful new result.

Depends on: Steps 3, 5, and 7. Can progress alongside Step 6, but its production UX waits for the transport gate.

## Step 9: Lock and test the version-one protocol

Goal: replace the transport-spike message with a small, stable application protocol.

Work:

- [ ] Choose and document framing and message encoding based on the USB/BLE spike results.
- [ ] Define `Hello`, capabilities, and layout-identifier negotiation.
- [ ] Define initial `StateSnapshot` and `LayerChanged` messages.
- [ ] Define persistent set and momentary press, renew, and release commands.
- [ ] Define command results and structured errors.
- [ ] Bound every message, collection, string, and receive buffer.
- [ ] Add session IDs, command IDs, and a state revision or sequence number.
- [ ] Allow only one command-owning session at a time.
- [ ] Require a fresh handshake and snapshot after every transport switch.
- [ ] Define duplicate, delayed, reordered, truncated, corrupted, mismatched-layout, and unsupported-version behavior.
- [ ] Create shared golden byte vectors consumed by both C and C# tests.

Deliverables:

- Version-one protocol specification.
- Matching C and C# codecs.
- Shared test vectors and malformed-input tests.

Done when:

- C and C# encode and decode the same golden messages.
- Invalid protocol traffic cannot change layer state or disrupt normal HID typing.
- Compatibility and version-rejection behavior are deterministic.

Depends on: Step 6.

## Step 10: Implement effective-layer telemetry

Goal: make the companion converge on the keyboard's true layer state without polling.

Work:

- [ ] Observe effective ZMK layer changes on the central.
- [ ] Send a state snapshot after every successful handshake.
- [ ] Send an event only when the effective layer changes.
- [ ] Include the state revision needed to identify stale or missed updates.
- [ ] Validate the firmware layout identifier against the loaded manifest.
- [ ] Resolve numeric layer IDs to names in C#.
- [ ] Test `&mo`, `&to`, `&tog`, conditional layers, transparent bindings, and right-half-triggered changes.
- [ ] Test reconnects and transport switches while layers are active.
- [ ] Verify feature parity over USB and Bluetooth.

Deliverables:

- Firmware layer observer.
- Companion-side state tracker suitable for the later widget.
- Layer telemetry integration tests and hardware results.

Done when:

- The C# state always converges to the firmware's effective layer after connect, change, sleep, reconnect, and transport switch.
- A missing or mismatched manifest prevents control and is reported clearly.

Depends on: Steps 7 and 9.

## Step 11: Decide battery support

Goal: retain battery features only if both halves can be reported reliably.

Work:

- [ ] Read left and right values distinctly at the central.
- [ ] Test wireless inter-half and TRRS inter-half operation.
- [ ] Test charging, a missing or powered-off right half, stale readings, reconnects, and keyboard sleep.
- [ ] Measure useful update frequency and added power cost.
- [ ] Define how unavailable or stale values are represented if the feasibility test passes.
- [ ] If any required behavior fails, remove battery fields and UI plans completely.

Deliverables:

- Battery feasibility report.
- Either a tested two-half protocol addition or an explicit removal decision.

Done when:

- Both readings are proven accurate, distinguishable, timely, and affordable in power use; or no battery feature remains in version-one scope.

Depends on: Steps 6 and 9. May run in parallel with Step 10.

## Step 12: Implement persistent and momentary layer control

Goal: add external control without overwriting layer state created by the keyboard.

Before coding, write a truth table covering keyboard-created state, an external persistent selection, and one or more session-owned momentary activations. Include `&mo`,
`&to`, `&tog`, conditional layers, and the case where physical and external actions activate the same layer.

Work:

- [ ] Implement validated persistent layer selection.
- [ ] Decide and document whether persistent state survives a keyboard reboot; the initial recommendation is runtime-only.
- [ ] Give each momentary activation a session and token.
- [ ] Implement press, periodic renewal, explicit release, session cleanup, and firmware expiry.
- [ ] Make commands idempotent where repetition is possible.
- [ ] Reject renewals and releases belonging to an old session.
- [ ] On companion reconnect, forget held shortcuts and require a fresh physical key-down.
- [ ] Test quick release before acknowledgement, duplicates, reordering, process termination, USB removal, BLE loss, Windows sleep, radio loss, keyboard reset, and transport switching while held.
- [ ] Verify that releasing an external momentary layer reveals the state created by keyboard keys and persistent commands.

Deliverables:

- Layer ownership and precedence specification.
- Firmware layer-command implementation.
- C# command state machine and failure-injection tests.

Done when:

- No tested failure can leave a momentary layer stuck beyond its lease and bounded scheduling tolerance.
- Persistent and momentary behavior is identical over USB and Bluetooth.
- The keyboard remains fully functional without the companion.

Depends on: Steps 9 and 10.

## Step 13: Build the headless companion core

Goal: complete the daily workflow without depending on final UI.

Work:

- [ ] Discover the correct Go60 over USB and Bluetooth.
- [ ] Select one command-owning transport and arbitrate transport switches.
- [ ] Handshake, validate the layout, request a snapshot, and maintain connection state.
- [ ] Reconnect with bounded backoff after sleep, disconnect, or keyboard restart.
- [ ] Load and validate the manifest and user configuration.
- [ ] Detect configured global shortcut key-down and key-up events.
- [ ] Suppress OS key-repeat from creating duplicate presses.
- [ ] Renew momentary leases only while the shortcut remains physically held.
- [ ] Test shortcuts generated by the real G502 as well as synthetic input sequences.
- [ ] Provide useful diagnostic logs and a compact diagnostic status view.

Deliverables:

- Companion core with no taskbar-widget dependency.
- Shortcut configuration model.
- End-to-end diagnostic application.

Done when:

- A configured shortcut changes the firmware layer and receives the resulting state over USB and Bluetooth.
- Startup order, sleep, disconnect, and transport switching recover automatically.

Depends on: Steps 10 and 12.

## Step 14: Add configuration and the Windows widget

Goal: present the stable companion state as a simple Windows experience.

Work:

- [ ] Build the WPF configuration surface for shortcut/action mappings.
- [ ] Build a focusless taskbar-adjacent widget.
- [ ] Display the active layer as the primary value.
- [ ] Display connected, disconnected, and stale states clearly.
- [ ] Add left/right battery values only if Step 11 passed.
- [ ] Add optional start-with-Windows behavior.
- [ ] Test DPI scaling, multiple monitors, taskbar movement, taskbar auto-hide, Explorer restart, Windows sleep, and no-focus-stealing behavior.
- [ ] Keep presentation separated from companion state so fake state can drive UI tests.

Deliverables:

- WPF companion configuration UI.
- Taskbar-adjacent widget.
- UI tests and manual Windows layout checks.

Done when:

- Ordinary use requires no diagnostic console or development commands.
- The widget always distinguishes current, stale, and disconnected information.

Depends on: Step 13 and the decision from Step 11.

## Step 15: Add the one-click builder experience

Goal: wrap the proven headless build pipeline in the agreed beginner-friendly flow.

Work:

- [ ] Find exactly one `.keymap` in `Input` or accept a dropped file.
- [ ] Prompt for selection rather than guessing when multiple inputs exist.
- [ ] Check Docker availability, build-image availability, and measured disk requirements.
- [ ] Show clear progress without exposing unnecessary compiler noise.
- [ ] Support cancellation without leaving a false-success output.
- [ ] Publish the UF2, manifest, and log atomically into `Output`.
- [ ] Open `Output` on success.
- [ ] Offer a scoped ShinyGo60 cache cleanup action.
- [ ] Publish the builder as a self-contained Windows executable.
- [ ] Test on an account with no Visual Studio, Git, Python, or .NET runtime installed.

Deliverables:

- `ShinyGo60.Builder.exe`.
- Release-style `Input` and `Output` folder layout.
- Builder prerequisite and error guidance.

Done when:

- A non-developer can add one keymap and double-click the executable to receive the matched UF2, manifest, and log.
- The measured storage use remains within the decision made at gate G2.

Depends on: Steps 3 and 8. Final UX should wait until Step 6 passes.

## Step 16: Harden the complete system

Goal: find lifecycle, transport, and safety failures before packaging.

Work:

- [ ] Run the complete verification matrix from IMPLEMENTATION_PLAN.md.
- [ ] Test USB/BLE switching, repeated reconnects, sleep cycles, radio toggles, and power cycles.
- [ ] Test the app starting before and after the keyboard.
- [ ] Test multiple serial devices and multiple paired Go60 host profiles.
- [ ] Inject stale manifests, corrupted frames, duplicate messages, delays, and event bursts.
- [ ] Test with the right half absent and with BLE/TRRS inter-half switching.
- [ ] Measure typing latency, command latency, idle CPU use, memory use, and wireless power impact.
- [ ] Run multi-day BLE-heavy soak testing.
- [ ] Repeatedly terminate and restart the companion during held and persistent actions.
- [ ] Verify the Go60 remains a normal keyboard with ShinyGo60 software absent.

Deliverables:

- Completed test matrix with defects and resolutions.
- Performance and power measurements.
- Soak-test results.

Done when:

- Both transports have feature parity.
- There is no known stuck-layer path.
- No unacceptable typing, battery, CPU, or reconnect regression remains.

Depends on: Steps 13, 14, and 15.

## Step 17: Validate flashing, upgrades, and rollback

Goal: make firmware installation and layout updates safe for a beginner.

Work:

- [ ] Test manual flashing of both halves in both orders.
- [ ] Test interrupted or apparently failed file copies and document the expected bootloader behavior.
- [ ] Test rollback to the known-good UF2.
- [ ] Test pairing retention and document re-pairing when required.
- [ ] Test Windows GATT service discovery after a firmware update.
- [ ] Test a layout update that reorders layers.
- [ ] Confirm the companion refuses control with a mismatched manifest.
- [ ] Keep automated flashing out of version one unless bootloader identification and failure recovery are proven safe and require explicit confirmation.

Deliverables:

- Flash-both-halves guide.
- Recovery and rollback guide.
- Upgrade and re-pairing guide.

Done when:

- A beginner can install, validate, update, and recover the firmware by following the written steps.

Depends on: Step 16.

## Step 18: Package and release

Goal: ship a coherent versioned product rather than a collection of development outputs.

Work:

- [ ] Publish self-contained Windows executables.
- [ ] Create a versioned ZIP with the builder, companion, `Input`, `Output`, example configuration, guides, licenses, and scoped cleanup option.
- [ ] Include exact Docker and disk prerequisites based on measurements.
- [ ] Include a diagnostics-bundle action that gathers only relevant logs and versions.
- [ ] Test SmartScreen and antivirus behavior; code-sign releases if practical.
- [ ] Run clean-machine acceptance with Docker installed but no development SDKs.
- [ ] Have a non-developer follow export, build, flash, configure, Bluetooth-use, update, and recovery instructions.
- [ ] Verify every item in the [version-one outcome](#version-one-outcome).
- [ ] Tag the pinned firmware, protocol, manifest schema, and Windows application versions together.

Deliverables:

- Versioned release package.
- Release notes and known limitations.
- Clean-machine and non-developer acceptance results.

Done when:

- Every blocking gate has passed.
- The version-one outcome at the top of this document is reproducible from the release package.

Depends on: Step 17.

## Parallel work after the baseline

After Steps 0 through 4, work can proceed in coordinated tracks:

| Track | Work | Integration checkpoints |
| --- | --- | --- |
| A: Build | Storage optimization, keymap inspector, headless pipeline, builder UI | Matched UF2/manifest/log and accepted footprint |
| B: Firmware | Module, USB/BLE transports, telemetry, layer commands, battery gate | Dual-transport handshake, state convergence, lease safety |
| C: Windows core | Protocol codec, diagnostic client, companion sessions, shortcuts | Golden vectors, transport parity, shortcut-to-layer round trip |
| D: UI | Widget mock and configuration design using fake state | Stable companion state contract before production integration |

Rules for parallel work:

- Track D may prototype layout early, but production UI waits for the dual-transport and state-contract gates.
- Track A may build the headless pipeline during the transport spike, but the final one-click promise waits for measured storage and Bluetooth success.
- Tracks B and C must share golden protocol vectors rather than independently interpreting prose.
- Battery investigation may run beside layer telemetry, but its pass/remove decision precedes final widget layout.

## Integration checkpoints

| Checkpoint | Integrated result |
| --- | --- |
| I0 | Known-good rollback, repeatable untouched UF2, and measured official build |
| I1 | Accepted slim build environment and no-op custom module |
| I2 | Identical `Hello` round trip over USB and Bluetooth |
| I3 | Headless builder produces an atomic, matched UF2/manifest/log set |
| I4 | C and C# pass shared protocol vectors; layer state converges over both transports |
| I5 | Persistent and leased momentary commands pass crash and disconnect tests |
| I6 | Real mouse shortcut changes the layer and the widget reflects it over USB and Bluetooth |
| I7 | Clean-machine, flashing, rollback, and non-developer acceptance pass |

## Release ladder

### Developer alpha

- Gates G0 through G2 pass.
- The baseline is reproducible and recoverable.
- The build footprint is measured and accepted.

### Hardware alpha

- Gate G3 passes.
- The custom module and C# diagnostic client communicate over USB and Bluetooth.
- The headless builder produces matched artifacts.

### Feature beta

- Telemetry, persistent layers, leased momentary layers, shortcuts, and the widget work over both transports.
- The battery feature has been retained or removed conclusively.
- Failure-injection tests show no stuck-layer path.

### Release candidate

- Clean-machine packaging passes.
- Flash, update, rollback, reconnect, and multi-day soak tests pass.
- Remaining limitations are written and acceptable.

### Version 1.0

- Every blocking gate and integration checkpoint passes.
- USB and Bluetooth have complete feature parity.
- The accepted storage target is met.
- A non-developer successfully completes the full workflow and recovery instructions.
