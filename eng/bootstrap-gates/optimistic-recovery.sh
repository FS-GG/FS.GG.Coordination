#!/usr/bin/env bash
set -euo pipefail
: "${GH_TOKEN:?GH_TOKEN is required}"
repo="${GITHUB_REPOSITORY:?repository required}"
git fetch origin main --force
output="$RUNNER_TEMP/optimistic-validation"
mkdir -p "$output"
git rev-list origin/main > "$output/protected-main-ancestry.txt"
gh api --paginate --slurp "repos/$repo/pulls?state=open&per_page=100" > "$output/open-pr-pages.json"
gh api --paginate --slurp "repos/$repo/actions/runs?status=pending&per_page=100" > "$output/pending-run-pages.json"
gh api --paginate --slurp "repos/$repo/actions/workflows/optimistic-parallel-validation.yml/runs?per_page=100" > "$output/coherent-run-pages.json"
gh api --paginate --slurp "repos/$repo/actions/artifacts?per_page=100" > "$output/artifact-pages.json"
jq -r '.[].[] | [.created_at,.head.sha] | @tsv' "$output/open-pr-pages.json" > "$output/candidate-events.tsv"
jq -r '.[].workflow_runs[] | [.created_at,.head_sha] | @tsv' "$output/pending-run-pages.json" >> "$output/candidate-events.tsv"
jq -r '.[].workflow_runs[] | select(.status == "queued" or .status == "in_progress") | .head_sha' "$output/coherent-run-pages.json" | sort -u > "$output/active-candidates.txt"
jq -r '.[].artifacts[] | select(.expired == false and (.name|startswith("coherent-aggregate-"))) | .name | sub("^coherent-aggregate-";"")' "$output/artifact-pages.json" | sort -u > "$output/passed-candidates.txt"
main="$(git rev-parse origin/main)"
sort -k1,1 -k2,2 "$output/candidate-events.tsv" | awk -F '\t' '!seen[$2]++ { print $2 }' | while IFS= read -r candidate; do
  [[ "$candidate" == "$main" ]] && continue
  grep -Fxq "$candidate" "$output/active-candidates.txt" && continue
  grep -Fxq "$candidate" "$output/passed-candidates.txt" && continue
  printf '%s\n' "$candidate"
done > "$output/pending-candidates.txt"
jq -Rn '[inputs | {candidate:.,coherentRunPending:true,state:"pending",reason:"nightly-native-census"}]' < "$output/pending-candidates.txt" > "$output/pending-candidates.json"
test -s "$output/protected-main-ancestry.txt"
