# Go60 v25.11 baseline build

Status: build reproducibility and hardware smoke test passed; detailed verification pending

Recorded: 2026-09-01 on Windows 11

This baseline proves that the active Layout Editor export builds through the official MoErgo path before any ShinyGo60 firmware code is added. The `.keymap` was copied byte-for-byte. The separate Layout Editor setting found in the matching JSON backup was preserved in `go60.conf`.

## Pinned inputs

| Item | Pin |
| --- | --- |
| MoErgo ZMK | `https://github.com/moergo-sc/zmk`, commit `11454d23596afbdb06380a1125371b19ab65675c` (tag `v25.11`) |
| Official Go60 config template | `https://github.com/moergo-keyboards/go60-zmk-config`, commit `8ccc8543191a95d6d676032ecb9a834634bda18e` |
| Docker base | `nixpkgs/nix:nixos-23.11@sha256:11c1c37da85b27f1b47a7c0fdff8e3cf970cafaac623312dbcf243c84b8756dd` |
| Locally built image | `shinygo60-baseline:v25.11`, image ID `sha256:cc0d9be01ab96780308d39025ffcff1999ddd43e5755d3b4b720ef0c889b4c26` |
| Docker engine | Client and server 29.6.1; `overlayfs` storage driver |
| Extra firmware configuration | `CONFIG_ZMK_HID_CONSUMER_REPORT_USAGES_FULL=y` |

The source pin was verified directly from the `v25.11` tag. Its Kconfig defines `ZMK_HID_CONSUMER_REPORT_USAGES_FULL` as the full consumer HID usage option, which is the ZMK equivalent of the JSON backup's `HID_FULL_CONSUMER_REPORT=y` setting.

The official baseline image was removed by its exact project image name after Step 3 proved the smaller replacement byte-for-byte. Its pins and reproduction
procedure remain here; the baseline UF2 and build evidence were retained.

## Representative input

- File: `Key Configuration/TailorKey v4.2m⁶ Bilateral - Gallium - Shinyclef.keymap`
- Size: 101,084 bytes
- SHA-256: `AB526E96C32048301990B09309BFAB7F2B6A1323CCBC07892AAC43DAB6C6B7F7`
- The source and copied build-workspace hashes matched exactly.
- No ShinyGo60 behavior, protocol, telemetry, or communication code was present.

## Reproduction procedure

1. Clone the official Go60 config template and check out template commit `8ccc8543191a95d6d676032ecb9a834634bda18e`.
2. Replace its `config/go60.keymap` with the representative keymap without transforming it.
3. Put the following single line in `config/go60.conf`:

   ```text
   CONFIG_ZMK_HID_CONSUMER_REPORT_USAGES_FULL=y
   ```

4. Replace the Dockerfile's first line with the pinned base reference from the table above.
5. Build the image from the template root:

   ```powershell
   docker build --progress=plain --tag shinygo60-baseline:v25.11 .
   ```

6. Run the build with the immutable ZMK commit rather than the movable tag:

   ```powershell
   $baselinePath = (Resolve-Path -LiteralPath 'Custom Firmware\Generated\Baseline-v25.11').Path
   docker run --rm --mount "type=bind,source=$baselinePath,target=/config" -e UID=0 -e GID=0 `
       -e BRANCH=11454d23596afbdb06380a1125371b19ab65675c shinygo60-baseline:v25.11
   ```

The official Nix expression builds `go60_lh` and `go60_rh` separately and combines them into one `go60.uf2`. The same combined UF2 is intended for both halves.

## Build result

| Result | Value |
| --- | --- |
| Packaged artifact | `Output/Baseline-v25.11/go60-baseline-v25.11.uf2` |
| Artifact size | 927,232 bytes |
| Artifact SHA-256 | `2A953E1E9FDAF9171BB3687E4895316D8CC6EEA23068C49E66EB7A555BF4C109` |
| Warm build log | `Output/Baseline-v25.11/build.log` |
| First and second build | Byte-for-byte identical |
| Right firmware flash use | 190,116 of 811,008 bytes (23.44%) |
| Right firmware RAM use | 37,148 of 262,144 bytes (14.17%) |
| Left firmware flash use | 273,328 of 811,008 bytes (33.70%) |
| Left firmware RAM use | 64,640 of 262,144 bytes (24.66%) |

The combined UF2 validator found:

- 1,811 512-byte UF2 blocks and exactly two segments;
- valid start and end magic in every block;
- no invalid payload sizes or block-sequence errors;
- 1,068 blocks for Go60 family `0x9809B007`;
- 743 blocks for Go60 family `0x980AB007`.

The known-good reference UF2 is 926,720 bytes and has 1,810 structurally valid blocks: the first segment is the same length, while the second is one 256-byte payload block smaller. Different firmware inputs are not expected to have matching hashes; this comparison is recorded only as a structural sanity check. Hardware behavior remains the decisive test.

## Observed timings and storage

| Measurement | Observation |
| --- | --- |
| Base-image pull | 14.317 seconds |
| Cold image construction | Approximately 188 seconds from BuildKit stage timings |
| First firmware compile | 16.243 seconds |
| Approximate cold total | 219 seconds, excluding Docker startup and the small Git clones |
| Warm firmware build with full log capture | 22.444 seconds |
| Built image virtual size | 1,074,166,368 bytes |

Before the base pull, `docker system df` reported 19.29 GB of images and 226.2 GB of build cache across the whole machine. After image construction it reported 24.23 GB of images and 230.9 GB of build cache. Those category deltas are not an exclusive physical-size measurement because Docker shares and can double-count layers. Step 3 must replace them with a scoped footprint measurement and a Go60-only build environment; no global Docker pruning is permitted.

## Non-fatal build warnings

The build completed with exit code 0. Its log contains deprecation notices for keymap `label` properties, linker warnings about RWX load segments, and Nix `patchelf` notices for statically linked firmware ELF files. These came from the pinned official build and did not prevent either half or the combined UF2 from being produced.

## Hardware regression checklist

Flash `Output/Baseline-v25.11/go60-baseline-v25.11.uf2` to both halves, then record pass or fail for:

- ordinary typing from every row on both halves;
- normal layer changes and return to the home layer;
- representative macros, combos, and hold-taps from the active layout;
- both touchpads and their expected pointer/scroll actions;
- normal USB host use;
- normal Bluetooth host use on Windows 11;
- BLE inter-half operation without TRRS;
- TRRS inter-half operation;
- restoration and verification of the known-good v25.11 rollback UF2 after the baseline test.

The user reported that the locally built UF2 works after flashing on 2026-09-01. This is the successful hardware smoke test for the baseline. Gate G1 remains open until the detailed transport, inter-half, behavior-regression, and rollback checks above are explicitly recorded.
