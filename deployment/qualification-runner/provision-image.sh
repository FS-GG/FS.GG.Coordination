#!/usr/bin/env bash
# Source recipe only. Execute ONLY in the separately admitted disposable VM image build.
set -euo pipefail
if [[ $# != 4 || "$1" != --apply ]]; then
  echo 'HELD: require --apply TARGET.json ACQUIRED-LOCK.json INPUT-DIRECTORY and separate Root image-build admission' >&2
  exit 2
fi
runner_target=$2
runner_lock=$3
runner_inputs=$4
runner_source=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
[[ $(id -u) == 0 && $(uname -m) == x86_64 ]] || exit 2
# Root's independently verified provider selection must already have installed this root-only guard.
# A JSON record alone is not provider authority; the operator owns that external attestation.
[[ -f /run/fsgg-image-build-selection.json && ! -L /run/fsgg-image-build-selection.json ]]
[[ $(stat -c '%u:%a' /run/fsgg-image-build-selection.json) == 0:600 ]]
runner_uuid=$(cat /sys/class/dmi/id/product_uuid)
python3 -I -B "$runner_source/eng/dedicated_qualification_runner.py" verify-image-target \
  --target "$runner_target" --approval /run/fsgg-image-build-selection.json --system-uuid "$runner_uuid"
python3 -I -B "$runner_source/eng/dedicated_qualification_runner.py" validate-inputs \
  --target "$runner_target" --lock "$runner_lock" --inputs "$runner_inputs"
. /etc/os-release
[[ $ID == ubuntu && $VERSION_ID == 26.10 ]]
# Offline complete signed transaction. No moving repository, remote resolver or signature bypass.
[[ $runner_inputs == /mnt/fsgg-qualification-inputs ]]
runner_apt=(-o Dir::Etc::sourcelist="$runner_source/deployment/qualification-runner/runner.sources.list" -o Dir::Etc::sourceparts=-)
apt-get "${runner_apt[@]}" update
apt-get "${runner_apt[@]}" --assume-yes --no-install-recommends install \
  build-essential binutils libc6-dev pkg-config git curl ca-certificates python3 sudo \
  uidmap libcap2-bin util-linux e2fsprogs jq docker.io apparmor apparmor-utils
runner_inventory=$(mktemp)
trap 'rm -f -- "$runner_inventory"' EXIT
dpkg-query -W -f='${binary:Package}\t${Version}\n' | LC_ALL=C sort > "$runner_inventory"
cmp -- "$runner_inventory" "$runner_inputs/apt/package-inventory.expected"
# This is the ONLY policy write: selected disposable IMAGE build, never any Actions job.
[[ -e /proc/sys/kernel/apparmor_restrict_unprivileged_userns ]]
install -m 0644 "$runner_source/deployment/qualification-runner/90-fsgg-qualification.conf" /etc/sysctl.d/90-fsgg-qualification.conf
sysctl -p /etc/sysctl.d/90-fsgg-qualification.conf
[[ $(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns) == 0 ]]
# Qualified fixed tool archives were checked above; preserve the existing complete ordinary SDK input.
install -d -m 0755 /opt/fsgg/toolchains/dotnet-10.0.401 /opt/fsgg/toolchains/go-1.27.1 /opt/fsgg/actions-runner
[[ -z $(ls -A /opt/fsgg/toolchains/dotnet-10.0.401) && -z $(ls -A /opt/fsgg/toolchains/go-1.27.1) && -z $(ls -A /opt/fsgg/actions-runner) ]]
tar -xzf "$runner_inputs/sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz" --no-same-owner -C /opt/fsgg/toolchains/dotnet-10.0.401
tar -xzf "$runner_inputs/go/go1.27.1.linux-amd64.tar.gz" --no-same-owner --strip-components=1 -C /opt/fsgg/toolchains/go-1.27.1
runner_version=$(python3 -I -B -c 'import json,sys;print(json.load(open(sys.argv[1]))["runner"]["version"])' "$runner_lock")
tar -xzf "$runner_inputs/runner/actions-runner-linux-x64-$runner_version.tar.gz" --no-same-owner -C /opt/fsgg/actions-runner
getent group runner >/dev/null || groupadd --gid 1001 runner
getent passwd runner >/dev/null || useradd --uid 1001 --gid runner --create-home --shell /bin/bash runner
[[ $(id -u runner) == 1001 && $(id -g runner) == 1001 && $(getent passwd runner | cut -d: -f6) == /home/runner ]]
chown -R runner:runner /opt/fsgg/actions-runner
visudo -cf "$runner_source/deployment/qualification-runner/runner.sudoers"
install -m 0440 "$runner_source/deployment/qualification-runner/runner.sudoers" /etc/sudoers.d/fsgg-qualification-runner
install -d -m 0755 /etc/fsgg-qualification-runner
install -m 0644 "$runner_lock" /etc/fsgg-qualification-runner/acquired-input-lock.json
# Image publication/native gates and provider instance bindings are separate operator effects.
# Do NOT execute dotnet/go/catalog/browser/registration or weaken NoNewPrivileges here.
echo 'Image recipe applied; NOT qualified/published/registered. Retain new image/package/native ELF manifests and perform separate exact gates.'
