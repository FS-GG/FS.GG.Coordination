#!/usr/bin/env bash
set -euo pipefail
source eng/bootstrap-gates/runner-temp.sh
fsgg_resolve_runner_temp
decision="$RUNNER_TEMP/bootstrap-decision/decision.json"
route="$(jq -er '.decision' "$decision")"

case "$route" in
  execute) artifact_root="$RUNNER_TEMP/bootstrap-artifacts" ;;
  reuse) artifact_root="$RUNNER_TEMP/prior-bootstrap-artifacts" ;;
  refuse)
    dotnet fsi eng/bootstrap-ci.fsx -- collect --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$RUNNER_TEMP/bootstrap-artifacts" --output "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
    exit 1
    ;;
  *) echo "unsupported qualification route: $route" >&2; exit 1 ;;
esac

mkdir -p "$artifact_root/evidence-manifest"
cp eng/bootstrap-qualification-plan.json "$artifact_root/evidence-manifest/plan.json"
dotnet fsi eng/bootstrap-ci.fsx -- collect --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$artifact_root" --output "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
dotnet fsi eng/bootstrap-ci.fsx -- evidence --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$artifact_root" --file "$RUNNER_TEMP/bootstrap-evidence.json" --decision "$decision"
