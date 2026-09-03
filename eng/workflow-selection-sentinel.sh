#!/usr/bin/env bash
set -euo pipefail

write_typed_decision() {
  local q7_decision="$1"
  local decision="$2"
  if ! jq -e '
      .schema == "fsgg.coordination.workflow-selection-supply-chain-decision/1"
      and (.seal | type == "string" and length == 64)
      and (.missedObligations | type == "array")
      and (.fleetSelection == "eligible" or .fleetSelection == "disabled")
      and .productionMutation == false
      and ((.missedObligations | length == 0) == (.fleetSelection == "eligible"))
    ' "$q7_decision" >/dev/null; then
    printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"invalid-q7-decision"}\n' > "$decision"
    return 1
  fi
  jq -c '{schema:"fsgg.coordination.workflow-selection-sentinel/1",fullSuite:"passed",missedObligation:(.missedObligations|length>0),missedObligations,fleetSelection,productionMutation:false,q7Seal:.seal}' "$q7_decision" > "$decision"
  test "$(jq -r .fleetSelection "$q7_decision")" = "eligible"
}

if [[ "${1:-}" == "--decision-only" && $# == 3 ]]; then
  write_typed_decision "$2" "$3"
  exit $?
fi

output_root="${RUNNER_TEMP:-/tmp}/workflow-selection-sentinel"
mkdir -p "$output_root"
decision="$output_root/decision.json"
q7_decision="$output_root/q7-decision.json"

set +e
dotnet test FS.GG.Coordination.sln -c Release --nologo --logger "trx;LogFileName=full-suite.trx" --results-directory "$output_root"
test_exit=$?
dotnet fsi eng/validate-github-workflow-selection.fsx -- .
q3_exit=$?
dotnet fsi eng/validate-github-workflow-selection-supply-chain.fsx -- . --decision "$q7_decision"
q7_exit=$?
set -e

if (( test_exit == 0 && q3_exit == 0 && q7_exit == 0 )); then
  write_typed_decision "$q7_decision" "$decision"
  exit $?
fi

printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"testExit":%d,"q3Exit":%d,"q7Exit":%d}\n' "$test_exit" "$q3_exit" "$q7_exit" > "$decision"
exit 1
