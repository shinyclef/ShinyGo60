# v25.11 generated-workspace template

The future C# builder copies these files into a disposable workspace, adds the selected `.keymap` as `config/go60.keymap`, and invokes the pinned Docker backend.
The template includes the hand-maintained module through the read-only `/shinygo60-module` mount supplied by `Build-Firmware.ps1`.

The Nix result keeps the combined `go60.uf2` as its user-facing artifact and also retains each side's UF2, ELF, and resolved Kconfig for build verification. These
side-specific files are diagnostic evidence and are not separate files that the user must flash.
