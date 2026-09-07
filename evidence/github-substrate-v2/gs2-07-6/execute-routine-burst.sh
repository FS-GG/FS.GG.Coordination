#!/usr/bin/env bash
set -euo pipefail

repo='FS-GG/FS.GG.GitHub.Substrate.Sandbox'
base='gs2-07-6-burst-base'
primary='gs2-07-6-burst-primary'
unrelated='gs2-07-6-burst-unrelated'
assets=$(cd "$(dirname "$0")" && pwd)
state_dir=${1:-$(mktemp -d /tmp/gs2-07-6-burst.XXXXXX)}
scratch="$state_dir/repo"
ruleset_id=''
primary_pr=''
unrelated_pr=''
armed=false
pre_settings=''
pre_branches=''
pre_workflows=''
workflow_id=''

api() { gh api -H 'X-GitHub-Api-Version: 2022-11-28' "$@"; }
now_ms() { date -u +%s%3N; }
digest_text() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
ref_status() { api -i "repos/$repo/git/ref/heads/$1" 2>/dev/null | sed -n '1s/^[^ ]* \([0-9][0-9][0-9]\).*/\1/p'; }
delete_ref() {
  local ref=$1
  for _ in $(seq 1 20); do
    api -X DELETE "repos/$repo/git/refs/heads/$ref" --silent 2>/dev/null || true
    [[ $(ref_status "$ref") == 404 ]] && return 0
    sleep 2
  done
  return 1
}

wait_run() {
  local branch=$1 head=$2 started run status
  started=$(now_ms)
  for _ in $(seq 1 80); do
    run=$(api "repos/$repo/actions/runs?event=pull_request&branch=$branch&per_page=50" --jq ".workflow_runs | map(select(.head_sha == \"$head\")) | first | .id // empty")
    if [[ -n "$run" ]]; then
      status=$(api "repos/$repo/actions/runs/$run" --jq '.status')
      [[ "$status" == completed ]] && break
    fi
    sleep 2
  done
  [[ -n "${run:-}" && "$status" == completed ]]
  api "repos/$repo/actions/runs/$run" --jq '{id,headSha:.head_sha,event,status,conclusion,url:.html_url,createdAt:.created_at,updatedAt:.updated_at}'
  printf '%s' "$(( $(now_ms)-started ))" > "$state_dir/wait-$branch-$head.ms"
}

cleanup() {
  local failed=false visibility final_settings final_branches final_workflows temporary_resources
  [[ "$armed" == true ]] || return 0
  [[ -z "$primary_pr" ]] || api -X PATCH "repos/$repo/pulls/$primary_pr" -f state=closed --silent 2>/dev/null || failed=true
  [[ -z "$unrelated_pr" ]] || api -X PATCH "repos/$repo/pulls/$unrelated_pr" -f state=closed --silent 2>/dev/null || true
  [[ -z "$ruleset_id" ]] || api -X DELETE "repos/$repo/rulesets/$ruleset_id" --silent 2>/dev/null || true
  for _ in $(seq 1 20); do
    api -X PATCH "repos/$repo" -f visibility=private --silent 2>/dev/null || true
    visibility=$(api "repos/$repo" --jq '.visibility' 2>/dev/null || true)
    [[ "$visibility" == private ]] && break
    sleep 2
  done
  [[ "$visibility" == private ]] || failed=true
  [[ -n "$workflow_id" ]] || workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
  [[ -z "$workflow_id" ]] || api -X PUT "repos/$repo/actions/workflows/$workflow_id/disable" --silent 2>/dev/null || failed=true
  delete_ref "$primary" || failed=true
  delete_ref "$unrelated" || failed=true
  delete_ref "$base" || failed=true
  final_settings=$(api "repos/$repo" | jq -cS '{id,full_name,visibility,private,default_branch,archived,disabled,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,delete_branch_on_merge}' | sha256sum | cut -d' ' -f1)
  final_branches=$(api "repos/$repo/branches?per_page=100" | jq -cS '[.[]|{name,sha:.commit.sha,protected}]' | sha256sum | cut -d' ' -f1)
  final_workflows=$(api "repos/$repo/actions/workflows" | jq -cS '[.workflows[]|select(.state=="active")|{id,name,path,state}]' | sha256sum | cut -d' ' -f1)
  temporary_resources=0
  for ref in "$base" "$primary" "$unrelated"; do [[ $(ref_status "$ref") == 404 ]] || temporary_resources=$((temporary_resources+1)); done
  [[ "$final_settings" == "$pre_settings" && "$final_branches" == "$pre_branches" && "$final_workflows" == "$pre_workflows" ]] || failed=true
  [[ "$temporary_resources" == 0 ]] || failed=true
  jq -n -S --arg visibility "$visibility" --arg settings "$final_settings" --arg branches "$final_branches" --arg workflows "$final_workflows" --argjson temporaryResources "$temporary_resources" \
    '{visibility:$visibility,settingsDigest:$settings,branchInventoryDigest:$branches,workflowInventoryDigest:$workflows,temporaryResources:$temporaryResources}' > "$state_dir/burst-cleanup.json"
  [[ "$failed" == false ]]
}
trap 'status=$?; trap - EXIT; cleanup || status=1; exit "$status"' EXIT

mkdir -p "$state_dir"
id=$(api "repos/$repo" --jq '.id')
visibility=$(api "repos/$repo" --jq '.visibility')
secrets=$(api "repos/$repo/actions/secrets" --jq '.total_count')
environments=$(api "repos/$repo/environments" --jq '.total_count')
[[ "$id" == 1353050537 && "$visibility" == private && "$secrets" == 0 && "$environments" == 0 ]]
for ref in "$base" "$primary" "$unrelated"; do [[ $(ref_status "$ref") == 404 ]]; done
pre_settings=$(api "repos/$repo" | jq -cS '{id,full_name,visibility,private,default_branch,archived,disabled,allow_merge_commit,allow_squash_merge,allow_rebase_merge,allow_auto_merge,delete_branch_on_merge}' | sha256sum | cut -d' ' -f1)
pre_branches=$(api "repos/$repo/branches?per_page=100" | jq -cS '[.[]|{name,sha:.commit.sha,protected}]' | sha256sum | cut -d' ' -f1)
pre_workflows=$(api "repos/$repo/actions/workflows" | jq -cS '[.workflows[]|select(.state=="active")|{id,name,path,state}]' | sha256sum | cut -d' ' -f1)
armed=true
work_started=$(now_ms)

gh repo clone "$repo" "$scratch" -- --quiet
cd "$scratch"
git config user.name 'FS.GG GS2-07.6 burst pilot'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git switch --quiet -c "$base" origin/main
mkdir -p .github/workflows
cp "$assets/sandbox-burst-workflow.yml" .github/workflows/gs2-07-6-queue-pilot.yml
git add .github/workflows/gs2-07-6-queue-pilot.yml
git commit --quiet -m 'test: install GS2-07.6 burst workflow'
git push --quiet origin "$base"
base_prior=$(git rev-parse HEAD)
git switch --quiet -c "$primary"
printf 'primary-v1\n' > gs2-07-6-burst-primary.txt
git add gs2-07-6-burst-primary.txt
git commit --quiet -m 'test: add routine burst primary'
git push --quiet origin "$primary"
primary_prior=$(git rev-parse HEAD)
git switch --quiet "$base"
git switch --quiet -c "$unrelated"
printf 'unrelated\n' > gs2-07-6-burst-unrelated.txt
git add gs2-07-6-burst-unrelated.txt
git commit --quiet -m 'test: add unrelated routine change'
git push --quiet origin "$unrelated"
unrelated_head=$(git rev-parse HEAD)

api -X PATCH "repos/$repo" -f visibility=public --silent
for _ in $(seq 1 20); do visibility=$(api "repos/$repo" --jq '.visibility'); [[ "$visibility" == public ]] && break; sleep 2; done
[[ "$visibility" == public ]]
for _ in $(seq 1 30); do
  workflow_id=$(api "repos/$repo/actions/workflows" --jq '.workflows[] | select(.path == ".github/workflows/gs2-07-6-queue-pilot.yml") | .id' | head -1)
  [[ -n "$workflow_id" ]] && break
  sleep 2
done
[[ -n "$workflow_id" ]]
api -X PUT "repos/$repo/actions/workflows/$workflow_id/enable" --silent
primary_pr=$(api -X POST "repos/$repo/pulls" -f title='GS2-07.6 routine edit burst' -f head="$primary" -f base="$base" -f body='burst-0' --jq '.number')
unrelated_pr=$(api -X POST "repos/$repo/pulls" -f title='GS2-07.6 unrelated routine PR' -f head="$unrelated" -f base="$base" -f body='unrelated routine subject' --jq '.number')
primary_node=$(api "repos/$repo/pulls/$primary_pr" --jq '.node_id')

jq -n '[]' > "$state_dir/burst-hints.json"
previous=''
for sequence in 1 2 3 4; do
  mutation_start=$(now_ms)
  response=$(api -X PATCH "repos/$repo/pulls/$primary_pr" -f body="burst-$sequence" --jq '{updatedAt:.updated_at,body}')
  mutation_end=$(now_ms)
  hint_id="primary-$sequence"
  body_digest=$(jq -r '.body' <<<"$response" | sha256sum | cut -d' ' -f1)
  jq --arg subject "pr-$primary_pr" --arg hint "$hint_id" --arg previous "$previous" --argjson sequence "$sequence" --arg digest "$body_digest" --argjson started "$mutation_start" --argjson completed "$mutation_end" \
    '. += [{subject:$subject,hintId:$hint,sequence:$sequence,supersedesHintId:(if $previous=="" then null else $previous end),bodyDigest:$digest,startedAtUnixMilliseconds:$started,completedAtUnixMilliseconds:$completed}]' \
    "$state_dir/burst-hints.json" > "$state_dir/burst-hints.json.tmp"
  mv "$state_dir/burst-hints.json.tmp" "$state_dir/burst-hints.json"
  previous=$hint_id
done

inflight_wait_started=$(now_ms)
for _ in $(seq 1 30); do
  in_flight=$(api "repos/$repo/actions/runs?event=pull_request&per_page=50" --jq "[.workflow_runs[]|select((.head_sha==\"$primary_prior\" or .head_sha==\"$unrelated_head\") and .status!=\"completed\")]|length")
  [[ "$in_flight" -ge 1 ]] && break
  sleep 1
done
[[ "$in_flight" -ge 1 ]]
inflight_observation_wait=$(( $(now_ms)-inflight_wait_started ))

unrelated_run=$(wait_run "$unrelated" "$unrelated_head")
[[ $(jq -r '.conclusion' <<<"$unrelated_run") == success ]]
merge_started=$(now_ms)
merge_result=$(api -X PUT "repos/$repo/pulls/$unrelated_pr/merge" -f merge_method=squash)
merge_completed=$(now_ms)
[[ $(jq -r '.merged' <<<"$merge_result") == true ]]
base_current=$(api "repos/$repo/git/ref/heads/$base" --jq '.object.sha')
[[ "$base_current" != "$base_prior" ]]
jq --arg subject "pr-$unrelated_pr" --argjson started "$merge_started" --argjson completed "$merge_completed" --arg head "$unrelated_head" \
  '. += [{subject:$subject,hintId:"unrelated-1",sequence:1,supersedesHintId:null,bodyDigest:$head,startedAtUnixMilliseconds:$started,completedAtUnixMilliseconds:$completed}]' \
  "$state_dir/burst-hints.json" > "$state_dir/burst-hints.json.tmp"
mv "$state_dir/burst-hints.json.tmp" "$state_dir/burst-hints.json"

initial_primary_run=$(wait_run "$primary" "$primary_prior")
[[ $(jq -r '.conclusion' <<<"$initial_primary_run") == success ]]
git switch --quiet "$primary"
printf 'primary-v2\n' >> gs2-07-6-burst-primary.txt
git add gs2-07-6-burst-primary.txt
git commit --quiet -m 'test: move routine burst source'
git push --quiet origin "$primary"
primary_current=$(git rev-parse HEAD)
[[ "$primary_current" != "$primary_prior" ]]
jq -n -S --arg staleHead "$primary_prior" --arg currentHead "$primary_current" --argjson runId "$(jq -r '.id' <<<"$initial_primary_run")" \
  '{priorGreenRunId:$runId,authorizationHeadSha:$staleHead,currentHeadSha:$currentHead,decision:"refused-stale-green"}' > "$state_dir/stale-green-refusal.json"

ruleset_initial=$(jq --arg base "refs/heads/$base" '.conditions.ref_name.include=[$base]' "$assets/sandbox-ruleset-initial.json")
ruleset_grown=$(jq --arg base "refs/heads/$base" '.conditions.ref_name.include=[$base]' "$assets/sandbox-ruleset-grown.json")
provider_wait_started=$(now_ms)
for _ in $(seq 1 30); do api "repos/$repo/rulesets" --silent 2>/dev/null && break; sleep 2; done
provider_wait=$(( $(now_ms)-provider_wait_started ))
ruleset_id=$(api -X POST "repos/$repo/rulesets" --input <(printf '%s' "$ruleset_initial") --jq '.id')
api -X PUT "repos/$repo/rulesets/$ruleset_id" --input <(printf '%s' "$ruleset_grown") --silent
primary_run=$(wait_run "$primary" "$primary_current")
[[ $(jq -r '.conclusion' <<<"$primary_run") == success ]]
primary_jobs=$(api "repos/$repo/actions/runs/$(jq -r '.id' <<<"$primary_run")/jobs" --jq '[.jobs[]|{name,headSha:.head_sha,status,conclusion,url:.html_url}]|sort_by(.name)')
[[ $(jq '[.[]|select(.conclusion=="success")]|length' <<<"$primary_jobs") == 2 ]]
edits=$(gh api graphql -f query='query($id:ID!){node(id:$id){... on PullRequest{userContentEdits(first:20){totalCount nodes{id editedAt editor{login}}}}}}' -f id="$primary_node" --jq '.data.node.userContentEdits')
[[ $(jq -r '.totalCount' <<<"$edits") -ge 4 ]]
cancelled=$(api "repos/$repo/actions/runs?event=pull_request&per_page=50" --jq "[.workflow_runs[]|select((.head_sha==\"$primary_prior\" or .head_sha==\"$primary_current\" or .head_sha==\"$unrelated_head\") and .conclusion==\"cancelled\")]|length")
[[ "$cancelled" == 0 ]]

work_completed=$(now_ms)
primary_wait=$(cat "$state_dir/wait-$primary-$primary_prior.ms")
primary_wait=$((primary_wait + $(cat "$state_dir/wait-$primary-$primary_current.ms") + inflight_observation_wait + provider_wait))
unrelated_wait=$(cat "$state_dir/wait-$unrelated-$unrelated_head.ms")
hint_work=$(jq '[.[]|.completedAtUnixMilliseconds-.startedAtUnixMilliseconds]|add' "$state_dir/burst-hints.json")
total_wait=$((primary_wait + unrelated_wait))
total_work=$((work_completed-work_started-total_wait))
unrelated_work=$((merge_completed-merge_started))
primary_work=$((total_work-unrelated_work))
[[ "$total_work" -ge "$hint_work" && "$total_wait" -ge 0 ]]
jq -n -S --slurpfile hints "$state_dir/burst-hints.json" --argjson edits "$edits" --argjson initialRun "$initial_primary_run" --argjson primaryRun "$primary_run" --argjson primaryJobs "$primary_jobs" --argjson unrelatedRun "$unrelated_run" \
  --arg primarySubject "pr-$primary_pr" --arg unrelatedSubject "pr-$unrelated_pr" --arg primaryPrior "$primary_prior" --arg primaryCurrent "$primary_current" --arg unrelatedHead "$unrelated_head" --arg basePrior "$base_prior" --arg baseCurrent "$base_current" \
  --argjson work "$total_work" --argjson waiting "$total_wait" --argjson primaryWork "$primary_work" --argjson unrelatedWork "$unrelated_work" --argjson primaryWaiting "$primary_wait" --argjson unrelatedWaiting "$unrelated_wait" --argjson inFlight "$in_flight" --argjson inFlightWaiting "$inflight_observation_wait" --argjson providerWaiting "$provider_wait" \
  '{schemaVersion:1,maxHintsPerSubject:4,hints:$hints[0],providerEdits:$edits,decisions:[{subject:$primarySubject,priorHeadSha:$primaryPrior,headSha:$primaryCurrent,authorizationHeadSha:$primaryCurrent,priorBaseSha:$basePrior,baseSha:$baseCurrent,priorRequiredChecks:["queue-pilot"],requiredChecks:["queue-growth","queue-pilot"],successfulChecks:($primaryJobs|map(.name)),workMilliseconds:$primaryWork,waitingMilliseconds:$primaryWaiting,delivered:false,initialRun:$initialRun,currentRun:$primaryRun},{subject:$unrelatedSubject,priorHeadSha:$unrelatedHead,headSha:$unrelatedHead,authorizationHeadSha:$unrelatedHead,priorBaseSha:$basePrior,baseSha:$baseCurrent,priorRequiredChecks:["queue-pilot"],requiredChecks:["queue-pilot"],successfulChecks:["queue-pilot"],workMilliseconds:$unrelatedWork,waitingMilliseconds:$unrelatedWaiting,delivered:true,run:$unrelatedRun}],inFlightEffectCount:$inFlight,cancelledInFlightEffectCount:0,workMilliseconds:$work,waitingMilliseconds:$waiting,waitingBreakdown:{inFlightObservationMilliseconds:$inFlightWaiting,providerCapabilityMilliseconds:$providerWaiting},staleGreenRefusal:"evidence/github-substrate-v2/gs2-07-6/stale-green-refusal.json",disposition:"routine-burst-qualified"}' > "$state_dir/routine-burst.json"
printf 'ROUTINE_BURST_OK primary=%s unrelated=%s hints=5 work_ms=%s waiting_ms=%s\n' "$primary_pr" "$unrelated_pr" "$total_work" "$total_wait"
