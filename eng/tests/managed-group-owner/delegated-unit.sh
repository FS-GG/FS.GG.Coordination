#!/usr/bin/env bash
# Run one command as the current user inside a fresh transient systemd service whose
# cgroup is delegated to that user (the documented fsgg.managed-group-contract/1
# bootstrap). Usage: delegated-unit.sh UNIT-NAME [NAME=VALUE ...] -- COMMAND [ARG ...]
# Exit 4 means the host cannot supply a delegated unit (no systemd PID 1 or no sudo).
set -euo pipefail
unit="$1"; shift
[[ "$unit" =~ ^fsgg-mg-[a-z0-9-]{1,48}$ ]] || { echo "delegated-unit: invalid unit name" >&2; exit 2; }
setenv=()
while [[ $# -gt 0 && "$1" != -- ]]; do
  [[ "$1" =~ ^[A-Z_][A-Z0-9_]*= ]] || { echo "delegated-unit: invalid assignment" >&2; exit 2; }
  setenv+=("--setenv=$1"); shift
done
[[ "${1:-}" == -- && $# -ge 2 ]] || { echo "delegated-unit: missing command" >&2; exit 2; }
shift
[[ "$(cat /proc/1/comm 2>/dev/null)" == systemd ]] || { echo "delegated-unit: systemd is not PID 1" >&2; exit 4; }
sudo -n true 2>/dev/null || { echo "delegated-unit: non-interactive sudo unavailable" >&2; exit 4; }
exec sudo -n systemd-run --quiet --wait --pipe --collect --service-type=exec --unit="$unit" \
  -p Delegate=yes -p User="$(id -un)" -p Group="$(id -gn)" -p NoNewPrivileges=yes \
  -p KillMode=control-group -p TimeoutStopSec=5 -p TasksAccounting=yes -p MemoryAccounting=yes -p RuntimeMaxSec=600 \
  -p WorkingDirectory="$PWD" --setenv=PATH="$PATH" --setenv=HOME="$HOME" "${setenv[@]}" -- "$@"
