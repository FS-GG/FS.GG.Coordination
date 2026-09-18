#!/usr/bin/env python3
"""Offline qualification for the prepared V2-CALL-01.4b operation."""

from __future__ import annotations

import copy
import datetime as dt
import importlib.util
import pathlib
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "eng/callable-cli-isolated-operation.py"
SPEC = importlib.util.spec_from_file_location("callable_isolated_operation", MODULE_PATH)
operation = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(operation)


def reseal(value: dict[str, object], field: str) -> dict[str, object]:
    result = copy.deepcopy(value)
    result.pop(field, None)
    result[field] = operation.digest(operation.canonical(result))
    return result


class FakeGitHub:
    def __init__(self, responses: list[object]):
        self.responses = list(responses)
        self.calls: list[tuple[str, str, object | None]] = []

    def request(self, method: str, path: str, body: object | None = None):
        self.calls.append((method, path, body))
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


class IsolatedOperationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = operation.load_contract(ROOT / "eng/callable-cli-isolated-operation-contract.json")
        cls.preflight = operation.read_json(ROOT / "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json")

    def create_plan(self):
        return operation.prepare_create(self.contract)

    def creation_receipt(self):
        plan = self.create_plan()
        receipt = {
            "schema": "fsgg.coordination.callable-isolated-creation-receipt/1",
            "contractSha256": self.contract["contractSha256"],
            "creationPlanSeal": plan["seal"],
            "authoritativeReadback": True,
            "target": {
                "fullName": self.contract["target"]["fullName"], "owner": "FS-GG", "repositoryId": 9001,
                "nodeId": "R_synthetic", "visibility": "public", "defaultBranch": "main", "syntheticOnly": True,
            },
        }
        return reseal(receipt, "receiptSha256")

    def grant_and_observation(self, plan):
        grant = {
            "schema": "fsgg.coordination.callable-isolated-operation-grant/1", "authorized": True,
            "operationIdentity": self.contract["identity"], "phase": plan["phase"],
            "contractSha256": self.contract["contractSha256"], "planSeal": plan["seal"],
            "sourceSha256": self.contract["source"]["operationSourceSha256"],
            "approvedAt": "2026-09-18T12:00:00Z", "expiresAt": "2026-09-18T13:00:00Z",
            "appId": 7001, "installationId": 8001,
            "authority": {
                "repository": "FS-GG/.github", "workflowPath": ".github/workflows/callable-isolated-operation-authorize.yml",
                "environment": "callable-isolated-operation", "runId": 6001,
                "workflowRevision": "a" * 40, "workflowSha256": "b" * 64,
            },
        }
        repository_ids = [] if plan["phase"] == "creation" else [plan["target"]["repositoryId"]]
        target = {"httpStatus": 404} if plan["phase"] == "creation" else {
            "repositoryId": plan["target"]["repositoryId"], "fullName": plan["target"]["fullName"], "visibility": "public"}
        observation = {
            "schema": "fsgg.coordination.callable-isolated-admission-observation/1", "complete": True,
            "contractSha256": self.contract["contractSha256"], "planSeal": plan["seal"],
            "protectedRun": {"id": 6001, "conclusion": "success", "event": "workflow_dispatch", "headSha": "a" * 40,
                             "path": ".github/workflows/callable-isolated-operation-authorize.yml"},
            "workflow": {"revision": "a" * 40, "sha256": "b" * 64},
            "approvals": [{"state": "approved", "userId": 1645484}],
            "reviewerMembership": {"userId": 1645484, "state": "active"},
            "credential": {
                "kind": "github-app-installation", "appId": 7001, "installationId": 8001,
                "account": "FS-GG", "accountType": "Organization",
                "permissions": copy.deepcopy(self.contract["authorization"]["requiredPermissions"]),
                "repositorySelection": "all" if plan["phase"] == "creation" else "selected",
                "repositoryIds": repository_ids,
            },
            "target": target,
            "capabilities": {name: True for name in self.contract["authorization"]["requiredCapabilities"][plan["phase"]]},
        }
        return grant, observation

    def assert_refused(self, expected: str, contract, plan, grant, observation, now="2026-09-18T12:01:00Z"):
        with self.assertRaisesRegex(operation.Refused, expected):
            operation.validate_admission(contract, plan, grant, observation, operation.parse_time(now, "now"))

    def test_checked_in_state_is_prepared_not_authorized_with_preflight_refusal(self):
        inspected = operation.inspect(self.contract, self.preflight)
        self.assertEqual("prepared-not-authorized", inspected["state"])
        self.assertFalse(inspected["authorized"])
        self.assertEqual(0, inspected["liveEffects"])
        self.assertEqual("refused-no-compatible-admitted-target", inspected["disposition"])

    def test_creation_plan_is_deterministic_sealed_and_cannot_self_authorize(self):
        first = self.create_plan()
        self.assertEqual(operation.canonical(first), operation.canonical(self.create_plan()))
        operation.validate_plan(self.contract, first)
        changed = reseal({**first, "authorized": True}, "seal")
        with self.assertRaisesRegex(operation.Refused, "plan-cannot-self-authorize"):
            operation.validate_plan(self.contract, changed)

    def test_creation_receipt_binds_identity_before_second_phase(self):
        receipt = self.creation_receipt()
        plan = operation.prepare_operation(self.contract, receipt)
        self.assertEqual(9001, plan["target"]["repositoryId"])
        self.assertEqual(["setup", "installed-execution", "independent-readback", "cleanup"], plan["stages"])
        changed = copy.deepcopy(receipt)
        changed["target"]["repositoryId"] = 9002
        with self.assertRaisesRegex(operation.Refused, "creation-receipt-digest"):
            operation.prepare_operation(self.contract, changed)

    def test_complete_protected_observation_admits_each_phase_offline_with_zero_effect(self):
        for plan in (self.create_plan(), operation.prepare_operation(self.contract, self.creation_receipt())):
            with self.subTest(phase=plan["phase"]):
                grant, observation = self.grant_and_observation(plan)
                operation.validate_admission(self.contract, plan, grant, observation, operation.parse_time("2026-09-18T12:01:00Z", "now"))

    def test_grant_plan_source_time_and_observation_negatives_fail_closed(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        base_grant, base_observation = self.grant_and_observation(plan)
        cases = [
            ("grant-not-authorized", lambda g, o: g.update(authorized=False)),
            ("grant-operation", lambda g, o: g.update(phase="creation")),
            ("grant-plan-binding", lambda g, o: g.update(planSeal="0" * 64)),
            ("grant-source-binding", lambda g, o: g.update(sourceSha256="0" * 64)),
            ("grant-authority", lambda g, o: g["authority"].update(environment="wrong")),
            ("admission-observation-incomplete", lambda g, o: o.update(complete=False)),
            ("admission-observation-binding", lambda g, o: o.update(planSeal="0" * 64)),
            ("protected-run", lambda g, o: o["protectedRun"].update(conclusion="failure")),
            ("protected-run-source", lambda g, o: o["protectedRun"].update(path="wrong.yml")),
            ("protected-workflow", lambda g, o: o["workflow"].update(sha256="0" * 64)),
            ("protected-approval", lambda g, o: o.update(approvals=[])),
            ("reviewer-membership-unproved", lambda g, o: o["reviewerMembership"].update(state="unknown-403")),
            ("credential-kind", lambda g, o: o["credential"].update(kind="user-token")),
            ("credential-owner", lambda g, o: o["credential"].update(account="other")),
            ("credential-identity", lambda g, o: o["credential"].update(installationId=2)),
            ("credential-permission", lambda g, o: o["credential"]["permissions"].update(contents="read")),
            ("operation-credential-scope", lambda g, o: o["credential"].update(repositoryIds=[])),
            ("target-identity-drift", lambda g, o: o["target"].update(repositoryId=9002)),
            ("required-capability-unproved", lambda g, o: o["capabilities"].update(branchProtection=False)),
        ]
        for expected, mutate in cases:
            with self.subTest(expected=expected):
                grant, observation = copy.deepcopy(base_grant), copy.deepcopy(base_observation)
                mutate(grant, observation)
                self.assert_refused(expected, self.contract, plan, grant, observation)
        self.assert_refused("grant-expired", self.contract, plan, base_grant, base_observation, "2026-09-18T13:00:00Z")

    def test_creation_unknown_response_reconciles_readback_and_never_blindly_retries(self):
        plan = self.create_plan()
        created = {"id": 9001, "node_id": "R_synthetic", "full_name": self.contract["target"]["fullName"],
                   "owner": {"login": "FS-GG"}, "visibility": "public", "default_branch": "main"}
        client = FakeGitHub([(404, {}), operation.Refused("github-outcome-unknown-requires-readback"), (200, created)])
        receipt = operation.execute_creation(client, self.contract, plan)
        self.assertTrue(receipt["authoritativeReadback"])
        self.assertEqual(1, sum(1 for method, _, _ in client.calls if method == "POST"))

        pending = FakeGitHub([(404, {}), operation.Refused("github-outcome-unknown-requires-readback"), (404, {})])
        with self.assertRaisesRegex(operation.Refused, "creation-pending-requires-readback"):
            operation.execute_creation(pending, self.contract, plan)
        self.assertEqual(1, sum(1 for method, _, _ in pending.calls if method == "POST"))

    def test_existing_target_refuses_before_create_and_phase_two_refuses_on_identity_drift(self):
        create = FakeGitHub([(200, {"id": 1})])
        with self.assertRaisesRegex(operation.Refused, "creation-prestate-not-absent"):
            operation.execute_creation(create, self.contract, self.create_plan())
        self.assertEqual(["GET"], [method for method, _, _ in create.calls])
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        target = {"id": 9002, "visibility": "public", "default_branch": "main"}
        live = FakeGitHub([(200, target)])
        with self.assertRaisesRegex(operation.Refused, "operation-target-readback"):
            operation.execute_identity_bound(live, self.contract, plan, "/not-used", "TOKEN", "/not-used")
        self.assertEqual(["GET"], [method for method, _, _ in live.calls])


if __name__ == "__main__":
    unittest.main(verbosity=2)
