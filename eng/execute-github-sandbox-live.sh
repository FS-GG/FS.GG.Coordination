#!/usr/bin/env bash
set -euo pipefail

phase="${1:-}"
owner="${FSGG_SANDBOX_OWNER:?FSGG_SANDBOX_OWNER is required}"
repo="${FSGG_SANDBOX_REPOSITORY:?FSGG_SANDBOX_REPOSITORY is required}"
repo_full="$owner/$repo"
repo_node="${FSGG_SANDBOX_REPOSITORY_NODE_ID:?FSGG_SANDBOX_REPOSITORY_NODE_ID is required}"
project_node="${FSGG_SANDBOX_PROJECT_NODE_ID:?FSGG_SANDBOX_PROJECT_NODE_ID is required}"
purpose="${FSGG_SANDBOX_PURPOSE:?FSGG_SANDBOX_PURPOSE is required}"
actor="${FSGG_SANDBOX_ACTOR:?FSGG_SANDBOX_ACTOR is required}"
actor_id="${FSGG_SANDBOX_ACTOR_ID:?FSGG_SANDBOX_ACTOR_ID is required}"
candidate="${FSGG_CANDIDATE_SHA:?FSGG_CANDIDATE_SHA is required}"
nonce="${FSGG_SANDBOX_RUN_NONCE:?FSGG_SANDBOX_RUN_NONCE is required}"
evidence="${FSGG_SANDBOX_EVIDENCE_DIR:?FSGG_SANDBOX_EVIDENCE_DIR is required}"
state="$evidence/live-state.json"
mkdir -p "$evidence"

[[ "$phase" == execute || "$phase" == cleanup ]] || { echo 'GSQ-LIVE-PHASE: expected execute or cleanup' >&2; exit 1; }
[[ "$candidate" =~ ^[0-9a-f]{40}$ ]] || { echo 'GSQ-LIVE-CANDIDATE: exact 40-hex candidate required' >&2; exit 1; }
[[ "$nonce" =~ ^[A-Za-z0-9._-]{16,160}$ ]] || { echo 'GSQ-LIVE-NONCE: safe bounded nonce required' >&2; exit 1; }
[[ "$repo_full" == FS-GG/FS.GG.GitHub.Substrate.Sandbox ]] || { echo 'GSQ-LIVE-TARGET: repository is not the registered sandbox' >&2; exit 1; }
[[ "$repo_node" == R_kgDOUKXpqQ && "$project_node" == PVT_kwDOEYAWY84BiESo ]] || { echo 'GSQ-LIVE-TARGET: node identity mismatch' >&2; exit 1; }
[[ "$purpose" == fsgg-sandbox-gs2-04-9 ]] || { echo 'GSQ-LIVE-TARGET: purpose mismatch' >&2; exit 1; }
[[ "$actor" == 'fs-gg-cross-repo-dispatch[bot]' && "$actor_id" == 297630107 ]] || { echo 'GSQ-LIVE-AUTHORITY: App identity mismatch' >&2; exit 1; }
[[ -n "${FSGG_SANDBOX_TOKEN:-}" ]] || { echo 'GSQ-LIVE-AUTHORITY: non-production token is missing' >&2; exit 1; }
export GH_TOKEN="$FSGG_SANDBOX_TOKEN"

sha256_text() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
api_json() { gh api "$@"; }
write_state() { local next="$state.tmp"; jq "$@" "$state" > "$next"; mv "$next" "$state"; }
record_effect() {
  local id="$1" surface="$2" pre="$3" post="$4" revision="$5"
  write_state --arg id "$id" --arg surface "$surface" --arg pre "$pre" --arg post "$post" --arg revision "$revision" \
    '.effects += [{operationId:$id,surface:$surface,preStateDigest:$pre,postStateDigest:$post,authoritativeRevision:$revision,disposition:"applied"}]'
}

verify_authority() {
  local viewer repository project
  viewer="$(api_json graphql -f query='{viewer{login databaseId}}' --jq '.data.viewer')"
  [[ "$(jq -r .login <<<"$viewer")" == "$actor" && "$(jq -r .databaseId <<<"$viewer")" == "$actor_id" ]] || { echo 'GSQ-LIVE-AUTHORITY: authenticated actor mismatch; refusing before any write' >&2; return 1; }
  repository="$(api_json "repos/$repo_full")"
  [[ "$(jq -r .node_id <<<"$repository")" == "$repo_node" && "$(jq -r .private <<<"$repository")" == true ]] || { echo 'GSQ-LIVE-TARGET: repository identity or privacy mismatch; refusing before any write' >&2; return 1; }
  [[ "$(jq -r .description <<<"$repository")" == 'fsgg-sandbox-gs2-04-9 disposable qualification target; never production' ]] || { echo 'GSQ-LIVE-TARGET: repository purpose mismatch; refusing before any write' >&2; return 1; }
  project="$(api_json graphql -f query='query($id:ID!){node(id:$id){... on ProjectV2{id title closed public}}}' -F id="$project_node" --jq '.data.node')"
  [[ "$(jq -r .id <<<"$project")" == "$project_node" && "$(jq -r .title <<<"$project")" == "$purpose" ]] || { echo 'GSQ-LIVE-TARGET: Project identity or purpose mismatch; refusing before any write' >&2; return 1; }
  [[ "$(jq -r .closed <<<"$project")" == false && "$(jq -r .public <<<"$project")" == false ]] || { echo 'GSQ-LIVE-TARGET: Project is closed or public; refusing before any write' >&2; return 1; }
}

execute_plan() {
  [[ ! -e "$state" ]] || { echo 'GSQ-LIVE-WARM-REUSE: state already exists' >&2; exit 1; }
  verify_authority

  local issue1 issue2 repository project_items base ref_name tag label
  issue1="$(api_json "repos/$repo_full/issues/1")"
  issue2="$(api_json "repos/$repo_full/issues/2")"
  repository="$(api_json "repos/$repo_full")"
  [[ "$(jq -r .state <<<"$issue1")" == open && "$(jq -r .state <<<"$issue2")" == open ]]
  [[ "$(jq -r .title <<<"$issue1")" == 'fsgg-sandbox-gs2-04-9 fixture primary' ]]
  [[ "$(jq -r .title <<<"$issue2")" == 'fsgg-sandbox-gs2-04-9 fixture secondary' ]]
  project_items="$(api_json graphql -f query='query($id:ID!){node(id:$id){... on Issue{projectItems(first:100){nodes{id project{id}}}}}}' -F id="$(jq -r .node_id <<<"$issue1")" --jq '.data.node.projectItems.nodes')"
  [[ "$(jq --arg id "$project_node" '[.[]|select(.project.id==$id)]|length' <<<"$project_items")" == 0 ]]
  [[ "$(api_json "repos/$repo_full/issues/1/sub_issues" --paginate --jq ".[]|select(.number==2)|.number" | wc -l)" == 0 ]]
  [[ "$(api_json "repos/$repo_full/issues/1/comments" --paginate --jq ".[]|select(.body|contains(\"$nonce\"))|.id" | wc -l)" == 0 ]]
  base="$(api_json "repos/$repo_full/git/ref/heads/main" --jq .object.sha)"
  ref_name="fsgg/sandbox/$nonce"
  tag="fsgg-sandbox-$nonce"
  # GitHub label names are limited to 50 characters. Keep the authoritative
  # nonce intact in evidence while using a bounded, run-unique resource name.
  label="fsgg-sandbox-${nonce:0:32}"
  [[ ${#label} -le 50 ]]

  jq -n --arg schema 'fsgg.github-substrate-v2.live-state/1' --arg candidate "$candidate" --arg nonce "$nonce" --arg repo "$repo_full" --arg repoNode "$repo_node" --arg projectNode "$project_node" \
    --argjson issue1 "$issue1" --argjson issue2 "$issue2" --argjson repository "$repository" --arg base "$base" --arg ref "$ref_name" --arg tag "$tag" --arg label "$label" \
    '{schema:$schema,candidateSha:$candidate,runNonce:$nonce,repository:$repo,repositoryNodeId:$repoNode,projectNodeId:$projectNode,pre:{issue1:{title:$issue1.title,body:$issue1.body,state:$issue1.state,nodeId:$issue1.node_id,databaseId:$issue1.id},issue2:{nodeId:$issue2.node_id,databaseId:$issue2.id},repository:{description:$repository.description,homepage:$repository.homepage}},resources:{relation:false,projectItemId:null,commentId:null,ref:null,releaseId:null,releaseAssetId:null,tag:null,label:null},effects:[]}' > "$state"

  local transport post issue_mutated relation project_add comment_created comment_updated ref_created file_created repo_mutated release_created release_updated asset_file asset_uploaded asset_digest
  transport="$(api_json rate_limit)"
  post="$(api_json "repos/$repo_full")"
  record_effect transport transport "$(sha256_text "$repository")" "$(sha256_text "$post$transport")" "$(jq -r .updated_at <<<"$post")"

  api_json --method POST "repos/$repo_full/labels" -f name="$label" -f color=5319e7 >/dev/null
  write_state --arg label "$label" '.resources.label=$label'
  issue_mutated="$(api_json --method PATCH "repos/$repo_full/issues/1" -f title="fsgg-sandbox-gs2-04-9 $nonce" -f labels[]="$label")"
  post="$(api_json "repos/$repo_full/issues/1")"
  [[ "$(jq -r .title <<<"$post")" == "fsgg-sandbox-gs2-04-9 $nonce" ]]
  record_effect issue-field issue-field "$(sha256_text "$issue1")" "$(sha256_text "$post")" "$(jq -r .updated_at <<<"$post")"

  relation="$(api_json --method POST "repos/$repo_full/issues/1/sub_issues" -F sub_issue_id="$(jq -r .id <<<"$issue2")")"
  write_state '.resources.relation=true'
  [[ "$(api_json "repos/$repo_full/issues/1/sub_issues" --jq '.[]|select(.number==2)|.number')" == 2 ]]
  record_effect native-relation native-relation "$(sha256_text absent)" "$(sha256_text "$relation")" "$(jq -r .updated_at <<<"$relation")"

  project_add="$(api_json graphql -f query='mutation($project:ID!,$content:ID!){addProjectV2ItemById(input:{projectId:$project,contentId:$content}){item{id}}}' -F project="$project_node" -F content="$(jq -r .node_id <<<"$issue1")" --jq '.data.addProjectV2ItemById.item')"
  write_state --arg id "$(jq -r .id <<<"$project_add")" '.resources.projectItemId=$id'
  # ProjectV2 membership is eventually consistent after addProjectV2ItemById.
  # The scoped App may not receive Project item content, so prove membership
  # with the authoritative item ID returned by the mutation. The bounded poll
  # handles propagation delay and still fails closed.
  local project_item_id
  project_item_id="$(jq -r .id <<<"$project_add")"
  for _ in {1..10}; do
    project_items="$(api_json graphql -f query='query($id:ID!){node(id:$id){... on ProjectV2{items(first:100){nodes{id content{... on Issue{id}}}}}}}' -F id="$project_node" --jq '.data.node.items.nodes')"
    [[ "$(jq --arg id "$project_item_id" '[.[]|select(.id==$id)]|length' <<<"$project_items")" == 1 ]] && break
    sleep 1
  done
  [[ "$(jq --arg id "$project_item_id" '[.[]|select(.id==$id)]|length' <<<"$project_items")" == 1 ]]
  record_effect project project "$(sha256_text absent)" "$(sha256_text "$project_add")" "$(jq -r .id <<<"$project_add")"

  comment_created="$(api_json --method POST "repos/$repo_full/issues/1/comments" -f body="fsgg-sandbox-created $nonce")"
  write_state --argjson id "$(jq -r .id <<<"$comment_created")" '.resources.commentId=$id'
  comment_updated="$(api_json --method PATCH "repos/$repo_full/issues/comments/$(jq -r .id <<<"$comment_created")" -f body="fsgg-sandbox-updated $nonce")"
  post="$(api_json "repos/$repo_full/issues/comments/$(jq -r .id <<<"$comment_created")")"
  [[ "$(jq -r .body <<<"$post")" == "fsgg-sandbox-updated $nonce" ]]
  record_effect comment-projection comment-projection "$(sha256_text absent)" "$(sha256_text "$post$comment_updated")" "$(jq -r .updated_at <<<"$post")"

  ref_created="$(api_json --method POST "repos/$repo_full/git/refs" -f ref="refs/heads/$ref_name" -f sha="$base")"
  write_state --arg ref "$ref_name" '.resources.ref=$ref'
  file_created="$(printf 'GS2-04.9 %s\n' "$nonce" | base64 -w0 | xargs -I{} gh api --method PUT "repos/$repo_full/contents/fsgg-sandbox/$nonce.txt" -f message="fsgg sandbox $nonce" -f content='{}' -f branch="$ref_name")"
  post="$(api_json "repos/$repo_full/git/ref/heads/$ref_name")"
  record_effect sharded-journal sharded-journal "$(sha256_text "$base")" "$(sha256_text "$post$file_created$ref_created")" "$(jq -r .object.sha <<<"$post")"

  repo_mutated="$(api_json --method PATCH "repos/$repo_full" -f homepage="https://example.invalid/$purpose/$nonce")"
  post="$(api_json "repos/$repo_full")"
  [[ "$(jq -r .homepage <<<"$post")" == "https://example.invalid/$purpose/$nonce" ]]
  record_effect repository-settings repository-settings "$(sha256_text "$repository")" "$(sha256_text "$post$repo_mutated")" "$(jq -r .updated_at <<<"$post")"

  release_created="$(api_json --method POST "repos/$repo_full/releases" -f tag_name="$tag" -f target_commitish=main -f name="$tag" -f body="GS2-04.9 $nonce" -F draft=true)"
  write_state --argjson id "$(jq -r .id <<<"$release_created")" --arg tag "$tag" '.resources.releaseId=$id|.resources.tag=$tag'
  release_updated="$(api_json --method PATCH "repos/$repo_full/releases/$(jq -r .id <<<"$release_created")" -f name="$tag-updated")"
  asset_file="$evidence/release-asset.txt"
  printf 'GS2-04.9 release asset %s\n' "$nonce" > "$asset_file"
  asset_uploaded="$(curl --fail-with-body --silent --show-error \
    --request POST \
    --header "Authorization: Bearer $FSGG_SANDBOX_TOKEN" \
    --header 'Accept: application/vnd.github+json' \
    --header 'Content-Type: text/plain' \
    --header 'X-GitHub-Api-Version: 2022-11-28' \
    --data-binary "@$asset_file" \
    "https://uploads.github.com/repos/$repo_full/releases/$(jq -r .id <<<"$release_created")/assets?name=qualification.txt")"
  write_state --argjson id "$(jq -r .id <<<"$asset_uploaded")" '.resources.releaseAssetId=$id'
  gh api -H 'Accept: application/octet-stream' "repos/$repo_full/releases/assets/$(jq -r .id <<<"$asset_uploaded")" > "$evidence/retrieved-asset.txt"
  asset_digest="$(sha256sum "$evidence/retrieved-asset.txt" | cut -d' ' -f1)"
  [[ "$asset_digest" == "$(sha256sum "$asset_file" | cut -d' ' -f1)" ]]
  post="$(api_json "repos/$repo_full/releases/$(jq -r .id <<<"$release_created")")"
  record_effect actions-release-feed actions-release-feed "$(sha256_text absent)" "$(sha256_text "$post$release_updated$asset_uploaded$asset_digest")" "$(jq -r .updated_at <<<"$post")"

  jq '{schema:"fsgg.github-substrate-v2.live-execution/1",candidateSha:.candidateSha,runNonce:.runNonce,effects:.effects,resources:.resources}' "$state" > "$evidence/execution.json"
  [[ "$(jq '.effects|length' "$state")" == 8 ]]
}

cleanup_plan() {
  [[ -s "$state" ]] || { echo 'GSQ-LIVE-CLEANUP: state is missing' >&2; exit 1; }
  verify_authority
  [[ "$(jq -r .candidateSha "$state")" == "$candidate" && "$(jq -r .runNonce "$state")" == "$nonce" ]]

  local release_id tag homepage description ref comment_id project_item relation label issue_title issue_body
  release_id="$(jq -r '.resources.releaseId // empty' "$state")"
  tag="$(jq -r '.resources.tag // empty' "$state")"
  [[ -z "$release_id" ]] || api_json --method DELETE "repos/$repo_full/releases/$release_id" >/dev/null
  if [[ -n "$tag" ]] && api_json "repos/$repo_full/git/ref/tags/$tag" >/dev/null 2>&1; then api_json --method DELETE "repos/$repo_full/git/refs/tags/$tag" >/dev/null; fi

  homepage="$(jq -r '.pre.repository.homepage // ""' "$state")"
  description="$(jq -r '.pre.repository.description // ""' "$state")"
  jq -n --arg homepage "$homepage" --arg description "$description" '{homepage:(if $homepage=="" then null else $homepage end),description:$description}' > "$evidence/repository-restore.json"
  api_json --method PATCH "repos/$repo_full" --input "$evidence/repository-restore.json" >/dev/null

  ref="$(jq -r '.resources.ref // empty' "$state")"
  if [[ -n "$ref" ]] && api_json "repos/$repo_full/git/ref/heads/$ref" >/dev/null 2>&1; then api_json --method DELETE "repos/$repo_full/git/refs/heads/$ref" >/dev/null; fi

  comment_id="$(jq -r '.resources.commentId // empty' "$state")"
  [[ -z "$comment_id" ]] || api_json --method DELETE "repos/$repo_full/issues/comments/$comment_id" >/dev/null

  project_item="$(jq -r '.resources.projectItemId // empty' "$state")"
  [[ -z "$project_item" ]] || api_json graphql -f query='mutation($project:ID!,$item:ID!){deleteProjectV2Item(input:{projectId:$project,itemId:$item}){deletedItemId}}' -F project="$project_node" -F item="$project_item" >/dev/null

  relation="$(jq -r .resources.relation "$state")"
  [[ "$relation" != true ]] || api_json --method DELETE "repos/$repo_full/issues/1/sub_issue" -F sub_issue_id="$(jq -r .pre.issue2.databaseId "$state")" >/dev/null

  issue_title="$(jq -r .pre.issue1.title "$state")"
  issue_body="$(jq -r '.pre.issue1.body // ""' "$state")"
  jq -n --arg title "$issue_title" --arg body "$issue_body" '{title:$title,body:$body,state:"open",labels:[]}' > "$evidence/issue-restore.json"
  api_json --method PATCH "repos/$repo_full/issues/1" --input "$evidence/issue-restore.json" >/dev/null
  label="$(jq -r '.resources.label // empty' "$state")"
  [[ -z "$label" ]] || api_json --method DELETE "repos/$repo_full/labels/$label" >/dev/null

  local final_issue final_repo project_items residue=0
  final_issue="$(api_json "repos/$repo_full/issues/1")"
  final_repo="$(api_json "repos/$repo_full")"
  [[ "$(jq -r .title <<<"$final_issue")" == "$issue_title" ]] || residue=$((residue+1))
  [[ "$(jq -r '.body // ""' <<<"$final_issue")" == "$issue_body" ]] || residue=$((residue+1))
  [[ "$(jq -r .state <<<"$final_issue")" == open ]] || residue=$((residue+1))
  [[ "$(jq -r '.labels|length' <<<"$final_issue")" == 0 ]] || residue=$((residue+1))
  [[ "$(jq -r '.homepage // ""' <<<"$final_repo")" == "$homepage" ]] || residue=$((residue+1))
  [[ "$(jq -r '.description // ""' <<<"$final_repo")" == "$description" ]] || residue=$((residue+1))
  [[ "$(api_json "repos/$repo_full/issues/1/comments" --paginate --jq ".[]|select(.body|contains(\"$nonce\"))|.id" | wc -l)" == 0 ]] || residue=$((residue+1))
  [[ "$(api_json "repos/$repo_full/issues/1/sub_issues" --paginate --jq '.[]|select(.number==2)|.number' | wc -l)" == 0 ]] || residue=$((residue+1))
  project_items="$(api_json graphql -f query='query($id:ID!){node(id:$id){... on ProjectV2{items(first:100){nodes{id content{... on Issue{id}}}}}}}' -F id="$project_node" --jq '.data.node.items.nodes')"
  [[ -z "$project_item" ]] || [[ "$(jq --arg id "$project_item" '[.[]|select(.id==$id)]|length' <<<"$project_items")" == 0 ]] || residue=$((residue+1))
  [[ -z "$ref" ]] || { api_json "repos/$repo_full/git/ref/heads/$ref" >/dev/null 2>&1 && residue=$((residue+1)) || true; }
  [[ -z "$release_id" ]] || { api_json "repos/$repo_full/releases/$release_id" >/dev/null 2>&1 && residue=$((residue+1)) || true; }

  jq -n --arg candidate "$candidate" --arg nonce "$nonce" --argjson residue "$residue" --arg issueDigest "$(sha256_text "$final_issue")" --arg repositoryDigest "$(sha256_text "$final_repo")" \
    '{schema:"fsgg.github-substrate-v2.cleanup/1",candidateSha:$candidate,runNonce:$nonce,disposition:(if $residue==0 then "complete" else "residual" end),residualCount:$residue,issueDigest:$issueDigest,repositoryDigest:$repositoryDigest}' > "$evidence/cleanup.json"
  jq -n --arg candidate "$candidate" --arg nonce "$nonce" --arg planDigest "$(sha256sum "$state" | cut -d' ' -f1)" --arg executionDigest "$(sha256sum "$evidence/execution.json" 2>/dev/null | cut -d' ' -f1)" --arg cleanupDigest "$(sha256sum "$evidence/cleanup.json" | cut -d' ' -f1)" --argjson cleanup "$(cat "$evidence/cleanup.json")" \
    '{schema:"fsgg.github-substrate-v2.closure/1",candidateSha:$candidate,runNonce:$nonce,planDigest:$planDigest,executionDigest:$executionDigest,cleanupDigest:$cleanupDigest,cleanup:$cleanup}' > "$evidence/closure.json"
  [[ "$residue" == 0 ]]
}

if [[ "$phase" == execute ]]; then execute_plan; else cleanup_plan; fi
