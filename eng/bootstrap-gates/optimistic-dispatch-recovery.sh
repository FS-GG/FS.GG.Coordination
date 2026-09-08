#!/usr/bin/env bash
set -euo pipefail
pending="${1:?pending candidate census required}"
repo="${GITHUB_REPOSITORY:?repository required}"
base="$(git rev-parse origin/main)"
active="$(gh api --paginate --slurp "repos/$repo/actions/workflows/optimistic-parallel-validation.yml/runs?per_page=100" --jq '[.[].workflow_runs[] | select(.status == "queued" or .status == "in_progress") | .head_sha] | unique | length')"
available=$((2 - active))
if (( available <= 0 )); then exit 0; fi
count=0
while IFS= read -r candidate; do
  [[ "$candidate" =~ ^[0-9a-f]{40}$ ]] || continue
  git cat-file -e "$candidate^{commit}" 2>/dev/null || continue
  gh workflow run optimistic-parallel-validation.yml --repo "$repo" --ref main -f candidate_sha="$candidate" -f base_sha="$base"
  count=$((count + 1))
  (( count >= available )) && break
done < "$pending"
