#!/usr/bin/env bash
set -euo pipefail
partition="${FSGG_PARTITION:?partition required}"
receipt_root="${RUNNER_TEMP:-/tmp}/coherent-partition-$partition"
mkdir -p "$receipt_root"
finish() {
  code=$?
  if [[ $code -eq 0 ]]; then passed=True; else passed=False; fi
  dotnet fsi eng/optimistic-validation.fsx -- run-partition \
    --obligation "$RUNNER_TEMP/optimistic-validation/candidate-obligation.json" \
    --plan "$RUNNER_TEMP/optimistic-validation/partition-plan.json" \
    --partition "$partition" --passed "$passed" --output "$receipt_root/receipt.json"
  exit "$code"
}
trap finish EXIT
obligation=$(dotnet fsi eng/optimistic-validation.fsx -- partition-obligation \
  --obligation "$RUNNER_TEMP/optimistic-validation/candidate-obligation.json" \
  --plan "$RUNNER_TEMP/optimistic-validation/partition-plan.json" \
  --partition "$partition")
case "$obligation" in
  unit|architecture)
    results="$receipt_root/test-results"
    project="tests/FS.GG.Coordination.$(if [[ "$obligation" == unit ]]; then printf UnitTests; else printf ArchitectureTests; fi)/FS.GG.Coordination.$(if [[ "$obligation" == unit ]]; then printf UnitTests; else printf ArchitectureTests; fi).fsproj"
    dotnet restore "$project" --locked-mode
    dotnet test "$project" -c Release --no-restore --no-build --logger "trx;LogFileName=$obligation.trx" --results-directory "$results"
    python eng/validate-test-census.py "$results/$obligation.trx" "$obligation"
    ;;
  formal) bash eng/bootstrap-gates/canonical-quint.sh ;;
  security) bash eng/bootstrap-gates/dependency-and-security.sh ;;
  package) bash eng/bootstrap-gates/package-install-smoke.sh ;;
  recovery) bash eng/bootstrap-gates/bootstrap-recovery.sh ;;
  *) echo "unsupported obligation for partition $partition: $obligation" >&2; exit 1 ;;
esac
