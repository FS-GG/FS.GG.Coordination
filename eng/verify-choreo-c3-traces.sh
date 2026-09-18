#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo_root/tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo/manifest.json"
scratch_parent="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
scratch_root="$(mktemp -d "$scratch_parent/fsgg-choreo-c3-traces.XXXXXX")"
trap 'rm -rf -- "$scratch_root"' EXIT

scenario=""
write=false
while (($#)); do
  case "$1" in
    --scenario) scenario="$2"; shift 2 ;;
    --write) write=true; shift ;;
    *) echo "CHOREO_C3_TRACE_REFUSED unsupported argument: $1" >&2; exit 2 ;;
  esac
done

quint_sha="939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
quint_bin="${FSGG_QUINT_BIN:-}"
if [[ -z "$quint_bin" ]]; then quint_bin="$(command -v quint)"; fi
test -x "$quint_bin"
printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status

test "$(jq -r '.schema' "$manifest")" = "fsgg.quint.choreo-trace-manifest/1"
test "$(jq -r '.source.sha256' "$manifest")" = "$(sha256sum "$repo_root/src/FS.GG.Coordination.Protocol/Protocol.md" | cut -d' ' -f1)"
test "$(jq -r '.quint.binarySha256' "$manifest")" = "$quint_sha"
test "$(jq -r '.choreo.commit' "$manifest")" = "$(jq -r '.commit' "$repo_root/eng/choreo-source-pin.json")"

model="$scratch_root/choreo.qnt"
awk '
  /^\/\/ BEGIN PINNED quint-co\/choreo spells\/basicSpells\.qnt$/ { selected = 1 }
  selected && /^module ChoreoSourcePinSmoke \{$/ { exit }
  selected { print }
' "$repo_root/src/FS.GG.Coordination.Protocol/Protocol.md" > "$model"
test -s "$model"

filter='.'
if [[ -n "$scenario" ]]; then filter="select(.id == \"$scenario\")"; fi
rows="$(jq -r ".scenarios[] | $filter | [.id,.quintTest,.file,(.stateCount|tostring),.traceSha256] | @tsv" "$manifest")"
test -n "$rows"

while IFS=$'\t' read -r id quint_test file state_count expected_sha; do
  raw="$scratch_root/$id.raw.json"
  normalized="$scratch_root/$id.itf.json"
  (
    cd "$scratch_root"
    "$quint_bin" test "$model" \
      --main=O2HostedWriterChoreoTests \
      --match="$quint_test" \
      --max-samples=1 \
      --backend=rust \
      --out-itf="$raw" \
      --verbosity=1 >/dev/null
  )
  jq -S -c '
    del(."#meta".description, ."#meta".timestamp)
    | ."#meta".source = "src/FS.GG.Coordination.Protocol/Protocol.md#O2HostedWriterChoreoTests"
  ' "$raw" > "$normalized"
  test "$(jq '.states | length' "$normalized")" = "$state_count"
  actual_sha="$(sha256sum "$normalized" | cut -d' ' -f1)"
  test "$actual_sha" = "$expected_sha"
  destination="$(dirname "$manifest")/$file"
  if $write; then
    cp "$normalized" "$destination"
  else
    cmp --silent "$normalized" "$destination"
  fi
  printf 'CHOREO_C3_TRACE_OK scenario=%s states=%s traceSha256=%s\n' "$id" "$state_count" "$actual_sha"
done <<< "$rows"
