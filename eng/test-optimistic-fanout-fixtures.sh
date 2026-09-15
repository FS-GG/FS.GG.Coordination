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
printf '{"schema":"fsgg.coordination.coherent-partition-plan/1","candidateObligationSha256":"%s","partitions":[{"index":1,"obligations":["formal"]}]}\n' "$obligation" > "$plan_root/partition-plan.json"

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
printf '{"schema":"fsgg.coordination.canonical-quint-performance/1","outcome":"passed","shardCount":16,"epochBudgetMs":105000}\n' > "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$performance/performance.json"

run_validate() { (cd "$repo" && bash eng/bootstrap-gates/optimistic-aggregate-formal.sh validate-only) >/dev/null 2>&1; }
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

(cd "$repo" && bash eng/bootstrap-gates/optimistic-aggregate-formal.sh formal-only) >/dev/null
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
printf 'OPTIMISTIC_FANOUT_FIXTURES_OK complete=1 delegated-aggregate=1 missing=1 foreign=1 failed=1 stale-envelope=1 stale-base=1 census-empty=1 census-failed=1 census-stale=1 census-missing=1\n'
