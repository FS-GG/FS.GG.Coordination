#!/usr/bin/env bash
set -u

if [[ -n "${FSGG_FAKE_CODEX_ARGS:-}" ]]; then
  printf '%s\n' "$@" >"$FSGG_FAKE_CODEX_ARGS"
fi

printf '%s\n' '{"type":"thread.started","thread_id":"receiver-test-thread"}'
exit "${FSGG_FAKE_CODEX_EXIT:-0}"
