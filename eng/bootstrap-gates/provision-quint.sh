#!/usr/bin/env bash
set -euo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/runner-temp.sh"
fsgg_resolve_runner_temp
quint_sha="939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f" evaluator_sha="b2efdeac5713d153e41bf2143b94ed75d888fdd5637f4a5d61a04c695313510a"
quint_root="$RUNNER_TEMP/fsgg-quint-0.32.0" quint_bin="$RUNNER_TEMP/fsgg-quint-0.32.0/quint" quint_home="$RUNNER_TEMP/fsgg-quint-0.32.0/home/.quint"
if [[ ! -x "$quint_bin" ]]; then
  mkdir -p "$quint_root"
  if [[ -n "${FSGG_QUINT_TOOLCHAIN_ARCHIVE:-}" ]]; then
    test -f "$FSGG_QUINT_TOOLCHAIN_ARCHIVE"
    tar -xOf "$FSGG_QUINT_TOOLCHAIN_ARCHIVE" "cache/objects/$quint_sha" > "$quint_bin"
  else
    curl --fail --location --retry 5 --retry-all-errors --silent --show-error "https://github.com/quint-co/quint/releases/download/v0.32.0/quint-linux-amd64" --output "$quint_bin"
  fi
  printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status
  chmod +x "$quint_bin"
fi
printf '%s  %s\n' "$quint_sha" "$quint_bin" | sha256sum --check --status
evaluator="$quint_home/rust-evaluator-v0.6.0/quint_evaluator"; mkdir -p "$(dirname "$evaluator")"; if [[ ! -x "$evaluator" ]]; then
  if [[ -n "${FSGG_QUINT_TOOLCHAIN_ARCHIVE:-}" ]]; then tar -xOf "$FSGG_QUINT_TOOLCHAIN_ARCHIVE" home/.quint/rust-evaluator-v0.6.0/quint_evaluator > "$evaluator"
  else curl --fail --location --retry 5 --retry-all-errors --silent --show-error "https://github.com/quint-co/quint/releases/download/evaluator/v0.6.0/quint_evaluator-x86_64-unknown-linux-gnu.tar.gz" | tar -xzOf - quint_evaluator > "$evaluator"
  fi
  chmod +x "$evaluator"; fi
printf '%s  %s\n' "$evaluator_sha" "$evaluator" | sha256sum --check --status
export FSGG_QUINT_HOME="$quint_home" QUINT_HOME="$quint_home" PATH="$quint_root:$PATH"
