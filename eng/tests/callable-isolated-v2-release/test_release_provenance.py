"""Fictional, no-network controls for the proposed protected release packet."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import os
import pathlib
import socket
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_release_provenance as release  # noqa: E402

NOW = dt.datetime(2026, 9, 25, 12, 5, tzinfo=dt.timezone.utc)


def fixture() -> dict:
    """Synthetic future coordinates; none is an approved release or selected runner."""
    source = {"repository": "FS-GG/FS.GG.Coordination", "revision": "a" * 40,
              "tree": "b" * 40, "protectedMain": True,
              "files": copy.deepcopy(release.PINNED_SOURCE),
              "releaseVerifierSha256": "c" * 64}
    run = {"repository": source["repository"], "workflowPath": release.WORKFLOW,
           "revision": source["revision"], "ref": "refs/heads/main",
           "event": "workflow_dispatch", "runId": 201, "runAttempt": 1,
           "environment": release.ENVIRONMENT, "environmentId": 202,
           "actorId": 203, "executionTokenPresent": False}
    return {
        "schema": release.SCHEMA, "state": "prepared-not-authorized",
        "source": source,
        "workflow": {"repository": source["repository"], "path": release.WORKFLOW,
                     "revision": source["revision"], "sha256": "d" * 64},
        "artifact": {"name": release.ARCHIVE,
                     "archiveSha256": release.ARCHIVE_SHA256,
                     "manifestSha256": release.ZIPAPP_MANIFEST_SHA256,
                     "producerRunId": 204, "artifactId": 205,
                     "sourceRevision": source["revision"],
                     "downloadReadbackSha256": release.ARCHIVE_SHA256},
        "runner": {"image": "ghcr.io/fs-gg/isolated-v2-fixture@sha256:" + "e" * 64,
                   "platform": "linux/amd64", "imageAttestationSha256": "f" * 64,
                   "ephemeral": True, "readOnlyRoot": True},
        "runtime": {"interpreterPath": "/opt/fsgg/python/bin/python3.14",
                    "interpreterSha256": "1" * 64, "interpreterVersion": "3.14.7",
                    "flags": ["-I", "-S"], "closureManifestSha256": "2" * 64,
                    "stdlibTreeSha256": "3" * 64, "mappedFileCount": 17},
        "run": run,
        "approval": {"schema": release.APPROVAL_SCHEMA, "complete": True,
                     "approvalId": 206, "runId": run["runId"],
                     "runAttempt": run["runAttempt"],
                     "environmentId": run["environmentId"], "reviewerId": 207,
                     "reviewerMembership": "active", "approvedAt": "2026-09-25T12:00:00Z",
                     "expiresAt": "2026-09-25T12:20:00Z"},
        "permissions": copy.deepcopy(release.PERMISSIONS),
    }


def canonical(value: dict) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True, allow_nan=False).encode("ascii")


class ReleaseObserver:
    def __init__(self, value: dict):
        self.value = value
        self.calls = 0

    def read_current(self) -> dict:
        self.calls += 1
        return {"schema": release.OBSERVATION_SCHEMA, "complete": True,
                **{key: copy.deepcopy(self.value[key]) for key in
                   ("source", "workflow", "artifact", "runner", "runtime", "run", "permissions")}}


class ApprovalObserver:
    def __init__(self, value: dict):
        self.value = value
        self.calls = 0

    def read_approval(self) -> dict:
        self.calls += 1
        return copy.deepcopy(self.value["approval"])


def verify(value: dict, *, expected: dict | None = None,
           approved: dict | None = None, now: dt.datetime = NOW):
    raw = canonical(value)
    return release.verify_prepared_packet(raw, hashlib.sha256(raw).hexdigest(),
                                          ReleaseObserver(value if expected is None else expected),
                                          ApprovalObserver(value if approved is None else approved), now)


class ReleaseProvenanceTests(unittest.TestCase):
    def test_exact_synthetic_packet_is_still_not_authority_or_an_effect(self):
        value = fixture()
        raw = canonical(value)
        observer = ReleaseObserver(value)
        reviewer = ApprovalObserver(value)
        with patch("builtins.open", side_effect=AssertionError("file or journal IO")), \
             patch.object(os, "getenv", side_effect=AssertionError("token read")), \
             patch.object(socket, "socket", side_effect=AssertionError("provider")):
            result = release.verify_prepared_packet(raw, hashlib.sha256(raw).hexdigest(),
                                                    observer, reviewer, NOW)
        self.assertEqual(1, observer.calls)
        self.assertEqual(1, reviewer.calls)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(0, result.live_effects)
        self.assertEqual(value["source"]["revision"], result.source_revision)

    def test_old_workflow_mutable_runner_local_digest_and_broad_grants_refuse(self):
        changes = [
            ("workflow", "path", ".github/workflows/callable-cli-release-publish.yml"),
            ("workflow", "revision", "0" * 40),
            ("artifact", "archiveSha256", "0" * 64),
            ("artifact", "downloadReadbackSha256", "0" * 64),
            ("runner", "image", "ubuntu-latest"),
            ("runner", "image", "ghcr.io/../foreign@sha256:" + "e" * 64),
            ("runner", "readOnlyRoot", False),
            ("runtime", "closureManifestSha256", release.LOCAL_CLOSURE_SHA256),
            ("runtime", "interpreterSha256", "invalid"),
            ("runtime", "flags", ["-I"]),
            ("run", "executionTokenPresent", True),
            ("run", "environment", "unprotected"),
            ("approval", "reviewerId", 203),
            ("approval", "reviewerMembership", "unknown"),
        ]
        for section, key, replacement in changes:
            with self.subTest(section=section, key=key):
                value = fixture()
                value[section][key] = replacement
                with self.assertRaises(release.Refused):
                    verify(value)
        value = fixture()
        value["permissions"] = {"contents": "write", "actions": "read"}
        with self.assertRaisesRegex(release.Refused, "permissions"):
            verify(value)

    def test_missing_extra_duplicate_noncanonical_and_digest_refuse(self):
        for section, key in [((), "workflow"), (("source",), "tree"),
                             (("artifact",), "artifactId"), (("runner",), "image"),
                             (("runtime",), "interpreterPath"), (("run",), "runAttempt"),
                             (("approval",), "approvalId")]:
            with self.subTest(section=section, key=key):
                value = fixture()
                node = value if not section else value[section[0]]
                original = node.pop(key)
                with self.assertRaises(release.Refused):
                    verify(value)
                node[key] = original
                node["extra"] = True
                with self.assertRaises(release.Refused):
                    verify(value)
        value = fixture()
        raw = canonical(value)
        with self.assertRaisesRegex(release.Refused, "packet-digest"):
            release.verify_prepared_packet(raw, "0" * 64, ReleaseObserver(value),
                                           ApprovalObserver(value), NOW)
        padded = raw + b"\n"
        with self.assertRaisesRegex(release.Refused, "noncanonical"):
            release.verify_prepared_packet(padded, hashlib.sha256(padded).hexdigest(),
                                           ReleaseObserver(value), ApprovalObserver(value), NOW)
        duplicate = raw.replace(b'"state":"prepared-not-authorized"',
                                b'"state":"prepared-not-authorized","state":"prepared-not-authorized"', 1)
        with self.assertRaisesRegex(release.Refused, "duplicate-member"):
            release.verify_prepared_packet(duplicate, hashlib.sha256(duplicate).hexdigest(),
                                           ReleaseObserver(value), ApprovalObserver(value), NOW)

    def test_foreign_observations_and_expiry_refuse(self):
        value = fixture()
        expected = fixture()
        expected["source"]["revision"] = "0" * 40
        with self.assertRaisesRegex(release.Refused, "protected-binding"):
            verify(value, expected=expected)
        approved = fixture()
        approved["approval"]["approvalId"] = 999
        with self.assertRaisesRegex(release.Refused, "protected-binding"):
            verify(value, approved=approved)
        approved = fixture()
        approved["approval"]["runAttempt"] = True
        with self.assertRaisesRegex(release.Refused, "protected-binding"):
            verify(value, approved=approved)
        expected = fixture()
        expected["run"]["runAttempt"] = True
        with self.assertRaisesRegex(release.Refused, "protected-binding"):
            verify(value, expected=expected)
        with self.assertRaisesRegex(release.Refused, "approval-time"):
            verify(value, now=dt.datetime(2026, 9, 25, 12, 20, tzinfo=dt.timezone.utc))
        raw = canonical(value)
        observer = ReleaseObserver(value)
        with self.assertRaisesRegex(release.Refused, "observer-custody"):
            release.verify_prepared_packet(raw, hashlib.sha256(raw).hexdigest(),
                                           observer, observer, NOW)
        observer.read_current = lambda: {"schema": release.OBSERVATION_SCHEMA,
                                         "complete": False}
        with self.assertRaises(release.Refused):
            release.verify_prepared_packet(raw, hashlib.sha256(raw).hexdigest(),
                                           observer, ApprovalObserver(value), NOW)


if __name__ == "__main__":
    unittest.main()
