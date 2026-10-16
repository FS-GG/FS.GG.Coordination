#!/usr/bin/env bash
# Early feedback only: full final qualification and donor checks remain required.
set -euo pipefail

if [[ "${1:-}" != --bounded-source-preflight ]]; then
  : "${RUNNER_TEMP:?source-preflight requires RUNNER_TEMP}"
  evidence="$RUNNER_TEMP/source-preflight"
  mkdir "$evidence" # Fresh job-owned evidence; never accept a previous attempt.
  printf '%s\n' 'restore not-run' 'compile not-run' 'ci-reuse-contracts not-run' 'ci-bootstrap-contracts not-run' 'portable-host-allocation not-run' \
    'portable-host-executor not-run' 'finite-custody-inert not-run' \
    'callback-fixture not-run' 'finite-custody-transport not-run' > "$evidence/stages.txt"
  export FSGG_SOURCE_PREFLIGHT_EVIDENCE="$evidence"
  export FSGG_UTEL_CALLBACK_SOURCE="$RUNNER_TEMP/source-preflight-callback-source"
  # timeout owns the process group, including restore/build/test children. No retry.
  set +e
  timeout --signal=TERM --kill-after=10s 480s bash "$0" --bounded-source-preflight
  result=$?
  set -e
  if (( result != 0 )) && [[ ! -f "$evidence/first-cause.txt" ]]; then
    printf 'supervision exit=%s (124/137 indicate deadline/forced termination)\n' "$result" > "$evidence/first-cause.txt"
  fi
  printf 'supervision exit=%s\n' "$result" >> "$evidence/stages.txt"
  exit "$result"
fi

: "${FSGG_SOURCE_PREFLIGHT_EVIDENCE:?bounded invocation requires evidence}"
evidence="$FSGG_SOURCE_PREFLIGHT_EVIDENCE"
# The existing fixture has compiler-and-tests-relative evidence paths. Isolate them
# in this attempt, preserving its source-pin and exact-case validation unchanged.
export RUNNER_TEMP="$evidence/fixtures"
export FSGG_UTEL_CALLBACK_EVIDENCE="$RUNNER_TEMP/compiler-and-tests/callback-pair"
mkdir "$RUNNER_TEMP"
first=0
stage=preparation
record_failure() {
  local code="$1"
  if (( first == 0 )); then
    first="$code"
    printf '%s exit=%s\n' "$stage" "$code" > "$evidence/first-cause.txt"
  fi
  printf '%s failed exit=%s\n' "$stage" "$code" >> "$evidence/stages.txt"
}
finish() {
  local original="$?" retained=0
  trap - EXIT
  python3 eng/utel-callback-fixture.py retain 0 || retained=$?
  printf 'retention exit=%s\n' "$retained" >> "$evidence/stages.txt"
  # Retain reporting failure independently, without replacing an original cause.
  if (( original == 0 && retained != 0 )); then
    stage=retention
    record_failure "$retained"
    original="$retained"
  fi
  exit "$original"
}
trap finish EXIT
trap 'stage=deadline; record_failure 124; exit 124' TERM

# Collect independent preparation defects before any restore or fixture download.
for tool in git dotnet python3 date; do
  if ! command -v "$tool" >/dev/null; then
    printf 'source-preflight: missing tool %s\n' "$tool" >&2
    record_failure 2
  fi
done
for path in FS.GG.Coordination.sln global.json \
  tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj \
  tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj \
  tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj \
  tests/FS.GG.Coordination.UnitTests/QualificationReuseSelectionTests.fs \
  tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs \
  eng/tests/source-preflight/test_source_preflight.py \
  eng/validate-test-census.py eng/utel-callback-fixture.py eng/utel-callback-source.json \
  eng/utel-slot-callback-source.json eng/utel-closed-callback-source.json eng/utel-callback-cases.json; do
  if [[ ! -f "$path" ]]; then
    printf 'source-preflight: missing input %s\n' "$path" >&2
    record_failure 2
  fi
done
candidate="${FSGG_CANDIDATE_SHA:-}"
if [[ ! "$candidate" =~ ^[0-9a-f]{40}$ ]] || [[ "$(git rev-parse HEAD)" != "$candidate" ]]; then
  printf '%s\n' 'source-preflight: exact candidate HEAD required' >&2
  record_failure 3
fi
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  printf '%s\n' 'source-preflight: clean tracked candidate required' >&2
  record_failure 3
fi
printf 'candidate=%s\n' "$candidate" > "$evidence/candidate.txt"
(( first == 0 )) || exit "$first"

run_required() {
  stage="$1"
  shift
  local result=0
  "$@" || result=$?
  if (( result != 0 )); then
    record_failure "$result"
    return "$result"
  fi
  printf '%s passed\n' "$stage" >> "$evidence/stages.txt"
}
run_required restore dotnet restore FS.GG.Coordination.sln --locked-mode || exit "$first"
run_required compile dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror || exit "$first"

cohort() {
  local name="$1" filter="$2" directory="$RUNNER_TEMP/$1" started result=0
  local project="${3:-tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj}"
  stage="$name"
  mkdir "$directory"
  started="$(date +%s%N)"
  dotnet test "$project" \
    --configuration Release --no-build --no-restore --filter "$filter" \
    --logger "trx;LogFileName=$name.trx" --results-directory "$directory" || result=$?
  if (( result != 0 )); then
    record_failure "$result"
    return "$result"
  fi
  python3 eng/validate-test-census.py "$directory/$name.trx" "$name" "$started" || result=$?
  if (( result == 0 )) && [[ "$name" == finite-custody-transport ]]; then
    python3 eng/utel-callback-fixture.py census "$directory/$name.trx" || result=$?
  fi
  if (( result != 0 )); then
    record_failure "$result"
    return "$result"
  fi
  printf '%s passed\n' "$name" >> "$evidence/stages.txt"
}
# A failed invocation/census does not prove healthy test-host completion or
# cleanup. Block later launches rather than treating that outcome as independent.
block_remaining() {
  printf '%s\n' 'remaining-cohorts prerequisite-blocked failed-or-unknown-test-outcome' >> "$evidence/stages.txt"
  exit "$first"
}
# Only the changed CI contracts and their healthy witnesses are selected. The
# remaining class methods include FSI/shell/formal fixtures and stay in full CI.
cohort ci-reuse-contracts 'FullyQualifiedName~QualificationReuseSelectionTests&(FullyQualifiedName~coherent source prerequisite|FullyQualifiedName~workflow recovery|FullyQualifiedName~optimistic workflow projection|FullyQualifiedName~hosted partition scripts|FullyQualifiedName~source preflight inert)' \
  tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj || block_remaining
cohort ci-bootstrap-contracts 'FullyQualifiedName~BootstrapCiTests&(FullyQualifiedName~early source qualification|FullyQualifiedName~bootstrap workflow satisfies|FullyQualifiedName~workflow generator reproduces|FullyQualifiedName~bootstrap control surface)' \
  tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj || block_remaining
cohort portable-host-allocation 'FullyQualifiedName~PortableHostAllocationTests' || block_remaining
cohort portable-host-executor 'FullyQualifiedName~PortableHostExecutorTests' || block_remaining
cohort finite-custody-inert 'FullyQualifiedName~FiniteCustodyWindowTests' || block_remaining
if run_required callback-fixture python3 eng/utel-callback-fixture.py prepare; then
  cohort finite-custody-transport 'FullyQualifiedName~FiniteCustodyTransportTests' || block_remaining
else
  printf '%s\n' 'finite-custody-transport prerequisite-blocked callback-fixture' >> "$evidence/stages.txt"
fi
exit "$first"
