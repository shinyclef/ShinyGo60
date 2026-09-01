# Custom firmware workspace

The custom firmware is deliberately split into three ownership areas:

- `Module` contains hand-maintained ShinyGo60 ZMK source.
- `BuildSupport` contains pinned build definitions, templates, measurements, and orchestration scripts.
- `Generated` is ignored disposable state created from a `.keymap`; deleting it must never remove maintained source.

Firmware output and compiler logs belong in the ignored repository-level `Output` directory. The generated workspace may contain copied templates, copied module
input, Nix results, and a temporary UF2, but none of those files is authoritative.

The version-one builder must preserve this boundary: read one exported `.keymap`, create a clean generated workspace, build through the pinned image, and copy the
matched UF2, manifest, and readable log to `Output`.
