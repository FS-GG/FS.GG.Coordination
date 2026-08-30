#!/usr/bin/env bash
set -euo pipefail
source eng/bootstrap-gates/runner-temp.sh
fsgg_resolve_runner_temp
dotnet restore FS.GG.Coordination.sln --locked-mode
dotnet fsi eng/verify-dependencies.fsx -- --root .
bash eng/bootstrap-gates/workflow-static.sh
mkdir -p "$RUNNER_TEMP/dependency-and-security"
dotnet list FS.GG.Coordination.sln package --vulnerable --include-transitive --format json --output-version 1 --source https://api.nuget.org/v3/index.json > "$RUNNER_TEMP/dependency-and-security/vulnerability-report.json"
dotnet fsi eng/bootstrap-ci.fsx -- vulnerability --root . --report "$RUNNER_TEMP/dependency-and-security/vulnerability-report.json"
