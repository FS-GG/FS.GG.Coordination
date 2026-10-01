#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
: "${RUNNER_TEMP:?absolute runner temp is required}"
[[ "$RUNNER_TEMP" = /* ]] || { echo "native collector CI requires an absolute runner temp" >&2; exit 2; }
readonly native_source_revision="a1310e14a60d1d025dd3fa9f404970890503d092"
readonly native_source_sha256="8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba"
readonly native_root="$RUNNER_TEMP/native-collector-v3"
readonly native_module="$native_root/learn_01_native_source.py"
readonly native_fixture="$native_root/fixture"
bash "$ROOT/tests/telemetry-runtime-receiver/run.sh"
test ! -e "$native_root"
mkdir -m 0700 "$native_root"
trap 'chmod -R u+w "$native_root" 2>/dev/null || true; rm -rf -- "$native_root"' EXIT
curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 \
  --connect-timeout 10 --max-time 60 --retry 0 \
  "https://raw.githubusercontent.com/FS-GG/.github/$native_source_revision/tools/learn_01_native_source.py" \
  --output "$native_module"
printf '%s  %s\n' "$native_source_sha256" "$native_module" | sha256sum --check --strict
chmod 0400 "$native_module"
bash "$ROOT/tests/telemetry-native-collector-installation/run-v3.sh" "$native_module" "$native_fixture"
PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/tests/telemetry-native-collector-installation/run.py"
