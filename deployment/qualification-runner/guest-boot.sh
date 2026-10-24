#!/usr/bin/env bash
# Per-boot qualified-image binding. Runs as root inside the disposable guest, from the baked
# layer, BEFORE the runner starts. It writes exactly one sealed instance-binding.json for this
# boot and reads the whole baked layer back. It does not register, start or contact anything.
set -euo pipefail
if [[ $# != 3 || "$1" != --apply ]]; then
  echo 'HELD: require --apply REQUEST.json fsgg-q-NAME; the request comes from the separately admitted launcher' >&2
  exit 2
fi
runner_request=$2
runner_name=$3
runner_layer=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
runner_tool=$runner_layer/eng/dedicated_qualification_runner.py
[[ $(id -u) == 0 && $(uname -m) == x86_64 ]] || exit 2
[[ $runner_name =~ ^fsgg-q-[A-Za-z0-9][A-Za-z0-9-]{0,63}$ ]] || exit 2
[[ -f $runner_request && ! -L $runner_request ]] || exit 2
runner_uuid=$(cat /sys/class/dmi/id/product_uuid)
# No policy is written here: the baked sysctl.d drop-in is applied by systemd-sysctl at boot and
# verify-guest only reads the live AppArmor userns value back.
python3 -I -B "$runner_tool" write-binding --request "$runner_request" \
  --lock /etc/fsgg-qualification-runner/acquired-input-lock.json --system-uuid "$runner_uuid"
python3 -I -B "$runner_tool" verify-guest --phase boot --runner-name "$runner_name" --system-uuid "$runner_uuid"
