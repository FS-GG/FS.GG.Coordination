"""Injected runner issuer event custody for the closed v5 refusal path."""

import copy
import datetime as dt
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_git_tree_membership as tree
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_install_approval as install_approval
import callable_isolated_v2_v5_runner_issuer as issuer

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
PATH = "/opt/fsgg/v5/fsgg-callable-isolated-v2-v5-no-grant.pyz"


class Port:
    def __init__(self, scope, record):
        self.scope_data = scope
        self.record = record
        self.calls = []

    def scope(self):
        return self.scope_data

    def read(self, event_id):
        self.calls.append(event_id)
        return copy.deepcopy(self.record)


def fixture():
    selection = candidate.Selection(
        candidate.PINNED_BYTES["archive"], candidate.PINNED_BYTES["manifest"],
        "1" * 40, "2" * 40, 300, 1, 305, 100, 302, 301, 304, 303,
        "2026-09-25T11:57:00Z", "2026-09-25T12:10:00Z",
        "source-reader", "a" * 64, "review-reader", "b" * 64)
    paths = {builder.ENTRY_SOURCE: "entry",
        "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
        "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
        builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}
    files = tuple(sorted((path, candidate.PINNED_BYTES[key])
                         for path, key in paths.items()))
    membership = tree.TreeWitness(selection.revision, selection.source_tree,
        files, 808, "artifact-reader", "c" * 64,
        "workflow-reader", "d" * 64, "git-reader", "e" * 64,
        "identity-reader", "f" * 64)
    readback = installed.Readback(selection.archive_sha256,
        selection.manifest_sha256, selection.revision, selection.artifact_id,
        700, 1, 702, selection.source_tree, selection.repository_id,
        701, 703, "3" * 64, "4" * 64, "5" * 64, PATH,
        "2026-09-25T11:58:00Z", "2026-09-25T11:58:01Z",
        "probe-reader", "6" * 64, "audit-reader", "7" * 64)
    approval = install_approval.Approval(selection.revision,
        selection.artifact_id, 909, 400,
        "2026-09-25T11:57:30Z", "2026-09-25T12:10:00Z",
        "approval-reader", "8" * 64)
    selected = {"eventId": 1001, "issuerActorId": 1002}
    scope = {"principalId": "issuer-reader", "credentialId": "9" * 64,
        "repository": candidate.REPOSITORY, "repositoryId": 100,
        "permissions": ["read-run-attestation"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    record = {"schema": issuer.ISSUER_SCHEMA, "complete": True,
        "principalId": "issuer-reader", "credentialId": "9" * 64,
        "eventId": 1001, "issuerActorId": 1002,
        "repository": candidate.REPOSITORY, "repositoryId": 100,
        "revision": selection.revision, "sourceTree": selection.source_tree,
        "artifactId": 302, "installApprovalEventId": 909,
        "runId": 700, "runAttempt": 1, "runnerActorId": 701,
        "imageDigest": "3" * 64, "interpreterSha256": "4" * 64,
        "runtimeClosureSha256": "5" * 64, "installPath": PATH,
        "issuedAt": "2026-09-25T11:57:40Z",
        "observedAt": "2026-09-25T11:57:45Z",
        "expiresAt": "2026-09-25T12:10:00Z"}
    return selection, membership, readback, approval, selected, Port(scope, record)


class RunnerIssuerTests(unittest.TestCase):
    def observe(self, change=None):
        selection, membership, readback, approval, selected, port = fixture()
        if change:
            change(selection, membership, readback, approval, selected, port)
        return issuer.qualify(selection, membership, readback, approval,
                              selected, port, NOW)

    def test_matching_fake_issuer_stays_closed(self):
        selection, membership, readback, approval, selected, port = fixture()
        result = issuer.qualify(selection, membership, readback, approval,
                                selected, port, NOW)
        self.assertEqual(port.calls, [1001])
        self.assertEqual(result.issued_at, "2026-09-25T11:57:40Z")
        self.assertEqual(result.reader_principal, "issuer-reader")
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_foreign_run_attempt_image_or_approval_refuses(self):
        for key, value in (("runAttempt", 2),
                           ("runnerActorId", 999),
                           ("imageDigest", "a" * 64),
                           ("runtimeClosureSha256", "a" * 64),
                           ("installApprovalEventId", 999),
                           ("installPath", "/tmp/foreign.pyz")):
            with self.subTest(key=key):
                with self.assertRaises(issuer.Refused):
                    self.observe(lambda _s, _m, _r, _a, _c, p:
                                 p.record.update({key: value}))

    def test_stale_self_issued_or_postcommand_event_refuses(self):
        with self.assertRaises(issuer.Refused):
            self.observe(lambda _s, _m, _r, _a, c, p:
                         (c.update(issuerActorId=701),
                          p.record.update(issuerActorId=701)))
        for key, value in (("issuedAt", "2026-09-25T11:59:00Z"),
                           ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                with self.assertRaises(issuer.Refused):
                    self.observe(lambda _s, _m, _r, _a, _c, p:
                                 p.record.update({key: value}))

    def test_reused_reader_or_broad_scope_refuses(self):
        with self.assertRaises(issuer.Refused):
            self.observe(lambda _s, _m, _r, a, _c, p:
                         (p.scope_data.update(principalId=a.reader_principal),
                          p.record.update(principalId=a.reader_principal)))
        with self.assertRaises(issuer.Refused):
            self.observe(lambda _s, _m, _r, _a, _c, p:
                         p.scope_data.update(permissions=["read-run-attestation",
                                                          "actions:write"]))

    def test_no_file_token_socket_or_journal_access(self):
        selection, membership, readback, approval, selected, port = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = issuer.qualify(selection, membership, readback, approval,
                                    selected, port, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
