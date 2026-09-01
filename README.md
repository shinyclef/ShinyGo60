# ShinyGo60

Custom firmware tooling and Windows 11 integration for the [MoErgo Go60](https://www.moergo.com/).

The project extends layouts created with the MoErgo Layout Editor with mouse-driven layer controls, keyboard status reporting, and an at-a-glance Windows widget. The editor remains the source of truth for key mappings and ordinary ZMK behaviors.

## Version-one contract

- Support Windows 11.
- Accept a MoErgo-exported ZMK `.keymap` as the only firmware input.
- Implement the builder, companion, configuration, and widget in C#.
- Implement the keyboard integration as native ZMK/Zephyr firmware code.
- Provide the full command and telemetry feature set over both USB and Bluetooth.
- Produce one customized UF2 that is manually flashed to both Go60 halves.
- Receive mouse controls as configurable Windows shortcuts without using a Logitech-specific API.
- Include battery reporting only if reliable, separate readings from both halves pass the feasibility gate.
- Preserve normal keyboard operation when the Windows companion is not running.

## Project goals

### 1. Control Go60 layers from a mouse

Use a button on a Logitech G502 X Plus as a Go60 layer switch. The mouse button can be configured to emit an uncommon shortcut such as `Ctrl+Alt+Shift+F13`, which the Windows companion translates into a command for the keyboard.

Each shortcut can be assigned one of two actions:

- **Momentary layer:** activate the target layer when the shortcut is pressed and release that external activation when the shortcut is released. The target remains active only while the mouse button is held.
- **Go to layer:** select the target layer when the shortcut is pressed and leave it active until another layer action replaces it.

The shortcut and target layer will be configurable rather than tied to a particular mouse button or function key. Using a normal keyboard shortcut also avoids requiring direct integration with Logitech software or a mouse-specific API.

### 2. Send Go60 status to Windows

The custom firmware should report the current effective ZMK layer and layer changes as they happen.

The project will also investigate whether the firmware can reliably report the battery level of the left and right halves independently. Battery support has an explicit feasibility gate: if the firmware cannot supply both values reliably, battery status will be removed from the project scope and will not be included in the Windows companion or widget.

### 3. Show status in a small Windows widget

Create a compact widget that lives at the bottom-left of the Windows taskbar area. It should show the active layer name and, only if the battery feasibility gate passes, separate battery levels for the left and right keyboard halves.

The widget should be unobtrusive, readable at a glance, and able to start with Windows 11. The exact implementation—native taskbar integration or a borderless taskbar-adjacent window—will be selected after prototyping, while preserving the intended bottom-left position and behavior.

## Intended workflow

The firmware build path is:

```text
MoErgo Layout Editor
        |
        | export .keymap
        v
ShinyGo60 transformation/build tool
        |
        | add communication support and build
        v
Customized .uf2 firmware
        |
        v
MoErgo Go60
```

At runtime, the Windows companion connects the mouse controls, keyboard, and widget:

```text
G502 X Plus ---- shortcut key-down/key-up ----> Windows companion ==== USB or Bluetooth ====> Go60
                                                       ^                                      |
                                                       |=========== layer telemetry ===========|
                                                       |
                                                       +----> Bottom-left taskbar widget
```

A key combination emitted by the mouse is delivered to Windows; it is not sent directly from the mouse to the keyboard. The Windows companion detects the shortcut, applies the configured action, and sends the corresponding command to the Go60.

1. Create and maintain the keymap in the MoErgo Layout Editor.
2. Export the layout in ZMK `.keymap` format.
3. Pass the export to the ShinyGo60 tool.
4. Add the communication functionality while preserving the exported layout and custom behaviors.
5. Build one customized UF2 and manually flash it to both Go60 halves.
6. Run the Windows companion to receive keyboard status, display the widget, and handle mouse shortcuts.

Generated firmware files should not need to be edited by hand. Updating a layout should be a repeatable export, transform, build, and flash process.

## Input formats

| Format | Intended use |
| --- | --- |
| ZMK `.keymap` | The only version-one build input. |
| `.uf2` | Flashable build output or reference artifact. A compiled UF2 is not intended to be patched as an input. |

Existing MoErgo JSON exports may remain in the repository as layout snapshots, but the version-one builder will not accept or convert them.

## Communication model

Communication is bidirectional.

### Go60 to Windows

When the effective layer changes, the firmware sends its numeric layer ID to the Windows companion. The companion resolves the ID against the manifest generated from the same layout, allowing the widget to display a name such as `Navigation`, `Keypad`, or `Gaming`.

If per-half battery reporting passes its feasibility gate, battery updates will contain a separate value for each keyboard half. If it fails, battery messages and fields will not be part of the application protocol.

### Windows to Go60

The companion listens for configured shortcut events and sends the corresponding layer action to the firmware:

```text
Mouse button down -> shortcut down -> activate or select target layer
Mouse button up   -> shortcut up   -> release momentary activation, if configured
```

Releasing a momentary action should reveal the layer state that would otherwise be effective, including layer state produced by keys on the Go60 itself. A persistent “go to layer” action remains active until another action changes it.

### Initial protocol requirements

The first version of the protocol should support:

- Identifying its protocol version and matching layout manifest.
- Reporting the current effective layer on connection.
- Sending layer-change events.
- Selecting a persistent layer by stable ID.
- Pressing and releasing an externally controlled momentary layer.
- Acknowledging commands with the resulting current state.
- Rejecting malformed, unsupported, or mismatched-layout commands without disturbing normal keyboard input.

Battery fields will be added only after reliable left- and right-half reporting has been demonstrated.

The generated manifest and firmware should share a layout identifier so that reordered layers cannot silently cause Windows shortcuts to select the wrong layer.

The application protocol must work over both USB and Bluetooth while coexisting with normal keyboard input. The first feasibility implementation will evaluate USB CDC/ACM and an encrypted custom Bluetooth Low Energy GATT service, following the proven ZMK Studio transport pattern. Failure of the Bluetooth path blocks the version-one architecture rather than reducing it to a USB-only release.

## Planned components

### Firmware transformation and build tool

- Accept a MoErgo-exported `.keymap`.
- Preserve custom ZMK behaviors and configuration from the export.
- Generate a versioned layer manifest for the Windows companion.
- Add the custom ZMK integration needed for layer commands and status events.
- Produce reproducible Go60 firmware builds.
- Keep generated output separate from hand-maintained source files.

### Go60 firmware integration

- Observe and report effective ZMK layer changes.
- Determine whether reliable per-half battery readings are available during the feasibility milestone.
- Receive validated persistent and momentary layer commands.
- Serve the same protocol over USB and encrypted Bluetooth Low Energy transports.
- Make externally held layers cooperate with layers activated on the keyboard.
- Avoid affecting typing latency or existing key behaviors.
- Continue to function as a normal keyboard when the companion is not running.

### Windows companion

- Run on Windows 11.
- Connect to the customized Go60 firmware over USB or its existing Bluetooth connection.
- Track the active layer and, if retained after the feasibility gate, both battery readings.
- Detect configurable shortcut key-down and key-up events, including shortcuts sent by the G502 X Plus.
- Map shortcuts to momentary or persistent layer actions.
- Host and update the bottom-left status widget.
- Start with Windows when enabled.
- Reconnect cleanly after sleep, disconnects, or keyboard restarts.

### Taskbar widget

- Display the active layer as the primary value.
- Display left- and right-half battery percentages only if battery support passes its feasibility gate.
- Remain compact and avoid taking focus during normal use.
- Show a clear disconnected or stale state for the keyboard connection.
- Update promptly without polling more often than necessary.

## Repository layout

```text
ShinyGo60/
|-- Custom Firmware/       # Maintained ZMK module, build support, and ignored generated state
|-- Input/                 # Runtime .keymap drop folder
|-- Key Configuration/     # Current Layout Editor source exports
|   `-- Previous/          # Older configuration snapshots
|-- Windows/               # C# solution, WPF shells, shared contracts, and tests
|-- .gitignore
|-- DEVELOPMENT_PLAN.md
|-- IMPLEMENTATION_PLAN.md
|-- LICENSE
`-- README.md
```

`Custom Firmware/BuildSupport/Docker-v25.11` contains the pinned firmware backend. `Custom Firmware/Module` and every directory under `Windows` are maintained
source; `Custom Firmware/Generated`, `Output`, `bin`, and `obj` are disposable and ignored.

## Current status

The project has completed workspace scaffolding, built its first enabled firmware feature, and passed its hardware smoke test. The repository currently contains:

- The active MoErgo-exported `.keymap` for the first build fixture.
- A MoErgo Layout Editor JSON snapshot, retained as a reference rather than a version-one build input.
- A reference UF2 build, excluded from Git as generated firmware.
- Previous configuration snapshots.
- A pinned, byte-reproducible v25.11 baseline build and its recorded build evidence.
- A pinned, single-revision Go60 firmware image definition, offline build scripts, and scoped cleanup.
- A .NET 10 C# solution with shared protocol and diagnostic libraries, headless builder and companion cores, WPF application shells, and offline scaffold tests.
- An out-of-tree ZMK module with a central-only Step 5 diagnostic included through the supported MoErgo build hook.

The baseline UF2 has passed reproducible build, structural validation, and an initial hardware flash test. Its exact pins, hashes, measurements, and remaining
regression checklist are recorded in [Custom Firmware/BuildSupport/BASELINE_V25_11.md](Custom%20Firmware/BuildSupport/BASELINE_V25_11.md).

The selected Docker backend retains a 4.46 GB image and builds the same UF2 offline in 14.857 seconds. The future C# builder will pull this prebuilt environment rather
than constructing it locally. Measurements and cleanup details are recorded in
[Custom Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md](Custom%20Firmware/BuildSupport/STEP3_BUILD_ENVIRONMENT.md).

The first enabled module feature embeds a versioned test identity and internal checksum only in the left/central firmware. It creates no host transport and changes
no key behavior. The right/peripheral UF2 remains byte-for-byte identical to the tested baseline; the left image grows by 136 bytes of flash and 8 bytes of RAM.
The combined customized UF2 passed offline structural and isolation checks, was flashed to both halves, and was reported working by the user. A brief state that
initially appeared abnormal resolved without intervention and has not been confirmed as a persistent firmware fault. Step 5 evidence is recorded in
[Custom Firmware/BuildSupport/STEP5_MINIMAL_FEATURE.md](Custom%20Firmware/BuildSupport/STEP5_MINIMAL_FEATURE.md).

The C# shells and contracts compile, but the functional build pipeline, firmware communication layer, and companion behavior have not yet been implemented. Step 4
scaffold evidence is recorded in [Custom Firmware/BuildSupport/STEP4_SCAFFOLD.md](Custom%20Firmware/BuildSupport/STEP4_SCAFFOLD.md).

## Design principles

- Keep the MoErgo Layout Editor workflow intact.
- Treat the exported `.keymap` as input, not as manually maintained generated code.
- Keep mouse shortcuts and their layer actions configurable.
- Preserve normal keyboard behavior if Windows integration is unavailable.
- Drop battery support completely if reliable telemetry from both halves cannot be demonstrated.
- Keep the communication protocol small, versioned, and testable.
- Avoid relying on reverse engineering or modifying compiled UF2 files.
- Make builds reproducible so a layout update can be reprocessed without reapplying changes by hand.

## Initial milestones

1. Establish safe rollback and reproduce an unchanged, pinned Go60 firmware build.
2. Measure and select a practical local build environment with an accepted disk footprint. **Complete.**
3. Integrate a minimal firmware module and prove the same message round-trip over USB and encrypted Bluetooth Low Energy.
4. Inspect a `.keymap` and generate a versioned layer manifest shared by firmware and Windows.
5. Build the reliable headless keymap-to-UF2 pipeline and wrap it in a one-click C# builder.
6. Report effective layer changes and decide the separate left/right battery feasibility gate.
7. Add persistent and leased momentary layer commands with disconnect and crash safety.
8. Build the Windows 11 companion with configurable shortcut key-down/key-up handling.
9. Build and position the bottom-left taskbar widget.
10. Harden reconnects, sleep/wake, transport switching, flashing, rollback, packaging, and clean-machine setup.

## License

ShinyGo60's original code is available under the [MIT License](LICENSE). ZMK, Zephyr, MoErgo, and other third-party components remain subject to their own licenses and notices.
