#!/usr/bin/env python3
"""Verify exact source and safe archive bytes for the v2 runtime candidate."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import re
import stat
import zipfile

import build_callable_isolated_v2_runtime as builder


HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
MANIFEST_KEYS = {
    "schema", "sourceRevision", "sourceTree", "builderSha256",
    "retainedOperatorSha256", "archiveSha256", "runtimeRequirements",
    "members",
}


class Refused(ValueError):
    """Fixed refusal without echoing candidate or credential bytes."""


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("runtime-manifest-duplicate")
        value[key] = item
    return value


def _constant(_value):
    raise Refused("runtime-manifest-nonfinite")


def _manifest(raw: bytes) -> dict:
    if type(raw) is not bytes or not 0 < len(raw) <= 64_000:
        raise Refused("runtime-manifest-size")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=_constant)
        encoded = builder.canonical(value)
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("runtime-manifest-json") from None
    if type(value) is not dict or raw != encoded or set(value) != MANIFEST_KEYS:
        raise Refused("runtime-manifest-shape")
    return value


def _members(archive: bytes) -> dict[str, bytes]:
    if type(archive) is not bytes or not 0 < len(archive) <= 2_000_000:
        raise Refused("runtime-archive-size")
    expected = sorted(builder.MEMBER_SOURCES)
    try:
        with zipfile.ZipFile(io.BytesIO(archive), "r") as zipped:
            if zipped.comment != b"":
                raise Refused("runtime-archive-comment")
            infos = zipped.infolist()
            if [info.filename for info in infos] != expected:
                raise Refused("runtime-archive-roster")
            for info in infos:
                mode = info.external_attr >> 16
                if (info.date_time != builder.FIXED_TIME
                        or info.compress_type != zipfile.ZIP_STORED
                        or info.flag_bits & 0x1
                        or info.create_system != 3
                        or not stat.S_ISREG(mode)
                        or stat.S_IMODE(mode) != 0o644
                        or not 0 < info.file_size <= 256_000
                        or info.compress_size != info.file_size):
                    raise Refused("runtime-archive-metadata")
            return {info.filename: zipped.read(info) for info in infos}
    except Refused:
        raise
    except (zipfile.BadZipFile, RuntimeError, OSError, ValueError):
        raise Refused("runtime-archive-invalid") from None


def verify(archive: bytes, manifest_raw: bytes) -> dict:
    manifest = _manifest(manifest_raw)
    try:
        revision, tree = builder.source_identity()
        sources = builder.source_members()
        builder_sha = _sha(builder.source_blob(builder.BUILDER_SOURCE))
    except ValueError:
        raise Refused("runtime-source-unavailable") from None
    requirements = manifest["runtimeRequirements"]
    if (type(manifest["schema"]) is not str
            or manifest["schema"] != builder.SCHEMA
            or type(manifest["sourceRevision"]) is not str
            or HEX40.fullmatch(manifest["sourceRevision"]) is None
            or manifest["sourceRevision"] != revision
            or type(manifest["sourceTree"]) is not str
            or HEX40.fullmatch(manifest["sourceTree"]) is None
            or manifest["sourceTree"] != tree
            or type(manifest["builderSha256"]) is not str
            or HEX64.fullmatch(manifest["builderSha256"]) is None
            or manifest["builderSha256"] != builder_sha
            or type(manifest["retainedOperatorSha256"]) is not str
            or HEX64.fullmatch(manifest["retainedOperatorSha256"]) is None
            or manifest["retainedOperatorSha256"] != _sha(sources[
                "callable_isolated_v2_retained_operator.py"])
            or type(manifest["archiveSha256"]) is not str
            or HEX64.fullmatch(manifest["archiveSha256"]) is None
            or manifest["archiveSha256"] != _sha(archive)
            or type(requirements) is not dict
            or set(requirements) !=
               {"pythonMin", "sqlite", "opensslEd25519Pkeyutl"}
            or type(requirements.get("pythonMin")) is not str
            or type(requirements.get("sqlite")) is not bool
            or type(requirements.get("opensslEd25519Pkeyutl")) is not bool
            or requirements != builder.RUNTIME_REQUIREMENTS):
        raise Refused("runtime-manifest-binding")

    members = _members(archive)
    listed = manifest["members"]
    if type(listed) is not list or len(listed) != len(sources):
        raise Refused("runtime-member-manifest")
    for index, path in enumerate(sorted(sources)):
        entry = listed[index]
        raw = members.get(path)
        if (type(entry) is not dict or set(entry) != {"path", "sha256", "size"}
                or entry["path"] != path
                or type(entry["sha256"]) is not str
                or HEX64.fullmatch(entry["sha256"]) is None
                or type(entry["size"]) is not int
                or entry["size"] != len(sources[path])
                or entry["sha256"] != _sha(sources[path])
                or raw != sources[path]):
            raise Refused("runtime-member-binding")
    if archive != builder._archive(sources):
        raise Refused("runtime-archive-noncanonical")
    return {"schema": builder.SCHEMA, "verified": True,
            "sourceRevision": revision, "sourceTree": tree,
            "archiveSha256": manifest["archiveSha256"]}


def _read(path: pathlib.Path, maximum: int, reason: str) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise Refused(reason)
    size = path.stat().st_size
    if not 0 < size <= maximum:
        raise Refused(reason)
    return path.read_bytes()


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--archive", required=True, type=pathlib.Path)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    if (arguments.archive.name != builder.ARCHIVE_NAME
            or arguments.manifest.name != builder.MANIFEST_NAME):
        raise Refused("runtime-artifact-name")
    result = verify(_read(arguments.archive, 2_000_000, "runtime-archive-file"),
                    _read(arguments.manifest, 64_000, "runtime-manifest-file"))
    print("CALLABLE_ISOLATED_V2_RUNTIME_ARTIFACT_VERIFIED "
          f"revision={result['sourceRevision']} "
          f"archiveSha256={result['archiveSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
