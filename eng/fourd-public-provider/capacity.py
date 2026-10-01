#!/usr/bin/env python3
"""Credential-free public-runner capacity screen for the fixed FourD route."""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import platform
import re
import shutil
import subprocess
from typing import Any

SCHEMA = "fsgg.fourd.public-provider-capacity/1"
MEMORY_FLOOR = 7_516_192_768
MAX_OUTPUT = 8192
MAX_COMMAND_OUTPUT = 65536
EXPECTED_REPOSITORY = "FS-GG/FS.GG.Coordination"
EXPECTED_REF = "refs/heads/qualification/fourd-native-20261001"
FORBIDDEN_CREDENTIALS = (
    "FSGG_FOURD_PUBLIC_PROVIDER_ADMISSION_JSON_B64",
    "FSGG_FOURD_READONLY_DEPLOY_KEY_B64",
)
CLOSED_FAILURES = frozenset({
    "capacity-context-refused", "capacity-credential-refused", "capacity-architecture-refused",
    "capacity-memory-refused", "capacity-cgroup-refused", "capacity-filesystem-refused",
    "capacity-podman-refused", "capacity-cleanup-refused", "qualification-phase-unimplemented",
    "capacity-internal-refused",
})


class Refusal(RuntimeError):
    pass


def refuse(code: str) -> None:
    if code not in CLOSED_FAILURES:
        code = "capacity-internal-refused"
    raise Refusal(code)


def bounded_string(value: Any, maximum: int = 128) -> str | None:
    return value if isinstance(value, str) and 0 < len(value) <= maximum and value.isprintable() else None


def read_meminfo(text: str) -> dict[str, int]:
    values: dict[str, int] = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([A-Za-z_()]+):\s+([0-9]+)\s+kB", line)
        if match:
            values[match.group(1)] = int(match.group(2)) * 1024
    required = ("MemTotal", "MemAvailable", "SwapTotal", "SwapFree")
    if any(key not in values for key in required):
        refuse("capacity-memory-refused")
    if not 0 < values["MemAvailable"] <= values["MemTotal"]:
        refuse("capacity-memory-refused")
    return values


def decode_mount(value: str) -> str:
    return re.sub(r"\\(040|011|012|134)", lambda m: {"040": " ", "011": "\t", "012": "\n", "134": "\\"}[m.group(1)], value)


def cgroup_location(mountinfo: str, membership: str) -> tuple[pathlib.Path, pathlib.Path]:
    rows = []
    for line in mountinfo.splitlines():
        before, marker, after = line.partition(" - ")
        fields, tail = before.split(), after.split()
        if marker and len(fields) >= 5 and tail and tail[0] == "cgroup2":
            rows.append((pathlib.Path(decode_mount(fields[3])), pathlib.Path(decode_mount(fields[4]))))
    member_lines = [line for line in membership.splitlines() if line.startswith("0::/") or line == "0::/"]
    if len(rows) != 1 or len(member_lines) != 1:
        refuse("capacity-cgroup-refused")
    mount_root, mount_point = rows[0]
    member = pathlib.Path(member_lines[0][3:])
    try:
        relative = member.relative_to(mount_root)
    except ValueError:
        refuse("capacity-cgroup-refused")
    leaf = mount_point / relative
    try:
        leaf.relative_to(mount_point)
    except ValueError:
        refuse("capacity-cgroup-refused")
    return mount_point, leaf


def cgroup_accounting(mount_point: pathlib.Path, leaf: pathlib.Path) -> dict[str, Any]:
    ancestors = []
    cursor = leaf
    while True:
        ancestors.append(cursor)
        if cursor == mount_point:
            break
        if len(ancestors) >= 32 or mount_point not in cursor.parents:
            refuse("capacity-cgroup-refused")
        cursor = cursor.parent
    finite_headroom: list[int] = []
    records = []
    for depth, directory in enumerate(ancestors):
        try:
            maximum = (directory / "memory.max").read_text().strip()
            current_text = (directory / "memory.current").read_text().strip()
        except OSError:
            refuse("capacity-cgroup-refused")
        if not re.fullmatch(r"[0-9]+", current_text):
            refuse("capacity-cgroup-refused")
        current = int(current_text)
        if maximum == "max":
            records.append({"depth": depth, "currentBytes": current,
                            "limitBytes": None, "headroomBytes": None})
            continue
        if not re.fullmatch(r"[0-9]+", maximum):
            refuse("capacity-cgroup-refused")
        limit = int(maximum)
        if limit <= 0 or current > limit:
            refuse("capacity-cgroup-refused")
        headroom = limit - current
        finite_headroom.append(headroom)
        records.append({"depth": depth, "currentBytes": current,
                        "limitBytes": limit, "headroomBytes": headroom})
    return {
        "version": 2,
        "ancestorCount": len(ancestors),
        "finiteLimitCount": len(finite_headroom),
        "minimumFiniteHeadroomBytes": min(finite_headroom) if finite_headroom else None,
        "accountingKnown": True,
        "ancestors": records,
    }


def effective_memory(meminfo: dict[str, int], cgroup: dict[str, Any]) -> int:
    available = meminfo["MemAvailable"]
    finite = cgroup["minimumFiniteHeadroomBytes"]
    return min(available, finite) if finite is not None else available


def mount_for(path: pathlib.Path, mountinfo: str) -> dict[str, str]:
    resolved = path.resolve()
    candidates = []
    for line in mountinfo.splitlines():
        before, marker, after = line.partition(" - ")
        fields, tail = before.split(), after.split()
        if not marker or len(fields) < 5 or len(tail) < 2:
            continue
        point = pathlib.Path(decode_mount(fields[4]))
        try:
            resolved.relative_to(point)
        except ValueError:
            continue
        candidates.append((len(point.parts), fields[2], tail[0]))
    if not candidates:
        refuse("capacity-filesystem-refused")
    _, device, filesystem = max(candidates)
    if bounded_string(device, 32) is None or bounded_string(filesystem, 64) is None:
        refuse("capacity-filesystem-refused")
    return {"mountDevice": device, "filesystemType": filesystem}


def filesystem_fact(path: pathlib.Path, mountinfo: str) -> dict[str, Any]:
    try:
        status = path.stat()
        space = os.statvfs(path)
    except OSError:
        refuse("capacity-filesystem-refused")
    free_bytes = space.f_bavail * space.f_frsize
    free_inodes = space.f_favail
    if free_bytes <= 0 or free_inodes <= 0:
        refuse("capacity-filesystem-refused")
    return {
        **mount_for(path, mountinfo),
        "statDevice": status.st_dev,
        "freeBytes": free_bytes,
        "freeInodes": free_inodes,
    }


def command_json(arguments: list[str], environment: dict[str, str]) -> Any:
    try:
        completed = subprocess.run(arguments, env=environment, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   timeout=20, check=False)
    except (OSError, subprocess.TimeoutExpired):
        refuse("capacity-podman-refused")
    if completed.returncode != 0 or len(completed.stdout) > MAX_COMMAND_OUTPUT or len(completed.stderr) > MAX_COMMAND_OUTPUT:
        refuse("capacity-podman-refused")
    try:
        return json.loads(completed.stdout)
    except (UnicodeDecodeError, json.JSONDecodeError):
        refuse("capacity-podman-refused")


def podman_fact(state: pathlib.Path) -> dict[str, Any]:
    root, runroot = state / "vfs", state / "run"
    root.mkdir(mode=0o700)
    runroot.mkdir(mode=0o700)
    environment = {key: value for key, value in os.environ.items()
                   if key not in FORBIDDEN_CREDENTIALS and not key.startswith("GIT_") and key != "SSH_AUTH_SOCK"}
    version = command_json(["podman", "version", "--format", "json"], environment)
    info = command_json(["podman", "--storage-driver=vfs", "--root", str(root),
                         "--runroot", str(runroot), "info", "--format", "json"], environment)
    client = version.get("Client", version) if isinstance(version, dict) else {}
    version_text = bounded_string(client.get("Version") if isinstance(client, dict) else None, 64)
    store = info.get("store", {}) if isinstance(info, dict) else {}
    host = info.get("host", {}) if isinstance(info, dict) else {}
    containers = store.get("containerStore", {}) if isinstance(store, dict) else {}
    images = store.get("imageStore", {}) if isinstance(store, dict) else {}
    if (version_text is None or store.get("graphDriverName") != "vfs"
            or host.get("security", {}).get("rootless") is not True
            or containers.get("number") != 0 or images.get("number") != 0):
        refuse("capacity-podman-refused")
    return {"version": version_text, "rootless": True, "graphDriver": "vfs",
            "containers": 0, "images": 0, "ownedState": True}


def context_fact() -> dict[str, Any]:
    expected = {
        "GITHUB_REPOSITORY": EXPECTED_REPOSITORY,
        "GITHUB_REF": EXPECTED_REF,
        "GITHUB_EVENT_NAME": "workflow_dispatch",
    }
    if any(os.environ.get(key) != value for key, value in expected.items()):
        refuse("capacity-context-refused")
    if any(os.environ.get(key) for key in FORBIDDEN_CREDENTIALS):
        refuse("capacity-credential-refused")
    sha = os.environ.get("GITHUB_SHA", "")
    if re.fullmatch(r"[0-9a-f]{40}", sha) is None:
        refuse("capacity-context-refused")
    try:
        run_id = int(os.environ.get("GITHUB_RUN_ID", "0"))
        run_attempt = int(os.environ.get("GITHUB_RUN_ATTEMPT", "0"))
    except ValueError:
        refuse("capacity-context-refused")
    if run_id <= 0 or run_attempt <= 0:
        refuse("capacity-context-refused")
    return {"repository": EXPECTED_REPOSITORY, "ref": EXPECTED_REF, "event": "workflow_dispatch",
            "placementSha": sha, "runId": run_id, "runAttempt": run_attempt}


def base_result(phase: str) -> dict[str, Any]:
    return {"schema": SCHEMA, "phase": phase, "capacityScreenPassed": False,
            "qualified": False, "failureCode": None, "facts": {}}


def write_result(path: pathlib.Path, result: dict[str, Any]) -> None:
    encoded = (json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if len(encoded) > MAX_OUTPUT:
        result = base_result(result.get("phase", "capacity"))
        result["failureCode"] = "capacity-internal-refused"
        encoded = (json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n").encode()
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(encoded)


def screen(output: pathlib.Path, state: pathlib.Path) -> bool:
    result = base_result("capacity")
    owned = False
    try:
        result["facts"]["context"] = context_fact()
        architecture = platform.machine()
        if architecture != "x86_64":
            refuse("capacity-architecture-refused")
        result["facts"]["architecture"] = architecture
        meminfo = read_meminfo(pathlib.Path("/proc/meminfo").read_text())
        mountinfo = pathlib.Path("/proc/self/mountinfo").read_text()
        mount_point, leaf = cgroup_location(mountinfo, pathlib.Path("/proc/self/cgroup").read_text())
        cgroup = cgroup_accounting(mount_point, leaf)
        headroom = effective_memory(meminfo, cgroup)
        result["facts"]["memory"] = {
            "memTotalBytes": meminfo["MemTotal"], "memAvailableBytes": meminfo["MemAvailable"],
            "swapTotalBytes": meminfo["SwapTotal"], "swapFreeBytes": meminfo["SwapFree"],
            "swapCreditedBytes": 0, "requiredHeadroomBytes": MEMORY_FLOOR,
            "effectiveHeadroomBytes": headroom, "cgroup": cgroup,
        }
        if headroom < MEMORY_FLOOR:
            refuse("capacity-memory-refused")
        if state.exists():
            refuse("capacity-cleanup-refused")
        state.mkdir(mode=0o700, parents=True)
        owned = True
        runner_temp = pathlib.Path(os.environ.get("RUNNER_TEMP", ""))
        if not runner_temp.is_absolute() or not runner_temp.is_dir():
            refuse("capacity-filesystem-refused")
        result["facts"]["filesystems"] = {
            "persistentRoot": filesystem_fact(pathlib.Path("/"), mountinfo),
            "runnerTemp": filesystem_fact(runner_temp, mountinfo),
            "ownedState": filesystem_fact(state, mountinfo),
        }
        result["facts"]["podman"] = podman_fact(state)
        result["facts"]["runner"] = {key: bounded_string(os.environ.get(key), 128)
                                            for key in ("RUNNER_OS", "RUNNER_ARCH", "RUNNER_ENVIRONMENT", "ImageOS", "ImageVersion")}
        result["capacityScreenPassed"] = True
    except Refusal as error:
        result["failureCode"] = str(error) if str(error) in CLOSED_FAILURES else "capacity-internal-refused"
    except Exception:
        result["failureCode"] = "capacity-internal-refused"
    finally:
        cleanup = True
        if owned:
            try:
                shutil.rmtree(state)
            except OSError:
                cleanup = False
            cleanup = cleanup and not state.exists()
        result["facts"]["cleanup"] = {"ownedStateCreated": owned,
                                        "ownedStateRemoved": cleanup if owned else False}
        if not cleanup:
            result["capacityScreenPassed"] = False
            result["failureCode"] = "capacity-cleanup-refused"
        write_result(output, result)
    return bool(result["capacityScreenPassed"])


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    capacity = sub.add_parser("screen")
    capacity.add_argument("--output", type=pathlib.Path, required=True)
    capacity.add_argument("--state", type=pathlib.Path, required=True)
    unimplemented = sub.add_parser("qualification-unimplemented")
    unimplemented.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if args.command == "screen":
        passed = screen(args.output, args.state)
        print("capacity-screen-passed" if passed else "capacity-screen-refused")
        return 0
    result = base_result("qualification")
    result["failureCode"] = "qualification-phase-unimplemented"
    write_result(args.output, result)
    print("qualification-phase-unimplemented")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
