#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/provision-quint.sh"
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
  echo "COMPILER_AND_TESTS_REFUSED identity-bound qualification requires a clean committed candidate; run focused tests while editing, then commit before this full gate" >&2
  exit 3
fi
dotnet restore FS.GG.Coordination.sln --locked-mode
dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror
dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj --configuration Release --no-build --no-restore
dotnet test tests/FS.GG.Coordination.Orchestration.Host.Tests/FS.GG.Coordination.Orchestration.Host.Tests.fsproj --configuration Release --no-build --no-restore
dotnet test tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj --configuration Release --no-build --no-restore --logger "trx;LogFileName=architecture-tests.trx" --results-directory artifacts/test-results/70-gs2-03-1-qualification-manifest
bash tests/telemetry-runtime-receiver/run.sh
(
  readonly native_source_revision="a1310e14a60d1d025dd3fa9f404970890503d092"
  readonly native_source_sha256="8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba"
  readonly native_root="$RUNNER_TEMP/native-collector-v3"
  readonly native_module="$native_root/learn_01_native_source.py"
  readonly native_fixture="$native_root/fixture"
  test ! -e "$native_root"
  mkdir -m 0700 "$native_root"
  trap 'chmod -R u+w "$native_root" 2>/dev/null || true; rm -rf -- "$native_root"' EXIT
  curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 \
    --connect-timeout 10 --max-time 60 --retry 0 \
    "https://raw.githubusercontent.com/FS-GG/.github/$native_source_revision/tools/learn_01_native_source.py" \
    --output "$native_module"
  printf '%s  %s\n' "$native_source_sha256" "$native_module" | sha256sum --check --strict
  chmod 0400 "$native_module"
  bash tests/telemetry-native-collector-installation/run-v3.sh "$native_module" "$native_fixture"
  PYTHONDONTWRITEBYTECODE=1 python3 tests/telemetry-native-collector-installation/run.py
)
mkdir -p "$RUNNER_TEMP/compiler-and-tests"
cp artifacts/test-results/70-gs2-03-1-qualification-manifest/architecture-tests.trx "$RUNNER_TEMP/compiler-and-tests/architecture.trx"
