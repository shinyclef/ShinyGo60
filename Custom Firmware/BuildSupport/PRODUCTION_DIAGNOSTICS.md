# Production connection diagnostics

Firmware 0.10.1 and companion 1.2.0 provide local connection history over Bluetooth.
USB is needed for flashing; recording and collection work without it afterward.

## Firmware 0.10.1 retention update

Flash the new 0.10.1 combined UF2 to both halves. Companion 1.2.0 remains compatible:
the control protocol is still 1.3 and diagnostic format is still 2. When the input
keymap is unchanged, the layout manifest is unchanged too.

Critical storage now retains up to **224 distinct events**: the first **32
uncollected failures**, protected from overwriting, plus the latest **192 failures**.
These sets overlap until enough events have occurred. Routine storage retains the
latest **128 events**. Repeated reconnect failures can overwrite the middle of a
long outage, but cannot erase its protected beginning.

Protected slots are released only when a subsequent same-boot collection request
acknowledges those sequence numbers. Merely starting a download, disconnecting,
or interrupting collection does not release them. As slots become available, new
failures are protected. This preserves the beginning of uncollected failure activity;
it does not try to infer distinct outages from a quiet-time threshold. Multiple
outages without successful collection share the same finite protected storage.

If the protected records and recent records have an overwrite gap, the firmware
sends them in separate downloads so each format-2 stream remains contiguous. The
companion saves the initial failures first and reports the gap when it reaches the
recent records. Larger histories can take multiple background collections. Records
already downloaded are skipped using the existing cursor. This update adds no
flash writes, periodic firmware tasks, or changes to Bluetooth latency settings.

Recorder regression tests compile the production C recorder against fake driver
boundaries: `sh Custom\ Firmware/Module/tests/history/run.sh [path-to-cc]`.

## Original 0.9.0 installation reference

1. Exit the running companion using its settings window's Exit button.
2. Flash `Output/ProductionDiagnostics-20260908/Go60-0.9.0.uf2` to both halves.
   The combined file contains the image for each half. Unplug USB afterward and
   select Bluetooth keyboard output.
3. Open the workspace's `ShinyGo60 Companion.lnk`. It now targets companion 1.1.0
   in `artifacts/ShinyGo60 Companion`, with the matching manifest and your existing
   Bluetooth/F23 Keypad configuration. Connection details shows the app version.
4. Use the keyboard normally. After a drop, note the time and whether it affected
   both halves, only the right, or only the companion. Leave the keyboard powered
   on so retained history can be collected after Bluetooth reconnects.
5. Open settings, expand Connection details, and choose **Export diagnostics…**.
   Choose a local ZIP destination. Saving never uploads the file.

The 0.8.3 experimental firmware uses an earlier diagnostic format. Upgrade both
firmware and companion together to collect production history. The previous
wireless companion has been left running for the currently flashed 0.8.3 firmware;
the production firmware has been built and packaged but has not been flashed.

## Recording and collection

- Firmware 0.9.0 enables connection diagnostics by default under SHINYGO60.
- Firmware 0.10.1 retains up to 224 critical and 128 routine events as described above;
  older 0.9.0/0.10.0 firmware retained 64 critical and 32 routine events. Disconnects,
  security/connection failures, queue/indication failures, and failed parameter
  requests use critical storage. EALREADY parameter requests are routine.
- No periodic firmware work, flash writes, or USB console is needed. Both halves
  record locally; only the left exposes history to Windows, covering its host and
  split links. Power loss clears RAM history.
- Production diagnostic format 2 is discoverable separately from protocol 1.2.
  Each metadata response and event record fits one default-MTU ATT response.
- Windows downloads retained history on a background task, after a one-second
  quiet period. Shortcut activity and control exchanges cancel outstanding reads
  without awaiting the diagnostic task. No periodic downloads begin while a
  companion momentary layer is held. Bluetooth operations still share radio airtime.
- Requests contain the last collected boot ID and independent critical/routine
  sequence positions. The app saves these to LocalAppData/ShinyGo60/
  connection-history-position.json. Checkpoint replacement is atomic; completed
  collection is reported only after persistence. A process crash before saving
  can replay already logged events; boot/stream/sequence identify duplicates.
- A read interrupted by control activity or its ten-second download timeout
  preserves complete records already received. The next collection continues from
  their saved positions. `collectionComplete` reports whether both snapshot stream
  totals have been reached, so a slow full ring can be collected across downloads.
- Logs include the firmware/format versions, decoded result with raw code,
  negotiated/requested parameter units, stream-specific overwrite gaps, and boot
  changes. A first observation is distinguished from an observed restart.
- Event UTC times are a bounded estimate using the snapshot's uptime and the
  download start/end times. Receipt timestamps are separate. Uptime is modulo
  2^32 milliseconds, so event-time estimates assume events are under 49.7 days
  old; raw uptime remains available.
- Older firmware without the production metadata characteristic reports
  firmware_history_unavailable. An unknown format reports firmware_history_unsupported.
  The control protocol continues independently.

Both halves record even when the companion is closed. Export contains only
history already collected by the companion. A link that never reconnects cannot
deliver its RAM history wirelessly; rebooting clears that evidence. The right
half's private recorder is not downloaded: the left reports its view of the split
link. A zero reset mask does not rule out a restart because the bootloader may
have cleared the hardware reset flags.

## Local diagnostic export

The ZIP contains:

- `companion.jsonl`: events from the last seven days, selected from the seven
  newest log files plus the active file under `%LOCALAPPDATA%/ShinyGo60/Logs`.
- `firmware-history.jsonl`: firmware events from those logs; repeated
  boot/stream/sequence records are omitted.
- `environment.json`: companion, OS, .NET, protocol, and observed firmware
  versions; layout identity; Bluetooth adapter name, driver provider/version/date;
  and explicit notes when information could not be collected.
- `README.txt`: timestamp interpretation, retention limits, and sharing guidance.

Adapter enumeration has a ten-second timeout; unavailable adapter metadata does
not prevent exporting logs. Export does not issue keyboard commands or request a
fresh firmware snapshot. It filters existing local files in the background.
It does not add Bluetooth addresses, pairing keys, or stable adapter identifiers.
Configured layer/shortcut names and error paths from earlier logs may be present.

The existing diagnostic CLI can export through the same Windows implementation:

```powershell
dotnet run --project Windows/ShinyGo60.TransportSpike --configuration Debug --no-build -- export-diagnostics `
    'Output/ProductionDiagnostics-20260908/layout-manifest.json' 'Output/Go60-diagnostics.zip'
```

The CLI labels the companion version as not queried; the settings action records
its own exact version. Firmware versions are those observed during collection.

## Publishing and startup

Use the maintained script with a manifest matching the firmware being installed:

```powershell
& Windows/Publish-Companion.ps1 `
    -ManifestPath 'Output/ProductionDiagnostics-20260908/layout-manifest.json' `
    -ConfigurationPath 'Output/ProductionDiagnostics-20260908/companion-settings.json'
```

The script builds a self-contained Windows x64 package by default, validates the
packaged configuration without connecting to the keyboard, and then replaces the
stable artifact directory. It removes the temporary rollback copy after replacement
succeeds. Exit a running copy from that installation before publishing.
If ConfigurationPath is omitted, it preserves the installed settings when present.
It updates the workspace shortcut and an already-enabled Windows startup entry;
it does not enable startup when disabled.

New companions publish their version/build identity to other local instances.
Launching a newer version requests a graceful exit from an older production
version before taking over. Launching an identical build opens its existing
settings. An older/unidentified app, a newer active version, or a different build
with the same version produces a visible conflict instead of silently retaining
that app. Startup logs record the version/build and refresh an enabled startup
entry to the running installation.

## Wire contract

Service: 5A9C0000-7F76-4C2A-9C46-9B7317F6A1E0. Existing message characteristic
0001 is unchanged. Diagnostic access requires encryption and a known bond.
All integers are little-endian.

- 0002: write 12 bytes (boot/u32, critical-after/u32, routine-after/u32) to freeze
  a per-connection snapshot. Read one 20-byte event at a time; an empty response
  ends it. Reads consume only this frozen download, never the recorder. Each
  collection starts a fresh snapshot, including after cancellation. One collector
  per host connection is required; the companion's single-instance rule enforces it.
- 0003: read 20-byte metadata: format/u8=2, record-size/u8=20, critical-capacity/u8,
  routine-capacity/u8, boot/u32, uptime-ms/u32, critical-total/u32, routine-total/u32.
  Before starting a download, only the first four bytes describe availability.
- 0004: read 20-byte context: reset-mask/u32, reset-result/i32, firmware-version/
  12-byte NUL-terminated ASCII. The version currently is 0.9.0.
- Record: stream-sequence/u32, uptime-ms/u32, event/u8 (bit 7 marks critical),
  connection+role/u8 (low 6 bits connection, high 2 bits role), result/i16,
  a/b/c/d/u16. Unknown connection=63 and role=3. Critical records precede routine
  records in a download; each stream is chronological. Sort by uptime for a
  combined timeline, allowing for the documented wrap.

The Windows transport joins metadata, context, and records for the core decoder.
This assembled buffer is not a single on-air attribute value.

## Verification on 2026-09-08

Firmware normal-build compilation succeeded for both halves on 2026-09-08.
Evidence is Output/production-diagnostics-build.log and the generated workspace
Custom Firmware/Generated/ProductionDiagnostics-20260908. Existing upstream
warnings remain. Left RAM usage is 83,504 bytes; right RAM usage is 41,124 bytes.

Windows Debug solution compilation passed with zero warnings/errors, and all
18 regression groups passed. Evidence is Output/production-windows-build.log and
Output/production-tests.log. Release self-contained publish and packaged manifest/
configuration validation also passed (Output/production-companion-publish.log).

The production UF2 has two complete segments: 1,136 left blocks (family
0x9809B007) and 748 right blocks (family 0x980AB007). UF2 framing, keymap hash,
embedded layout identity and feature version were checked. The package's
verification.json records the full SHA-256 and confirms no flash was performed.

| Requirement | Implementation and evidence |
| --- | --- |
| Protect useful history | Separate fixed 64/32 RAM rings, critical-event classification reviewed; both firmware images compiled |
| Production defaults/discovery | Kconfig defaults on, format-2 metadata/context characteristics, no periodic recorder task or flash writes |
| Readable evidence | Error decoding, event time bounds, gaps and boot changes; decoder/collector regression checks |
| Reliable collection/control priority | Saved progress across restarts and interrupted downloads; stalled-read test confirms layer press/release dispatch and cancellation |
| Local export | Bundle content/filter/deduplication checks; real Windows export read this PC's Realtek driver details successfully |
| Current companion startup | Staged publish/config validation, stable shortcut/startup target; older-app conflict dialog and production startup exercised |

The real export is Output/production-diagnostics-export.zip. Its adapter metadata
reported Realtek Bluetooth LEAI Driver, version 18.4032.2510.901. Existing 0.8.3
logs did not carry firmware version metadata; the export reports that absence.
The new export button was compiled and its shared export implementation exercised;
the button/save-dialog interaction was not exercised after UI automation was
stopped at the user's request.

Rider calls confirmed Go60 is not open there, so IDE refactoring, test discovery,
and inspections are unavailable. Edits were made directly with compiler checks.
The previously attempted InspectCode fallback could not resolve the SDK/framework.

The dedicated production-impact validation campaign was excluded by request:
battery-use, repeated disconnects, older-firmware compatibility, malformed
snapshots, and active-typing impact campaigns were not performed. Focused existing
regressions are implementation checks, not evidence for those excluded campaigns.
The new on-device protocol and graceful takeover between two production versions
have not been exercised on hardware. Flashing and collecting a real 0.9.0 snapshot
remain deployment checks; the package does not claim they have already passed.
