#!/usr/bin/env bash
set -euo pipefail
dotnet restore FS.GG.Coordination.sln --locked-mode
dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror
dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj --configuration Release --no-build --no-restore
dotnet test tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj --configuration Release --no-build --no-restore --logger "trx;LogFileName=architecture-tests.trx" --results-directory artifacts/test-results/70-gs2-03-1-qualification-manifest
mkdir -p "$RUNNER_TEMP/compiler-and-tests"
cp artifacts/test-results/70-gs2-03-1-qualification-manifest/architecture-tests.trx "$RUNNER_TEMP/compiler-and-tests/architecture.trx"
