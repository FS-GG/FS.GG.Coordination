"""Fake read-only issuer key registry selection and revocation checks."""

import copy
import datetime as dt
import hashlib
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_install_approval as install_approval
import callable_isolated_v2_v5_runner_issuer as issuer
import callable_isolated_v2_v5_issuer_envelope as envelope
import callable_isolated_v2_v5_key_registry as registry

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
PUBLIC_KEY = bytes(range(1, 33))
KEY_ID = hashlib.sha256(PUBLIC_KEY).hexdigest()


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
    approval = install_approval.Approval(selection.revision,
        selection.artifact_id, 909, 400,
        "2026-09-25T11:57:30Z", "2026-09-25T12:10:00Z",
        "approval-reader", "8" * 64)
    issued = issuer.IssuerWitness(selection.revision, 700, 1,
        1001, 1002, "2026-09-25T11:57:40Z",
        "2026-09-25T12:10:00Z", "issuer-reader", "9" * 64)
    checked = envelope.EnvelopeWitness("c" * 64, KEY_ID, 1001,
        "d" * 64, "signature-reader", "1" * 64)
    selected = {"registryEventId": 1100, "reviewerActorId": 1200,
                "keyId": KEY_ID}
    scope = {"principalId": "registry-reader", "credentialId": "2" * 64,
        "repository": candidate.REPOSITORY, "repositoryId": 100,
        "permissions": ["read-key-registry"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    record = {"schema": registry.RECORD_SCHEMA, "complete": True,
        "principalId": "registry-reader", "credentialId": "2" * 64,
        "eventId": 1100, "repository": candidate.REPOSITORY,
        "repositoryId": 100, "revision": selection.revision,
        "sourceTree": selection.source_tree, "artifactId": 302,
        "installApprovalEventId": 909, "issuerEventId": 1001,
        "issuerActorId": 1002, "runId": 700, "runAttempt": 1,
        "envelopeSha256": "c" * 64, "nonce": "d" * 64,
        "keyId": KEY_ID, "publicKeyBytes": PUBLIC_KEY,
        "algorithm": "Ed25519",
        "audience": "fsgg.coordination.v5-no-grant-runner/1",
        "status": "active", "revokedAt": None,
        "reviewerActorId": 1200,
        "approvedAt": "2026-09-25T11:57:35Z",
        "observedAt": "2026-09-25T11:57:41Z",
        "expiresAt": "2026-09-25T12:10:00Z"}
    return selection, approval, issued, checked, selected, Port(scope, record)


class KeyRegistryTests(unittest.TestCase):
    def observe(self, change=None):
        s, a, i, e, chosen, port = fixture()
        if change:
            change(s, a, i, e, chosen, port)
        return registry.qualify(s, a, i, e, chosen, port, NOW)

    def test_matching_fake_registry_stays_closed(self):
        s, a, i, e, chosen, port = fixture()
        result = registry.qualify(s, a, i, e, chosen, port, NOW)
        self.assertEqual(port.calls, [1100])
        self.assertEqual(result.key_id, KEY_ID)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_wrong_public_key_actor_run_or_envelope_refuses(self):
        for key, value in (("publicKeyBytes", b"X" * 32),
                           ("issuerActorId", 999), ("runAttempt", 2),
                           ("envelopeSha256", "f" * 64),
                           ("installApprovalEventId", 999),
                           ("nonce", "e" * 64)):
            with self.subTest(key=key):
                with self.assertRaises(registry.Refused):
                    self.observe(lambda _s, _a, _i, _e, _c, p:
                                 p.record.update({key: value}))

    def test_revoked_stale_or_self_review_refuses(self):
        for key, value in (("status", "revoked"),
                           ("revokedAt", "2026-09-25T11:58:00Z"),
                           ("expiresAt", "2026-09-25T11:59:00Z"),
                           ("observedAt", "2026-09-25T12:00:01Z"),
                           ("approvedAt", "2026-09-25T11:57:50Z")):
            with self.subTest(key=key):
                with self.assertRaises(registry.Refused):
                    self.observe(lambda _s, _a, _i, _e, _c, p:
                                 p.record.update({key: value}))
        with self.assertRaises(registry.Refused):
            self.observe(lambda _s, _a, i, _e, c, p:
                         (c.update(reviewerActorId=i.issuer_actor_id),
                          p.record.update(reviewerActorId=i.issuer_actor_id)))

    def test_envelope_bound_registry_observation_must_follow_signing(self):
        # The record contains the completed envelope hash, which cannot be
        # independently observed before the issuer signs that envelope.
        with self.assertRaises(registry.Refused):
            self.observe(lambda _s, _a, _i, _e, _c, p:
                         p.record.update(observedAt="2026-09-25T11:57:37Z"))

    def test_reused_reader_or_broad_scope_refuses(self):
        with self.assertRaises(registry.Refused):
            self.observe(lambda _s, _a, _i, e, _c, p:
                         (p.scope_data.update(principalId=e.verifier_reader_principal),
                          p.record.update(principalId=e.verifier_reader_principal)))
        with self.assertRaises(registry.Refused):
            self.observe(lambda _s, _a, _i, _e, _c, p:
                         p.scope_data.update(permissions=["read-key-registry",
                                                          "contents:write"]))

    def test_no_file_token_socket_or_journal_access(self):
        s, a, i, e, chosen, port = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = registry.qualify(s, a, i, e, chosen, port, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
