# Step 4 workspace scaffold

Status: complete

Recorded: 2026-09-01 on Windows 11

## Windows foundation

The repository now pins .NET SDK `10.0.203` in `global.json`. `Windows/ShinyGo60.sln` contains seven projects with nullable reference types, deterministic builds,
recommended .NET analyzers, and warnings treated as errors.

| Project | Initial boundary |
| --- | --- |
| `ShinyGo60.Diagnostics` | Metadata-only structured events and a JSON-lines sink |
| `ShinyGo60.Protocol` | Message envelope, manifest, validation, and common USB/Bluetooth transport contracts |
| `ShinyGo60.Builder.Core` | Headless build, process runner, and generated-workspace contracts |
| `ShinyGo60.Builder` | Minimal WPF firmware-builder shell |
| `ShinyGo60.Companion.Core` | Session, shortcut, and reconnect contracts |
| `ShinyGo60.Companion` | Minimal WPF companion and widget-process shell |
| `ShinyGo60.Tests` | Offline checks, fake external boundaries, and fixture directories |

The scaffold deliberately has no third-party package dependencies. Restore, compilation, and its small executable test harness can run without downloading NuGet
packages. Ordinary diagnostic events are JSON objects containing metadata only; keymap contents, raw protocol payloads, pairing data, secrets, and stable device
identifiers are excluded by policy.

Verification:

- Release solution build: passed with zero warnings and zero errors.
- Offline scaffold harness: 5 of 5 checks passed.
- Covered boundaries: manifests, both transport kinds, fake process orchestration, companion shortcut contracts, and JSON diagnostic output.

## Source and generated-state boundaries

- `Custom Firmware/Module` is maintained firmware source.
- `Custom Firmware/BuildSupport` is maintained build integration and templates.
- `Windows` is maintained C# source.
- `Input` contains ignored runtime `.keymap` files except for its tracked instructions.
- `Custom Firmware/Generated` and `Output` are ignored disposable state.
- Parser fixtures and protocol-vector locations are tracked under `Windows/ShinyGo60.Tests/Fixtures`.

Deleting `Custom Firmware/Generated`, `Output`, and every `bin` or `obj` directory cannot remove maintained source.

## No-op firmware module verification

The out-of-tree module is discovered through `Custom Firmware/Module/zephyr/module.yml`. The pinned MoErgo Nix build receives the module through its supported
`extraModules` argument, which appears in the build as `ZMK_EXTRA_MODULES`. `Build-Firmware.ps1` mounts maintained module source read-only at
`/shinygo60-module`.

`CONFIG_SHINYGO60` defaults to disabled during Step 4. This proves module discovery and Kconfig/CMake integration without adding runtime behavior.

| Result | Value |
| --- | --- |
| Network access | Disabled |
| Build duration | 17.536 seconds |
| Combined UF2 size | 927,232 bytes |
| Combined UF2 SHA-256 | `2A953E1E9FDAF9171BB3687E4895316D8CC6EEA23068C49E66EB7A555BF4C109` |
| Step 2 hardware-tested baseline comparison | Byte-for-byte identical |
| Right flash/RAM | 190,116 bytes / 37,148 bytes |
| Left flash/RAM | 273,328 bytes / 64,640 bytes |

No Step 4 flash is needed because the module-aware result is identical to the firmware already tested. Step 5 deliberately enables the first central-only module
feature, produces the first changed UF2, records memory growth, and requires the next hardware flash.
