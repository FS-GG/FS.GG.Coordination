#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
cd "$repo"
candidate="$(printf 'a%.0s' {1..40})"
base="$(printf 'b%.0s' {1..40})"
digest="$(printf 'c%.0s' {1..64})"
plan="$scratch/plan"
receipts="$scratch/receipts"
mkdir -p "$plan" "$receipts"
dotnet fsi eng/optimistic-validation.fsx -- prepare \
  --candidate "$candidate" --base "$base" --tree "$digest" --source "$digest" \
  --behavioral "$digest" --contract "$digest" --toolchain "$digest" --bounds "$digest" \
  --corpus "$digest" --harness "$digest" --binding "$digest" \
  --qualification-plan "$digest" --obligations unit,architecture,formal,security,package,recovery \
  --partitions 6 --output-root "$plan" >/dev/null
obligation="$(jq -er '.obligationSha256' "$plan/candidate-obligation.json")"
jq -n --arg candidate "$candidate" --arg base "$base" --arg obligation "$obligation" --arg digest "$digest" \
  '{schema:"fsgg.coordination.optimistic-profile/1",profile:"scoped",candidate:$candidate,baseRevision:$base,
   candidateObligationSha256:$obligation,profileSha256:$digest,selectionSha256:$digest,
   fullDonorReceiptSha256:$digest}' > "$scratch/profile.json"
for index in 0 2 3 4 5; do
  mkdir "$receipts/$index"
  dotnet fsi eng/optimistic-validation.fsx -- run-partition \
    --obligation "$plan/candidate-obligation.json" --plan "$plan/partition-plan.json" \
    --partition "$index" --passed True --output "$receipts/$index/receipt.json" >/dev/null
done
scoped() {
  dotnet fsi eng/optimistic-validation.fsx -- aggregate-scoped \
    --obligation "$plan/candidate-obligation.json" --plan "$plan/partition-plan.json" \
    --receipts "$receipts" --profile "$scratch/profile.json" \
    --output "$scratch/scoped-aggregate-receipt.json" >/dev/null 2>&1
}
scoped
jq -e '.schema == "fsgg.coordination.scoped-aggregate-receipt/1" and .passed == true and (.partitionReceiptSha256|length) == 5' "$scratch/scoped-aggregate-receipt.json" >/dev/null
if dotnet fsi eng/optimistic-validation.fsx -- validate-prior \
    --obligation "$plan/candidate-obligation.json" --current-plan "$plan/partition-plan.json" \
    --prior-obligation "$plan/candidate-obligation.json" --prior-plan "$plan/partition-plan.json" \
    --aggregate-receipt "$scratch/scoped-aggregate-receipt.json" >/dev/null 2>&1; then
  echo 'scoped aggregate was accepted as a full donor' >&2; exit 1
fi
mv "$receipts/5/receipt.json" "$scratch/last-receipt.json"
if scoped; then echo 'missing scoped partition was accepted' >&2; exit 1; fi
mv "$scratch/last-receipt.json" "$receipts/5/receipt.json"
jq '.passed = false' "$receipts/5/receipt.json" > "$scratch/failed.json"
mv "$receipts/5/receipt.json" "$scratch/last-receipt.json"
cp "$scratch/failed.json" "$receipts/5/receipt.json"
if scoped; then echo 'failed or forged scoped partition was accepted' >&2; exit 1; fi
mv "$scratch/last-receipt.json" "$receipts/5/receipt.json"
jq '.candidate = "0000000000000000000000000000000000000000"' "$scratch/profile.json" > "$scratch/foreign.json"
mv "$scratch/profile.json" "$scratch/last-profile.json"
cp "$scratch/foreign.json" "$scratch/profile.json"
if scoped; then echo 'foreign scoped profile was accepted' >&2; exit 1; fi
printf 'OPTIMISTIC_SCOPED_AGGREGATE_OK complete=1 full-donor-rejection=1 missing=1 forged=1 foreign-profile=1\n'
