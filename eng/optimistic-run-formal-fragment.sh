#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_QUINT_SHARD:?formal shard required}"
: "${FSGG_QUINT_TOOLCHAIN_ARCHIVE:?toolchain archive required}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

plan_root="$RUNNER_TEMP/optimistic-validation"
jq -e --slurpfile obligation "$plan_root/candidate-obligation.json" '
  .schema == "fsgg.coordination.coherent-partition-plan/1" and
  .candidateObligationSha256 == $obligation[0].obligationSha256 and
  any(.partitions[]; .index == 1 and .obligations == ["formal"])
' "$plan_root/partition-plan.json" >/dev/null

fragment="$RUNNER_TEMP/coherent-formal-fragment-$FSGG_QUINT_SHARD"
if [[ -e "$fragment" ]]; then
  printf 'formal fragment scratch already exists: %s\n' "$fragment" >&2
  exit 1
fi
mkdir "$fragment"
install -m 0644 "$plan_root/candidate-obligation.json" "$fragment/candidate-obligation.json"
install -m 0644 "$plan_root/partition-plan.json" "$fragment/partition-plan.json"

shard_root="$fragment/shard"
export FSGG_QUINT_SHARD_ROOT="$shard_root"
bash eng/bootstrap-gates/canonical-quint-shard.sh
install -m 0644 "$shard_root/$FSGG_QUINT_SHARD.json" "$fragment/receipt.json"
if [[ "$FSGG_QUINT_SHARD" == epoch ]]; then
  export FSGG_QUINT_PERFORMANCE_RECEIPT="$fragment/performance.json"
  bash eng/bootstrap-gates/canonical-quint-performance.sh
fi
rm "$shard_root/$FSGG_QUINT_SHARD.json"
rmdir "$shard_root"
