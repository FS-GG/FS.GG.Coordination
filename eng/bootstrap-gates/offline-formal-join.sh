#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_TRUSTED_ROOT:?protected base checkout required}"
: "${FSGG_CANDIDATE_ROOT:?exact candidate checkout required}"
: "${FSGG_CANDIDATE_SHA:?exact candidate required}"
: "${FSGG_BASE_SHA:?exact protected base required}"
: "${FSGG_OFFLINE_EVIDENCE:?signed envelope required}"
: "${FSGG_OFFLINE_PUBLIC_KEY:?protected public key required}"
: "${FSGG_CANDIDATE_OBLIGATION:?same-run obligation required}"
: "${FSGG_PARTITION_PLAN:?same-run plan required}"
: "${FSGG_OFFLINE_FRAGMENT_ROOT:?fragment output required}"

trusted_root="$(realpath "$FSGG_TRUSTED_ROOT")"
policy="$trusted_root/eng/offline-formal-pilot.json"
[[ "$(git -C "$trusted_root" rev-parse HEAD)" == "$FSGG_BASE_SHA" ]] || {
  echo "OFFLINE_FORMAL_JOIN_REFUSED reason=protected-base-head" >&2; exit 1;
}
for relative in eng/offline-formal-pilot.json eng/bootstrap-gates/offline-formal-join.sh; do
  git -C "$trusted_root" ls-files --error-unmatch -- "$relative" >/dev/null 2>&1 &&
    git -C "$trusted_root" diff --quiet "$FSGG_BASE_SHA" -- "$relative" &&
    test -f "$trusted_root/$relative" && ! test -L "$trusted_root/$relative" || {
      echo "OFFLINE_FORMAL_JOIN_REFUSED reason=protected-file-drift" >&2; exit 1;
    }
done
[[ "$(jq -er '.mode' "$policy")" == active ]] || {
  echo "OFFLINE_FORMAL_JOIN_REFUSED reason=shadow-policy" >&2; exit 1;
}
shard="$(jq -er '.pilotShard' "$policy")"
[[ "$shard" == authority-reconciliation ]] || {
  echo "OFFLINE_FORMAL_JOIN_REFUSED reason=unexpected-shard" >&2; exit 1;
}
jq -e --arg head "$FSGG_CANDIDATE_SHA" --arg base "$FSGG_BASE_SHA" '
  .candidate == $head and .baseRevision == $base and
  (.obligationSha256 | type == "string")
' "$FSGG_CANDIDATE_OBLIGATION" >/dev/null
jq -e --slurpfile obligation "$FSGG_CANDIDATE_OBLIGATION" '
  .schema == "fsgg.coordination.coherent-partition-plan/1" and
  .candidateObligationSha256 == $obligation[0].obligationSha256 and
  any(.partitions[]; .index == 1 and .obligations == ["formal"])
' "$FSGG_PARTITION_PLAN" >/dev/null

fragment="$(realpath -m "$FSGG_OFFLINE_FRAGMENT_ROOT")"
[[ ! -e "$fragment" && ! -L "$fragment" ]] || {
  echo "OFFLINE_FORMAL_JOIN_REFUSED reason=fragment-exists" >&2; exit 1;
}
mkdir "$fragment"
export FSGG_OFFLINE_RECEIPT_OUTPUT="$fragment/receipt.json"
bash "$trusted_root/eng/bootstrap-gates/offline-formal-verify.sh"
jq -e --slurpfile obligation "$FSGG_CANDIDATE_OBLIGATION" '
  .sourceSha256 == $obligation[0].sourceSha256 and
  .contractSha256 == $obligation[0].compiledContractSha256
' "$fragment/receipt.json" >/dev/null
install -m 0644 "$FSGG_CANDIDATE_OBLIGATION" "$fragment/candidate-obligation.json"
install -m 0644 "$FSGG_PARTITION_PLAN" "$fragment/partition-plan.json"
printf 'OFFLINE_FORMAL_JOIN_OK shard=%s head=%s\n' "$shard" "$FSGG_CANDIDATE_SHA"
