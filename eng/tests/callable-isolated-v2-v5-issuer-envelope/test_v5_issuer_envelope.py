"""Canonical issuer envelope with a fake signature-verification port."""

import base64
import copy
import datetime as dt
import hashlib
import json
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
import callable_isolated_v2_v5_issuer_envelope as envelope

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
PATH = "/opt/fsgg/v5/fsgg-callable-isolated-v2-v5-no-grant.pyz"


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                       ensure_ascii=True) + "\n").encode("ascii")


class FakeVerifier:
    def __init__(self):
        self.scope_data = {"principalId": "signature-reader",
            "credentialId": "1" * 64, "repository": candidate.REPOSITORY,
            "repositoryId": 100, "permissions": ["verify-signature"],
            "keyId": "2" * 64, "algorithm": "Ed25519",
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.calls = []
        self.answer = True

    def scope(self):
        return self.scope_data

    def verify(self, key_id, algorithm, payload, signature):
        self.calls.append((key_id, algorithm, payload, signature))
        return self.answer


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
    issued = issuer.IssuerWitness(selection.revision, 700, 1, 1001, 1002,
        "2026-09-25T11:57:40Z", "2026-09-25T12:10:00Z",
        "issuer-reader", "9" * 64)
    policy = {"keyId": "2" * 64, "nonce": "5" * 64,
              "algorithm": "Ed25519",
              "audience": "fsgg.coordination.v5-no-grant-runner/1"}
    payload = {"schema": envelope.PAYLOAD_SCHEMA,
        "repository": candidate.REPOSITORY, "repositoryId": 100,
        "revision": selection.revision, "sourceTree": selection.source_tree,
        "treeIdentityEventId": 808, "artifactId": 302,
        "manifestSha256": selection.manifest_sha256,
        "archiveSha256": selection.archive_sha256,
        "installApprovalEventId": 909, "runId": 700, "runAttempt": 1,
        "runnerActorId": 701, "imageDigest": "3" * 64,
        "interpreterSha256": "4" * 64,
        "runtimeClosureSha256": "5" * 64, "installPath": PATH,
        "issuerEventId": 1001, "issuerActorId": 1002,
        "keyId": "2" * 64, "nonce": "5" * 64,
        "algorithm": "Ed25519",
        "audience": "fsgg.coordination.v5-no-grant-runner/1",
        "issuedAt": "2026-09-25T11:57:40Z",
        "expiresAt": "2026-09-25T12:10:00Z"}
    signature = bytes(range(64))
    raw = canonical({"schema": envelope.ENVELOPE_SCHEMA,
        "payload": payload,
        "signature": base64.b64encode(signature).decode("ascii")})
    return (selection, membership, readback, approval, issued, policy,
            raw, payload, FakeVerifier())


class IssuerEnvelopeTests(unittest.TestCase):
    def observe(self, change=None):
        selection, membership, readback, approval, issued, policy, raw, payload, port = fixture()
        if change:
            selection, membership, readback, approval, issued, policy, raw, payload, port = change(
                selection, membership, readback, approval, issued, policy, raw, payload, port)
        return envelope.qualify(selection, membership, readback, approval,
                                issued, policy, raw, port, NOW)

    def test_exact_payload_verifies_once_and_stays_closed(self):
        selection, membership, readback, approval, issued, policy, raw, payload, port = fixture()
        result = envelope.qualify(selection, membership, readback, approval,
                                  issued, policy, raw, port, NOW)
        self.assertEqual(len(port.calls), 1)
        self.assertEqual(port.calls[0], (policy["keyId"], "Ed25519",
            canonical(payload), bytes(range(64))))
        self.assertEqual(result.envelope_sha256, hashlib.sha256(raw).hexdigest())
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_swapped_claim_or_policy_refuses(self):
        for key, value in (("runAttempt", 2), ("runnerActorId", 999),
                           ("imageDigest", "a" * 64),
                           ("installApprovalEventId", 999),
                           ("issuerEventId", 999), ("nonce", "0" * 64),
                           ("keyId", "f" * 64)):
            with self.subTest(key=key):
                args = list(fixture())
                body = {"schema": envelope.ENVELOPE_SCHEMA,
                        "payload": copy.deepcopy(args[7]),
                        "signature": base64.b64encode(bytes(range(64))).decode()}
                body["payload"][key] = value
                args[6] = canonical(body)
                with self.assertRaises(envelope.Refused):
                    envelope.qualify(args[0], args[1], args[2], args[3],
                                     args[4], args[5], args[6], args[8], NOW)

    def test_noncanonical_duplicate_bad_signature_or_false_verifier_refuses(self):
        args = list(fixture())
        args[6] = args[6].replace(b'"schema":', b'"schema":"duplicate","schema":', 1)
        with self.assertRaises(envelope.Refused):
            envelope.qualify(args[0], args[1], args[2], args[3], args[4],
                             args[5], args[6], args[8], NOW)
        args = list(fixture())
        body = json.loads(args[6])
        body["signature"] = "@@@"
        args[6] = canonical(body)
        with self.assertRaises(envelope.Refused):
            envelope.qualify(args[0], args[1], args[2], args[3], args[4],
                             args[5], args[6], args[8], NOW)
        for answer in (1, False, None):
            with self.subTest(answer=answer):
                args = list(fixture())
                args[8].answer = answer
                with self.assertRaises(envelope.Refused):
                    envelope.qualify(args[0], args[1], args[2], args[3],
                                     args[4], args[5], args[6], args[8], NOW)

    def test_reused_verifier_reader_or_broad_scope_refuses(self):
        args = list(fixture())
        args[8].scope_data["principalId"] = args[4].reader_principal
        with self.assertRaises(envelope.Refused):
            envelope.qualify(args[0], args[1], args[2], args[3], args[4],
                             args[5], args[6], args[8], NOW)
        args = list(fixture())
        args[8].scope_data["permissions"] = ["verify-signature", "contents:write"]
        with self.assertRaises(envelope.Refused):
            envelope.qualify(args[0], args[1], args[2], args[3], args[4],
                             args[5], args[6], args[8], NOW)

    def test_no_file_token_socket_or_journal_access(self):
        selection, membership, readback, approval, issued, policy, raw, _payload, port = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = envelope.qualify(selection, membership, readback,
                approval, issued, policy, raw, port, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
