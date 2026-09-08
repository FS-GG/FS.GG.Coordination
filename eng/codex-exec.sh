#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CURRENT_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"

if [[ ! -f "$ROOT/.config/dotnet-tools.json" ]]; then
  echo "coordination codex-exec: exact tool manifest is missing" >&2
  exit 2
fi

if [[ "$CURRENT_ROOT" != "$ROOT" ]]; then
  echo "coordination codex-exec: launch from this repository so dotnet resolves its exact local tool manifest" >&2
  exit 2
fi

# The published adapter owns prospective activation, dispatch and invocation
# observations. It deliberately preserves the native Codex exit status when
# observation publication or reconciliation fails.
exec dotnet tool run fsgg-coord-engine -- telemetry runtime codex-exec "$@"
