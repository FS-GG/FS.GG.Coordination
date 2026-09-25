"""Pure exact-byte verifier for a closed effect scaffold, never authority."""

from __future__ import annotations

import io
import json
import re
import stat
import zipfile

import build_callable_isolated_v2_effect_scaffold as builder

HEX64 = re.compile(r"[0-9a-f]{64}\Z")


class Refused(ValueError):
    """Fixed non-authorizing refusal."""


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("effect-manifest-duplicate")
        value[key] = item
    return value


def _no_constant(_value):
    raise Refused("effect-manifest-nonfinite")


def verify(archive: bytes, workflow: bytes, manifest_raw: bytes,
           approved_manifest_sha256: str, builder_source: bytes | None = None,
           native_source: bytes | None = None) -> dict:
    """Compare caller-supplied bytes to an independently approved manifest pin."""
    if (type(archive) is not bytes or not 0 < len(archive) <= 256_000
            or type(workflow) is not bytes or not 0 < len(workflow) <= 128_000
            or type(manifest_raw) is not bytes
            or not 0 < len(manifest_raw) <= 16_384
            or type(builder_source) is not bytes
            or not 0 < len(builder_source) <= 128_000
            or type(native_source) is not bytes
            or not 0 < len(native_source) <= 256_000
            or type(approved_manifest_sha256) is not str
            or HEX64.fullmatch(approved_manifest_sha256) is None
            or approved_manifest_sha256 == "0" * 64
            or builder._sha(manifest_raw) != approved_manifest_sha256):
        raise Refused("effect-approval-or-bytes")
    try:
        manifest = json.loads(manifest_raw.decode("utf-8"),
                              object_pairs_hook=_unique,
                              parse_constant=_no_constant)
        canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":"),
                               ensure_ascii=True, allow_nan=False).encode("ascii") + b"\n"
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("effect-manifest-json") from None
    if (type(manifest) is not dict or manifest_raw != canonical
            or set(manifest) != {"schema", "state", "artifact", "archiveSha256",
                    "archiveSize", "builderSourceSha256", "nativeSource",
                    "workflowPath",
                    "workflowSha256", "entry", "members"}
            or manifest["schema"] !=
               "fsgg.coordination.callable-isolated-v2-effect-scaffold/1"
            or manifest["state"] != "closed-source-not-installed"
            or manifest["artifact"] != builder.ARCHIVE_NAME
            or type(manifest["archiveSize"]) is not int
            or manifest["archiveSize"] != len(archive)
            or manifest["archiveSha256"] != builder._sha(archive)
            or manifest["builderSourceSha256"] != builder._sha(builder_source)
            or manifest["nativeSource"] !=
               {"path": builder.NATIVE_SOURCE,
                "sha256": builder.NATIVE_SOURCE_SHA256,
                "size": len(native_source)}
            or builder._sha(native_source) != builder.NATIVE_SOURCE_SHA256
            or manifest["workflowPath"] != builder.WORKFLOW
            or manifest["workflowSha256"] != builder.PINNED_WORKFLOW_SHA256
            or builder._sha(workflow) != builder.PINNED_WORKFLOW_SHA256
            or manifest["entry"] != ["python3", "-I", "-S",
                                      builder.ARCHIVE_NAME, "execute-native-pull"]):
        raise Refused("effect-manifest-binding")
    members = manifest["members"]
    if (type(members) is not list or len(members) != len(builder.MEMBERS)
            or any(type(item) is not dict
                   or set(item) != {"path", "source", "sha256", "size"}
                   for item in members)):
        raise Refused("effect-member-shape")
    if [item["path"] for item in members] != sorted(builder.MEMBERS):
        raise Refused("effect-member-selection")
    try:
        member_bytes = {}
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            if zipped.namelist() != sorted(builder.MEMBERS) or zipped.comment:
                raise Refused("effect-archive-members")
            for item in members:
                name = item["path"]
                info = zipped.getinfo(name)
                if (item["source"] != builder.MEMBERS[name]
                        or item["sha256"] != builder.PINNED_MEMBERS[name]
                        or type(item["size"]) is not int
                        or item["size"] <= 0
                        or info.file_size != item["size"]
                        or info.date_time != builder.FIXED_TIME
                        or info.compress_type != zipfile.ZIP_STORED
                        or info.external_attr >> 16 != 0o100644
                        or info.extra or info.comment
                        or builder._sha(zipped.read(name)) != item["sha256"]):
                    raise Refused("effect-member-binding")
                member_bytes[name] = zipped.read(name)
        exact = io.BytesIO()
        with zipfile.ZipFile(exact, mode="w", compression=zipfile.ZIP_STORED,
                             allowZip64=False) as canonical:
            canonical.comment = b""
            for name in sorted(member_bytes):
                info = zipfile.ZipInfo(name, builder.FIXED_TIME)
                info.compress_type = zipfile.ZIP_STORED
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | 0o644) << 16
                canonical.writestr(info, member_bytes[name])
        if exact.getvalue() != archive:
            raise Refused("effect-archive-noncanonical")
    except Refused:
        raise
    except (KeyError, OSError, ValueError, zipfile.BadZipFile):
        raise Refused("effect-archive-invalid") from None
    return {"schema": "fsgg.coordination.callable-isolated-v2-effect-byte-check/1",
            "verified": True, "authorized": False, "canDispatch": False}
