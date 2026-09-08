#!/usr/bin/env bash
set -euo pipefail
root="${1:?partition receipt root required}"
plan_root="${2:?candidate plan root required}"
dotnet fsi eng/optimistic-validation.fsx -- aggregate \
  --obligation "$plan_root/candidate-obligation.json" --plan "$plan_root/partition-plan.json" --receipts "$root" \
  --output "$plan_root/coherent-aggregate-receipt.json"
