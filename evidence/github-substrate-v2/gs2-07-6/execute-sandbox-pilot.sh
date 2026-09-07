#!/usr/bin/env bash
set -euo pipefail

repo='FS-GG/FS.GG.GitHub.Substrate.Sandbox'
base='gs2-07-6-pilot-base'
candidate='gs2-07-6-pilot-candidate'
advanced='gs2-07-6-pilot-base-advanced'
assets=$(cd "$(dirname "$0")" && pwd)
scratch=$(mktemp -d)
ruleset_id=''
pr_number=''

api() { gh api -H 'X-GitHub-Api-Version: 2022-11-28' "$@"; }

delete_ref() {
  local ref=$1 attempt
  for attempt in $(seq 1 20); do
    api -X DELETE "repos/$repo/git/refs/heads/$ref" --silent 2>/dev/null && return 0
    ! api "repos/$repo/git/ref/heads/$ref" --silent 2>/dev/null && return 0
    sleep 3
  done
  return 1
}

private_readback() {
  local attempt visibility
  for attempt in $(seq 1 20); do
    api -X PATCH "repos/$repo" -f visibility=private --silent 2>/dev/null || true
    visibility=$(api "repos/$repo" --jq '.visibility' 2>/dev/null || true)
    if [[ "$visibility" == private ]]; then printf 'private-attempt=%s' "$attempt"; return 0; fi
    sleep 3
  done
  return 1
}

cleanup() {
  set +e
  [[ -z "$ruleset_id" ]] || api -X DELETE "repos/$repo/rulesets/$ruleset_id" --silent
  [[ -z "$pr_number" ]] || api -X PATCH "repos/$repo/pulls/$pr_number" -f state=closed --silent
  rollback=$(private_readback)
  delete_ref "$candidate"
  delete_ref "$advanced"
  delete_ref "$base"
  while read -r queue_ref; do
    [[ -z "$queue_ref" ]] || delete_ref "$queue_ref"
  done < <(api "repos/$repo/branches?per_page=100" --jq '.[].name | select(startswith("gh-readonly-queue/gs2-07-6-pilot-base/"))')
  workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
  [[ -z "$workflow_id" ]] || api -X PUT "repos/$repo/actions/workflows/$workflow_id/disable" --silent
  printf '\nCLEANUP rollback=%s ruleset=%s pr=%s\n' "$rollback" "$ruleset_id" "$pr_number"
}
trap cleanup EXIT

id=$(api "repos/$repo" --jq '.id')
visibility=$(api "repos/$repo" --jq '.visibility')
secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count')
environments=$(api "repos/$repo/environments" --jq '.total_count')
[[ "$id" == 1353050537 && "$visibility" == private && "$secrets" == 0 && "$environments" == 0 ]]
for ref in "$base" "$candidate" "$advanced"; do
  ! api "repos/$repo/git/ref/heads/$ref" --silent 2>/dev/null
done

# Clone while the repository is still private; authenticated transport remains
# stable while provider visibility propagation is asynchronous.
gh repo clone "$repo" "$scratch/repo" -- --quiet

cd "$scratch/repo"
git config user.name 'FS.GG GS2-07.6 pilot'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git switch --quiet -c "$base" origin/main
mkdir -p .github/workflows
cp "$assets/sandbox-queue-workflow.yml" .github/workflows/gs2-07-6-queue-pilot.yml
git add .github/workflows/gs2-07-6-queue-pilot.yml
git commit --quiet -m 'test: install GS2-07.6 queue pilot workflow'
git push --quiet origin "$base"
base_initial=$(git rev-parse HEAD)
git switch --quiet -c "$candidate"
cp "$assets/sandbox-candidate.txt" gs2-07-6-candidate.txt
git add gs2-07-6-candidate.txt
git commit --quiet -m 'test: add GS2-07.6 exact candidate'
git push --quiet origin "$candidate"
candidate_sha=$(git rev-parse HEAD)
git switch --quiet "$base"
cp "$assets/sandbox-forward-base.txt" gs2-07-6-forward-base.txt
git add gs2-07-6-forward-base.txt
git commit --quiet -m 'test: prepare GS2-07.6 forward base'
git push --quiet origin "HEAD:$advanced"
base_advanced=$(git rev-parse HEAD)
printf 'BRANCHES base_initial=%s candidate=%s advanced=%s\n' "$base_initial" "$candidate_sha" "$base_advanced"

workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
[[ -z "$workflow_id" ]] || api -X PUT "repos/$repo/actions/workflows/$workflow_id/enable" --silent

api -X PATCH "repos/$repo" -f visibility=public --silent
for attempt in $(seq 1 20); do
  visibility=$(api "repos/$repo" --jq '.visibility')
  [[ "$visibility" == public ]] && break
  sleep 2
done
[[ "$visibility" == public ]]
printf 'PUBLIC id=%s secrets=%s environments=%s\n' "$id" "$secrets" "$environments"

provider_ready=false
for attempt in $(seq 1 30); do
  if api "repos/$repo/rulesets" --silent 2>/dev/null; then provider_ready=true; break; fi
  sleep 3
done
[[ "$provider_ready" == true ]]
printf 'PROVIDER_READY rulesets-attempt=%s\n' "$attempt"

ruleset_response=''
for attempt in $(seq 1 20); do
  ruleset_response=$(api -X POST "repos/$repo/rulesets" --input "$assets/sandbox-ruleset-initial.json" 2>/dev/null || true)
  ruleset_id=$(jq -r '.id // empty' <<<"$ruleset_response")
  [[ "$ruleset_id" =~ ^[0-9]+$ ]] && break
  sleep 3
done
[[ "$ruleset_id" =~ ^[0-9]+$ ]]
printf 'RULESET initial=%s checks=queue-pilot\n' "$ruleset_id"
pr_number=$(api -X POST "repos/$repo/pulls" -f title='GS2-07.6 queue pilot' -f head="$candidate" -f base="$base" -f body='Ephemeral bounded queue pilot for FS-GG/FS.GG.Coordination#320.' --jq '.number')
pr_node=$(api "repos/$repo/pulls/$pr_number" --jq '.node_id')
printf 'PR number=%s url=https://github.com/%s/pull/%s\n' "$pr_number" "$repo" "$pr_number"
pull_run=''
for attempt in $(seq 1 40); do
  pull_run=$(api "repos/$repo/actions/runs?event=pull_request&branch=$candidate&per_page=20" --jq ".workflow_runs | map(select(.head_sha == \"$candidate_sha\")) | first | .id // empty")
  [[ -n "$pull_run" ]] && break
  sleep 3
done
[[ -n "$pull_run" ]]
for attempt in $(seq 1 40); do
  pull_status=$(api "repos/$repo/actions/runs/$pull_run" --jq '.status')
  [[ "$pull_status" == completed ]] && break
  sleep 3
done
api "repos/$repo/actions/runs/$pull_run/jobs" --jq '[.jobs[]|{name,status,conclusion,head_sha,html_url}]' | sed 's/^/PULL_JOBS /'
gh api graphql -f query='mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{id}}}' -f id="$pr_node" --jq '.data.enqueuePullRequest.mergeQueueEntry.id'

first_run=''
first_queue_ref="gh-readonly-queue/$base/pr-$pr_number-$base_initial"
for attempt in $(seq 1 40); do
  first_run=$(api "repos/$repo/actions/runs?event=merge_group&per_page=30" --jq ".workflow_runs | map(select(.head_branch == \"$first_queue_ref\")) | first | .id // empty")
  [[ -n "$first_run" ]] && break
  sleep 3
done
[[ -n "$first_run" ]]
api "repos/$repo/actions/runs/$first_run" --jq '{id,head_sha,event,status,conclusion,html_url,created_at}' | sed 's/^/FIRST_RUN /'
first_jobs='[]'
for attempt in $(seq 1 30); do
  first_jobs=$(api "repos/$repo/actions/runs/$first_run/jobs" --jq '[.jobs[]|{name,status,conclusion,head_sha,html_url}]')
  [[ $(jq '[.[]|select(.name=="queue-growth" and .conclusion=="failure")]|length' <<<"$first_jobs") == 1 ]] && break
  sleep 3
done
printf 'FIRST_JOBS %s\n' "$first_jobs"
[[ $(jq '[.[]|select(.name=="queue-growth" and .conclusion=="failure")]|length' <<<"$first_jobs") == 1 ]]
api -X POST "repos/$repo/actions/runs/$first_run/cancel" --silent || true

jq '.enforcement="disabled"' "$assets/sandbox-ruleset-initial.json" | api -X PUT "repos/$repo/rulesets/$ruleset_id" --input - --silent
api -X PATCH "repos/$repo/git/refs/heads/$base" -f sha="$base_advanced" -F force=true --silent
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input "$assets/sandbox-ruleset-grown.json" --silent
printf 'FORWARD_BASE prior=%s current=%s checks=queue-growth,queue-pilot\n' "$base_initial" "$base_advanced"
current_candidate=$(api "repos/$repo/git/ref/heads/$candidate" --jq '.object.sha')
[[ "$candidate_sha" == "$current_candidate" ]]
gh api graphql -f query='mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{id}}}' -f id="$pr_node" --jq '.data.enqueuePullRequest.mergeQueueEntry.id' 2>/dev/null || true

second_run=''
second_queue_ref="gh-readonly-queue/$base/pr-$pr_number-$base_advanced"
for attempt in $(seq 1 50); do
  second_run=$(api "repos/$repo/actions/runs?event=merge_group&per_page=30" --jq ".workflow_runs | map(select(.head_branch == \"$second_queue_ref\")) | first | .id // empty")
  [[ -n "$second_run" ]] && break
  sleep 3
done
[[ -n "$second_run" ]]
for attempt in $(seq 1 60); do
  status=$(api "repos/$repo/actions/runs/$second_run" --jq '.status')
  [[ "$status" == completed ]] && break
  sleep 3
done
run=$(api "repos/$repo/actions/runs/$second_run" --jq '{id,head_sha,event,status,conclusion,html_url,created_at,updated_at}')
jobs=$(api "repos/$repo/actions/runs/$second_run/jobs" --jq '[.jobs[]|{name,status,conclusion,head_sha,html_url}]')
printf 'SECOND_RUN %s\n' "$run"
printf 'SECOND_JOBS %s\n' "$jobs"
[[ $(jq -r '.status' <<<"$run") == completed ]]
[[ $(jq -r '.conclusion' <<<"$run") == success ]]
[[ $(jq '[.[]|select(.name=="queue-growth" and .conclusion=="success")]|length' <<<"$jobs") == 1 ]]
printf 'PILOT_OK candidate=%s first_run=%s second_run=%s\n' "$candidate_sha" "$first_run" "$second_run"

cleanup
trap - EXIT
[[ $(api "repos/$repo" --jq '.visibility') == private ]]
printf 'ROLLBACK_OK visibility=private\n'
