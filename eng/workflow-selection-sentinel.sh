#!/usr/bin/env bash
set -euo pipefail

output_root="${RUNNER_TEMP:-/tmp}/workflow-selection-sentinel"
mkdir -p "$output_root"
decision="$output_root/decision.json"

set +e
dotnet test FS.GG.Coordination.sln -c Release --nologo --logger "trx;LogFileName=full-suite.trx" --results-directory "$output_root"
test_exit=$?
dotnet fsi eng/validate-github-workflow-selection.fsx -- .
q3_exit=$?
dotnet fsi eng/validate-github-workflow-selection-supply-chain.fsx -- .
q7_exit=$?
set -e

if (( test_exit == 0 && q3_exit == 0 && q7_exit == 0 )); then
  printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"passed","missedObligation":false,"fleetSelection":"eligible","productionMutation":false}\n' > "$decision"
  exit 0
fi

printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"testExit":%d,"q3Exit":%d,"q7Exit":%d}\n' "$test_exit" "$q3_exit" "$q7_exit" > "$decision"
exit 1
