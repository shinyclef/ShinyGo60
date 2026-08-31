# ShinyGo60

Custom firmware tooling and Windows integration for the [MoErgo Go60](https://www.moergo.com/).

The project extends layouts created with the MoErgo Layout Editor with mouse-driven layer controls, keyboard status reporting, and an at-a-glance Windows widget. The editor remains the source of truth for key mappings and ordinary ZMK behaviors.

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

The widget should be unobtrusive, readable at a glance, and able to start with Windows. The exact implementation—native taskbar integration or a borderless taskbar-adjacent window—will be selected after prototyping, while preserving the intended bottom-left position and behavior.

## Intended workflow

The firmware build path is:

```text
MoErgo Layout Editor
        |
        | export .keymap or .json
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
G502 X Plus ---- shortcut key-down/key-up ----> Windows companion ---- layer command ----> Go60
                                                       ^                                   |
                                                       |--------- layer telemetry ---------|
                                                       |
                                                       +----> Bottom-left taskbar widget
```

A key combination emitted by the mouse is delivered to Windows; it is not sent directly from the mouse to the keyboard. The Windows companion detects the shortcut, applies the configured action, and sends the corresponding command to the Go60.

1. Create and maintain the keymap in the MoErgo Layout Editor.
2. Export the layout in ZMK `.keymap` format or as JSON.
3. Pass the export to the ShinyGo60 tool.
4. Add the communication functionality while preserving the exported layout and custom behaviors.
5. Build a customized UF2 and flash it to the Go60.
6. Run the Windows companion to receive keyboard status, display the widget, and handle mouse shortcuts.

Generated firmware files should not need to be edited by hand. Updating a layout should be a repeatable export, transform, build, and flash process.

## Input formats

| Format | Intended use |
| --- | --- |
| ZMK `.keymap` | Preferred firmware source when available. |
| MoErgo `.json` | Layout Editor source containing layer names, bindings, custom behaviors, and configuration. The tool will extract or convert the data it needs. |
| `.uf2` | Flashable build output or reference artifact. A compiled UF2 is not intended to be patched as an input. |

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

The transport between Windows and the Go60 is still to be selected. It must coexist with normal keyboard input. USB support is the first implementation target; Bluetooth support will depend on the transport and the interfaces available in the Go60 ZMK distribution.

## Planned components

### Firmware transformation and build tool

- Accept a `.keymap` or MoErgo JSON export.
- Preserve custom ZMK behaviors and configuration from the export.
- Generate a versioned layer manifest for the Windows companion.
- Add the custom ZMK integration needed for layer commands and status events.
- Produce reproducible Go60 firmware builds.
- Keep generated output separate from hand-maintained source files.

### Go60 firmware integration

- Observe and report effective ZMK layer changes.
- Determine whether reliable per-half battery readings are available during the feasibility milestone.
- Receive validated persistent and momentary layer commands.
- Make externally held layers cooperate with layers activated on the keyboard.
- Avoid affecting typing latency or existing key behaviors.
- Continue to function as a normal keyboard when the companion is not running.

### Windows companion

- Connect to the customized Go60 firmware.
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
|-- Custom Firmware/       # Custom ZMK integration and generated build workspace
|-- Key Configuration/     # Current Layout Editor source exports
|   `-- Previous/          # Older configuration snapshots
|-- .gitignore
|-- LICENSE
`-- README.md
```

Additional tool and Windows companion directories will be added as their implementations are established.

## Current status

The project is in its design and scaffolding stage. The repository currently contains:

- The active MoErgo Layout Editor JSON export.
- A reference UF2 build, excluded from Git as generated firmware.
- Previous configuration snapshots.
- An empty workspace for the custom firmware implementation.

The transformation/build tool, firmware communication layer, and Windows companion have not yet been implemented.

## Design principles

- Keep the MoErgo Layout Editor workflow intact.
- Treat exported layout data as input, not as manually maintained generated code.
- Keep mouse shortcuts and their layer actions configurable.
- Preserve normal keyboard behavior if Windows integration is unavailable.
- Drop battery support completely if reliable telemetry from both halves cannot be demonstrated.
- Keep the communication protocol small, versioned, and testable.
- Avoid relying on reverse engineering or modifying compiled UF2 files.
- Make builds reproducible so a layout update can be reprocessed without reapplying changes by hand.

## Initial milestones

1. Confirm the supported Go60 ZMK build environment and select the USB communication transport.
2. Determine how to read the effective layer and test whether both halves' battery state can be reported reliably. Keep or drop battery support at this gate.
3. Parse a Layout Editor export and generate a versioned layer manifest.
4. Build and flash firmware that reports layer changes and, only if retained, battery telemetry.
5. Create a minimal Windows companion that displays the received state.
6. Add persistent layer commands triggered by configurable shortcuts.
7. Add key-down/key-up handling for momentary mouse-held layers.
8. Build and position the bottom-left taskbar widget.
9. Add reconnect, stale-state, startup, and packaging behavior.
10. Investigate Bluetooth support after the USB path is reliable.

## License

ShinyGo60's original code is available under the [MIT License](LICENSE). ZMK, Zephyr, MoErgo, and other third-party components remain subject to their own licenses and notices.
