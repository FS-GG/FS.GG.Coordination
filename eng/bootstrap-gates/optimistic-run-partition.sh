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
case "$partition" in
  0) dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj -c Release --no-restore --no-build ;;
  1) dotnet test tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj -c Release --no-restore --no-build ;;
  2) bash eng/bootstrap-gates/canonical-quint.sh ;;
  3) bash eng/bootstrap-gates/dependency-and-security.sh ;;
  4) bash eng/bootstrap-gates/package-install-smoke.sh ;;
  5) bash eng/bootstrap-gates/bootstrap-recovery.sh ;;
  *) echo "unknown partition: $FSGG_PARTITION" >&2; exit 1 ;;
esac
