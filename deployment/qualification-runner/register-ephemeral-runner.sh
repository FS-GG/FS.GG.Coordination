#!/usr/bin/env bash
# One fixed registration role. No PAT/private key is accepted, printed or persisted by this source.
set -euo pipefail
if [[ $# != 2 || "$1" != --register ]]; then
  echo 'HELD: require --register fsgg-qualification-NAME and separate Root target/image/group/registration admission' >&2
  exit 2
fi
runner_name=$2
[[ $runner_name =~ ^fsgg-qualification-[a-zA-Z0-9-]{1,64}$ && $(id -u) == 1001 ]]
[[ -f /etc/fsgg-qualification-runner/instance-binding.json && ! -L /etc/fsgg-qualification-runner/instance-binding.json ]]
[[ -f /etc/fsgg-qualification-runner/acquired-input-lock.json && ! -L /etc/fsgg-qualification-runner/acquired-input-lock.json ]]
[[ $(stat -c '%u:%a' /etc/fsgg-qualification-runner/acquired-input-lock.json) == 0:644 ]]
# This public nonsecret instance binding must be root-owned and separately accepted against provider evidence.
[[ $(stat -c '%u:%a' /etc/fsgg-qualification-runner/instance-binding.json) == 0:644 ]]
runner_source=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
python3 -I -B "$runner_source/eng/dedicated_qualification_runner.py" verify-registration \
  --target /etc/fsgg-qualification-runner/instance-binding.json \
  --lock /etc/fsgg-qualification-runner/acquired-input-lock.json --runner-name "$runner_name" \
  --system-uuid "$(cat /sys/class/dmi/id/product_uuid)"
: "${ACTIONS_RUNNER_INPUT_TOKEN:?Root must supply one short-lived registration token through the protected environment}"
# Supported actions/runner env input is masked and consumed by CommandSettings; never put token in argv.
cd /opt/fsgg/actions-runner
[[ ! -e .runner && ! -e .credentials && ! -e .credentials_rsaparams ]]
./config.sh --url https://github.com/FS-GG --runnergroup fsgg-native-qualification \
  --labels fsgg-native-linux-x64-v1 --name "$runner_name" --work _work \
  --ephemeral --disableupdate --unattended
unset ACTIONS_RUNNER_INPUT_TOKEN
# Root's existing finite provider/operation custodian owns this foreground process and VM teardown.
# Exactly one job; no --replace, re-registration, unattended restart or host reuse.
exec ./run.sh
