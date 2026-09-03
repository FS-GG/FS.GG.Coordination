#!/usr/bin/env bash
set -euo pipefail

write_typed_decision() {
  local q7_decision="$1"
  local decision="$2"
  local full_suite="${3:-passed}"
  if ! jq -e '
      keys == ["fleetSelection","missedObligations","productionMutation","schema","seal"]
      and .schema == "fsgg.coordination.workflow-selection-supply-chain-decision/1"
      and (.seal | type == "string" and test("^[0-9a-f]{64}$"))
      and (.missedObligations | type == "array"
           and all(.[]; type == "string" and IN("build","test","policy","coordination","packaging","release"))
           and length == (unique | length))
      and (.fleetSelection == "eligible" or .fleetSelection == "disabled")
      and .productionMutation == false
      and ((.missedObligations | length == 0) == (.fleetSelection == "eligible"))
    ' "$q7_decision" >/dev/null; then
    printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"invalid-q7-decision"}\n' > "$decision"
    return 1
  fi
  jq -c --arg full_suite "$full_suite" '{schema:"fsgg.coordination.workflow-selection-sentinel/1",fullSuite:$full_suite,missedObligation:(.missedObligations|length>0),missedObligations,fleetSelection,productionMutation:false,q7Seal:.seal}' "$q7_decision" > "$decision"
  test "$(jq -r .fleetSelection "$q7_decision")" = "eligible"
}

compare_current_selection() {
  local selection="$1"
  local failures="$2"
  local decision="$3"
  local typed="$decision.typed"
  if ! jq -e '
      keys == ["aggregates","children","closure","graphVersion","inventorySeal","inventoryVersion","mergeGroupQueuedHead","roots","schema"]
      and .schema == "fsgg.coordination.workflow-selection-decision/1"
      and .inventoryVersion == "coordination-workflows/1"
      and .graphVersion == "fsgg.workflow-impact/1"
      and (.inventorySeal | test("^[0-9a-f]{64}$"))
      and (.closure | type == "array" and all(.[]; IN("build","test","policy","coordination","packaging","release")) and length == (unique|length))
    ' "$selection" >/dev/null \
    || ! jq -e 'type == "array" and all(.[]; type == "string" and IN("build","test","policy","coordination","packaging","release")) and length == (unique|length)' "$failures" >/dev/null; then
    printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"invalid-current-comparison"}\n' > "$decision"
    return 1
  fi
  jq -n -c --slurpfile selection "$selection" --slurpfile failures "$failures" '
      ($failures[0] - $selection[0].closure) as $missed
      | {schema:"fsgg.coordination.workflow-selection-supply-chain-decision/1",seal:$selection[0].inventorySeal,
         missedObligations:$missed,fleetSelection:(if ($missed|length)==0 then "eligible" else "disabled" end),productionMutation:false}
    ' > "$typed"
  local full_suite
  full_suite="$(jq -r 'if length == 0 then "passed" else "failed" end' "$failures")"
  write_typed_decision "$typed" "$decision" "$full_suite"
}

if [[ "${1:-}" == "--decision-only" && $# == 3 ]]; then
  write_typed_decision "$2" "$3"
  exit $?
fi

if [[ "${1:-}" == "--compare-selection" && $# == 4 ]]; then
  compare_current_selection "$2" "$3" "$4"
  exit $?
fi

output_root="${RUNNER_TEMP:-/tmp}/workflow-selection-sentinel"
mkdir -p "$output_root"
decision="$output_root/decision.json"
q7_decision="$output_root/q7-decision.json"
selection="$output_root/current-selection.json"
failures="$output_root/actual-failures.json"

set +e
dotnet build FS.GG.Coordination.sln -c Release --nologo /warnaserror
build_exit=$?
dotnet test FS.GG.Coordination.sln -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=full-suite.trx" --results-directory "$output_root"
test_exit=$?
RUNNER_TEMP="$output_root/policy" bash eng/bootstrap-gates/dependency-and-security.sh
policy_exit=$?
dotnet fsi eng/validate-github-workflow-selection.fsx -- .
q3_exit=$?
dotnet fsi eng/validate-github-workflow-selection-supply-chain.fsx -- . --decision "$q7_decision"
q7_exit=$?
bash eng/bootstrap-gates/package-install-smoke.sh
packaging_exit=$?
dotnet fsi eng/validate-github-release-hardening.fsx -- .
release_exit=$?
dotnet src/FS.GG.Coordination.Cli/bin/Release/net10.0/FS.GG.Coordination.Cli.dll workflow-select \
  --inventory evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json \
  --request evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json \
  --expected-inventory-version coordination-workflows/1 --expected-graph-version fsgg.workflow-impact/1 \
  --expected-seal ba78404be6abddc7f4bd2c057b19468b226f9b51fd9012a48b9d630ef5829421 \
  --current-base 57305e540267f3f4696ba5a6cdfc84361de577d3 \
  --current-settings 5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518 \
  --current-queued-head none > "$selection"
selection_exit=$?
set -e

if (( selection_exit != 0 )); then
  printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"current-selection-refused"}\n' > "$decision"
  exit 1
fi

jq -n -c \
  --argjson build "$build_exit" --argjson test "$test_exit" --argjson policy "$policy_exit" \
  --argjson q3 "$q3_exit" --argjson q7 "$q7_exit" --argjson packaging "$packaging_exit" --argjson release "$release_exit" '
  [if $build != 0 then "build" else empty end,
   if $test != 0 then "test" else empty end,
   if $policy != 0 then "policy" else empty end,
   if ($q3 != 0 or $q7 != 0) then "coordination" else empty end,
   if $packaging != 0 then "packaging" else empty end,
   if $release != 0 then "release" else empty end]
' > "$failures"

set +e
compare_current_selection "$selection" "$failures" "$decision"
comparison_exit=$?
set -e
if (( comparison_exit != 0 )) || jq -e 'length > 0' "$failures" >/dev/null; then exit 1; fi
exit 0
