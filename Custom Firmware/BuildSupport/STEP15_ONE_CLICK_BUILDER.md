# Step 15 one-click firmware builder

Status: implementation, automated verification, self-contained startup, and genuine GUI build complete; visual and clean-account acceptance pending

Recorded: 2026-09-03 on Windows 11

## What it does

`ShinyGo60.Builder.exe` turns one complete MoErgo-exported Go60 `.keymap` into one matched output set:

```text
Output/ShinyGo60-<timestamp>-<layout>/
|-- ShinyGo60-<layout>.uf2
|-- layout-manifest.json
`-- build.log
```

The exact keymap bytes are preserved. The builder adds the maintained ShinyGo60 module, builds both keyboard halves with the pinned MoErgo v25.11 environment,
validates both UF2 segments and the embedded layout identity, then publishes all three files together. It never flashes the keyboard.

## Normal use

1. Install and start Docker Desktop.
2. Keep the supplied `ShinyGo60 Builder` folder together.
3. Put exactly one exported `.keymap` in its `Input` folder.
4. Double-click `ShinyGo60.Builder.exe`.
5. Wait for the newly created output folder to open.
6. Flash the same `.uf2` manually to both Go60 halves.

A `.keymap` can instead be dropped onto the executable or the open builder window. If `Input` contains more than one keymap, the builder opens a file-selection
prompt and does not guess which layout was intended.

## Prerequisites and storage

- Windows 11.
- Docker Desktop installed and running.
- The pinned `shinygo60-builder:v25.11` image, validated by its ShinyGo60 ownership, role, version, and exact ZMK revision labels.
- At least 1 GB free on every drive used for temporary and published files.

The validated image is approximately 4.46 GB unpacked. A normal warm build takes roughly 15–30 seconds and leaves only the approximately 1 MB matched output
after its temporary workspace is removed. The executable includes its own .NET runtime and does not require Visual Studio, Git, Python, or a separately installed
.NET runtime.

A prebuilt registry image has not been published during development. Until its immutable release digest is recorded, `Setup help` opens the contained advanced
local-construction instructions. Local construction needs approximately 10 GB free temporarily. The builder never silently reconstructs, replaces, prunes, or
retags an image; this avoids turning a still-useful image into an unexpected dangling image.

## Failure and cancellation behavior

Compiler details are kept out of the main window and written to `build.log`. A failed or canceled run cannot publish a successful-looking UF2. When possible, a
diagnostic `.failed.log` remains under `Output/Failures`; the UI links to it after a failure. Existing successful output sets are never overwritten.

`Cancel` stops the exact named build container and removes that invocation's temporary workspace. Closing the window during a build asks before doing the same.

## Scoped cleanup

`Clean cache` can remove only:

- abandoned GUID-named ShinyGo60 workspaces;
- incomplete GUID-named ShinyGo60 output stages; and
- the exact isolated Buildx construction cache named `shinygo60-v25-11`, when it exists.

It preserves `shinygo60-builder:v25.11`, every successful output set, and all unrelated Docker resources. It never invokes a global Docker prune.

Running `Windows/Publish-Builder.ps1` again updates only packaged program and support files. It refuses to run while that packaged executable is open and leaves
local `.keymap` files in `Input` plus every generated set in `Output` in place.

## Verification

- The complete Debug solution builds with zero warnings and errors.
- All 14 offline Windows suites pass.
- Step 15 tests cover case-insensitive top-level `.keymap` discovery, deterministic multiple-input ordering, packaged support-file discovery, Docker stopped or
  missing, image missing or substituted, insufficient working space, every ordered UI progress stage, and exact cleanup boundaries.
- The earlier Step 8 tests continue to cover atomic success, repeat builds, compiler failure, false success without a fresh UF2, cancellation, Unicode and long
  paths, and preservation of known-good output.
- `Windows/Publish-Builder.ps1` produced a 64,923,879-byte single-file Windows x64 executable. The complete package was 62.05 MB before local input and output
  files were added.
- The packaged executable reached an idle WPF window with `DOTNET_ROOT` pointed at a nonexistent directory and external runtime lookup disabled.
- A packaged one-input launch reused installed image `sha256:f5fedc1e224a672db76f4b345583545a9c3a3b7053dd55b4d30162f19639c446` and completed a genuine
  network-disabled build in 19.558 seconds. It opened a matched set containing a 958,464-byte, 1,872-block, two-segment UF2 with layout ID
  `sg60-v1-214f19fd7094b06306ad09a675ef3a88` and SHA-256 `74fcdd61fddc7321096299f418784d915a071da424617af4eea4c55c833246e8`.
- The genuine GUI run left zero temporary workspaces and stages. Its newest Docker dangling-image timestamp remained 2026-09-01, confirming that the normal build
  did not construct or replace an image.

Remaining acceptance consists of user-visible interaction checks, a separate Windows account without development tools, and publishing/measuring the
digest-pinned registry image before a general release.
