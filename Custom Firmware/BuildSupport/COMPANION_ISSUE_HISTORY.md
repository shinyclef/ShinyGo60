# Companion connection issue history

Companion 1.3.2 shows selectable connection issue history in its settings window.
Copy All copies the displayed incidents. The latest issue's local date and time appear above Advanced Settings.
Advanced Settings groups Connection Issue History, Adaptive Bluetooth Latency, and Connection Details.
Diagnostic export controls have been removed.
It works with the existing protocol-1.3 firmware. No firmware flash is needed for this feature.

The firmware collector still runs automatically on Bluetooth connection and periodically afterward.
It yields to shortcut/control activity and acknowledges saved progress through the existing cursor.
The firmware buffers are not physically cleared. Acknowledgement releases protected failure slots,
and ordinary old records are overwritten naturally.

The companion reads up to seven recent daily log files plus the active file, filters to seven days,
and displays up to 50 recent incidents. It refreshes at connection-state changes and every 30 seconds
on a background task. History is reconstructed after restart, including earlier firmware versions'
logs where the required fields exist. Incomplete final log lines are retried on the next refresh.

Companion-observed loss and validated reconnection bound an incident. Repeated security/reconnection
failures are grouped into that incident and replayed firmware records are deduplicated by boot,
stream and sequence. Firmware-only failures without an observed outage are grouped when their
estimated times are close (within ten seconds of the preceding evidence). Those groups are labelled
estimated and do not claim an exact recovery time. Starting the companion again does not invent a
recovery duration for an outage it stopped observing.

The summary distinguishes reported evidence from root cause. For example, 0x28 is labelled
Instant Passed, a link-control timing failure. It does not assign blame to the keyboard or PC.
Recovery duration measures the companion session; normal keyboard input may recover sooner.

Windows correlation reads up to 512 recent BTHUSB events using a hidden, bounded Windows event query.
Event 5 counts are matched to each incident's time window. These are system-wide adapter events,
so correlation does not prove that the Go60 or its firmware caused a driver error. If Windows event
access is unavailable, the history panel says so and firmware/companion analysis remains available.

`Logs/connection-issues.json` beside the companion logs contains the latest generated report,
its generation time and Windows event IDs/timestamps. It is a derived summary, not the acknowledgement
checkpoint or the only copy of diagnostic evidence. No data is uploaded.

Verification covers replay deduplication, a 31-attempt reconnect storm, host/split separation,
missing records, app restart boundaries, long outages, estimated history, and summary export.
Replaying the user's 11 September export produces one 15:29 incident with code 0x28, 93 seconds
to companion recovery, 31 failed reconnects, 31 security failures and six Windows driver errors.
