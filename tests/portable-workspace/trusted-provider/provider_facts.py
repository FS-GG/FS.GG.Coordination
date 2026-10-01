#!/usr/bin/env python3
"""Collect and recheck concrete Python provider facts without authorizing them."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid


SCHEMA = "fsgg.portable-workspace-python-provider-facts/1"
IMAGE = "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2@sha256:a994814516fa02d8ac537eed0bdade80db979ac22a415b9f55e73f931c2a7e0e"
ROLE_AGGREGATE_BYTES = {
    "receiver": 64 * 1024 * 1024,
    "runtime": 128 * 1024 * 1024,
}
MAX_ENTRIES = 4096
MAX_FILE_BYTES = 16 * 1024 * 1024


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def contract_bytes(value: object) -> bytes:
    """Match PortableWorkspaceContract's Utf8JsonWriter byte encoding."""
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tree_digest(root: Path, role: str) -> tuple[str, list[dict[str, object]]]:
    if role not in ROLE_AGGREGATE_BYTES:
        raise ValueError("payload role is not closed")
    aggregate_bound = ROLE_AGGREGATE_BYTES[role]
    files: list[dict[str, object]] = []
    total = 0
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        if ".git" in path.relative_to(root).parts:
            continue
        if path.is_symlink():
            raise ValueError("payload contains a link")
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError("payload contains a special file")
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        total += size
        if len(files) >= MAX_ENTRIES or size > MAX_FILE_BYTES or total > aggregate_bound:
            raise ValueError(f"payload exceeds {role} bounds")
        files.append({"path": relative, "sha256": sha256(path), "size": size})
    lines = "".join(f"{item['sha256']} {item['size']} {item['path']}\n" for item in files)
    return hashlib.sha256(lines.encode()).hexdigest(), files


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(
        ["/usr/bin/git", "-C", str(root), *args],
        text=True,
        env={"HOME": "/nonexistent", "PATH": "/usr/bin:/bin", "GIT_CONFIG_NOSYSTEM": "1"},
        timeout=10,
    ).strip()


def profile(commit: str) -> dict[str, object]:
    return {
        "schema": "fsgg.workspace.toolchain-profile/1",
        "profileId": "portable-python-hello-v1",
        "revision": "1",
        "workspaceScope": "fs-gg/p4-python-receiver",
        "sourceRevision": commit,
        "qualifiedImage": IMAGE,
        "components": [{
            "id": "python",
            "language": "python",
            "workingDirectory": "python",
            "toolchain": {"id": "cpython", "version": "3.14.0"},
            "entryPoints": {
                "build": "python-build",
                "test": "python-test",
            },
        }],
        "product": {
            "build": "unsupported-product-build",
            "test": "unsupported-product-test",
            "journey": "unsupported-product-journey",
        },
        "limits": {
            "maximumRuntimeSeconds": "60",
            "maximumOutputBytes": "262144",
        },
    }


def collect(args: argparse.Namespace) -> None:
    join = json.loads(args.join.read_text())
    candidate = json.loads(args.candidate.read_text())
    receiver = args.receiver.resolve()
    if git(receiver, "status", "--porcelain"):
        raise ValueError("receiver is not clean")
    commit, tree = git(receiver, "rev-parse", "HEAD"), git(receiver, "rev-parse", "HEAD^{tree}")
    tracked = git(receiver, "ls-files", "-z").split("\0")
    tracked = [value for value in tracked if value]
    payload_digest, payload = tree_digest(receiver, "receiver")
    if [item["path"] for item in payload] != tracked:
        raise ValueError("receiver inventory differs from tracked paths")
    runtime_digest, runtime_files = tree_digest(args.runtime.resolve(), "runtime")
    profile_value = profile(commit)
    profile_bytes = contract_bytes(profile_value)
    args.profile.write_bytes(profile_bytes)
    facts = {
        "schema": SCHEMA,
        "authorizationState": "required-external",
        "nativeExecutionAuthorized": False,
        "joinSha256": sha256(args.join),
        "candidateReceiptSha256": sha256(args.candidate),
        "receiverArchiveSha256": sha256(args.receiver_archive),
        "profileSha256": hashlib.sha256(profile_bytes).hexdigest(),
        "producerSourceRevision": candidate["producer"]["sourceRevision"],
        "package": candidate["package"],
        "installedCli": candidate["installedCli"],
        "receiver": {
            "commit": commit,
            "tree": tree,
            "payloadSha256": payload_digest,
            "projectedPayload": [{"path": item["path"], "sha256": item["sha256"]} for item in payload],
        },
        "runtime": {
            "version": join["runtime"]["version"],
            "payloadSha256": runtime_digest,
            "files": runtime_files,
        },
        "image": candidate["image"],
        "provider": {
            "sddVersion": join["sdd"]["version"],
            "sddPackageSha256": join["sdd"]["sha256"],
            "templateVersion": join["templates"]["version"],
            "templatePackageSha256": join["templates"]["sha256"],
        },
    }
    args.output.write_bytes(canonical(facts))


def verify(args: argparse.Namespace) -> None:
    facts = json.loads(args.facts.read_text())
    candidate = json.loads(args.candidate.read_text())
    if facts["schema"] != SCHEMA or facts["authorizationState"] != "required-external":
        raise ValueError("provider facts schema or authorization state changed")
    if facts["nativeExecutionAuthorized"] is not False:
        raise ValueError("candidate facts falsely authorize execution")
    if (facts["candidateReceiptSha256"] != sha256(args.candidate)
            or facts["package"] != candidate["package"]
            or facts["installedCli"] != candidate["installedCli"]):
        raise ValueError("installed candidate differs from provider facts")
    receiver = args.receiver.resolve()
    payload_digest, payload = tree_digest(receiver, "receiver")
    if git(receiver, "status", "--porcelain") or git(receiver, "rev-parse", "HEAD") != facts["receiver"]["commit"]:
        raise ValueError("receiver commit or cleanliness changed")
    if git(receiver, "rev-parse", "HEAD^{tree}") != facts["receiver"]["tree"]:
        raise ValueError("receiver tree changed")
    if payload_digest != facts["receiver"]["payloadSha256"]:
        raise ValueError("receiver payload changed")
    if [{"path": item["path"], "sha256": item["sha256"]} for item in payload] != facts["receiver"]["projectedPayload"]:
        raise ValueError("receiver inventory changed")
    runtime_digest, runtime_files = tree_digest(args.runtime.resolve(), "runtime")
    if runtime_digest != facts["runtime"]["payloadSha256"] or runtime_files != facts["runtime"]["files"]:
        raise ValueError("runtime payload changed")
    for value in facts["executables"].values():
        if sha256(Path(value["path"])) != value["sha256"]:
            raise ValueError("selected host tool changed")


def command(args: argparse.Namespace) -> None:
    facts = json.loads(args.facts.read_text())
    grant = json.loads(args.grant.read_text())
    now = datetime.now(timezone.utc)
    deadline = now + timedelta(minutes=5)
    value = {
        "schema": "fsgg.workspace.command/1",
        "commandId": str(uuid.uuid4()),
        "idempotencyId": f"p4-python-{uuid.uuid4().hex}",
        "workspaceScope": "fs-gg/p4-python-receiver",
        "profileId": "portable-python-hello-v1",
        "profileRevision": "1",
        "sourceRevision": facts["receiver"]["commit"],
        "expectedWorkflowRevision": str(grant["workflowRevision"]),
        "fenceGeneration": str(grant["fenceGeneration"]),
        "deadline": deadline.strftime("%Y-%m-%dT%H:%M:%S.") + f"{deadline.microsecond:06d}Z",
        "operation": "test",
        "componentId": "python",
    }
    args.output.write_bytes(contract_bytes(value))


def contract(args: argparse.Namespace) -> None:
    grant = json.loads(args.grant.read_text())
    profile_value = profile(args.receiver_commit)
    args.profile.write_bytes(contract_bytes(profile_value))
    command(argparse.Namespace(facts=args.facts, grant=args.grant, output=args.command))


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser()
    commands = result.add_subparsers(dest="operation", required=True)
    collect_parser = commands.add_parser("collect")
    for name in ("join", "candidate", "receiver", "receiver-archive", "runtime", "output", "profile"):
        collect_parser.add_argument(f"--{name}", required=True, type=Path)
    verify_parser = commands.add_parser("verify")
    for name in ("facts", "candidate", "receiver", "runtime"):
        verify_parser.add_argument(f"--{name}", required=True, type=Path)
    command_parser = commands.add_parser("command")
    command_parser.add_argument("--facts", required=True, type=Path)
    command_parser.add_argument("--grant", required=True, type=Path)
    command_parser.add_argument("--output", required=True, type=Path)
    contract_parser = commands.add_parser("contract")
    contract_parser.add_argument("--facts", required=True, type=Path)
    contract_parser.add_argument("--grant", required=True, type=Path)
    contract_parser.add_argument("--receiver-commit", required=True)
    contract_parser.add_argument("--profile", required=True, type=Path)
    contract_parser.add_argument("--command", required=True, type=Path)
    return result


def main() -> int:
    args = parser().parse_args()
    try:
        {"collect": collect, "verify": verify, "command": command, "contract": contract}[args.operation](args)
        return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.SubprocessError) as error:
        print(f"PORTABLE_PROVIDER_FACTS_REFUSED {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
