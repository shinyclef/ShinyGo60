#!/bin/sh
set -eu
module=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
# Empty include placeholders: fake_zephyr.h supplies the driver boundary used by this test.
for header in bluetooth/conn.h bluetooth/gatt.h drivers/hwinfo.h init.h kernel.h random/random.h sys/byteorder.h; do
    mkdir -p "$scratch/zephyr/$(dirname "$header")"
    touch "$scratch/zephyr/$header"
done
"${1:-cc}" -std=c11 -Wall -Wextra -Werror -I"$scratch" -I"$module/zephyr/include" \
    "$module/tests/history/connection_history_test.c" -o "$scratch/history-test"
"$scratch/history-test"
