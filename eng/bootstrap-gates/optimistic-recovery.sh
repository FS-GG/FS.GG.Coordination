#!/usr/bin/env bash
set -euo pipefail
: "${GH_TOKEN:?GH_TOKEN is required}"
repo="${GITHUB_REPOSITORY:?repository required}"
output="$RUNNER_TEMP/optimistic-validation"
mkdir -p "$output"
# The nightly recovery has no work while its two coherent candidate slots are
# occupied. Check that before enumerating PRs, runs or artifact history.
gh api "repos/$repo/actions/workflows/optimistic-parallel-validation.yml/runs?status=queued&per_page=2" > "$output/queued-runs.json"
gh api "repos/$repo/actions/workflows/optimistic-parallel-validation.yml/runs?status=in_progress&per_page=2" > "$output/running-runs.json"
queued="$(jq -er '.total_count' "$output/queued-runs.json")"
running="$(jq -er '.total_count' "$output/running-runs.json")"
if (( queued + running >= 2 )); then
  : > "$output/pending-candidates.txt"
  printf '[]\n' > "$output/pending-candidates.json"
  exit 0
fi
git fetch origin main --force
# Reuse the last scan position. The named artifact query remains small even
# when unrelated workflow artifacts have grown without bound.
gh api "repos/$repo/actions/workflows/optimistic-parallel-validation.yml/runs?event=schedule&per_page=100" > "$output/scheduled-runs.json"
jq -e '.workflow_runs | type == "array"' "$output/scheduled-runs.json" >/dev/null
gh api "repos/$repo/actions/artifacts?name=optimistic-recovery-cursor&per_page=100" > "$output/cursor-artifacts.json"
jq -e '.artifacts | type == "array"' "$output/cursor-artifacts.json" >/dev/null
cursor_key=""
while IFS=$'\t' read -r cursor_id cursor_head; do
  git merge-base --is-ancestor "$cursor_head" origin/main || continue
  gh api "repos/$repo/actions/artifacts/$cursor_id/zip" > "$output/prior-cursor.zip" || continue
  unzip -p "$output/prior-cursor.zip" recovery-cursor.json > "$output/prior-cursor.json" 2>/dev/null || continue
  cursor_created="$(jq -er '.createdAt | select(type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T"))' "$output/prior-cursor.json" 2>/dev/null)" || continue
  cursor_candidate="$(jq -er '.candidate | select(type == "string" and test("^[0-9a-f]{40}$"))' "$output/prior-cursor.json" 2>/dev/null)" || continue
  cursor_key="$cursor_created"$'\t'"$cursor_candidate"
  break
done < <(jq -r --slurpfile runs "$output/scheduled-runs.json" '[.artifacts[] | . as $artifact | select(.expired == false and .name == "optimistic-recovery-cursor") | select(any($runs[0].workflow_runs[]; .id == $artifact.workflow_run.id and .event == "schedule" and .status == "completed" and .head_branch == "main" and .head_sha == $artifact.workflow_run.head_sha))] | sort_by(.created_at,.id) | reverse | .[] | [.id,.workflow_run.head_sha] | @tsv' "$output/cursor-artifacts.json")
git rev-list origin/main > "$output/protected-main-ancestry.txt"
gh api --paginate --slurp "repos/$repo/pulls?state=open&per_page=100" > "$output/open-pr-pages.json"
gh api --paginate --slurp "repos/$repo/actions/runs?status=pending&per_page=100" > "$output/pending-run-pages.json"
jq -r '.[].[] | [.created_at,.head.sha] | @tsv' "$output/open-pr-pages.json" > "$output/candidate-events.tsv"
jq -r '.[].workflow_runs[] | [.created_at,.head_sha] | @tsv' "$output/pending-run-pages.json" >> "$output/candidate-events.tsv"
jq -r '.workflow_runs[].head_sha' "$output/queued-runs.json" "$output/running-runs.json" | sort -u > "$output/active-candidates.txt"
main="$(git rev-parse origin/main)"
sort -k1,1 -k2,2 "$output/candidate-events.tsv" | awk -F '\t' '!seen[$2]++' > "$output/ordered-candidates.tsv"
if [[ -n "$cursor_key" ]]; then
  LC_ALL=C awk -v cursor="$cursor_key" '$0 > cursor' "$output/ordered-candidates.tsv" > "$output/scan-candidates.tsv"
  LC_ALL=C awk -v cursor="$cursor_key" '$0 <= cursor' "$output/ordered-candidates.tsv" >> "$output/scan-candidates.tsv"
else
  cp "$output/ordered-candidates.tsv" "$output/scan-candidates.tsv"
fi
: > "$output/pending-candidates.txt"
lookups=0
last_created=""
last_candidate=""
while IFS=$'\t' read -r created candidate; do
  if [[ "$candidate" == "$main" ]] || grep -Fxq "$candidate" "$output/active-candidates.txt"; then
    last_created="$created"
    last_candidate="$candidate"
    continue
  fi
  if (( lookups >= 100 )); then
    echo "nightly recovery resumes after 100 exact-name artifact lookups" >&2
    break
  fi
  last_created="$created"
  last_candidate="$candidate"
  lookups=$((lookups + 1))
  artifact="$output/artifact-$candidate.json"
  gh api "repos/$repo/actions/artifacts?name=coherent-aggregate-$candidate&per_page=100" > "$artifact"
  jq -e '.artifacts | type == "array"' "$artifact" >/dev/null
  if jq -e --arg candidate "$candidate" 'any(.artifacts[]; .name == ("coherent-aggregate-" + $candidate) and .expired == false)' "$artifact" >/dev/null; then
    continue
  fi
  printf '%s\n' "$candidate" >> "$output/pending-candidates.txt"
  if [[ "$(wc -l < "$output/pending-candidates.txt")" -ge 2 ]]; then break; fi
done < "$output/scan-candidates.tsv"
if [[ -n "$last_created" ]]; then
  jq -n --arg createdAt "$last_created" --arg candidate "$last_candidate" '{createdAt:$createdAt,candidate:$candidate}' > "$output/recovery-cursor.json"
fi
jq -Rn '[inputs | {candidate:.,coherentRunPending:true,state:"pending",reason:"nightly-native-census"}]' < "$output/pending-candidates.txt" > "$output/pending-candidates.json"
test -s "$output/protected-main-ancestry.txt"
