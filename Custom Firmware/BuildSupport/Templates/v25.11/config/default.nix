{ pkgs ? import <nixpkgs> {}
, firmware ? import /src {}
, shinyGo60Module ? /shinygo60-module
}:

let
  config = ./.;
  common = {
    keymap = "${config}/go60.keymap";
    kconfig = "${config}/go60.conf";
    extraModules = [ shinyGo60Module ];
  };

  go60_left = firmware.zmk.override (common // { board = "go60_lh"; });
  go60_right = firmware.zmk.override (common // { board = "go60_rh"; });

in firmware.combine_uf2 go60_left go60_right "go60"
