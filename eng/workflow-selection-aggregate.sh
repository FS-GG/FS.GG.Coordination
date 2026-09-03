#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 13 || "$1" != "success" ]]; then
  echo "selector did not complete successfully" >&2
  exit 1
fi
shift

while (( $# > 0 )); do
  selection="$1"
  result="$2"
  shift 2
  case "$selection:$result" in
    selected:success|not-applicable:skipped) ;;
    *) echo "selection/result mismatch: $selection/$result" >&2; exit 1 ;;
  esac
done

printf 'outcome=passed\nsupply-chain=passed\n'
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  printf 'outcome=passed\nsupply-chain=passed\n' >> "$GITHUB_OUTPUT"
fi
