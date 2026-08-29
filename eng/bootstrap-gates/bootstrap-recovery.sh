#!/usr/bin/env bash
set -euo pipefail
dotnet fsi eng/bootstrap-recovery.fsx -- .
mkdir -p "$RUNNER_TEMP/bootstrap-recovery"
cp artifacts/bootstrap-recovery/result.json "$RUNNER_TEMP/bootstrap-recovery/result.json"
