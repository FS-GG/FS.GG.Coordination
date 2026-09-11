#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_QUINT_TOOLCHAIN_OUTPUT:?FSGG_QUINT_TOOLCHAIN_OUTPUT is required}"
mkdir -p "$(dirname "$FSGG_QUINT_TOOLCHAIN_OUTPUT")"
dotnet restore src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj --locked-mode
FSGG_QUINT_PREPARE_ONLY=1 bash eng/qualify-canonical-quint.sh
test -s "$FSGG_QUINT_TOOLCHAIN_OUTPUT"
