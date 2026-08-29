#!/usr/bin/env bash
set -euo pipefail
dotnet restore src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj --locked-mode
if [[ -r /proc/sys/kernel/apparmor_restrict_unprivileged_userns ]]; then
  sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0
fi
/usr/bin/unshare --user --map-root-user --net -- /usr/bin/true
bash eng/qualify-canonical-quint.sh
