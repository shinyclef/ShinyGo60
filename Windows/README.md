# ShinyGo60 Windows workspace

Companion 1.3.0 adds [automatic connection issue summaries and history](../Custom%20Firmware/BuildSupport/COMPANION_ISSUE_HISTORY.md).

Companion 1.2.0 adds configurable [adaptive Bluetooth latency](../Custom%20Firmware/BuildSupport/ADAPTIVE_BLUETOOTH_SETTINGS.md), paired with firmware 0.10.0 and protocol 1.3.

This workspace contains the Windows 11 tooling and shared application contracts:

| Project | Responsibility |
| --- | --- |
| `ShinyGo60.Diagnostics` | Metadata-only structured diagnostic events and JSON-lines output |
| `ShinyGo60.Protocol` | Messages, manifests, validation results, and transport contracts |
| `ShinyGo60.Builder.Core` | Go60 keymap inspection, layout artifacts, headless firmware pipeline, and process/workspace contracts |
| `ShinyGo60.BuildTool` | Command-line entry point for one explicit keymap-to-UF2 build |
| `ShinyGo60.Builder` | WPF shell for the eventual double-click firmware builder |
| `ShinyGo60.Companion.Core` | Connection sessions, manifest-backed layer/battery state, reconnect policy, and shortcut contracts |
| `ShinyGo60.Platform.Windows` | Windows 11 USB/Bluetooth transports, global shortcut capture, startup registration, and taskbar hosting |
| `ShinyGo60.Companion` | WPF configuration application and focusless taskbar status widget |
| `ShinyGo60.Tests` | Offline contract checks, parser fixtures, protocol vectors, and fake external boundaries |
| `ShinyGo60.TransportSpike` | USB/Bluetooth layer/battery snapshot and live-event diagnostic client |

The solution targets the SDK pinned in the repository's `global.json` and has no third-party NuGet references. The Windows-specific transport project may restore
the official Microsoft Windows SDK .NET targeting pack on its first build; later builds can reuse the local package cache.

Build everything from the repository root:

```powershell
dotnet build '.\Windows\ShinyGo60.sln' --configuration Release --maxcpucount:1
```

Run the offline checks:

```powershell
dotnet run --project '.\Windows\ShinyGo60.Tests\ShinyGo60.Tests.csproj' --configuration Release
```

After flashing the matching firmware, exercise both transports with five `Hello`, `GetState`, and `GetBattery` sessions each. Pass that build's manifest first:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\Step11\ShinyGo60-20260902-061754-3fd12c2c\layout-manifest.json' both 5
```

Use `usb`, `bluetooth`, or `switch` instead of `both` to isolate one path or test USB-to-Bluetooth-to-USB reconnection.

Watch resolved layer names and per-half battery state live with `watch-usb 240` or `watch-bluetooth 240` instead of `both 5`. Step 11 adds independent fresh,
stale, and unavailable battery readings while leaving all layer-control commands disabled. See
[`../Custom Firmware/BuildSupport/STEP11_BATTERY_FEASIBILITY.md`](../Custom%20Firmware/BuildSupport/STEP11_BATTERY_FEASIBILITY.md).

After flashing a matching Step 12 build, run the bounded persistent/momentary control diagnostic over one transport at a time. The final argument is a
non-default layer ID; layer 3 is `Navigation` in the current manifest:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\ShinyGo60-20260902-090655-3fd12c2c\layout-manifest.json' control-usb 3
```

Use `control-bluetooth 3` for Bluetooth. The diagnostic tests persistent selection/replacement, exact replay, momentary press/renew/release, and firmware lease
expiry, simultaneous owners, and session replacement. `control-switch 3` verifies held-state cleanup in both USB/Bluetooth handoff directions. Use `select-usb`
or `select-bluetooth` plus a layer ID to leave a runtime-persistent layer selected; layer ID 0 restores Home. The control diagnostic restores persistent Home
before testing momentary commands, and it attempts an emergency Home restore if the persistent phase is interrupted. `hold-usb` and `hold-bluetooth` maintain a
leased activation without replacing the current persistent selection for deliberate interruption and reboot tests. See
[`../Custom Firmware/BuildSupport/STEP12_LAYER_CONTROL.md`](../Custom%20Firmware/BuildSupport/STEP12_LAYER_CONTROL.md).

Run the Step 13 companion diagnostic with the manifest from the flashed firmware and a settings file. The checked-in example maps the real G502 `F23` input to
a momentary Navigation layer and prefers USB before Bluetooth:

```powershell
dotnet run --no-build --project '.\Windows\ShinyGo60.Companion\ShinyGo60.Companion.csproj' --configuration Release -- `
    '.\Output\ShinyGo60-20260902-090655-3fd12c2c\layout-manifest.json' `
    '.\Windows\companion-settings.example.json'
```

The app validates both files before installing its global shortcut hook. It displays the active transport, resolved layer ownership, per-half batteries, latest
shortcut route, reconnect detail, and log path. Daily JSON-lines diagnostics are appended under `%LOCALAPPDATA%\ShinyGo60\Logs`. A manual re-scan releases any
live momentary actions when possible, then starts automatic discovery from USB. Firmware leases remain the final safety bound if a transport has already failed.
See [`../Custom Firmware/BuildSupport/STEP13_HEADLESS_COMPANION.md`](../Custom%20Firmware/BuildSupport/STEP13_HEADLESS_COMPANION.md).

The Step 14 companion edits shortcut mappings, applies changes without restarting, and optionally registers an exact background command under the current
user's Windows startup key. The settings window can place its status widget on all taskbars or on one selected display. Each widget is a child of that display's
Windows 11 `Shell_TrayWnd` or `Shell_SecondaryTrayWnd`, not a topmost screen overlay, so the taskbar owns fullscreen visibility and z-order. A one-second
maintenance check only detects taskbar additions or Explorer replacement and reapplies taskbar-relative placement; status updates remain event-driven. Windows
must be configured to show a taskbar on each desired display. No driver, administrator access, hardware-monitor library, or high-frequency fullscreen polling is
used.
See [`../Custom Firmware/BuildSupport/STEP14_WINDOWS_EXPERIENCE.md`](../Custom%20Firmware/BuildSupport/STEP14_WINDOWS_EXPERIENCE.md).

Protocol 1.2 adaptively lowers the existing Bluetooth connection's peripheral latency while Windows is active and restores its power-saving value after 60
seconds of system inactivity or as soon as Windows locks. This uses the same paired connection and does not add another Bluetooth link. Interactive mode expires
after 90 seconds without companion traffic, and a normal shutdown requests power saving before the GATT client unsubscribes. Parameter negotiation is not tied
to momentary shortcut presses or releases. See
[`../Custom Firmware/BuildSupport/ADAPTIVE_BLUETOOTH_LATENCY.md`](../Custom%20Firmware/BuildSupport/ADAPTIVE_BLUETOOTH_LATENCY.md).

Step 9 locks the 20-byte protocol-v1 frame, layout negotiation, session ownership, layer-state messages, leased momentary commands, and structured errors. Step 11
extends that fixed frame to protocol 1.1, and adaptive Bluetooth control advances it to 1.2. The firmware and C# codecs consume the same fifteen golden byte
vectors. See
[`../Custom Firmware/BuildSupport/STEP9_PROTOCOL_V1.md`](../Custom%20Firmware/BuildSupport/STEP9_PROTOCOL_V1.md).

Step 7's `Go60KeymapInspector` validates exported metadata while treating all key behaviors as opaque bytes. `LayoutArtifactGenerator` copies those exact bytes and
writes `layout-manifest.json` plus `shinygo60_layout.h`; both carry the same deterministic layout identity. The shared `LayoutManifestJson` contract is used for
strict JSON writing and reading.

Layout identity now hashes the protocol version and ordered layer IDs/names, so changing bindings or export formatting does not invalidate the companion session.
The separate `keymapSha256` still identifies the exact source bytes for firmware verification. Moving from the old full-source identity requires one matching
manifest update and companion restart with the first firmware built this way. Subsequent binding-only flashes reconnect automatically; changing layer names,
order, count, or protocol still requires a matching manifest.

Step 15 wraps that pipeline in the WPF `ShinyGo60.Builder`. It discovers exactly one top-level `.keymap` in `Input`, accepts a file dropped onto the executable or
window, and prompts when multiple candidates exist. It preflights Docker Desktop, the exact pinned image, and a 1 GB working-space reserve; reports real pipeline
stages; cancels the exact build container; opens a successful atomic output set; and offers cleanup limited to GUID-named temporary folders and the isolated
`shinygo60-v25-11` construction cache. The cleanup preserves the installed image, successful outputs, and unrelated Docker state.

Publish the approximately 62 MB self-contained Windows x64 package with:

```powershell
& '.\Windows\Publish-Builder.ps1'
```

The resulting `artifacts\ShinyGo60 Builder\ShinyGo60.Builder.exe` requires Docker Desktop and the pinned image but no separately installed .NET runtime, Visual
Studio, Git, or Python. See
[`../Custom Firmware/BuildSupport/STEP15_ONE_CLICK_BUILDER.md`](../Custom%20Firmware/BuildSupport/STEP15_ONE_CLICK_BUILDER.md).

Step 8 connects those pieces to the pinned firmware environment. Run a development build with:

```powershell
dotnet run --project '.\Windows\ShinyGo60.BuildTool\ShinyGo60.BuildTool.csproj' --configuration Release -- '.\path\layout.keymap'
```

The tool requires the exact installed `shinygo60-builder:v25.11` image, disables container networking by default, uses a new clean workspace, embeds and verifies
the current identity, and atomically publishes a matched UF2, manifest, and compiler log. It never flashes the keyboard. See
[`../Custom Firmware/BuildSupport/STEP8_HEADLESS_PIPELINE.md`](../Custom%20Firmware/BuildSupport/STEP8_HEADLESS_PIPELINE.md) for the full contract and current
acceptance evidence. Step 8 passed two genuine network-disabled builds from the OneDrive project path on 2026-09-02.

Ordinary logs use one JSON object per line. Log event names, revisions, hashes, durations, sizes, exit codes, and sanitized error summaries. Never log keymap
contents, raw protocol payloads, pairing material, secrets, or stable device identifiers. Full compiler output belongs in the user-requested build log under the
ignored `Output` directory, not in ordinary companion diagnostics.

Companion 1.1.0 collects production firmware 0.9.0 connection history in the background over Bluetooth. It saves collection progress across restarts and pauses
diagnostic reads for control activity. Settings → Connection details → **Export diagnostics…** saves recent logs, firmware history, software versions, and
Bluetooth driver details to a local ZIP. Saving does not upload anything.

Publish the companion with a matching manifest and your settings:

```powershell
& '.\Windows\Publish-Companion.ps1' `
    -ManifestPath '.\Output\ProductionDiagnostics-20260908\layout-manifest.json' `
    -ConfigurationPath '.\Output\ProductionDiagnostics-20260908\companion-settings.json'
```

The script validates the staged self-contained package, replaces the installation without retaining old copies, and updates the workspace shortcut and any enabled startup entry.
Startup detects a different running version/build and either hands over from an older production version or displays a conflict that requires exiting that copy.
See the [production diagnostic guide](../Custom%20Firmware/BuildSupport/PRODUCTION_DIAGNOSTICS.md) for upgrade instructions, retention limits, and verification.
