#!/usr/bin/env bash
set -euo pipefail

json_has_unique_members() {
  python3 - "$1" <<'PY'
import json
import sys

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON member: {key}")
        result[key] = value
    return result

with open(sys.argv[1], "rb") as stream:
    json.load(stream, object_pairs_hook=unique_object)
PY
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

write_disabled_decision() {
  local decision="$1"
  local reason="$2"
  printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"%s"}\n' "$reason" > "$decision"
}

resolve_current_authority() {
  local inventory="$1"
  local source_request="$2"
  local reviewed_authority="$3"
  local output="$4"
  local runtime_request="$5"
  local repository_root checkout_head actions_head event_name settings_receipt settings_sha base_revision request_base request_settings event_base queued_head
  local declared_inventory_path declared_request_path declared_settings_path inventory_sha source_request_sha settings_receipt_sha runtime_request_sha
  repository_root="$(git rev-parse --show-toplevel)" || return 1
  checkout_head="$(git -C "$repository_root" rev-parse --verify HEAD)" || return 1
  [[ "$checkout_head" =~ ^[0-9a-f]{40}$ ]] || return 1

  actions_head="${GITHUB_SHA:-$checkout_head}"
  [[ "$actions_head" =~ ^[0-9a-f]{40}$ && "$actions_head" == "$checkout_head" ]] || return 1
  event_name="${GITHUB_EVENT_NAME:-local}"
  case "$event_name" in
    schedule|workflow_dispatch|local)
      queued_head="none"
      event_base="$checkout_head"
      ;;
    merge_group)
      [[ -n "${GITHUB_EVENT_PATH:-}" && -f "$GITHUB_EVENT_PATH" ]] || return 1
      json_has_unique_members "$GITHUB_EVENT_PATH" || return 1
      event_base="$(jq -er '.merge_group.base_sha | select(type == "string" and test("^[0-9a-f]{40}$"))' "$GITHUB_EVENT_PATH")" || return 1
      queued_head="$(jq -er '.merge_group.head_sha | select(type == "string" and test("^[0-9a-f]{40}$"))' "$GITHUB_EVENT_PATH")" || return 1
      [[ "$queued_head" == "$checkout_head" ]] || return 1
      git -C "$repository_root" merge-base --is-ancestor "$event_base" "$queued_head" || return 1
      ;;
    *) return 1 ;;
  esac

  [[ -f "$inventory" && -f "$source_request" && -f "$reviewed_authority" ]] || return 1
  json_has_unique_members "$inventory" || return 1
  json_has_unique_members "$source_request" || return 1
  json_has_unique_members "$reviewed_authority" || return 1
  jq -e '
    keys == ["inventory","repository","request","schema","settings"]
    and .schema == "fsgg.coordination.workflow-selection-reviewed-authority/1"
    and .repository == "FS-GG/FS.GG.Coordination"
    and (.inventory | keys == ["baseRevision","graphVersion","inventoryVersion","path","seal","sha256"])
    and (.request | keys == ["path","sha256"])
    and (.settings | keys == ["desiredSha256","path","receiptSha256"])
    and ([.inventory.sha256,.inventory.seal,.request.sha256,.settings.receiptSha256,.settings.desiredSha256]
         | all(.[]; type == "string" and test("^[0-9a-f]{64}$")))
    and (.inventory.baseRevision | type == "string" and test("^[0-9a-f]{40}$"))
  ' "$reviewed_authority" >/dev/null || return 1
  declared_inventory_path="$(jq -er '.inventory.path' "$reviewed_authority")" || return 1
  declared_request_path="$(jq -er '.request.path' "$reviewed_authority")" || return 1
  declared_settings_path="$(jq -er '.settings.path' "$reviewed_authority")" || return 1
  [[ "$(realpath -e "$inventory")" == "$repository_root/$declared_inventory_path" ]] || return 1
  [[ "$(realpath -e "$source_request")" == "$repository_root/$declared_request_path" ]] || return 1
  git -C "$repository_root" ls-files --error-unmatch -- "$declared_inventory_path" "$declared_request_path" "$declared_settings_path" "$reviewed_authority" >/dev/null || return 1
  inventory_sha="$(sha256_file "$inventory")" || return 1
  source_request_sha="$(sha256_file "$source_request")" || return 1
  [[ "$inventory_sha" == "$(jq -er '.inventory.sha256' "$reviewed_authority")" ]] || return 1
  [[ "$source_request_sha" == "$(jq -er '.request.sha256' "$reviewed_authority")" ]] || return 1
  base_revision="$(jq -er '.baseRevision | select(type == "string" and test("^[0-9a-f]{40}$"))' "$inventory")" || return 1
  request_base="$(jq -er '.baseRevision | select(type == "string" and test("^[0-9a-f]{40}$"))' "$source_request")" || return 1
  request_settings="$(jq -er '.settingsSha256 | select(type == "string" and test("^[0-9a-f]{64}$"))' "$source_request")" || return 1
  [[ "$base_revision" == "$request_base" ]] || return 1
  [[ "$base_revision" == "$(jq -er '.inventory.baseRevision' "$reviewed_authority")" ]] || return 1
  [[ "$(jq -er '.inventoryVersion' "$inventory")" == "$(jq -er '.inventory.inventoryVersion' "$reviewed_authority")" ]] || return 1
  [[ "$(jq -er '.graphVersion' "$inventory")" == "$(jq -er '.inventory.graphVersion' "$reviewed_authority")" ]] || return 1
  [[ "$(jq -er '.seal' "$inventory")" == "$(jq -er '.inventory.seal' "$reviewed_authority")" ]] || return 1
  git -C "$repository_root" merge-base --is-ancestor "$base_revision" "$checkout_head" || return 1

  settings_receipt="$repository_root/$declared_settings_path"
  [[ -f "$settings_receipt" ]] || return 1
  json_has_unique_members "$settings_receipt" || return 1
  settings_receipt_sha="$(sha256_file "$settings_receipt")" || return 1
  [[ "$settings_receipt_sha" == "$(jq -er '.settings.receiptSha256' "$reviewed_authority")" ]] || return 1
  jq -e '
    .schema == "fsgg.coordination.repository-settings-receipt/2"
    and .repository.nameWithOwner == "FS-GG/FS.GG.Coordination"
    and (.desiredSha256 | type == "string" and test("^[0-9a-f]{64}$"))
  ' "$settings_receipt" >/dev/null || return 1
  settings_sha="$(jq -er '.desiredSha256' "$settings_receipt")" || return 1
  [[ "$settings_sha" == "$request_settings" && "$settings_sha" == "$(jq -er '.settings.desiredSha256' "$reviewed_authority")" ]] || return 1

  if [[ "$event_name" == "merge_group" ]]; then
    local changed_paths
    changed_paths="$(git -C "$repository_root" diff --name-only --diff-filter=ACDMRTUXB "$event_base" "$queued_head")" || return 1
    [[ -n "$changed_paths" ]] || return 1
    jq -n -c \
      --arg inventoryVersion "$(jq -er '.inventoryVersion' "$inventory")" \
      --arg graphVersion "$(jq -er '.graphVersion' "$inventory")" \
      --arg seal "$(jq -er '.seal' "$inventory")" \
      --arg base "$base_revision" --arg settings "$settings_sha" \
      --arg queued "$queued_head" --arg eventBase "$event_base" \
      --rawfile changed <(printf '%s\n' "$changed_paths") '
      {inventoryVersion:$inventoryVersion,graphVersion:$graphVersion,expectedInventorySeal:$seal,
       baseRevision:$base,settingsSha256:$settings,complete:true,
       changedPaths:($changed|split("\n")|map(select(length>0))),nonFileInputs:["merge-group-settings"],
       mergeGroup:{queuedHead:$queued,currentQueuedHead:$queued,currentBaseRevision:$eventBase,currentSettingsSha256:$settings,recomputed:true}}' > "$runtime_request" || return 1
  else
    cp "$source_request" "$runtime_request" || return 1
  fi
  json_has_unique_members "$runtime_request" || return 1
  runtime_request_sha="$(sha256_file "$runtime_request")" || return 1

  jq -n -c --arg base "$base_revision" --arg current "$checkout_head" --arg eventBase "$event_base" \
    --arg settings "$settings_sha" --arg queued "$queued_head" --arg inventorySha "$inventory_sha" \
    --arg requestSha "$runtime_request_sha" --arg sourceRequestSha "$source_request_sha" \
    '{schema:"fsgg.coordination.workflow-selection-authority/2",inventoryBaseRevision:$base,currentRevision:$current,
      eventBaseRevision:$eventBase,settingsSha256:$settings,queuedHead:$queued,inventorySha256:$inventorySha,
      requestSha256:$requestSha,sourceRequestSha256:$sourceRequestSha}' > "$output"
}

write_typed_decision() {
  local q7_decision="$1"
  local decision="$2"
  local full_suite="${3:-passed}"
  if ! json_has_unique_members "$q7_decision" \
    || ! jq -e '
      keys == ["fleetSelection","missedObligations","productionMutation","schema","seal"]
      and .schema == "fsgg.coordination.workflow-selection-supply-chain-decision/1"
      and (.seal | type == "string" and test("^[0-9a-f]{64}$"))
      and (.missedObligations | type == "array"
           and all(.[]; type == "string" and IN("build","test","policy","coordination","packaging","release"))
           and length == (unique | length))
      and (.fleetSelection == "eligible" or .fleetSelection == "disabled")
      and .productionMutation == false
      and ((.missedObligations | length == 0) == (.fleetSelection == "eligible"))
    ' "$q7_decision" >/dev/null; then
    printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"invalid-q7-decision"}\n' > "$decision"
    return 1
  fi
  jq -c --arg full_suite "$full_suite" '{schema:"fsgg.coordination.workflow-selection-sentinel/1",fullSuite:$full_suite,missedObligation:(.missedObligations|length>0),missedObligations,fleetSelection,productionMutation:false,q7Seal:.seal}' "$q7_decision" > "$decision"
  test "$(jq -r .fleetSelection "$q7_decision")" = "eligible"
}

compare_current_selection() {
  local selection="$1"
  local failures="$2"
  local q7_decision="$3"
  local decision="$4"
  local typed="$decision.typed"
  if ! json_has_unique_members "$selection" \
    || ! json_has_unique_members "$failures" \
    || ! json_has_unique_members "$q7_decision" \
    || ! jq -e '
      keys == ["aggregates","children","closure","graphVersion","inventorySeal","inventoryVersion","mergeGroupQueuedHead","roots","schema"]
      and .schema == "fsgg.coordination.workflow-selection-decision/1"
      and .inventoryVersion == "coordination-workflows/1"
      and .graphVersion == "fsgg.workflow-impact/1"
      and (.inventorySeal | test("^[0-9a-f]{64}$"))
      and (.closure | type == "array" and all(.[]; IN("build","test","policy","coordination","packaging","release")) and length == (unique|length))
    ' "$selection" >/dev/null \
    || ! jq -e 'type == "array" and all(.[]; type == "string" and IN("build","test","policy","coordination","packaging","release")) and length == (unique|length)' "$failures" >/dev/null \
    || ! jq -e '
      keys == ["fleetSelection","missedObligations","productionMutation","schema","seal"]
      and .schema == "fsgg.coordination.workflow-selection-supply-chain-decision/1"
      and (.seal | type == "string" and test("^[0-9a-f]{64}$"))
      and (.missedObligations | type == "array"
           and all(.[]; type == "string" and IN("build","test","policy","coordination","packaging","release"))
           and length == (unique | length))
      and (.fleetSelection == "eligible" or .fleetSelection == "disabled")
      and .productionMutation == false
      and ((.missedObligations | length == 0) == (.fleetSelection == "eligible"))
    ' "$q7_decision" >/dev/null; then
    printf '{"schema":"fsgg.coordination.workflow-selection-sentinel/1","fullSuite":"failed","missedObligation":true,"fleetSelection":"disabled","productionMutation":false,"reason":"invalid-current-comparison"}\n' > "$decision"
    return 1
  fi
  jq -n -c --slurpfile selection "$selection" --slurpfile failures "$failures" --slurpfile q7 "$q7_decision" '
      ($failures[0] - $selection[0].closure) as $missed
      | {schema:"fsgg.coordination.workflow-selection-supply-chain-decision/1",seal:$q7[0].seal,
         missedObligations:$missed,fleetSelection:(if ($missed|length)==0 then "eligible" else "disabled" end),productionMutation:false}
    ' > "$typed"
  local full_suite
  full_suite="$(jq -r 'if length == 0 then "passed" else "failed" end' "$failures")"
  write_typed_decision "$typed" "$decision" "$full_suite"
}

if [[ "${1:-}" == "--decision-only" && $# == 3 ]]; then
  write_typed_decision "$2" "$3"
  exit $?
fi

if [[ "${1:-}" == "--compare-selection" && $# == 5 ]]; then
  compare_current_selection "$2" "$3" "$4" "$5"
  exit $?
fi

if [[ "${1:-}" == "--resolve-authority" && $# == 6 ]]; then
  resolve_current_authority "$2" "$3" "$4" "$5" "$6"
  exit $?
fi

output_root="${RUNNER_TEMP:-/tmp}/workflow-selection-sentinel"
mkdir -p "$output_root"
decision="$output_root/decision.json"
q7_decision="$output_root/q7-decision.json"
selection="$output_root/current-selection.json"
failures="$output_root/actual-failures.json"
authority="$output_root/current-authority.json"
runtime_request="$output_root/runtime-request.json"

if ! resolve_current_authority \
  evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json \
  evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json \
  evidence/github-substrate-v2/gs2-06-7/runtime-reviewed-authority.json \
  "$authority" "$runtime_request"; then
  write_disabled_decision "$decision" "current-authority-unavailable"
  exit 1
fi

set +e
dotnet build FS.GG.Coordination.sln -c Release --nologo /warnaserror
build_exit=$?
dotnet test FS.GG.Coordination.sln -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=full-suite.trx" --results-directory "$output_root"
test_exit=$?
RUNNER_TEMP="$output_root/policy" bash eng/bootstrap-gates/dependency-and-security.sh
policy_exit=$?
dotnet fsi eng/validate-github-workflow-selection.fsx -- .
q3_exit=$?
dotnet fsi eng/validate-github-workflow-selection-supply-chain.fsx -- . --decision "$q7_decision"
q7_exit=$?
bash eng/bootstrap-gates/package-install-smoke.sh
packaging_exit=$?
dotnet fsi eng/validate-github-release-hardening.fsx -- .
release_exit=$?
dotnet src/FS.GG.Coordination.Cli/bin/Release/net10.0/FS.GG.Coordination.Cli.dll workflow-select \
  --inventory evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json \
  --request "$runtime_request" \
  --expected-inventory-version coordination-workflows/1 --expected-graph-version fsgg.workflow-impact/1 \
  --expected-seal 2ff268103734c9f14d80302575aea4996c1a040a125b7f4356880efde90b5d5a \
  --authority "$authority" > "$selection"
selection_exit=$?
set -e

if (( selection_exit != 0 )); then
  write_disabled_decision "$decision" "current-selection-refused"
  exit 1
fi

jq -n -c \
  --argjson build "$build_exit" --argjson test "$test_exit" --argjson policy "$policy_exit" \
  --argjson q3 "$q3_exit" --argjson q7 "$q7_exit" --argjson packaging "$packaging_exit" --argjson release "$release_exit" '
  [if $build != 0 then "build" else empty end,
   if $test != 0 then "test" else empty end,
   if $policy != 0 then "policy" else empty end,
   if ($q3 != 0 or $q7 != 0) then "coordination" else empty end,
   if $packaging != 0 then "packaging" else empty end,
   if $release != 0 then "release" else empty end]
' > "$failures"

set +e
compare_current_selection "$selection" "$failures" "$q7_decision" "$decision"
comparison_exit=$?
set -e
if (( comparison_exit != 0 )) || jq -e 'length > 0' "$failures" >/dev/null; then exit 1; fi
exit 0
