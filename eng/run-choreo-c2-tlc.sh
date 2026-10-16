#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -lt 5 || "$4" != -- ]]; then
  echo "usage: run-choreo-c2-tlc.sh <seconds> <log> <endpoint-base> -- <command> [args...]" >&2
  exit 64
fi

budget_seconds="$1"
result_log="$2"
endpoint_base="$3"
shift 4
adapter="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/retain-choreo-c2-diagnostics.py"

[[ "$budget_seconds" =~ ^[1-9][0-9]*$ ]]
[[ "$endpoint_base" =~ ^[1-9][0-9]*$ ]]
(( endpoint_base >= 1024 && endpoint_base <= 65534 ))

run_attempt() {
  local attempt="$1"
  shift
  local attempt_log="$result_log.attempt-$attempt"
  local endpoint=$((endpoint_base + attempt - 1))
  local status

  if python3 -I -S -B "$adapter" command "$budget_seconds" "$attempt_log" "$endpoint" -- "$@"; then
    status=0
  else
    status="$?"
  fi
  [[ ! -f "$attempt_log" ]] || cp -- "$attempt_log" "$result_log"
  return "$status"
}

if run_attempt 1 "$@"; then
  first_status=0
else
  first_status="$?"
fi

retry_class=""
if grep -Fq 'PASS #0: SanyParser' "$result_log" \
  && ! grep -Fq 'states generated' "$result_log" \
  && ! grep -Fq 'Invariant violated' "$result_log"; then
  if [[ "$first_status" -eq 124 ]]; then
    retry_class="execution-timeout-after-parser"
  elif [[ "$first_status" -eq 0 ]] \
    && grep -Fq 'Started Apalache server on pid=' "$result_log" \
    && grep -Fq 'Shutting down Apalache server' "$result_log"; then
    retry_class="early-lifecycle-exit"
  fi
fi

if [[ -z "$retry_class" && "$first_status" -eq 124 ]]; then
  # Only the original first attempt can consume the one shared retry slot.
  # Structured custody, cleanup and fresh pin checks precede any second launch.
  retry_class="$(python3 -I -S -B "$adapter" startup-retry "$budget_seconds" "$result_log.attempt-1" "$endpoint_base" -- "$@")" || retry_class=""
fi

if [[ -z "$retry_class" ]]; then
  exit "$first_status"
fi

printf 'APALACHE_STARTUP_RETRY count=1 command=verify class=%s\n' "$retry_class" >&2
run_attempt 2 "$@"
