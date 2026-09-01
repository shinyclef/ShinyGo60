# Step 3 build-environment decision

Status: complete and accepted

Recorded: 2026-09-01 on Windows 11

## Decision

Use the pinned, single-revision, Go60-only image defined in [Docker-v25.11](Docker-v25.11/README.md). Normal firmware builds use the installed image with Docker
networking disabled. The future C# builder will pull a published, digest-pinned copy; it will not construct the image on the user's machine unless an advanced
recovery workflow is selected.

This keeps persistent ShinyGo60 storage below the 6 GB target. Local image construction exceeded the provisional 8 GB cold-space target, so that operation is a
maintainer workflow with a documented 10 GB free-space requirement. The release's clean-machine test must measure the prebuilt registry pull before final packaging.

## Pins

| Item | Pin |
| --- | --- |
| MoErgo ZMK | `v25.11`, commit `11454d23596afbdb06380a1125371b19ab65675c` |
| Docker base | `nixpkgs/nix:nixos-23.11@sha256:11c1c37da85b27f1b47a7c0fdff8e3cf970cafaac623312dbcf243c84b8756dd` |
| Validated local image | `shinygo60-builder:v25.11` |
| Validated local image ID | `sha256:8c05b8af27498f7f42391fa408dfd841fbebfdc70f0d7766a280edd03db98720` |
| ARM cross-compiler | Arm GNU Toolchain `12.3.Rel1`, `arm-none-eabi-gcc 12.3.1` |

The local image ID is not a substitute for a registry digest. Publishing and pinning the release image digest remains packaging work.

## Official path versus selected image

| Measurement | Official multi-revision image | Selected single-revision image |
| --- | ---: | ---: |
| Docker content size | 1,074,166,368 bytes | 949,287,144 bytes |
| Docker unpacked image size | 4.94 GB | 4.46 GB |
| Unique size while the common base tag exists | 4.549 GB | 4.066 GB |
| `/nix/store` inside image | 3.4 GiB | 3.1 GiB |
| Firmware source inside image | 21 MiB full mirror | 27 MiB shallow `v25.11` checkout |
| Firmware revisions preloaded | Four | One |
| Cold image construction | Approximately 188 seconds | 187.73 seconds |
| Final offline firmware build | Not tested offline | 14.857 seconds |

The selected image saves about 480 MB of installed image storage and about 125 MB of Docker content. Its largest benefit is scope and predictability: only the exact
firmware revision used by ShinyGo60 is present, and normal builds need no network.

## Cold-build measurements

Image construction was measured with the dedicated Buildx builder `shinygo60-v25-11`, starting with an empty project build cache:

- builder network received: 985 MB, including registry and BuildKit protocol overhead;
- Nix-reported binary-cache payload: 459.70 MiB downloaded and 2,284.78 MiB unpacked;
- base layer transferred by BuildKit: 106.24 MB;
- Dockerfile frontend transferred by BuildKit: 11.98 MB;
- isolated BuildKit cache after construction: 4.496 GB;
- largest cache record: approximately 4.19 GB for the dependency/source construction layer;
- observed Windows drive free-space decrease while the completed image and construction cache coexisted: 9,143,857,152 bytes (8.52 GiB);
- retained image after cleanup: 4.46 GB; a representative UF2 and build log total approximately 1.04 MB.

The 949,287,144-byte Docker content size is the best local proxy for a prebuilt registry download, but registry transfer and peak clean-machine installation space
must be measured from the published image during release acceptance. Deleting data inside Docker Desktop makes the space reusable by Docker; its WSL virtual disk
file does not necessarily shrink immediately on the Windows drive.

## Toolchain and offline verification

With `--network none`, the selected image reported:

- `arm-none-eabi-gcc 12.3.1`;
- CMake 3.30.5;
- Ninja 1.12.1;
- Python 3.12.7;
- Devicetree compiler 1.7.2.

The final build explicitly disabled Nix substituters as well as Docker networking. It completed without cache lookup or network warnings.

| Result | Value |
| --- | --- |
| Input `.keymap` SHA-256 | `AB526E96C32048301990B09309BFAB7F2B6A1323CCBC07892AAC43DAB6C6B7F7` |
| Offline build duration | 14.857 seconds |
| Offline build after cache cleanup | 13.326 seconds |
| Output size | 927,232 bytes |
| Output SHA-256 | `2A953E1E9FDAF9171BB3687E4895316D8CC6EEA23068C49E66EB7A555BF4C109` |
| Comparison with Step 2 baseline | Byte-for-byte identical |

The Step 2 baseline with that same hash was flashed to the keyboard and reported working by the user. Because the selected environment produced identical bytes,
the replacement has the same hardware-tested firmware result without requiring another redundant flash.

## Resource ownership and cleanup

- The retained image and transient firmware-build containers use `io.shinygo60.managed=true` and role labels.
- The Buildx helper container and volume cannot receive custom labels through Buildx. They are isolated under the exact name `shinygo60-v25-11`.
- [Cleanup.ps1](Docker-v25.11/Cleanup.ps1) removes only that named builder and its dedicated cache by default.
- `-IncludeImage` additionally removes only `shinygo60-builder:v25.11`, after verifying its managed label.
- No project script performs global Docker pruning.

Cleanup verification removed the 4.496 GB isolated cache, its helper container, and its volume while leaving the selected image installed. The superseded official
baseline and intermediate project images were removed by their exact names or IDs. Unrelated Docker images, containers, volumes, and caches were not touched.

## Accepted footprint

- Ordinary user: 4.46 GB persistent installed image; prebuilt image pull selected for first setup.
- Maintainer constructing the image locally: require 10 GB free, then run scoped cleanup.
- Normal warm/offline firmware build: negligible persistent growth; a UF2 and representative log total approximately 1.04 MB.

This closes storage gate G2 for the selected architecture. The clean-machine release gate still verifies the published-image pull and reports its exact peak.
