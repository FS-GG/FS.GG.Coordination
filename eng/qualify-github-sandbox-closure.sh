#!/usr/bin/env bash
set -euo pipefail

root="${1:-.}"
mode="${FSGG_SANDBOX_MODE:-synthetic}"
candidate="${FSGG_CANDIDATE_SHA:-$(git -C "$root" rev-parse HEAD)}"
run_nonce="${FSGG_SANDBOX_RUN_NONCE:-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
evidence_root="${FSGG_SANDBOX_EVIDENCE_DIR:-$(mktemp -d)}"
mkdir -p "$evidence_root/children"

if [[ ! "$candidate" =~ ^[0-9a-f]{40,64}$ ]]; then
  echo "GSQ-CANDIDATE: exact candidate SHA is malformed" >&2
  exit 1
fi

if [[ "$mode" == "live" ]]; then
  required=(FSGG_SANDBOX_TOKEN FSGG_SANDBOX_ACTOR FSGG_SANDBOX_ACTOR_ID FSGG_SANDBOX_OWNER FSGG_SANDBOX_REPOSITORY FSGG_SANDBOX_REPOSITORY_NODE_ID FSGG_SANDBOX_PROJECT_NODE_ID FSGG_SANDBOX_PURPOSE)
  for name in "${required[@]}"; do
    if [[ -z "${!name:-}" ]]; then
      echo "GSQ-LIVE-AUTHORITY: $name is required before any write" >&2
      exit 1
    fi
  done
  if [[ "$FSGG_SANDBOX_ACTOR" == "EHotwagner" || "$FSGG_SANDBOX_PURPOSE" != fsgg-sandbox-* ]]; then
    echo "GSQ-LIVE-AUTHORITY: production-capable or unmarked authority refused before any write" >&2
    exit 1
  fi
  phase="${FSGG_SANDBOX_PHASE:-execute}"
  if [[ "$phase" == cleanup ]]; then
    exec bash "$root/eng/execute-github-sandbox-live.sh" cleanup
  fi
  [[ "$phase" == execute ]] || { echo "GSQ-LIVE-PHASE: expected execute or cleanup" >&2; exit 1; }
elif [[ "$mode" != "synthetic" ]]; then
  echo "GSQ-MODE: expected synthetic or live" >&2
  exit 1
fi

commands=(
  validate-github-transport.fsx
  validate-github-issue-field.fsx
  validate-github-native-relation.fsx
  validate-github-project-adapter.fsx
  validate-github-comment-projection.fsx
  validate-github-sharded-journal.fsx
  validate-github-repository-settings.fsx
  validate-github-actions-release-feed.fsx
)

for script in "${commands[@]}"; do
  gate="${script#validate-}"
  gate="${gate%.fsx}-contract"
  output="$evidence_root/children/$gate.txt"
  FSGG_SANDBOX_RUN_NONCE="$run_nonce" FSGG_CANDIDATE_SHA="$candidate" dotnet fsi "$root/eng/$script" -- "$root" > "$output" &
  child_pid=$!
  wait "$child_pid"
  printf '%s %s %s\n' "$gate" "$child_pid" "$(sha256sum "$output" | cut -d' ' -f1)" >> "$evidence_root/children.tsv"
done

FSGG_SANDBOX_RUN_NONCE="$run_nonce" FSGG_CANDIDATE_SHA="$candidate" dotnet fsi "$root/eng/validate-github-sandbox-closure.fsx" -- "$root" > "$evidence_root/q4.txt"

if [[ "$mode" == live ]]; then
  bash "$root/eng/execute-github-sandbox-live.sh" execute
fi

python3 - "$candidate" "$run_nonce" "$evidence_root" "$mode" <<'PY'
import hashlib, json, pathlib, sys
candidate, nonce, root, mode = sys.argv[1:]
root = pathlib.Path(root)
children = []
for line in (root / "children.tsv").read_text().splitlines():
    gate, process_id, digest = line.split()
    children.append({"gateId": gate, "processId": int(process_id), "resultDigest": digest})
payload = {
    "schema": "fsgg.coordination.github-sandbox-comprehensive/1",
    "candidateSha": candidate,
    "runNonce": nonce,
    "mode": mode,
    "coldStart": True,
    "children": children,
    "q4Digest": hashlib.sha256((root / "q4.txt").read_bytes()).hexdigest(),
}
(root / "comprehensive.json").write_text(json.dumps(payload, separators=(",", ":"), sort_keys=True) + "\n")
PY

echo "github-sandbox-comprehensive OK mode=$mode candidate=$candidate nonce=$run_nonce evidence=$evidence_root/comprehensive.json"
