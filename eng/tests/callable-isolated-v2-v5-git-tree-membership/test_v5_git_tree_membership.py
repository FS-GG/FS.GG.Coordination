"""Raw Git object membership for the exact v5 no-grant source candidate."""

import copy
import datetime as dt
import hashlib
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_artifact_workflow_witness as producer
import callable_isolated_v2_v5_git_tree_membership as witness

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def oid(kind, raw):
    return hashlib.sha1(kind.encode() + b" " + str(len(raw)).encode()
                        + b"\0" + raw).hexdigest()


def tree_for(files):
    nested = {}
    for path, (raw, mode) in files.items():
        node = nested
        for part in path.split("/")[:-1]:
            node = node.setdefault(part, {})
        node[path.split("/")[-1]] = (raw, mode)
    objects = {}

    def encode(node):
        parts = []
        for name in sorted(node):
            value = node[name]
            if type(value) is dict:
                child = encode(value)
                mode = "40000"
            else:
                raw, mode = value
                child = oid("blob", raw)
                objects[child] = raw
            parts.append(mode.encode() + b" " + name.encode() + b"\0"
                         + bytes.fromhex(child))
        raw = b"".join(parts)
        tree_oid = oid("tree", raw)
        objects[tree_oid] = raw
        return tree_oid

    return encode(nested), objects


class GitPort:
    def __init__(self, objects):
        self.objects = objects
        self.scope_data = {"principalId": "git-reader",
            "credentialId": "8" * 64, "repository": candidate.REPOSITORY,
            "repositoryId": 100,
            "permissions": ["contents:read", "metadata:read"],
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.calls = []

    def scope(self):
        return self.scope_data

    def read_commit(self, selected):
        self.calls.append(("commit", selected))
        return self.objects[selected]

    def read_tree(self, selected):
        self.calls.append(("tree", selected))
        return self.objects[selected]

    def read_blob(self, selected):
        self.calls.append(("blob", selected))
        return self.objects[selected]


class IdentityPort:
    def __init__(self):
        self.scope_data = {"principalId": "identity-reader",
            "credentialId": "9" * 64, "repository": candidate.REPOSITORY,
            "repositoryId": 100, "permissions": ["metadata:read"],
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.record = {"schema": witness.IDENTITY_SCHEMA, "complete": True,
            "principalId": "identity-reader", "credentialId": "9" * 64,
            "eventId": 808, "repository": candidate.REPOSITORY,
            "repositoryId": 100, "objectFormat": "sha1",
            "observedAt": "2026-09-25T11:59:00Z"}
        self.calls = []

    def scope(self):
        return self.scope_data

    def read_repository_identity(self, event_id):
        self.calls.append(event_id)
        return copy.deepcopy(self.record)


def fixture(mode_override=None):
    sources = {"entry": (ROOT / builder.ENTRY_SOURCE).read_bytes(),
        "builder": (ENG / "build_callable_isolated_v2_v5_no_grant.py").read_bytes(),
        "verifier": (ENG / "verify_callable_isolated_v2_v5_no_grant.py").read_bytes(),
        "manifest": (ROOT / builder.MANIFEST).read_bytes(),
        "workflow": (ROOT / builder.WORKFLOW).read_bytes()}
    sources["archive"] = builder._archive(sources["entry"])
    paths = {builder.ENTRY_SOURCE: "entry",
        "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
        "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
        builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}
    files = {path: (sources[key], "100644") for path, key in paths.items()}
    if mode_override:
        path, mode = mode_override
        files[path] = (files[path][0], mode)
    root, objects = tree_for(files)
    commit = (b"tree " + root.encode() + b"\nauthor Fake <fake@example.test> "
              b"0 +0000\n\nv5 source\n")
    revision = oid("commit", commit)
    objects[revision] = commit
    selection = candidate.Selection(
        candidate.PINNED_BYTES["archive"], candidate.PINNED_BYTES["manifest"],
        revision, root, 300, 1, 305, 100, 302, 301, 304, 303,
        "2026-09-25T11:57:00Z", "2026-09-25T12:10:00Z",
        "source-reader", "a" * 64, "review-reader", "b" * 64)
    prior = producer.Witness(revision, root, 302,
        candidate.PINNED_BYTES["archive"],
        candidate.PINNED_BYTES["workflow"],
        "artifact-reader", "6" * 64,
        "workflow-reader", "7" * 64)
    return selection, prior, sources, GitPort(objects), IdentityPort()


class GitTreeMembershipTests(unittest.TestCase):
    def test_exact_fake_tree_stays_closed(self):
        selection, prior, sources, git, identity = fixture()
        result = witness.qualify(selection, prior, sources, git, identity,
                                 808, NOW)
        self.assertEqual(result.revision, selection.revision)
        self.assertEqual(len(result.source_files), 5)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_blob_commit_tree_or_mode_refuses(self):
        for change in ("blob", "commit", "tree"):
            with self.subTest(change=change):
                selection, prior, sources, git, identity = fixture()
                if change == "blob":
                    sources["verifier"] += b"foreign"
                elif change == "commit":
                    object.__setattr__(selection, "revision", "1" * 40)
                else:
                    object.__setattr__(selection, "source_tree", "2" * 40)
                with self.assertRaises(witness.Refused):
                    witness.qualify(selection, prior, sources, git, identity,
                                    808, NOW)
        selection, prior, sources, git, identity = fixture(
            (builder.WORKFLOW, "120000"))
        with self.assertRaises(witness.Refused):
            witness.qualify(selection, prior, sources, git, identity, 808, NOW)

    def test_foreign_format_reader_or_event_refuses(self):
        for key, value in (("objectFormat", "sha256"), ("eventId", 809)):
            selection, prior, sources, git, identity = fixture()
            identity.record[key] = value
            with self.assertRaises(witness.Refused):
                witness.qualify(selection, prior, sources, git, identity,
                                808, NOW)
        selection, prior, sources, git, identity = fixture()
        git.scope_data["principalId"] = prior.artifact_reader_principal
        with self.assertRaises(witness.Refused):
            witness.qualify(selection, prior, sources, git, identity, 808, NOW)

    def test_conflicting_duplicate_commit_tree_header_refuses(self):
        selection, prior, sources, git, identity = fixture()
        commit = (b"tree " + selection.source_tree.encode() + b"\n"
                  + b"tree " + b"f" * 40 + b"\n"
                  + b"author Fake <fake@example.test> 0 +0000\n\nbody\n")
        revision = oid("commit", commit)
        git.objects[revision] = commit
        object.__setattr__(selection, "revision", revision)
        object.__setattr__(prior, "revision", revision)
        with self.assertRaises(witness.Refused):
            witness.qualify(selection, prior, sources, git, identity, 808, NOW)

    def test_no_file_token_socket_or_journal_access(self):
        selection, prior, sources, git, identity = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = witness.qualify(selection, prior, sources, git, identity,
                                     808, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
