ShinyGo60 local diagnostic export

companion.jsonl: events from the last seven days in the seven newest log files plus the active file.
firmware-history.jsonl: collected firmware events; repeated boot/stream/sequence entries are omitted.
environment.json: software versions, local Bluetooth adapter/driver details, and collection notes.

Event timestamps in the outer log are host receipt times. Firmware uptime and estimated event-time
bounds describe occurrence. Earlier experimental logs may contain raw codes without descriptions.
A boot change clears on-device RAM history. Missing-event counts are events, not disconnect counts.
This export uses already collected history; it does not disturb the keyboard to obtain a new snapshot.

Logs may contain configured layer names, shortcut names, and error paths from older software.
Adapter identifiers, Bluetooth addresses and pairing keys are not intentionally added by the export.
Review the files before sharing. Saving this ZIP does not upload it anywhere.