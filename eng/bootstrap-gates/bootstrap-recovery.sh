#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
dotnet fsi eng/bootstrap-recovery.fsx -- .
mkdir -p "$RUNNER_TEMP/bootstrap-recovery"
cp artifacts/bootstrap-recovery/result.json "$RUNNER_TEMP/bootstrap-recovery/result.json"
