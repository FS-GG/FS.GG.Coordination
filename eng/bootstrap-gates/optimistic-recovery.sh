#!/usr/bin/env bash
set -euo pipefail
: "${GH_TOKEN:?GH_TOKEN is required}"
repo="${GITHUB_REPOSITORY:?repository required}"
git fetch origin main --force
git rev-list origin/main > "$RUNNER_TEMP/protected-main-ancestry.txt"
gh api --paginate "repos/$repo/pulls?state=open&per_page=100" --jq '.[].head.sha' > "$RUNNER_TEMP/pending-pr-candidates.txt"
gh api --paginate "repos/$repo/actions/runs?status=pending&per_page=100" --jq '.workflow_runs[].head_sha' > "$RUNNER_TEMP/pending-run-candidates.txt"
sort -u "$RUNNER_TEMP/pending-pr-candidates.txt" "$RUNNER_TEMP/pending-run-candidates.txt" > "$RUNNER_TEMP/pending-candidates.txt"
test -s "$RUNNER_TEMP/protected-main-ancestry.txt"
