#!/usr/bin/env bash
set -euo pipefail
candidate="${FSGG_CANDIDATE_SHA:?candidate required}"
base="${FSGG_BASE_SHA:?base required}"
tree="$(git archive --format=tar "$candidate" | sha256sum | cut -d' ' -f1)"
semantic_manifest="src/FS.GG.Coordination.Protocol/Generated/generated-structural-tests.json"
source="$(jq -er '.sourceSha256' "$semantic_manifest")"
behavioral="$(jq -er '.behavioralSha256' "$semantic_manifest")"
compiled_contract="$(jq -er '.contractSha256' "$semantic_manifest")"
digest_tracked_set() {
  git ls-files -- "$@" | LC_ALL=C sort | while IFS= read -r path; do
    printf '%s\0' "$path"
    git show "$candidate:$path"
  done | sha256sum | cut -d' ' -f1
}
output_root="${RUNNER_TEMP:-/tmp}/optimistic-validation"
mkdir -p "$output_root"
dotnet fsi eng/optimistic-validation.fsx -- prepare --candidate "$candidate" --base "$base" \
  --tree "$tree" --source "$source" \
  --behavioral "$behavioral" --contract "$compiled_contract" \
  --toolchain "$(digest_tracked_set global.json Directory.Packages.props 'src/**/packages.lock.json' 'tests/**/packages.lock.json')" \
  --bounds "$(digest_tracked_set eng/quint-qualification.json eng/quint-qualification-baseline.json eng/optimistic-qualification-plan.json)" \
  --corpus "$(digest_tracked_set 'src/FS.GG.Coordination.Protocol/Generated/**' eng/qualify-canonical-quint.sh eng/validate-canonical-quint-protocol.fsx eng/validate-quint-qualification.fsx)" \
  --harness "$(digest_tracked_set 'tests/FS.GG.Coordination.ArchitectureTests/*Quint*' tests/FS.GG.Coordination.UnitTests/QualificationReuseSelectionTests.fs eng/bootstrap-gates/canonical-quint.sh)" \
  --binding "$(digest_tracked_set src/FS.GG.Coordination.Protocol/Generated/Protocol.Generated.fs src/FS.GG.Coordination.Protocol/Generated/contract.json src/FS.GG.Coordination.Protocol/Protocol.bindings.json)" \
  --obligations unit,architecture,formal,security,package,recovery --partitions 6 --output-root "$output_root" > "$output_root/plan.identity"
install -m 0644 src/FS.GG.Coordination.Protocol/Protocol.bindings.json "$output_root/protocol.bindings.json"
