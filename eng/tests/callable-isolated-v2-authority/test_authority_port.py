"""Synthetic source-only controls; no protected credential, journal, or provider."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import io
import os
import pathlib
import socket
import sys
import unittest
import zipfile
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests" / "callable-isolated-v2-grant"))
import callable_isolated_v2_authority as authority  # noqa: E402
from test_grant_parser import NOW, canonical, fixture, replay  # noqa: E402


def archive_of(raw: bytes, *, duplicate: bool = False) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as package:
        with package.open(authority.GRANT_MEMBER, "w", force_zip64=False) as member:
            member.write(raw)
        if duplicate:
            with patch("warnings.warn"):
                package.writestr(authority.GRANT_MEMBER, raw)
    return output.getvalue()


class Observer:
    def __init__(self, observation: dict, replay_value: dict):
        self.observation = observation
        self.replay_value = replay_value
        self.calls: list[str] = []
        self.issuer = Issuer(self)
        self.journal = ReplayReader(self)

    def observe_current(self) -> dict:
        self.calls.append("observe")
        return {key: value for key, value in self.observation.items()
                if key not in {"artifact", "issuance"}}


class Issuer:
    def __init__(self, observer: Observer):
        self.observer = observer

    def read_issuance(self) -> dict:
        self.observer.calls.append("issuance")
        return {"schema": authority.ISSUER_OBSERVATION_SCHEMA, "complete": True,
                "artifact": self.observer.observation["artifact"],
                "issuance": self.observer.observation["issuance"]}


class ReplayReader:
    def __init__(self, observer: Observer):
        self.observer = observer

    def read_replay(self, grant_id: str, operation_id: str) -> dict:
        self.observer.calls.append("replay")
        assert grant_id == self.observer.observation["issuance"]["grantId"]
        assert operation_id == self.observer.observation["operation"]["id"]
        return self.observer.replay_value


def subject() -> tuple[bytes, Observer]:
    value = fixture()
    raw = canonical(value)
    archive = archive_of(raw)
    observation = {
        "schema": authority.OBSERVATION_SCHEMA,
        "complete": True,
        "authority": copy.deepcopy(value["authority"]),
        "source": copy.deepcopy(value["source"]),
        "target": copy.deepcopy(value["target"]),
        "credential": copy.deepcopy(value["credential"]),
        "operation": copy.deepcopy(value["operation"]),
        "journal": copy.deepcopy(value["journal"]),
        "artifact": {
            "schema": authority.ARTIFACT_SCHEMA,
            "archiveSha256": hashlib.sha256(archive).hexdigest(),
            "payloadSha256": hashlib.sha256(raw).hexdigest(),
            "member": authority.GRANT_MEMBER,
            "memberCount": 1,
            "grantId": value["grantId"],
            "runId": value["authority"]["runId"],
            "runAttempt": value["authority"]["runAttempt"],
        },
        "issuance": {
            "schema": authority.ISSUE_SCHEMA,
            "state": "issued",
            "grantId": value["grantId"],
            "nonce": value["nonce"],
            "issuerActorId": 109,
            "issuanceEventId": 110,
            "issuedAt": "2026-09-25T12:03:00Z",
            "approvalId": value["authority"]["approvalId"],
            "runId": value["authority"]["runId"],
            "runAttempt": value["authority"]["runAttempt"],
            "archiveSha256": hashlib.sha256(archive).hexdigest(),
            "payloadSha256": hashlib.sha256(raw).hexdigest(),
        },
    }
    observed = {key: value for key, value in observation.items()
                if key not in {"artifact", "issuance"}}
    observation["issuance"]["issueRequestSha256"] = authority.plan_issue_request(
        observed, NOW).request_sha256
    return archive, Observer(observation, replay(value))


def readback(request: dict) -> tuple[dict, dict]:
    body = {
        "schema": authority.CAS_SCHEMA,
        "complete": True,
        "repository": request["repository"],
        "ref": request["ref"],
        "expectedGeneration": request["expectedGeneration"],
        "expectedHead": request["expectedHead"],
        "generation": request["nextGeneration"],
        "head": "a" * 40,
        "marker": copy.deepcopy(request),
    }
    return {**body, "outcome": "accepted"}, body


class AuthorityPortTests(unittest.TestCase):
    def test_exact_synthetic_observation_remains_closed_and_reads_no_token(self):
        archive, observer = subject()
        with patch.object(os, "getenv", side_effect=AssertionError("token read")), \
             patch.object(socket, "socket", side_effect=AssertionError("network")), \
             patch("builtins.open", side_effect=AssertionError("file or journal IO")):
            result = authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)
        self.assertEqual(["observe", "issuance", "replay"], observer.calls)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(0, result.live_effects)
        self.assertEqual(observer.observation["issuance"]["grantId"], result.grant_id)
        issue = authority.plan_issue_request(observer.observe_current(), NOW)
        self.assertEqual("candidate", issue.request["state"])
        self.assertFalse(issue.authorized)
        self.assertFalse(issue.can_dispatch)

    def test_observer_absent_unavailable_or_incomplete_refuses_before_replay(self):
        archive, observer = subject()
        with self.assertRaisesRegex(authority.Refused, "port-custody"):
            authority.verify_issued_candidate(archive, None, observer.issuer, observer.journal, NOW)
        with self.assertRaisesRegex(authority.Refused, "port-custody"):
            authority.verify_issued_candidate(archive, observer, observer, observer.journal, NOW)
        observer.observation["complete"] = False
        with self.assertRaisesRegex(authority.Refused, "observation-incomplete"):
            authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)
        self.assertEqual(["observe"], observer.calls)

    def test_foreign_issuance_artifact_and_protected_fields_refuse(self):
        changes = [
            ("issuance", "issuerActorId", 104),
            ("issuance", "issuerActorId", 105),
            ("issuance", "runId", 999),
            ("issuance", "runAttempt", True),
            ("issuance", "approvalId", 999),
            ("issuance", "issueRequestSha256", "0" * 64),
            ("issuance", "issuedAt", "2026-09-25T11:59:59Z"),
            ("issuance", "issuedAt", "2026-09-25T12:21:00Z"),
            ("artifact", "archiveSha256", "0" * 64),
            ("artifact", "payloadSha256", "0" * 64),
            ("artifact", "memberCount", 2),
            ("source", "operatorSha256", "0" * 64),
            ("target", "repositoryId", 999),
            ("credential", "repositoryIds", [106, 999]),
            ("authority", "reviewerId", 104),
        ]
        for section, key, replacement in changes:
            with self.subTest(section=section, key=key, value=replacement):
                archive, observer = subject()
                observer.observation[section][key] = replacement
                with self.assertRaises(authority.Refused):
                    authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)

    def test_issuance_shape_replay_and_grant_archive_refuse(self):
        archive, observer = subject()
        del observer.observation["issuance"]["issuanceEventId"]
        with self.assertRaisesRegex(authority.Refused, "issuance-shape"):
            authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)

        archive, observer = subject()
        observer.replay_value["used"] = True
        with self.assertRaisesRegex(authority.Refused, "replayed-or-unknown"):
            authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)

        archive, observer = subject()
        with self.assertRaisesRegex(authority.Refused, "artifact-binding"):
            authority.verify_issued_candidate(archive + b"tamper", observer, observer.issuer, observer.journal, NOW)

        archive, observer = subject()
        raw = canonical(fixture())
        duplicate = archive_of(raw, duplicate=True)
        observer.observation["artifact"]["archiveSha256"] = hashlib.sha256(duplicate).hexdigest()
        observer.observation["issuance"]["archiveSha256"] = hashlib.sha256(duplicate).hexdigest()
        with self.assertRaisesRegex(authority.Refused, "artifact-member"):
            authority.verify_issued_candidate(duplicate, observer, observer.issuer, observer.journal, NOW)

    def test_exact_reservation_is_only_a_plan_and_readback_remains_closed(self):
        archive, observer = subject()
        verified = authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW)
        candidate = authority.plan_attempt_reservation(verified)
        self.assertEqual("attempt-may-have-started", candidate.request["state"])
        self.assertEqual(2, candidate.request["nextGeneration"])
        self.assertFalse(candidate.can_dispatch)
        cas, check = readback(candidate.request)
        result = authority.verify_attempt_readback(candidate, cas, check)
        self.assertEqual(2, result.generation)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(0, result.live_effects)

    def test_cas_conflict_loss_and_foreign_readback_refuse(self):
        archive, observer = subject()
        candidate = authority.plan_attempt_reservation(
            authority.verify_issued_candidate(archive, observer, observer.issuer, observer.journal, NOW))
        changes = [
            ("cas", "outcome", "response-unknown"),
            ("cas", "head", "b" * 40),
            ("readback", "complete", False),
            ("readback", "head", "b" * 40),
            ("readback", "generation", 3),
            ("readback", "expectedGeneration", True),
            ("readback", "expectedHead", "b" * 40),
        ]
        for side, key, replacement in changes:
            with self.subTest(side=side, key=key):
                cas, check = readback(candidate.request)
                (cas if side == "cas" else check)[key] = replacement
                with self.assertRaises(authority.Refused):
                    authority.verify_attempt_readback(candidate, cas, check)
        cas, check = readback(candidate.request)
        check["marker"]["grantId"] = "0" * 64
        with self.assertRaisesRegex(authority.Refused, "readback-binding"):
            authority.verify_attempt_readback(candidate, cas, check)
        cas, check = readback(candidate.request)
        check["marker"]["runAttempt"] = True
        with self.assertRaisesRegex(authority.Refused, "readback-binding"):
            authority.verify_attempt_readback(candidate, cas, check)

    def test_no_journal_file_io_or_dispatch_from_reservation(self):
        archive, observer = subject()
        with patch("builtins.open", side_effect=AssertionError("journal IO")), \
             patch.object(socket, "socket", side_effect=AssertionError("provider")):
            candidate = authority.plan_attempt_reservation(
                authority.verify_issued_candidate(archive, observer, observer.issuer,
                                                  observer.journal, NOW))
            cas, check = readback(candidate.request)
            result = authority.verify_attempt_readback(candidate, cas, check)
        self.assertFalse(candidate.can_dispatch)
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
