#!/usr/bin/env bash
set -euo pipefail
: "${FSGG_TRUSTED_ROOT:?protected base checkout required}"
: "${FSGG_CANDIDATE_SHA:?exact candidate required}"
: "${FSGG_BASE_SHA:?exact protected base required}"
: "${FSGG_OFFLINE_EVIDENCE:?envelope output required}"

trusted_root="$(realpath "$FSGG_TRUSTED_ROOT")"
policy="$trusted_root/eng/offline-formal-pilot.json"
[[ "$(git -C "$trusted_root" rev-parse HEAD)" == "$FSGG_BASE_SHA" ]] || {
  echo "OFFLINE_FORMAL_FETCH_REFUSED reason=protected-base-head" >&2; exit 1;
}
for relative in eng/offline-formal-pilot.json eng/offline-formal-fetch.sh; do
  git -C "$trusted_root" ls-files --error-unmatch -- "$relative" >/dev/null 2>&1 &&
    git -C "$trusted_root" diff --quiet "$FSGG_BASE_SHA" -- "$relative" &&
    test -f "$trusted_root/$relative" && ! test -L "$trusted_root/$relative" || {
      echo "OFFLINE_FORMAL_FETCH_REFUSED reason=protected-file-drift" >&2; exit 1;
    }
done
[[ "$FSGG_CANDIDATE_SHA" =~ ^[0-9a-f]{40}$ && "$FSGG_BASE_SHA" =~ ^[0-9a-f]{40}$ ]] || {
  echo "OFFLINE_FORMAL_FETCH_REFUSED reason=revision-shape" >&2; exit 1;
}
jq -e '.mode == "active" and .signer.publicKeySpkiSha256 != null and
       .transport.kind == "git-ref" and .transport.repository == "FS-GG/.github" and
       .transport.ref == "refs/heads/evidence/coordination-offline-formal-pilot" and
       .pilotShard == "authority-reconciliation"' "$policy" >/dev/null
shard="$(jq -er '.pilotShard' "$policy")"
ref="refs/heads/evidence/coordination-offline-formal-pilot/$FSGG_CANDIDATE_SHA/$FSGG_BASE_SHA/$shard"
output="$(realpath -m "$FSGG_OFFLINE_EVIDENCE")"
[[ ! -e "$output" && ! -L "$output" ]] || {
  echo "OFFLINE_FORMAL_FETCH_REFUSED reason=output-exists" >&2; exit 1;
}
scratch="$(mktemp -d "${RUNNER_TEMP:-/tmp}/offline-formal-fetch.XXXXXX")"
trap 'rm -rf -- "$scratch"' EXIT
git init --bare -q "$scratch/evidence.git"
git -C "$scratch/evidence.git" -c protocol.version=2 fetch -q --no-tags --depth=1 \
  https://github.com/FS-GG/.github.git "$ref"
[[ "$(git -C "$scratch/evidence.git" cat-file -t FETCH_HEAD:envelope.json)" == blob ]] || {
  echo "OFFLINE_FORMAL_FETCH_REFUSED reason=envelope-not-blob" >&2; exit 1;
}
git -C "$scratch/evidence.git" show FETCH_HEAD:envelope.json > "$output"
test -s "$output"
printf 'OFFLINE_FORMAL_FETCH_OK ref=%s\n' "$ref"
