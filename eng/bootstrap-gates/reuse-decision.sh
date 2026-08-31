#!/usr/bin/env bash
set -euo pipefail
test -f eng/bootstrap-qualification-plan.json || { echo "missing qualification subject: eng/bootstrap-qualification-plan.json" >&2; exit 1; }
test -f eng/milestone-qualification.json || { echo "missing milestone qualification state" >&2; exit 1; }
root="${FSGG_RUNNER_TEMP:?FSGG_RUNNER_TEMP is required}"
candidate="${FSGG_CANDIDATE_SHA:?FSGG_CANDIDATE_SHA is required}"
current_run="${FSGG_CURRENT_RUN_ID:?FSGG_CURRENT_RUN_ID is required}"
repository="${FSGG_REPOSITORY:?FSGG_REPOSITORY is required}"
mkdir -p "$root"
decision="$root/decision.json"; formal_decision="$root/formal-decision.json"; milestone="$root/milestone.json"; candidates="$root/candidates.json"
not_before="$(jq -er '.reuse.notBefore' eng/bootstrap-qualification-plan.json)"
formal_not_before="$(jq -er '.formalReuse.notBefore' eng/bootstrap-qualification-plan.json)"
max_candidates="$(jq -er '.reuse.maxCandidateArtifacts | select(. > 0 and . <= 100)' eng/bootstrap-qualification-plan.json)"

dotnet fsi eng/bootstrap-ci.fsx -- milestone --root . --output "$milestone"
mode="$(jq -er '.mode | select(. == "scoped" or . == "comprehensive")' "$milestone")"
write_execute() { dotnet fsi eng/bootstrap-ci.fsx -- select --root . --head "$candidate" --output "$decision"; }
write_formal_execute() { dotnet fsi eng/bootstrap-ci.fsx -- formal-select --root . --head "$candidate" --output "$formal_decision"; }
write_execute; write_formal_execute
subject="$(jq -er '.subjectSha256' "$decision")"
dotnet fsi eng/bootstrap-ci.fsx -- formal-subject --root . --output "$root/formal-subject.json" >/dev/null
formal_subject="$(jq -er '.subjectSha256' "$root/formal-subject.json")"

select_whole_tree() {
  local artifact_name="bootstrap-evidence-manifest-$subject"
  gh api "repos/$repository/actions/artifacts?name=$artifact_name&per_page=$max_candidates" >"$candidates" || return
  while IFS=$'\t' read -r artifact_id run_id prior_head expires_at; do
    [ "$run_id" = "$current_run" ] && continue
    local run_json="$root/run-$run_id.json" jobs_json="$root/run-$run_id-jobs.json" runner_minutes=""
    gh api "repos/$repository/actions/runs/$run_id" >"$run_json" || continue
    jq -e --arg head "$prior_head" '.path == ".github/workflows/bootstrap-qualification.yml" and .status == "completed" and .conclusion == "success" and .head_sha == $head and (.run_attempt | type == "number")' "$run_json" >/dev/null || continue
    if gh api "repos/$repository/actions/runs/$run_id/jobs?filter=latest&per_page=100" >"$jobs_json"; then
      runner_minutes="$(jq -er 'if .total_count > 0 and .total_count <= 100 and .total_count == (.jobs|length) and (.jobs|all(.status == "completed" and (.conclusion == "success" or .conclusion == "skipped") and .started_at != null and .completed_at != null)) then ([.jobs[]|select(.conclusion == "success")|((.completed_at|fromdateiso8601)-(.started_at|fromdateiso8601))]|add/60*1000000|round/1000000) else empty end' "$jobs_json" 2>/dev/null || true)"
    fi
    local archive="$root/artifact-$artifact_id.zip" extracted="$root/artifact-$artifact_id" manifest
    gh api "repos/$repository/actions/artifacts/$artifact_id/zip" >"$archive" || continue
    mkdir -p "$extracted"; unzip -qq "$archive" -d "$extracted" || continue
    manifest="$extracted/bootstrap-evidence.json"; [ -f "$manifest" ] || continue
    local args=(select --root . --head "$candidate" --output "$decision" --prior-manifest "$manifest" --prior-head "$prior_head" --prior-run "$run_id" --prior-attempt "$(jq -er '.run_attempt' "$run_json")" --expires "$expires_at")
    [ -z "$runner_minutes" ] || args+=(--runner-minutes "$runner_minutes")
    if dotnet fsi eng/bootstrap-ci.fsx -- "${args[@]}" >/dev/null 2>&1 && [ "$(jq -er '.decision' "$decision")" = reuse ]; then return; fi
  done < <(jq -r --arg current "$current_run" --arg epoch "$not_before" --arg name "$artifact_name" '[.artifacts[]|select(.name==$name and .expired==false and .created_at >= $epoch and (.workflow_run.id|tostring)!=$current)|[.id,.workflow_run.id,.workflow_run.head_sha,.expires_at]]|sort_by(.[1],.[0])|reverse[]|@tsv' "$candidates")
  write_execute
}

select_formal() {
  local artifact_name="canonical-quint-$formal_subject"
  gh api "repos/$repository/actions/artifacts?name=$artifact_name&per_page=$max_candidates" >"$candidates" || return
  while IFS=$'\t' read -r artifact_id run_id prior_head expires_at; do
    [ "$run_id" = "$current_run" ] && continue
    local run_json="$root/formal-run-$run_id.json" jobs_json="$root/formal-run-$run_id-jobs.json"
    gh api "repos/$repository/actions/runs/$run_id" >"$run_json" || continue
    jq -e --arg head "$prior_head" '.path == ".github/workflows/bootstrap-qualification.yml" and .status == "completed" and .conclusion == "success" and .head_sha == $head' "$run_json" >/dev/null || continue
    gh api "repos/$repository/actions/runs/$run_id/jobs?filter=latest&per_page=100" >"$jobs_json" || continue
    jq -e '.jobs|any(.name == "canonical-quint" and .status == "completed" and .conclusion == "success")' "$jobs_json" >/dev/null || continue
    local archive="$root/formal-artifact-$artifact_id.zip" extracted="$root/formal-artifact-$artifact_id" receipt runner_minutes
    gh api "repos/$repository/actions/artifacts/$artifact_id/zip" >"$archive" || continue
    mkdir -p "$extracted"; unzip -qq "$archive" -d "$extracted" || continue
    receipt="$extracted/qualification.json"; [ -f "$receipt" ] || continue
    runner_minutes="$(jq -er '[.jobs[]|select(.name == "canonical-quint")|((.completed_at|fromdateiso8601)-(.started_at|fromdateiso8601))]|add/60*1000000|round/1000000' "$jobs_json")"
    if dotnet fsi eng/bootstrap-ci.fsx -- formal-select --root . --head "$candidate" --output "$formal_decision" --prior-receipt "$receipt" --prior-head "$prior_head" --prior-run "$run_id" --prior-attempt "$(jq -er '.run_attempt' "$run_json")" --expires "$expires_at" --prior-subject "$formal_subject" --runner-minutes "$runner_minutes" >/dev/null 2>&1 && [ "$(jq -er '.decision' "$formal_decision")" = reuse ]; then return; fi
  done < <(jq -r --arg current "$current_run" --arg epoch "$formal_not_before" --arg name "$artifact_name" '[.artifacts[]|select(.name==$name and .expired==false and .created_at >= $epoch and (.workflow_run.id|tostring)!=$current)|[.id,.workflow_run.id,.workflow_run.head_sha,.expires_at]]|sort_by(.[1],.[0])|reverse[]|@tsv' "$candidates")
  write_formal_execute
}

if [ "$mode" = scoped ]; then
  select_whole_tree
  if [ "$(jq -er '.decision' "$decision")" = execute ]; then select_formal; fi
fi
route="$(jq -er '.decision' "$decision")"; formal_route="$(jq -er '.decision' "$formal_decision")"
printf 'route=%s\nprior-run-id=%s\nsubject-sha=%s\nformal-route=%s\nprior-formal-run-id=%s\nformal-subject-sha=%s\nqualification-mode=%s\n' "$route" "$(jq -r '.prior.runId // ""' "$decision")" "$subject" "$formal_route" "$(jq -r '.prior.runId // ""' "$formal_decision")" "$formal_subject" "$mode" >>"${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
printf 'qualification route: whole=%s formal=%s mode=%s\n' "$route" "$formal_route" "$mode"
