# ShinyGo60 Windows workspace

This workspace contains the Windows 11 tooling and shared application contracts:

| Project | Responsibility |
| --- | --- |
| `ShinyGo60.Diagnostics` | Metadata-only structured diagnostic events and JSON-lines output |
| `ShinyGo60.Protocol` | Messages, manifests, validation results, and transport contracts |
| `ShinyGo60.Builder.Core` | Go60 keymap inspection, layout artifacts, headless firmware pipeline, and process/workspace contracts |
| `ShinyGo60.BuildTool` | Command-line entry point for one explicit keymap-to-UF2 build |
| `ShinyGo60.Builder` | WPF shell for the eventual double-click firmware builder |
| `ShinyGo60.Companion.Core` | Connection sessions, manifest-backed layer state, reconnect policy, and shortcut contracts |
| `ShinyGo60.Companion` | WPF shell for configuration and the eventual taskbar-adjacent widget |
| `ShinyGo60.Tests` | Offline contract checks, parser fixtures, protocol vectors, and fake external boundaries |
| `ShinyGo60.TransportSpike` | USB/Bluetooth snapshot and live layer-event diagnostic client |

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

After flashing the matching firmware, exercise both transports with five `Hello` plus `GetState` sessions each. Pass that build's manifest first:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- `
    '.\Output\Step10\ShinyGo60-<timestamp>-<layout>\layout-manifest.json' both 5
```

Use `usb`, `bluetooth`, or `switch` instead of `both` to isolate one path or test USB-to-Bluetooth-to-USB reconnection.

Watch resolved layer names live for 60 seconds with `watch-usb 60` or `watch-bluetooth 60` instead of `both 5`. Step 10 adds the unsolicited transport path and
manifest-backed state tracker while leaving all layer-control commands disabled. See
[`../Custom Firmware/BuildSupport/STEP10_LAYER_TELEMETRY.md`](../Custom%20Firmware/BuildSupport/STEP10_LAYER_TELEMETRY.md).

Step 9 locks the 20-byte protocol-v1 frame, layout negotiation, session ownership, layer-state messages, leased momentary commands, and structured errors. The
firmware and C# codecs consume the same eleven golden byte vectors. See
[`../Custom Firmware/BuildSupport/STEP9_PROTOCOL_V1.md`](../Custom%20Firmware/BuildSupport/STEP9_PROTOCOL_V1.md).

Step 7's `Go60KeymapInspector` validates exported metadata while treating all key behaviors as opaque bytes. `LayoutArtifactGenerator` copies those exact bytes and
writes `layout-manifest.json` plus `shinygo60_layout.h`; both carry the same deterministic layout identity. The shared `LayoutManifestJson` contract is used for
strict JSON writing and reading.

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
