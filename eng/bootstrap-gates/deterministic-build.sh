#!/usr/bin/env bash
set -euo pipefail
source eng/bootstrap-gates/runner-temp.sh
fsgg_resolve_runner_temp
dotnet restore FS.GG.Coordination.sln --locked-mode
dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror
mkdir -p "$RUNNER_TEMP/deterministic-build"
cp src/FS.GG.Coordination.Protocol/bin/Release/net10.0/FS.GG.Coordination.Protocol.dll "$RUNNER_TEMP/deterministic-build/protocol.dll"
