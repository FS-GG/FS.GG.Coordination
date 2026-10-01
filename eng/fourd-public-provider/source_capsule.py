#!/usr/bin/env python3
"""Closed validation and inert reconstruction for the FourD source capsule."""

from __future__ import annotations

import base64
import binascii
import hashlib
import json
import os
import pathlib
import re
import stat
import subprocess
from typing import Mapping

SNAPSHOT_SCHEMA = "fsgg.fourd.source-snapshot/1"
DESCRIPTOR_SCHEMA = "fsgg.fourd.source-capsule-descriptor/1"
SNAPSHOT_KEYS = {"schema", "sourceSha", "sourceTree", "inventorySha256", "commitObjectBase64", "files"}
FILE_KEYS = {"path", "mode", "blobOid", "bytes", "sha256", "contentBase64"}
DESCRIPTOR_KEYS = {
    "schema", "purpose", "placementSha", "runId", "runAttempt", "runNonce",
    "coordinationRepositoryId", "fourdRepositoryId", "sourceSha", "sourceTree",
    "inventorySha256", "plaintextBytes", "plaintextSha256", "recipientPublicKeySha256",
    "sealerSha256", "issuedAt", "expiresAt",
}
MAX_FILES = 512
MAX_PATH = 4096
MAX_FILE = 2 * 1024 * 1024
MAX_BLOBS = 8 * 1024 * 1024
MAX_COMMIT = 64 * 1024
MAX_PLAINTEXT = 16 * 1024 * 1024
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
ALLOWED_MODES = {"100644", "100755"}
CAPSULE_KEYS = {"schema", "algorithm", "runNonce", "sourceSha", "profileSha256", "aadSha256",
                "nonce", "tag", "wrappedKey", "ciphertext"}


class CapsuleRefusal(RuntimeError):
    pass


def canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"


def closed_json(raw: bytes, limit: int) -> object:
    if not raw or len(raw) > limit:
        raise CapsuleRefusal("json-size-refused")
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise CapsuleRefusal("json-duplicate-key-refused")
            result[key] = value
        return result
    try:
        return json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=pairs)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise CapsuleRefusal("json-refused") from error


def strict_b64(value: object, limit: int) -> bytes:
    if not isinstance(value, str) or len(value) > 4 * ((limit + 2) // 3):
        raise CapsuleRefusal("base64-refused")
    try:
        decoded = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as error:
        raise CapsuleRefusal("base64-refused") from error
    if len(decoded) > limit or base64.b64encode(decoded).decode() != value:
        raise CapsuleRefusal("base64-refused")
    return decoded


def _path(value: object) -> str:
    if not isinstance(value, str) or not value or len(value.encode("utf-8")) > MAX_PATH:
        raise CapsuleRefusal("snapshot-path-refused")
    if any(ord(char) < 32 or ord(char) == 127 for char in value):
        raise CapsuleRefusal("snapshot-path-refused")
    path = pathlib.PurePosixPath(value)
    if path.is_absolute() or str(path) != value or any(part in ("", ".", "..") or part.casefold() == ".git" for part in path.parts):
        raise CapsuleRefusal("snapshot-path-refused")
    return value


def validate_descriptor(value: object, expected: Mapping[str, object]) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != DESCRIPTOR_KEYS:
        raise CapsuleRefusal("descriptor-shape-refused")
    if value.get("schema") != DESCRIPTOR_SCHEMA or value.get("purpose") != "fourd-source-acquisition":
        raise CapsuleRefusal("descriptor-purpose-refused")
    for key, expected_value in expected.items():
        if key not in DESCRIPTOR_KEYS or value.get(key) != expected_value:
            raise CapsuleRefusal("descriptor-binding-refused")
    for key in ("placementSha", "sourceSha", "sourceTree"):
        if not isinstance(value[key], str) or not HEX40.fullmatch(value[key]):
            raise CapsuleRefusal("descriptor-identity-refused")
    for key in ("inventorySha256", "plaintextSha256", "recipientPublicKeySha256", "sealerSha256"):
        if not isinstance(value[key], str) or not HEX64.fullmatch(value[key]):
            raise CapsuleRefusal("descriptor-digest-refused")
    if type(value["plaintextBytes"]) is not int or not 2 <= value["plaintextBytes"] <= MAX_PLAINTEXT:
        raise CapsuleRefusal("descriptor-size-refused")
    for key in ("runId", "runAttempt", "coordinationRepositoryId", "fourdRepositoryId"):
        if not isinstance(value[key], str) or not value[key].isdigit() or value[key].startswith("0"):
            raise CapsuleRefusal("descriptor-identifier-refused")
    return value


def validate_outer_capsule(raw: bytes, run_nonce: str, source_sha: str, profile_sha: str) -> dict[str, object]:
    value = closed_json(raw, 24 * 1024 * 1024)
    if not isinstance(value, dict) or set(value) != CAPSULE_KEYS:
        raise CapsuleRefusal("capsule-shape-refused")
    if (value["schema"] != "fsgg.telemetry.native-custody-capsule/1"
            or value["algorithm"] != "AES-256-GCM+RSA-OAEP-SHA256"
            or value["runNonce"] != run_nonce or value["sourceSha"] != source_sha
            or value["profileSha256"] != profile_sha):
        raise CapsuleRefusal("capsule-binding-refused")
    aad = f"fsgg-native-custody/1\0{run_nonce}\0{source_sha}\0{profile_sha}".encode()
    if value["aadSha256"] != hashlib.sha256(aad).hexdigest():
        raise CapsuleRefusal("capsule-aad-refused")
    strict_b64(value["nonce"], 12)
    strict_b64(value["tag"], 16)
    strict_b64(value["wrappedKey"], 1024)
    strict_b64(value["ciphertext"], MAX_PLAINTEXT)
    return value


def validate_snapshot(raw: bytes, expected: Mapping[str, str]) -> tuple[dict[str, object], list[tuple[str, str, str, bytes]], bytes]:
    value = closed_json(raw, MAX_PLAINTEXT)
    if not isinstance(value, dict) or set(value) != SNAPSHOT_KEYS or value.get("schema") != SNAPSHOT_SCHEMA:
        raise CapsuleRefusal("snapshot-shape-refused")
    for key in ("sourceSha", "sourceTree", "inventorySha256"):
        if value.get(key) != expected[key]:
            raise CapsuleRefusal("snapshot-binding-refused")
    commit = strict_b64(value["commitObjectBase64"], MAX_COMMIT)
    if not commit or b"\0" in commit:
        raise CapsuleRefusal("snapshot-commit-refused")
    files = value["files"]
    if not isinstance(files, list) or not 1 <= len(files) <= MAX_FILES:
        raise CapsuleRefusal("snapshot-files-refused")
    records = []
    total = 0
    previous = None
    inventory = hashlib.sha256()
    for item in files:
        if not isinstance(item, dict) or set(item) != FILE_KEYS:
            raise CapsuleRefusal("snapshot-file-shape-refused")
        name = _path(item["path"])
        if previous is not None and name <= previous:
            raise CapsuleRefusal("snapshot-order-refused")
        previous = name
        mode = item["mode"]
        if mode not in ALLOWED_MODES:
            raise CapsuleRefusal("snapshot-mode-refused")
        content = strict_b64(item["contentBase64"], MAX_FILE)
        if type(item["bytes"]) is not int or item["bytes"] != len(content):
            raise CapsuleRefusal("snapshot-size-refused")
        total += len(content)
        if total > MAX_BLOBS:
            raise CapsuleRefusal("snapshot-total-refused")
        digest = hashlib.sha256(content).hexdigest()
        blob = hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
        if item["sha256"] != digest or item["blobOid"] != blob:
            raise CapsuleRefusal("snapshot-file-digest-refused")
        inventory.update(name.encode()); inventory.update(b"\0"); inventory.update(content); inventory.update(b"\0")
        records.append((name, mode, blob, content))
    if inventory.hexdigest() != expected["inventorySha256"]:
        raise CapsuleRefusal("snapshot-inventory-refused")
    return value, records, commit


def _run(argv: list[str], cwd: pathlib.Path, stdin: bytes | None = None) -> bytes:
    safe = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8",
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_TERMINAL_PROMPT": "0"}
    try:
        result = subprocess.run(argv, cwd=cwd, env=safe, input=stdin, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise CapsuleRefusal("git-reconstruction-refused") from error
    if result.returncode or len(result.stdout) > 65536 or len(result.stderr) > 65536:
        raise CapsuleRefusal("git-reconstruction-refused")
    return result.stdout


def reconstruct(raw: bytes, target: pathlib.Path, expected: Mapping[str, str]) -> pathlib.Path:
    _value, records, commit = validate_snapshot(raw, expected)
    target.mkdir(mode=0o700, parents=False, exist_ok=False)
    _run(["/usr/bin/git", "init", "--quiet", "--template="], target)
    _run(["/usr/bin/git", "config", "core.hooksPath", "/dev/null"], target)
    _run(["/usr/bin/git", "config", "core.autocrlf", "false"], target)
    for name, mode, expected_blob, content in records:
        blob = _run(["/usr/bin/git", "hash-object", "--no-filters", "-w", "--stdin"], target, content).decode("ascii").strip()
        if blob != expected_blob:
            raise CapsuleRefusal("git-blob-refused")
        _run(["/usr/bin/git", "update-index", "--add", "--cacheinfo", mode, blob, name], target)
    tree = _run(["/usr/bin/git", "write-tree"], target).decode("ascii").strip()
    if tree != expected["sourceTree"]:
        raise CapsuleRefusal("git-tree-refused")
    commit_oid = _run(["/usr/bin/git", "hash-object", "-t", "commit", "-w", "--stdin"], target, commit).decode("ascii").strip()
    if commit_oid != expected["sourceSha"]:
        raise CapsuleRefusal("git-commit-refused")
    commit_tree = _run(["/usr/bin/git", "show", "-s", "--format=%T", commit_oid], target).decode("ascii").strip()
    if commit_tree != tree:
        raise CapsuleRefusal("git-commit-tree-refused")
    shallow = target / ".git/shallow"
    fd = os.open(shallow, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        os.write(fd, (commit_oid + "\n").encode()); os.fsync(fd)
    finally:
        os.close(fd)
    (target / ".git/index").unlink(missing_ok=True)
    _run(["/usr/bin/git", "checkout", "--quiet", "--detach", commit_oid], target)
    if _run(["/usr/bin/git", "status", "--porcelain", "--untracked-files=all"], target):
        raise CapsuleRefusal("git-cleanliness-refused")
    for name, mode, _blob, _content in records:
        entry = target / name
        actual = entry.lstat()
        if not stat.S_ISREG(actual.st_mode) or actual.st_nlink != 1:
            raise CapsuleRefusal("git-worktree-refused")
        expected_mode = 0o755 if mode == "100755" else 0o644
        if stat.S_IMODE(actual.st_mode) != expected_mode:
            raise CapsuleRefusal("git-worktree-refused")
    return target
