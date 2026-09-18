#!/usr/bin/env python3
"""Offline qualification for the prepared V2-CALL-01.4b operation."""

from __future__ import annotations

import base64
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


class FullOperationGitHub:
    def __init__(self, contract: dict[str, object], plan: dict[str, object]):
        self.contract = contract
        self.plan = plan
        self.exists = True
        self.refs = {"refs/heads/main": "a" * 40}
        self.contents: dict[str, bytes] = {}
        self.source_revision = 0
        self.pull = None
        self.protection = None
        self.calls: list[tuple[str, str, object | None]] = []

    def request(self, method: str, path: str, body: object | None = None):
        self.calls.append((method, path, body))
        full_name = self.plan["target"]["fullName"]
        if path == f"repos/{full_name}" and method == "GET":
            if not self.exists:
                return 404, {}
            return 200, {"id": self.plan["target"]["repositoryId"], "visibility": "public", "default_branch": "main"}
        if path.startswith(f"repos/{full_name}/git/ref/") and method == "GET":
            ref = "refs/" + path.split("/git/ref/", 1)[1]
            return (200, {"object": {"sha": self.refs[ref]}}) if ref in self.refs else (404, {})
        if path == f"repos/{full_name}/git/refs" and method == "POST":
            self.refs[body["ref"]] = body["sha"]
            return 201, {}
        if path.startswith(f"repos/{full_name}/contents/") and method == "GET":
            content_path = path.split("/contents/", 1)[1].split("?", 1)[0]
            if content_path.startswith("ordinary/"):
                merge = "f" * 40
                state = operation.canonical({"schema": "fsgg.coordination.ordinary-delivery-journal/1", "stage": "settled", "mergeCommit": merge})
                return 200, {"sha": "9" * 40, "content": base64.b64encode(state).decode()}
            if content_path not in self.contents:
                return 404, {}
            return 200, {"sha": "8" * 40, "content": base64.b64encode(self.contents[content_path]).decode()}
        if path.startswith(f"repos/{full_name}/contents/") and method == "PUT":
            content_path = path.split("/contents/", 1)[1]
            self.contents[content_path] = base64.b64decode(body["content"])
            self.source_revision += 1
            self.refs[f"refs/heads/{operation.SOURCE_BRANCH}"] = str(self.source_revision) * 40
            return 201, {}
        if path.startswith(f"repos/{full_name}/git/trees/") and method == "GET":
            return 200, {"truncated": False, "tree": [{"path": item, "type": "blob"} for item in
                ["README.md", ".github/workflows/callable-synthetic.yml", "synthetic.txt", "epoch.json"]]}
        if path.startswith(f"repos/{full_name}/pulls?") and method == "GET":
            return 200, [] if self.pull is None else [self.pull]
        if path == f"repos/{full_name}/pulls" and method == "POST":
            source = self.refs[f"refs/heads/{operation.SOURCE_BRANCH}"]
            self.pull = {"number": 7, "node_id": "PR_synthetic", "head": {"sha": source}, "base": {"sha": "a" * 40}}
            return 201, self.pull
        if path.endswith("/check-runs?per_page=100") and method == "GET":
            return 200, {"total_count": 1, "check_runs": [{"name": operation.CHECK_NAME, "status": "completed", "conclusion": "success", "app": {"id": 15368}}]}
        if path == f"repos/{full_name}/branches/main/protection" and method == "GET":
            return (200, self.protection) if self.protection is not None else (404, {})
        if path == f"repos/{full_name}/branches/main/protection" and method == "PUT":
            self.protection = body
            return 200, body
        if path == f"repos/{full_name}/pulls/7" and method == "GET":
            return 200, {"merged": True, "merge_commit_sha": "f" * 40}
        if path == f"repos/{full_name}" and method == "DELETE":
            self.exists = False
            return 204, {}
        raise AssertionError(f"unexpected request: {method} {path}")


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

    def test_identity_bound_interpreter_persists_before_cleanup_and_replay_is_noop(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        client = FullOperationGitHub(self.contract, plan)
        original_installed = operation.installed_command
        original_run = operation.run_cli
        calls = []

        class Result:
            def __init__(self, code, stdout):
                self.returncode = code
                self.stdout = stdout
                self.stderr = b""

        def run(_command, arguments, _token_environment):
            calls.append(arguments[1])
            if arguments[1] == "plan":
                return Result(0, b'{"sealed":"synthetic"}')
            if len([item for item in calls if item == "advance"]) == 1:
                return Result(0, b'AdvanceSettled')
            return Result(0, b'AdvanceAlreadySettled')

        operation.installed_command = lambda *_: pathlib.Path("/qualified/fsgg-coordination")
        operation.run_cli = run
        try:
            with tempfile.TemporaryDirectory() as scratch:
                receipt_path = pathlib.Path(scratch) / "receipt.json"
                receipt = operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", receipt_path)
                self.assertEqual("settled", receipt["cleanup"]["state"])
                self.assertEqual(["plan", "advance", "advance"], calls)
                persisted = operation.read_json(receipt_path)
                self.assertEqual("intent-persisted", persisted["cleanup"]["state"])
                operation.write_private(receipt_path, receipt)
                replay = operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", receipt_path)
                self.assertEqual(receipt, replay)
                self.assertEqual(1, sum(1 for method, _, _ in client.calls if method == "DELETE"))
        finally:
            operation.installed_command = original_installed
            operation.run_cli = original_run


if __name__ == "__main__":
    unittest.main(verbosity=2)
