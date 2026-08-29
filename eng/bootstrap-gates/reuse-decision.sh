#!/usr/bin/env bash
set -euo pipefail
test -f eng/bootstrap-qualification-plan.json || { echo "missing qualification subject: eng/bootstrap-qualification-plan.json" >&2; exit 1; }
test -f eng/bootstrap-ci.fsx || { echo "missing qualification subject: eng/bootstrap-ci.fsx" >&2; exit 1; }
root="${FSGG_RUNNER_TEMP:?FSGG_RUNNER_TEMP is required}"
candidate="${FSGG_CANDIDATE_SHA:?FSGG_CANDIDATE_SHA is required}"
current_run="${FSGG_CURRENT_RUN_ID:?FSGG_CURRENT_RUN_ID is required}"
repository="${FSGG_REPOSITORY:?FSGG_REPOSITORY is required}"
mkdir -p "$root"
decision="$root/decision.json"
candidates="$root/candidates.json"
not_before="$(jq -er '.reuse.notBefore' eng/bootstrap-qualification-plan.json)"

write_execute() {
  dotnet fsi eng/bootstrap-ci.fsx -- select --root . --head "$candidate" --output "$decision"
}

if ! gh api "repos/$repository/actions/artifacts?name=bootstrap-evidence-manifest&per_page=100" >"$candidates"; then
  write_execute
else
  selected=false
  while IFS=$'\t' read -r artifact_id run_id prior_head expires_at; do
    [ "$run_id" = "$current_run" ] && continue
    run_json="$root/run-$run_id.json"
    if ! gh api "repos/$repository/actions/runs/$run_id" >"$run_json"; then
      continue
    fi
    if ! jq -e --arg head "$prior_head" '
        .path == ".github/workflows/bootstrap-qualification.yml" and
        .status == "completed" and .conclusion == "success" and
        .head_sha == $head and (.run_attempt | type == "number")
      ' "$run_json" >/dev/null; then
      continue
    fi
    attempt="$(jq -er '.run_attempt' "$run_json")"
    jobs_json="$root/run-$run_id-jobs.json"
    runner_minutes=""
    if gh api "repos/$repository/actions/runs/$run_id/jobs?filter=latest&per_page=100" >"$jobs_json"; then
      runner_minutes="$(jq -er '
        if .total_count > 0 and .total_count <= 100 and .total_count == (.jobs | length)
           and (.jobs | all(.status == "completed" and .conclusion == "success" and .started_at != null and .completed_at != null))
        then ([.jobs[] | ((.completed_at | fromdateiso8601) - (.started_at | fromdateiso8601))] | add / 60 * 1000000 | round / 1000000)
        else empty end' "$jobs_json" 2>/dev/null || true)"
    fi
    archive="$root/artifact-$artifact_id.zip"
    extracted="$root/artifact-$artifact_id"
    if ! gh api "repos/$repository/actions/artifacts/$artifact_id/zip" >"$archive"; then
      continue
    fi
    mkdir -p "$extracted"
    if ! unzip -qq "$archive" -d "$extracted"; then
      continue
    fi
    manifest="$extracted/bootstrap-evidence.json"
    [ -f "$manifest" ] || continue
    select_args=(select --root . --head "$candidate" --output "$decision" --prior-manifest "$manifest"
      --prior-head "$prior_head" --prior-run "$run_id" --prior-attempt "$attempt" --expires "$expires_at")
    if [ -n "$runner_minutes" ]; then select_args+=(--runner-minutes "$runner_minutes"); fi
    if dotnet fsi eng/bootstrap-ci.fsx -- "${select_args[@]}" >/dev/null 2>&1 \
       && [ "$(jq -er '.decision' "$decision")" = "reuse" ]; then
      selected=true
      break
    fi
  done < <(jq -r --arg current "$current_run" --arg not_before "$not_before" '
      [.artifacts[]
       | select(.expired == false)
       | select(.created_at >= $not_before)
       | select((.workflow_run.id | tostring) != $current)
       | [.id, .workflow_run.id, .workflow_run.head_sha, .expires_at]]
      | sort_by(.[1], .[0]) | reverse[] | @tsv
    ' "$candidates")
  if [ "$selected" != true ]; then
    write_execute
  fi
fi

route="$(jq -er '.decision' "$decision")"
prior_run="$(jq -r '.prior.runId // ""' "$decision")"
printf 'route=%s\n' "$route" >>"${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
printf 'prior-run-id=%s\n' "$prior_run" >>"$GITHUB_OUTPUT"
printf 'qualification route: %s\n' "$route"
