#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
dotnet restore src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj --locked-mode
bash eng/package-install-smoke.sh "$RUNNER_TEMP/package-install-smoke-run"
mkdir -p "$RUNNER_TEMP/package-install-smoke"
cp "$RUNNER_TEMP/package-install-smoke-run/feed/FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg" "$RUNNER_TEMP/package-install-smoke/"
