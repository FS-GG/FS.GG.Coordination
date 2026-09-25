"""Git object membership of closed scaffold source bytes via fake read port."""

import copy
import datetime as dt
import hashlib
import io
import os
import pathlib
import socket
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_release_preflight as release
import callable_isolated_v2_effect_git_tree_witness as witness

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def git_oid(kind, raw):
    return hashlib.sha1(kind.encode() + b" " + str(len(raw)).encode() + b"\0" + raw).hexdigest()


def git_tree(files):
    root = {}
    for path, (data, mode) in files.items():
        node = root
        parts = path.split("/")
        for part in parts[:-1]:
            node = node.setdefault(part, {})
        node[parts[-1]] = (data, mode)
    objects = {}
    def build(node):
        chunks = []
        for name in sorted(node):
            value = node[name]
            if isinstance(value, dict):
                child = build(value)
                mode = "40000"
            else:
                raw, mode = value
                child = git_oid("blob", raw)
                objects[child] = raw
            chunks.append(mode.encode() + b" " + name.encode() + b"\0" +
                          bytes.fromhex(child))
        raw = b"".join(chunks)
        oid = git_oid("tree", raw)
        objects[oid] = raw
        return oid
    return build(root), objects


class FakeGit:
    def __init__(self, objects):
        self.objects = dict(objects)
        scope = {"principalId": "git-reader", "credentialId": "2" * 64,
            "repository": release.REPOSITORY, "repositoryId": 77,
            "permissions": ["contents:read", "metadata:read"],
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.reads = []

    def scope(self):
        return copy.deepcopy(self.scopes.pop(0))

    def read_commit(self, oid):
        self.reads.append(("commit", oid))
        return self.objects[oid]

    def read_tree(self, oid):
        self.reads.append(("tree", oid))
        return self.objects[oid]

    def read_blob(self, oid):
        self.reads.append(("blob", oid))
        return self.objects[oid]


class FakeIdentity:
    def __init__(self):
        scope = {"principalId": "identity-reader", "credentialId": "3" * 64,
            "repository": release.REPOSITORY, "repositoryId": 77,
            "permissions": ["metadata:read"],
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.record = {"schema": "fsgg.coordination.callable-isolated-v2-repository-identity/1",
            "complete": True,
            "principalId": "identity-reader", "credentialId": "3" * 64,
            "eventId": 808, "repository": release.REPOSITORY,
            "repositoryId": 77, "objectFormat": "sha1",
            "observedAt": "2026-09-25T11:58:00Z"}
        self.reads = []

    def scope(self):
        return copy.deepcopy(self.scopes.pop(0))

    def read_repository_identity(self, event_id):
        self.reads.append(event_id)
        return copy.deepcopy(self.record)


def fixture(mode_override=None):
    with tempfile.TemporaryDirectory() as temporary:
        archive_path = pathlib.Path(temporary) / builder.ARCHIVE_NAME
        builder.build(archive_path)
        archive = archive_path.read_bytes()
    blobs = {"archive": archive,
        "workflow": (ROOT / builder.WORKFLOW).read_bytes(),
        "manifest": (ROOT / builder.MANIFEST).read_bytes(),
        "builderSource": (ENG / "build_callable_isolated_v2_effect_scaffold.py").read_bytes(),
        "nativeSource": (ROOT / builder.NATIVE_SOURCE).read_bytes()}
    files = {builder.WORKFLOW: (blobs["workflow"], "100644"),
        builder.MANIFEST: (blobs["manifest"], "100644"),
        "eng/build_callable_isolated_v2_effect_scaffold.py": (blobs["builderSource"], "100644"),
        builder.NATIVE_SOURCE: (blobs["nativeSource"], "100644")}
    with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
        for name, source in builder.MEMBERS.items():
            files[source] = (zipped.read(name), "100644")
    if mode_override:
        path, mode = mode_override
        raw, _ = files[path]
        files[path] = (raw, mode)
    tree, objects = git_tree(files)
    commit = b"tree " + tree.encode() + b"\nauthor Fake <fake@example.test> 0 +0000\n\nsource witness\n"
    revision = git_oid("commit", commit)
    objects[revision] = commit
    result = release.PreflightResult(revision, tree,
        hashlib.sha256(blobs["manifest"]).hexdigest(), 404,
        hashlib.sha256(archive).hexdigest(), 202, 1, 303, 606, 505)
    selection = {"repositoryId": 77, "identityEventId": 808,
        "sourceReaderPrincipalId": "source-reader",
        "sourceReaderCredentialId": "1" * 64}
    return result, blobs, selection, FakeGit(objects), FakeIdentity()


class GitTreeWitnessTests(unittest.TestCase):
    def observe(self, change=None, mode_override=None):
        result, blobs, selection, port, identity = fixture(mode_override)
        if change:
            change(result, blobs, selection, port, identity)
        return witness.qualify(result, blobs, port, identity, selection, NOW)

    def refuses(self, change=None, mode_override=None):
        with self.assertRaises(witness.Refused):
            self.observe(change, mode_override)

    def test_complete_git_objects_match_exact_scaffold_source_without_effect_ports(self):
        preflight, blobs, selection, port, identity = fixture()
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = witness.qualify(preflight, blobs, port, identity, selection, NOW)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.identity_event_id, 808)
        self.assertEqual(len(result.source_files), 7)
        self.assertFalse(hasattr(witness, "dispatch"))

    def test_wrong_commit_tree_blob_and_path_mode_refuse(self):
        self.refuses(lambda p, _b, _s, _g, _i: object.__setattr__(p, "coordination_revision", "a" * 40))
        self.refuses(lambda p, _b, _s, _g, _i: object.__setattr__(p, "source_tree", "b" * 40))
        self.refuses(lambda _p, b, _s, _g, _i: b.__setitem__("builderSource", b"foreign"))
        self.refuses(lambda _p, _b, _s, g, _i: g.objects.__setitem__(
            next(oid for kind, oid in git_objects_read_order(g) if kind == "blob"), b"foreign"))
        self.refuses(mode_override=(builder.WORKFLOW, "120000"))

    def test_scope_and_missing_object_refuse(self):
        self.refuses(lambda _p, _b, _s, g, _i: g.scopes[0].__setitem__("credentialId", "1" * 64))
        self.refuses(lambda _p, _b, _s, g, _i: g.scopes[1].__setitem__("repositoryId", 78))
        self.refuses(lambda _p, _b, _s, g, _i: g.objects.clear())

    def test_same_scope_object_mutated_during_git_read_refuses(self):
        preflight, blobs, selection, port, identity = fixture()
        shared = copy.deepcopy(port.scopes[0])
        port.scope = lambda: shared
        original_read = port.read_commit
        def drift(oid):
            raw = original_read(oid)
            shared["credentialId"] = "f" * 64
            return raw
        port.read_commit = drift
        with self.assertRaises(witness.Refused):
            witness.qualify(preflight, blobs, port, identity, selection, NOW)

    def test_independent_repository_identity_and_object_format_refuse(self):
        self.refuses(lambda _p, _b, _s, _g, i: i.record.__setitem__("objectFormat", "sha256"))
        self.refuses(lambda _p, _b, _s, _g, i: i.record.__setitem__("repositoryId", 78))
        self.refuses(lambda _p, _b, _s, _g, i: i.record.__setitem__("eventId", 809))
        self.refuses(lambda _p, _b, _s, _g, i: i.record.__setitem__("observedAt", "2026-09-25T11:00:00Z"))
        self.refuses(lambda _p, _b, _s, _g, i: i.scopes[0].__setitem__("credentialId", "2" * 64))
        self.refuses(lambda _p, _b, _s, _g, i: i.scopes[1].__setitem__("principalId", "foreign"))
        self.refuses(lambda _p, _b, _s, _g, i: i.record.pop("objectFormat"))
        self.refuses(lambda _p, _b, _s, _g, i: i.scopes[0].__setitem__("permissions", ["metadata:write"]))
        self.refuses(lambda _p, _b, _s, _g, i: i.record.__setitem__("principalId", "git-reader"))


def git_objects_read_order(port):
    # A stable blob key suffices to independently mutate one returned object.
    return [("blob", oid) for oid, raw in port.objects.items()
            if git_oid("blob", raw) == oid]


if __name__ == "__main__":
    unittest.main()
