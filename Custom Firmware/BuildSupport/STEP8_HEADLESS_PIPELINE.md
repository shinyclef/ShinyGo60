# Step 8 headless firmware pipeline

Status: complete

Recorded: 2026-09-02 on Windows 11

## Purpose

Step 8 turns one explicit Go60 `.keymap` path into a matched, all-or-nothing output set. It is the reliable C# build core that the later double-click Windows
application will wrap; it does not flash the keyboard or require the keyboard to be connected.

## Development command

Run the headless tool from the repository root:

```powershell
dotnet run --project '.\Windows\ShinyGo60.BuildTool\ShinyGo60.BuildTool.csproj' --configuration Release -- `
    '.\Key Configuration\TailorKey v4.2m⁶ Bilateral - Gallium - Shinyclef.keymap'
```

Optional `--generated` and `--output` paths select the disposable workspace root and published output root. `--repository` is available when the command is
started outside the project. Container networking is disabled by default; `--allow-network` is an explicit maintainer option, not part of an ordinary cached
build.

The installed image must be exactly:

| Pin | Value |
| --- | --- |
| Docker tag | `shinygo60-builder:v25.11` |
| Base image digest | `nixpkgs/nix:nixos-23.11@sha256:11c1c37da85b27f1b47a7c0fdff8e3cf970cafaac623312dbcf243c84b8756dd` |
| MoErgo ZMK commit | `11454d23596afbdb06380a1125371b19ab65675c` |

The pipeline verifies the image's ShinyGo60 ownership and role labels, ZMK tag, exact source revision, and records the local image ID before running it. Local
construction IDs can vary with BuildKit export metadata and therefore are evidence, not a portable content pin. A registry digest will become the distribution
pin when the prebuilt image is published.

## Build and publication contract

For every invocation, the pipeline:

1. Validates and hashes the exact input before creating build state.
2. Creates a new GUID-named workspace beneath the configured generated root.
3. Copies the tracked v25.11 template and the exact keymap bytes into that clean workspace.
4. Generates the versioned manifest and appends its layout ID and full keymap hash to the firmware Kconfig.
5. Mounts only that workspace and the maintained firmware module into the pinned container using direct process arguments, never a shell command string.
6. Requires a newly created `go60.uf2` from the clean workspace.
7. Validates all 512-byte UF2 blocks, both complete Go60 firmware segments, and the embedded current layout ID and keymap hash.
8. Stages the UF2, manifest, and complete compiler log together, revalidates the staged copies, removes the disposable workspace, and atomically renames the
   directory to its final `ShinyGo60-<timestamp>-<layout>` name.

A successful directory contains only the matched artifacts for that invocation:

```text
ShinyGo60-<timestamp>-<layout>/
|-- ShinyGo60-<layout>.uf2
|-- layout-manifest.json
`-- build.log
```

Repeated builds publish separate sets and never overwrite a known-good result. Failed or canceled builds publish no successful set and no UF2 under a successful
name. A readable `.failed.log` is retained under `Output/Failures` when possible. Cleanup accepts only exact GUID workspaces, staging directories, and named build
containers owned by this invocation.

## Verification completed

- The nine-project Release solution builds with zero warnings and zero errors.
- All 7 offline checks pass, including the atomic keymap-to-UF2 pipeline suite.
- The suite covers success, repeated builds, compiler failure, false success without a fresh UF2, cancellation, image-metadata substitution, and preservation of
  an existing known-good output.
- A simulated container writes a structurally valid two-segment UF2 from paths containing spaces, Unicode, and a long directory name. The validator rejects output
  unless it contains the generated current layout ID and keymap hash.
- Docker receives every path through `ProcessStartInfo.ArgumentList`; the Unicode workspace mount remains one unsplit argument.
- `dotnet format --verify-no-changes` passes, and the changed C# lines remain within the repository's 160-character limit.
- The missing image preflight produced only a failure log and no UF2. Reconstructing the same pinned sources produced local image ID
  `sha256:efc1cd8775ef49246130c2841cc06638f3e243caffd29c3d587292423706bb41`; Docker reports its retained size as 4.46 GB.
- The first genuine compiler run exposed Kconfig identity fields that were not directly configurable. The pipeline retained its compiler log and published no
  UF2. Both fields are now configurable whenever the common ShinyGo60 configuration is enabled, while runtime code remains central-only.
- The next genuine run compiled and validated the firmware but exposed OneDrive read-only placeholder directories during cleanup. It also published no successful
  output. Cleanup now clears only the read-only attribute inside the exact managed GUID directory and deletes its tree bottom-up.

## Genuine acceptance result

Two complete builds ran from the OneDrive project path with container networking disabled. Both used the current 101,084-byte keymap and produced separate atomic
output sets with these identical firmware measurements:

| Measurement | Value |
| --- | --- |
| Keymap SHA-256 | `ab526e96c32048301990b09309bfab7f2b6a1323ccbc07892aac43dab6c6b7f7` |
| Layout identifier | `sg60-v1-b4c690cedfc730f31f0dbfb696b59779` |
| Combined UF2 size | 937,984 bytes |
| UF2 blocks / segments | 1,832 / 2 |
| UF2 SHA-256 | `9900fdcdb44fdc0343a45e7a785e866935f692bf3053e61d5ce7faacc937e468` |
| First build duration | 14.65 seconds |
| Repeat build duration | 14.89 seconds |
| Left flash / RAM | 278,624 / 68,720 bytes |
| Right flash / RAM | 190,116 / 37,148 bytes |
| Residual workspaces / stages | 0 / 0 |

The isolated `shinygo60-v25-11` Buildx builder and its construction cache were removed after acceptance; the validated 4.46 GB image remains installed. The newest
matched set is `Output/Step8/ShinyGo60-20260902-023216-b4c690ce`. Its UF2 is ready for the normal manual flash process on both halves.
