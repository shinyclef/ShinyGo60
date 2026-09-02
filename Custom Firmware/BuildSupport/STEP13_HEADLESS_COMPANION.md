# Step 13 headless companion

Status: complete

Recorded: 2026-09-02 on Windows 11

## Goal

Turn the proven protocol, transport, telemetry, and layer-command pieces into one long-running Windows service core. The core must complete the daily shortcut
workflow without depending on the final configuration UI or taskbar widget, and it must recover safely when devices appear, disappear, or change transport.

Step 13 changes only the Windows software. It reuses the Step 12 firmware and matching manifest:

- firmware: `Output/ShinyGo60-20260902-090655-3fd12c2c/ShinyGo60-3fd12c2c.uf2`;
- layout ID: `sg60-v1-3fd12c2c4edf42e10f1a9e29e08557cb`; and
- manifest: `Output/ShinyGo60-20260902-090655-3fd12c2c/layout-manifest.json`.

No Docker operation, firmware rebuild, or flash is required for this step.

## Runtime design

`CompanionService` owns the transport-independent lifecycle. `ShinyGo60.Platform.Windows` supplies the Windows USB CDC, Bluetooth GATT, and global keyboard-hook
implementations. The WPF application is a compact diagnostic host over the core rather than the final Step 14 widget.

The connection sequence is:

1. Load and strictly validate the layout manifest and companion settings.
2. In automatic mode, try USB and then paired Bluetooth.
3. Open exactly one transport and negotiate all required protocol 1.1 capabilities.
4. Verify the firmware layout fingerprint against the manifest.
5. Request complete layer and battery snapshots before accepting shortcuts.
6. Route shortcut transitions to serialized layer commands and apply each returned full state.
7. On failure, end local session ownership, retain no momentary action, and reconnect with bounded backoff.

USB discovery uses the Go60 left-side CDC endpoint's exact vendor/product pair, `16C0:27DB`. Bluetooth discovery considers paired BLE devices and selects only one
device exposing the ShinyGo60 service and message characteristic. The protocol handshake remains the final identity check on either transport. Automatic mode
keeps one healthy command owner; it does not open a competing background session. If the active transport fails, discovery resumes in USB-first order. The
diagnostic view's re-scan button can deliberately restart that USB-first selection while a transport remains healthy.

The reconnect delay starts at 500 ms, doubles after each failed USB/Bluetooth discovery round, and caps at 10 seconds. Bluetooth also performs a state exchange
after 30 seconds without protocol activity. This covers Windows radio-off cases where `BluetoothLEDevice.ConnectionStatusChanged` is not delivered. Successful
commands or telemetry reset the inactivity clock, so the check does not add traffic while useful activity is already flowing.

## Shortcut and configuration contract

The example settings file is `Windows/companion-settings.example.json`:

```json
{
  "schemaVersion": 1,
  "transportPreference": "automatic",
  "shortcuts": [
    {
      "shortcut": "F23",
      "action": "momentaryLayer",
      "targetLayer": "Navigation"
    }
  ]
}
```

Configuration property names and enum strings are case-sensitive, unknown properties and numeric enum values are rejected, and every target layer name must
match the supplied manifest exactly. Duplicate normalized shortcuts are rejected. Supported gestures use zero or more of `Ctrl`, `Alt`, `Shift`, and `Win`, plus
one A-Z, 0-9, F1-F24, or supported navigation key.

The Windows low-level keyboard hook observes only configured trigger keys and never suppresses their normal Windows input. It records down/up state, modifier
state, and whether Windows marked the input as injected. Physical and injected events use the same behavior; the origin is retained for diagnostics.

One physical key-down creates at most one shortcut press. Windows key-repeat is suppressed until the matching key-up. A momentary mapping requests the firmware's
maximum five-second lease and renews it every two seconds only while that shortcut remains held. A persistent mapping sends one selection on key-down and does
nothing on release. If a session ends during a hold, the companion forgets the action but retains the observed down state: the user must release and press again
after reconnect, preventing a stale held key from silently creating ownership in a new session.

Commands are serialized. An acknowledgement timeout retries the exact packet once so the firmware's duplicate-command contract can resolve ambiguity without a
second mutation. On normal stop or manual re-scan, the service attempts to release every live momentary activation before closing. If the transport has already
failed, that cleanup is best-effort and the firmware lease remains the bounded safety mechanism; the requested stop or reconnect still completes.

## Diagnostic surface

The Step 13 WPF host displays:

- connection state and active transport;
- effective, persistent, and momentary layer state;
- resolved left/right battery state;
- configured shortcut mappings and the latest routed transition;
- current retry or connection detail; and
- the diagnostic log path.

Daily JSON-lines logs are appended under `%LOCALAPPDATA%\ShinyGo60\Logs`. They include connection attempts/failures, validated connections, shortcut transitions,
command completion, retry, effective layer ID, momentary count, and whether shortcut input was injected. They do not contain raw keymap bytes, protocol packets,
pairing material, secrets, or stable device identifiers. A stale packet buffered from an earlier firmware session is ignored by session ID; malformed or invalid
current-session telemetry still ends the connection and forces a clean handshake.

## Implemented components

- `ShinyGo60.Companion.Core.Configuration` loads strict JSON and resolves layer names against the build manifest.
- `ShortcutGesture`, `ShortcutRouter`, and related records normalize gestures, suppress repeat, retain physical down state across session loss, and produce
  persistent or momentary routes.
- `CompanionService` owns discovery, negotiation, snapshots, shortcut commands, lease renewal, telemetry, diagnostic status, graceful cleanup, and reconnects.
- `ShinyGo60.Platform.Windows` contains the shared production USB/Bluetooth transports formerly embedded in the transport spike, plus global shortcut capture.
- `ShinyGo60.Companion` hosts the core in a small WPF diagnostic window with a manual re-scan action.
- `companion-settings.example.json` supplies the tested real-mouse F23-to-Navigation mapping.

## Automated verification

The complete Release solution builds serially with zero warnings and errors. All 12 offline suites pass. Step 13-specific coverage includes:

- strict settings resolution, gesture normalization, duplicate rejection, missing layers, unknown fields, missing shortcut values, and numeric enum rejection;
- synthetic F23 down/repeat/up sequences for ordinary and Windows-injected input;
- persistent F22 selection and momentary F23 press/renew/release lifecycles;
- held-key suppression across session replacement, including startup seeding;
- exact layout/capability handshake, initial layer/battery snapshots, and resulting command state;
- automatic USB-to-Bluetooth fallback and explicit USB-first re-scan;
- silent Bluetooth loss detection through the bounded health exchange;
- companion startup before the keyboard becomes available;
- exponential delay capping;
- stop completion when the transport silently fails during best-effort momentary cleanup; and
- rejection of invalid current-session state while ignoring a buffered wrong-session battery event.

Run the checks from the repository root:

```powershell
dotnet build '.\Windows\ShinyGo60.sln' --configuration Release --no-restore --disable-build-servers --maxcpucount:1
dotnet run --no-build --project '.\Windows\ShinyGo60.Tests\ShinyGo60.Tests.csproj' --configuration Release
```

## Physical verification

The following tests used the flashed Step 12 firmware, the matching generated manifest, the real G502 button configured as F23, and one continuously running
Step 13 companion unless a startup-order test explicitly says otherwise. Diagnostic timestamps below are UTC.

- USB connected and Windows Bluetooth off: the app validated USB at `13:31:01`. The real F23 down at `13:31:19` was reported as hardware input, sent exactly one
  momentary Navigation press, and returned revision 86 with effective layer 3 and one momentary owner. Renewals continued approximately every two seconds while
  held. F23 up at `13:31:49` sent one release and returned revision 87, effective Home, with zero momentary owners.
- With Windows Bluetooth enabled, physical USB removal at `13:32:53` raised transport loss. The same running app immediately selected Bluetooth and completed a
  new validated session at `13:32:54`.
- Over that Bluetooth session, the real F23 down at `13:33:23` returned revision 98, effective Navigation, with one owner. Repeated renewals kept that state live.
  F23 up at `13:33:33` returned revision 99, effective Home, with zero owners.
- The diagnostic window then closed normally and its process exited without needing forced termination.
- The app was next launched while USB was disconnected and the Windows Bluetooth radio was off. Both discoveries failed cleanly while the process stayed
  responsive. Logged rounds demonstrated the increasing delay and ten-second cap. USB was connected later; the existing process discovered and validated it at
  `13:35:56` without an app restart.
- An earlier live recovery test turned the Windows Bluetooth radio off while Bluetooth was the command owner and USB was available. With no native disconnect
  callback, the inactivity health exchange reported `Unreachable` at `13:19:30`; USB was validated about 520 ms later. This is the reason for retaining the
  bounded low-frequency Bluetooth health check.
- The same firmware had already passed normal USB/Bluetooth typing, TRRS and wireless split operation, full keyboard reboot, and firmware-side lease-safety tests
  in Step 12. Step 13 did not change the firmware image.

Physical Windows sleep/resume remains explicitly deferred because this test PC does not enter sleep reliably. The service uses the same transport-loss,
session-replacement, snapshot, and bounded-reconnect path exercised by the physical radio/cable/startup tests and the synthetic lifecycle tests. This limitation
does not conceal a failed sleep test; it records an unavailable host condition for later clean-machine acceptance.

## Run the diagnostic

Build first, then launch with the matched files:

```powershell
dotnet run --no-build --project '.\Windows\ShinyGo60.Companion\ShinyGo60.Companion.csproj' --configuration Release -- `
    '.\Output\ShinyGo60-20260902-090655-3fd12c2c\layout-manifest.json' `
    '.\Windows\companion-settings.example.json'
```

When published for ordinary use, those two JSON files will sit beside the executable so explicit arguments are unnecessary. That packaging and the editable
configuration/taskbar-widget experience belong to Step 14 and later release work.
