#!/usr/bin/env bash
set -u

if [[ -n "${FSGG_FAKE_CODEX_ARGS:-}" ]]; then
  printf '%s\n' "$@" >"$FSGG_FAKE_CODEX_ARGS"
fi

printf '%s\n' '{"type":"thread.started","thread_id":"receiver-test-thread"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":12,"cached_input_tokens":4,"output_tokens":5,"reasoning_output_tokens":2}}'
exit "${FSGG_FAKE_CODEX_EXIT:-0}"
