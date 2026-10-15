#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
[[ -f FS.GG.Coordination.sln ]] || { printf '%s\n' 'compiler-and-tests: required subject FS.GG.Coordination.sln is unavailable' >&2; exit 2; }
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/provision-quint.sh"
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
  echo "COMPILER_AND_TESTS_REFUSED identity-bound qualification requires a clean committed candidate; run focused tests while editing, then commit before this full gate" >&2
  exit 3
fi
dotnet restore FS.GG.Coordination.sln --locked-mode
PYTHONDONTWRITEBYTECODE=1 python3 eng/tests/choreo-c2-tlc-retry/test_diagnostics.py DiagnosticControls.test_bootstrap_base_retains_command_cause_before_fixture_scratch_cleanup CommandInterfaceControls
dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror
host_tests_started_ns="$(date +%s%N)"
dotnet test tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~PortableHostAllocationTests --logger "trx;LogFileName=portable-host-allocation.trx" --results-directory "$RUNNER_TEMP/compiler-and-tests-portable-host-allocation"
python eng/validate-test-census.py "$RUNNER_TEMP/compiler-and-tests-portable-host-allocation/portable-host-allocation.trx" "portable-host-allocation" "$host_tests_started_ns"
host_executor_tests_started_ns="$(date +%s%N)"
dotnet test tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~PortableHostExecutorTests --logger "trx;LogFileName=portable-host-executor.trx" --results-directory "$RUNNER_TEMP/compiler-and-tests-portable-host-executor"
python eng/validate-test-census.py "$RUNNER_TEMP/compiler-and-tests-portable-host-executor/portable-host-executor.trx" "portable-host-executor" "$host_executor_tests_started_ns"
finite_custody_tests_started_ns="$(date +%s%N)"
dotnet test tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~FiniteCustodyWindowTests --logger "trx;LogFileName=finite-custody-inert.trx" --results-directory "$RUNNER_TEMP/compiler-and-tests-finite-custody-inert"
python eng/validate-test-census.py "$RUNNER_TEMP/compiler-and-tests-finite-custody-inert/finite-custody-inert.trx" "finite-custody-inert" "$finite_custody_tests_started_ns"
dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj --configuration Release --no-build --no-restore
process_started_ns="$(date +%s%N)"
dotnet test tests/FS.GG.Execution.Processes.Tests/FS.GG.Execution.Processes.Tests.fsproj --configuration Release --no-build --no-restore --logger "trx;LogFileName=process-sessions.trx" --results-directory "$RUNNER_TEMP/compiler-and-tests-process-sessions"
python eng/validate-test-census.py "$RUNNER_TEMP/compiler-and-tests-process-sessions/process-sessions.trx" "process-sessions" "$process_started_ns"
dotnet test tests/FS.GG.Coordination.Orchestration.Host.Tests/FS.GG.Coordination.Orchestration.Host.Tests.fsproj --configuration Release --no-build --no-restore
dotnet test tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj --configuration Release --no-build --no-restore --logger "trx;LogFileName=architecture-tests.trx" --results-directory artifacts/test-results/70-gs2-03-1-qualification-manifest
bash tests/telemetry-native-collector-installation/run-ci.sh
mkdir -p "$RUNNER_TEMP/compiler-and-tests"
cp artifacts/test-results/70-gs2-03-1-qualification-manifest/architecture-tests.trx "$RUNNER_TEMP/compiler-and-tests/architecture.trx"
