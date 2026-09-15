# Step 16 system hardening

Status: noninteractive build and short real-app measurements complete; long-duration and hardware-specific checks remain

Recorded: 2026-09-04 on Windows 11

## Scope and safety boundary

The first tranche was deliberately limited to checks that could not affect normal keyboard use. The ShinyGo60 companion, widget, builder UI, USB transport, and
Bluetooth GATT transport remained closed, and Docker Desktop was not started. After the user explicitly made Docker available and authorized a real-app
measurement, the pinned image performed one network-disabled firmware build and the current Release companion ran in background mode for a bounded sample.
No firmware was flashed, no Windows Bluetooth setting was changed, and no synthetic Windows input was generated.

The user's ordinary work with the companion absent continues to exercise the requirement that the Go60 remain a normal keyboard without ShinyGo60 software.

## Completed checks

| Check | Result |
| --- | --- |
| Running-process guard | No `ShinyGo60` process was present before or after the checks. |
| Release solution | All ten projects compiled with zero warnings and errors using one MSBuild worker and no shared compiler process. |
| Offline Windows harness | 15 of 15 suites passed. |
| Formatting | `dotnet format --verify-no-changes --no-restore` passed. |
| PowerShell sources | Every maintained `.ps1` file parsed without an error. |
| C# line limit | No maintained C# source line exceeded 160 characters. |
| Git whitespace | `git diff --check` passed. |
| Native C codec | All 15 shared frames and malformed cases passed under MSVC C11 with `/W4 /WX`. |
| Builder package | All required support files were present; no loose runtime DLL or runtime metadata file was present. |
| Bounded simulated soak | Five consecutive below-normal-priority harness processes passed all 15 suites and exited cleanly. |
| Network-disabled firmware build | The existing pinned image produced and verified a fresh matched output set in 21.31 seconds. |
| Release companion sample | A 91.34-second normal-use Bluetooth sample completed with low CPU use and bounded process resources. |
| Windows startup registration | The current-user Run entry was updated to the verified `214f19fd` manifest and read back exactly. |

The inspected builder executable was 64,923,879 bytes with SHA-256
`9B433569CCA61839025999413CD69CA276B457842E9ECC1A32460090A6763671`. Its existing keymap and three successful output sets remained in place; inspection did
not launch or modify the package.

## Added deterministic stress coverage

The new `Step 16 deterministic stress` suite runs entirely in memory and adds:

- every one-bit mutation of every byte in all 15 shared 20-byte protocol frames, for 2,400 mutations;
- 50,000 deterministic packets with a valid protocol header, known message type, and randomized payload;
- variable-length packet rejection from zero through 40 bytes;
- exact decode, encode, and second-decode stability for every mutated or randomized packet that remains valid;
- two-worker bursts of 20,000 out-of-order layer events and 20,000 out-of-order battery events, each converging on the highest revision;
- 5,000 layer-command sessions split between interruption before acknowledgement, quick release before acknowledgement, and transport loss while held; and
- 100 complete companion service cycles alternating fake USB and Bluetooth, including stop while held, loss and reconnect, fresh-press enforcement, release,
  and Bluetooth power-saving shutdown.

The full 15-suite Release run, including this stress suite, completed in 8.466 seconds. It found no defect and touched no operating-system input or keyboard
transport.

A bounded repeat-run soak then executed that complete harness five times. It covered another 500 fake companion lifecycles, 25,000 layer-command sessions,
100,000 layer events, and 100,000 battery events in 40.324 seconds. Across the five fresh processes, peak working set was 72.1-73.8 MiB, peak private memory
was 37.9-39.6 MiB, peak thread count was 17, and peak handle count was 268-270. Every process exited successfully, and no test or ShinyGo60 process remained.
These figures characterize the offline harness, not the packaged companion, and the fresh-process method is not a substitute for a multi-hour single-process
leak test.

## Network-disabled firmware build

Docker Desktop 29.6.1 reused `shinygo60-builder:v25.11` at image ID
`sha256:f5fedc1e224a672db76f4b345583545a9c3a3b7053dd55b4d30162f19639c446`. The source and builder-package keymaps were both 101,084 bytes with SHA-256
`AB526E96C32048301990B09309BFAB7F2B6A1323CCBC07892AAC43DAB6C6B7F7`.

The build completed in 21.31 seconds with container networking disabled and published `ShinyGo60-20260904-064830-214f19fd`. Its 958,464-byte UF2 has SHA-256
`74FCDD61FDDC7321096299F418784D915A071DA424617AF4EEA4C55C833246E8` and layout ID `sg60-v1-214f19fd7094b06306ad09a675ef3a88`. The existing pinned image ID
was unchanged; no dangling image or ShinyGo60 container remained. The UF2 is byte-for-byte identical to the previously accepted
`ShinyGo60-20260902-174213-214f19fd` build.

## Release companion resource sample

The current Release companion started with `--background`, the fresh matching manifest, and the existing user configuration. It first performed the expected
automatic USB discovery attempt, found no USB CDC endpoint, then established a validated Bluetooth session. During the sample, four real F23 press/release
pairs and all eight corresponding momentary-layer commands completed successfully without a reconnect or protocol error.

After a 15-second settle period, a 91.34-second sample recorded 0.8594 CPU seconds. That is 0.9408% of one logical core or approximately 0.0588% in the
whole-machine convention used by Task Manager on this 16-logical-processor computer. Working set started at 136.09 MiB, ended at 139.63 MiB, and peaked at
145.16 MiB. Private memory started at 67.24 MiB, ended at 68.04 MiB, and peaked at 73.61 MiB. Handles changed from 792 to 782 with a peak of 794; threads changed
from 33 to 27 with a peak of 33. The settings window stayed closed, and the measured instance was terminated and confirmed absent afterward. This short,
active-use sample supports low overhead but is not long enough to establish leak behavior or idle-only consumption.

The Windows startup registration audit initially found that the current `HKCU` Run entry still named the older `3fd12c2c` manifest while the current shortcut
and firmware use layout `214f19fd`. It was corrected to use `--background`, the current Release executable, the verified
`ShinyGo60-20260904-064830-214f19fd` manifest, and the existing companion settings. The registry value read back exactly, and those same executable arguments
had already completed the successful Bluetooth resource sample. The local Windows startup manager picked up the changed entry immediately: the companion
started in background mode, kept its settings window closed, and established a validated Bluetooth session. That test process was then stopped and confirmed
absent; the corrected Run entry remains registered.

## Prior evidence credited to Step 16

Earlier physical acceptance already covers several broad Step 16 rows:

- Step 13 tested the companion starting before the keyboard and connecting after the keyboard was already available.
- Step 11 tested a missing or powered-off right half, recovery, wireless split, TRRS split, and host-transport switching.
- Steps 10 through 13 repeatedly confirmed normal HID behavior while the companion was absent.
- Step 14's corrected firmware physically confirmed that `&to Home` clears a companion persistent selection.

These cases should not be repeated merely because Step 16 originally listed the complete matrix again.

## Remaining noninteractive work

- Run a longer single-process simulated soak with managed-heap snapshots if leak-specific evidence is needed.
- Measure a longer strictly idle interval separately if idle-only consumption is needed.
- Consolidate the complete verification matrix, linking each accepted row to its earlier or Step 16 evidence.

## Remaining hardware or observational work

- Quantify current adaptive-Bluetooth command latency and observe longer-term battery impact.
- Complete final taskbar-child fullscreen, focus, Explorer replacement, DPI, and selected-monitor checks.
- Test an additional Go60 host profile if one is available.
- Run the remaining companion termination scenarios against the physical keyboard.
- Record a longer Bluetooth-heavy normal-use soak. Windows sleep remains explicitly deferred on this test computer because it is unreliable.
