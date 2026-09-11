#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_QUINT_SHARD_ROOT:?FSGG_QUINT_SHARD_ROOT is required}"
: "${FSGG_QUINT_PERFORMANCE_RECEIPT:?FSGG_QUINT_PERFORMANCE_RECEIPT is required}"
receipt="$FSGG_QUINT_SHARD_ROOT/epoch.json"
test -f "$receipt"
jq -e '
  .schema == "fsgg.coordination.canonical-quint-formal-shard/1" and
  .id == "epoch" and .outcome == "passed" and
  (.elapsedMs | type == "number") and (.elapsedMs > 0)
' "$receipt" >/dev/null
elapsed="$(jq -r '.elapsedMs' "$receipt")"
epoch_budget="$(jq -r '.formalTests[] | select(.id == "epoch") | .budget.elapsedMs' eng/quint-qualification.json)"
test "$epoch_budget" -eq 75000
if (( elapsed > epoch_budget )); then
  printf 'CANONICAL_QUINT_PERFORMANCE_RED id=epoch elapsedMs=%s budgetMs=%s\n' "$elapsed" "$epoch_budget" >&2
  exit 1
fi
mkdir -p "$(dirname "$FSGG_QUINT_PERFORMANCE_RECEIPT")"
jq -cn --argjson count 16 --argjson epochBudgetMs "$epoch_budget" --argjson epochElapsedMs "$elapsed" \
  '{schema:"fsgg.coordination.canonical-quint-performance/1",outcome:"passed",shardCount:$count,epochBudgetMs:$epochBudgetMs,epochElapsedMs:$epochElapsedMs}' \
  > "$FSGG_QUINT_PERFORMANCE_RECEIPT"
