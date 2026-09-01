#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
quint_sha="939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
quint_root="$RUNNER_TEMP/fsgg-quint-0.32.0" quint_bin="$RUNNER_TEMP/fsgg-quint-0.32.0/quint"
if [[ ! -x "$quint_bin" ]]; then
  mkdir -p "$quint_root"
  curl --fail --location --retry 5 --retry-all-errors --silent --show-error "https://github.com/quint-co/quint/releases/download/v0.32.0/quint-linux-amd64" --output "$quint_bin"
  printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status
  chmod +x "$quint_bin"
fi
printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status
export PATH="$quint_root:$PATH"
