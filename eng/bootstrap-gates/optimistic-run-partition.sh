#!/usr/bin/env bash
set -euo pipefail
partition="${FSGG_PARTITION:?partition required}"
receipt_root="${RUNNER_TEMP:-/tmp}/coherent-partition-$partition"
mkdir -p "$receipt_root"
finish() {
  code=$?
  if [[ $code -eq 0 ]]; then passed=true; else passed=false; fi
  jq -n --argjson partition "$partition" --argjson passed "$passed" \
    '{schema:"fsgg.coordination.coherent-partition-receipt/1",partition:$partition,passed:$passed}' > "$receipt_root/receipt.json"
  exit "$code"
}
trap finish EXIT
case "$partition" in
  0) dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj -c Release --no-restore --filter FullyQualifiedName~QualificationReuseSelectionTests ;;
  1) dotnet test tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj -c Release --no-restore --filter FullyQualifiedName~BootstrapCiTests ;;
  2) bash eng/bootstrap-gates/canonical-quint.sh ;;
  3) bash eng/bootstrap-gates/dependency-and-security.sh ;;
  4) bash eng/bootstrap-gates/package-install-smoke.sh ;;
  5) bash eng/bootstrap-gates/bootstrap-recovery.sh ;;
  *) echo "unknown partition: $FSGG_PARTITION" >&2; exit 1 ;;
esac
