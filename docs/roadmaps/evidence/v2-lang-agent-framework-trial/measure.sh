#!/usr/bin/env bash
set -euo pipefail

evidence_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$evidence_dir" rev-parse --show-toplevel)"
measurement_root="$(mktemp -d "${TMPDIR:-/tmp}/fsgg-agent-framework-measure.XXXXXX")"
trap 'rm -rf "$measurement_root"' EXIT

cp "$evidence_dir/Measurement.fs" "$measurement_root/Program.fs"

cat >"$measurement_root/Measure.fsproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$repo_root/src/FS.GG.Coordination.Orchestration.Execution/FS.GG.Coordination.Orchestration.Execution.fsproj" />
    <ProjectReference Include="$repo_root/src/FS.GG.Coordination.Orchestration.Execution.AgentFramework/FS.GG.Coordination.Orchestration.Execution.AgentFramework.fsproj" />
  </ItemGroup>
</Project>
EOF

dotnet build "$measurement_root/Measure.fsproj" --configuration Release --nologo --verbosity quiet >/dev/null

repetitions="${FSGG_MEASURE_REPETITIONS:-5}"
success_iterations="${FSGG_MEASURE_SUCCESS_ITERATIONS:-5000}"
recovery_iterations="${FSGG_MEASURE_RECOVERY_ITERATIONS:-2000}"
workflow_iterations="${FSGG_MEASURE_WORKFLOW_ITERATIONS:-500}"
source_head="$(git -C "$repo_root" rev-parse HEAD)"
source_tree="$(git -C "$repo_root" rev-parse 'HEAD^{tree}')"
sdk_version="$(dotnet --version)"
helper_sha256="$(sha256sum "$evidence_dir/Measurement.fs" | cut -d' ' -f1)"

printf '{"schema":"fsgg.agent-framework-measurement/1","sourceHead":"%s","sourceTree":"%s","helperSha256":"%s","sdkVersion":"%s","warmupOperations":25,"repetitions":%s,"successIterations":%s,"recoveryIterations":%s,"workflowIterations":%s}\n' \
  "$source_head" "$source_tree" "$helper_sha256" "$sdk_version" "$repetitions" "$success_iterations" "$recovery_iterations" "$workflow_iterations"

run_scenario() {
  local scenario="$1"
  local iterations="$2"
  local repetition

  for ((repetition = 1; repetition <= repetitions; repetition++)); do
    dotnet "$measurement_root/bin/Release/net10.0/Measure.dll" "$scenario" "$iterations"
  done
}

run_scenario direct "$success_iterations"
run_scenario adapter "$success_iterations"
run_scenario workflow "$workflow_iterations"
run_scenario direct-recovery "$recovery_iterations"
run_scenario adapter-recovery "$recovery_iterations"
