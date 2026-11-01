#!/usr/bin/env bash
# Guest side of the fdev M1 bake (.github#4650, owning plan "M1 bake layout"). Runs once, as root,
# from cloud-init runcmd inside the offline nested image-build guest. The NoCloud seed copies it from
# the read-only control disk to /run before it runs, so nothing here executes from a disk it unmounts.
#
# It checks the apt units are masked and the DMI UUID equals TARGET's, mounts the input disk read-only
# at the path provision-image.sh requires, installs the build selection on tmpfs (root, 0600), runs
# provision-image.sh --apply from the control disk's source subset, removes the selection and unmounts.
# cloud-init's power_state powers the guest off afterwards whatever happened here.
#
# The result reaches the host only as one serial-console line carrying the operation's nonce (the
# request digest, TARGET's operatorAcceptanceSha256), the stage of the first failure (or "complete")
# and its status. The collector trusts it only together with host-side evidence; a missing, foreign
# or conflicting line is a failed bake, never a success.
set -uo pipefail
bake_control=/mnt/control
bake_inputs=/mnt/fsgg-qualification-inputs
bake_selection=/run/fsgg-image-build-selection.json
bake_uuid_file=/sys/class/dmi/id/product_uuid
bake_source=$bake_control/source
bake_status=0
bake_stage=complete
bake_nonce=unknown
# The masks set by the seed's bootcmd persist into the qualified image: runner guests never run apt timers.
bake_apt_units=(apt-daily.timer apt-daily-upgrade.timer apt-daily.service apt-daily-upgrade.service
  unattended-upgrades.service)

bake_report() {
  local line="FSGG-BAKE-RESULT/1 nonce=$bake_nonce stage=$1 status=$2"
  printf '%s\n' "$line"
  if [[ -w /dev/ttyS0 ]]; then
    printf '%s\n' "$line" > /dev/ttyS0
  fi
}

# Keep the first failure and its stage: a later cleanup failure never replaces an earlier one.
bake_first() {
  if [[ $bake_status == 0 ]]; then
    bake_stage=$1
    bake_status=$2
  fi
}

bake_fail() {
  bake_report "$1" "$2"
  rm -f -- "$bake_selection"
  exit "$2"
}

bake_target_field() {
  python3 -I -B -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' \
    "$bake_control/TARGET.json" "$1"
}

[[ $(id -u) == 0 ]] || bake_fail identity 2
mountpoint -q "$bake_control" || bake_fail control-mount 2
bake_nonce=$(bake_target_field operatorAcceptanceSha256) || bake_fail target 2
[[ $bake_nonce =~ ^[0-9a-f]{64}$ ]] || { bake_nonce=unknown; bake_fail target 2; }
for bake_unit in "${bake_apt_units[@]}"; do
  [[ $(systemctl is-enabled "$bake_unit" 2>/dev/null) == masked ]] || bake_fail apt-mask 2
done
# provision-image.sh compares the raw DMI UUID with TARGET byte for byte; name a casing mismatch apart.
bake_uuid=$(cat -- "$bake_uuid_file") || bake_fail uuid 2
bake_target_uuid=$(bake_target_field systemUuid) || bake_fail target 2
if [[ $bake_uuid != "$bake_target_uuid" ]]; then
  if [[ ${bake_uuid,,} == "${bake_target_uuid,,}" ]]; then
    bake_fail uuid-case 2
  fi
  bake_fail uuid 2
fi
install -d -m 0755 "$bake_inputs"
mount -o ro,nodev,nosuid LABEL=fsgg-inputs "$bake_inputs" || bake_fail inputs-mount 2
# provision-image.sh requires the selection root-owned, mode 0600, as a regular file; tmpfs only.
install -m 0600 -o 0 -g 0 -- "$bake_control/fsgg-image-build-selection.json" "$bake_selection" \
  || bake_fail selection-install 2
cd / || bake_fail cwd 2
bash "$bake_source/deployment/qualification-runner/provision-image.sh" --apply "$bake_control/TARGET.json" \
  "$bake_source/deployment/qualification-runner/noble/acquired-input-lock.json" "$bake_inputs" \
  || bake_first provision $?
rm -f -- "$bake_selection"
[[ ! -e $bake_selection ]] || bake_first selection-remove 4
sync
umount "$bake_inputs" || bake_first inputs-umount 5
bake_report "$bake_stage" "$bake_status"
exit "$bake_status"
