# Step 7 keymap inspection and layout manifest

Status: complete

Recorded: 2026-09-02 on Windows 11

## Purpose

Step 7 creates the identity shared by a source keymap, its future firmware build, and the Windows companion. It reads only the small amount of generated metadata
needed by ShinyGo60. Macros, combos, hold-taps, input processors, bindings, and other Devicetree content remain opaque and are copied byte-for-byte.

## Accepted input contract

`Go60KeymapInspector` requires all of these signals:

- A UTF-8 file with the `.keymap` extension and a Go60 Layout Editor export marker.
- `KB_TYPE_GO_60` definitions selecting the Go60 keyboard type.
- Exactly one complete `zmk,keymap` node.
- Unique generated `LAYER_<name> <number>` definitions with contiguous IDs starting at zero.
- Matching top-level `layer_<name>` nodes in numeric order.
- Exactly 60 behavior references in every layer's `bindings` property.

Comments, quoted braces, harmless whitespace changes, and an optional keymap node label do not affect metadata extraction. Missing, duplicated, mismatched, truncated,
non-UTF-8, or non-Go60 inputs fail with an actionable message. This is a targeted metadata reader, not a general Devicetree parser or rewriter.

## Identity contract

The exact source bytes are hashed with SHA-256 and copied without decoding or rewriting them. The version-one layout identifier is:

```text
sg60-v1-<first 128 bits of SHA-256(domain || protocol version || exact keymap bytes)>
```

The domain separates this identifier from unrelated hashes. The protocol major and minor numbers are encoded as two big-endian 16-bit integers. Reordering layers,
changing any source byte, or changing the protocol version therefore changes the identifier. The display-only build timestamp and firmware source revision do not.

The generator writes three matched files into a caller-selected generated directory:

| File | Purpose |
| --- | --- |
| Original `.keymap` filename | Exact byte-for-byte source copy for the firmware build |
| `layout-manifest.json` | Schema, protocol, layout ID, full keymap hash, firmware revision, ordered layers, and UTC build time |
| `shinygo60_layout.h` | The same layout ID and full keymap hash as C macros for later firmware integration |

`LayoutManifestJson` is the shared strict JSON writer and reader. It rejects unknown fields, unsupported schemas, malformed identifiers or hashes, non-UTC timestamps,
and empty, duplicated, or non-contiguous layer mappings.

## Current keymap evidence

| Measurement | Value |
| --- | --- |
| Source size | 101,084 bytes |
| Extracted layers | 22 |
| Protocol version | `0.1` |
| Keymap SHA-256 | `ab526e96c32048301990b09309bfab7f2b6a1323ccbc07892aac43dab6c6b7f7` |
| Layout identifier | `sg60-v1-b4c690cedfc730f31f0dbfb696b59779` |
| Firmware source revision recorded by the fixture | `11454d23596afbdb06380a1125371b19ab65675c` |

The extracted mapping is:

| ID | Name | ID | Name |
| ---: | --- | ---: | --- |
| 0 | `Home` | 11 | `MouseSlow` |
| 1 | `NoHRM` | 12 | `MouseFast` |
| 2 | `Qwerty` | 13 | `MouseWarp` |
| 3 | `Navigation` | 14 | `LeftPinky` |
| 4 | `Keypad` | 15 | `LeftRingy` |
| 5 | `Shortcuts` | 16 | `LeftMiddy` |
| 6 | `WindowsAndSymbols` | 17 | `LeftIndex` |
| 7 | `Gaming` | 18 | `RightPinky` |
| 8 | `GamingShortcuts` | 19 | `RightRingy` |
| 9 | `Magic` | 20 | `RightMiddy` |
| 10 | `Mouse` | 21 | `RightIndex` |

## Verification

- The complete eight-project Release solution builds with zero warnings and zero errors.
- All 6 offline checks pass.
- The current 2,000-line export produces the mapping above from a path containing spaces and a Unicode superscript.
- The generated keymap copy is byte-for-byte identical, and generation leaves the source unchanged.
- Manifest JSON writes and reads through the shared strict contract; the generated C header contains the same ID and hash.
- Reordered layers and a changed protocol version produce different layout identifiers.
- Alternate whitespace and node-label formatting remain accepted.
- Truncated bindings, duplicated numeric IDs, and invalid UTF-8 fail with focused messages.
- `dotnet format --verify-no-changes` passes, and all changed C# lines remain at or below 160 characters.

Step 7 is software-only. It changes no firmware behavior and requires no flash. Step 8 will place these artifacts into an atomic generated workspace and embed the
header in the pinned firmware build.
