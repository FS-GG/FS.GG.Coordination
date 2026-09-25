"""In-memory independent controls for the closed protected-observer ports."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_candidate_observers as observed

NOW = dt.datetime(2026, 9, 25, 12, 0, tzinfo=dt.timezone.utc)
BLOBS = {"operator": b"synthetic-operator", "controls": b"synthetic-controls",
         "archive": b"synthetic-effect-archive", "workflow": b"synthetic-workflow"}


def sha(value):
    return hashlib.sha256(value).hexdigest()


def fixture():
    packet = {
        "schema": candidate.SCHEMA, "state": "prepared-not-authorized",
        "source": {
            "coordinationRevision": "a" * 40, "sourceTree": "b" * 40,
            "operatorSha256": sha(BLOBS["operator"]),
            "controlsSha256": sha(BLOBS["controls"]),
            "effectArchiveSha256": sha(BLOBS["archive"]),
            "workflowRevision": "c" * 40,
            "workflowPath": candidate.WORKFLOW_PATH,
            "workflowSha256": sha(BLOBS["workflow"]),
            "producerRunId": 100, "artifactId": 101, "producerActorId": 102,
        },
        "runtime": {
            "runnerImage": "ghcr.io/fs-gg/synthetic@sha256:" + "d" * 64,
            "imageAttestationSha256": "e" * 64,
            "interpreterSha256": "f" * 64, "closureSha256": "1" * 64,
        },
        "review": {
            "repository": "FS-GG/.github", "runId": 200, "runAttempt": 1,
            "environmentId": 201, "dispatchActorId": 202, "reviewerId": 203,
            "reviewEventId": 204, "membership": "active", "purpose": "source-only",
            "reviewedAt": "2026-09-25T11:55:00Z",
            "expiresAt": "2026-09-25T12:20:00Z",
        },
        "target": {
            "repository": "FS-GG/synthetic-unselected", "repositoryId": 300,
            "nodeId": "R_synthetic", "installationId": 301,
            "sourceRef": "refs/heads/source", "sourceSha": "2" * 40,
            "baseRef": "refs/heads/main", "baseSha": "3" * 40,
            "prestateSha256": "4" * 64,
        },
        "operation": {
            "identity": candidate.IDENTITY, "id": "5" * 64, "method": "POST",
            "path": "repos/FS-GG/synthetic-unselected/pulls",
            "requestSha256": "6" * 64, "maxProviderWrites": 1,
        },
    }
    raw = json.dumps(packet, sort_keys=True, separators=(",", ":"),
                     ensure_ascii=True).encode("ascii")
    digest = sha(raw)
    facts = [
        {**{key: packet["source"][key] for key in
            ("coordinationRevision", "sourceTree", "operatorSha256",
             "controlsSha256", "effectArchiveSha256")},
         "runtime": copy.deepcopy(packet["runtime"]),
         "producerRunId": 100, "artifactId": 101, "producerActorId": 102},
        {**{key: packet["source"][key] for key in
            ("workflowRevision", "workflowPath", "workflowSha256")},
         "runId": 200, "runAttempt": 1, "dispatchActorId": 202},
        copy.deepcopy(packet["review"]),
        {"target": copy.deepcopy(packet["target"]),
         "credential": {"kind": "github-app-installation",
                        "installationId": 301, "repositoryIds": [300],
                        "permissions": {"metadata": "read", "contents": "read",
                                        "pull_requests": "write"},
                        "expiresAt": "2026-09-25T12:20:00Z"}},
        {"operation": copy.deepcopy(packet["operation"])},
    ]
    blobs = [{key: BLOBS[key] for key in ("operator", "controls", "archive")},
             {"workflow": BLOBS["workflow"]}, {}, {}, {}]
    values = []
    for index, role in enumerate(observed.ROLES):
        values.append({"envelope": {
            "schema": observed.SCHEMA, "role": role, "complete": True,
            "principalId": f"synthetic-principal-{index}",
            "credentialId": format(index + 10, "x") * 64,
            "recordId": index + 400, "candidateSha256": digest,
            "observedAt": "2026-09-25T11:59:00Z", "facts": facts[index],
        }, "blobs": blobs[index]})
    return packet, raw, digest, values


class Port:
    def __init__(self, value):
        self.value = copy.deepcopy(value)

    def observe_source_release(self):
        return copy.deepcopy(self.value)

    def observe_workflow(self):
        return copy.deepcopy(self.value)

    def observe_review(self):
        return copy.deepcopy(self.value)

    def observe_target_scope(self):
        return copy.deepcopy(self.value)

    def observe_operation_plan(self):
        return copy.deepcopy(self.value)


class ObserverTests(unittest.TestCase):
    def verify(self, values=None, raw=None, digest=None, now=NOW):
        _, default_raw, default_digest, default_values = fixture()
        values = default_values if values is None else values
        ports = [Port(value) for value in values]
        return observed.verify_observed_candidate(
            default_raw if raw is None else raw,
            default_digest if digest is None else digest,
            *ports, now)

    def assert_refused(self, values=None, raw=None, digest=None, now=NOW):
        with self.assertRaises(observed.Refused):
            self.verify(values, raw, digest, now)

    def test_matching_fake_observers_are_still_non_authorizing(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("socket")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = self.verify()
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual((result.producer_run_id, result.artifact_id), (100, 101))

    def test_changed_immutable_bytes_and_observed_pins_refuse(self):
        for role, key in ((0, "archive"), (0, "operator"), (0, "controls"),
                          (1, "workflow")):
            with self.subTest(role=role, key=key):
                values = fixture()[3]
                values[role]["blobs"][key] += b"-changed"
                self.assert_refused(values)
        for role, section, key, value in (
                (0, "facts", "coordinationRevision", "f" * 40),
                (0, "runtime", "closureSha256", "7" * 64),
                (1, "facts", "workflowRevision", "f" * 40),
                (1, "facts", "dispatchActorId", 999),
                (2, "facts", "reviewerId", 999),
                (3, "target", "repositoryId", 999),
                (4, "operation", "requestSha256", "7" * 64)):
            with self.subTest(role=role, key=key):
                values = fixture()[3]
                facts = values[role]["envelope"]["facts"]
                dest = facts if section == "facts" else (
                    facts["runtime"] if section == "runtime" else facts[section])
                dest[key] = value
                self.assert_refused(values)

    def test_producer_identity_swap_must_refuse(self):
        for key in ("producerRunId", "artifactId", "producerActorId"):
            with self.subTest(key=key):
                values = fixture()[3]
                values[0]["envelope"]["facts"][key] = 999
                self.assert_refused(values)

    def test_foreign_scope_stale_review_and_observer_custody_refuse(self):
        for role, field, value in (
                (0, "credentialId", "f" * 64),
                (1, "principalId", "synthetic-principal-0"),
                (2, "candidateSha256", "f" * 64),
                (3, "complete", False),
                (4, "role", "target-scope")):
            values = fixture()[3]
            values[role]["envelope"][field] = value
            if role == 0:  # An altered unique credential ID alone is not authority.
                result = self.verify(values)
                self.assertFalse(result.can_dispatch)
            else:
                self.assert_refused(values)
        for key, value in (("repositoryIds", [300, 999]),
                           ("permissions", {"metadata": "read",
                                            "contents": "write",
                                            "pull_requests": "write"}),
                           ("expiresAt", "2026-09-25T12:10:00Z")):
            values = fixture()[3]
            values[3]["envelope"]["facts"]["credential"][key] = value
            self.assert_refused(values)
        values = fixture()[3]
        values[2]["envelope"]["facts"]["expiresAt"] = "2026-09-25T11:59:59Z"
        self.assert_refused(values)
        self.assert_refused(now=NOW + dt.timedelta(minutes=31))
        values = fixture()[3]
        values[0]["envelope"]["principalId"] = values[1]["envelope"]["principalId"]
        self.assert_refused(values)

    def test_missing_port_and_exception_text_refuse(self):
        _, raw, digest, values = fixture()
        ports = [Port(value) for value in values]
        with self.assertRaisesRegex(observed.Refused, "observer-port-custody"):
            observed.verify_observed_candidate(raw, digest, ports[0], ports[0],
                                               ports[2], ports[3], ports[4], NOW)
        class Broken:
            def observe_source_release(self):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(observed.Refused, "observer-unavailable") as result:
            observed.verify_observed_candidate(raw, digest, Broken(), *ports[1:], NOW)
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(result.exception))


if __name__ == "__main__":
    unittest.main()
