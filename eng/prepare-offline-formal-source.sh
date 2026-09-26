#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 3 ]]; then
  printf 'usage: %s CHECKOUT HEAD_SHA DESTINATION\n' "$0" >&2
  exit 64
fi
checkout="$(realpath "$1")"
head_sha="$2"
destination="$3"
[[ "$head_sha" =~ ^[0-9a-f]{40}$ ]]
[[ "$(git -C "$checkout" rev-parse HEAD)" == "$head_sha" ]]
test ! -e "$destination"
mkdir -p "$destination"
git -C "$checkout" archive --format=tar "$head_sha" | tar -xf - -C "$destination"
git -C "$destination" init --quiet
git -C "$destination" config core.autocrlf false
git -C "$destination" add -f -A
expected_tree="$(git -C "$checkout" rev-parse "$head_sha^{tree}")"
actual_tree="$(git -C "$destination" write-tree)"
[[ "$actual_tree" == "$expected_tree" ]] || {
  printf 'OFFLINE_FORMAL_SOURCE_REFUSED reason=exported-tree-mismatch expected=%s actual=%s\n' "$expected_tree" "$actual_tree" >&2
  exit 1
}
imported_head="$(git -C "$checkout" cat-file commit "$head_sha" | git -C "$destination" hash-object -t commit -w --stdin)"
[[ "$imported_head" == "$head_sha" ]]
git -C "$destination" symbolic-ref HEAD refs/heads/offline-candidate
git -C "$destination" update-ref refs/heads/offline-candidate "$head_sha"
printf 'OFFLINE_FORMAL_SOURCE_OK head=%s tree=%s\n' "$head_sha" "$actual_tree"
