#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
model_dir=${1:-${CINEKROS_E5_MODEL_DIR:-}}
if [ -z "$model_dir" ] || [ ! -d "$model_dir" ]; then
    echo "Usage: verify-model.sh MODEL_DIRECTORY" >&2
    exit 2
fi

manifest="$script_dir/model.SHA256SUMS"
expected=$(mktemp)
actual=$(mktemp)
trap 'rm -f "$expected" "$actual"' EXIT HUP INT TERM

awk '{print $2}' "$manifest" | LC_ALL=C sort > "$expected"
find "$model_dir" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort > "$actual"
if ! diff -u "$expected" "$actual"; then
    echo "Model directory must contain exactly the five artifacts in model.SHA256SUMS." >&2
    exit 1
fi

(
    cd "$model_dir"
    sha256sum --check --strict "$manifest"
)
