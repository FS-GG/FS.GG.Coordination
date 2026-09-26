#!/usr/bin/env bash
set -euo pipefail
root="${1:?partition receipt root required}"
plan_root="${2:?candidate plan root required}"
case "${FSGG_CI_PROFILE:-}" in
  scoped)
    python3 eng/optimistic-profile.py verify-scoped \
      "$RUNNER_TEMP/optimistic-selection/selection.json" \
      "$plan_root/candidate-obligation.json" \
      "$RUNNER_TEMP/optimistic-selection/profile.json"
    dotnet fsi eng/optimistic-validation.fsx -- aggregate-scoped \
      --obligation "$plan_root/candidate-obligation.json" --plan "$plan_root/partition-plan.json" \
      --receipts "$root" --profile "$RUNNER_TEMP/optimistic-selection/profile.json" \
      --output "$plan_root/scoped-aggregate-receipt.json"
    exit 0
    ;;
  full) ;;
  *) echo "unknown optimistic CI profile" >&2; exit 1 ;;
esac
dotnet fsi eng/optimistic-validation.fsx -- aggregate \
  --obligation "$plan_root/candidate-obligation.json" --plan "$plan_root/partition-plan.json" --receipts "$root" \
  --output "$plan_root/coherent-aggregate-receipt.json"
