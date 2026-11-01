#!/usr/bin/env bash
# Provision the P4 executor account on fdev: FS-GG/.github#5078, replaced by fdev provisioning under
# FS-GG/.github#4730 decision 7. Plan: docs/roadmaps/v2-lang-portable-integration.md, P4 amendment.
#
#   sudo deployment/p4-executor/provision-fdev.sh [--evidence DIR]   provision (idempotent), then read back
#   sudo deployment/p4-executor/provision-fdev.sh --check            read back only; changes nothing
#
# Creates only: the p4executor group and account (uid/gid 32001, locked, nologin, own group only), the
# subuid/subgid range 2000000:65536, provider_runtime.REQUIRED_DIRS and /srv/p4-receiver (32001:32001,
# 0700), and /etc/fsgg/portable-workspaces (root:root 0755). It never writes the receiver-bound grant
# /etc/fsgg/portable-workspaces/python-hello-v1.json: installing it is a separately admitted effect.
# Partial or conflicting state is refused, never repaired. Re-run after any fdev root-disk rebuild.
set -euo pipefail
umask 022

readonly NAME=p4executor ID=32001 START=2000000 COUNT=65536
readonly HOME_DIR=/p4/runtime-v1/home LOGIN_SHELL=/usr/bin/nologin RECEIVER=/srv/p4-receiver
readonly GRANT_ROOT=/etc/fsgg GRANT_DIR=/etc/fsgg/portable-workspaces
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
readonly verify=(python3 -I "$here/verify_p4executor.py")

refuse() { echo "provision-fdev.sh: REFUSED: $*" >&2; exit 2; }
note() { echo "provision-fdev.sh: $*" >&2; }

mode=provision evidence=
while (($#)); do
  case "$1" in
    --check) mode=check; shift ;;
    --evidence) [[ $# -ge 2 && -d $2 ]] || refuse "--evidence needs an existing directory"; evidence=$2; shift 2 ;;
    *) refuse "unknown argument: $1" ;;
  esac
done
[[ $EUID -eq 0 ]] || refuse "run as root (sudo)"

record() { # record NAME: copy stdin to stdout and, with --evidence, to DIR/NAME owned by the invoking user
  if [[ -n $evidence ]]; then
    tee "$evidence/$1"
    [[ -z ${SUDO_UID:-} ]] || chown "$SUDO_UID:${SUDO_GID:-$SUDO_UID}" "$evidence/$1"
  else cat; fi
}

private_dir() { # private_dir PATH: an account-owned 0700 directory; never follows or replaces a link
  local path=$1
  if [[ -L $path ]] || { [[ -e $path ]] && [[ ! -d $path ]]; }; then refuse "$path exists and is not a plain directory"; fi
  [[ -d $path ]] || mkdir -- "$path"
  chown -h "$ID:$ID" -- "$path"
  chmod 0700 -- "$path"
}

no_extended_acl() {
  [[ -z $(getfacl --absolute-names --skip-base --omit-header -- "$1") ]] || refuse "$1 carries an extended ACL"
}

subids() { # subids FILE FLAG: add the planned range once; an existing row must be exactly the plan
  local file=$1 flag=$2 rows
  rows=$(awk -F: -v n="$NAME" '$1 == n' "$file")
  if [[ -z $rows ]]; then
    usermod "$flag" "$START-$((START + COUNT - 1))" "$NAME"
  elif [[ $rows != "$NAME:$START:$COUNT" ]]; then
    refuse "$file already has a different $NAME row"
  fi
}

if [[ $mode == provision ]]; then
  if getent passwd "$NAME" >/dev/null; then
    note "account $NAME exists; checking it instead of creating it"
  else
    if getent group "$NAME" >/dev/null || getent group "$ID" >/dev/null || getent passwd "$ID" >/dev/null; then
      refuse "partial or conflicting identity (group $NAME, gid $ID or uid $ID exists without account $NAME)"
    fi
    "${verify[@]}" preflight | record preflight.json >/dev/null || refuse "account_plan preflight failed"
    groupadd --gid "$ID" "$NAME"
    # -K SUB_*_COUNT=0 stops useradd allocating its own subordinate range; the planned one is added below.
    useradd --uid "$ID" --gid "$ID" --no-create-home --home-dir "$HOME_DIR" --shell "$LOGIN_SHELL" \
      --comment "FS-GG P4 executor" -K SUB_UID_COUNT=0 -K SUB_GID_COUNT=0 "$NAME"
    note "created account $NAME"
  fi

  "${verify[@]}" collisions | record collisions.json >/dev/null || refuse "planned identity or range collides"
  subids /etc/subuid --add-subuids
  subids /etc/subgid --add-subgids

  mapfile -t required < <("${verify[@]}" required-dirs)
  [[ ${required[0]:-} == /p4 ]] || refuse "unexpected REQUIRED_DIRS from provider_runtime"
  for path in "${required[@]}" "$RECEIVER"; do private_dir "$path"; done

  if [[ ! -e $GRANT_ROOT && ! -L $GRANT_ROOT ]]; then
    install -d -o root -g root -m 0755 "$GRANT_ROOT"
  fi
  [[ -d $GRANT_ROOT && ! -L $GRANT_ROOT ]] || refuse "$GRANT_ROOT is not a plain directory"
  no_extended_acl "$GRANT_ROOT"
  if [[ -L $GRANT_DIR ]] || { [[ -e $GRANT_DIR ]] && [[ ! -d $GRANT_DIR ]]; }; then refuse "$GRANT_DIR is not a plain directory"; fi
  [[ -d $GRANT_DIR ]] || mkdir -- "$GRANT_DIR"
  chown -h root:root -- "$GRANT_DIR"
  chmod 0755 -- "$GRANT_DIR"
  no_extended_acl "$GRANT_DIR"
fi

"${verify[@]}" readback | record readback.json
