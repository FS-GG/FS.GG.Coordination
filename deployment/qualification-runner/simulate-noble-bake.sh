#!/usr/bin/env bash
# Offline simulation of the APT part of provision-image.sh inside a disposable ROOTLESS container whose root
# is an extraction of the pinned Ubuntu 24.04 cloud image. It uses the SAME noble-apt.sh and runner.sources.list
# as the bake, with the network disabled by the caller, and writes the resulting package inventory.
# It is not the image build: no VM, disk image, kernel, sysctl or toolchain is touched.
#   podman run --rm --network=none --rootfs ROOTFS \
#     -v REPO/deployment/qualification-runner:/layer:ro -v APT:/mnt/fsgg-qualification-inputs/apt:ro -v OUT:/out \
#     bash /layer/simulate-noble-bake.sh
set -euo pipefail
[[ $# == 0 && -d /layer && -d /mnt/fsgg-qualification-inputs/apt && -d /out ]] || { echo 'HELD: run only as documented in a disposable container' >&2; exit 2; }
runner_apt_sources=/layer/runner.sources.list
# shellcheck source=noble-apt.sh
. /layer/noble-apt.sh
# Rootless containers cannot map the _apt user; this is the only difference from the bake.
runner_apt+=(-o APT::Sandbox::User=root)
apt-get "${runner_apt[@]}" update
apt-get "${runner_apt[@]}" --assume-yes --no-install-recommends install "${runner_packages[@]}"
dpkg-query -W -f='${binary:Package}\t${Version}\n' | LC_ALL=C sort > /out/package-inventory.expected
apt-get check
