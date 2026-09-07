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
cleanup_armed=false
visibility_changed=false
base_created=false
candidate_created=false
advanced_created=false
workflow_enabled=false

api() { gh api -H 'X-GitHub-Api-Version: 2022-11-28' "$@"; }

ref_status() {
  api -i "repos/$repo/git/ref/heads/$1" 2>/dev/null | sed -n '1s/^[^ ]* \([0-9][0-9][0-9]\).*/\1/p'
}

delete_ref() {
  local ref=$1 attempt status
  for attempt in $(seq 1 20); do
    api -X DELETE "repos/$repo/git/refs/heads/$ref" --silent 2>/dev/null && return 0
    status=$(ref_status "$ref")
    [[ "$status" == 404 ]] && return 0
    [[ "$status" == 200 ]] || return 1
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

cleanup() (
  set +e
  [[ "$cleanup_armed" == true ]] || return 0
  cleanup_failed=false
  [[ -z "$pr_number" ]] || api -X PATCH "repos/$repo/pulls/$pr_number" -f state=closed --silent || cleanup_failed=true
  [[ -z "$ruleset_id" ]] || api -X DELETE "repos/$repo/rulesets/$ruleset_id" --silent || cleanup_failed=true
  if [[ "$visibility_changed" == true ]]; then rollback=$(private_readback) || cleanup_failed=true; else rollback='not-required'; fi
  [[ "$candidate_created" == false ]] || delete_ref "$candidate" || cleanup_failed=true
  [[ "$advanced_created" == false ]] || delete_ref "$advanced" || cleanup_failed=true
  [[ "$base_created" == false ]] || delete_ref "$base" || cleanup_failed=true
  queue_refs=$(api "repos/$repo/branches?per_page=100" --jq '.[].name | select(startswith("gh-readonly-queue/gs2-07-6-pilot-base/"))') || cleanup_failed=true
  while read -r queue_ref; do [[ -z "$queue_ref" ]] || delete_ref "$queue_ref" || cleanup_failed=true; done <<<"$queue_refs"
  if [[ "$workflow_enabled" == true ]]; then
    workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
    if [[ -n "$workflow_id" ]]; then
      api -X PUT "repos/$repo/actions/workflows/$workflow_id/disable" --silent 2>/dev/null
      workflow_state=$(api "repos/$repo/actions/workflows/$workflow_id" --jq '.state')
      [[ "$workflow_state" == disabled_manually ]] || cleanup_failed=true
    fi
  fi
  final_settings=$(api "repos/$repo" | jq -cS '{id,full_name,visibility,private,default_branch,archived,disabled,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,delete_branch_on_merge}' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_branches=$(api "repos/$repo/branches?per_page=100" | jq -cS '[.[]|{name,sha:.commit.sha,protected}]' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_workflows=$(api "repos/$repo/actions/workflows" | jq -cS '[.workflows[]|select(.state=="active")|{id,name,path,state}]' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count') || cleanup_failed=true
  final_environments=$(api "repos/$repo/environments" --jq '.total_count') || cleanup_failed=true
  [[ "$final_settings" == 98af1adee4e835df7abfb775d17ba3b8af59568fa195cfe6b4c06f4366bc035e ]] || cleanup_failed=true
  [[ "$final_branches" == 3c3d74cff5ea950fe3527e2a74a1040ec81fd3e800e07749e3295a9fb4ffa281 ]] || cleanup_failed=true
  [[ "$final_workflows" == ac55f313d337ef9469ef992b74cc5fe00a6b3f7b3fe6a635bea33e0abdf2a508 ]] || cleanup_failed=true
  [[ "$final_secrets" == 0 && "$final_environments" == 0 ]] || cleanup_failed=true
  printf '\nCLEANUP rollback=%s ruleset=%s pr=%s settings=%s branches=%s workflows=%s\n' "$rollback" "$ruleset_id" "$pr_number" "$final_settings" "$final_branches" "$final_workflows"
  [[ "$cleanup_failed" == false ]]
)

on_exit() {
  status=$?
  trap - EXIT
  cleanup || status=1
  exit "$status"
}
trap on_exit EXIT

id=$(api "repos/$repo" --jq '.id')
visibility=$(api "repos/$repo" --jq '.visibility')
secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count')
environments=$(api "repos/$repo/environments" --jq '.total_count')
[[ "$id" == 1353050537 && "$visibility" == private && "$secrets" == 0 && "$environments" == 0 ]]
for ref in "$base" "$candidate" "$advanced"; do
  [[ $(ref_status "$ref") == 404 ]]
done
cleanup_armed=true

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
base_created=true
base_initial=$(git rev-parse HEAD)
git switch --quiet -c "$candidate"
cp "$assets/sandbox-candidate.txt" gs2-07-6-candidate.txt
git add gs2-07-6-candidate.txt
git commit --quiet -m 'test: add GS2-07.6 exact candidate'
git push --quiet origin "$candidate"
candidate_created=true
candidate_sha=$(git rev-parse HEAD)
git switch --quiet "$base"
cp "$assets/sandbox-forward-base.txt" gs2-07-6-forward-base.txt
git add gs2-07-6-forward-base.txt
git commit --quiet -m 'test: prepare GS2-07.6 forward base'
git push --quiet origin "HEAD:$advanced"
advanced_created=true
base_advanced=$(git rev-parse HEAD)
printf 'BRANCHES base_initial=%s candidate=%s advanced=%s\n' "$base_initial" "$candidate_sha" "$base_advanced"

workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
[[ -z "$workflow_id" ]] || api -X PUT "repos/$repo/actions/workflows/$workflow_id/enable" --silent
[[ -z "$workflow_id" ]] || workflow_enabled=true

api -X PATCH "repos/$repo" -f visibility=public --silent
visibility_changed=true
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
admitted_at=$(date -u +%s)
expires_at=$((admitted_at + 30))

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
first_head=$(api "repos/$repo/actions/runs/$first_run" --jq '.head_sha')
jq -n -c --argjson schemaVersion 1 --arg repository "$repo" --argjson repositoryId 1353050537 \
  --arg candidateSha "$candidate_sha" --arg mergeGroupHeadSha "$first_head" --arg baseRef "refs/heads/$base" \
  --arg baseSha "$base_initial" --argjson claimGeneration 5565937139 --argjson admittedAt "$admitted_at" \
  --argjson expiresAt "$expires_at" --argjson runId "$first_run" \
  '{schemaVersion:$schemaVersion,repository:$repository,repositoryId:$repositoryId,candidateSha:$candidateSha,mergeGroupHeadSha:$mergeGroupHeadSha,baseRef:$baseRef,baseSha:$baseSha,claimGeneration:$claimGeneration,requiredChecks:["queue-pilot"],admittedAtUnixSeconds:$admittedAt,expiresAtUnixSeconds:$expiresAt,runId:$runId,failedStepObserved:true,interrupted:true}' > "$scratch/durable-checkpoint.json"
checkpoint_digest=$(sha256sum "$scratch/durable-checkpoint.json" | cut -d' ' -f1)
resume_digest=$(sha256sum "$scratch/durable-checkpoint.json" | cut -d' ' -f1)
[[ "$checkpoint_digest" == "$resume_digest" ]]
while [[ $(date -u +%s) -lt "$expires_at" ]]; do sleep 1; done
expired_at=$(date -u +%s)
[[ "$expired_at" -ge "$expires_at" ]]
printf 'CHECKPOINT digest=%s admitted=%s expires=%s expired_observed=%s\n' "$checkpoint_digest" "$admitted_at" "$expires_at" "$expired_at"

jq '.enforcement="disabled"' "$assets/sandbox-ruleset-initial.json" | api -X PUT "repos/$repo/rulesets/$ruleset_id" --input - --silent
api -X PATCH "repos/$repo/git/refs/heads/$base" -f sha="$base_advanced" -F force=true --silent
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input "$assets/sandbox-ruleset-grown.json" --silent
retry_digest_one=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input "$assets/sandbox-ruleset-grown.json" --silent
retry_digest_two=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
ruleset_count=$(api "repos/$repo/rulesets" --jq '[.[]|select(.name=="gs2-07-6-queue-pilot")]|length')
[[ "$retry_digest_one" == "$retry_digest_two" && "$ruleset_count" == 1 ]]
printf 'FORWARD_BASE prior=%s current=%s checks=queue-growth,queue-pilot\n' "$base_initial" "$base_advanced"
printf 'DETERMINISTIC_RETRY digest=%s duplicate_rulesets=0\n' "$retry_digest_two"
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
artifact=$(api "repos/$repo/actions/runs/$second_run/artifacts" --jq ".artifacts | map(select(.name == \"gs2-07-6-$second_run\" and (.expired | not))) | first")
artifact_id=$(jq -r '.id // empty' <<<"$artifact")
[[ "$artifact_id" =~ ^[0-9]+$ ]]
mkdir -p "$scratch/artifact"
gh run download "$second_run" -R "$repo" -n "gs2-07-6-$second_run" -D "$scratch/artifact"
proof="$scratch/artifact/hosted-proof.json"
[[ $(jq -r '.repositoryId' "$proof") == 1353050537 ]]
[[ $(jq -r '.runId' "$proof") == "$second_run" ]]
[[ $(jq -r '.mergeGroupHeadSha' "$proof") == "$(jq -r '.head_sha' <<<"$run")" ]]
[[ $(jq -r '.event' "$proof") == merge_group && $(jq -r '.requiredCheck' "$proof") == queue-growth ]]
artifact_digest=$(sha256sum "$proof" | cut -d' ' -f1)
printf 'HOSTED_ARTIFACT id=%s digest=%s\n' "$artifact_id" "$artifact_digest"
printf 'PILOT_OK candidate=%s first_run=%s second_run=%s\n' "$candidate_sha" "$first_run" "$second_run"

printf 'PILOT_COMPLETE cleanup-pending=true\n'
