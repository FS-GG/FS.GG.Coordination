#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
  echo "ORCHESTRATION_PILOT_POSTGRESQL_REFUSED identity-bound qualification requires a clean committed candidate" >&2
  exit 3
fi
results="$RUNNER_TEMP/orchestration-pilot-postgresql"
mkdir -p "$results"
project="tests/FS.GG.Coordination.Orchestration.Pilot.PostgreSql.Tests/FS.GG.Coordination.Orchestration.Pilot.PostgreSql.Tests.fsproj"
"${DOTNET_EXE:-dotnet}" restore "$project" --locked-mode
(
  cd tests/FS.GG.Coordination.Orchestration.Pilot.PostgreSql.Tests
  bash run-private-postgres.sh \
    --no-restore \
    --logger "trx;LogFileName=results.trx" \
    --results-directory "$results"
)
test -s "$results/results.trx"
