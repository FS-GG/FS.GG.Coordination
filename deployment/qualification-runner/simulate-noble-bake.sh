#!/usr/bin/env bash
# Offline simulation of the APT part of provision-image.sh inside a disposable ROOTLESS container whose root
# is an extraction of the pinned Ubuntu 24.04 cloud image. It uses the SAME noble-apt.sh and runner.sources.list
# as the bake (checked against the digest locked in apt-transaction.json), with the network disabled by the caller,
# and writes the resulting package inventory. Mount the input tree with the most hostile modes a builder may produce
# (a copy with `chmod -R go-rwx`) to prove the result does not depend on who can read it.
# It is not the image build: no VM, disk image, kernel, sysctl or toolchain is touched.
#   podman run --rm --network=none --rootfs ROOTFS \
#     -v REPO/deployment/qualification-runner:/layer:ro -v APT:/mnt/fsgg-qualification-inputs/apt:ro -v OUT:/out \
#     bash /layer/simulate-noble-bake.sh
set -euo pipefail
[[ $# == 0 && -d /layer && -d /mnt/fsgg-qualification-inputs/apt && -d /out && -f /run/.containerenv && $(id -u) == 0 ]] \
  || { echo 'HELD: run only as documented in a disposable podman container' >&2; exit 2; }
# shellcheck source=/dev/null
. /etc/os-release
[[ $ID == ubuntu && $VERSION_ID == 24.04 ]]
runner_apt_sources=/layer/runner.sources.list
# shellcheck source=noble-apt.sh
. /layer/noble-apt.sh
# No option is added or removed here: the apt behaviour is the bake's, proven by the locked digest.
[[ $(python3 -I -B -c 'import json,sys;print(json.load(open(sys.argv[1]))["aptOptionsSha256"])' /mnt/fsgg-qualification-inputs/apt/apt-transaction.json) == "$runner_apt_digest" ]]
apt-get "${runner_apt[@]}" update
apt-get "${runner_apt[@]}" --assume-yes --no-install-recommends install "${runner_packages[@]}"
dpkg-query -W -f='${binary:Package}\t${Version}\n' | LC_ALL=C sort > /out/package-inventory.expected
apt-get check
