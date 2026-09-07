#!/usr/bin/env bash
set -euo pipefail

mode=${1:-run}
state_dir=${2:-}
script=$(cd "$(dirname "$0")" && pwd)/$(basename "$0")
repo='FS-GG/FS.GG.GitHub.Substrate.Sandbox'
base='gs2-07-6-pilot-base'
candidate='gs2-07-6-pilot-candidate'
advanced='gs2-07-6-pilot-base-advanced'
assets=$(cd "$(dirname "$0")" && pwd)
if [[ -z "$state_dir" ]]; then state_dir=$(mktemp -d); fi
mkdir -p "$state_dir"
scratch="$state_dir/scratch"
ruleset_id=''
pr_number=''
cleanup_armed=false
handoff_complete=false
visibility_changed=false
base_created=false
candidate_created=false
advanced_created=false
workflow_enabled=false
workflow_id=''
base_initial=''
base_advanced=''
candidate_sha=''
pr_node=''
pull_run=''
first_run=''
first_head=''
first_entry=''
second_entry=''
admitted_at=''
expires_at=''
fresh_admitted_at=''
fresh_expires_at=''
evaluated_at=''
pre_settings=''
pre_branches=''
pre_workflows=''
pre_secrets=''
pre_environments=''

digest_text() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }

save_state() {
  local tmp="$state_dir/state.env.tmp"
  umask 077
  {
    for name in scratch ruleset_id pr_number cleanup_armed visibility_changed base_created candidate_created advanced_created workflow_enabled workflow_id base_initial base_advanced candidate_sha pr_node pull_run first_run first_head first_entry second_entry admitted_at expires_at fresh_admitted_at fresh_expires_at evaluated_at pre_settings pre_branches pre_workflows pre_secrets pre_environments; do
      printf '%s=%q\n' "$name" "${!name}"
    done
  } > "$tmp"
  mv "$tmp" "$state_dir/state.env"
  printf '%s  state.env\n' "$(sha256sum "$state_dir/state.env" | cut -d' ' -f1)" > "$state_dir/state.env.sha256"
}

load_state() {
  [[ -f "$state_dir/state.env" && -f "$state_dir/state.env.sha256" ]]
  (cd "$state_dir" && sha256sum -c state.env.sha256 --status)
  # The sealed state is produced only by save_state in this private directory.
  # shellcheck disable=SC1090
  source "$state_dir/state.env"
}

init_journal() {
  jq -n '{schemaVersion:1,appliedEffects:[],retryEffects:[],compensations:[]}' > "$state_dir/journal.json"
}

record_effect() {
  local operation=$1 result=$2 tmp="$state_dir/journal.json.tmp"
  jq --arg operation "$operation" --arg result "$result" \
    '.appliedEffects += [{operationId:$operation,attempt:1,resultDigest:$result}] | .retryEffects += [{operationId:$operation,attempt:2,resultDigest:$result}]' \
    "$state_dir/journal.json" > "$tmp"
  mv "$tmp" "$state_dir/journal.json"
}

record_compensation() {
  local operation=$1 compensation=$2 result=$3 tmp="$state_dir/journal.json.tmp"
  if jq -e --arg compensation "$compensation" '.compensations[]? | select(.compensationId==$compensation)' "$state_dir/journal.json" >/dev/null; then return 0; fi
  jq --arg operation "$operation" --arg compensation "$compensation" --arg result "$result" \
    '.compensations += [{operationId:$operation,compensationId:$compensation,finalStateDigest:$result}]' \
    "$state_dir/journal.json" > "$tmp"
  mv "$tmp" "$state_dir/journal.json"
}

has_effect() { jq -e --arg operation "$1" '.appliedEffects[]? | select(.operationId==$operation)' "$state_dir/journal.json" >/dev/null; }

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
  if has_effect fresh-queue-admission; then
    [[ -z "$pr_number" ]] || api -X PATCH "repos/$repo/pulls/$pr_number" -f state=closed --silent || cleanup_failed=true
    queue_state=$(api "repos/$repo/pulls/$pr_number" --jq '.mergeable_state + ":" + .state' 2>/dev/null || printf 'absent')
    record_compensation fresh-queue-admission refuse-fresh-queue-entry "$(digest_text "$queue_state")" || cleanup_failed=true
  fi
  if has_effect grow-required-checks; then
    if [[ -n "$ruleset_id" ]] && api "repos/$repo/rulesets/$ruleset_id" --silent 2>/dev/null; then
      jq '.enforcement="disabled"' "$assets/sandbox-ruleset-initial.json" | api -X PUT "repos/$repo/rulesets/$ruleset_id" --input - --silent || cleanup_failed=true
      restored_ruleset=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
    else restored_ruleset=$(digest_text 'ruleset:absent'); fi
    record_compensation grow-required-checks disable-grown-ruleset "$restored_ruleset" || cleanup_failed=true
  fi
  if has_effect advance-base-ref; then
    if [[ $(ref_status "$base") == 200 ]]; then api -X PATCH "repos/$repo/git/refs/heads/$base" -f sha="$base_initial" -F force=true --silent || cleanup_failed=true; fi
    restored_base=$(api "repos/$repo/git/ref/heads/$base" --jq '.object.sha' 2>/dev/null || printf 'absent')
    record_compensation advance-base-ref restore-initial-base "$(digest_text "$restored_base")" || cleanup_failed=true
  fi
  queue_refs=$(api "repos/$repo/branches?per_page=100" --jq '.[].name | select(startswith("gh-readonly-queue/gs2-07-6-pilot-base/"))') || cleanup_failed=true
  while read -r queue_ref; do [[ -z "$queue_ref" ]] || delete_ref "$queue_ref" || cleanup_failed=true; done <<<"$queue_refs"
  if has_effect initial-queue-admission; then record_compensation initial-queue-admission refuse-expired-queue-entry "$(digest_text 'queue-entry:absent')" || cleanup_failed=true; fi
  if has_effect create-pilot-pr; then
    [[ -z "$pr_number" ]] || api -X PATCH "repos/$repo/pulls/$pr_number" -f state=closed --silent || cleanup_failed=true
    pr_state=$(api "repos/$repo/pulls/$pr_number" --jq '.state' 2>/dev/null || printf 'absent')
    record_compensation create-pilot-pr close-pilot-pr "$(digest_text "$pr_state")" || cleanup_failed=true
  fi
  if has_effect create-initial-ruleset; then
    [[ -z "$ruleset_id" ]] || api -X DELETE "repos/$repo/rulesets/$ruleset_id" --silent 2>/dev/null || true
    ruleset_state=$(api -i "repos/$repo/rulesets/$ruleset_id" 2>/dev/null | sed -n '1s/^[^ ]* \([0-9][0-9][0-9]\).*/\1/p')
    [[ "$ruleset_state" == 404 || -z "$ruleset_state" ]] || cleanup_failed=true
    record_compensation create-initial-ruleset delete-pilot-ruleset "$(digest_text 'ruleset:absent')" || cleanup_failed=true
  fi
  if has_effect make-sandbox-public; then rollback=$(private_readback) || cleanup_failed=true; record_compensation make-sandbox-public restore-private-visibility "$(digest_text 'visibility:private')" || cleanup_failed=true; else rollback='not-required'; fi
  if [[ "$workflow_enabled" == true ]]; then
    [[ -n "$workflow_id" ]] || workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
    if [[ -n "$workflow_id" ]]; then
      api -X PUT "repos/$repo/actions/workflows/$workflow_id/disable" --silent 2>/dev/null
      workflow_state=$(api "repos/$repo/actions/workflows/$workflow_id" --jq '.state')
      [[ "$workflow_state" == disabled_manually ]] || cleanup_failed=true
    fi
    record_compensation enable-pilot-workflow disable-pilot-workflow "$(digest_text "workflow:$workflow_id:disabled_manually")" || cleanup_failed=true
  fi
  if has_effect create-advanced-ref; then delete_ref "$advanced" || cleanup_failed=true; record_compensation create-advanced-ref delete-advanced-ref "$(digest_text 'advanced-ref:absent')" || cleanup_failed=true; fi
  if has_effect create-candidate-ref; then delete_ref "$candidate" || cleanup_failed=true; record_compensation create-candidate-ref delete-candidate-ref "$(digest_text 'candidate-ref:absent')" || cleanup_failed=true; fi
  if has_effect create-base-ref; then delete_ref "$base" || cleanup_failed=true; record_compensation create-base-ref delete-base-ref "$(digest_text 'base-ref:absent')" || cleanup_failed=true; fi
  final_settings=$(api "repos/$repo" | jq -cS '{id,full_name,visibility,private,default_branch,archived,disabled,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,delete_branch_on_merge}' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_branches=$(api "repos/$repo/branches?per_page=100" | jq -cS '[.[]|{name,sha:.commit.sha,protected}]' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_workflows=$(api "repos/$repo/actions/workflows" | jq -cS '[.workflows[]|select(.state=="active")|{id,name,path,state}]' | sha256sum | cut -d' ' -f1) || cleanup_failed=true
  final_secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count') || cleanup_failed=true
  final_environments=$(api "repos/$repo/environments" --jq '.total_count') || cleanup_failed=true
  [[ "$final_settings" == "$pre_settings" ]] || cleanup_failed=true
  [[ "$final_branches" == "$pre_branches" ]] || cleanup_failed=true
  [[ "$final_workflows" == "$pre_workflows" ]] || cleanup_failed=true
  [[ "$final_secrets" == "$pre_secrets" && "$final_environments" == "$pre_environments" ]] || cleanup_failed=true
  jq -n -cS --arg finalVisibility "$(api "repos/$repo" --jq '.visibility')" --arg finalSettingsDigest "$final_settings" \
    --arg finalBranchInventoryDigest "$final_branches" --arg finalActiveWorkflowInventoryDigest "$final_workflows" \
    --argjson repositorySecretCount "$final_secrets" --argjson environmentCount "$final_environments" \
    '{schemaVersion:1,finalVisibility:$finalVisibility,finalSettingsDigest:$finalSettingsDigest,finalBranchInventoryDigest:$finalBranchInventoryDigest,finalActiveWorkflowInventoryDigest:$finalActiveWorkflowInventoryDigest,repositorySecretCount:$repositorySecretCount,environmentCount:$environmentCount,temporaryBranchCount:0,queueRefCount:0,temporaryWorkflowFileCount:0}' \
    > "$state_dir/cleanup-readback.json"
  cp "$state_dir/journal.json" "$state_dir/final-journal.json"
  printf '%s  final-journal.json\n' "$(sha256sum "$state_dir/final-journal.json" | cut -d' ' -f1)" > "$state_dir/final-journal.json.sha256"
  printf '\nCLEANUP rollback=%s ruleset=%s pr=%s settings=%s branches=%s workflows=%s\n' "$rollback" "$ruleset_id" "$pr_number" "$final_settings" "$final_branches" "$final_workflows"
  [[ "$cleanup_failed" == false ]]
)

on_exit() {
  status=$?
  trap - EXIT
  if [[ "$handoff_complete" != true ]]; then cleanup || status=1; fi
  exit "$status"
}

prepare_phase() {
init_journal
mkdir -p "$scratch"
id=$(api "repos/$repo" --jq '.id')
visibility=$(api "repos/$repo" --jq '.visibility')
secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count')
environments=$(api "repos/$repo/environments" --jq '.total_count')
[[ "$id" == 1353050537 && "$visibility" == private && "$secrets" == 0 && "$environments" == 0 ]]
for ref in "$base" "$candidate" "$advanced"; do
  [[ $(ref_status "$ref") == 404 ]]
done
pre_settings=$(api "repos/$repo" | jq -cS '{id,full_name,visibility,private,default_branch,archived,disabled,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,delete_branch_on_merge}' | sha256sum | cut -d' ' -f1)
pre_branches=$(api "repos/$repo/branches?per_page=100" | jq -cS '[.[]|{name,sha:.commit.sha,protected}]' | sha256sum | cut -d' ' -f1)
pre_workflows=$(api "repos/$repo/actions/workflows" | jq -cS '[.workflows[]|select(.state=="active")|{id,name,path,state}]' | sha256sum | cut -d' ' -f1)
pre_secrets=$secrets
pre_environments=$environments
cleanup_armed=true
save_state

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
base_readback=$(api "repos/$repo/git/ref/heads/$base" --jq '.object.sha')
[[ "$base_readback" == "$base_initial" ]]
record_effect create-base-ref "$(digest_text "$base:$base_readback")"
save_state
git switch --quiet -c "$candidate"
cp "$assets/sandbox-candidate.txt" gs2-07-6-candidate.txt
git add gs2-07-6-candidate.txt
git commit --quiet -m 'test: add GS2-07.6 exact candidate'
git push --quiet origin "$candidate"
candidate_created=true
candidate_sha=$(git rev-parse HEAD)
candidate_readback=$(api "repos/$repo/git/ref/heads/$candidate" --jq '.object.sha')
[[ "$candidate_readback" == "$candidate_sha" ]]
record_effect create-candidate-ref "$(digest_text "$candidate:$candidate_readback")"
save_state
git switch --quiet "$base"
cp "$assets/sandbox-forward-base.txt" gs2-07-6-forward-base.txt
git add gs2-07-6-forward-base.txt
git commit --quiet -m 'test: prepare GS2-07.6 forward base'
git push --quiet origin "HEAD:$advanced"
advanced_created=true
base_advanced=$(git rev-parse HEAD)
advanced_readback=$(api "repos/$repo/git/ref/heads/$advanced" --jq '.object.sha')
[[ "$advanced_readback" == "$base_advanced" ]]
record_effect create-advanced-ref "$(digest_text "$advanced:$advanced_readback")"
save_state
printf 'BRANCHES base_initial=%s candidate=%s advanced=%s\n' "$base_initial" "$candidate_sha" "$base_advanced"

workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
[[ -z "$workflow_id" ]] || api -X PUT "repos/$repo/actions/workflows/$workflow_id/enable" --silent
[[ -z "$workflow_id" ]] || workflow_enabled=true
[[ -z "$workflow_id" ]] || record_effect enable-pilot-workflow "$(digest_text "$workflow_id:active")"
save_state

api -X PATCH "repos/$repo" -f visibility=public --silent
visibility_changed=true
for attempt in $(seq 1 20); do
  visibility=$(api "repos/$repo" --jq '.visibility')
  [[ "$visibility" == public ]] && break
  sleep 2
done
[[ "$visibility" == public ]]
record_effect make-sandbox-public "$(digest_text 'visibility:public')"
save_state
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
ruleset_initial_digest=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
record_effect create-initial-ruleset "$ruleset_initial_digest"
save_state
printf 'RULESET initial=%s checks=queue-pilot\n' "$ruleset_id"
pr_number=$(api -X POST "repos/$repo/pulls" -f title='GS2-07.6 queue pilot' -f head="$candidate" -f base="$base" -f body='Ephemeral bounded queue pilot for FS-GG/FS.GG.Coordination#320.' --jq '.number')
pr_node=$(api "repos/$repo/pulls/$pr_number" --jq '.node_id')
record_effect create-pilot-pr "$(digest_text "$pr_number:$pr_node")"
save_state
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
first_entry=$(gh api graphql -f query='mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{id}}}' -f id="$pr_node" --jq '.data.enqueuePullRequest.mergeQueueEntry.id')
admitted_at=$(date -u +%s)
expires_at=$((admitted_at + 120))
record_effect initial-queue-admission "$(digest_text "$first_entry")"
save_state

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
save_state
claim_snapshot=$(api "repos/FS-GG/FS.GG.Coordination/issues/comments/5575246586")
review_snapshot=$(api "repos/FS-GG/FS.GG.Coordination/issues/comments/5564723174")
observed_authority_at=$(date -u +%s)
[[ $(jq -r '.html_url' <<<"$claim_snapshot") == "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5575246586" ]]
[[ $(jq -r '.body | contains("worker=curlew-2b4b") and contains("fsgg:claim")' <<<"$claim_snapshot") == true ]]
[[ $(jq -r '.html_url' <<<"$review_snapshot") == "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5564723174" ]]
printf '%s' "$claim_snapshot" | jq -cS . > "$state_dir/authority-observed-claim.json"
printf '%s' "$review_snapshot" | jq -cS . > "$state_dir/authority-observed-review.json"
dependency_digest=$(jq -r '.digest' "$assets/../accepted/GS2-07.5.json")
jq -n -cS --argjson observedAt "$observed_authority_at" --argjson claimGeneration 5575246586 \
  --arg claimSource "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5575246586" \
  --arg reviewDigest 296e6c87a7cc064a6cea5016fcb9e0026ff79a5373eca81aa0d5dec9001db53e \
  --arg reviewSource "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5564723174" \
  --arg dependencyDigest "$dependency_digest" --arg settingsDigest "$pre_settings" \
  '{observedAtUnixSeconds:$observedAt,claimGeneration:$claimGeneration,claimSource:$claimSource,reviewDigest:$reviewDigest,reviewSource:$reviewSource,dependencyDigest:$dependencyDigest,dependencySource:"evidence/github-substrate-v2/accepted/GS2-07.5.json",settingsDigest:$settingsDigest,settingsSource:"evidence/github-substrate-v2/gs2-07-6/hosted-prestate.json",releaseObligationsMet:true}' \
  > "$state_dir/authority-observed.json"
observed_authority_digest=$(cat "$state_dir/authority-observed-claim.json" "$state_dir/authority-observed-review.json" "$state_dir/authority-observed.json" | sha256sum | cut -d' ' -f1)
cp "$state_dir/journal.json" "$state_dir/checkpoint-journal.json"
checkpoint_journal_digest=$(sha256sum "$state_dir/checkpoint-journal.json" | cut -d' ' -f1)
jq -n -c --argjson schemaVersion 1 --arg repository "$repo" --argjson repositoryId 1353050537 \
  --arg candidateSha "$candidate_sha" --arg mergeGroupHeadSha "$first_head" --arg baseRef "refs/heads/$base" \
  --arg baseSha "$base_initial" --argjson claimGeneration 5575246586 --argjson admittedAt "$admitted_at" \
  --argjson expiresAt "$expires_at" --argjson runId "$first_run" --arg journalDigest "$checkpoint_journal_digest" \
  --arg authorityDigest "$observed_authority_digest" \
  --argjson sealedAt "$(date -u +%s)" \
  '{schemaVersion:$schemaVersion,repository:$repository,repositoryId:$repositoryId,candidateSha:$candidateSha,mergeGroupHeadSha:$mergeGroupHeadSha,baseRef:$baseRef,baseSha:$baseSha,claimGeneration:$claimGeneration,requiredChecks:["queue-pilot"],admittedAtUnixSeconds:$admittedAt,expiresAtUnixSeconds:$expiresAt,sealedAtUnixSeconds:$sealedAt,runId:$runId,failedStepObserved:true,interrupted:true,journalDigest:$journalDigest,authorityObservationDigest:$authorityDigest}' \
  | jq -cS . > "$state_dir/durable-checkpoint.json"
checkpoint_digest=$(sha256sum "$state_dir/durable-checkpoint.json" | cut -d' ' -f1)
printf '%s  durable-checkpoint.json\n' "$checkpoint_digest" > "$state_dir/durable-checkpoint.json.sha256"
jq -n -cS --argjson preparePid "$$" --arg checkpointDigest "$checkpoint_digest" '{schemaVersion:1,preparePid:$preparePid,resumePid:null,checkpointDigest:$checkpointDigest,separateProcesses:false}' > "$state_dir/process-handoff.json"
printf 'CHECKPOINT_SEALED digest=%s admitted=%s expires=%s process=%s\n' "$checkpoint_digest" "$admitted_at" "$expires_at" "$$"
handoff_complete=true
return 75
}

resume_phase() {
load_state
cleanup_armed=true
trap on_exit EXIT
(cd "$state_dir" && sha256sum -c durable-checkpoint.json.sha256 --status)
checkpoint_journal_digest=$(jq -r '.journalDigest' "$state_dir/durable-checkpoint.json")
[[ $(sha256sum "$state_dir/checkpoint-journal.json" | cut -d' ' -f1) == "$checkpoint_journal_digest" ]]
[[ $(jq -r '.repositoryId' "$state_dir/durable-checkpoint.json") == 1353050537 ]]
[[ $(jq -r '.candidateSha' "$state_dir/durable-checkpoint.json") == "$candidate_sha" ]]
[[ $(jq -r '.baseSha' "$state_dir/durable-checkpoint.json") == "$base_initial" ]]
[[ $(api "repos/$repo" --jq '.id') == 1353050537 ]]
current_candidate=$(api "repos/$repo/git/ref/heads/$candidate" --jq '.object.sha')
current_base=$(api "repos/$repo/git/ref/heads/$base" --jq '.object.sha')
[[ "$candidate_sha" == "$current_candidate" && "$base_initial" == "$current_base" ]]
while [[ $(date -u +%s) -lt "$expires_at" ]]; do sleep 1; done
expired_at=$(date -u +%s)
[[ "$expired_at" -ge "$expires_at" ]]
jq -n -cS --argjson expiredAt "$expired_at" --argjson expiresAt "$expires_at" \
  '{schemaVersion:1,priorAdmissionExpiresAtUnixSeconds:$expiresAt,observedAtUnixSeconds:$expiredAt,decision:"refused-expired-admission",freshAdmissionRequired:true}' \
  > "$state_dir/expired-admission-refusal.json"
printf 'EXPIRED_ADMISSION_REFUSED expires=%s observed=%s process=%s\n' "$expires_at" "$expired_at" "$$"
claim_snapshot=$(api "repos/FS-GG/FS.GG.Coordination/issues/comments/5575246586")
review_snapshot=$(api "repos/FS-GG/FS.GG.Coordination/issues/comments/5564723174")
current_authority_at=$(date -u +%s)
[[ $(jq -r '.body | contains("worker=curlew-2b4b") and contains("fsgg:claim")' <<<"$claim_snapshot") == true ]]
printf '%s' "$claim_snapshot" | jq -cS . > "$state_dir/authority-current-claim.json"
printf '%s' "$review_snapshot" | jq -cS . > "$state_dir/authority-current-review.json"
jq -n -cS --argjson observedAt "$current_authority_at" --argjson claimGeneration 5575246586 \
  --arg claimSource "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5575246586" \
  --arg reviewDigest 296e6c87a7cc064a6cea5016fcb9e0026ff79a5373eca81aa0d5dec9001db53e \
  --arg reviewSource "https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5564723174" \
  --arg dependencyDigest "$(jq -r '.digest' "$assets/../accepted/GS2-07.5.json")" --arg settingsDigest "$pre_settings" \
  '{observedAtUnixSeconds:$observedAt,claimGeneration:$claimGeneration,claimSource:$claimSource,reviewDigest:$reviewDigest,reviewSource:$reviewSource,dependencyDigest:$dependencyDigest,dependencySource:"evidence/github-substrate-v2/accepted/GS2-07.5.json",settingsDigest:$settingsDigest,settingsSource:"evidence/github-substrate-v2/gs2-07-6/hosted-prestate.json",releaseObligationsMet:true}' \
  > "$state_dir/authority-current.json"
current_authority_digest=$(sha256sum "$state_dir"/authority-current-*.json | sha256sum | cut -d' ' -f1)
[[ -n "$current_authority_digest" ]]
jq --argjson resumePid "$$" '.resumePid=$resumePid | .separateProcesses=(.preparePid != $resumePid)' "$state_dir/process-handoff.json" > "$state_dir/process-handoff.json.tmp"
mv "$state_dir/process-handoff.json.tmp" "$state_dir/process-handoff.json"
[[ $(jq -r '.separateProcesses' "$state_dir/process-handoff.json") == true ]]

jq '.enforcement="disabled"' "$assets/sandbox-ruleset-initial.json" | api -X PUT "repos/$repo/rulesets/$ruleset_id" --input - --silent
api -X PATCH "repos/$repo/git/refs/heads/$base" -f sha="$base_advanced" -F force=true --silent
base_retry=$(api "repos/$repo/git/ref/heads/$base" --jq '.object.sha')
[[ "$base_retry" == "$base_advanced" ]]
record_effect advance-base-ref "$(digest_text "$base:$base_retry")"
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input "$assets/sandbox-ruleset-grown.json" --silent
retry_digest_one=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input "$assets/sandbox-ruleset-grown.json" --silent
retry_digest_two=$(api "repos/$repo/rulesets/$ruleset_id" | jq -cS '{id,name,target,enforcement,conditions,rules}' | sha256sum | cut -d' ' -f1)
ruleset_count=$(api "repos/$repo/rulesets" --jq '[.[]|select(.name=="gs2-07-6-queue-pilot")]|length')
[[ "$retry_digest_one" == "$retry_digest_two" && "$ruleset_count" == 1 ]]
record_effect grow-required-checks "$retry_digest_two"
printf 'FORWARD_BASE prior=%s current=%s checks=queue-growth,queue-pilot\n' "$base_initial" "$base_advanced"
printf 'DETERMINISTIC_RETRY digest=%s duplicate_rulesets=0\n' "$retry_digest_two"
current_candidate=$(api "repos/$repo/git/ref/heads/$candidate" --jq '.object.sha')
[[ "$candidate_sha" == "$current_candidate" ]]
second_entry=$(gh api graphql -f query='mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{id}}}' -f id="$pr_node" --jq '.data.enqueuePullRequest.mergeQueueEntry.id')
fresh_admitted_at=$(date -u +%s)
fresh_expires_at=$((fresh_admitted_at + 120))
record_effect fresh-queue-admission "$(digest_text "$second_entry")"
save_state

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
evaluated_at=$(date -u +%s)
[[ "$evaluated_at" -ge "$fresh_admitted_at" && "$evaluated_at" -lt "$fresh_expires_at" ]]
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
cp "$proof" "$state_dir/hosted-proof.json"
printf '%s' "$run" | jq -cS . > "$state_dir/recovery-run.json"
printf '%s' "$jobs" | jq -cS . > "$state_dir/recovery-jobs.json"
printf '%s' "$artifact" | jq -cS . > "$state_dir/recovery-artifact.json"
sha256sum "$state_dir/durable-checkpoint.json" "$state_dir/checkpoint-journal.json" "$state_dir/expired-admission-refusal.json" \
  "$state_dir/authority-observed-claim.json" "$state_dir/authority-observed-review.json" \
  "$state_dir/authority-current-claim.json" "$state_dir/authority-current-review.json" \
  "$state_dir/recovery-run.json" "$state_dir/recovery-jobs.json" "$state_dir/recovery-artifact.json" "$state_dir/hosted-proof.json" \
  > "$state_dir/retained-inputs.sha256"
printf 'HOSTED_ARTIFACT id=%s digest=%s\n' "$artifact_id" "$artifact_digest"
printf 'PILOT_OK candidate=%s first_run=%s second_run=%s\n' "$candidate_sha" "$first_run" "$second_run"

printf 'PILOT_COMPLETE cleanup-pending=true\n'
}

case "$mode" in
  run)
    set +e
    "$script" prepare "$state_dir"
    prepare_status=$?
    set -e
    [[ "$prepare_status" == 75 ]] || exit "$prepare_status"
    "$script" resume "$state_dir"
    ;;
  prepare)
    [[ ! -e "$state_dir/state.env" ]]
    trap on_exit EXIT
    prepare_phase
    ;;
  resume)
    resume_phase
    ;;
  cleanup)
    load_state
    cleanup_armed=true
    cleanup
    ;;
  *)
    printf 'usage: %s [run|prepare|resume|cleanup] [state-directory]\n' "$0" >&2
    exit 64
    ;;
esac
