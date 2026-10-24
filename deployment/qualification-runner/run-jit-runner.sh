#!/usr/bin/env bash
# Starts the baked GitHub runner once with a one-job JIT configuration. Runs as root inside the
# disposable guest after guest-boot.sh. The JIT configuration arrives only in the supported
# ACTIONS_RUNNER_INPUT_JITCONFIG environment input; it is never an argument, printed or stored.
# Exactly one job: no --replace, no re-registration, no restart, no host reuse.
set -euo pipefail
if [[ $# != 2 || "$1" != --run ]]; then
  echo 'HELD: require --run fsgg-q-NAME and a separate Root dispatch/registration admission' >&2
  exit 2
fi
runner_name=$2
runner_layer=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
[[ $(id -u) == 0 ]] || exit 2
[[ $runner_name =~ ^fsgg-q-[A-Za-z0-9][A-Za-z0-9-]{0,63}$ ]] || exit 2
: "${ACTIONS_RUNNER_INPUT_JITCONFIG:?the launcher must supply one JIT configuration through the protected environment}"
# Fresh readback immediately before launch: baked layer, fixed paths and UID 1001, sealed binding, unconfigured runner.
python3 -I -B "$runner_layer/eng/dedicated_qualification_runner.py" verify-guest --phase boot \
  --runner-name "$runner_name" --system-uuid "$(cat /sys/class/dmi/id/product_uuid)" >/dev/null
cd /opt/fsgg/actions-runner
exec runuser -u runner -- /opt/fsgg/actions-runner/run.sh
