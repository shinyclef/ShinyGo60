# Builder input

The ShinyGo60 Builder looks here when a `.keymap` is not dropped onto or passed to the executable. Put exactly one MoErgo-exported `.keymap` in this directory,
then double-click `ShinyGo60.Builder.exe`.

If this folder contains more than one keymap, the builder asks you to select one instead of guessing. Runtime input files are ignored by Git.

Use `ShinyGo60 Builder.lnk` in the Go60 workspace. The published builder's `firmware-source.txt` points here, so each build uses the current maintained firmware
and this Input folder. Successful firmware and its matching manifest appear in the workspace's Output folder.
