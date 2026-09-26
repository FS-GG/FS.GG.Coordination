#!/usr/bin/env bash
set -euo pipefail
: "${GH_TOKEN:?GH_TOKEN is required}"
root="${RUNNER_TEMP:?RUNNER_TEMP is required}/optimistic-selection"; mkdir -p "$root"
current="${RUNNER_TEMP}/optimistic-validation/candidate-obligation.json"; candidate="$(jq -er '.candidate' "$current")"
current_plan="${RUNNER_TEMP}/optimistic-validation/partition-plan.json"; current_plan_sha="$(jq -er '.qualificationPlanSha256' "$current_plan")"
candidate_limit="$(jq -er '.selection.priorAggregateCandidateLimit' eng/optimistic-qualification-plan.json)"; test "$candidate_limit" -eq 25
repository="${GITHUB_REPOSITORY:?repository required}"; args=(classify --obligation "$current" --current-plan "$current_plan" --output "$root/selection.json"); stale_diagnostics=0
skip_prior() {
  if [[ $stale_diagnostics -lt 3 ]]; then
    echo "qualification prior skipped: $1" >&2
  fi
  stale_diagnostics=$((stale_diagnostics + 1))
}
# Discovery is an optimization, not authority. An unavailable or truncated
# repository listing can only forgo reuse; the current candidate still runs its
# complete coherent qualification. Keep the API budget independent of the
# repository's artifact retention history.
artifact_pages="$root/artifact-pages.jsonl"
: > "$artifact_pages"
discovery_ok=true
candidate_count=0
for page in {1..10}; do
  page_file="$root/artifact-page-$page.json"
  if ! gh api "repos/$repository/actions/artifacts?per_page=100&page=$page" > "$page_file" \
      || ! jq -e '(.artifacts | type == "array") and all(.artifacts[]; (.name|type == "string") and (.expired|type == "boolean") and (if (.name|startswith("coherent-aggregate-")) then ((.id|type == "number") and (.workflow_run.id|type == "number") and (.workflow_run.head_sha|type == "string") and (.expires_at|type == "string") and (.created_at|type == "string")) else true end))' "$page_file" >/dev/null; then
    echo "qualification prior discovery unavailable; validating current candidate" >&2
    discovery_ok=false
    break
  fi
  cat "$page_file" >> "$artifact_pages"
  printf '\n' >> "$artifact_pages"
  page_candidates="$(jq --arg candidate "$candidate" '[.artifacts[] | select(.expired == false and (.name|startswith("coherent-aggregate-")) and .workflow_run.head_sha != $candidate)] | length' "$page_file")"
  candidate_count=$((candidate_count + page_candidates))
  if [[ "$candidate_count" -ge "$candidate_limit" ]]; then break; fi
  if [[ "$(jq '.artifacts | length' "$page_file")" -lt 100 ]]; then break; fi
done
if [[ "$discovery_ok" != true ]]; then : > "$artifact_pages"; fi
while IFS=$'\t' read -r artifact_id run_id expires_at; do
  archive="$root/prior-$artifact_id.zip"; prior="$root/prior-$artifact_id"
  gh api "repos/$repository/actions/artifacts/$artifact_id/zip" > "$archive" || continue
  mkdir "$prior"; unzip -q "$archive" -d "$prior" || continue
  test -s "$prior/candidate-obligation.json" && test -s "$prior/partition-plan.json" && test -s "$prior/coherent-aggregate-receipt.json" || continue
  if [[ "$(jq -er '.qualificationPlanSha256' "$prior/partition-plan.json" 2>/dev/null || true)" != "$current_plan_sha" ]]; then
    skip_prior "qualification plan differs run=$run_id"
    continue
  fi
  run="$root/run-$run_id.json"
  gh api "repos/$repository/actions/runs/$run_id" > "$run" || continue
  jq -e --arg current "$GITHUB_RUN_ID" '.status == "completed" and .conclusion == "success" and (.id|tostring) != $current' "$run" >/dev/null || continue
  if ! dotnet fsi eng/optimistic-validation.fsx -- validate-prior \
    --obligation "$current" --current-plan "$current_plan" \
    --prior-obligation "$prior/candidate-obligation.json" --prior-plan "$prior/partition-plan.json" \
    --aggregate-receipt "$prior/coherent-aggregate-receipt.json" >/dev/null 2>&1; then
    skip_prior "incompatible or invalid executed receipt run=$run_id"
    continue
  fi
  prior_receipt="$(sha256sum "$prior/coherent-aggregate-receipt.json" | cut -d' ' -f1)"
  prior_args=(--prior-obligation "$prior/candidate-obligation.json" --prior-run "$run_id" \
    --prior-attempt "$(jq -er '.run_attempt' "$run")" --prior-receipt "$prior_receipt" \
    --prior-completed "$(jq -er '.updated_at' "$run")" --prior-expires "$expires_at" \
    --prior-plan "$prior/partition-plan.json" --aggregate-receipt "$prior/coherent-aggregate-receipt.json")
  if ! cmp -s "$prior/protocol.bindings.json" "${RUNNER_TEMP}/optimistic-validation/protocol.bindings.json"; then
    correspondence="$(bash eng/bootstrap-gates/optimistic-binding-correspondence.sh "$prior/protocol.bindings.json" "${RUNNER_TEMP}/optimistic-validation/protocol.bindings.json")" || continue
    prior_args+=(--binding-correspondence "$correspondence")
  fi
  args+=("${prior_args[@]}")
  break
done < <(jq -sr --arg candidate "$candidate" --argjson limit "$candidate_limit" '[.[].artifacts[] | select(.expired == false and (.name|startswith("coherent-aggregate-")) and .workflow_run.head_sha != $candidate)] | sort_by(.created_at,.id) | reverse | .[:$limit][] | [.id,.workflow_run.id,.expires_at] | @tsv' "$artifact_pages")
dotnet fsi eng/optimistic-validation.fsx -- "${args[@]}"
