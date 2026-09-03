# Adaptive Bluetooth latency

Status: corrected GATT behavior passed physical shortcut testing; latency measurement and longer-term battery acceptance pending

Recorded: 2026-09-03 on Windows 11

## Goal

Reduce the noticeable delay between a Windows shortcut such as the G502's `F23` and a companion-controlled layer change over Bluetooth, without holding the
Go60 at its highest-power connection setting all day.

This does not create another radio connection, pairing, service, or dongle path. The normal Bluetooth HID keyboard and ShinyGo60 GATT service continue to share
the Go60's existing bonded Bluetooth Low Energy connection. Only that connection's negotiated peripheral-latency parameter changes.

## Policy

The Windows companion monitors ordinary system keyboard and mouse activity with `GetLastInputInfo` once per second and listens for Windows session lock and
unlock events. It requests:

| Situation | Requested peripheral latency |
| --- | ---: |
| Windows unlocked and used within the last 60 seconds | 4 |
| Windows locked or idle for at least 60 seconds | 30 |

Latency `N` permits the peripheral to skip as many as `N` connection events when it has no data to send. Lower values improve the responsiveness of traffic
initiated by Windows but require the keyboard radio to listen more often. The connection interval itself is unchanged.

Firmware starts and returns to the power-saving value when a protocol session is replaced, its GATT subscription ends, or the Bluetooth connection drops. A
normal companion shutdown explicitly requests power saving before unsubscribing. Interactive mode is also an expiring 90-second lease, renewed by ordinary
companion traffic, so a crashed or vanished client cannot leave lower latency selected indefinitely.

The first physical candidate also requested latency 0 when a companion momentary layer became active and returned to latency 4 on release. Repeated F23 use
produced intermittent Windows GATT `ProtocolError` failures and companion reconnects even after removing and re-pairing the keyboard. Version
`0.8.1-adaptive-ble` removes that per-press negotiation. Connection changes are now limited to Windows activity transitions and wait 250 ms before being
submitted, allowing the command acknowledgement to finish and coalescing rapid policy changes.

Diagnostic history showed that the same Windows `ProtocolError` could also occur on firmware from before adaptive latency. The Bluetooth transport allowed only
one in-flight indication and rejected a command write if a layer or battery event—or the confirmation tail of the preceding response—still occupied that slot.
The corrected transport queues one command response behind the in-flight indication. This matches the companion's serialized request model while retaining
event coalescing and bounded memory.

## Protocol and failure behavior

Protocol 1.2 adds the `AdaptiveBluetoothLatency` capability and a session-bound, replay-safe `SetBluetoothConnectionMode` command. The command uses the same
20-byte framing, serialized command queue, acknowledgement handling, and encrypted GATT transport as the existing layer commands. USB sessions never send or
accept it.

The firmware coalesces redundant parameter changes and asks Zephyr to update the current connection. Transient busy or buffer-allocation failures are retried at
100 ms intervals, at most five times. The command acknowledgement confirms that the request was accepted by the firmware; the Windows Bluetooth controller is
still allowed by Bluetooth Low Energy to clamp or reject the requested parameters.

The first shortcut after a long idle period can still pay the previous power-saving delay because the keyboard must receive traffic before either side can ask
for a faster setting. Once normal activity has selected interactive mode, later shortcut commands should use the lower negotiated latency. Physical measurement
is therefore required before claiming a particular millisecond result.

## Verification

- The complete Debug companion build passes with zero warnings and errors.
- All 13 offline Windows suites pass, including protocol 1.2 vectors, invalid mode values, command coalescing, Bluetooth mode transitions, idle-boundary policy,
  session-lock policy, initial Bluetooth mode, graceful power-saving shutdown, and confirmation that USB sends no mode command.
- The C and C# codecs share 15 golden 20-byte vectors, including `set-bluetooth-connection-mode.bytes`.
- A network-disabled firmware build using the existing `shinygo60-builder:v25.11` image compiled and validated both halves and the combined UF2 in 17.09 seconds.

The matched physical-test set is:

- UF2: `Output/ShinyGo60-20260902-174213-214f19fd/ShinyGo60-214f19fd.uf2`
- manifest: `Output/ShinyGo60-20260902-174213-214f19fd/layout-manifest.json`
- layout ID: `sg60-v1-214f19fd7094b06306ad09a675ef3a88`
- UF2 SHA-256: `74fcdd61fddc7321096299f418784d915a071da424617af4eea4c55c833246e8`

## Physical acceptance

After flashing the same UF2 to both halves, verify normal typing and the split link first. Then run the matching protocol 1.2 companion and check:

1. USB and Bluetooth connection, layer telemetry, battery telemetry, repeated `F23` momentary behavior, and physical layer keys.
2. Repeated `F23` presses during active Bluetooth use feel materially faster than protocol 1.1.
3. After at least 60 seconds without Windows input, the first press may be slower but subsequent presses recover.
4. Repeated F23 presses do not produce `ProtocolError`, stale state, or a companion reconnect.
5. Closing the companion, turning off Windows Bluetooth, or selecting another Go60 host leaves or returns the keyboard to power-saving mode.
6. Locking and unlocking Windows does not disconnect the keyboard or companion.
7. Bluetooth, USB, wireless split, and TRRS split continue to work.
8. Longer-term battery life remains acceptable with LEDs disabled in the user's normal configuration.

Actual negotiated connection parameters and battery impact remain hardware observations, not guarantees inferred from the requested values.

On 2026-09-03, the user reported that the corrected physical test passed after flashing the matched `0.8.1-adaptive-ble` UF2. Repeated Bluetooth F23
press/release behavior, layer return, and normal keyboard use no longer produced the earlier visible stale/reconnect failure. Quantified latency and longer-term
battery observations remain open rather than being inferred from that functional pass.
