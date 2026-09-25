"""Independent fake install approval remains read-only and closed."""

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
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_git_tree_membership as tree
import callable_isolated_v2_v5_install_approval as approval

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
    source_files = tuple(sorted((path, candidate.PINNED_BYTES[key])
                                for path, key in paths.items()))
    membership = tree.TreeWitness(selection.revision,
        selection.source_tree, source_files, 808,
        "artifact-reader", "c" * 64,
        "workflow-reader", "d" * 64,
        "git-reader", "e" * 64,
        "identity-reader", "f" * 64)
    readback = installed.Readback(selection.archive_sha256,
        selection.manifest_sha256, selection.revision, selection.artifact_id,
        700, 1, 702, selection.source_tree, selection.repository_id, 701, 703,
        "3" * 64, "4" * 64, "5" * 64, PATH,
        "2026-09-25T11:58:00Z", "2026-09-25T11:58:01Z",
        "probe-reader", "6" * 64,
        "audit-reader", "7" * 64)
    selected = {"eventId": 909, "reviewerActorId": 400}
    scope = {"principalId": "install-approval-reader",
        "credentialId": "8" * 64, "repository": candidate.REPOSITORY,
        "repositoryId": 100, "permissions": ["read-install-approval"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    record = {"schema": approval.APPROVAL_SCHEMA, "complete": True,
        "principalId": "install-approval-reader", "credentialId": "8" * 64,
        "eventId": 909, "repository": candidate.REPOSITORY,
        "repositoryId": 100, "revision": selection.revision,
        "sourceTree": selection.source_tree, "treeIdentityEventId": 808,
        "artifactId": 302, "manifestSha256": selection.manifest_sha256,
        "archiveSha256": selection.archive_sha256,
        "runnerActorId": 701, "auditActorId": 703, "auditEventId": 702,
        "imageDigest": "3" * 64, "interpreterSha256": "4" * 64,
        "runtimeClosureSha256": "5" * 64, "installPath": PATH,
        "reviewerActorId": 400, "decision": "install-no-grant-refusal",
        "reviewedAt": "2026-09-25T11:57:30Z",
        "observedAt": "2026-09-25T11:57:35Z",
        "expiresAt": "2026-09-25T12:10:00Z"}
    return selection, membership, readback, selected, Port(scope, record)


class InstallApprovalTests(unittest.TestCase):
    def observe(self, change=None):
        selection, membership, readback, selected, port = fixture()
        if change:
            change(selection, membership, readback, selected, port)
        return approval.qualify(selection, membership, readback, selected,
                                port, NOW)

    def test_matching_fake_approval_stays_closed(self):
        selection, membership, readback, selected, port = fixture()
        result = approval.qualify(selection, membership, readback, selected,
                                  port, NOW)
        self.assertEqual(port.calls, [909])
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_wrong_artifact_tree_runner_or_runtime_refuses(self):
        for key, value in (("sourceTree", "9" * 40),
                           ("artifactId", 999),
                           ("treeIdentityEventId", 999),
                           ("imageDigest", "9" * 64),
                           ("runtimeClosureSha256", "9" * 64),
                           ("installPath", "/tmp/foreign.pyz")):
            with self.subTest(key=key):
                with self.assertRaises(approval.Refused):
                    self.observe(lambda _s, _t, _r, _c, p:
                                 p.record.update({key: value}))

    def test_missing_tree_file_self_review_or_postcommand_approval_refuses(self):
        with self.assertRaises(approval.Refused):
            self.observe(lambda _s, t, _r, _c, _p:
                         object.__setattr__(t, "source_files", ()))
        with self.assertRaises(approval.Refused):
            self.observe(lambda s, _t, _r, c, p:
                         (c.update(reviewerActorId=s.producer_actor_id),
                          p.record.update(reviewerActorId=s.producer_actor_id)))
        with self.assertRaises(approval.Refused):
            self.observe(lambda _s, _t, _r, _c, p:
                         p.record.update(observedAt="2026-09-25T11:59:00Z"))

    def test_stale_broad_or_wrong_event_refuses(self):
        for key, value in (("eventId", 999),
                           ("decision", "native-effect"),
                           ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                with self.assertRaises(approval.Refused):
                    self.observe(lambda _s, _t, _r, _c, p:
                                 p.record.update({key: value}))
        with self.assertRaises(approval.Refused):
            self.observe(lambda _s, _t, _r, _c, p:
                         p.scope_data.update(permissions=["read-install-approval",
                                                          "contents:write"]))

    def test_expired_prior_selection_review_refuses(self):
        with self.assertRaises(approval.Refused):
            self.observe(lambda s, _t, _r, _c, _p:
                         object.__setattr__(s, "expires_at",
                                            "2026-09-25T11:59:00Z"))

    def test_install_approval_reader_cannot_reuse_git_reader(self):
        selection, membership, readback, selected, port = fixture()
        port.scope_data["principalId"] = "git-reader"
        port.record["principalId"] = "git-reader"
        with self.assertRaises(approval.Refused):
            approval.qualify(selection, membership, readback, selected,
                             port, NOW)

    def test_all_prior_reader_identity_collisions_refuse(self):
        for principal in ("artifact-reader", "workflow-reader",
                          "identity-reader", "probe-reader", "audit-reader"):
            with self.subTest(principal=principal):
                selection, membership, readback, selected, port = fixture()
                port.scope_data["principalId"] = principal
                port.record["principalId"] = principal
                with self.assertRaises(approval.Refused):
                    approval.qualify(selection, membership, readback,
                                     selected, port, NOW)
        selection, membership, readback, selected, port = fixture()
        port.scope_data["credentialId"] = membership.git_credential_id
        port.record["credentialId"] = membership.git_credential_id
        with self.assertRaises(approval.Refused):
            approval.qualify(selection, membership, readback, selected,
                             port, NOW)
        selection, membership, readback, selected, port = fixture()
        object.__setattr__(readback, "probe_reader_principal",
                           membership.artifact_reader_principal)
        with self.assertRaises(approval.Refused):
            approval.qualify(selection, membership, readback, selected,
                             port, NOW)

    def test_no_file_token_socket_or_journal_access(self):
        selection, membership, readback, selected, port = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = approval.qualify(selection, membership, readback,
                                      selected, port, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
