#!/usr/bin/env python3
"""Read-only readback of the fdev P4 executor account against provider_runtime's own checks.

Modes:
  preflight  The account must be absent. Runs account_plan against the live passwd, subuid and
             subgid files and writes the plan it returns. This is the only time account_plan can
             pass on the live files, because it refuses an existing name, uid or gid.
  readback   The account must exist exactly as planned. Re-runs account_plan with only this
             account's own rows removed (proving the result collides with nothing else), confirms
             account_plan now refuses the live files, and checks groups, subordinate ranges,
             directory ownership and modes, and the grant directory's trust predicate.

  collisions Re-runs account_plan with only this account's own rows removed; passes only if the
             planned identity and ranges collide with nothing else. Run before writing subid rows.
  required-dirs  Prints provider_runtime.REQUIRED_DIRS, one per line, so the shell never copies it.

Prints one canonical JSON document (except required-dirs). Never prints password or shadow fields. Exits 2 on any failure.
"""
from __future__ import annotations

import grp
import importlib.util
import json
import os
import pwd
import stat
import subprocess
import sys
from pathlib import Path

NAME = "p4executor"
UID = 32001
GID = 32001
START = 2000000
COUNT = 65536
HOME = "/p4/runtime-v1/home"
SHELL = "/usr/bin/nologin"
RECEIVER = "/srv/p4-receiver"
GRANT_PARENTS = ("/etc/fsgg", "/etc/fsgg/portable-workspaces")
GRANT_FILE = "/etc/fsgg/portable-workspaces/python-hello-v1.json"
SCHEMA = "fsgg.fdev-p4executor-provisioning-readback/1"


def load_runtime():
    sys.dont_write_bytecode = True  # never leave root-owned __pycache__ in the checkout
    path = Path(__file__).resolve().parents[2] / "tests/portable-workspace/trusted-provider/provider_runtime.py"
    spec = importlib.util.spec_from_file_location("provider_runtime", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module, str(path)


def text(path):
    return Path(path).read_text()


def without_own_rows(content):
    return "\n".join(line for line in content.splitlines() if line.split(":", 1)[0] != NAME) + "\n"


def run(argv):
    return subprocess.run(argv, check=True, capture_output=True, text=True, timeout=30).stdout


def owned_private(path, uid, gid):
    """The directory predicate capability() applies to every REQUIRED_DIRS entry."""
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return {"path": path, "exists": False, "ok": False}
    mode = format(st.st_mode & 0o777, "04o")
    ok = stat.S_ISDIR(st.st_mode) and st.st_uid == uid and st.st_gid == gid and mode == "0700"
    return {"path": path, "uid": st.st_uid, "gid": st.st_gid, "mode": mode,
            "symlink": stat.S_ISLNK(st.st_mode), "ok": ok}


def extended_acl(path):
    return run(["getfacl", "--absolute-names", "--skip-base", "--omit-header", path]).strip()


def administrator_only(path):
    """Mirror of PortableWorkspaceTrustedFileSecurity.validate for a directory: no link, owner uid 0,
    not group/other writable, and no extended access or default ACL entry that could delegate write."""
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return {"path": path, "exists": False, "ok": False}
    acl = extended_acl(path)
    ok = (stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode) and st.st_uid == 0
          and st.st_mode & 0o022 == 0 and acl == "")
    return {"path": path, "uid": st.st_uid, "gid": st.st_gid, "mode": format(st.st_mode & 0o777, "04o"),
            "extendedAcl": acl != "", "ok": ok}


def preflight(runtime):
    failures = []
    for label, probe in (("name", lambda: pwd.getpwnam(NAME)), ("uid", lambda: pwd.getpwuid(UID)),
                         ("gid", lambda: grp.getgrgid(GID)), ("group", lambda: grp.getgrnam(NAME))):
        try:
            probe()
            failures.append(f"{label} already exists")
        except KeyError:
            pass
    for path in ("/etc/subuid", "/etc/subgid"):
        if any(line.split(":", 1)[0] == NAME for line in text(path).splitlines()):
            failures.append(f"{path} already has a {NAME} row")
    plan = None
    try:
        plan = runtime.account_plan(NAME, UID, GID, START, COUNT,
                                    text("/etc/passwd"), text("/etc/subuid"), text("/etc/subgid"))
    except ValueError as error:
        failures.append(f"account_plan refused: {error}")
    return {"mode": "preflight", "plan": plan, "failures": failures}


def collisions(runtime):
    try:
        plan = runtime.account_plan(NAME, UID, GID, START, COUNT, without_own_rows(text("/etc/passwd")),
                                    without_own_rows(text("/etc/subuid")), without_own_rows(text("/etc/subgid")))
        return {"mode": "collisions", "plan": plan, "failures": []}
    except ValueError as error:
        return {"mode": "collisions", "plan": None, "failures": [f"account_plan refused without own rows: {error}"]}


def readback(runtime):
    failures = []
    result = {"mode": "readback"}
    try:
        entry = pwd.getpwnam(NAME)
        group = grp.getgrnam(NAME)
    except KeyError:
        return {"mode": "readback", "failures": [f"{NAME} account or group is missing"]}
    result["account"] = {"name": entry.pw_name, "uid": entry.pw_uid, "gid": entry.pw_gid,
                         "home": entry.pw_dir, "shell": entry.pw_shell, "groupName": group.gr_name,
                         "groupGid": group.gr_gid}
    if (entry.pw_uid, entry.pw_gid, group.gr_gid, entry.pw_dir, entry.pw_shell) != (UID, GID, GID, HOME, SHELL):
        failures.append("account identity, home or shell differs from the plan")

    # Same measurement collect_provider_capability.py makes, through runuser as the account.
    measured = {"name": NAME,
                "uid": int(run(["runuser", "--user", NAME, "--", "id", "-u"]).strip()),
                "gid": int(run(["runuser", "--user", NAME, "--", "id", "-g"]).strip())}
    groups = run(["runuser", "--user", NAME, "--", "id", "-nG"]).split()
    result["measured"] = {"account": measured, "groups": groups}
    if measured != {"name": NAME, "uid": UID, "gid": GID}:
        failures.append("measured account differs (capability: actual selected account differs)")
    if set(groups) != {NAME}:
        failures.append("selected account has unexpected groups")
    members = sorted(g.gr_name for g in grp.getgrall() if NAME in g.gr_mem)
    result["supplementaryMemberships"] = members
    if members:
        failures.append("account is a listed member of another group")

    # Password state only: the hash field is never read into the output.
    with open("/etc/shadow") as shadow:
        hashes = [line.split(":")[1] for line in shadow if line.split(":", 1)[0] == NAME]
    result["passwordLocked"] = len(hashes) == 1 and hashes[0][:1] in ("!", "*")
    if not result["passwordLocked"]:
        failures.append("account password is not locked")
    del hashes

    subids = {}
    for label, path, flag in (("subuid", "/etc/subuid", []), ("subgid", "/etc/subgid", ["-g"])):
        own = [(start, count) for owner, start, count in runtime.ranges(text(path)) if owner == NAME]
        reported = run(["getsubids", *flag, NAME]).strip()
        subids[label] = {"rows": [{"start": s, "count": c} for s, c in own], "getsubids": reported}
        if own != [(START, COUNT)] or reported != f"0: {NAME} {START} {COUNT}":
            failures.append(f"{label} range differs from {START}:{COUNT}")
    result["subordinateIds"] = subids

    # account_plan on the live files must now refuse (identity exists); with only this account's own
    # rows removed it must pass, so the provisioned ranges and identity collide with nothing else.
    try:
        runtime.account_plan(NAME, UID, GID, START, COUNT, text("/etc/passwd"), text("/etc/subuid"), text("/etc/subgid"))
        live = "passed"
        failures.append("account_plan passed on live files although the account exists")
    except ValueError as error:
        live = f"refused: {error}"
    result["accountPlanLive"] = live
    try:
        result["accountPlanWithoutOwnRows"] = runtime.account_plan(
            NAME, UID, GID, START, COUNT, without_own_rows(text("/etc/passwd")),
            without_own_rows(text("/etc/subuid")), without_own_rows(text("/etc/subgid")))
    except ValueError as error:
        result["accountPlanWithoutOwnRows"] = f"refused: {error}"
        failures.append(f"account_plan refused without own rows: {error}")

    directories = [owned_private(path, UID, GID) for path in runtime.REQUIRED_DIRS]
    result["requiredDirectories"] = directories
    if [d["path"] for d in directories] != list(runtime.REQUIRED_DIRS) or not all(d["ok"] for d in directories):
        failures.append("provider directory inventory or ownership differs")
    receiver = owned_private(RECEIVER, UID, GID)
    if receiver.get("exists", True):
        receiver["empty"] = not any(Path(RECEIVER).iterdir())
    result["receiver"] = receiver
    if not receiver["ok"]:
        failures.append(f"{RECEIVER} ownership or mode differs")

    parents = [administrator_only(path) for path in GRANT_PARENTS]
    result["grantParents"] = parents
    if not all(p["ok"] for p in parents):
        failures.append("grant directory is not administrator-only")
    result["grantInstalled"] = os.path.lexists(GRANT_FILE)
    if result["grantInstalled"]:
        failures.append("a grant file is present; provisioning never installs one")
    result["failures"] = failures
    return result


def main():
    modes = {"preflight": preflight, "collisions": collisions, "readback": readback}
    if len(sys.argv) != 2 or sys.argv[1] not in (*modes, "required-dirs"):
        print("usage: verify_p4executor.py preflight|collisions|readback|required-dirs", file=sys.stderr)
        return 2
    runtime, runtime_path = load_runtime()
    if sys.argv[1] == "required-dirs":
        sys.stdout.write("".join(path + "\n" for path in runtime.REQUIRED_DIRS))
        return 0
    value = modes[sys.argv[1]](runtime)
    value.update({"schema": SCHEMA, "providerRuntime": runtime_path, "ok": not value["failures"]})
    sys.stdout.write(json.dumps(value, sort_keys=True, indent=1) + "\n")
    return 0 if value["ok"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
