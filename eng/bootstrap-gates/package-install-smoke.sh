#!/usr/bin/env bash
set -euo pipefail
dotnet restore src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj --locked-mode
bash eng/package-install-smoke.sh "$RUNNER_TEMP/package-install-smoke-run"
mkdir -p "$RUNNER_TEMP/package-install-smoke"
cp "$RUNNER_TEMP/package-install-smoke-run/feed/FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg" "$RUNNER_TEMP/package-install-smoke/"
