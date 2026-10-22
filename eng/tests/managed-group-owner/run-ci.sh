#!/usr/bin/env bash
# Managed-group owner controls (fsgg.managed-group-contract/1).
# Refusal controls and their mutants run everywhere. Containment controls and their
# mutants need a delegated cgroup v2 unit; on GitHub-hosted runners (or with
# FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT=1) that unit is required, elsewhere its
# absence is reported as not-run rather than as a pass.
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
base="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
scratch="$(mktemp -d "$base/managed-group-owner.XXXXXX")"
python="$(command -v python3)"
require=0
[[ "${GITHUB_ACTIONS:-}" == true || "${FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT:-}" == 1 ]] && require=1

TMPDIR="$scratch" PYTHONDONTWRITEBYTECODE=1 "$python" -I -B "$here/test_managed_group_owner.py" RefusalControls
"$python" -I -B "$here/mutation_proof.py" --scratch "$scratch/refusal" --mode refusal

status=0
bash "$here/delegated-unit.sh" "fsgg-mg-suite-$$" --owner-sets-no-new-privileges TMPDIR="$scratch" PYTHONDONTWRITEBYTECODE=1 \
  FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT=1 FSGG_MANAGED_GROUP_REQUIRE_BCL="$require" -- \
  "$python" -I -B "$here/test_managed_group_owner.py" || status=$?
if [[ "$status" == 4 && "$require" == 0 ]]; then
  echo "managed-group containment controls: not-run (no delegated unit on this host); refusal controls passed"
  exit 0
fi
[[ "$status" == 0 ]] || { echo "managed-group containment controls failed (status $status)" >&2; exit 1; }
"$python" -I -B "$here/owner_death_control.py" outside "$scratch/owner-death"
"$python" -I -B "$here/mutation_proof.py" --scratch "$scratch/all" --mode all
