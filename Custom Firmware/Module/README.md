# ShinyGo60 ZMK module

This is the hand-maintained out-of-tree firmware module. Zephyr discovers it through `zephyr/module.yml`; the pinned MoErgo Nix build receives it through its
`extraModules` argument.

`CONFIG_SHINYGO60` enables the integration. Its internal `CONFIG_SHINYGO60_CENTRAL` option selects runtime code only for the left/central Go60 image; the
right/peripheral image compiles no ShinyGo60 source.

Step 5 adds a deliberately harmless diagnostic containing feature version `0.1.0-step5`, a fixed test layout identifier, and a checksum of that identity. The
diagnostic is available only to firmware code. It does not create a USB or Bluetooth service, send host traffic, or alter keymap behavior.
