#!/usr/bin/env bash
# Guest side of the fdev M1 bake (.github#4650, owning plan "M1 bake layout"). Runs once, as root,
# from cloud-init runcmd inside the offline nested image-build guest. The NoCloud seed copies it from
# the read-only control disk to /run before it runs, so nothing here executes from a disk it unmounts.
#
# It mounts the input disk read-only at the path provision-image.sh requires, installs the build
# selection on tmpfs (root, 0600), runs provision-image.sh --apply from the control disk's source
# subset, removes the selection and unmounts. cloud-init's power_state powers the guest off afterwards
# whatever happened here. The result reaches the host only as one marker line on the serial console;
# a missing marker is a failed bake, never a success.
set -uo pipefail
bake_control=/mnt/control
bake_inputs=/mnt/fsgg-qualification-inputs
bake_selection=/run/fsgg-image-build-selection.json
bake_source=$bake_control/source
bake_status=0

bake_report() {
  local line="FSGG-BAKE-RESULT/1 stage=$1 status=$2"
  printf '%s\n' "$line"
  if [[ -w /dev/ttyS0 ]]; then
    printf '%s\n' "$line" > /dev/ttyS0
  fi
}

# Keep the first failure: a later cleanup failure never replaces an earlier status.
bake_first() {
  if [[ $bake_status == 0 ]]; then
    bake_status=$1
  fi
}

bake_fail() {
  bake_report "$1" "$2"
  rm -f -- "$bake_selection"
  exit "$2"
}

[[ $(id -u) == 0 ]] || bake_fail identity 2
mountpoint -q "$bake_control" || bake_fail control-mount 2
install -d -m 0755 "$bake_inputs"
mount -o ro,nodev,nosuid LABEL=fsgg-inputs "$bake_inputs" || bake_fail inputs-mount 2
# provision-image.sh requires the selection root-owned, mode 0600, as a regular file; tmpfs only.
install -m 0600 -o 0 -g 0 -- "$bake_control/fsgg-image-build-selection.json" "$bake_selection" \
  || bake_fail selection-install 2
cd / || bake_fail cwd 2
bash "$bake_source/deployment/qualification-runner/provision-image.sh" --apply "$bake_control/TARGET.json" \
  "$bake_source/deployment/qualification-runner/noble/acquired-input-lock.json" "$bake_inputs" || bake_first $?
rm -f -- "$bake_selection"
[[ ! -e $bake_selection ]] || bake_first 4
sync
umount "$bake_inputs" || bake_first 5
bake_report provision "$bake_status"
exit "$bake_status"
