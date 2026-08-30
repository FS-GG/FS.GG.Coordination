#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
decision="$RUNNER_TEMP/bootstrap-decision/decision.json"; route="$(jq -er '.decision' "$decision")"
subject="${FSGG_QUALIFICATION_SUBJECT_SHA:?FSGG_QUALIFICATION_SUBJECT_SHA is required}"
[[ "$subject" =~ ^[0-9a-f]{64}$ ]] || { echo "qualification subject must be lowercase SHA-256" >&2; exit 1; }

case "$route" in
  execute) artifact_root="$RUNNER_TEMP/bootstrap-artifacts" ;;
  reuse) artifact_root="$RUNNER_TEMP/prior-bootstrap-artifacts" ;;
  refuse)
    dotnet fsi eng/bootstrap-ci.fsx -- collect --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$RUNNER_TEMP/bootstrap-artifacts" --output "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
    exit 1
    ;;
  *) echo "unsupported qualification route: $route" >&2; exit 1 ;;
esac

if [[ "$route" == "reuse" ]]; then
  subject_manifest="$artifact_root/bootstrap-evidence-manifest-$subject"
  canonical_manifest="$artifact_root/bootstrap-evidence-manifest"
  [[ -d "$subject_manifest" && ! -e "$canonical_manifest" ]] || {
    echo "subject-indexed prior evidence is missing or ambiguous: $subject" >&2
    exit 1
  }
  mv "$subject_manifest" "$canonical_manifest"
fi

mkdir -p "$artifact_root/evidence-manifest"
cp eng/bootstrap-qualification-plan.json "$artifact_root/evidence-manifest/plan.json"
dotnet fsi eng/bootstrap-ci.fsx -- collect --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$artifact_root" --output "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
dotnet fsi eng/bootstrap-ci.fsx -- evidence --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$artifact_root" --file "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
