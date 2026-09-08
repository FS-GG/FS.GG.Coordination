#!/usr/bin/env bash
set -euo pipefail
root="${1:?partition receipt root required}"
for partition in 0 1 2 3 4 5; do
  test -s "$root/partition-$partition/receipt.json"
  jq -e --argjson partition "$partition" '.schema == "fsgg.coordination.coherent-partition-receipt/1" and .partition == $partition and .passed == true' "$root/partition-$partition/receipt.json" >/dev/null
done
