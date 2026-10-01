#!/usr/bin/env python3
"""Credential-free, read-only public-runner capacity screen for FourD."""
from __future__ import annotations

import argparse
import errno
import json
import os
import pathlib
import platform
import re
import shutil
import signal
from typing import Any

SCHEMA = "fsgg.fourd.public-provider-capacity/1"
MEMORY_FLOOR = 7_516_192_768
MAX_OUTPUT = 8192
HELPER_TIMEOUT_SECONDS = 60
EXPECTED_REPOSITORY = "FS-GG/FS.GG.Coordination"
EXPECTED_REF = "refs/heads/qualification/fourd-native-20261001"
FORBIDDEN_CREDENTIALS = (
    "FSGG_FOURD_PUBLIC_PROVIDER_ADMISSION_JSON_B64",
    "FSGG_FOURD_READONLY_DEPLOY_KEY_B64",
)
CLOSED_FAILURES = frozenset({
    "capacity-context-refused", "capacity-credential-refused", "capacity-architecture-refused",
    "capacity-memory-refused", "capacity-cgroup-refused", "capacity-filesystem-refused",
    "capacity-cancelled", "capacity-timeout-refused", "qualification-phase-unimplemented",
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
    return re.sub(r"\\(040|011|012|134)",
                  lambda match: {"040": " ", "011": "\t", "012": "\n", "134": "\\"}[match.group(1)],
                  value)


def absolute_clean_path(value: str) -> pathlib.PurePosixPath:
    if not value.startswith("/") or "\x00" in value:
        refuse("capacity-cgroup-refused")
    parts = value.split("/")[1:]
    if value != "/" and any(part in ("", ".", "..") for part in parts):
        refuse("capacity-cgroup-refused")
    return pathlib.PurePosixPath(value)


def cgroup_location(mountinfo: str, membership: str) -> tuple[pathlib.Path, pathlib.Path, bool]:
    rows: list[tuple[pathlib.PurePosixPath, pathlib.PurePosixPath]] = []
    for line in mountinfo.splitlines():
        before, marker, after = line.partition(" - ")
        fields, tail = before.split(), after.split()
        if marker and len(fields) >= 5 and tail and tail[0] == "cgroup2":
            root = absolute_clean_path(decode_mount(fields[3]))
            point = absolute_clean_path(decode_mount(fields[4]))
            rows.append((root, point))
    member_lines = [line for line in membership.splitlines() if line.startswith("0::")]
    if len(rows) != 1 or len(member_lines) != 1:
        refuse("capacity-cgroup-refused")
    mount_root, mount_point = rows[0]
    member = absolute_clean_path(member_lines[0][3:])
    try:
        relative = member.relative_to(mount_root)
    except ValueError:
        refuse("capacity-cgroup-refused")
    leaf = mount_point.joinpath(*relative.parts)
    try:
        leaf.relative_to(mount_point)
    except ValueError:
        refuse("capacity-cgroup-refused")
    return pathlib.Path(mount_point), pathlib.Path(leaf), mount_root == pathlib.PurePosixPath("/")


def controller_value(path: pathlib.Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8").strip()
    except FileNotFoundError:
        return None
    except OSError:
        refuse("capacity-cgroup-refused")


def limit_value(value: str, current: int, *, hard: bool) -> tuple[int | None, int | None]:
    if value == "max":
        return None, None
    if re.fullmatch(r"[0-9]+", value) is None:
        refuse("capacity-cgroup-refused")
    limit = int(value)
    if limit <= 0 or (hard and current > limit):
        refuse("capacity-cgroup-refused")
    return limit, max(0, limit - current)


def cgroup_accounting(mount_point: pathlib.Path, leaf: pathlib.Path,
                      hierarchy_root: bool) -> dict[str, Any]:
    ancestors: list[pathlib.Path] = []
    cursor = leaf
    while True:
        ancestors.append(cursor)
        if cursor == mount_point:
            break
        if len(ancestors) >= 32 or mount_point not in cursor.parents:
            refuse("capacity-cgroup-refused")
        cursor = cursor.parent

    finite_headroom: list[int] = []
    records: list[dict[str, Any]] = []
    for depth, directory in enumerate(ancestors):
        values = {name: controller_value(directory / name)
                  for name in ("memory.current", "memory.max", "memory.high")}
        if all(value is None for value in values.values()):
            if directory == mount_point and hierarchy_root:
                records.append({"depth": depth, "hierarchyRoot": True,
                                "currentBytes": None, "maxBytes": None,
                                "highBytes": None, "headroomBytes": None})
                continue
            refuse("capacity-cgroup-refused")
        if any(value is None for value in values.values()):
            refuse("capacity-cgroup-refused")
        current_text = values["memory.current"]
        maximum_text = values["memory.max"]
        high_text = values["memory.high"]
        assert current_text is not None and maximum_text is not None and high_text is not None
        if re.fullmatch(r"[0-9]+", current_text) is None:
            refuse("capacity-cgroup-refused")
        current = int(current_text)
        maximum, maximum_headroom = limit_value(maximum_text, current, hard=True)
        high, high_headroom = limit_value(high_text, current, hard=False)
        headrooms = [item for item in (maximum_headroom, high_headroom) if item is not None]
        finite_headroom.extend(headrooms)
        records.append({
            "depth": depth,
            "hierarchyRoot": directory == mount_point and hierarchy_root,
            "currentBytes": current,
            "maxBytes": maximum,
            "highBytes": high,
            "headroomBytes": min(headrooms) if headrooms else None,
        })
    return {
        "version": 2,
        "hierarchyRootVisible": hierarchy_root,
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


def podman_static_fact() -> dict[str, Any]:
    return {
        "binaryAvailable": shutil.which("podman") is not None,
        "version": None,
        "rootlessCapability": "unmeasured",
        "probeExecuted": False,
    }


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


def encoded_result(result: dict[str, Any]) -> bytes:
    encoded = (json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if len(encoded) <= MAX_OUTPUT:
        return encoded
    fallback = base_result(result.get("phase", "capacity"))
    fallback["failureCode"] = "capacity-internal-refused"
    return (json.dumps(fallback, sort_keys=True, separators=(",", ":")) + "\n").encode()


def write_result(path: pathlib.Path, result: dict[str, Any]) -> None:
    if not path.is_absolute() or not path.parent.is_dir() or path.parent.is_symlink():
        raise OSError(errno.EINVAL, "result parent must be an existing absolute directory")
    temporary = path.with_name(f".{path.name}.tmp-{os.getpid()}")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(descriptor, "wb", closefd=False) as stream:
            stream.write(encoded_result(result))
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, path, follow_symlinks=False)
    finally:
        os.close(descriptor)
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def screen(output: pathlib.Path) -> bool:
    result = base_result("capacity")
    cancellation = {"requested": False, "code": None, "settling": False}
    watched = (signal.SIGINT, signal.SIGTERM, signal.SIGALRM)
    previous = {item: signal.getsignal(item) for item in watched}

    def cancel(signum: int, _frame: Any) -> None:
        cancellation["requested"] = True
        cancellation["code"] = ("capacity-timeout-refused"
                                if signum == signal.SIGALRM else "capacity-cancelled")
        if cancellation["settling"]:
            return
        refuse(str(cancellation["code"]))

    try:
        for item in watched:
            signal.signal(item, cancel)
        signal.alarm(HELPER_TIMEOUT_SECONDS)
        result["facts"]["context"] = context_fact()
        architecture = platform.machine()
        if architecture != "x86_64":
            refuse("capacity-architecture-refused")
        result["facts"]["architecture"] = architecture
        meminfo = read_meminfo(pathlib.Path("/proc/meminfo").read_text(encoding="utf-8"))
        mountinfo = pathlib.Path("/proc/self/mountinfo").read_text(encoding="utf-8")
        mount_point, leaf, hierarchy_root = cgroup_location(
            mountinfo, pathlib.Path("/proc/self/cgroup").read_text(encoding="utf-8"))
        cgroup = cgroup_accounting(mount_point, leaf, hierarchy_root)
        headroom = effective_memory(meminfo, cgroup)
        result["facts"]["memory"] = {
            "memTotalBytes": meminfo["MemTotal"], "memAvailableBytes": meminfo["MemAvailable"],
            "swapTotalBytes": meminfo["SwapTotal"], "swapFreeBytes": meminfo["SwapFree"],
            "swapCreditedBytes": 0, "requiredHeadroomBytes": MEMORY_FLOOR,
            "effectiveHeadroomBytes": headroom, "cgroup": cgroup,
        }
        if headroom < MEMORY_FLOOR:
            refuse("capacity-memory-refused")
        runner_temp = pathlib.Path(os.environ.get("RUNNER_TEMP", ""))
        if not runner_temp.is_absolute() or not runner_temp.is_dir():
            refuse("capacity-filesystem-refused")
        result["facts"]["filesystems"] = {
            "persistentRoot": filesystem_fact(pathlib.Path("/"), mountinfo),
            "runnerTemp": filesystem_fact(runner_temp, mountinfo),
        }
        result["facts"]["podman"] = podman_static_fact()
        result["facts"]["runner"] = {
            key: bounded_string(os.environ.get(key), 128)
            for key in ("RUNNER_OS", "RUNNER_ARCH", "RUNNER_ENVIRONMENT", "ImageOS", "ImageVersion")
        }
        result["capacityScreenPassed"] = True
    except Refusal as error:
        result["failureCode"] = str(error) if str(error) in CLOSED_FAILURES else "capacity-internal-refused"
    except Exception:
        result["failureCode"] = "capacity-internal-refused"
    finally:
        cancellation["settling"] = True
        signal.alarm(0)
        blocked = signal.pthread_sigmask(signal.SIG_BLOCK, watched)
        try:
            if cancellation["requested"]:
                result["capacityScreenPassed"] = False
                result["failureCode"] = cancellation["code"]
            write_result(output, result)
        finally:
            for item, handler in previous.items():
                signal.signal(item, handler)
            signal.pthread_sigmask(signal.SIG_SETMASK, blocked)
    return bool(result["capacityScreenPassed"])


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    capacity = sub.add_parser("screen")
    capacity.add_argument("--output", type=pathlib.Path, required=True)
    unimplemented = sub.add_parser("qualification-unimplemented")
    unimplemented.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if args.command == "screen":
        passed = screen(args.output)
        print("capacity-screen-passed" if passed else "capacity-screen-refused")
        return 0
    result = base_result("qualification")
    result["failureCode"] = "qualification-phase-unimplemented"
    write_result(args.output, result)
    print("qualification-phase-unimplemented")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
