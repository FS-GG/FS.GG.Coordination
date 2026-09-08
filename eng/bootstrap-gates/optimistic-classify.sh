#!/usr/bin/env bash
set -euo pipefail
: "${GH_TOKEN:?GH_TOKEN is required}"
root="${RUNNER_TEMP:?RUNNER_TEMP is required}/optimistic-selection"
mkdir -p "$root"
current="${RUNNER_TEMP}/optimistic-validation/candidate-obligation.json"
candidate="$(jq -er '.candidate' "$current")"
repository="${GITHUB_REPOSITORY:?repository required}"
args=(classify --obligation "$current" --output "$root/selection.json")
gh api --paginate --slurp "repos/$repository/actions/artifacts?per_page=100" > "$root/artifact-pages.json"
while IFS=$'\t' read -r artifact_id run_id expires_at; do
  run="$root/run-$run_id.json"
  gh api "repos/$repository/actions/runs/$run_id" > "$run" || continue
  jq -e --arg current "$GITHUB_RUN_ID" '.status == "completed" and .conclusion == "success" and (.id|tostring) != $current' "$run" >/dev/null || continue
  archive="$root/prior.zip"; prior="$root/prior"
  gh api "repos/$repository/actions/artifacts/$artifact_id/zip" > "$archive" || continue
  mkdir -p "$prior"; unzip -qo "$archive" -d "$prior" || continue
  test -s "$prior/candidate-obligation.json" && test -s "$prior/coherent-aggregate-receipt.json" || continue
  prior_receipt="$(sha256sum "$prior/coherent-aggregate-receipt.json" | cut -d' ' -f1)"
  prior_args=(--prior-obligation "$prior/candidate-obligation.json" --prior-run "$run_id" \
    --prior-attempt "$(jq -er '.run_attempt' "$run")" --prior-receipt "$prior_receipt" \
    --prior-completed "$(jq -er '.updated_at' "$run")" --prior-expires "$expires_at" \
    --aggregate-receipt "$prior/coherent-aggregate-receipt.json")
  if ! cmp -s "$prior/protocol.bindings.json" "${RUNNER_TEMP}/optimistic-validation/protocol.bindings.json"; then
    correspondence="$(bash eng/bootstrap-gates/optimistic-binding-correspondence.sh "$prior/protocol.bindings.json" "${RUNNER_TEMP}/optimistic-validation/protocol.bindings.json")" || continue
    prior_args+=(--binding-correspondence "$correspondence")
  fi
  args+=("${prior_args[@]}")
  break
done < <(jq -r --arg candidate "$candidate" '[.[].artifacts[] | select(.expired == false and (.name|startswith("coherent-aggregate-")) and .workflow_run.head_sha != $candidate)] | sort_by(.created_at,.id) | reverse[] | [.id,.workflow_run.id,.expires_at] | @tsv' "$root/artifact-pages.json")
dotnet fsi eng/optimistic-validation.fsx -- "${args[@]}"
