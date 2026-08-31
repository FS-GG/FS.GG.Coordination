#!/usr/bin/env bash
set -euo pipefail
root="${FSGG_RUNNER_TEMP:?FSGG_RUNNER_TEMP is required}"
repository="${FSGG_REPOSITORY:?FSGG_REPOSITORY is required}"
attributions="$(jq -er '.economics.attributionPath' eng/bootstrap-qualification-plan.json)"
jq -e '.schema == "fsgg.coordination.qualification-defect-attributions/1" and (.policyVersion | type == "string") and (.attributions | type == "array")' "$attributions" >/dev/null
mkdir -p "$root"
runs="$root/runs.json"; observations="$root/observations.ndjson"; array="$root/observations.json"
window_days="$(jq -er '.economics.windowDays' eng/bootstrap-qualification-plan.json)"
cutoff="$(date -u -d "$window_days days ago" +%Y-%m-%dT%H:%M:%SZ)"
now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
status=available
: >"$observations"
if ! gh api "repos/$repository/actions/workflows/bootstrap-qualification.yml/runs?per_page=100" >"$runs"; then
  status=unavailable
  printf '{"workflow_runs":[]}' >"$runs"
fi
while IFS=$'\t' read -r run_id attempt created_at event; do
  jobs="$root/jobs-$run_id.json"
  if ! gh api "repos/$repository/actions/runs/$run_id/jobs?filter=latest&per_page=100" >"$jobs"; then status=unavailable; continue; fi
  jq -c --argjson run "$run_id" --argjson attempt "$attempt" --slurpfile ledger "$attributions" '
    .jobs[] as $job
    | ([$ledger[0].attributions[] | select(.runId == $run and .gate == $job.name)]
       | if length == 0 then null elif length == 1 then .[0] else error("duplicate defect attribution") end) as $attribution
    | $job
    | select(.name == "deterministic-build" or .name == "compiler-and-tests" or .name == "canonical-quint" or .name == "dependency-and-security" or .name == "package-install-smoke" or .name == "bootstrap-recovery")
    | select(.status == "completed" and .started_at != null and .completed_at != null and (.completed_at|fromdateiso8601) >= (.started_at|fromdateiso8601))
    | {gate:.name,runId:$run,attempt:$attempt,observedAt:.completed_at,
       durationSeconds:((.completed_at|fromdateiso8601)-(.started_at|fromdateiso8601)),
       runnerMinutes:((((.completed_at|fromdateiso8601)-(.started_at|fromdateiso8601))/60)*1000000|round/1000000),
       reused:(.conclusion == "skipped"),
       outcome:(if .conclusion == "success" or .conclusion == "skipped" then "passed" elif $attribution == null then "unattributed-failure" else $attribution.outcome end),
       boundary:($attribution.boundary // "child"),
       closureEquivalent:($attribution.closureEquivalent // false),
       detectionDelayHours:($attribution.detectionDelayHours // null)}' "$jobs" >>"$observations"
done < <(jq -r --arg cutoff "$cutoff" '.workflow_runs[] | select(.created_at >= $cutoff and .event != "schedule") | [.id,.run_attempt,.created_at,.event] | @tsv' "$runs")
jq -s '.' "$observations" >"$array"
dotnet fsi eng/bootstrap-ci.fsx -- cadence --root . --observations "$array" --output "$root/recommendations.json" --now "$now" --data-status "$status"
printf 'QUALIFICATION_ECONOMICS_OK status=%s observations=%s\n' "$status" "$(jq length "$array")"
