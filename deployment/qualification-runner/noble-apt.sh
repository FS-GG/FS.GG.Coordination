# shellcheck shell=bash disable=SC2034
# Sourced by provision-image.sh and simulate-noble-bake.sh so the bake and its simulation can never diverge.
# Offline signed Ubuntu 24.04 (noble) transaction from the acquired input directory: only the archive's own
# signed InRelease/Packages indexes verified with the Ubuntu archive keyring, no moving repository, no resolver.
# Optional index targets (command-not-found, AppStream) are not part of the transaction and are disabled
# because the offline repository deliberately carries only Packages indexes and the needed .deb files.
# Every option except the sources path is in runner_apt_fixed. Its digest is recorded in the locked apt-transaction.json
# and checked by both the bake and the simulation, so neither can run with different apt behaviour.
#  - APT::Sandbox::User=root: the repository is a local, hash-checked, signature-verified file: tree and the bake runs as
#    root. With the default _apt sandbox the file method depends on the input tree being readable by _apt; a tree with
#    mode 0600/0700 files made apt probe fallback index names and fail with "File not found .../Packages" (first bake).
#  - Acquire::By-Hash=no: the snapshot's Release says Acquire-By-Hash, but the acquired tree has no by-hash directories.
#  - Acquire::CompressionTypes::Order::=xz: only Packages.xz is locked, so an xz failure must be a clear error naming
#    it, never a silent fallback to an uncompressed or gz index that is not in the population.
runner_apt_fixed=(-o Dir::Etc::sourceparts=- -o Acquire::Languages=none -o APT::Sandbox::User=root
  -o Acquire::By-Hash=no -o Acquire::CompressionTypes::Order::=xz
  -o Acquire::IndexTargets::deb::CNF::DefaultEnabled=false -o Acquire::IndexTargets::deb::DEP-11::DefaultEnabled=false)
runner_apt=(-o Dir::Etc::sourcelist="${runner_apt_sources:?}" "${runner_apt_fixed[@]}")
runner_apt_digest=$(printf '%s\n' "${runner_apt_fixed[@]}" | sha256sum | cut -d' ' -f1)
# Build tools, container engine, AppArmor, and the GitHub runner dependencies that replace a live installdependencies.sh.
runner_packages=(build-essential binutils libc6-dev pkg-config git curl ca-certificates python3 sudo
  uidmap libcap2-bin util-linux e2fsprogs jq docker.io apparmor apparmor-utils
  libicu74 libkrb5-3 zlib1g)
