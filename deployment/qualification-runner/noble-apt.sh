# shellcheck shell=bash disable=SC2034
# Sourced by provision-image.sh and simulate-noble-bake.sh so the bake and its simulation can never diverge.
# Offline signed Ubuntu 24.04 (noble) transaction from the acquired input directory: only the archive's own
# signed InRelease/Packages indexes verified with the Ubuntu archive keyring, no moving repository, no resolver.
# Optional index targets (command-not-found, AppStream) are not part of the transaction and are disabled
# because the offline repository deliberately carries only Packages indexes and the needed .deb files.
runner_apt=(-o Dir::Etc::sourcelist="${runner_apt_sources:?}" -o Dir::Etc::sourceparts=- -o Acquire::Languages=none
  -o Acquire::IndexTargets::deb::CNF::DefaultEnabled=false -o Acquire::IndexTargets::deb::DEP-11::DefaultEnabled=false)
# Build tools, container engine, AppArmor, and the GitHub runner dependencies that replace a live installdependencies.sh.
runner_packages=(build-essential binutils libc6-dev pkg-config git curl ca-certificates python3 sudo
  uidmap libcap2-bin util-linux e2fsprogs jq docker.io apparmor apparmor-utils
  libicu74 libkrb5-3 zlib1g)
