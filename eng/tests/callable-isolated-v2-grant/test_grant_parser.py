"""Synthetic, no-network controls for the proposed v2 grant parser."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_grant as grant  # noqa: E402


def fixture() -> dict:
    """Fictional identities; this is not an issued artifact or real target."""
    return {
        "schema": grant.SCHEMA,
        "state": "issued",
        "grantId": "a" * 64,
        "nonce": "b" * 64,
        "operation": {
            "identity": grant.IDENTITY,
            "id": "c" * 64,
            "method": "POST",
            "path": "repos/FS-GG/isolated-v2-fixture.invalid/pulls",
            "requestSha256": "d" * 64,
            "maxProviderWrites": 1,
        },
        "authority": {
            "repository": "FS-GG/.github",
            "workflowPath": ".github/workflows/callable-isolated-v2-execute.yml",
            "workflowRevision": "e" * 40,
            "workflowSha256": "f" * 64,
            "runId": 101,
            "runAttempt": 1,
            "environment": "callable-isolated-v2",
            "environmentId": 102,
            "approvalId": 103,
            "approvedAt": "2026-09-25T12:00:00Z",
            "expiresAt": "2026-09-25T12:20:00Z",
            "dispatchActorId": 104,
            "reviewerId": 105,
            "reviewerMembership": "active",
        },
        "source": {
            "coordinationRevision": "1" * 40,
            "operatorSha256": "2" * 64,
            "contractSha256": "3" * 64,
            "proposalSha256": "4" * 64,
            "installedCommandSha256": "5" * 64,
        },
        "target": {
            "repository": "FS-GG/isolated-v2-fixture.invalid",
            "repositoryId": 106,
            "nodeId": "R_fixture_only",
            "installationId": 107,
            "sourceRef": "refs/heads/synthetic-source",
            "sourceSha": "6" * 40,
            "baseRef": "refs/heads/main",
            "baseSha": "7" * 40,
            "prestateSha256": "8" * 64,
        },
        "credential": {
            "kind": "github-app-installation",
            "appId": 108,
            "installationId": 107,
            "repositoryIds": [106],
            "repositoryFullNames": ["FS-GG/isolated-v2-fixture.invalid"],
            "permissions": {"metadata": "read", "contents": "read", "pull_requests": "write"},
            "expiresAt": "2026-09-25T12:25:00Z",
        },
        "journal": {
            "repository": "FS-GG/.github",
            "ref": "refs/heads/fsgg/v2/journal/operation/ab",
            "generation": 1,
            "head": "9" * 40,
            "operationId": "c" * 64,
            "state": "intent-committed",
        },
    }


def replay(value: dict) -> dict:
    return {
        "schema": grant.REPLAY_SCHEMA,
        "complete": True,
        "grantId": value["grantId"],
        "operationId": value["operation"]["id"],
        "journalGeneration": value["journal"]["generation"],
        "journalHead": value["journal"]["head"],
        "used": False,
    }


def canonical(value: dict) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True, allow_nan=False).encode("ascii")


NOW = dt.datetime(2026, 9, 25, 12, 5, tzinfo=dt.timezone.utc)


class GrantParserTests(unittest.TestCase):
    def assert_refused(self, value: dict, *, expected: dict | None = None,
                       replay_value: dict | None = None, now: dt.datetime = NOW,
                       reason: str | None = None) -> None:
        baseline = fixture() if expected is None else expected
        raw = canonical(value)
        with self.assertRaises(grant.Refused) as caught:
            grant.parse_grant(raw, hashlib.sha256(raw).hexdigest(), baseline,
                              replay(baseline) if replay_value is None else replay_value, now)
        if reason is not None:
            self.assertEqual(reason, str(caught.exception))

    def test_exact_synthetic_grant_is_only_parsed_not_authorized(self):
        value = fixture()
        raw = canonical(value)
        result = grant.parse_grant(raw, hashlib.sha256(raw).hexdigest(), value, replay(value), NOW)
        self.assertEqual(result.grant_id, value["grantId"])
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_missing_and_extra_members_refuse_at_every_boundary(self):
        for path, member in [((), "nonce"), (("operation",), "requestSha256"),
                             (("authority",), "reviewerId"), (("source",), "operatorSha256"),
                             (("target",), "repositoryId"), (("credential",), "permissions"),
                             (("journal",), "generation")]:
            with self.subTest(path=path, member=member):
                value = fixture()
                node = value
                for key in path:
                    node = node[key]
                original = copy.deepcopy(node[member])
                del node[member]
                self.assert_refused(value)
                node[member] = original
                node["unexpected"] = True
                self.assert_refused(value)

    def test_duplicate_noncanonical_and_digest_drift_refuse(self):
        value = fixture()
        raw = canonical(value)
        duplicate = raw.replace(b'"state":"issued"', b'"state":"issued","state":"issued"', 1)
        with self.assertRaisesRegex(grant.Refused, "grant-duplicate-member"):
            grant.parse_grant(duplicate, hashlib.sha256(duplicate).hexdigest(), value, replay(value), NOW)
        with self.assertRaisesRegex(grant.Refused, "grant-noncanonical"):
            padded = raw + b"\n"
            grant.parse_grant(padded, hashlib.sha256(padded).hexdigest(), value, replay(value), NOW)
        with self.assertRaisesRegex(grant.Refused, "grant-payload-digest"):
            grant.parse_grant(raw, "0" * 64, value, replay(value), NOW)
        nonfinite = raw.replace(b'"runId":101', b'"runId":NaN', 1)
        with self.assertRaisesRegex(grant.Refused, "grant-nonfinite"):
            grant.parse_grant(nonfinite, hashlib.sha256(nonfinite).hexdigest(),
                              value, replay(value), NOW)

    def test_foreign_protected_bindings_refuse_even_with_new_payload_digest(self):
        changes = [
            (("authority", "runId"), 201),
            (("authority", "workflowRevision"), "0" * 40),
            (("authority", "reviewerId"), 205),
            (("source", "operatorSha256"), "0" * 64),
            (("target", "repositoryId"), 206),
            (("target", "nodeId"), "R_foreign"),
            (("target", "prestateSha256"), "0" * 64),
            (("operation", "requestSha256"), "0" * 64),
            (("journal", "head"), "0" * 40),
            (("nonce",), "0" * 64),
        ]
        for path, replacement in changes:
            with self.subTest(path=path):
                value = fixture()
                node = value
                for key in path[:-1]:
                    node = node[key]
                node[path[-1]] = replacement
                if path == ("target", "repositoryId"):
                    value["credential"]["repositoryIds"] = [replacement]
                self.assert_refused(value, reason="grant-protected-binding")

    def test_broad_effect_or_credential_refuses(self):
        changes = [
            (("operation", "maxProviderWrites"), 2),
            (("operation", "maxProviderWrites"), True),
            (("operation", "method"), "PUT"),
            (("operation", "path"), "repos/FS-GG/other/pulls"),
            (("target", "repository"), "OtherOrg/isolated-v2-fixture.invalid"),
            (("target", "sourceRef"), "refs/heads/../foreign"),
            (("target", "sourceRef"), "refs/heads/main"),
            (("credential", "repositoryIds"), [106, 206]),
            (("credential", "repositoryIds"), [True]),
            (("credential", "permissions"), {"metadata": "read", "contents": "write",
                                                 "pull_requests": "write"}),
            (("credential", "permissions"), {"metadata": "read", "contents": "read",
                                                 "pull_requests": "write", "administration": "write"}),
            (("authority", "dispatchActorId"), 105),
            (("authority", "environment"), "unprotected"),
            (("journal", "ref"), "refs/heads/fsgg/v2/journal/operation/../foreign"),
            (("credential", "expiresAt"), "2026-09-25T12:19:00Z"),
        ]
        for path, replacement in changes:
            with self.subTest(path=path):
                value = fixture()
                value[path[0]][path[1]] = replacement
                self.assert_refused(value, expected=value)

    def test_expiry_unknown_and_replay_refuse(self):
        baseline = fixture()
        self.assert_refused(baseline, expected=baseline,
                            now=dt.datetime(2026, 9, 25, 12, 20, tzinfo=dt.timezone.utc),
                            reason="grant-expired")
        value = copy.deepcopy(baseline)
        value["authority"]["expiresAt"] = "2026-09-25T12:31:00Z"
        self.assert_refused(value, expected=value, reason="grant-expired")
        for patch in ({"used": True}, {"used": None}, {"complete": False},
                      {"grantId": "0" * 64}, {"journalGeneration": 2},
                      {"journalGeneration": True}):
            with self.subTest(patch=patch):
                observation = replay(baseline)
                observation.update(patch)
                self.assert_refused(baseline, expected=baseline, replay_value=observation,
                                    reason="grant-replayed-or-unknown")
        self.assert_refused(baseline, expected=baseline, replay_value={},
                            reason="grant-replay-shape")


if __name__ == "__main__":
    unittest.main()
