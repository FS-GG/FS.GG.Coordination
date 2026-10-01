#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
[[ -f eng/bootstrap-recovery.fsx ]] || { printf '%s\n' 'bootstrap-recovery: required subject eng/bootstrap-recovery.fsx is unavailable' >&2; exit 2; }
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/provision-quint.sh"
dotnet fsi eng/bootstrap-recovery.fsx -- .
mkdir -p "$RUNNER_TEMP/bootstrap-recovery"
cp artifacts/bootstrap-recovery/result.json "$RUNNER_TEMP/bootstrap-recovery/result.json"
