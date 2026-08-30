#!/usr/bin/env bash
set -euo pipefail
source eng/bootstrap-gates/runner-temp.sh
fsgg_resolve_runner_temp

version="1.7.12"
archive_sha256="8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"
tool_root="$RUNNER_TEMP/actionlint-$version"
archive="$tool_root/actionlint.tar.gz"
binary="$tool_root/actionlint"

mkdir -p "$tool_root"
if [ ! -x "$binary" ]; then
  curl --proto '=https' --tlsv1.2 --fail --location --silent --show-error \
    "https://github.com/rhysd/actionlint/releases/download/v$version/actionlint_${version}_linux_amd64.tar.gz" \
    --output "$archive"
  printf '%s  %s\n' "$archive_sha256" "$archive" | sha256sum --check --status
  tar --extract --gzip --file "$archive" --directory "$tool_root" actionlint
fi

fixture_root="$(mktemp -d "$RUNNER_TEMP/actionlint-negative-XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT
cat > "$fixture_root/invalid.yml" <<'YAML'
on: workflow_dispatch
jobs:
  invalid:
    runs-on: ubuntu-latest
    env:
      OUTPUT: ${{ runner.temp }}/invalid
    steps:
      - run: 'true'
YAML

if "$binary" "$fixture_root/invalid.yml" > "$fixture_root/output.txt" 2>&1; then
  echo "WORKFLOW_STATIC_MUTATION_SURVIVED fixture=job-env-runner-context" >&2
  exit 1
fi
grep --fixed-strings 'context "runner" is not allowed here' "$fixture_root/output.txt" >/dev/null

"$binary"
printf 'WORKFLOW_STATIC_OK actionlint=%s mutation=job-env-runner-context\n' "$version"
