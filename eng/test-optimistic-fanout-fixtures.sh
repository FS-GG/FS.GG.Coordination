#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
export RUNNER_TEMP="$scratch/runner"
export FSGG_FORMAL_FRAGMENT_ROOT="$scratch/fragments"
export FSGG_FORMAL_OUTPUT_ROOT="$scratch/output"
plan_root="$RUNNER_TEMP/optimistic-validation"
mkdir -p "$plan_root" "$FSGG_FORMAL_FRAGMENT_ROOT"
candidate="$(printf 'a%.0s' {1..40})"
obligation="$(printf 'b%.0s' {1..64})"
formal_digest="$(printf 'c%.0s' {1..64})"
printf '{"candidate":"%s","obligationSha256":"%s","sourceSha256":"%s","compiledContractSha256":"%s"}\n' "$candidate" "$obligation" "$formal_digest" "$formal_digest" > "$plan_root/candidate-obligation.json"
printf '{"schema":"fsgg.coordination.coherent-partition-plan/1","candidateObligationSha256":"%s","qualificationPlanSha256":"%s","partitions":[{"index":1,"obligations":["formal"]}]}\n' "$obligation" "$formal_digest" > "$plan_root/partition-plan.json"

mapfile -t semantic < <(jq -r '.formalFanout.semanticShards[]' "$repo/eng/optimistic-qualification-plan.json")
performance="$(jq -r '.formalFanout.performanceShard' "$repo/eng/optimistic-qualification-plan.json")"
for id in "${semantic[@]}" "$performance"; do
  dir="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$id"
  mkdir "$dir"
  cp "$plan_root/candidate-obligation.json" "$dir/candidate-obligation.json"
  cp "$plan_root/partition-plan.json" "$dir/partition-plan.json"
  if [[ "$id" == base ]]; then
    jq -n --arg digest "$(printf 'c%.0s' {1..64})" '
      {schema:"fsgg.coordination.canonical-quint-qualification/1",q1Outcome:"passed",q2Outcome:"passed",
       positiveInvariantCount:8,negativeControlCount:71,preparationDurationMs:1,q2DurationMs:1,totalDurationMs:2,
       processCounts:{external:1,quintCli:1,apalacheVerify:1},processAccounting:"logical-invocations-plus-explicit-startup-retries/v1",
       physicalProcessCounts:{external:1,quintCli:1,apalacheVerify:1},
       startupRetries:{total:0,verify:0,reflectionDeadline:0,earlyLifecycleExit:0},formalCounterexamples:[],
       tools:{toolchainSha256:$digest,quintSha256:$digest,apalacheJarSha256:$digest},
       inputs:{sourceSha256:$digest,contractSha256:$digest},preparationSha256:$digest,failure:null,resultSha256:$digest}' > "$dir/receipt.json"
  else
    jq -n --arg id "$id" --arg digest "$(printf 'c%.0s' {1..64})" '
      {schema:"fsgg.coordination.canonical-quint-formal-shard/1",id:$id,outcome:"passed",
       accountingMethod:"logical-formal-contribution-and-observed-execution/v1",negativeControlCount:5,
       processCounts:{external:7,quintCli:7,apalacheVerify:3},
       startupRetries:{total:0,verify:0,reflectionDeadline:0,earlyLifecycleExit:0},
       executedProcessCounts:{external:10,quintCli:10,apalacheVerify:3},q2DurationMs:1,elapsedMs:1,
       toolchainSha256:$digest,quintSha256:$digest,apalacheJarSha256:$digest,
       sourceSha256:$digest,contractSha256:$digest,preparationSha256:$digest,
       manifestSha256:$digest,traceSha256:$digest,itfSha256:$digest}' > "$dir/receipt.json"
  fi
done
printf '{"schema":"fsgg.coordination.canonical-quint-performance/1","outcome":"passed","shardCount":19,"epochBudgetMs":300000}\n' > "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$performance/performance.json"

run_validate() { (cd "$repo" && bash eng/optimistic-aggregate-formal.sh validate-only) >/dev/null 2>&1; }
run_validate

missing="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-${semantic[1]}"
mv "$missing" "$missing.hidden"
if run_validate; then echo "missing fragment was accepted" >&2; exit 1; fi
mv "$missing.hidden" "$missing"

mkdir "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-duplicate"
if run_validate; then echo "foreign or duplicate fragment was accepted" >&2; exit 1; fi
rmdir "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-duplicate"

failed="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-${semantic[2]}/receipt.json"
sed -i 's/"passed"/"failed"/' "$failed"
if run_validate; then echo "failed fragment was accepted" >&2; exit 1; fi
sed -i 's/"failed"/"passed"/' "$failed"

stale="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-${semantic[3]}/partition-plan.json"
printf '{}\n' > "$stale"
if run_validate; then echo "stale fragment envelope was accepted" >&2; exit 1; fi
cp "$plan_root/partition-plan.json" "$stale"
run_validate

base_receipt="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-base/receipt.json"
sed -i "s/$formal_digest/$(printf 'd%.0s' {1..64})/g" "$base_receipt"
if run_validate; then echo "stale canonical base was accepted" >&2; exit 1; fi
sed -i "s/$(printf 'd%.0s' {1..64})/$formal_digest/g" "$base_receipt"

(cd "$repo" && bash eng/optimistic-aggregate-formal.sh formal-only) >/dev/null
test -s "$FSGG_FORMAL_OUTPUT_ROOT/qualification.json"
test -s "$FSGG_FORMAL_OUTPUT_ROOT/accounting.json"

trx="$scratch/results.trx"
started="$(date +%s%N)"
printf '<TestRun><ResultSummary><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>\n' > "$trx"
python "$repo/eng/validate-test-census.py" "$trx" unit "$started" >/dev/null
printf '<TestRun><ResultSummary><Counters total="0" passed="0" failed="0" /></ResultSummary></TestRun>\n' > "$trx"
if python "$repo/eng/validate-test-census.py" "$trx" unit "$started" >/dev/null 2>&1; then echo "empty census was accepted" >&2; exit 1; fi
printf '<TestRun><ResultSummary><Counters total="1" passed="0" failed="1" /></ResultSummary></TestRun>\n' > "$trx"
if python "$repo/eng/validate-test-census.py" "$trx" unit "$started" >/dev/null 2>&1; then echo "failed census was accepted" >&2; exit 1; fi
touch -d '@1' "$trx"
if python "$repo/eng/validate-test-census.py" "$trx" unit "$started" >/dev/null 2>&1; then echo "stale census was accepted" >&2; exit 1; fi
if python "$repo/eng/validate-test-census.py" "$scratch/missing.trx" unit "$started" >/dev/null 2>&1; then echo "missing census was accepted" >&2; exit 1; fi

export CLASSIFY_MOCK_ROOT="$scratch/classify-mock"
mkdir -p "$CLASSIFY_MOCK_ROOT/prior"
printf '{"qualificationPlanSha256":"%s"}\n' "$(printf 'e%.0s' {1..64})" > "$CLASSIFY_MOCK_ROOT/prior/partition-plan.json"
printf '{}\n' > "$CLASSIFY_MOCK_ROOT/prior/candidate-obligation.json"
printf '{}\n' > "$CLASSIFY_MOCK_ROOT/prior/coherent-aggregate-receipt.json"
python - "$CLASSIFY_MOCK_ROOT/prior" "$CLASSIFY_MOCK_ROOT/prior.zip" <<'PY'
import pathlib, sys, zipfile
source, target = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
with zipfile.ZipFile(target, "w") as archive:
    for path in source.glob("*.json"):
        archive.write(path, path.name)
PY
jq -n --arg head "$(printf 'f%.0s' {1..40})" '{artifacts:[range(1;31) | {id:.,expired:false,name:("coherent-aggregate-" + ($head)),workflow_run:{id:.,head_sha:$head},created_at:"2026-09-15T00:00:00Z",expires_at:"2026-12-15T00:00:00Z"}]}' > "$CLASSIFY_MOCK_ROOT/artifact-page.json"
gh() {
  if [[ "$*" == *"actions/artifacts?per_page=100"* ]]; then
    printf 'page\n' >> "$CLASSIFY_MOCK_ROOT/pages"
    if [[ "${CLASSIFY_MOCK_FAIL:-}" == 1 ]]; then return 1; fi
    /usr/bin/cat "$CLASSIFY_MOCK_ROOT/artifact-page.json"
  elif [[ "$*" == *"/zip"* ]]; then
    printf 'download\n' >> "$CLASSIFY_MOCK_ROOT/downloads"
    /usr/bin/cat "$CLASSIFY_MOCK_ROOT/prior.zip"
  else
    printf 'unexpected-api %s\n' "$*" >> "$CLASSIFY_MOCK_ROOT/forbidden"
    echo "unexpected unbounded classifier API call: $*" >&2
    return 1
  fi
}
dotnet() {
  if [[ "$*" == *"validate-prior"* ]]; then
    printf 'typed-prior\n' >> "$CLASSIFY_MOCK_ROOT/forbidden"
    echo "mismatched plan reached typed prior validation" >&2
    return 1
  fi
  if [[ "$*" == *"--prior-"* ]]; then
    printf 'prior-authority\n' >> "$CLASSIFY_MOCK_ROOT/forbidden"
    echo "old-plan fallback attached prior authority" >&2
    return 1
  fi
  local output next=false
  for argument in "$@"; do
    if $next; then output="$argument"; next=false; elif [[ "$argument" == --output ]]; then next=true; fi
  done
  printf '{"disposition":"current"}\n' > "$output"
}
export -f gh dotnet
export GH_TOKEN=fixture GITHUB_REPOSITORY=FS-GG/FS.GG.Coordination GITHUB_RUN_ID=999
(cd "$repo" && bash eng/bootstrap-gates/optimistic-classify.sh) >/dev/null 2>&1
test "$(wc -l < "$CLASSIFY_MOCK_ROOT/downloads")" -eq 25
test "$(wc -l < "$CLASSIFY_MOCK_ROOT/pages")" -eq 1
test ! -e "$CLASSIFY_MOCK_ROOT/forbidden"
test "$(jq -r '.disposition' "$RUNNER_TEMP/optimistic-selection/selection.json")" = current
rm "$CLASSIFY_MOCK_ROOT/downloads" "$RUNNER_TEMP/optimistic-selection/selection.json"
export CLASSIFY_MOCK_FAIL=1
(cd "$repo" && bash eng/bootstrap-gates/optimistic-classify.sh) >/dev/null 2>&1
test ! -e "$CLASSIFY_MOCK_ROOT/downloads"
test "$(jq -r '.disposition' "$RUNNER_TEMP/optimistic-selection/selection.json")" = current

export RECOVERY_MOCK_ROOT="$scratch/recovery-mock"
mkdir -p "$RECOVERY_MOCK_ROOT"
export RECOVERY_MAIN="$(printf 'a%.0s' {1..40})"
passed_candidate="$(printf '1%.0s' {1..40})"
pending_candidate="$(printf '2%.0s' {1..40})"
active_candidate="$(printf '3%.0s' {1..40})"
jq -n --arg passed "$passed_candidate" --arg pending "$pending_candidate" --arg active "$active_candidate" \
  '[[{created_at:"2026-09-20T00:00:00Z",head:{sha:$passed}},
     {created_at:"2026-09-21T00:00:00Z",head:{sha:$pending}},
     {created_at:"2026-09-22T00:00:00Z",head:{sha:$active}}]]' > "$RECOVERY_MOCK_ROOT/pulls.json"
printf '[{"workflow_runs":[]}]\n' > "$RECOVERY_MOCK_ROOT/pending-runs.json"
jq -n --arg active "$active_candidate" '{total_count:1,workflow_runs:[{status:"queued",head_sha:$active}]}' > "$RECOVERY_MOCK_ROOT/queued-runs.json"
export RECOVERY_QUEUE=2
gh() {
  printf '%s\n' "$*" >> "$RECOVERY_MOCK_ROOT/calls"
  case "$*" in
    *"status=queued&per_page=2"*)
      if [[ "$RECOVERY_QUEUE" == 1 ]]; then cat "$RECOVERY_MOCK_ROOT/queued-runs.json"
      else printf '{"total_count":2,"workflow_runs":[]}\n'; fi ;;
    *"status=in_progress&per_page=2"*) printf '{"total_count":0,"workflow_runs":[]}\n' ;;
    *"status=queued&per_page=1"*) printf '%s\n' "$RECOVERY_QUEUE" ;;
    *"status=in_progress&per_page=1"*) printf '0\n' ;;
    *"pulls?state=open"*) cat "$RECOVERY_MOCK_ROOT/pulls.json" ;;
    *"actions/runs?status=pending"*) cat "$RECOVERY_MOCK_ROOT/pending-runs.json" ;;
    *"optimistic-parallel-validation.yml/runs?event=schedule&per_page=100"*)
      if [[ -n "${RECOVERY_CURSOR_ZIP:-}" && "${RECOVERY_CURSOR_FORGED:-}" != 1 ]]; then
        printf '{"workflow_runs":[{"id":99,"event":"schedule","status":"completed","head_branch":"main","head_sha":"%s"}]}\n' "$RECOVERY_MAIN"
      else printf '{"workflow_runs":[]}\n'; fi ;;
    *"actions/artifacts?name=optimistic-recovery-cursor"*)
      if [[ -n "${RECOVERY_CURSOR_ZIP:-}" ]]; then
        printf '{"artifacts":[{"id":999,"name":"optimistic-recovery-cursor","expired":false,"created_at":"2026-09-25T00:00:00Z","workflow_run":{"id":99,"head_sha":"%s"}}]}\n' "$RECOVERY_MAIN"
      else printf '{"artifacts":[]}\n'; fi ;;
    *"actions/artifacts/999/zip"*) cat "$RECOVERY_CURSOR_ZIP" ;;
    *"actions/artifacts?name=coherent-aggregate-"*)
      local matched="${2#*coherent-aggregate-}"
      matched="${matched%%&*}"
      if [[ -n "${RECOVERY_UNPASSED:-}" && "$matched" == "$RECOVERY_UNPASSED" ]]; then
        printf '{"artifacts":[]}\n'
      elif [[ "${RECOVERY_ALL_PASSED:-}" == 1 ]]; then
        printf '{"artifacts":[{"name":"coherent-aggregate-%s","expired":false}]}\n' "$matched"
      elif [[ "$*" == *"$RECOVERY_PASSED"* ]]; then
        printf '{"artifacts":[{"name":"coherent-aggregate-%s","expired":false}]}\n' "$RECOVERY_PASSED"
      else printf '{"artifacts":[]}\n'; fi ;;
    *"workflow run optimistic-parallel-validation.yml"*) printf '%s\n' "$*" >> "$RECOVERY_MOCK_ROOT/dispatches" ;;
    *) echo "unexpected recovery API call: $*" >&2; return 1 ;;
  esac
}
git() {
  case "$1" in
    fetch) return 0 ;;
    rev-list|rev-parse) printf '%s\n' "$RECOVERY_MAIN" ;;
    merge-base) return 0 ;;
    cat-file) return 0 ;;
    *) echo "unexpected recovery git call: $*" >&2; return 1 ;;
  esac
}
export -f gh git
export RECOVERY_PASSED="$passed_candidate"
export RUNNER_TEMP="$scratch/recovery-runner"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null
test ! -s "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt"
test "$(wc -l < "$RECOVERY_MOCK_ROOT/calls")" -eq 2
before="$(wc -l < "$RECOVERY_MOCK_ROOT/calls")"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-dispatch-recovery.sh "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt") >/dev/null
test "$(wc -l < "$RECOVERY_MOCK_ROOT/calls")" -eq "$before"
export RECOVERY_QUEUE=1
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null
test "$(cat "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt")" = "$pending_candidate"
test "$(jq -r '.[0].candidate' "$RUNNER_TEMP/optimistic-validation/pending-candidates.json")" = "$pending_candidate"
test "$(rg -F -c 'actions/artifacts?name=coherent-aggregate-' "$RECOVERY_MOCK_ROOT/calls")" -eq 2
(cd "$repo" && bash eng/bootstrap-gates/optimistic-dispatch-recovery.sh "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt") >/dev/null
test "$(wc -l < "$RECOVERY_MOCK_ROOT/dispatches")" -eq 1
test "$(rg -F -c 'status=queued&per_page=1' "$RECOVERY_MOCK_ROOT/calls")" -eq 1
python3 - "$RECOVERY_MOCK_ROOT/pulls.json" <<'PY'
import json, sys
rows = [{"created_at": "2026-09-20T00:00:00Z", "head": {"sha": f"{i:040x}"}} for i in range(1, 102)]
with open(sys.argv[1], "w") as output:
    json.dump([rows], output)
PY
export RECOVERY_ALL_PASSED=1
rm "$RECOVERY_MOCK_ROOT/calls"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null 2>&1
test "$(rg -F -c 'actions/artifacts?name=coherent-aggregate-' "$RECOVERY_MOCK_ROOT/calls")" -eq 100
test ! -s "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt"
test "$(jq -r '.candidate' "$RUNNER_TEMP/optimistic-validation/recovery-cursor.json")" = "$(printf '%040x' 100)"
python3 - "$RUNNER_TEMP/optimistic-validation/recovery-cursor.json" "$RECOVERY_MOCK_ROOT/cursor.zip" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[2], "w") as archive:
    archive.write(sys.argv[1], "recovery-cursor.json")
PY
export RECOVERY_CURSOR_ZIP="$RECOVERY_MOCK_ROOT/cursor.zip"
export RECOVERY_UNPASSED="$(printf '%040x' 101)"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null 2>&1
test "$(cat "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt")" = "$RECOVERY_UNPASSED"
export RECOVERY_CURSOR_FORGED=1
rm "$RECOVERY_MOCK_ROOT/calls"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null 2>&1
test ! -s "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt"
if rg -Fq 'actions/artifacts/999/zip' "$RECOVERY_MOCK_ROOT/calls"; then
  echo 'untrusted cursor artifact was downloaded' >&2
  exit 1
fi
unset RECOVERY_CURSOR_FORGED
printf 'invalid archive\n' > "$RECOVERY_MOCK_ROOT/invalid-cursor.zip"
export RECOVERY_CURSOR_ZIP="$RECOVERY_MOCK_ROOT/invalid-cursor.zip"
(cd "$repo" && bash eng/bootstrap-gates/optimistic-recovery.sh) >/dev/null 2>&1
test ! -s "$RUNNER_TEMP/optimistic-validation/pending-candidates.txt"

printf 'OPTIMISTIC_FANOUT_FIXTURES_OK complete=1 delegated-aggregate=1 missing=1 foreign=1 failed=1 stale-envelope=1 stale-base=1 census-empty=1 census-failed=1 census-stale=1 census-missing=1 bounded-old-plan-fallback=25 unavailable-discovery-current=1 recovery-full-skip=1 recovery-exact-name=1 recovery-bounded-dispatch=1 recovery-resume-after-100=1 recovery-forged-cursor=1 recovery-malformed-cursor=1\n'
