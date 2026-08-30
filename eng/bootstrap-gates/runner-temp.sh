#!/usr/bin/env bash

fsgg_resolve_runner_temp() {
  if [[ -n "${RUNNER_TEMP:-}" ]]; then
    mkdir -p "$RUNNER_TEMP"
    export RUNNER_TEMP
    return
  fi

  RUNNER_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/fsgg-bootstrap-runner-XXXXXX")"
  export RUNNER_TEMP
  trap 'rm -rf "$RUNNER_TEMP"' EXIT
}
