#!/usr/bin/env bash
set -euo pipefail
candidate="${FSGG_CANDIDATE_SHA:?candidate required}"
base="${FSGG_BASE_SHA:?base required}"
tree="$(git rev-parse "$candidate^{tree}" | tr -d '\n' | sha256sum | cut -d' ' -f1)"
source="$(git archive --format=tar "$candidate" | sha256sum | cut -d' ' -f1)"
digest_file() { sha256sum "$1" | cut -d' ' -f1; }
output_root="${RUNNER_TEMP:-/tmp}/optimistic-validation"
mkdir -p "$output_root"
dotnet fsi eng/optimistic-validation.fsx -- prepare --candidate "$candidate" --base "$base" \
  --tree "$tree" --source "$source" \
  --behavioral "$(digest_file src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs)" \
  --contract "$(digest_file src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fsi)" \
  --toolchain "$(digest_file global.json)" --bounds "$(digest_file eng/optimistic-qualification-plan.json)" \
  --corpus "$(git ls-files 'src/FS.GG.Coordination.Protocol/**' | sort | sha256sum | cut -d' ' -f1)" \
  --harness "$(digest_file tests/FS.GG.Coordination.UnitTests/QualificationReuseSelectionTests.fs)" \
  --binding "$(digest_file src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj)" \
  --obligations unit,architecture,formal,security,package,recovery --partitions 6 --output-root "$output_root" > "$output_root/plan.identity"
