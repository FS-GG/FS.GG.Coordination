"""Local candidate manifest for the inspect-only v2 Python runtime closure.

This module has no CLI or protected adapter. A manifest made by this module is
self-observed until a separate protected release pins its digest, runner image,
and immutable filesystem. It never supplies authority or a dispatch path.
"""

from __future__ import annotations

import dataclasses
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import tempfile
from typing import Any

import verify_callable_isolated_v2_install as install

SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-closure/1"
STATE = "local-candidate-not-protected"
EXCLUDED_STDLIB_DIRS = frozenset({"site-packages", "dist-packages"})
MAX_MANIFEST = 65_536
MAX_FILE = 100_000_000
MAX_FILES = 10_000
MAX_TOTAL_BYTES = 1_000_000_000

PROBE = r'''
import contextlib, io, json, os, pathlib, runpy, sys, sysconfig
archive = sys.argv[1]
sys.argv = [archive, "--help"]
output = io.StringIO()
with contextlib.redirect_stdout(output):
    try:
        runpy.run_path(archive, run_name="__main__")
    except SystemExit as result:
        if result.code != 0:
            raise
if output.getvalue() != "usage: inspect-grant --grant FILE --digest SHA256 --expected FILE --replay FILE\n":
    raise RuntimeError("closed entry changed")
root = pathlib.Path(sysconfig.get_path("stdlib")).resolve(strict=True)
mapped = set()
for line in pathlib.Path("/proc/self/maps").read_text().splitlines():
    fields = line.split(None, 5)
    if len(fields) == 6 and fields[5].startswith("/"):
        if fields[5].endswith(" (deleted)"):
            raise RuntimeError("mapped file deleted")
        mapped.add(os.path.realpath(fields[5]))
print(json.dumps({"stdlibRoot": str(root), "mappedPaths": sorted(mapped)},
                 sort_keys=True, separators=(",", ":")))
'''
PROBE_SHA256 = hashlib.sha256(PROBE.encode("utf-8")).hexdigest()


class Refused(ValueError):
    """Fixed refusal code without raw runtime paths or child output."""


@dataclasses.dataclass(frozen=True)
class ClosureEvidence:
    manifest_sha256: str
    stdlib_tree_sha256: str
    stdlib_file_count: int
    mapped_file_count: int
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _canonical(value: Any) -> bytes:
    try:
        return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=True, allow_nan=False) + "\n").encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("runtime-canonical") from None


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise Refused("runtime-duplicate-member")
        value[key] = item
    return value


def _file_entry(path: pathlib.Path) -> dict[str, Any]:
    if not isinstance(path, pathlib.Path) or not path.is_absolute():
        raise Refused("runtime-path")
    try:
        if path.resolve(strict=True) != path:
            raise Refused("runtime-path")
        metadata = path.lstat()
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > MAX_FILE:
            raise Refused("runtime-file")
        raw = path.read_bytes()
    except Refused:
        raise
    except (OSError, RuntimeError):
        raise Refused("runtime-file") from None
    if len(raw) != metadata.st_size:
        raise Refused("runtime-file")
    return {"path": str(path), "size": len(raw), "sha256": _sha(raw)}


def _tree(root: pathlib.Path) -> dict[str, Any]:
    if not isinstance(root, pathlib.Path) or not root.is_absolute():
        raise Refused("runtime-stdlib-root")
    try:
        if root.resolve(strict=True) != root or not root.is_dir() or root.is_symlink():
            raise Refused("runtime-stdlib-root")
    except (OSError, RuntimeError):
        raise Refused("runtime-stdlib-root") from None
    entries: list[tuple[str, int, str]] = []
    directories: list[str] = []
    total = 0
    def on_error(_error: OSError) -> None:
        raise Refused("runtime-stdlib-walk")

    for directory, names, files in os.walk(root, topdown=True,
                                           followlinks=False, onerror=on_error):
        base = pathlib.Path(directory)
        for name in names[:]:
            if name in EXCLUDED_STDLIB_DIRS:
                names.remove(name)
            elif (base / name).is_symlink():
                raise Refused("runtime-stdlib-symlink")
            else:
                directories.append((base / name).relative_to(root).as_posix())
                if len(directories) > MAX_FILES:
                    raise Refused("runtime-stdlib-bounds")
        for name in files:
            path = base / name
            if path.is_symlink():
                raise Refused("runtime-stdlib-symlink")
            item = _file_entry(path)
            relative = path.relative_to(root).as_posix()
            entries.append((relative, item["size"], item["sha256"]))
            total += item["size"]
            if len(entries) > MAX_FILES or total > MAX_TOTAL_BYTES:
                raise Refused("runtime-stdlib-bounds")
    if not entries:
        raise Refused("runtime-stdlib-empty")
    entries.sort()
    digest = hashlib.sha256()
    for relative in sorted(directories):
        raw = relative.encode("utf-8")
        digest.update(b"D")
        digest.update(len(raw).to_bytes(4, "big"))
        digest.update(raw)
    for relative, size, sha in entries:
        raw = relative.encode("utf-8")
        digest.update(b"F")
        digest.update(len(raw).to_bytes(4, "big"))
        digest.update(raw)
        digest.update(size.to_bytes(8, "big"))
        digest.update(bytes.fromhex(sha))
    return {"root": str(root), "directoryCount": len(directories),
            "fileCount": len(entries), "totalBytes": total,
            "treeSha256": digest.hexdigest(),
            "excludedDirectories": sorted(EXCLUDED_STDLIB_DIRS)}


def _probe(binary: pathlib.Path, archive: pathlib.Path) -> tuple[pathlib.Path, list[pathlib.Path]]:
    try:
        with tempfile.TemporaryDirectory(prefix="fsgg-v2-closure-probe-") as temporary:
            result = subprocess.run([str(binary), "-I", "-S", "-c", PROBE, str(archive)],
                                    cwd=temporary,
                                    env={"LC_ALL": "C", "PATH": "/usr/bin:/bin"},
                                    capture_output=True, text=True, timeout=15, check=False)
            if list(pathlib.Path(temporary).iterdir()):
                raise Refused("runtime-probe-wrote-cwd")
    except Refused:
        raise
    except (OSError, subprocess.TimeoutExpired, UnicodeError):
        raise Refused("runtime-probe-unavailable") from None
    if result.returncode != 0 or result.stderr or len(result.stdout) > 32_768:
        raise Refused("runtime-probe-failed")
    try:
        value = json.loads(result.stdout, object_pairs_hook=_unique)
    except Refused:
        raise
    except (ValueError, UnicodeError):
        raise Refused("runtime-probe-json") from None
    if (type(value) is not dict or set(value) != {"stdlibRoot", "mappedPaths"}
            or type(value["stdlibRoot"]) is not str
            or type(value["mappedPaths"]) is not list
            or not value["mappedPaths"] or len(value["mappedPaths"]) > 128
            or any(type(path) is not str or not path.startswith("/")
                   for path in value["mappedPaths"])
            or value["mappedPaths"] != sorted(set(value["mappedPaths"]))):
        raise Refused("runtime-probe-shape")
    return pathlib.Path(value["stdlibRoot"]), [pathlib.Path(path) for path in value["mappedPaths"]]


def capture_candidate(source_root: pathlib.Path, installed_archive: pathlib.Path,
                      interpreter_pin: dict[str, Any]) -> bytes:
    """Capture local closure after #560 exact archive/interpreter verification."""
    try:
        checked = install.verify_clean_install(source_root, installed_archive, interpreter_pin)
    except install.Refused as error:
        raise Refused(f"runtime-install-{error}") from None
    binary = pathlib.Path(interpreter_pin["path"])
    stdlib, mapped_paths = _probe(binary, installed_archive)
    if (install._sha(install._regular(installed_archive, 32_768)) != checked.archive_sha256
            or install._sha(install._regular(binary, 100_000_000)) != checked.interpreter_sha256):
        raise Refused("runtime-probe-drift")
    if binary not in mapped_paths:
        raise Refused("runtime-interpreter-unmapped")
    mapped = [_file_entry(path) for path in mapped_paths]
    manifest = {
        "schema": SCHEMA, "state": STATE,
        "archiveSha256": checked.archive_sha256,
        "interpreter": {"path": str(binary), "sha256": checked.interpreter_sha256,
                        "version": checked.interpreter_version, "flags": ["-I", "-S"]},
        "stdlib": _tree(stdlib), "mappedFiles": mapped,
        "probeSha256": PROBE_SHA256,
        "environment": {"LC_ALL": "C", "PATH": "/usr/bin:/bin"},
    }
    raw = _canonical(manifest)
    if len(raw) > MAX_MANIFEST:
        raise Refused("runtime-manifest-bounds")
    return raw


def verify_candidate(raw: bytes, expected_sha256: str, source_root: pathlib.Path,
                     installed_archive: pathlib.Path,
                     interpreter_pin: dict[str, Any]) -> ClosureEvidence:
    """Require an independently supplied digest and recompute the closure.

    This cannot prove that the digest or runner came from a protected release.
    """
    if (type(raw) is not bytes or len(raw) > MAX_MANIFEST
            or type(expected_sha256) is not str
            or install.HEX64.fullmatch(expected_sha256) is None
            or _sha(raw) != expected_sha256):
        raise Refused("runtime-manifest-digest")
    try:
        parsed = json.loads(raw.decode("ascii"), object_pairs_hook=_unique)
    except Refused:
        raise
    except (UnicodeError, ValueError):
        raise Refused("runtime-manifest-json") from None
    if _canonical(parsed) != raw:
        raise Refused("runtime-manifest-noncanonical")
    fresh = capture_candidate(source_root, installed_archive, interpreter_pin)
    if fresh != raw:
        raise Refused("runtime-closure-drift")
    return ClosureEvidence(expected_sha256, parsed["stdlib"]["treeSha256"],
                           parsed["stdlib"]["fileCount"], len(parsed["mappedFiles"]))
