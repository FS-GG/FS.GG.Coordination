"""Independent synthetic inversions for the non-authorizing effect candidate."""

import copy
import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import socket
import sqlite3
import unittest
from unittest import mock

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "callable_isolated_v2_effect_candidate.py"
SPEC = importlib.util.spec_from_file_location("effect_candidate", SOURCE)
candidate = importlib.util.module_from_spec(SPEC)
import sys
sys.modules[SPEC.name] = candidate
SPEC.loader.exec_module(candidate)

NOW = dt.datetime(2026, 9, 25, 12, 0, tzinfo=dt.timezone.utc)


def packet():
    return {
        "schema": candidate.SCHEMA,
        "state": "prepared-not-authorized",
        "source": {
            "coordinationRevision": "a" * 40, "sourceTree": "b" * 40,
            "operatorSha256": "c" * 64, "controlsSha256": "d" * 64,
            "effectArchiveSha256": "e" * 64, "workflowRevision": "f" * 40,
            "workflowPath": candidate.WORKFLOW_PATH, "workflowSha256": "1" * 64,
            "producerRunId": 99, "artifactId": 100, "producerActorId": 98,
        },
        "runtime": {
            "runnerImage": "ghcr.io/fs-gg/isolated-v2@sha256:" + "2" * 64,
            "imageAttestationSha256": "3" * 64,
            "interpreterSha256": "4" * 64, "closureSha256": "5" * 64,
        },
        "review": {
            "repository": "FS-GG/.github", "runId": 101, "runAttempt": 1,
            "environmentId": 102, "dispatchActorId": 103, "reviewerId": 104,
            "reviewEventId": 105, "membership": "active", "purpose": "source-only",
            "reviewedAt": "2026-09-25T11:55:00Z",
            "expiresAt": "2026-09-25T12:20:00Z",
        },
        "target": {
            "repository": "FS-GG/synthetic-unselected", "repositoryId": 106,
            "nodeId": "R_synthetic", "installationId": 107,
            "sourceRef": "refs/heads/source", "sourceSha": "6" * 40,
            "baseRef": "refs/heads/main", "baseSha": "7" * 40,
            "prestateSha256": "8" * 64,
        },
        "operation": {
            "identity": candidate.IDENTITY, "id": "9" * 64, "method": "POST",
            "path": "repos/FS-GG/synthetic-unselected/pulls",
            "requestSha256": "a" * 64, "maxProviderWrites": 1,
        },
    }


def encoded(value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"),
                     ensure_ascii=True).encode("ascii")
    return raw, hashlib.sha256(raw).hexdigest()


class EffectCandidateTests(unittest.TestCase):
    def verify(self, value, expected=None, now=NOW):
        raw, digest = encoded(value)
        return candidate.verify_candidate(raw, digest,
                                          packet() if expected is None else expected,
                                          now)

    def assert_refused(self, value, expected=None, now=NOW):
        with self.assertRaises(candidate.Refused):
            self.verify(value, expected, now)

    def test_matching_synthetic_candidate_remains_closed_and_has_no_ports(self):
        value = packet()
        with (mock.patch("builtins.open", side_effect=AssertionError("file read")),
              mock.patch("os.getenv", side_effect=AssertionError("token env")),
              mock.patch("socket.socket", side_effect=AssertionError("socket")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = self.verify(value, copy.deepcopy(value))
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertFalse(hasattr(candidate, "issue_grant"))
        self.assertFalse(hasattr(candidate, "dispatch"))
        self.assertFalse(hasattr(candidate, "read_token"))

    def test_wrong_source_runtime_workflow_actor_target_and_operation_refuse(self):
        mutations = (
            ("source", "coordinationRevision", "b" * 40),
            ("source", "sourceTree", "c" * 40),
            ("source", "operatorSha256", "b" * 64),
            ("source", "controlsSha256", "b" * 64),
            ("source", "effectArchiveSha256", "b" * 64),
            ("source", "workflowRevision", "b" * 40),
            ("source", "workflowSha256", "b" * 64),
            ("source", "producerRunId", 201),
            ("source", "artifactId", 202),
            ("source", "producerActorId", 203),
            ("runtime", "runnerImage", "ghcr.io/fs-gg/other@sha256:" + "2" * 64),
            ("runtime", "imageAttestationSha256", "b" * 64),
            ("runtime", "interpreterSha256", "b" * 64),
            ("runtime", "closureSha256", "b" * 64),
            ("review", "runId", 201),
            ("review", "runAttempt", 2),
            ("review", "dispatchActorId", 201),
            ("review", "reviewerId", 202),
            ("review", "reviewEventId", 203),
            ("target", "repository", "FS-GG/foreign"),
            ("target", "repositoryId", 201),
            ("target", "installationId", 202),
            ("target", "prestateSha256", "b" * 64),
            ("operation", "requestSha256", "b" * 64),
        )
        expected = packet()
        for section, key, replacement in mutations:
            with self.subTest(section=section, key=key):
                value = copy.deepcopy(expected)
                value[section][key] = replacement
                self.assert_refused(value, expected)

    def test_selected_expected_number_type_aliases_refuse(self):
        value = packet()
        for section, key, alias in (
            ("source", "producerRunId", 99.0),
            ("review", "runAttempt", True),
            ("target", "repositoryId", 106.0),
            ("operation", "maxProviderWrites", True),
        ):
            with self.subTest(section=section, key=key):
                expected = copy.deepcopy(value)
                expected[section][key] = alias
                self.assert_refused(value, expected)

    def test_stale_self_review_and_wrong_effect_refuse_even_if_resealed(self):
        for section, key, replacement in (
            ("review", "reviewerId", 103),
            ("review", "membership", "unknown"),
            ("review", "purpose", "execute"),
            ("review", "expiresAt", "2026-09-25T12:40:00Z"),
            ("source", "effectArchiveSha256", candidate.INSPECT_ONLY_ARCHIVE),
            ("source", "workflowPath", ".github/workflows/callable-isolated-v2-release.yml"),
            ("runtime", "runnerImage", "ghcr.io/fs-gg/isolated-v2:latest"),
            ("operation", "method", "PUT"),
            ("operation", "maxProviderWrites", 2),
            ("operation", "path", "repos/FS-GG/foreign/pulls"),
        ):
            with self.subTest(section=section, key=key):
                value = packet()
                value[section][key] = replacement
                self.assert_refused(value, copy.deepcopy(value))
        self.assert_refused(packet(), packet(),
                            NOW + dt.timedelta(minutes=21))

    def test_missing_extra_duplicate_and_noncanonical_refuse(self):
        for section in ("source", "runtime", "review", "target", "operation"):
            value = packet()
            del value[section][next(iter(value[section]))]
            with self.subTest(section=section):
                self.assert_refused(value, copy.deepcopy(value))
        value = packet()
        value["grant"] = {"issued": True}
        self.assert_refused(value, copy.deepcopy(value))
        value = packet()
        value["state"] = "authorized"
        self.assert_refused(value, copy.deepcopy(value))
        raw, digest = encoded(packet())
        with self.assertRaisesRegex(candidate.Refused, "candidate-input"):
            candidate.verify_candidate(raw, "0" * 64, packet(), NOW)
        with self.assertRaisesRegex(candidate.Refused, "candidate-noncanonical"):
            candidate.verify_candidate(raw + b"\n", hashlib.sha256(raw + b"\n").hexdigest(),
                                       packet(), NOW)
        duplicate = raw[:-1] + b',"state":"prepared-not-authorized"}'
        with self.assertRaisesRegex(candidate.Refused, "candidate-duplicate-member"):
            candidate.verify_candidate(duplicate, hashlib.sha256(duplicate).hexdigest(),
                                       packet(), NOW)
        nonfinite = raw[:-1] + b',"extra":NaN}'
        with self.assertRaisesRegex(candidate.Refused, "candidate-nonfinite"):
            candidate.verify_candidate(nonfinite, hashlib.sha256(nonfinite).hexdigest(),
                                       packet(), NOW)


if __name__ == "__main__":
    unittest.main()
