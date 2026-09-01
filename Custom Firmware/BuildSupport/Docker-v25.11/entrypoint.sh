#!/usr/bin/env bash

set -euo pipefail

readonly expected_commit="11454d23596afbdb06380a1125371b19ab65675c"
readonly actual_commit="$(cat /src/.shinygo60-zmk-commit)"

if [[ "${actual_commit}" != "${expected_commit}" ]]; then
    echo "Pinned ZMK source verification failed." >&2
    exit 1
fi

if [[ ! -f /config/config/default.nix || ! -f /config/config/go60.keymap || ! -f /config/config/go60.conf ]]; then
    echo "The mounted build workspace must contain config/default.nix, config/go60.keymap, and config/go60.conf." >&2
    exit 2
fi

: "${UID:=0}"
: "${GID:=0}"

echo "Building Go60 firmware from pinned ZMK commit ${actual_commit}" >&2

cd /config
nix-build --option substituters "" ./config --arg firmware 'import /src/default.nix {}' -j2 -o /tmp/combined --show-trace
install -m 0644 -o "${UID}" -g "${GID}" /tmp/combined/go60.uf2 ./go60.uf2.shinygo60-new
mv -f ./go60.uf2.shinygo60-new ./go60.uf2
