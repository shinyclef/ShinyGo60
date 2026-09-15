# Wireless connection diagnostics

This guide records the earlier 0.8.3 experimental package. For current builds,
installation, local export, and the production format, use the
[production diagnostic guide](PRODUCTION_DIAGNOSTICS.md). Normal firmware builds
now enable the recorder by default; the experimental build instructions below
describe the original investigation.

Firmware `0.8.3-wireless-diagnostics` stores connection events in RAM and exposes
an optional encrypted, bonded GATT read. USB is needed only to flash the UF2.
The companion downloads the history after transport connection (before Hello)
and on its existing Bluetooth health-check cycle, normally every 30 seconds.
It skips periodic downloads while a companion momentary layer is held.

## Use the prepared package

1. Exit the running companion.
2. Flash `Output/WirelessDiagnostics-20260907/Go60-wireless-diagnostics.uf2` to
   both halves, then unplug USB and select Bluetooth keyboard output.
3. Run `Output/WirelessDiagnostics-20260907/Start-Companion.ps1`. It uses the
   matching manifest and a copy of your example configuration with Bluetooth
   selected, including the existing F23 Keypad shortcut.
4. Use the keyboard normally. When a drop occurs, note the time and whether
   typing failed on both halves, just the right, or only the companion/widget.
   Leave the keyboard powered on so its history can be downloaded after reconnecting.
5. Logs are appended to `%LOCALAPPDATA%/ShinyGo60/Logs/companion-YYYYMMDD.jsonl`.
   `Export-Diagnostics.ps1` in the package copies the current day's log to a
   timestamped package subfolder for analysis.

For the older power-on problem, test with the companion closed, then start it
once the keyboard connects. Recording works without the companion running.
A connection that never recovers cannot deliver its history over Bluetooth.

## Evidence and limits

- The left half retains the latest **20 events**, oldest first. Repeated reads
  are deduplicated within a companion run, including reconnections. Sequence gaps
  appear as `missedEvents`. A long failure burst may overwrite earlier evidence.
- This is RAM history: a power cycle or firmware restart clears earlier events.
  The next download has a new random `bootId`. Reset status/mask is included,
  but bootloader-cleared reset bits can be zero; zero does not rule out a restart.
- The downloaded history is the **left half's view** of the Windows link (local
  role `peripheral`) and wireless split links (local role `central`). It does not
  download the right half's private history or identify its reset cause.
- Event timestamps are firmware uptime in milliseconds, wrapping after about
  49.7 days. JSON timestamps are host receipt times, possibly after reconnection.
  Connection indices are local/reused, so correlate them with boot and lifecycle.
- Reads use a separate characteristic and never enter the command/indication
  queue or renew the adaptive latency lease. Each client gets a frozen snapshot
  across ATT long-read fragments. No raw keys, addresses, or pairing data are stored.
- There is no periodic firmware logging task or flash writing. Collection adds
  occasional GATT traffic, so this is not a measurement of baseline power usage.
- An optional read failure produces `firmware_history_failed` without deliberately
  reconnecting the control session. Older firmware with no history characteristic
  continues to work but cannot supply firmware history.

## Log interpretation

`firmware_boot` includes `bootId`, current `uptimeMs`, `resetCause` and the hardware
API `resetResult`. `firmware_connection_event` includes the same boot ID, sequence,
event uptime, role/index, signed result, and event-specific fields:

| Event | result | a | b | c | d |
| --- | --- | --- | --- | --- | --- |
| connected / disconnected | HCI error / reason | interval | latency | timeout | 0 |
| security_changed | security error | security level | 0 | 0 | 0 |
| parameters_updated | 0 | interval | latency | timeout | 0 |
| parameters_requested | API result | min interval | max interval | latency | timeout |
| interactive_lease_expired | 0 | 0 | 0 | 0 | 0 |
| subscription_changed | 0 | CCC value | 0 | 0 | 0 |
| response_queue_failed | 0 | request type | 0 | 0 | 0 |
| indication_failed | ATT error | 0 | 0 | 0 | 0 |
| indication_submit_failed | API result | 0 | 0 | 0 | 0 |

Intervals use 1.25 ms units; supervision timeouts use 10 ms units.
A successful parameter request does not confirm negotiation; use the subsequent
`parameters_updated` event. Missing connection info has an unknown role and zero
parameters. CCC callbacks do not identify the affected connection.

Windows events `transport_ready`, `exchange_received`, `exchange_failed`,
`transport_connection_lost`, and `connection_failed` retain timing, session/request
IDs, and available GATT/ATT status. An exchange being received does not mean its
response passed protocol validation. Loss during an exchange may appear only as
an exchange failure. Exception metadata uses a fixed allowlist.

## Build and wire format

Use the current generated `config/default.nix` and matching keymap/identifiers:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Build-Firmware.ps1' `
    -Workspace '.\Custom Firmware\Generated\ConnectionDiagnostics-20260907' `
    -ConnectionDiagnostics
```

The normal build leaves the recorder disabled. The diagnostic configuration
explicitly disables logging and adds no USB console. The legacy
`Windows/Capture-FirmwareDiagnostics.ps1` applies only to the earlier USB-console
firmware; it is not used by this solution.

Characteristic `5A9C0002-7F76-4C2A-9C46-9B7317F6A1E0` is read-only under the existing
ShinyGo60 service. All integers are little-endian. Header: `SGD1` (4 bytes), boot ID
(u32), current uptime (u32), reset mask (u32), reset result (i32), count (u16), capacity
(u16, 20). Each 24-byte record: sequence/u32, uptime/u32, event/u8, connection/u8,
role/u8, reserved/u8, result/i32, a/b/c/d/u16. Total size is 24–504 bytes.

## Verification

Both diagnostic firmware halves and the complete Windows solution must compile;
run the existing Windows test executable. The wireless tests cover malformed and
full snapshots, sequence ordering/wrap, repeated downloads across reconnects,
new boot detection, missing-event counts, and diagnostic read failure containment.
Hardware validation still requires flashing this new version and observing
wireless collection and a disconnect/reconnect. This instrumentation does not
claim to fix the underlying connection problem.

Rider cannot inspect this solution because Go60 is not open there. The installed
InspectCode fallback previously failed to resolve the SDK/framework correctly;
its results are unusable. Compiler and regression results do not replace that inspection.
