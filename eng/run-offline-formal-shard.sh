#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 8 ]]; then
  printf 'usage: %s CHECKOUT HEAD_SHA BASE_SHA TOOLCHAIN_ARCHIVE IMAGE_DIGEST PUBLIC_KEY KEY_ID OUTPUT\n' "$0" >&2
  exit 64
fi
: "${FSGG_OFFLINE_SIGNING_KEY_FD:?FSGG_OFFLINE_SIGNING_KEY_FD is required}"
[[ "$FSGG_OFFLINE_SIGNING_KEY_FD" == 3 ]] || {
  printf 'FSGG_OFFLINE_SIGNING_KEY_FD must be 3\n' >&2
  exit 64
}

supervisor_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
checkout="$(realpath "$1")"
head_sha="$2"
base_sha="$3"
toolchain="$(realpath "$4")"
image="$5"
public_key="$(realpath "$6")"
key_id="$7"
output="$(realpath -m "$8")"
policy="$supervisor_root/eng/offline-formal-pilot.json"
shard="$(jq -er '.pilotShard' "$policy")"
configured_key_id="$(jq -er '.signer.keyId' "$policy")"
[[ "$key_id" == "$configured_key_id" ]]
[[ "$(git -C "$checkout" rev-parse HEAD)" == "$head_sha" ]]
git -C "$checkout" merge-base --is-ancestor "$base_sha" "$head_sha"
test -f "$toolchain"
test -f "$public_key"
actual_toolchain_sha="$(sha256sum "$toolchain" | cut -d' ' -f1)"
expected_toolchain_sha="$(jq -er '.toolchainArchiveSha256' "$policy")"
[[ "$actual_toolchain_sha" == "$expected_toolchain_sha" ]] || {
  printf 'offline toolchain archive does not match the policy pin\n' >&2
  exit 1
}
[[ "$(podman info --format '{{.Host.Security.Rootless}}')" == "true" ]] || {
  printf 'rootless Podman is required\n' >&2
  exit 1
}
resolved_image_id="$(podman image inspect --format '{{.Id}}' "$image")"
expected_image_id="sha256:$(jq -er '.imageSha256' "$policy")"
[[ "$resolved_image_id" == "$expected_image_id" ]] || {
  printf 'offline image does not match the policy pin\n' >&2
  exit 1
}

scratch="$(mktemp -d "${TMPDIR:-/tmp}/fsgg-offline-formal-XXXXXX")"
cleanup() { rm -rf -- "$scratch"; }
trap cleanup EXIT
mkdir -p "$scratch/source" "$scratch/output"
"$supervisor_root/eng/prepare-offline-formal-source.sh" "$checkout" "$head_sha" "$scratch/source"

timeout --kill-after=30s 90m podman run --rm \
  --pull=never \
  --network=none \
  --userns=keep-id \
  --read-only \
  --cap-drop=all \
  --security-opt=no-new-privileges \
  --pids-limit=2048 \
  --memory=4g \
  --cpus=4 \
  --tmpfs /tmp:rw,nosuid,nodev,size=2g \
  --tmpfs /home/runner:rw,nosuid,nodev,size=512m \
  --mount "type=bind,src=$scratch/source,dst=/workspace,rw" \
  --mount "type=bind,src=$scratch/output,dst=/evidence,rw" \
  --mount "type=bind,src=$toolchain,dst=/inputs/toolchain.tar.gz,ro" \
  --workdir /workspace \
  --env HOME=/home/runner \
  --env RUNNER_TEMP=/tmp/runner \
  --env FSGG_QUINT_SHARD="$shard" \
  --env FSGG_QUINT_SHARD_ROOT=/evidence \
  --env FSGG_QUINT_TOOLCHAIN_ARCHIVE=/inputs/toolchain.tar.gz \
  "$image" \
  bash -c 'mkdir -p "$RUNNER_TEMP" && exec bash eng/bootstrap-gates/canonical-quint-shard.sh' \
  3<&-

created_at="$(date --utc +%Y-%m-%dT%H:%M:%SZ)"
expires_at="$(date --utc --date="$created_at + $(jq -er '.maximumAgeSeconds' "$policy") seconds" +%Y-%m-%dT%H:%M:%SZ)"
python3 "$supervisor_root/eng/offline-formal-evidence.py" seal \
  --root "$checkout" \
  --policy "$policy" \
  --receipt "$scratch/output/$shard.json" \
  --head-sha "$head_sha" \
  --base-sha "$base_sha" \
  --toolchain-archive "$toolchain" \
  --image-digest "$resolved_image_id" \
  --created-at "$created_at" \
  --expires-at "$expires_at" \
  --public-key "$public_key" \
  --private-key-fd "$FSGG_OFFLINE_SIGNING_KEY_FD" \
  --output "$output"
