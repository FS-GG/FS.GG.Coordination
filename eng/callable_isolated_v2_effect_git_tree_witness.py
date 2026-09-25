"""Pure Git object membership witness for closed effect scaffold source bytes."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
import io
import copy
import zipfile
from typing import Any, Protocol

import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_release_preflight as release
import verify_callable_isolated_v2_effect_scaffold as bytes_check

RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-git-tree-witness/1"
IDENTITY_SCHEMA = "fsgg.coordination.callable-isolated-v2-repository-identity/1"
MAX_GIT_OBJECT = 2_000_000
MODES = {b"100644", b"100755", b"120000", b"160000", b"40000"}


class Refused(ValueError):
    """Fixed refusal without source bytes or reader credential contents."""


class GitObjectPort(Protocol):
    """Injected Git commit/tree/blob reader; no transport is supplied."""

    def scope(self) -> dict[str, Any]: ...
    def read_commit(self, oid: str) -> bytes: ...
    def read_tree(self, oid: str) -> bytes: ...
    def read_blob(self, oid: str) -> bytes: ...


class RepositoryIdentityPort(Protocol):
    """Separate read-only repository/object-format attestation reader."""

    def scope(self) -> dict[str, Any]: ...
    def read_repository_identity(self, event_id: int) -> dict[str, Any]: ...


@dataclasses.dataclass(frozen=True)
class TreeWitnessResult:
    coordination_revision: str
    source_tree: str
    archive_sha256: str
    source_files: tuple[tuple[str, str], ...]
    identity_event_id: int
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _oid(kind: str, raw: bytes) -> str:
    prefix = kind.encode() + b" " + str(len(raw)).encode() + b"\0"
    return hashlib.sha1(prefix + raw).hexdigest()


def _read(port: GitObjectPort, kind: str, oid: str) -> bytes:
    try:
        raw = getattr(port, f"read_{kind}")(oid)
    except Exception:
        raise Refused("git-object-unavailable") from None
    if (type(raw) is not bytes or not 0 < len(raw) <= MAX_GIT_OBJECT
            or _oid(kind, raw) != oid):
        raise Refused("git-object-binding")
    return raw


def _scope(port: GitObjectPort | RepositoryIdentityPort,
           repository_id: int, permissions: list[str],
           now: dt.datetime) -> dict[str, Any]:
    try:
        scope = port.scope()
    except Exception:
        raise Refused("git-scope-unavailable") from None
    scope = _exact(scope, {"principalId", "credentialId", "repository",
                           "repositoryId", "permissions", "expiresAt"},
                   "git-scope-shape")
    try:
        expires = candidate._time(scope["expiresAt"])
    except candidate.Refused:
        raise Refused("git-scope-time") from None
    if (type(scope["principalId"]) is not str or not scope["principalId"]
            or not candidate._hex(scope["credentialId"], candidate.HEX64)
            or scope["repository"] != release.REPOSITORY
            or type(scope["repositoryId"]) is not int
            or scope["repositoryId"] != repository_id
            or type(scope["permissions"]) is not list
            or scope["permissions"] != permissions
            or expires <= now):
        raise Refused("git-scope-binding")
    return copy.deepcopy(scope)


def _identity(port: RepositoryIdentityPort, event_id: int,
              scope: dict[str, Any], repository_id: int,
              now: dt.datetime) -> None:
    try:
        record = port.read_repository_identity(event_id)
    except Exception:
        raise Refused("git-identity-unavailable") from None
    record = _exact(record, {"schema", "complete", "principalId",
        "credentialId", "eventId", "repository", "repositoryId",
        "objectFormat", "observedAt"}, "git-identity-shape")
    try:
        observed = candidate._time(record["observedAt"])
    except candidate.Refused:
        raise Refused("git-identity-time") from None
    if (record["schema"] != IDENTITY_SCHEMA or record["complete"] is not True
            or record["principalId"] != scope["principalId"]
            or record["credentialId"] != scope["credentialId"]
            or type(record["eventId"]) is not int
            or record["eventId"] != event_id
            or record["repository"] != release.REPOSITORY
            or type(record["repositoryId"]) is not int
            or record["repositoryId"] != repository_id
            or record["objectFormat"] != "sha1"
            or not now - dt.timedelta(minutes=30) <= observed <= now):
        raise Refused("git-identity-binding")


def _tree_entries(raw: bytes) -> dict[str, tuple[bytes, str]]:
    entries: dict[str, tuple[bytes, str]] = {}
    offset = 0
    while offset < len(raw):
        split = raw.find(b" ", offset)
        nul = raw.find(b"\0", split + 1)
        if split <= offset or nul <= split + 1 or nul + 21 > len(raw):
            raise Refused("git-tree-shape")
        mode = raw[offset:split]
        name_raw = raw[split + 1:nul]
        try:
            name = name_raw.decode("utf-8")
        except UnicodeError:
            raise Refused("git-tree-name") from None
        if (mode not in MODES or not name or name in (".", "..")
                or "/" in name or "\x00" in name or name in entries):
            raise Refused("git-tree-entry")
        entries[name] = (mode, raw[nul + 1:nul + 21].hex())
        offset = nul + 21
    if not entries:
        raise Refused("git-tree-empty")
    return entries


def _blob_for_path(port: GitObjectPort, root_oid: str,
                   path: str) -> bytes:
    tree_oid = root_oid
    parts = path.split("/")
    for index, part in enumerate(parts):
        entries = _tree_entries(_read(port, "tree", tree_oid))
        if part not in entries:
            raise Refused("git-path-missing")
        mode, oid = entries[part]
        if index < len(parts) - 1:
            if mode != b"40000":
                raise Refused("git-path-directory")
            tree_oid = oid
        else:
            if mode != b"100644":
                raise Refused("git-path-file-mode")
            return _read(port, "blob", oid)
    raise Refused("git-path-missing")


def qualify(preflight: release.PreflightResult, blobs: dict[str, bytes],
            git_port: GitObjectPort, identity_port: RepositoryIdentityPort,
            selection: dict[str, Any],
            now: dt.datetime) -> TreeWitnessResult:
    """Bind exact selected source bytes to raw Git objects; never authorize."""
    selection = _exact(selection, {"repositoryId", "identityEventId",
        "sourceReaderPrincipalId", "sourceReaderCredentialId"},
        "git-selection-shape")
    if (type(preflight) is not release.PreflightResult
            or preflight.schema != release.RESULT_SCHEMA
            or preflight.authorized is not False
            or preflight.can_dispatch is not False
            or type(preflight.live_effects) is not int
            or preflight.live_effects != 0
            or not candidate._hex(preflight.coordination_revision, candidate.HEX40)
            or not candidate._hex(preflight.source_tree, candidate.HEX40)
            or not candidate._hex(preflight.archive_sha256, candidate.HEX64)
            or not candidate._hex(preflight.manifest_sha256, candidate.HEX64)
            or not _positive(preflight.artifact_id)
            or not _positive(selection["repositoryId"])
            or not _positive(selection["identityEventId"])
            or type(selection["sourceReaderPrincipalId"]) is not str
            or not selection["sourceReaderPrincipalId"]
            or not candidate._hex(selection["sourceReaderCredentialId"],
                                  candidate.HEX64)
            or git_port is None or identity_port is None
            or git_port is identity_port
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("git-selection-invalid")
    scope_before = _scope(git_port, selection["repositoryId"],
                          ["contents:read", "metadata:read"], now)
    identity_scope = _scope(identity_port, selection["repositoryId"],
                            ["metadata:read"], now)
    if (scope_before["principalId"] == selection["sourceReaderPrincipalId"]
            or scope_before["credentialId"] ==
               selection["sourceReaderCredentialId"]
            or identity_scope["principalId"] in
               (scope_before["principalId"], selection["sourceReaderPrincipalId"])
            or identity_scope["credentialId"] in
               (scope_before["credentialId"], selection["sourceReaderCredentialId"])):
        raise Refused("git-reader-custody")
    _identity(identity_port, selection["identityEventId"], identity_scope,
              selection["repositoryId"], now)
    blobs = _exact(blobs, {"archive", "workflow", "manifest",
                            "builderSource", "nativeSource"},
                   "git-source-blobs-shape")
    try:
        if (hashlib.sha256(blobs["archive"]).hexdigest() !=
                preflight.archive_sha256
                or hashlib.sha256(blobs["manifest"]).hexdigest() !=
                   preflight.manifest_sha256):
            raise Refused("git-source-digest")
        checked = bytes_check.verify(blobs["archive"], blobs["workflow"],
            blobs["manifest"], preflight.manifest_sha256,
            blobs["builderSource"], blobs["nativeSource"])
        if checked["authorized"] is not False or checked["canDispatch"] is not False:
            raise Refused("git-byte-authority")
        source_files = {
            builder.WORKFLOW: blobs["workflow"],
            builder.MANIFEST: blobs["manifest"],
            "eng/build_callable_isolated_v2_effect_scaffold.py": blobs["builderSource"],
            builder.NATIVE_SOURCE: blobs["nativeSource"]}
        with zipfile.ZipFile(io.BytesIO(blobs["archive"])) as zipped:
            for member, path in builder.MEMBERS.items():
                source_files[path] = zipped.read(member)
    except Refused:
        raise
    except Exception:
        raise Refused("git-source-byte-check") from None
    commit = _read(git_port, "commit", preflight.coordination_revision)
    if not commit.startswith(b"tree " + preflight.source_tree.encode() + b"\n"):
        raise Refused("git-commit-tree-binding")
    digests = []
    for path in sorted(source_files):
        actual = _blob_for_path(git_port, preflight.source_tree, path)
        if actual != source_files[path]:
            raise Refused("git-source-membership")
        digests.append((path, hashlib.sha256(actual).hexdigest()))
    if (_scope(git_port, selection["repositoryId"],
               ["contents:read", "metadata:read"], now) != scope_before
            or _scope(identity_port, selection["repositoryId"],
                      ["metadata:read"], now) != identity_scope):
        raise Refused("git-scope-drift")
    return TreeWitnessResult(preflight.coordination_revision,
                             preflight.source_tree, preflight.archive_sha256,
                             tuple(digests), selection["identityEventId"])
