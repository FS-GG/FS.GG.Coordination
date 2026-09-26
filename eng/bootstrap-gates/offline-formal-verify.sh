#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_TRUSTED_ROOT:?FSGG_TRUSTED_ROOT is required}"
: "${FSGG_CANDIDATE_ROOT:?FSGG_CANDIDATE_ROOT is required}"
: "${FSGG_OFFLINE_EVIDENCE:?FSGG_OFFLINE_EVIDENCE is required}"
: "${FSGG_OFFLINE_PUBLIC_KEY:?FSGG_OFFLINE_PUBLIC_KEY is required}"
: "${FSGG_OFFLINE_TOOLCHAIN_ARCHIVE:?FSGG_OFFLINE_TOOLCHAIN_ARCHIVE is required}"
: "${FSGG_CANDIDATE_SHA:?FSGG_CANDIDATE_SHA is required}"
: "${FSGG_BASE_SHA:?FSGG_BASE_SHA is required}"

trusted_root="$(realpath "$FSGG_TRUSTED_ROOT")"
candidate_root="$(realpath "$FSGG_CANDIDATE_ROOT")"
public_key="$(realpath "$FSGG_OFFLINE_PUBLIC_KEY")"
verifier="$trusted_root/eng/offline-formal-evidence.py"
policy="$trusted_root/eng/offline-formal-pilot.json"

refuse() { printf 'OFFLINE_FORMAL_VERIFY_REFUSED reason=%s\n' "$1" >&2; exit 1; }
[[ "$(git -C "$trusted_root" rev-parse HEAD 2>/dev/null)" == "$FSGG_BASE_SHA" ]] || refuse protected-base-head
for relative in eng/offline-formal-evidence.py eng/offline-formal-pilot.json eng/bootstrap-gates/offline-formal-verify.sh; do
  test -f "$trusted_root/$relative" && ! test -L "$trusted_root/$relative" || refuse protected-file-shape
  git -C "$trusted_root" diff --quiet "$FSGG_BASE_SHA" -- "$relative" || refuse protected-base-drift
done
case "$public_key" in
  "$trusted_root"/*) ;;
  *) refuse public-key-outside-protected-root ;;
esac
test -f "$public_key" && ! test -L "$public_key" || refuse public-key-shape
verification_time="$(date --utc +%Y-%m-%dT%H:%M:%SZ)"
receipt_args=()
if [[ -n "${FSGG_OFFLINE_RECEIPT_OUTPUT:-}" ]]; then
  receipt_args=(--receipt-output "$FSGG_OFFLINE_RECEIPT_OUTPUT")
fi

python3 "$verifier" verify \
  --root "$candidate_root" \
  --policy "$policy" \
  --evidence "$FSGG_OFFLINE_EVIDENCE" \
  --expected-head "$FSGG_CANDIDATE_SHA" \
  --expected-base "$FSGG_BASE_SHA" \
  --toolchain-archive "$FSGG_OFFLINE_TOOLCHAIN_ARCHIVE" \
  --public-key "$public_key" \
  --now "$verification_time" \
  "${receipt_args[@]}"
