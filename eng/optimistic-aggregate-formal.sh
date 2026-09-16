#!/usr/bin/env bash
set -euo pipefail
mode="${1:-aggregate}"
: "${FSGG_FORMAL_FRAGMENT_ROOT:?formal fragment artifact root required}"
: "${FSGG_FORMAL_OUTPUT_ROOT:?formal aggregate output root required}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

plan_root="$RUNNER_TEMP/optimistic-validation"
candidate="$(jq -er '.candidate' "$plan_root/candidate-obligation.json")"
jq -e --slurpfile obligation "$plan_root/candidate-obligation.json" '
  .schema == "fsgg.coordination.coherent-partition-plan/1" and
  .candidateObligationSha256 == $obligation[0].obligationSha256 and
  any(.partitions[]; .index == 1 and .obligations == ["formal"])
' "$plan_root/partition-plan.json" >/dev/null
mapfile -t semantic < <(jq -er '.formalFanout.semanticShards[]' eng/optimistic-qualification-plan.json)
performance="$(jq -er '.formalFanout.performanceShard' eng/optimistic-qualification-plan.json)"
test "${#semantic[@]}" -eq 19

expected_dirs=()
for id in "${semantic[@]}" "$performance"; do expected_dirs+=("coherent-formal-fragment-$candidate-$id"); done
mapfile -t actual_dirs < <(find "$FSGG_FORMAL_FRAGMENT_ROOT" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | LC_ALL=C sort)
mapfile -t sorted_expected < <(printf '%s\n' "${expected_dirs[@]}" | LC_ALL=C sort)
if [[ "$(printf '%s\n' "${actual_dirs[@]}")" != "$(printf '%s\n' "${sorted_expected[@]}")" ]]; then
  printf 'formal fragment set is missing, duplicated, or foreign\n' >&2
  exit 1
fi

for id in "${semantic[@]}" "$performance"; do
  dir="$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$id"
  mapfile -t files < <(find "$dir" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)
  expected_files=(candidate-obligation.json partition-plan.json receipt.json)
  [[ "$id" == "$performance" ]] && expected_files+=(performance.json)
  mapfile -t expected_files < <(printf '%s\n' "${expected_files[@]}" | LC_ALL=C sort)
  [[ "$(printf '%s\n' "${files[@]}")" == "$(printf '%s\n' "${expected_files[@]}")" ]] || { echo "formal fragment $id has missing or foreign files" >&2; exit 1; }
  cmp -s "$dir/candidate-obligation.json" "$plan_root/candidate-obligation.json" || { echo "formal fragment $id has stale candidate envelope" >&2; exit 1; }
  cmp -s "$dir/partition-plan.json" "$plan_root/partition-plan.json" || { echo "formal fragment $id has stale plan envelope" >&2; exit 1; }
  if [[ "$id" == base ]]; then
    jq -e --slurpfile obligation "$plan_root/candidate-obligation.json" '
      .schema == "fsgg.coordination.canonical-quint-qualification/1" and .q1Outcome == "passed" and .q2Outcome == "passed" and
      .inputs.sourceSha256 == $obligation[0].sourceSha256 and
      .inputs.contractSha256 == $obligation[0].compiledContractSha256
    ' "$dir/receipt.json" >/dev/null
  else
    jq -e --arg id "$id" '.schema == "fsgg.coordination.canonical-quint-formal-shard/1" and .id == $id and .outcome == "passed"' "$dir/receipt.json" >/dev/null
  fi
  if [[ "$id" == "$performance" ]]; then
    jq -e '.schema == "fsgg.coordination.canonical-quint-performance/1" and .outcome == "passed" and .shardCount == 19 and .epochBudgetMs == 150000' "$dir/performance.json" >/dev/null
  fi
done

[[ "$mode" == validate-only ]] && exit 0
[[ "$mode" == aggregate || "$mode" == formal-only ]] || { echo "unsupported formal aggregate mode: $mode" >&2; exit 1; }

if [[ -e "$FSGG_FORMAL_OUTPUT_ROOT" ]]; then
  echo "formal aggregate output already exists: $FSGG_FORMAL_OUTPUT_ROOT" >&2
  exit 1
fi
mkdir -p "$FSGG_FORMAL_OUTPUT_ROOT/shards" "$FSGG_FORMAL_OUTPUT_ROOT/performance"
for id in "${semantic[@]}"; do
  install -m 0644 "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$id/receipt.json" "$FSGG_FORMAL_OUTPUT_ROOT/shards/$id.json"
done
install -m 0644 "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$performance/receipt.json" "$FSGG_FORMAL_OUTPUT_ROOT/shards/$performance.json"
install -m 0644 "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$performance/performance.json" "$FSGG_FORMAL_OUTPUT_ROOT/performance/performance.json"
install -m 0644 "$FSGG_FORMAL_FRAGMENT_ROOT/coherent-formal-fragment-$candidate-$performance/receipt.json" "$FSGG_FORMAL_OUTPUT_ROOT/performance/$performance.json"
export FSGG_QUINT_SHARD_ROOT="$FSGG_FORMAL_OUTPUT_ROOT/shards"
export FSGG_QUINT_PERFORMANCE_RECEIPT="$FSGG_FORMAL_OUTPUT_ROOT/performance/performance.json"
export FSGG_QUINT_RECEIPT="$FSGG_FORMAL_OUTPUT_ROOT/qualification.json"
export FSGG_QUINT_ACCOUNTING_RECEIPT="$FSGG_FORMAL_OUTPUT_ROOT/accounting.json"
bash eng/bootstrap-gates/canonical-quint-aggregate.sh
[[ "$mode" == formal-only ]] && exit 0
dotnet fsi eng/optimistic-validation.fsx -- run-partition \
  --obligation "$plan_root/candidate-obligation.json" --plan "$plan_root/partition-plan.json" \
  --partition 1 --passed True --output "$FSGG_FORMAL_OUTPUT_ROOT/receipt.json"
