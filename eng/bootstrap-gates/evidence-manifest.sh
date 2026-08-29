#!/usr/bin/env bash
set -euo pipefail
mkdir -p "$RUNNER_TEMP/bootstrap-artifacts/evidence-manifest"
cp eng/bootstrap-qualification-plan.json "$RUNNER_TEMP/bootstrap-artifacts/evidence-manifest/plan.json"
dotnet fsi eng/bootstrap-ci.fsx -- collect --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$RUNNER_TEMP/bootstrap-artifacts" --output "$RUNNER_TEMP/bootstrap-evidence.json"
dotnet fsi eng/bootstrap-ci.fsx -- evidence --root . --head "$FSGG_CANDIDATE_SHA" --artifacts "$RUNNER_TEMP/bootstrap-artifacts" --file "$RUNNER_TEMP/bootstrap-evidence.json"
