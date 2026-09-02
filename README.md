# ShinyGo60

ShinyGo60 adds Windows 11 integration to the [MoErgo Go60](https://www.moergo.com/): mouse-driven layer controls, live layer and battery telemetry, and a compact taskbar widget.

Layouts remain owned by the MoErgo Layout Editor. ShinyGo60 takes an exported ZMK `.keymap`, adds its communication module, and produces a matched firmware image and Windows manifest. Mouse buttons work through ordinary configurable keyboard shortcuts, so no Logitech-specific software or API is required.

> [!WARNING]
> ShinyGo60 is a development preview for experienced Go60 and ZMK users. It modifies keyboard firmware, has no packaged installer, and still has outstanding physical acceptance checks. Keep a known-good Go60 firmware image and a spare keyboard available before flashing.

ShinyGo60 is an independent community project and is not affiliated with or endorsed by MoErgo.

## Features

- Activate a layer while a configured shortcut is held.
- Select a persistent layer with a configured shortcut.
- Report the effective ZMK layer over USB or encrypted, bonded Bluetooth Low Energy.
- Report separate left- and right-half battery state, including stale or unavailable readings.
- Display the current layer, connection, transport, and battery state in a focusless Windows 11 taskbar widget.
- Prefer USB, Bluetooth, or automatic transport selection.
- Preserve normal keyboard behavior when the Windows companion is not running.
- Build a validated UF2 and matching layout manifest reproducibly from one exported `.keymap`.

## Project status

The protocol 1.1 firmware, command-line build pipeline, USB/Bluetooth transport, layer control, per-half battery telemetry, Windows companion, configuration UI, and taskbar widget are implemented. Completed automated and hardware checks are recorded in the linked acceptance reports.

This repository is currently a **source release**, not a general end-user release:

| Component | Status |
| --- | --- |
| Command-line keymap-to-UF2 builder | Working and hardware-tested |
| Firmware protocol and USB/Bluetooth transports | Working and hardware-tested |
| Windows companion and shortcut editor | Working and hardware-tested |
| Taskbar widget | Implemented; final fullscreen and Explorer-lifecycle checks are in progress |
| Graphical firmware builder | Placeholder only; use the command-line builder |
| Installer and prebuilt firmware-build image | Not yet published |

See [Known limitations](#known-limitations) before building or flashing.

## How it works

The firmware build keeps the Layout Editor as the source of truth:

```text
MoErgo Layout Editor
        |
        | export .keymap
        v
ShinyGo60 build tool
        |
        | add the module and build
        v
Matched UF2 + layout manifest
        |
        v
Both Go60 halves
```

At runtime, the Windows companion joins shortcuts, keyboard commands, and telemetry:

```text
Mouse shortcut ----> Windows companion ==== USB or Bluetooth ====> Go60
                            ^                                      |
                            |========== layer and battery ==========|
                            |
                            +----> taskbar widget
```

The protocol binds the firmware and companion to the same deterministic layout identifier. A manifest from a different keymap is rejected rather than allowing a shortcut to target the wrong layer.

## Requirements

- A MoErgo Go60. Other ZMK keyboards are not supported.
- Windows 11 for the companion application.
- A Go60 `.keymap` exported from the [MoErgo Layout Editor](https://docs.moergo.com/layout-editor-guide/advanced-usage-export-import/). JSON layout backups are not accepted as build input.
- The [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows). `global.json` requests SDK `10.0.203` and permits later 10.0 patch releases.
- [Docker Desktop for Windows](https://docs.docker.com/desktop/setup/install/windows-install/) with Linux containers and Buildx.
- PowerShell and approximately 10 GB of free disk space while constructing the pinned firmware image. The retained image is approximately 4.46 GB.
- Internet access for the initial .NET restore, NuGet vulnerability-index refreshes, and Docker image construction. Normal firmware builds run with container networking disabled.

## Quick start

Run all commands in PowerShell from the repository root.

### 1. Build the pinned firmware environment

Start Docker Desktop, then construct the local `shinygo60-builder:v25.11` image:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Build-Image.ps1'
```

This fetches pinned inputs and may take some time on its first run. The image contains MoErgo ZMK v25.11 at the revision recorded in the [firmware-builder documentation](Custom%20Firmware/BuildSupport/Docker-v25.11/README.md).

### 2. Build firmware from a keymap

Export a Go60 layout as a `.keymap`, then pass its path to the command-line builder:

```powershell
dotnet run --project '.\Windows\ShinyGo60.BuildTool\ShinyGo60.BuildTool.csproj' --configuration Release -- `
    '.\path\to\layout.keymap'
```

The builder does not need a connected keyboard and never flashes one. A successful build prints the path to a new directory under `Output`. That directory contains one matched set:

```text
ShinyGo60-<timestamp>-<layout>/
|-- ShinyGo60-<layout>.uf2
|-- layout-manifest.json
`-- build.log
```

The builder validates the keymap, image metadata, both firmware segments, embedded layout identity, and complete UF2 structure before publishing the set. Failed or cancelled builds do not publish a successful UF2.

### 3. Flash both Go60 halves

Flash the **same UF2 from the new matched output set** to both halves. Follow MoErgo's [official Go60 firmware-loading instructions](https://docs.moergo.com/go60-user-guide/customizing-key-layout/#loading-new-zmk-firmware-onto-your-go60), including its recommendation to keep a spare keyboard available.

Do not mix halves built from different keymaps or ShinyGo60 versions. Keep `layout-manifest.json` with the exact UF2 that produced it.

### 4. Run the Windows companion

Create a local settings file, then set `$buildDirectory` to the output-set directory printed by the firmware build:

```powershell
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'ShinyGo60'
New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
$settingsPath = Join-Path $settingsDirectory 'companion-settings.json'
Copy-Item '.\Windows\companion-settings.example.json' $settingsPath
$buildDirectory = 'PATH_PRINTED_BY_THE_BUILD_COMMAND'
```

Launch the companion with the matched manifest and settings:

```powershell
dotnet run --project '.\Windows\ShinyGo60.Companion\ShinyGo60.Companion.csproj' --configuration Release -- `
    (Join-Path $buildDirectory 'layout-manifest.json') `
    $settingsPath
```

The settings window can capture a shortcut, choose momentary or persistent layer behavior, select a target layer, choose a transport preference, and enable start-with-Windows behavior. Saving applies changes without restarting the application.

For Bluetooth, pair the Go60 with Windows before starting the companion. Bluetooth commands require an encrypted connection and a stored firmware bond. Automatic transport selection prefers USB when both transports are available.

Companion diagnostics are written as JSON Lines under `%LOCALAPPDATA%\ShinyGo60\Logs`. They are designed not to contain keymap contents, raw protocol packets, pairing material, secrets, or stable device identifiers.

## Shortcut actions

Each shortcut maps to one action:

- **Momentary layer:** activates the target while the shortcut is held and releases that external activation when the shortcut is released. A short firmware lease prevents a lost key-up event or terminated companion from leaving the layer held indefinitely.
- **Go to layer:** selects the target persistently until another persistent or physical layer action changes it.

A mouse only needs to emit a keyboard shortcut that Windows can observe. The checked-in example uses `F23`, but shortcuts and target layers are configurable.

## Build and test

Exit any running ShinyGo60 companion instance before rebuilding; Windows locks the loaded application files. Then build the complete Windows solution:

```powershell
dotnet build '.\Windows\ShinyGo60.sln' --configuration Release --maxcpucount:1
```

Run the offline contract and integration checks:

```powershell
dotnet run --project '.\Windows\ShinyGo60.Tests\ShinyGo60.Tests.csproj' --configuration Release
```

The solution has no third-party NuGet dependencies. The first build may restore Microsoft's Windows SDK targeting pack; later builds can use the local package cache.

See [Windows/README.md](Windows/README.md) for transport diagnostics and lower-level development commands.

## Known limitations

- There is no installer, signed binary release, or published prebuilt Docker image yet. The current workflow builds and runs from source.
- The WPF graphical firmware builder is not functional; use `ShinyGo60.BuildTool`.
- The taskbar widget targets the primary Windows 11 taskbar. Multi-monitor placement and nonstandard taskbar-edge policy are not part of the current acceptance scope.
- Final taskbar-child checks for fullscreen transitions and Explorer replacement are still in progress. Taskbar parenting uses an established but unofficial Windows shell technique.
- Windows sleep/resume has not completed physical acceptance.
- USB-powered battery readings can saturate at 100%. The companion distinguishes current, stale, and unavailable values; battery accuracy was accepted for battery-powered Bluetooth use.
- Firmware input is limited to a MoErgo-exported Go60 `.keymap` and the pinned v25.11 backend.
- The most recent physical regression for returning from a companion-selected persistent layer through a keyboard `&to` binding remains pending.

Detailed acceptance evidence and exact test scope are recorded in the [Step 14 Windows experience report](Custom%20Firmware/BuildSupport/STEP14_WINDOWS_EXPERIENCE.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `Custom Firmware/Module` | Maintained ShinyGo60 ZMK module |
| `Custom Firmware/BuildSupport` | Pinned firmware environment, templates, scripts, and acceptance records |
| `Input` | Optional ignored drop folder for local `.keymap` files |
| `Key Configuration` | Reference Layout Editor exports used during development |
| `Windows` | C# solution, build tool, companion, shared protocol, diagnostics, and tests |
| `Output` | Ignored generated UF2, manifest, and build-log sets |

Generated firmware workspaces, build output, binaries, and local settings are ignored. Do not commit generated UF2 files or diagnostic logs without reviewing them deliberately.

## Documentation

- [Windows workspace and diagnostic commands](Windows/README.md)
- [Firmware workspace](Custom%20Firmware/README.md)
- [Firmware module](Custom%20Firmware/Module/README.md)
- [Implementation plan](IMPLEMENTATION_PLAN.md)
- [Development plan](DEVELOPMENT_PLAN.md)
- [Protocol 1.1 design and golden vectors](Custom%20Firmware/BuildSupport/STEP9_PROTOCOL_V1.md)
- [Headless firmware pipeline](Custom%20Firmware/BuildSupport/STEP8_HEADLESS_PIPELINE.md)
- [Layer telemetry](Custom%20Firmware/BuildSupport/STEP10_LAYER_TELEMETRY.md)
- [Battery feasibility results](Custom%20Firmware/BuildSupport/STEP11_BATTERY_FEASIBILITY.md)
- [Layer-control safety and recovery](Custom%20Firmware/BuildSupport/STEP12_LAYER_CONTROL.md)
- [Windows companion](Custom%20Firmware/BuildSupport/STEP13_HEADLESS_COMPANION.md)
- [Windows UI and taskbar widget](Custom%20Firmware/BuildSupport/STEP14_WINDOWS_EXPERIENCE.md)

## Contributing

Issues and focused pull requests are welcome. Keep maintained source separate from generated firmware state, preserve the exported keymap bytes, and run the Release build and offline checks before submitting a change. Hardware-dependent changes should state which USB, Bluetooth, split-keyboard, and recovery paths were physically tested.

## License

Original ShinyGo60 code is licensed under the [MIT License](LICENSE). ZMK, Zephyr, MoErgo sources, and other third-party components remain subject to their own licenses and notices.
