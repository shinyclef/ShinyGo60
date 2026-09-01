# ShinyGo60 ZMK module

This is the hand-maintained out-of-tree firmware module. Zephyr discovers it through `zephyr/module.yml`; the pinned MoErgo Nix build receives it through its
`extraModules` argument.

`CONFIG_SHINYGO60` defaults to disabled. Step 4 includes the module in the baseline build while leaving that option disabled, proving module discovery without
changing firmware behavior. Step 5 will enable the first central-only feature and measure its flash and RAM cost.
