#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
MANIFEST="$ROOT/.config/dotnet-tools.json"
CONFIG="$ROOT/.fsgg/telemetry-runtime.json"
CI_CONFIG="$ROOT/.fsgg/telemetry-ci-attribution.json"
LAUNCHER="$ROOT/eng/codex-exec.sh"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/fsgg-telemetry-receiver.XXXXXX")"
trap 'rm -rf -- "$SCRATCH"' EXIT

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
            "version": "0.87.0",
            "commands": ["fsgg-coord-engine"],
            "rollForward": False,
        }
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
[[ "$version" == "0.87.0.0" ]] || {
  echo "receiver test: expected installed engine 0.87.0.0, got $version" >&2
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

echo "telemetry runtime receiver: exact pin/config and native-result neutrality passed"
