#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
MANIFEST="$ROOT/.config/dotnet-tools.json"
CONFIG="$ROOT/.fsgg/telemetry-runtime.json"
CI_CONFIG="$ROOT/.fsgg/telemetry-ci-attribution.json"
LAUNCHER="$ROOT/eng/codex-exec.sh"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/fsgg-telemetry-receiver.XXXXXX")"
STORE=""
trap 'rm -rf -- "$SCRATCH"; if [[ -n "$STORE" ]]; then rm -rf -- "$STORE"; fi' EXIT

python3 - "$MANIFEST" "$CONFIG" "$CI_CONFIG" <<'PY'
import json
import pathlib
import sys

manifest = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
tool = manifest["tools"]["fs.gg.coord.cli"]
assert manifest == {
    "version": 1,
    "isRoot": True,
    "tools": {
        "fs.gg.coord.cli": {
            "version": "0.91.4",
            "commands": ["fsgg-coord-engine"],
            "rollForward": False,
        },
        "fantomas": {
            "version": "8.0.0",
            "commands": ["fantomas"],
            "rollForward": False,
        },
    },
}

config = json.loads(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8"))
assert config == {
    "schema": "fsgg.telemetry.runtime-receiver/1",
    "enginePackage": "FS.GG.Coord.Cli",
    "engineVersion": tool["version"],
    "entrypoint": "eng/codex-exec.sh",
    "runtime": "codex-exec",
    "scope": "prospective-repository-owned",
    "storeEnvironment": "FSGG_TELEMETRY_STORE",
    "observerAuthority": "advisory",
    "historicalSessionDiscovery": False,
    "hostedDaemon": False,
    "platformNativeSpawnSupported": False,
}

ci_config = json.loads(pathlib.Path(sys.argv[3]).read_text(encoding="utf-8"))
assert ci_config == {
    "schema": "fsgg.telemetry.ci-attribution/1",
    "rules": [],
}
PY

dotnet tool restore --tool-manifest "$MANIFEST" >/dev/null
version="$(dotnet tool run fsgg-coord-engine -- --version)"
[[ "$version" == "0.91.4.0" ]] || {
  echo "receiver test: expected installed engine 0.91.4.0, got $version" >&2
  exit 1
}

mkdir -p "$SCRATCH/bin"
ln -s "$ROOT/tests/telemetry-runtime-receiver/fake-codex.sh" "$SCRATCH/bin/codex"
assignment="$SCRATCH/assignment.json"
printf '%s\n' '{"schema":"fsgg.telemetry.codex-assignment/1","featureId":"UTEL-06","itemId":"UTEL-06.6","attemptId":"coordination-receiver-test","parentAttemptId":null,"producerStream":"coordination-receiver-test"}' >"$assignment"
chmod 0600 "$assignment"

args_file="$SCRATCH/args.txt"
stderr_file="$SCRATCH/native-success.stderr"
PATH="$SCRATCH/bin:$PATH" FSGG_FAKE_CODEX_ARGS="$args_file" \
  env -u FSGG_TELEMETRY_STORE "$LAUNCHER" --assignment "$assignment" -- \
    --json --ephemeral -m gpt-5.6-sol "receiver task" >/dev/null 2>"$stderr_file"

python3 - "$args_file" <<'PY'
import pathlib
import sys
assert pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines() == [
    "exec", "--json", "--ephemeral", "-m", "gpt-5.6-sol", "receiver task"
]
PY
grep -Fq 'observation publication incomplete; native exit is unchanged' "$stderr_file"

set +e
PATH="$SCRATCH/bin:$PATH" FSGG_FAKE_CODEX_EXIT=23 \
  env -u FSGG_TELEMETRY_STORE "$LAUNCHER" --assignment "$assignment" -- \
    --json --ephemeral "native failure" >/dev/null 2>"$SCRATCH/native-failure.stderr"
native_rc=$?
set -e
[[ "$native_rc" -eq 23 ]] || {
  echo "receiver test: observer changed native exit 23 to $native_rc" >&2
  exit 1
}
grep -Fq 'observation publication incomplete; native exit is unchanged' "$SCRATCH/native-failure.stderr"

# The adapter rejects temporary storage and Git worktrees. Allocate a fresh
# private store under local state, never the host's FSGG_TELEMETRY_STORE.
state_root="${XDG_STATE_HOME:-$HOME/.local/state}"
mkdir -p -- "$state_root"
STORE="$(mktemp -d "$state_root/fsgg-telemetry-receiver.XXXXXX")"
dotnet tool run fsgg-coord-engine -- telemetry store init --store-root "$STORE" \
  >"$SCRATCH/store-init.stdout"
population="$SCRATCH/member-population.json"
python3 - "$population" <<'PY'
import hashlib
import json
import pathlib
import sys

item = "UTEL-06.6"
original = "UTEL-06"
key = hashlib.sha256(f"{item}\x1f{original}".encode()).hexdigest()[:32]
event = {
    "kind": "budget-population",
    "identity": f"budget-population-{key}",
    "itemId": item,
    "revision": 0,
    "originalItemId": original,
    "state": "open",
    "sourceKind": "native-item",
    "sourceRef": f"roadmap-dispatch:{key}",
}
batch = {
    "schema": "fsgg.telemetry.ingest/1",
    "ingestId": "receiver-canonical-member-population",
    "sourceIdentity": "receiver-test",
    "generation": "receiver-test",
    "cursor": "population-1",
    "eventCount": 1,
    "events": [event],
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(batch, separators=(",", ":")) + "\n", encoding="utf-8")
PY
dotnet tool run fsgg-coord-engine -- telemetry store ingest --store-root "$STORE" --input "$population" \
  >"$SCRATCH/member-population.stdout"
PATH="$SCRATCH/bin:$PATH" FSGG_TELEMETRY_STORE="$STORE" \
  "$LAUNCHER" --assignment "$assignment" -- \
    --json --ephemeral -m gpt-5.6-sol "receiver task" \
    >"$SCRATCH/persisted.stdout" 2>"$SCRATCH/persisted.stderr"

if grep -Eq 'observation publication incomplete|reconciliation pending' "$SCRATCH/persisted.stderr"; then
  echo "receiver test: isolated store publication or reconciliation failed" >&2
  exit 1
fi

python3 - "$SCRATCH/persisted.stdout" <<'PY'
import pathlib
import sys

observed = pathlib.Path(sys.argv[1]).read_bytes()
expected = b"\n".join([
    b'{"type":"thread.started","thread_id":"receiver-test-thread"}',
    b'{"type":"turn.completed","usage":{"input_tokens":12,"cached_input_tokens":4,"output_tokens":5,"reasoning_output_tokens":2}}',
]) + b"\n"
assert observed == expected, "receiver test: adapter changed native Codex JSONL bytes"
PY

python3 - "$STORE" <<'PY'
import pathlib
import sqlite3
import sys

database = pathlib.Path(sys.argv[1]) / "telemetry.sqlite3"
assert database.is_file(), "receiver test: isolated telemetry database was not created"
with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
    rows = connection.execute("""
        SELECT item_id, thread_id, requested_model,
               input_count, cached_input, output_count, reasoning, total
        FROM runtime_turn_usage
    """).fetchall()
    terminals = connection.execute("""
        SELECT item_id, outcome, exit_code
        FROM runtime_terminals
    """).fetchall()
    item_outcomes = connection.execute("SELECT count(*) FROM native_item_outcomes").fetchone()[0]
    populations = connection.execute("""
        SELECT item_id, original_item_id, state, source_kind, source_ref
        FROM budget_population_facts
    """).fetchall()
assert rows == [("UTEL-06.6", "receiver-test-thread", "gpt-5.6-sol", 12, 4, 5, 2, 17)], \
    "receiver test: expected exactly one attributed native token-usage observation"
assert terminals == [("UTEL-06.6", "completed", 0)], \
    "receiver test: expected exactly one successful process terminal"
assert item_outcomes == 0, \
    "receiver test: a process terminal must not invent a machine delivery outcome"
assert len(populations) == 1, \
    "receiver test: process observation must preserve one canonical member population"
assert populations[0][:4] == ("UTEL-06.6", "UTEL-06", "open", "native-item"), \
    "receiver test: process completion must leave the canonical member population open"
assert populations[0][4].startswith("roadmap-dispatch:"), \
    "receiver test: canonical member population source must remain authoritative"
PY

echo "telemetry runtime receiver: exact pin/config, native-result neutrality, byte-preserved JSONL, persisted usage and delivery boundary passed"
