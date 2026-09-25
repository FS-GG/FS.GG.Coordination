"""Pure exact-byte check of a local v5 refusal archive; never authority."""

from __future__ import annotations

import hashlib
import json
import re

import build_callable_isolated_v2_v5_no_grant as builder

HEX64 = re.compile(r"[0-9a-f]{64}\Z")


class Refused(ValueError):
    """Fixed refusal without source, archive or credential contents."""


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("v5-manifest-duplicate")
        value[key] = item
    return value


def _no_constant(_value):
    raise Refused("v5-manifest-nonfinite")


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def verify(archive: bytes, manifest_raw: bytes,
           approved_manifest_sha256: str, entry_source: bytes,
           builder_source: bytes, workflow: bytes) -> dict:
    """Compare exact selected bytes; a caller pin is not protected approval."""
    if (type(archive) is not bytes or not 0 < len(archive) <= 256_000
            or type(manifest_raw) is not bytes
            or not 0 < len(manifest_raw) <= 8192
            or type(entry_source) is not bytes
            or not 0 < len(entry_source) <= 128_000
            or type(builder_source) is not bytes
            or not 0 < len(builder_source) <= 128_000
            or type(workflow) is not bytes
            or not 0 < len(workflow) <= 128_000
            or type(approved_manifest_sha256) is not str
            or HEX64.fullmatch(approved_manifest_sha256) is None
            or approved_manifest_sha256 == "0" * 64
            or _sha(manifest_raw) != approved_manifest_sha256):
        raise Refused("v5-approval-or-bytes")
    try:
        manifest = json.loads(manifest_raw.decode("utf-8"),
                              object_pairs_hook=_unique,
                              parse_constant=_no_constant)
        canonical = (json.dumps(manifest, sort_keys=True,
                    separators=(",", ":"), ensure_ascii=True,
                    allow_nan=False) + "\n").encode("ascii")
    except Refused:
        raise
    except (UnicodeError, ValueError, TypeError):
        raise Refused("v5-manifest-json") from None
    if (type(manifest) is not dict or manifest_raw != canonical
            or set(manifest) != {"schema", "state", "artifact",
                "archiveSha256", "archiveSize", "builderSourceSha256",
                "workflowPath", "workflowSha256", "entry", "members"}
            or manifest["schema"] !=
               "fsgg.coordination.callable-isolated-v2-v5-no-grant/1"
            or manifest["state"] != "closed-source-not-installed"
            or manifest["artifact"] != builder.ARCHIVE_NAME
            or type(manifest["archiveSize"]) is not int
            or manifest["archiveSize"] != len(archive)
            or manifest["archiveSha256"] != _sha(archive)
            or manifest["builderSourceSha256"] != _sha(builder_source)
            or manifest["workflowPath"] != builder.WORKFLOW
            or manifest["workflowSha256"] != builder.PINNED_WORKFLOW_SHA256
            or _sha(workflow) != builder.PINNED_WORKFLOW_SHA256
            or manifest["entry"] != ["python3", "-I", "-S",
                builder.ARCHIVE_NAME, "execute-native-pull"]):
        raise Refused("v5-manifest-binding")
    members = manifest["members"]
    if (type(members) is not list or len(members) != 1
            or type(members[0]) is not dict
            or set(members[0]) != {"path", "source", "sha256", "size"}
            or members[0]["path"] != "__main__.py"
            or members[0]["source"] != builder.ENTRY_SOURCE
            or members[0]["sha256"] != builder.PINNED_ENTRY_SHA256
            or type(members[0]["size"]) is not int
            or members[0]["size"] != len(entry_source)
            or _sha(entry_source) != builder.PINNED_ENTRY_SHA256):
        raise Refused("v5-member-binding")
    if archive != builder._archive(entry_source):
        raise Refused("v5-archive-noncanonical")
    return {"schema": "fsgg.coordination.callable-isolated-v2-v5-no-grant-byte-check/1",
            "verified": True, "authorized": False, "canDispatch": False}
