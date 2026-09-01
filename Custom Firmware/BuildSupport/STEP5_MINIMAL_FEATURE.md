# Step 5 minimal firmware feature

Status: complete; offline verification and hardware smoke test passed

Recorded: 2026-09-01 on Windows 11

## Feature scope

The first enabled ShinyGo60 firmware feature is intentionally inert. It provides firmware code with this diagnostic identity:

| Field | Value |
| --- | --- |
| Feature version | `0.1.0-step5` |
| Fixed test layout identifier | `00000000-0000-0000-0000-000000000005` |
| Diagnostic value | FNV-1a checksum of the version and layout identifier |

The diagnostic is compiled from the hand-maintained out-of-tree module, not inserted into the exported keymap. It does not expose a USB endpoint, advertise a
Bluetooth service, send host traffic, change a layer, or process key events. Step 6 can now add transport behavior on this tested foundation.

## Central-only verification

The build enables `CONFIG_SHINYGO60` for the combined firmware configuration. The internal `CONFIG_SHINYGO60_CENTRAL` selection is enabled only when building
`BOARD_GO60_LH`, which is the Go60 left/central image.

| Check | Left/central | Right/peripheral |
| --- | --- | --- |
| Board selection | `CONFIG_BOARD_GO60_LH=y` | `CONFIG_BOARD_GO60_RH=y` |
| Central role | Enabled | Disabled |
| ShinyGo60 runtime source compiled | Yes | No |
| Diagnostic symbol and identity in ELF | Present | Absent |
| Change from Step 4 UF2 | Expected central feature | Byte-for-byte identical |

The right UF2 SHA-256 is `C9390B7A5FD0F1CA01C39F44AD132AE4115FF7B942103B8E53AA7D7418B9386`, exactly matching the right segment extracted from the
Step 4 baseline. Because the right image contains no ShinyGo60 runtime code or identity, it cannot initiate or serve ShinyGo60 host protocol traffic.

## Reproducible build evidence

| Item | Result |
| --- | --- |
| MoErgo ZMK revision | `11454d23596afbdb06380a1125371b19ab65675c` |
| Builder image | `shinygo60-builder:v25.11` |
| Builder network | Disabled |
| Build duration | 17.974 seconds |
| Build result | Success, with no error lines |
| Combined UF2 structure | Valid; 1,812 512-byte blocks in two complete segments |
| Packaged artifact | `Output/Step5/go60-step5-v0.1.0.uf2` |
| Artifact size | 927,744 bytes |
| Artifact SHA-256 | `A0E474D264452237E85158F2A15D4A1BFB2CA8A4FDA090DCB2E71705404DC8BE` |
| Build log | `Output/Step5/build.log` |

The combined artifact is byte-for-byte equal to the separately built left UF2 followed by the separately built right UF2. It remains the one file that is flashed
to both halves.

## Memory comparison

| Image | Step 4 flash | Step 5 flash | Growth | Step 4 RAM | Step 5 RAM | Growth |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Left/central | 273,328 B | 273,464 B | 136 B | 64,640 B | 64,648 B | 8 B |
| Right/peripheral | 190,116 B | 190,116 B | 0 B | 37,148 B | 37,148 B | 0 B |

The combined file grows by one 512-byte UF2 block. That block-size change is expected even though the linked central feature adds only 136 bytes of used flash.

## Hardware result

The customized artifact was flashed to both halves. On 2026-09-01, the user reported that the build is working normally. Immediately after flashing, it initially
appeared that the keyboard might not be working; the supplied photo showed a temporary mixed per-key lighting state. The condition resolved, and the user reported
that the keyboard continued to work fine.

There is not enough evidence to classify that transient state as a firmware defect, so it is recorded as an observation rather than an open failure. If it recurs,
the next useful evidence will be its duration, which half was being flashed or restarted, the host connection in use, and whether input or only lighting was affected.

The user's working-build report passes the Step 5 hardware smoke gate. It does not claim that every detailed transport, inter-half, and rollback scenario was tested;
those item-by-item checks remain explicitly open under Step 2. Step 6, the mandatory USB and Bluetooth transport spike, is now next.
