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
  combined = firmware.combine_uf2 go60_left go60_right "go60";

in combined.overrideAttrs (_: {
  buildCommand = ''
    mkdir -p $out
    cat ${go60_left}/zmk.uf2 ${go60_right}/zmk.uf2 > $out/go60.uf2
    cp ${go60_left}/zmk.uf2 $out/go60_lh.uf2
    cp ${go60_right}/zmk.uf2 $out/go60_rh.uf2
    cp ${go60_left}/zmk.elf $out/go60_lh.elf
    cp ${go60_right}/zmk.elf $out/go60_rh.elf
    cp ${go60_left}/zmk.kconfig $out/go60_lh.kconfig
    cp ${go60_right}/zmk.kconfig $out/go60_rh.kconfig
  '';
})
