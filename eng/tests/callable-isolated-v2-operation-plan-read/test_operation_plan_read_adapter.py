"""Independent fake-store controls for a held operation-plan observer."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_operation_plan_read_adapter as plan

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def sha(value):
    return hashlib.sha256(value).hexdigest()


def fixture():
    selected = {"repository": "FS-GG/v2-synthetic", "repositoryId": 300,
        "nodeId": "R_300", "installationId": 301,
        "sourceRef": "refs/heads/source", "sourceSha": "2" * 40,
        "baseRef": "refs/heads/base", "baseSha": "3" * 40,
        "prestateSha256": "4" * 64}
    facts = [
        {"sourceTree": "b" * 40},
        {"workflowRevision": "c" * 40, "runId": 200, "runAttempt": 1,
         "dispatchActorId": 202},
        {"runId": 200, "runAttempt": 1, "reviewEventId": 204,
         "reviewerId": 203, "dispatchActorId": 202,
         "reviewedAt": "2026-09-25T11:55:00Z",
         "expiresAt": "2026-09-25T12:20:00Z"},
        {"target": selected},
    ]
    envelopes = [{"schema": observers.SCHEMA, "role": role, "complete": True,
                  "principalId": f"observer-{index}",
                  "credentialId": format(index + 10, "x") * 64,
                  "recordId": index + 400, "candidateSha256": "f" * 64,
                  "observedAt": "2026-09-25T11:59:00Z", "facts": facts[index]}
                 for index, role in enumerate(observers.ROLES[:4])]
    core = {"operation_identity": candidate.IDENTITY, "write_attempts": 1,
            "repository_id": 300, "repository": selected["repository"],
            "source_ref": selected["sourceRef"],
            "source_sha": selected["sourceSha"],
            "base_ref": selected["baseRef"], "base_sha": selected["baseSha"]}
    marker = sha(canonical({"effect": "create-pull", "intent": core}))
    request = {"title": plan.TITLE, "head": "source", "base": "base",
               "body": f"FS-GG-Effect: {marker}"}
    operation = {"identity": candidate.IDENTITY, "id": "5" * 64,
                 "method": "POST", "path": "repos/FS-GG/v2-synthetic/pulls",
                 "requestSha256": sha(canonical(request)), "maxProviderWrites": 1}
    record = {"schema": plan.PLAN_SCHEMA, "state": "prepared-not-authorized",
              "candidateSha256": "f" * 64, "sourceTree": "b" * 40,
              "workflowRevision": "c" * 40, "runId": 200, "runAttempt": 1,
              "reviewEventId": 204, "target": selected,
              "operation": operation, "request": request}
    scope = {"principalId": "plan-reader", "credentialId": "1" * 64,
             "store": "coordination-protected-plan",
             "permissions": ["read-plan"],
             "expiresAt": "2026-09-25T12:20:00Z"}
    seal = {"schema": plan.SEAL_SCHEMA, "complete": True,
            "principalId": "plan-sealer", "credentialId": "2" * 64,
            "recordId": 500, "candidateSha256": "f" * 64,
            "sourceTree": "b" * 40, "workflowRevision": "c" * 40,
            "runId": 200, "runAttempt": 1, "reviewEventId": 204,
            "targetRepositoryId": 300, "operationId": "5" * 64,
            "sealedAt": "2026-09-25T11:58:00Z",
            "expiresAt": "2026-09-25T12:15:00Z"}
    return envelopes, record, scope, seal


class FakePort:
    def __init__(self, raw, scope):
        self.raw = raw
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read(self, record_id):
        self.reads.append(record_id)
        return self.raw


class FakeSeal:
    def __init__(self, value):
        self.value = copy.deepcopy(value)
        self.calls = []

    def read_seal(self, record_id, plan_sha256):
        self.calls.append((record_id, plan_sha256))
        result = copy.deepcopy(self.value)
        result.setdefault("planSha256", plan_sha256)
        return result


class OperationPlanTests(unittest.TestCase):
    def observe(self, envelopes=None, record=None, raw=None, scope=None, seal=None):
        selected, prepared, reader_scope, attestation = fixture()
        source = selected if envelopes is None else envelopes
        entry = prepared if record is None else record
        port = FakePort(canonical(entry) if raw is None else raw,
                        reader_scope if scope is None else scope)
        independent = FakeSeal(attestation if seal is None else seal)
        adapter = plan.OperationPlanReadAdapter(port, independent, *source,
                                                500, "f" * 64, NOW)
        return adapter.observe_operation_plan(), port, independent

    def refuses(self, **changes):
        with self.assertRaises(plan.Refused):
            self.observe(**changes)

    def test_matching_fake_remains_read_only(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch("os.getenv", side_effect=AssertionError("token")),
              mock.patch("socket.socket", side_effect=AssertionError("network")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result, port, seal = self.observe()
        self.assertEqual(port.reads, [500])
        self.assertEqual(seal.calls[0][0], 500)
        self.assertEqual(result["envelope"]["facts"]["operation"]["method"], "POST")
        self.assertEqual(result["blobs"], {})
        self.assertNotIn("authorized", result)
        self.assertFalse(hasattr(plan, "dispatch"))

    def test_wrong_run_actor_target_or_request_refuses(self):
        for section, key, changed in (
            ("root", "runId", 201), ("root", "runAttempt", 2),
            ("root", "reviewEventId", 205),
            ("target", "repositoryId", 302),
            ("request", "head", "foreign"),
            ("request", "body", "FS-GG-Effect: " + "a" * 64),
            ("operation", "method", "PUT"),
            ("operation", "path", "repos/FS-GG/foreign/pulls"),
            ("operation", "maxProviderWrites", 2)):
            with self.subTest(section=section, key=key):
                record = fixture()[1]
                destination = record if section == "root" else record[section]
                destination[key] = changed
                self.refuses(record=record)
        envelopes = fixture()[0]
        envelopes[1]["facts"]["dispatchActorId"] = 203
        self.refuses(envelopes=envelopes)
        envelopes = fixture()[0]
        envelopes[3]["facts"]["target"]["sourceSha"] = "not-a-git-sha"
        record = fixture()[1]
        record["target"]["sourceSha"] = "not-a-git-sha"
        core = {"operation_identity": candidate.IDENTITY, "write_attempts": 1,
                "repository_id": 300, "repository": "FS-GG/v2-synthetic",
                "source_ref": "refs/heads/source", "source_sha": "not-a-git-sha",
                "base_ref": "refs/heads/base", "base_sha": "3" * 40}
        record["request"]["body"] = "FS-GG-Effect: " + sha(canonical(
            {"effect": "create-pull", "intent": core}))
        record["operation"]["requestSha256"] = sha(canonical(record["request"]))
        self.refuses(envelopes=envelopes, record=record)

    def test_noncanonical_duplicate_or_broad_reader_refuses(self):
        prepared = fixture()[1]
        self.refuses(raw=json.dumps(prepared).encode())
        self.refuses(raw=b'{"schema":"a","schema":"b"}')
        for key, changed in (("permissions", ["read-plan", "write-plan"]),
                             ("expiresAt", "2026-09-25T11:00:00Z"),
                             ("principalId", "observer-0")):
            scope = fixture()[2]
            scope[key] = changed
            self.refuses(scope=scope)

    def test_missing_replayed_or_stale_seal_refuses(self):
        for key, changed in (("complete", False), ("recordId", 501),
                             ("planSha256", "a" * 64),
                             ("candidateSha256", "a" * 64),
                             ("sourceTree", "a" * 40),
                             ("runAttempt", 2), ("reviewEventId", 205),
                             ("targetRepositoryId", 302),
                             ("operationId", "a" * 64),
                             ("principalId", "plan-reader"),
                             ("sealedAt", "2026-09-25T11:00:00Z"),
                             ("expiresAt", "2026-09-25T11:59:00Z")):
            with self.subTest(key=key):
                seal = fixture()[3]
                seal[key] = changed
                self.refuses(seal=seal)
        seal = fixture()[3]
        del seal["recordId"]
        self.refuses(seal=seal)

    def test_scope_drift_and_secret_exception_refuse(self):
        envelopes, prepared, scope, seal = fixture()
        port = FakePort(canonical(prepared), scope)
        port.scopes[1]["credentialId"] = "a" * 64
        with self.assertRaisesRegex(plan.Refused, "plan-reader-drift"):
            plan.OperationPlanReadAdapter(port, FakeSeal(seal), *envelopes,
                500, "f" * 64, NOW).observe_operation_plan()
        class Broken(FakePort):
            def read(self, record_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        with self.assertRaisesRegex(plan.Refused, "plan-read-unavailable") as caught:
            plan.OperationPlanReadAdapter(Broken(canonical(prepared), scope),
                FakeSeal(seal), *envelopes, 500, "f" * 64, NOW).observe_operation_plan()
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))

    def test_reused_plan_reader_scope_mutated_at_final_read_refuses(self):
        envelopes, prepared, scope, seal = fixture()
        port = FakePort(canonical(prepared), scope)
        shared = copy.deepcopy(scope)
        reads = [0]
        def reused_scope():
            reads[0] += 1
            if reads[0] == 2:
                shared["credentialId"] = "a" * 64
            return shared
        port.scope = reused_scope
        with self.assertRaises(plan.Refused):
            plan.OperationPlanReadAdapter(port, FakeSeal(seal), *envelopes,
                500, "f" * 64, NOW).observe_operation_plan()


if __name__ == "__main__":
    unittest.main()
