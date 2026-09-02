# Step 9 protocol v1

Status: complete; software vectors, firmware build, flash, normal HID, TRRS, USB, Bluetooth, and transport switching passed

Recorded: 2026-09-02 on Windows 11

## Purpose and current scope

Protocol 1.0 replaces the provisional Step 6 echo packet with the bounded application contract that later telemetry and layer-control steps will implement. USB CDC
and encrypted Bluetooth GATT carry exactly the same bytes. The Step 9 firmware implements layout-bound session negotiation but advertises no production
capabilities yet. Valid state and layer commands therefore return `CapabilityUnavailable` and cannot alter ZMK layer state.

The protocol describes the final version-one message shapes now so the firmware and Windows work can advance without changing the wire format independently.

## Common 20-byte frame

Every frame is exactly 20 bytes, which fits the minimum Bluetooth ATT value payload without an MTU negotiation.

| Packet offset | Size | Field | Encoding |
| ---: | ---: | --- | --- |
| 0 | 2 | Magic | ASCII `SG` |
| 2 | 1 | Version | Major in the high nibble, minor in the low nibble; v1.0 is `0x10` |
| 3 | 1 | Message type | One of the values below |
| 4 | 16 | Payload | Fixed layout selected by message type |

Unsigned 16- and 32-bit integers are little-endian. The layout fingerprint is the first 16 hexadecimal characters of the 32-character digest in
`sg60-v1-<digest>`, carried as eight bytes in display order. Zero is reserved for session, command, request, activation, and state-revision IDs. Layer IDs are
`0..254`; `255` means that no optional persistent layer is active. Every reserved byte must be zero. There are no strings or variable-length collections on the
wire.

An application checksum is intentionally absent. USB and Bluetooth already provide link-level integrity, and Bluetooth also authenticates encrypted bonded-host
traffic. A second checksum would consume scarce bytes without providing authentication. A damaged frame that reaches the application is accepted only if its
magic, exact version, message type, reserved fields, capabilities, session, IDs, revision, layer values, and lease all remain valid.

## Message layouts

Offsets below are absolute packet offsets.

| Type | Name | Payload fields |
| ---: | --- | --- |
| `0x01` | `Hello` | `4..5` client nonce; `6` requested capabilities; `7` reserved; `8..15` expected layout; `16..19` reserved |
| `0x02` | `HelloResult` | `4..5` nonce; `6` status; `7` selected capabilities; `8..11` session; `12..19` actual layout |
| `0x03` | `GetState` | `4..7` session; `8..11` request ID; `12..19` reserved |
| `0x04` | `StateSnapshot` | `4..7` session; `8..11` state revision; `12..15` request ID; `16..19` layer state |
| `0x05` | `LayerChanged` | `4..7` session; `8..11` state revision; `12..15` source command ID or zero; `16..19` layer state |
| `0x10` | `SetPersistentLayer` | `4..7` session; `8..11` command ID; `12..15` expected revision; `16` layer; `17..19` reserved |
| `0x11` | `PressMomentaryLayer` | Set fields through `16`; `17` lease units; `18..19` reserved |
| `0x12` | `RenewMomentaryLayer` | `4..7` session; `8..11` command ID; `12..15` activation ID; `16` lease units; `17..19` reserved |
| `0x13` | `ReleaseMomentaryLayer` | `4..7` session; `8..11` command ID; `12..15` activation ID; `16..19` reserved |
| `0x20` | `CommandResult` | `4..7` session; `8..11` command ID; `12..15` revision; `16` status; `17..19` compact layer state |
| `0x7f` | `Error` | `4..7` session; `8..11` related ID; `12..15` revision; `16` code; `17` offending type; `18..19` detail |

The four-byte layer state in `StateSnapshot` and `LayerChanged` is effective layer, persistent layer or `255`, active momentary count, and indicators. Indicator
bit 0 must exactly match whether the persistent field is present. Bit 1 must exactly match whether the momentary count is nonzero. `CommandResult` carries the
same first three values without an indicator byte; the receiver derives the two indicators.

One lease unit is 100 ms. Valid leases are `1..50`, so every momentary activation expires within at most five seconds without a valid renewal.

## Enumerations

Capabilities are bit 0 state telemetry, bit 1 persistent-layer control, bit 2 momentary-layer control, and bit 3 battery telemetry. Unknown bits are invalid.
The selected mask in `HelloResult` is the intersection of requested and currently supported capabilities. Step 9 returns zero; later steps enable bits only when
their complete behavior is present. Battery remains disabled unless Step 11 passes.

`HelloResult` statuses are success `0`, layout mismatch `1`, and unsupported version `2`. A failure has session zero and no selected capabilities.

Command-result statuses are applied `0`, no change `1`, duplicate `2`, and already released `3`.

| Error | Value | Meaning |
| --- | ---: | --- |
| `MalformedPacket` | 1 | Known current-version type with an invalid field or reserved byte |
| `UnsupportedVersion` | 2 | Exact frame uses a version other than 1.0 |
| `UnsupportedMessage` | 3 | Unknown type or a server-only type sent by a client |
| `NoSession` | 4 | A command arrived before a successful handshake |
| `WrongSession` | 5 | Session ID or owning transport is no longer current |
| `LayoutMismatch` | 6 | Layout identity is not the firmware layout |
| `CapabilityUnavailable` | 7 | Negotiated session does not provide the requested feature |
| `InvalidLayer` | 8 | Target is `255` or outside the manifest's layers |
| `StaleState` | 9 | Expected revision is not current |
| `StaleCommand` | 10 | Command ID precedes the most recently processed ID |
| `DuplicateConflict` | 11 | Current command ID was reused with different bytes |
| `LeaseOutOfRange` | 12 | Lease is outside `1..50` |
| `Busy` | 13 | The bounded one-at-a-time exchange slot is occupied |
| `Internal` | 14 | Firmware could not honor an otherwise valid request |

## Sessions, ordering, and ownership

- A successful exact-layout `Hello` creates a fresh nonzero session and makes that transport the sole command owner. It revokes the preceding session even when
  the same transport sent the new handshake. A mismatch, unsupported version, or malformed `Hello` does not revoke a working session.
- After every connection or USB/Bluetooth switch, the client performs a new `Hello`, then `GetState`, and waits for its snapshot before issuing commands. Packets
  from the previous transport or session receive `WrongSession`.
- A transport disconnect or session replacement releases all momentary activations owned by that session. Persistent selection is runtime-only and remains until
  explicitly changed or the keyboard reboots.
- Request IDs correlate snapshots and need only be nonzero. Command IDs increase within a session and do not wrap. The client serializes commands and normally
  keeps one outstanding request.
- The firmware caches the most recently processed command bytes and result. Repeating that exact command ID and bytes returns `Duplicate` without another state
  change. Reusing the ID with different bytes returns `DuplicateConflict`; an older ID returns `StaleCommand`.
- State revision starts at one and increments exactly once for each externally observable layer-state change. Set and press commands must carry the latest
  revision; a mismatch returns `StaleState` without mutation. Producers end a session before any 32-bit ID or revision would wrap to zero.
- A press command's command ID is its activation ID. Renewal extends the lease from receipt time. Release is idempotent. If a higher-ID release overtakes its
  press, it records `AlreadyReleased`; the delayed lower-ID press is then stale and cannot leave a layer active.
- Snapshots, command results, and unsolicited events are sent only to the owning transport. A client that misses an event requests a new snapshot rather than
  inferring state.

## Invalid and damaged traffic

- A Bluetooth value with the wrong length is rejected by GATT. A truncated USB frame remains incomplete and cannot reach the protocol handler; the bounded
  scanner resynchronizes on the next valid `SG` frame.
- Bad magic is discarded. A complete current-version frame with an unknown type returns `UnsupportedMessage`; a known type with invalid content returns
  `MalformedPacket`. Neither can change state.
- An unsupported-version `Hello` receives a current-version `HelloResult` rejection when its nonce can be read; other unsupported-version frames receive an
  `UnsupportedVersion` error. A current-version client rejects any response that is not exactly version 1.0.
- Layout mismatch, stale revision, stale command, duplicate conflict, expired activation, wrong session, and unselected capability are explicit failures with no
  layer mutation.
- Bluetooth protocol writes still require an encrypted connection from a host in the firmware bond table. The protocol is not a replacement for Bluetooth
  security and does not expose a second pairing identity.

## Shared verification and build result

The neutral `.bytes` fixtures under `Custom Firmware/Module/tests/protocol/vectors` contain all eleven message forms. The C# test project copies and consumes those
same files. The portable C test includes them directly, decodes and re-encodes each frame, checks representative values, and rejects malformed variants. The
Step 9 checks pass as follows:

| Check | Result |
| --- | --- |
| C# solution | Success; zero warnings and errors |
| C# scaffold checks | 8/8 passed |
| Native C codec | 11 golden vectors plus malformed packets passed with `-Wall -Wextra -Werror -pedantic` |
| Firmware build | Success in the pinned image with networking disabled |
| Reconstructed builder | `sha256:71f0923c8cbc49c18bcf5f8d168e24f9e0cc0cee087d61e8213242a4ec6d09b6`; temporary cache removed |
| Protocol version / layout ID | `1.0` / `sg60-v1-11804322b898ead8b189330754427a65` |
| Combined UF2 | 942,080 bytes; SHA-256 `af8995966c1a00434674d5f6d35f97393539ecb085cd48cfb3b255adf945198b` |
| Left flash / RAM | 280,772 / 68,760 bytes |
| Right flash / RAM | 190,116 / 37,148 bytes |

The matched artifact set is `Output/Step9/ShinyGo60-20260902-032254-11804322`. The same UF2 is flashed to both halves. Its included manifest is the required input
to the updated Windows transport diagnostic.

## Physical acceptance progress

The user flashed the final matched UF2 to both halves and confirmed normal keyboard behavior with the right half working over TRRS. The manifest-bound protocol-v1
client then completed every hardware check. Every response returned the correct layout and nonce plus a fresh nonzero session.

| Run | Exchanges | Minimum | Mean | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Initial USB | 5/5 | 0.49 ms | 2.58 ms | 7.88 ms |
| Dedicated Bluetooth | 5/5 | 19.66 ms | 31.16 ms | 45.01 ms |
| Switch: first USB | 5/5 | 0.50 ms | 1.93 ms | 5.60 ms |
| Switch: Bluetooth | 5/5 | 27.73 ms | 32.77 ms | 44.92 ms |
| Switch: second USB | 5/5 | 0.46 ms | 0.47 ms | 0.48 ms |

This completes the immediate Step 9 physical acceptance. The broader unpaired-host, sleep, reconnect, range-loss, radio-loss, and soak matrix remains under gate G3
and the later hardening steps.
