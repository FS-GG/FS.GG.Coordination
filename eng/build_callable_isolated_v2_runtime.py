#!/usr/bin/env python3
"""Build the deterministic callable isolated v2 runtime candidate."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import re
import stat
import subprocess
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
ARCHIVE_NAME = "callable-isolated-v2-runtime.pyz"
MANIFEST_NAME = "callable-isolated-v2-runtime-manifest.json"
SCHEMA = "fsgg.gs2-09-9-v5-runtime-candidate/1"
BUILDER_SOURCE = "eng/build_callable_isolated_v2_runtime.py"
RETAINED_OPERATOR_SOURCE = "eng/callable-cli-isolated-operation-v2.py"
MEMBER_SOURCES = {
    "__main__.py": "eng/callable_isolated_v2_runtime_main.py",
    "callable_isolated_v2_retained_operator.py": RETAINED_OPERATOR_SOURCE,
    "callable_isolated_v2_runtime/__init__.py":
        "eng/callable_isolated_v2_runtime/__init__.py",
    "callable_isolated_v2_runtime/adapters.py":
        "eng/callable_isolated_v2_runtime/adapters.py",
    "callable_isolated_v2_runtime/contracts.py":
        "eng/callable_isolated_v2_runtime/contracts.py",
    "callable_isolated_v2_runtime/coordinator.py":
        "eng/callable_isolated_v2_runtime/coordinator.py",
    "callable_isolated_v2_runtime/grant.py":
        "eng/callable_isolated_v2_runtime/grant.py",
}
RUNTIME_REQUIREMENTS = {
    "pythonMin": "3.11",
    "sqlite": True,
    "opensslEd25519Pkeyutl": True,
}
FIXED_TIME = (1980, 1, 1, 0, 0, 0)
HEX40 = re.compile(r"[0-9a-f]{40}\Z")


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _git(*arguments: str) -> bytes:
    try:
        return subprocess.run(
            ["git", "-C", str(ROOT), *arguments], check=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        raise ValueError("runtime-source-unavailable") from None


def source_identity() -> tuple[str, str]:
    revision = _git("rev-parse", "--verify", "HEAD").decode("ascii").strip()
    tree = _git("rev-parse", "--verify", "HEAD^{tree}").decode("ascii").strip()
    if HEX40.fullmatch(revision) is None or HEX40.fullmatch(tree) is None:
        raise ValueError("runtime-source-identity")
    return revision, tree


def source_blob(relative: str) -> bytes:
    if (type(relative) is not str or relative.startswith("/")
            or ".." in pathlib.PurePosixPath(relative).parts):
        raise ValueError("runtime-source-path")
    raw = _git("show", f"HEAD:{relative}")
    if not 0 < len(raw) <= 256_000:
        raise ValueError("runtime-source-size")
    return raw


def source_members() -> dict[str, bytes]:
    return {path: source_blob(source)
            for path, source in sorted(MEMBER_SOURCES.items())}


def _archive(members: dict[str, bytes]) -> bytes:
    if list(members) != sorted(MEMBER_SOURCES) or set(members) != set(MEMBER_SOURCES):
        raise ValueError("runtime-member-roster")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED,
                         allowZip64=False) as archive:
        archive.comment = b""
        for path, raw in members.items():
            if type(raw) is not bytes or not 0 < len(raw) <= 256_000:
                raise ValueError("runtime-member-bytes")
            info = zipfile.ZipInfo(path, FIXED_TIME)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, raw)
    return output.getvalue()


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                       ensure_ascii=True, allow_nan=False) + "\n").encode("utf-8")


def build(output_directory: pathlib.Path) -> dict:
    output_directory = pathlib.Path(output_directory)
    if output_directory.is_symlink():
        raise ValueError("runtime-output-directory")
    output_directory.mkdir(parents=True, exist_ok=True)
    if not output_directory.is_dir():
        raise ValueError("runtime-output-directory")
    archive_path = output_directory / ARCHIVE_NAME
    manifest_path = output_directory / MANIFEST_NAME
    if archive_path.is_symlink() or manifest_path.is_symlink():
        raise ValueError("runtime-output-symlink")

    revision, tree = source_identity()
    members = source_members()
    archive = _archive(members)
    manifest = {
        "schema": SCHEMA,
        "sourceRevision": revision,
        "sourceTree": tree,
        "builderSha256": _sha(source_blob(BUILDER_SOURCE)),
        "retainedOperatorSha256": _sha(members[
            "callable_isolated_v2_retained_operator.py"]),
        "archiveSha256": _sha(archive),
        "runtimeRequirements": RUNTIME_REQUIREMENTS,
        "members": [
            {"path": path, "sha256": _sha(raw), "size": len(raw)}
            for path, raw in members.items()
        ],
    }
    archive_path.write_bytes(archive)
    manifest_path.write_bytes(canonical(manifest))
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--output-directory", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    build(arguments.output_directory)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
