# ShinyGo60 Implementation Plan

Status: first central-only firmware feature passed its hardware smoke test; recovery preparation skipped by user; detailed baseline verification remains

Last updated: 2026-09-01

This document turns the goals in [README.md](README.md) into an implementation plan. It records the decisions made so far, the proposed user experience, the technical architecture, the major risks, and the order in which the work should be validated.

## 1. Target experience

The normal layout workflow should remain simple:

1. Create and maintain the layout in the MoErgo Layout Editor.
2. Export the layout as a ZMK `.keymap` file.
3. Put that file in the builder's `Input` folder.
4. Double-click `ShinyGo60.Builder.exe`.
5. Find the customized UF2 and its matching manifest in `Output`.
6. Manually flash the same UF2 to both halves of the Go60.
7. Run the ShinyGo60 Windows companion for layer control and status display.

An end-user release should look approximately like this:

```text
ShinyGo60 Builder/
|-- Input/
|   `-- My Layout.keymap
|-- Output/
`-- ShinyGo60.Builder.exe
```

A successful build should produce:

```text
Output/
|-- My Layout - ShinyGo60.uf2
|-- layout-manifest.json
`-- build.log
```

Generated firmware files should never need manual editing. Updating the layout should be an export, build, and flash operation.

## 2. Agreed decisions

### `.keymap` is the version-one input

The first version will accept a MoErgo-exported `.keymap` file. JSON import is out of scope for version one.

MoErgo documents the `.keymap` export as a ZMK DTSI fragment intended for advanced uses such as local compilation. MoErgo also labels its JSON export as experimental and warns that its format may change. Restricting version one to `.keymap` avoids maintaining a second, unstable conversion path.

The builder should preserve the exported keymap rather than recreating its bindings. This matters because a layout may contain custom behaviors, macros, hold-taps, combos, input processors, touchpad configuration, and other ZMK definitions.

### C# for Windows tooling

C# tooling and the widget will support Windows 11 in version one.

C# is the preferred language for:

- The one-click firmware builder and its user interface.
- The background Windows companion.
- Global shortcut detection and configuration.
- USB and Bluetooth transport adapters.
- The status widget.
- Manifest generation, validation, and protocol code shared by Windows components.

WPF is the initial recommendation for the Windows UI because the project is Windows-only and needs a small, unobtrusive desktop widget rather than a cross-platform interface.

The C# programs should be published as self-contained Windows executables so the user does not have to install the .NET runtime. A single-file release is preferred when its library choices permit it.

Firmware flashing will remain a deliberate manual copy to each half for version one. The builder produces the artifact but does not automatically write to bootloader drives. Mouse integration remains based on ordinary configurable Windows shortcuts rather than a Logitech-specific API.

### Native code for keyboard firmware

Code that runs on the Go60 must use the ZMK/Zephyr toolchain. The custom integration will therefore contain C, Devicetree, Kconfig, and CMake files. C# will orchestrate the build but will not run on the keyboard.

The firmware changes should be maintained as an out-of-tree ZMK feature module where the MoErgo build permits it. This keeps the custom functionality separate from both the exported keymap and the MoErgo ZMK source.

### USB and Bluetooth are equally required

Layer commands and keyboard telemetry must work through either host connection:

```text
C# companion
    |-- USB transport: CDC/serial
    `-- Bluetooth transport: custom BLE GATT service
                  |
                  v
         One ShinyGo60 protocol
                  |
                  v
          Go60 central/left half
```

Bluetooth is not a later enhancement. A wireless Go60 must support the complete feature set without a USB cable.

The transport design should follow the proven ZMK Studio pattern: framed messages over USB CDC/ACM and a custom Bluetooth Low Energy GATT service. Direct reuse of ZMK Studio internals may be considered if it is clean and maintainable; otherwise ShinyGo60 should implement a smaller service with its own UUID and protocol while following the same transport model.

The customized UF2 must contain both transports. Separate USB and Bluetooth firmware variants are not desired.

## 3. Important Go60 architecture

The left half is the ZMK central and is the Go60's connection to the host. USB host connections must use the left half, and Bluetooth host connections are also made by the left half. The right half is a peripheral and forwards its events to the left half.

Consequently:

- Host protocol handling belongs on the central/left side.
- Effective layer state is owned by the central.
- The Bluetooth GATT service is exposed by the central.
- The same MoErgo-produced UF2 is still flashed to both halves.
- Inter-half communication may use BLE or TRRS independently of whether the host connection uses BLE or USB.

## 4. Development repository structure

The scaffold keeps maintained source, disposable build state, and user output separated:

```text
ShinyGo60/
|-- Custom Firmware/
|   |-- Module/                 # Hand-maintained ZMK feature module
|   |-- BuildSupport/           # Pinned build definitions and integration
|   `-- Generated/              # Disposable build workspace; ignored
|-- Key Configuration/          # Layout Editor exports and snapshots
|-- Windows/
|   |-- ShinyGo60.sln
|   |-- ShinyGo60.Diagnostics/
|   |-- ShinyGo60.Builder.Core/
|   |-- ShinyGo60.Builder/
|   |-- ShinyGo60.Companion.Core/
|   |-- ShinyGo60.Companion/
|   |-- ShinyGo60.Protocol/
|   `-- ShinyGo60.Tests/
|-- Output/                     # Generated UF2, manifest, and logs; ignored
|-- IMPLEMENTATION_PLAN.md
`-- README.md
```

Hand-maintained source, generated build files, and user outputs must remain visibly separate.

## 5. Firmware build pipeline

### Builder responsibilities

`ShinyGo60.Builder.exe` should:

1. Accept a dropped `.keymap` path or find exactly one `.keymap` in `Input`.
2. Verify that it resembles a complete Go60 Layout Editor export.
3. Extract the ordered layer identifiers and names from the generated layer definitions.
4. Calculate a layout identifier from the build protocol version and keymap content.
5. Create a clean generated build workspace.
6. Copy the exported keymap into that workspace without rewriting its behavior definitions.
7. Generate the layout manifest and the firmware-side layout identifier.
8. Add the ShinyGo60 firmware module and required configuration.
9. invoke a pinned, reproducible MoErgo ZMK build.
10. Copy the UF2, manifest, and readable log into `Output`.
11. Clearly report success or a useful error and open the output folder on success.

If there are zero or multiple input keymaps, the builder should display an understandable selection or validation message rather than guessing.

### Layer manifest

The generated manifest should contain at least:

- Manifest schema version.
- ShinyGo60 protocol version.
- Layout identifier.
- Hash of the exact keymap used for the build.
- Firmware source revision.
- Ordered mapping from numeric layer ID to layer name.
- Build timestamp for display and diagnostics; it must not affect reproducibility of the firmware content.

The firmware and manifest must contain the same layout identifier. The companion must reject layer-changing commands when the identifiers do not match, preventing reordered layers from silently selecting the wrong target.

### Reproducible build environment

MoErgo's official Go60 configuration repository supplies a Windows `build.bat` backed by Docker, Nix, and the MoErgo ZMK distribution. The selected backend preserves
that supported toolchain in a pinned, single-revision, Go60-only image instead of preloading several firmware revisions.

The C# builder will hide the commands and present a friendly experience, but version one requires Docker Desktop. It will pull a published, digest-pinned image when
the image is absent and will run normal firmware builds without container network access. The validated image is 949,287,144 bytes as Docker content and 4.46 GB
unpacked. A warm offline firmware build took 14.857 seconds.

A self-contained C# executable does not make the embedded compiler self-contained. Eliminating Docker would require either bundling a large toolchain or moving compilation to a build service. Those alternatives are not version-one requirements.

All firmware dependencies must be pinned to tested revisions. Updating the MoErgo ZMK revision should be an explicit, tested operation rather than silently following its latest branch.

Constructing the image locally is a maintainer and recovery operation requiring approximately 10 GB free during construction. The user-facing path pulls the
prebuilt image because local construction temporarily retained a 4.496 GB BuildKit cache alongside the completed image. Cleanup is restricted to named,
ShinyGo60-owned resources; global Docker pruning is forbidden. The complete measurements are in
[Custom Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md](Custom%20Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md).

## 6. Firmware module

The firmware feature module should remain independent of the generated keymap and provide these responsibilities:

### Layer observation

- Listen for ZMK layer-state changes on the central.
- Determine the current effective layer using ZMK's layer state rather than duplicating keymap logic.
- Send the current state when a companion session connects.
- Send an event only when the effective state changes.
- Avoid polling and avoid adding measurable typing latency.

### External layer control

Maintain externally requested state without overwriting state created by keys on the Go60:

- A persistent command selects a layer until another persistent command or a physical keyboard `&to` action replaces it.
- A momentary press adds an external activation while the mouse button is held.
- Releasing the momentary activation reveals whatever keyboard-created and persistent state would otherwise be effective.
- Invalid layer IDs, malformed messages, layout mismatches, and unsupported protocol versions are rejected without changing keyboard state.
- Normal keyboard operation does not depend on the companion being present.

The exact interaction between persistent external state and every ZMK layer mechanism (`&mo`, `&to`, `&tog`, conditional layers, and transparent bindings) must be specified and tested before implementation is considered complete.

### Momentary-layer failure safety

A lost process or connection must never leave an externally held layer active indefinitely.

Momentary activation should therefore be a renewable lease:

1. The companion sends a momentary-press command with a short lease.
2. It renews that lease only while the mouse button remains held.
3. It sends an explicit release on button-up.
4. Firmware automatically releases the activation if the lease expires.
5. Firmware also clears session-owned momentary activations when the protocol session ends.

Persistent actions are not cleared by a transient transport disconnect. A physical keyboard `&to` action deliberately clears the external persistent owner so
the keyboard can always select Home or another layer. Persistent selections are runtime-only and clear on keyboard reboot without recurring flash writes.

## 7. Transport-independent protocol

The same application messages must be used over USB and Bluetooth. Transport-specific code should only move framed bytes and report connection state.

### Initial message set

The first protocol should support:

- `Hello`: exchange protocol version, capabilities, and layout identifier.
- `StateSnapshot`: report the current effective layer after connection or resynchronization.
- `LayerChanged`: notify the companion when the effective layer changes.
- `SetPersistentLayer`: select a persistent external layer.
- `PressMomentaryLayer`: start a leased external activation.
- `RenewMomentaryLayer`: keep a held activation alive.
- `ReleaseMomentaryLayer`: end it normally.
- `CommandResult`: acknowledge a command with its result and resulting state.
- `Error`: reject invalid, unsupported, unauthorized, or mismatched messages.

Every command should have an identifier so delayed or duplicate responses can be recognized. Messages should be compact, versioned, bounded in size, and safe to parse from untrusted input.

The framing and encoding choice should be made during the transport spike. ZMK Studio's framed protobuf protocol is a useful reference, but ShinyGo60 does not require all of Studio's message definitions or runtime keymap functionality.

### USB transport

The preferred first prototype is a CDC/ACM serial endpoint, matching the USB transport used by ZMK Studio. Windows has standard support for this class and C# can communicate with it without a ShinyGo60 kernel driver.

The build and companion must identify the correct Go60 endpoint reliably rather than selecting an arbitrary serial port.

### Bluetooth transport

The central should expose a ShinyGo60-specific, encrypted BLE GATT service alongside the normal Bluetooth HID keyboard service. The likely minimal shape is:

- One custom service UUID.
- A write-capable characteristic for companion-to-keyboard frames.
- Indications or notifications for keyboard-to-companion frames.
- Encryption/bonding permissions so an unpaired nearby device cannot control layers.

The C# companion can use `Windows.Devices.Bluetooth.GenericAttributeProfile` to discover the service, write commands, and subscribe to value changes.

Bluetooth requirements include:

- No additional physical dongle.
- No USB cable during wireless operation.
- Preferably no second pairing entry; the service belongs to the already paired Go60.
- Automatic recovery after sleep, radio disable/enable, keyboard restart, and temporary range loss.
- Correct behavior across the Go60's multiple paired host profiles.
- No high-frequency polling.
- No permanent low-latency connection setting that materially harms battery life.

Windows GATT caching and any need to remove and re-pair after firmware service changes must be tested during the prototype rather than discovered during packaging.

### Transport selection

The companion should expose a common transport interface and select automatically:

1. Prefer the transport corresponding to the Go60's active host output when it can be determined.
2. Avoid two simultaneous command-owning sessions to the same keyboard.
3. When transport changes, perform a new handshake and request a state snapshot.
4. Show a stale or disconnected state if no valid session exists.

## 8. Windows companion and widget

The companion should be a single background Windows application containing these logical services:

- Go60 discovery and transport selection.
- Protocol session, validation, acknowledgements, and reconnect behavior.
- Configurable global shortcut detection with separate key-down and key-up events.
- Mapping from shortcuts to persistent or momentary layer actions.
- Manifest loading and layer-name resolution.
- Widget state and UI.
- Optional start-with-Windows integration.

The Logitech mouse does not require a Logitech-specific API. Its button can be configured to emit an uncommon shortcut such as `Ctrl+Alt+Shift+F13`; the companion observes that normal Windows input and translates it into a Go60 command.

The widget should:

- Display the effective layer name as its primary value.
- Run as a focusless child of the Windows 11 taskbar at its far left.
- Display clear connected, disconnected, and stale states.
- Update from events rather than frequent polling.
- Show separate left and right battery values only if the battery feasibility gate passes.

The selected hosting method reparents the WPF widget HWND to `Shell_TrayWnd` and uses coordinates relative to the taskbar client area. Explorer consequently owns
the widget's fullscreen visibility and z-order. A low-frequency lifecycle check is permitted only to detect taskbar replacement and restore attachment; it is
not used to detect fullscreen applications or update keyboard state.

## 9. Battery feasibility gate

Battery telemetry remains conditional even though the Go60 itself has separate batteries and local indicators.

The feasibility milestone must establish that firmware can provide accurate, timely, and distinguishable left- and right-half readings to the central under both wired-split and wireless-split operation. It must also measure whether obtaining and transmitting the values has an unacceptable power cost.

If either half cannot be reported reliably, battery support will be removed completely from:

- Firmware messages.
- Protocol schemas.
- Companion state.
- Widget layout.
- Configuration and tests.

There should not be a permanently half-working one-battery fallback.

## 10. Milestones and gates

### Milestone 0: Baseline and scaffolding

- Pin a known production-compatible MoErgo ZMK revision.
- Reproduce the official build from an ordinary exported Go60 keymap.
- Record the toolchain and output hash.
- Scaffold the C# solution and firmware module without custom behavior.

Exit condition: a repeatable local build produces a working, otherwise unmodified Go60 UF2.

### Milestone 1: One-click keymap builder

- Implement input discovery and validation.
- Extract layer names and generate a manifest.
- Generate an isolated build workspace.
- Run the pinned build and package outputs.
- Provide readable progress and failures.

Exit condition: placing a keymap in `Input` and double-clicking the builder produces the UF2, manifest, and log without manual source editing.

### Milestone 2: Mandatory dual-transport spike

- Build a minimal custom firmware feature on the central.
- Exchange a versioned `Hello` and acknowledgement over USB.
- Exchange the identical messages over the existing Go60 Bluetooth connection.
- Validate encrypted access, Windows discovery, reconnects, sleep/wake, and transport switching.
- Measure basic latency and battery impact.

Exit condition: the same C# program reliably performs a round trip over both USB and Bluetooth. Failure of the Bluetooth path blocks the planned architecture and must be resolved before proceeding.

### Milestone 3: Layer telemetry and battery gate

- Observe effective ZMK layer changes.
- Send initial and changed state over both transports.
- Resolve numeric IDs using the matching manifest.
- Test and decide the per-half battery feasibility gate.

Exit condition: the companion always converges on the correct layer after connect, change, sleep, and reconnect; battery support is either proven or removed.

### Milestone 4: External layer commands

- Implement persistent selection.
- Implement leased momentary press, renewal, and release.
- Verify coexistence with layers activated on the keyboard.
- Reject invalid and mismatched commands safely.

Exit condition: mouse-held and persistent layer behavior works over USB and Bluetooth, including disconnect and app-crash cases, without stuck layers.

### Milestone 5: Companion and widget

- Add shortcut configuration and global key-down/key-up handling.
- Add transport discovery and automatic reconnect.
- Build the taskbar-adjacent widget.
- Add disconnected and stale states.
- Add start-with-Windows support.

Exit condition: the complete day-to-day workflow works without development tools or manual commands.

### Milestone 6: Packaging and hardening

- Publish self-contained Windows executables.
- Test clean-machine setup and Docker prerequisites for the builder.
- Add protocol, parser, and integration tests.
- Document flashing, recovery, upgrades, and log collection.
- Verify reproducible builds and license notices.

Exit condition: a non-developer can export, build, flash, configure, and use ShinyGo60 from the provided instructions.

## 11. Verification matrix

At minimum, test these scenarios:

| Area | Scenarios |
| --- | --- |
| Build | Valid keymap, missing keymap, multiple keymaps, malformed export, paths with spaces and Unicode, cold Docker cache, warm cache, offline cached build |
| Layout safety | Matching manifest, reordered layers, stale manifest, unsupported protocol version, corrupted message |
| USB | Initial connection, unplug/replug, sleep/resume, selected and non-selected USB output, wrong serial devices present |
| Bluetooth | Fresh pairing, existing pairing after firmware update, all host profiles, radio disable/enable, sleep/resume, range loss, keyboard power cycle |
| Transport switching | BLE to USB, USB to BLE, both present, app starts before keyboard, keyboard starts before app |
| Momentary layers | Normal release, app crash while held, BLE loss while held, USB removal while held, duplicate press/release, lease expiry |
| Persistent layers | Replacement by another command, interaction with `&mo`, `&to`, and `&tog`, reconnect, keyboard reboot decision |
| Split behavior | BLE between halves, TRRS between halves, missing right half, right-half key activates a layer |
| Performance | Typing latency, layer-command latency, event burst handling, idle CPU usage, BLE power impact |
| Widget | Correct layer name, disconnected/stale display, taskbar movement, DPI scaling, multiple monitors, Explorer restart |

Firmware must also be tested as a normal keyboard with the companion never installed or not running.

## 12. Primary risks and mitigations

### MoErgo ZMK divergence

The Go60 uses MoErgo's ZMK distribution rather than an arbitrary upstream ZMK build. Pin and test the MoErgo revision, keep the feature out of tree when possible, and isolate version-specific integration.

### Export parser fragility

Only extract the small amount of standardized metadata needed from `.keymap`; do not attempt a general Devicetree rewrite. Keep representative Layout Editor exports as parser test fixtures.

### Bluetooth service behavior on Windows

Prototype GATT discovery, encrypted writes, indications, reconnection, Windows caching, and multiple profiles before building higher-level features. The Bluetooth round trip is a blocking milestone.

### Wireless momentary-state loss

Use session-owned renewable leases and firmware-side expiry so a dropped connection or crashed app cannot leave a layer held.

### Battery and sleep regressions

Keep messages event-driven, avoid continuous polling, measure real idle behavior, and test wake from sleep with the exact pinned Go60 firmware. Do not enable the complete ZMK Studio feature set merely to obtain its transport if that adds unrelated memory, storage, or power behavior.

### Toolchain size and setup

Hide Docker and compiler commands behind the builder, provide a prerequisite check, and retain build logs. Do not describe the builder as dependency-free while it relies on Docker.

### Firmware recovery

Never automate flashing until the bootloader-drive identification and failure behavior are proven. Always retain documented power-on bootloader recovery and advise having another keyboard or the Windows on-screen keyboard available during firmware testing.

## 13. Explicit non-goals for version one

- Patching or reverse engineering an existing UF2.
- Accepting MoErgo JSON as a build input.
- Direct integration with Logitech software or a Logitech-specific API.
- Editing the layout within the ShinyGo60 application.
- Supporting operating systems other than Windows 11 for the companion and widget.
- Hiding an unreliable battery implementation behind partial or stale values.
- Requiring users to edit generated ZMK files manually.

## 14. Open decisions

These choices should be resolved by their associated milestone rather than assumed now:

- Reuse ZMK Studio transport internals or implement a smaller ShinyGo60 transport following the same design.
- Exact message encoding and framing.
- Exact pinned MoErgo ZMK production revision.
- Whether persistent external layer selection survives keyboard reboot.
- Exact rule when both USB and Bluetooth connections are simultaneously available.
- Multi-monitor policy beyond the primary Windows taskbar.
- Registry location and immutable digest for the prebuilt Docker image when it is published.
- Whether flashing remains manual or gains an explicitly confirmed helper after safe bootloader detection is proven.

## 15. Authoritative references

- [MoErgo Layout Editor: exporting a keymap and JSON stability warning](https://docs.moergo.com/layout-editor-guide/advanced-usage-export-import/)
- [MoErgo Go60 official ZMK configuration and build template](https://github.com/moergo-keyboards/go60-zmk-config)
- [MoErgo Go60 architecture: left central and host connections](https://docs.moergo.com/go60-user-guide/introduction/)
- [MoErgo Go60 wired and wireless split behavior](https://docs.moergo.com/go60-user-guide/wired-and-wireless-split/)
- [MoErgo Go60 firmware flashing instructions](https://docs.moergo.com/go60-user-guide/customizing-key-layout/)
- [ZMK module creation](https://zmk.dev/docs/development/module-creation)
- [ZMK Studio RPC protocol and its USB/BLE transports](https://zmk.dev/docs/development/studio-rpc-protocol)
- [Microsoft Windows Bluetooth GATT client APIs](https://learn.microsoft.com/windows/apps/develop/devices-sensors/gatt-client)
- [Microsoft .NET single-file deployment](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
