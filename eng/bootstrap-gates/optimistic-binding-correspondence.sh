#!/usr/bin/env bash
set -euo pipefail
prior="${1:?prior bindings required}"
current="${2:?current bindings required}"
prior_projection="${RUNNER_TEMP:-/tmp}/prior-binding-semantic.json"
current_projection="${RUNNER_TEMP:-/tmp}/current-binding-semantic.json"
jq -S 'walk(if type == "object" then del(.source) else . end)' "$prior" > "$prior_projection"
jq -S 'walk(if type == "object" then del(.source) else . end)' "$current" > "$current_projection"
cmp -s "$prior_projection" "$current_projection"
sha256sum "$prior_projection" | cut -d' ' -f1
