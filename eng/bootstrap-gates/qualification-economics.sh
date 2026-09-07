#!/usr/bin/env bash
set -euo pipefail
if [[ -n "${RUNNER_TEMP:-}" ]]; then
  root="$RUNNER_TEMP/qualification-economics"
else
  root="${FSGG_RUNNER_TEMP:?RUNNER_TEMP or FSGG_RUNNER_TEMP is required}"
fi
repository="${FSGG_REPOSITORY:?FSGG_REPOSITORY is required}"
attributions="$(jq -er '.economics.attributionPath' eng/bootstrap-qualification-plan.json)"
jq -e '.schema == "fsgg.coordination.qualification-defect-attributions/1" and (.policyVersion | type == "string") and (.attributions | type == "array")' "$attributions" >/dev/null
mkdir -p "$root"
runs_pages="$root/run-pages.json"; runs="$root/runs.json"
observations="$root/observations.ndjson"; array="$root/observations.json"
completeness="$root/completeness.json"
window_days="$(jq -er '.economics.windowDays' eng/bootstrap-qualification-plan.json)"
cutoff="$(date -u -d "$window_days days ago" +%Y-%m-%dT%H:%M:%SZ)"
now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
status=available
failed_requests=0
attempts_expected=0
attempts_observed=0
: >"$observations"
if gh api --method GET --paginate --slurp \
    "repos/$repository/actions/workflows/bootstrap-qualification.yml/runs" \
    -f per_page=100 -f created=">=$cutoff" >"$runs_pages"; then
  jq '{workflow_runs:([.[].workflow_runs[]] | unique_by(.id) | sort_by(.created_at,.id))}' "$runs_pages" >"$runs"
else
  status=unavailable
  failed_requests=$((failed_requests + 1))
  printf '{"workflow_runs":[]}' >"$runs"
fi
attempts="$root/attempts.tsv"
jq -r --arg cutoff "$cutoff" '
  .workflow_runs[]
  | select(.created_at >= $cutoff and .event != "schedule")
  | . as $run
  | range(1; (($run.run_attempt // 1) + 1))
  | [$run.id,.]
  | @tsv' "$runs" >"$attempts"
attempts_expected="$(wc -l <"$attempts" | tr -d ' ')"

fetch_attempt_jobs() {
  local run_id="$1" attempt="$2" target="$root/job-pages-$1-$2.json" temporary="$root/job-pages-$1-$2.tmp"
  if gh api --method GET --paginate --slurp \
      "repos/$repository/actions/runs/$run_id/attempts/$attempt/jobs" \
      -f per_page=100 >"$temporary"; then
    mv "$temporary" "$target"
  else
    rm -f "$temporary"
    return 1
  fi
}
export -f fetch_attempt_jobs
export root repository
if ! xargs -r -P 8 -n 2 bash -c 'fetch_attempt_jobs "$1" "$2"' _ <"$attempts"; then
  [[ "$status" == unavailable ]] || status=partial
fi

while IFS=$'\t' read -r run_id attempt; do
  jobs_pages="$root/job-pages-$run_id-$attempt.json"
  jobs="$root/jobs-$run_id-$attempt.json"
  if [[ ! -f "$jobs_pages" ]]; then
    [[ "$status" == unavailable ]] || status=partial
    failed_requests=$((failed_requests + 1))
    continue
  fi
  jq '{jobs:([.[].jobs[]] | unique_by(.id) | sort_by(.name,.id))}' "$jobs_pages" >"$jobs"
  attempts_observed=$((attempts_observed + 1))
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
done <"$attempts"
jq -s '.' "$observations" >"$array"
runs_enumerated="$(jq '[.workflow_runs[] | select(.created_at >= $cutoff and .event != "schedule")] | length' --arg cutoff "$cutoff" "$runs")"
jq -n --arg cutoff "$cutoff" --arg now "$now" --arg status "$status" \
  --argjson runs "$runs_enumerated" --argjson expected "$attempts_expected" \
  --argjson observed "$attempts_observed" --argjson failed "$failed_requests" \
  '{schema:"fsgg.coordination.qualification-census-completeness/1",
    windowStart:$cutoff,windowEnd:$now,status:$status,runsEnumerated:$runs,
    attemptsExpected:$expected,attemptsObserved:$observed,failedRequests:$failed,
    complete:($status == "available" and $expected == $observed and $failed == 0)}' >"$completeness"
dotnet fsi eng/bootstrap-ci.fsx -- cadence --root . --observations "$array" \
  --completeness "$completeness" --output "$root/recommendations.json" --now "$now" --data-status "$status"
printf 'QUALIFICATION_ECONOMICS_OK status=%s runs=%s attempts=%s/%s observations=%s\n' \
  "$status" "$runs_enumerated" "$attempts_observed" "$attempts_expected" "$(jq length "$array")"
