#!/usr/bin/env bash
# Provision the P4 executor account on fdev: FS-GG/.github#5078, replaced by fdev provisioning under
# FS-GG/.github#4730 decision 7. Plan: docs/roadmaps/v2-lang-portable-integration.md, P4 amendment.
#
#   sudo deployment/p4-executor/provision-fdev.sh [--evidence DIR]   provision a fresh host, then read back
#   sudo deployment/p4-executor/provision-fdev.sh --check            read back only; changes nothing
#
# Creates only: the p4executor group and account (uid/gid 32001, locked, nologin, own group only), the
# subuid/subgid range 2000000:65536, provider_runtime.REQUIRED_DIRS and /srv/p4-receiver (32001:32001,
# 0700), and /etc/fsgg/portable-workspaces (root:root 0755). It never writes the receiver-bound grant
# /etc/fsgg/portable-workspaces/python-hello-v1.json: installing it is a separately admitted effect.
#
# Refuses rather than repairs. Every check runs before the first change. Exactly two states pass:
#   fresh        no account, group, uid, gid or subordinate row, account_plan passes, and no account-owned
#                path exists. Everything is created.
#   provisioned  everything already exactly as planned. Nothing is changed (a no-op).
# Any other state (partial, drifted, foreign-owned or non-empty receiver) is refused without a change.
# Re-run after any fdev root-disk rebuild.
set -euo pipefail
umask 022

readonly NAME=p4executor ID=32001 START=2000000 COUNT=65536
readonly HOME_DIR=/p4/runtime-v1/home LOGIN_SHELL=/usr/bin/nologin RECEIVER=/srv/p4-receiver
readonly GRANT_ROOT=/etc/fsgg GRANT_DIR=/etc/fsgg/portable-workspaces
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
# -B: root never writes bytecode into the checkout.
readonly verify=(python3 -I -B "$here/verify_p4executor.py")

refuse() { echo "provision-fdev.sh: REFUSED (nothing changed by this check): $*" >&2; exit 2; }
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

present() { [[ -e $1 || -L $1 ]]; }
exact_dir() { # exact_dir PATH UID:GID MODE: a plain directory (not a link) with exactly this owner and mode
  [[ -d $1 && ! -L $1 ]] && [[ $(stat -c '%u:%g %a' -- "$1") == "$2 $3" ]]
}
no_extended_acl() { [[ -z $(getfacl --absolute-names --skip-base --omit-header -- "$1") ]]; }
subid_rows() { awk -F: -v n="$NAME" '$1 == n' "$1"; }

if [[ $mode == provision ]]; then
  mapfile -t required < <("${verify[@]}" required-dirs)
  [[ ${required[0]:-} == /p4 ]] || refuse "unexpected REQUIRED_DIRS from provider_runtime"
  owned=("${required[@]}" "$RECEIVER")

  # --- Classify. Read-only until the classification and every path check have passed.
  if getent passwd "$NAME" >/dev/null; then
    state=provisioned
    IFS=: read -r _ _ uid gid _ home shell < <(getent passwd "$NAME")
    [[ $uid == "$ID" && $gid == "$ID" && $home == "$HOME_DIR" && $shell == "$LOGIN_SHELL" ]] ||
      refuse "existing $NAME has uid=$uid gid=$gid home=$home shell=$shell, not $ID/$ID/$HOME_DIR/$LOGIN_SHELL"
    [[ $(getent passwd "$ID" | cut -d: -f1) == "$NAME" ]] || refuse "uid $ID is not uniquely $NAME"
    [[ $(getent group "$NAME" | cut -d: -f3) == "$ID" && $(getent group "$ID" | cut -d: -f1) == "$NAME" ]] ||
      refuse "group $NAME is not gid $ID"
    [[ $(id -nG "$NAME") == "$NAME" ]] || refuse "$NAME belongs to groups other than its own"
    [[ $(passwd -S "$NAME" | cut -d' ' -f2) == L ]] || refuse "$NAME password is not locked"
    for file in /etc/subuid /etc/subgid; do
      [[ $(subid_rows "$file") == "$NAME:$START:$COUNT" ]] || refuse "$file row for $NAME is not exactly $START:$COUNT"
    done
    "${verify[@]}" collisions | record collisions.json >/dev/null || refuse "planned identity or range collides"
    for path in "${owned[@]}"; do
      exact_dir "$path" "$ID:$ID" 700 || refuse "$path is not an existing $ID:$ID 0700 directory"
    done
  else
    state=fresh
    if getent group "$NAME" >/dev/null || getent group "$ID" >/dev/null || getent passwd "$ID" >/dev/null; then
      refuse "partial or conflicting identity (group $NAME, gid $ID or uid $ID exists without account $NAME)"
    fi
    for file in /etc/subuid /etc/subgid; do
      [[ -z $(subid_rows "$file") ]] || refuse "$file already has a $NAME row without the account"
    done
    "${verify[@]}" preflight | record preflight.json >/dev/null || refuse "account_plan preflight failed"
    for path in "${owned[@]}"; do
      ! present "$path" || refuse "$path already exists before the account; refusing to adopt it"
    done
  fi

  if present "$RECEIVER" && [[ -n $(ls -A -- "$RECEIVER") ]]; then refuse "$RECEIVER is not empty"; fi
  if present "$GRANT_ROOT"; then
    if ! { exact_dir "$GRANT_ROOT" 0:0 755 && no_extended_acl "$GRANT_ROOT"; }; then
      refuse "$GRANT_ROOT is not a root:root 0755 directory without an extended ACL"
    fi
  fi
  if present "$GRANT_DIR"; then
    if ! { exact_dir "$GRANT_DIR" 0:0 755 && no_extended_acl "$GRANT_DIR"; }; then
      refuse "$GRANT_DIR is not a root:root 0755 directory without an extended ACL"
    fi
  elif [[ $state == provisioned ]]; then
    refuse "$GRANT_DIR is missing although $NAME exists"
  fi

  # --- Mutate: only a fresh host reaches here with work to do; only newly created paths are chowned.
  if [[ $state == fresh ]]; then
    groupadd --gid "$ID" "$NAME"
    # -K SUB_*_COUNT=0 stops useradd allocating its own subordinate range; the planned one is added next.
    useradd --uid "$ID" --gid "$ID" --no-create-home --home-dir "$HOME_DIR" --shell "$LOGIN_SHELL" \
      --comment "FS-GG P4 executor" -K SUB_UID_COUNT=0 -K SUB_GID_COUNT=0 "$NAME"
    usermod --add-subuids "$START-$((START + COUNT - 1))" --add-subgids "$START-$((START + COUNT - 1))" "$NAME"
    for path in "${owned[@]}"; do
      mkdir -- "$path"
      chown -h "$ID:$ID" -- "$path"
      chmod 0700 -- "$path"
    done
    present "$GRANT_ROOT" || install -d -o root -g root -m 0755 "$GRANT_ROOT"
    present "$GRANT_DIR" || install -d -o root -g root -m 0755 "$GRANT_DIR"
    note "created account $NAME and its paths"
  else
    note "account $NAME is already provisioned exactly as planned; nothing changed"
  fi
fi

"${verify[@]}" readback | record readback.json
