#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
scratch_parent="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
scratch_root="$(mktemp -d "$scratch_parent/fsgg-choreo-c2-bounded.XXXXXX")"
trap 'rm -rf -- "$scratch_root"' EXIT

quint_sha="939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
quint_bin="${FSGG_QUINT_BIN:-}"

if [[ -n "${FSGG_QUINT_TOOLCHAIN_ARCHIVE:-}" ]]; then
  test -f "$FSGG_QUINT_TOOLCHAIN_ARCHIVE"
  toolchain_root="$scratch_root/toolchain"
  mkdir -p "$toolchain_root"
  tar -xzf "$FSGG_QUINT_TOOLCHAIN_ARCHIVE" -C "$toolchain_root"
  quint_bin="$toolchain_root/cache/objects/$quint_sha"
  export FSGG_QUINT_HOME="$toolchain_root/home/.quint"
  export JAVA_HOME="$toolchain_root/runtime/jdk-21.0.9+10-jre"
  export PATH="$JAVA_HOME/bin:$PATH"
fi

if [[ -z "$quint_bin" ]]; then
  quint_bin="$(command -v quint)"
fi

test -x "$quint_bin"
command -v java >/dev/null
printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status

model="$scratch_root/choreo-c2-bounded.qnt"
test_model="$scratch_root/choreo-c2-tests.qnt"
awk '
  /^\/\/ BEGIN PINNED quint-co\/choreo spells\/basicSpells\.qnt$/ { selected = 1 }
  selected && /^module O2HostedWriterChoreoTests \{$/ { exit }
  selected { print }
' "$repo_root/src/FS.GG.Coordination.Protocol/Protocol.md" > "$model"
awk '
  /^\/\/ BEGIN PINNED quint-co\/choreo spells\/basicSpells\.qnt$/ { selected = 1 }
  selected && /^module ChoreoSourcePinSmoke \{$/ { exit }
  selected { print }
' "$repo_root/src/FS.GG.Coordination.Protocol/Protocol.md" > "$test_model"
test -s "$model" && test -s "$test_model"

cd "$scratch_root"
"$quint_bin" typecheck "$model"
"$quint_bin" test "$test_model" \
  --main=O2HostedWriterChoreoTests \
  --max-samples=10000 \
  --backend=rust \
  --verbosity=1
"$quint_bin" run "$test_model" \
  --main=O2HostedWriterChoreoModel \
  --init=init \
  --step=step \
  --invariant=safety \
  --max-samples=200 \
  --max-steps=30 \
  --seed=0xC2F0 \
  --backend=rust \
  --verbosity=1

verify_lane() {
  local main="$1"
  local log="$scratch_root/$main.log"
  timeout 150s "$quint_bin" verify "$model" \
    --main="$main" \
    --init=init \
    --step=step \
    --invariant=safety \
    --max-steps=20 \
    --backend=tlc \
    --verbosity=3 2>&1 | tee "$log"
  grep -Fq '[ok] No violation found' "$log"
  grep -Eq '[0-9]+ states generated, [0-9]+ distinct states found, 0 states left on queue\.' "$log"
}

verify_lane O2HostedWriterChoreoProviderBounded
verify_lane O2HostedWriterChoreoRunnerBounded

printf 'CHOREO_C2_BOUNDED_OK quintSha256=%s maxSteps=20 lanes=provider,runner\n' "$quint_sha"
