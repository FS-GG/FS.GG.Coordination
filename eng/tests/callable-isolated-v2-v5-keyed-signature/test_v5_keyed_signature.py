"""Fake verifier receives the exact public key selected by the registry."""

import base64
import dataclasses
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_v5_issuer_envelope as envelope
import callable_isolated_v2_v5_key_registry as registry
import callable_isolated_v2_v5_keyed_signature as keyed
import callable_isolated_v2_v5_no_grant_selection as candidate

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
PUBLIC_KEY = bytes(range(1, 33))
KEY_ID = hashlib.sha256(PUBLIC_KEY).hexdigest()
SIGNATURE = bytes(range(64))


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                       ensure_ascii=True) + "\n").encode("ascii")


class Port:
    def __init__(self):
        self.scope_data = {"principalId": "keyed-verifier",
            "credentialId": "3" * 64, "repository": candidate.REPOSITORY,
            "repositoryId": 100, "permissions": ["verify-signature"],
            "keyId": KEY_ID, "algorithm": "Ed25519",
            "expiresAt": "2026-09-25T12:10:00Z"}
        self.calls = []
        self.answer = True

    def scope(self):
        return self.scope_data

    def verify(self, public_key, algorithm, payload, signature):
        self.calls.append((public_key, algorithm, payload, signature))
        return self.answer


def fixture():
    payload = {name: "x" for name in envelope.PAYLOAD}
    payload.update(schema=envelope.PAYLOAD_SCHEMA, keyId=KEY_ID,
                   nonce="d" * 64, issuerEventId=1001,
                   algorithm="Ed25519", audience=envelope.AUDIENCE)
    raw = canonical({"schema": envelope.ENVELOPE_SCHEMA,
                     "payload": payload,
                     "signature": base64.b64encode(SIGNATURE).decode("ascii")})
    checked = envelope.EnvelopeWitness(hashlib.sha256(raw).hexdigest(),
        KEY_ID, 1001, "d" * 64, "signature-reader", "1" * 64)
    key = registry.KeyWitness(KEY_ID, 1100, 1200, KEY_ID, PUBLIC_KEY,
        100, "registry-reader", "2" * 64,
        "2026-09-25T11:57:41Z", "2026-09-25T12:10:00Z")
    return checked, key, raw, payload, Port()


class KeyedSignatureTests(unittest.TestCase):
    def test_exact_registry_key_and_payload_stay_closed(self):
        checked, key, raw, payload, port = fixture()
        result = keyed.qualify(checked, key, raw, port, NOW)
        self.assertEqual(port.calls, [(PUBLIC_KEY, "Ed25519",
                                      canonical(payload), SIGNATURE)])
        self.assertEqual(result.key_id, KEY_ID)
        self.assertEqual(result.envelope_sha256, checked.envelope_sha256)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_wrong_key_or_envelope_cannot_rebind_fake_true(self):
        for field, value in (("public_key_bytes", b"X" * 32),
                             ("public_key_sha256", "f" * 64),
                             ("key_id", "f" * 64),
                             ("envelope_sha256", "f" * 64),
                             ("nonce", "e" * 64)):
            with self.subTest(field=field):
                checked, key, raw, _payload, port = fixture()
                if hasattr(key, field):
                    key = dataclasses.replace(key, **{field: value})
                else:
                    checked = dataclasses.replace(
                        checked, **{field: value})
                with self.assertRaises(keyed.Refused):
                    keyed.qualify(checked, key, raw, port, NOW)
                self.assertEqual(port.calls, [])

    def test_duplicate_payload_or_wrong_signature_shape_refuses(self):
        checked, key, raw, payload, port = fixture()
        raw = raw.replace(b'"nonce":', b'"nonce":"duplicate","nonce":', 1)
        checked = dataclasses.replace(
            checked, envelope_sha256=hashlib.sha256(raw).hexdigest())
        with self.assertRaises(keyed.Refused):
            keyed.qualify(checked, key, raw, port, NOW)
        self.assertEqual(port.calls, [])
        checked, key, _raw, payload, port = fixture()
        raw = canonical({"schema": envelope.ENVELOPE_SCHEMA,
                         "payload": payload, "signature": "AAAA"})
        checked = dataclasses.replace(
            checked, envelope_sha256=hashlib.sha256(raw).hexdigest())
        with self.assertRaises(keyed.Refused):
            keyed.qualify(checked, key, raw, port, NOW)

    def test_false_or_broad_or_reused_verifier_refuses(self):
        for answer in (False, 1, None):
            checked, key, raw, _payload, port = fixture()
            port.answer = answer
            with self.subTest(answer=answer), self.assertRaises(keyed.Refused):
                keyed.qualify(checked, key, raw, port, NOW)
        for change in (lambda p: p.scope_data.update(
                           permissions=["verify-signature", "contents:write"]),
                       lambda p: p.scope_data.update(
                           principalId="registry-reader"),
                       lambda p: p.scope_data.update(
                           credentialId="2" * 64)):
            checked, key, raw, _payload, port = fixture()
            change(port)
            with self.assertRaises(keyed.Refused):
                keyed.qualify(checked, key, raw, port, NOW)
            self.assertEqual(port.calls, [])

    def test_expired_registry_or_zero_authority_refuses(self):
        checked, key, raw, _payload, port = fixture()
        key = dataclasses.replace(
            key, expires_at="2026-09-25T11:59:00Z")
        with self.assertRaises(keyed.Refused):
            keyed.qualify(checked, key, raw, port, NOW)
        checked, key, raw, _payload, port = fixture()
        key = dataclasses.replace(key, live_effects=1)
        with self.assertRaises(keyed.Refused):
            keyed.qualify(checked, key, raw, port, NOW)

    def test_no_file_token_socket_or_journal_access(self):
        checked, key, raw, _payload, port = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = keyed.qualify(checked, key, raw, port, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
