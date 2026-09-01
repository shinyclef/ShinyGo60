# ShinyGo60 Windows workspace

This workspace contains the Windows 11 tooling and shared application contracts:

| Project | Responsibility |
| --- | --- |
| `ShinyGo60.Diagnostics` | Metadata-only structured diagnostic events and JSON-lines output |
| `ShinyGo60.Protocol` | Messages, manifests, validation results, and transport contracts |
| `ShinyGo60.Builder.Core` | Headless firmware pipeline, process orchestration, and generated-workspace contracts |
| `ShinyGo60.Builder` | WPF shell for the eventual double-click firmware builder |
| `ShinyGo60.Companion.Core` | Connection sessions, reconnect policy, and shortcut contracts |
| `ShinyGo60.Companion` | WPF shell for configuration and the eventual taskbar-adjacent widget |
| `ShinyGo60.Tests` | Offline scaffold checks, parser fixtures, protocol vectors, and fake external boundaries |

The solution targets the SDK pinned in the repository's `global.json`. It has no third-party package dependencies at this stage, so restore and tests can run
without downloading packages.

Build everything from the repository root:

```powershell
dotnet build '.\Windows\ShinyGo60.sln' --configuration Release --maxcpucount:1
```

Run the scaffold checks:

```powershell
dotnet run --project '.\Windows\ShinyGo60.Tests\ShinyGo60.Tests.csproj' --configuration Release
```

Ordinary logs use one JSON object per line. Log event names, revisions, hashes, durations, sizes, exit codes, and sanitized error summaries. Never log keymap
contents, raw protocol payloads, pairing material, secrets, or stable device identifiers. Full compiler output belongs in the user-requested build log under the
ignored `Output` directory, not in ordinary companion diagnostics.
