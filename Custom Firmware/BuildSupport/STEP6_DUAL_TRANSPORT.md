# Step 6 dual-transport spike

Status: corrected v0.2.1 passed TRRS, USB, Bluetooth, and transport-switching checks; remaining gate G3 security and reconnection tests are pending

Recorded: 2026-09-01 on Windows 11

## Purpose

Step 6 adds one provisional diagnostic exchange to prove that ShinyGo60 can carry the same application message over both required host transports before any
layer-control feature is developed:

```text
Hello -> HelloResult
```

This is deliberately not the production layer protocol. It does not change layers, intercept keys, advertise a new Bluetooth device, or require the diagnostic
client for normal keyboard operation.

## Shared wire packet

USB and Bluetooth carry the same fixed 16-byte packet without a transport-specific wrapper:

| Offset | Size | Field | Value or encoding |
| ---: | ---: | --- | --- |
| 0 | 4 | Magic | ASCII `SG60` |
| 4 | 1 | Protocol major | `0` |
| 5 | 1 | Protocol minor | `1` |
| 6 | 1 | Message type | `1` for `Hello`; `2` for `HelloResult` |
| 7 | 1 | Reserved | Must be zero |
| 8 | 4 | Sequence | Unsigned 32-bit little-endian integer |
| 12 | 4 | Challenge | Unsigned 32-bit little-endian integer |

Firmware accepts only a complete, valid `Hello` packet. `HelloResult` preserves its sequence and random challenge, allowing the client to prove that the reply
matches the current exchange rather than stale data.

## Firmware transports

Both transports are compiled only into the Go60 left/central image.

### USB

- Reuses the pinned ZMK `studio-rpc-usb-uart` snippet for its tested composite USB CDC/ACM node and required serial settings.
- Uses a ShinyGo60-owned fixed-packet handler rather than enabling the ZMK Studio protocol.
- Receives and replies through an interrupt-driven CDC UART while normal USB HID keyboard input remains present.
- Explicitly keeps physical `UART0` on the asynchronous API required by Go60's TRRS wired split, with compile-time guards against an API-mode regression.
- Exposes the central board's exact USB identity: vendor ID `16C0`, product ID `27DB`.

### Bluetooth

- Service UUID: `5A9C0000-7F76-4C2A-9C46-9B7317F6A1E0`.
- Message characteristic UUID: `5A9C0001-7F76-4C2A-9C46-9B7317F6A1E0`.
- The characteristic supports writes with response and indications.
- Characteristic writes and indication configuration require an encrypted connection.
- Firmware additionally verifies that the connected host exists in Zephyr's stored bond table and has at least Bluetooth security level 2 before processing a
  packet.
- Only one exchange may be outstanding. The diagnostic client sends requests sequentially.

The custom service is discovered on the already paired Go60; it does not create a second Bluetooth pairing or a separate advertised keyboard.

## Windows diagnostic client

`ShinyGo60.TransportSpike` is a Windows 11 C# console client built on the shared `IKeyboardTransport` contract and `HelloMessageCodec`.

- USB discovery uses the exact central VID/PID selector and refuses zero or multiple matching serial endpoints.
- Bluetooth discovery uses Windows' paired-BLE selector, performs uncached lookup of the exact service and characteristic UUIDs, and refuses ambiguous matches. It
  does not depend on the display name or the inconsistently populated secondary pairing flag.
- Each exchange uses a new random challenge, verifies the returned type, sequence, and challenge, and reports round-trip latency.
- The client logs no raw packet bytes, bond material, or stable device identifiers.

Run it from the repository root after flashing:

```powershell
dotnet run --project '.\Windows\ShinyGo60.TransportSpike\ShinyGo60.TransportSpike.csproj' --configuration Release -- both 5
```

The first argument may be `usb`, `bluetooth` (or `ble`), `both`, or `switch`. `switch` tests USB, then Bluetooth, then USB again. The optional second argument is
the number of exchanges per connection and defaults to five.

## Offline build evidence

| Item | Result |
| --- | --- |
| MoErgo ZMK revision | `11454d23596afbdb06380a1125371b19ab65675c` |
| Builder image | `shinygo60-builder:v25.11` (`8c05b8af27498f7f42391fa408dfd841fbebfdc70f0d7766a280edd03db98720`) |
| Builder network | Disabled |
| Corrected build duration | 14.303 seconds |
| Firmware build | Success; no compiler error lines |
| Windows solution build | Success; zero warnings and zero errors |
| Offline C# checks | 5/5 passed |
| Combined UF2 structure | Valid; 1,832 512-byte blocks in two complete segments |
| Packaged artifact | `Output/Step6/go60-step6-transport-spike-v0.2.1-trrs-fix.uf2` |
| Artifact size | 937,984 bytes |
| Artifact SHA-256 | `39232A2AB09A9D20AC4C1CF4CF096128C06D3B8DA48516AD488D7B72CBC9502F` |
| Final build log | `Output/Step6/build-corrected.log` |

The combined artifact is byte-for-byte equal to the final left UF2 followed by the final right UF2. The right UF2 remains byte-for-byte identical to Step 5, with
SHA-256 `C9390B7A5FD0F1CA01C39F44AD132AE4115FF7B942103B8E53AA7D7418B9386F`. Resolved Kconfig and ELF marker checks also confirm that the transport is present only
on the central image.

## Memory comparison

| Image | Step 5 flash | Step 6 flash | Growth | Step 5 RAM | Step 6 RAM | Growth |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Left/central | 273,464 B | 278,532 B | 5,068 B | 64,648 B | 68,712 B | 4,064 B |
| Right/peripheral | 190,116 B | 190,116 B | 0 B | 37,148 B | 37,148 B | 0 B |

## Hardware findings

The initial v0.2.0 build produced these results:

- Both halves booted and ordinary USB/Bluetooth keyboard operation was initially reported working.
- USB returned 5/5 matching replies: minimum 0.43 ms, mean 1.77 ms, maximum 4.28 ms.
- The first Bluetooth lookup exposed two invalid client assumptions: Windows supplied neither the Settings display name nor a consistent secondary pairing flag.
  After removing those filters and relying on the paired-device selector plus exact service UUID, the existing bond worked without re-pairing.
- Bluetooth returned 5/5 matching replies: minimum 29.25 ms, mean 214.29 ms, maximum 495.29 ms. The two roughly 500 ms replies require further observation.
- A USB-to-Bluetooth-to-USB run was not evaluated because USB had been physically disconnected before that run.
- TRRS inter-half communication failed with v0.2.0 while the original firmware worked with the same hardware. The supplied lighting photo records the state seen
  while TRRS was connected, but the user cannot confirm that this pattern began with Step 6, so it is not classified as a visual regression.

Resolved Kconfig comparison identified the TRRS cause. The CDC snippet enabled the global interrupt-driven UART API, which also changed physical `UART0` from
`CONFIG_UART_0_ASYNC=y` to `CONFIG_UART_0_INTERRUPT_DRIVEN=y`. Go60's wired-split code remained configured for the asynchronous API, so cable detection could occur
without working inter-half traffic.

Corrected v0.2.1 keeps the global interrupt API required by the virtual CDC UART while explicitly restoring asynchronous physical `UART0`. The build verifies all
of these simultaneously:

- `CONFIG_ZMK_SPLIT_WIRED_UART_MODE_ASYNC=y`.
- `CONFIG_UART_0_ASYNC=y`.
- `CONFIG_UART_0_INTERRUPT_DRIVEN` is disabled.
- `CONFIG_USB_CDC_ACM=y` and the ShinyGo60 transport remain enabled.
- The right UF2 remains byte-for-byte identical to the working Step 5 right image.

After flashing corrected v0.2.1 to both halves, the user confirmed that Bluetooth and TRRS operation both work. This closes the observed TRRS regression. The
existing Bluetooth pairing remained usable after the firmware update, and the corrected-build diagnostic client returned 5/5 matching Bluetooth replies:
minimum 29.59 ms, mean 121.32 ms, maximum 484.49 ms. One roughly 484 ms reply remains consistent with the occasional long reply seen on v0.2.0 and should be
observed during later soak testing.

After the left USB data cable was connected, corrected v0.2.1 returned 5/5 matching USB replies: minimum 0.43 ms, mean 1.71 ms, maximum 4.11 ms.

A subsequent software-only USB-to-Bluetooth-to-USB run passed all 15 exchanges without changing cables or Bluetooth settings:

- First USB session: minimum 0.43 ms, mean 1.77 ms, maximum 4.06 ms.
- Bluetooth session: minimum 27.76 ms, mean 29.87 ms, maximum 31.48 ms.
- Second USB session: minimum 0.39 ms, mean 0.45 ms, maximum 0.56 ms.

## Physical validation checkpoint

Corrected v0.2.1 is now running on both halves. TRRS inter-half operation, normal Bluetooth operation, continued use of the existing bond, repeated USB and
Bluetooth `HelloResult` replies, and USB-to-Bluetooth-to-USB switching have passed. Step 6 still requires hardware evidence for:

- Existing and fresh pairings, including confirmation that an unpaired host cannot issue a valid command.
- Reconnect, Bluetooth cache refresh, sleep/resume, radio loss, range loss, keyboard power cycle, and all relevant host profiles.
- Round-trip latency, idle CPU use, observable wireless power impact, and ordinary keyboard behavior while the client is absent.

Gate G3 remains pending until the same client passes both transports and the security/reconnection checks on the physical keyboard.
