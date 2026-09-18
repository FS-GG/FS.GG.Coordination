#!/usr/bin/env python3
"""Offline qualification for the prepared V2-CALL-01.4b operation."""

from __future__ import annotations

import base64
import copy
import datetime as dt
import importlib.util
import io
import json
import pathlib
import subprocess
import tempfile
import unittest
import unittest.mock
import zipfile


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
    def __init__(self, responses: list[object], byte_responses: list[object] | None = None):
        self.responses = list(responses)
        self.byte_responses = list(byte_responses or [])
        self.calls: list[tuple[str, str, object | None]] = []

    def request(self, method: str, path: str, body: object | None = None):
        self.calls.append((method, path, body))
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response

    def request_bytes(self, method: str, path: str):
        self.calls.append((method, path, None))
        response = self.byte_responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


def grant_archive(payload: dict[str, object], extra: tuple[str, bytes] | None = None) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr(operation.GRANT_ARTIFACT_FILE, operation.canonical(payload))
        if extra is not None:
            bundle.writestr(*extra)
    return buffer.getvalue()


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
        self.journal_protection = None
        self.installed_plan_seal = "c" * 64
        self.pull_reads = 0
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
                state = operation.canonical({"schema": "fsgg.coordination.ordinary-delivery-journal/1", "stage": "settled", "mergeCommit": merge,
                                             "operationId": self.contract["identity"], "planDigest": self.installed_plan_seal, "generation": 3})
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
        if "/branches/fsgg/v2/journal/operation/" in path and path.endswith("/protection") and method == "GET":
            return (200, self.journal_protection) if self.journal_protection is not None else (404, {})
        if "/branches/fsgg/v2/journal/operation/" in path and path.endswith("/protection") and method == "PUT":
            self.journal_protection = body
            return 200, body
        if path == f"repos/{full_name}/pulls/7" and method == "GET":
            self.pull_reads += 1
            return 200, {"number": 7, "node_id": "PR_synthetic", "head": {"sha": self.refs[f"refs/heads/{operation.SOURCE_BRANCH}"]},
                         "base": {"sha": "a" * 40}, "merged": self.pull_reads > 1,
                         "merge_commit_sha": "f" * 40 if self.pull_reads > 1 else None}
        if path == f"repos/{full_name}" and method == "DELETE":
            self.exists = False
            return 204, {}
        raise AssertionError(f"unexpected request: {method} {path}")


class IsolatedOperationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = operation.load_contract(ROOT / "eng/callable-cli-isolated-operation-contract.json")
        cls.preflight = operation.read_json(ROOT / "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json")
        cls.proposal = operation.load_proposal(ROOT / "eng/callable-cli-isolated-operation-proposal.json", cls.contract, cls.preflight)

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
        roles = {}
        observed_roles = {}
        for index, (role, spec) in enumerate(self.contract["authorization"]["credentialRoles"].items()):
            granted = {"kind": spec["kind"], "appId": 7001, "installationId": 8001 + index,
                       "permissions": copy.deepcopy(spec["permissions"]), "expiresAt": "2026-09-18T13:00:00Z"}
            roles[role] = granted
            observed = {**granted, "account": "FS-GG", "accountType": "Organization",
                        "repositorySelection": "all" if role in {"app-installation-observer", "creation"} else "selected",
                        "repositoryIds": [], "repositoryFullNames": []}
            if role == "creation":
                observed.update(organizationScope="FS-GG", canCreateRepositories=True)
            observed_roles[role] = observed
        grant = {
            "schema": "fsgg.coordination.callable-isolated-operation-grant/1", "authorized": True,
            "operationIdentity": self.contract["identity"], "phase": plan["phase"],
            "contractSha256": self.contract["contractSha256"], "planSeal": plan["seal"],
            "sourceSha256": self.contract["source"]["operationSourceSha256"],
            "approvedAt": "2026-09-18T12:00:00Z", "expiresAt": "2026-09-18T13:00:00Z",
            "credentials": roles,
            "authority": {
                "repository": "FS-GG/.github", "workflowPath": ".github/workflows/callable-isolated-operation-authorize.yml",
                "environment": "callable-isolated-operation", "environmentId": 5001, "runId": 6001, "runAttempt": 2,
                "workflowRevision": "a" * 40, "workflowSha256": "b" * 64,
            },
        }
        artifact_envelope = {
            "schema": "fsgg.coordination.callable-isolated-operation-grant-artifact-envelope/1",
            "repository": "FS-GG/.github", "artifactId": 6101,
            "artifactName": "callable-isolated-operation-grant-6001-2", "artifactSha256": "d" * 64,
            "payloadSha256": operation.digest(operation.canonical(grant)),
            "workflowRunId": 6001, "workflowRunAttempt": 2, "expiresAt": "2026-09-19T12:00:00Z",
        }
        repository_ids = [] if plan["phase"] == "creation" else [plan["target"]["repositoryId"]]
        for role in ("setup", "execution", "cleanup"):
            observed_roles[role]["repositoryIds"] = repository_ids
            observed_roles[role]["repositoryFullNames"] = ([] if plan["phase"] == "creation" else [plan["target"]["fullName"]])
        observed_roles["authority-observer"]["repositoryFullNames"] = ["FS-GG/.github"]
        target = {"httpStatus": 404} if plan["phase"] == "creation" else {
            "repositoryId": plan["target"]["repositoryId"], "fullName": plan["target"]["fullName"], "visibility": "public"}
        observation = {
            "schema": "fsgg.coordination.callable-isolated-admission-observation/1", "complete": True,
            "contractSha256": self.contract["contractSha256"], "planSeal": plan["seal"],
            "protectedRun": {"id": 6001, "attempt": 2, "conclusion": "success", "event": "workflow_dispatch", "headSha": "a" * 40,
                             "path": ".github/workflows/callable-isolated-operation-authorize.yml"},
            "workflow": {"revision": "a" * 40, "sha256": "b" * 64},
            "grantArtifact": {"repository": "FS-GG/.github", "id": 6101,
                              "name": "callable-isolated-operation-grant-6001-2", "sha256": "d" * 64,
                              "payloadSha256": operation.digest(operation.canonical(grant)),
                              "workflowRunId": 6001, "workflowRunAttempt": 2,
                              "expiresAt": "2026-09-19T12:00:00Z", "expired": False},
            "approvals": [{"state": "approved", "userId": 1645484, "environment": "callable-isolated-operation", "environmentId": 5001}],
            "reviewerMembership": {"userId": 1645484, "state": "active"},
            "credentials": observed_roles,
            "target": target,
            "capabilities": {name: True for name in self.contract["authorization"]["requiredCapabilities"][plan["phase"]]},
        }
        return grant, artifact_envelope, observation

    def assert_refused(self, expected: str, contract, plan, grant, artifact_envelope, observation, now="2026-09-18T12:01:00Z"):
        with self.assertRaisesRegex(operation.Refused, expected):
            operation.validate_admission(contract, plan, grant, artifact_envelope, observation,
                                         operation.parse_time(now, "now"))

    def test_checked_in_state_is_prepared_not_authorized_with_preflight_refusal(self):
        inspected = operation.inspect(self.contract, self.preflight, self.proposal)
        self.assertEqual("prepared-not-authorized", inspected["state"])
        self.assertFalse(inspected["authorized"])
        self.assertEqual(0, inspected["liveEffects"])
        self.assertEqual("refused-no-compatible-admitted-target", inspected["disposition"])
        with tempfile.TemporaryDirectory() as scratch:
            changed = copy.deepcopy(self.proposal)
            changed["authorized"] = True
            path = pathlib.Path(scratch) / "proposal.json"
            path.write_text(operation.canonical(changed).decode())
            with self.assertRaisesRegex(operation.Refused, "proposal-digest"):
                operation.load_proposal(path, self.contract, self.preflight)

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
                grant, envelope, observation = self.grant_and_observation(plan)
                operation.validate_admission(self.contract, plan, grant, envelope, observation,
                                             operation.parse_time("2026-09-18T12:01:00Z", "now"))

    def test_grant_plan_source_time_and_observation_negatives_fail_closed(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        base_grant, base_envelope, base_observation = self.grant_and_observation(plan)
        cases = [
            ("grant-not-authorized", lambda g, o: g.update(authorized=False)),
            ("grant-operation", lambda g, o: g.update(phase="creation")),
            ("grant-plan-binding", lambda g, o: g.update(planSeal="0" * 64)),
            ("grant-source-binding", lambda g, o: g.update(sourceSha256="0" * 64)),
            ("grant-authority", lambda g, o: g["authority"].update(environment="wrong")),
            ("protected-run", lambda g, o: o["protectedRun"].update(attempt=3)),
            ("grant-artifact-readback", lambda g, o: o["grantArtifact"].update(sha256="0" * 64)),
            ("admission-observation-incomplete", lambda g, o: o.update(complete=False)),
            ("admission-observation-binding", lambda g, o: o.update(planSeal="0" * 64)),
            ("protected-run", lambda g, o: o["protectedRun"].update(conclusion="failure")),
            ("protected-run-source", lambda g, o: o["protectedRun"].update(path="wrong.yml")),
            ("protected-workflow", lambda g, o: o["workflow"].update(sha256="0" * 64)),
            ("protected-approval", lambda g, o: o["approvals"][0].update(environmentId=9)),
            ("reviewer-membership-unproved", lambda g, o: o["reviewerMembership"].update(state="unknown-403")),
            ("credential-kind", lambda g, o: o["credentials"]["setup"].update(kind="user-token")),
            ("credential-owner", lambda g, o: o["credentials"]["setup"].update(account="other")),
            ("credential-identity", lambda g, o: o["credentials"]["setup"].update(installationId=2)),
            ("credential-permission", lambda g, o: o["credentials"]["setup"]["permissions"].update(contents="read")),
            ("authority-credential-scope", lambda g, o: o["credentials"]["authority-observer"].update(repositoryFullNames=["FS-GG/other"])),
            ("operation-credential-scope", lambda g, o: o["credentials"]["setup"].update(repositoryIds=[9001, 9002])),
            ("target-identity-drift", lambda g, o: o["target"].update(repositoryId=9002)),
            ("required-capability-unproved", lambda g, o: o["capabilities"].update(branchProtection=False)),
        ]
        for expected, mutate in cases:
            with self.subTest(expected=expected):
                grant, observation = copy.deepcopy(base_grant), copy.deepcopy(base_observation)
                mutate(grant, observation)
                self.assert_refused(expected, self.contract, plan, grant, base_envelope, observation)
        self.assert_refused("grant-expired", self.contract, plan, base_grant, base_envelope,
                            base_observation, "2026-09-18T13:00:00Z")

    def test_creation_unknown_response_reconciles_readback_and_never_blindly_retries(self):
        plan = self.create_plan()
        created = {"id": 9001, "node_id": "R_synthetic", "full_name": self.contract["target"]["fullName"],
                   "owner": {"login": "FS-GG"}, "visibility": "public", "default_branch": "main", "archived": False,
                   "homepage": plan["request"]["body"]["homepage"], "created_at": "2026-09-18T12:00:01Z"}
        with tempfile.TemporaryDirectory() as scratch:
            state = pathlib.Path(scratch) / "creation.json"
            pending = FakeGitHub([(404, {}), operation.Refused("github-outcome-unknown-requires-readback"), (404, {})])
            with self.assertRaisesRegex(operation.Refused, "creation-pending-requires-readback"):
                operation.execute_creation(pending, self.contract, plan, state, operation.parse_time("2026-09-18T12:00:00Z", "now"))
            self.assertEqual(1, sum(1 for method, _, _ in pending.calls if method == "POST"))
            resumed = FakeGitHub([(200, created), (200, created)])
            receipt = operation.execute_creation(resumed, self.contract, plan, state, operation.parse_time("2026-09-18T12:01:00Z", "now"))
            self.assertTrue(receipt["authoritativeReadback"])
            self.assertEqual(0, sum(1 for method, _, _ in resumed.calls if method == "POST"))

    def test_existing_target_refuses_before_create_and_phase_two_refuses_on_identity_drift(self):
        create = FakeGitHub([(200, {"id": 1})])
        with tempfile.TemporaryDirectory() as scratch, self.assertRaisesRegex(operation.Refused, "creation-prestate-not-absent"):
            operation.execute_creation(create, self.contract, self.create_plan(), pathlib.Path(scratch) / "state.json", operation.parse_time("2026-09-18T12:00:00Z", "now"))
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
                return Result(0, operation.canonical({"seal": "c" * 64}))
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
                self.assertEqual("settled", persisted["cleanup"]["state"])
                self.assertEqual(9001, persisted["cleanup"]["expectedRepositoryId"])
                operation.write_private(receipt_path, receipt)
                replay = operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", receipt_path)
                self.assertEqual(receipt, replay)
                self.assertEqual(1, sum(1 for method, _, _ in client.calls if method == "DELETE"))
        finally:
            operation.installed_command = original_installed
            operation.run_cli = original_run

    def test_resealed_out_of_scope_creation_request_refuses_before_mutation(self):
        plan = self.create_plan()
        changed = copy.deepcopy(plan)
        changed["request"]["path"] = "orgs/FS-GG/repos/other"
        changed = reseal(changed, "seal")
        client = FakeGitHub([])
        with tempfile.TemporaryDirectory() as scratch, self.assertRaisesRegex(operation.Refused, "creation-plan-not-canonical-contract"):
            operation.execute_creation(client, self.contract, changed, pathlib.Path(scratch) / "state.json",
                                       operation.parse_time("2026-09-18T12:00:00Z", "now"))
        self.assertEqual([], client.calls)

    def test_authority_attempt_environment_artifact_and_role_drift_refuse_offline(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        base_grant, base_envelope, base_observation = self.grant_and_observation(plan)
        cases = [
            ("protected-run", lambda g, o: o["protectedRun"].update(attempt=99)),
            ("protected-approval", lambda g, o: o["approvals"][0].update(environment="wrong")),
            ("grant-artifact-readback", lambda g, o: o.pop("grantArtifact")),
            ("grant-artifact-readback", lambda g, o: o["grantArtifact"].update(id=999)),
            ("credential-role-set", lambda g, o: o["credentials"].pop("cleanup")),
            ("credential-expired", lambda g, o: o["credentials"]["setup"].update(expiresAt="2026-09-18T12:00:00Z")),
            ("operation-credential-scope", lambda g, o: o["credentials"]["execution"].update(repositoryIds=[9001, 9002])),
            ("required-capability-unproved", lambda g, o: o["capabilities"].update(workflows=False)),
        ]
        for expected, mutate in cases:
            with self.subTest(expected=expected):
                grant, observation = copy.deepcopy(base_grant), copy.deepcopy(base_observation)
                mutate(grant, observation)
                self.assert_refused(expected, self.contract, plan, grant, base_envelope, observation)

    def test_artifact_envelope_rejects_self_reference_replay_and_coordinate_substitution(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        base_grant, base_envelope, base_observation = self.grant_and_observation(plan)
        cases = [
            ("grant-artifact-self-reference", lambda g, e, o: g.update(artifact={"id": 6101})),
            ("grant-artifact-envelope-binding", lambda g, e, o: e.update(repository="FS-GG/other")),
            ("grant-artifact-envelope-binding", lambda g, e, o: e.update(payloadSha256="0" * 64)),
            ("grant-artifact-envelope-binding", lambda g, e, o: e.update(workflowRunId=999)),
            ("grant-artifact-envelope-binding", lambda g, e, o: e.update(workflowRunAttempt=3)),
            ("grant-artifact-readback", lambda g, e, o: e.update(artifactId=999)),
            ("grant-artifact-envelope-binding", lambda g, e, o: e.update(artifactName="wrong")),
            ("grant-artifact-readback", lambda g, e, o: e.update(artifactSha256="0" * 64)),
            ("grant-artifact-readback", lambda g, e, o: e.update(expiresAt="2026-09-18T12:00:00Z")),
        ]
        for expected, mutate in cases:
            with self.subTest(expected=expected):
                grant, envelope, observation = (copy.deepcopy(base_grant), copy.deepcopy(base_envelope),
                                                copy.deepcopy(base_observation))
                mutate(grant, envelope, observation)
                self.assert_refused(expected, self.contract, plan, grant, envelope, observation)

    def test_pending_repeated_checks_and_changed_protection_refuse_without_mutation(self):
        pending = FakeGitHub([(200, {"total_count": 1, "check_runs": [
            {"name": operation.CHECK_NAME, "status": "in_progress", "conclusion": None, "app": {"id": 1}}]})])
        with self.assertRaisesRegex(operation.Refused, "required-check-not-passed"):
            operation.observe_check(pending, self.contract["target"]["fullName"], "a" * 40)
        repeated = FakeGitHub([(200, {"total_count": 2, "check_runs": [
            {"name": operation.CHECK_NAME}, {"name": operation.CHECK_NAME}]})])
        with self.assertRaisesRegex(operation.Refused, "required-check-identity"):
            operation.observe_check(repeated, self.contract["target"]["fullName"], "a" * 40)
        changed = FakeGitHub([(200, {"required_status_checks": {"strict": False, "checks": [
            {"context": operation.CHECK_NAME, "app_id": 1}]}, "enforce_admins": False,
            "required_pull_request_reviews": None, "restrictions": None})])
        with self.assertRaisesRegex(operation.Refused, "branch-protection-conflict"):
            operation.ensure_protection(changed, self.contract["target"]["fullName"], "main", 1)
        self.assertTrue(all(method == "GET" for method, _, _ in pending.calls + repeated.calls + changed.calls))

    def test_mid_setup_expiry_stops_before_cli_or_cleanup(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        grant, _, _ = self.grant_and_observation(plan)
        client = FullOperationGitHub(self.contract, plan)
        original_installed, original_run = operation.installed_command, operation.run_cli
        calls = []
        class Result:
            returncode = 0
            stdout = operation.canonical({"seal": "c" * 64})
            stderr = b""
        operation.installed_command = lambda *_: pathlib.Path("/qualified/fsgg-coordination")
        operation.run_cli = lambda *_: (calls.append("plan") or Result())
        moments = iter([operation.parse_time("2026-09-18T12:01:00Z", "now"), operation.parse_time("2026-09-18T13:00:00Z", "now")])
        try:
            with tempfile.TemporaryDirectory() as scratch, self.assertRaisesRegex(operation.Refused, "grant-expired"):
                operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN",
                    pathlib.Path(scratch) / "state.json", client, grant, lambda: next(moments))
            self.assertEqual(["plan"], calls)
            self.assertFalse(any(method == "DELETE" for method, _, _ in client.calls))
        finally:
            operation.installed_command, operation.run_cli = original_installed, original_run

    def test_lost_cli_response_resumes_by_immutable_merged_pull_without_duplicate_advance(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        original_installed, original_run = operation.installed_command, operation.run_cli
        operation.installed_command = lambda *_: pathlib.Path("/qualified/fsgg-coordination")
        phase = {"value": "first"}
        calls = []
        class Result:
            def __init__(self, stdout): self.returncode, self.stdout, self.stderr = 0, stdout, b""
        def run(_command, arguments, _environment):
            calls.append(arguments[1])
            if arguments[1] == "plan": return Result(operation.canonical({"seal": "c" * 64}))
            if phase["value"] == "first": raise subprocess.TimeoutExpired("cli", 120)
            return Result(b"AdvanceAlreadySettled")
        operation.run_cli = run
        try:
            with tempfile.TemporaryDirectory() as scratch:
                state = pathlib.Path(scratch) / "state.json"
                first = FullOperationGitHub(self.contract, plan)
                with self.assertRaisesRegex(operation.Refused, "installed-advance-outcome-unknown-requires-readback"):
                    operation.execute_identity_bound(first, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", state)
                phase["value"] = "resume"
                resumed = FullOperationGitHub(self.contract, plan)
                resumed.pull_reads = 1
                receipt = operation.execute_identity_bound(resumed, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", state)
                self.assertEqual("native-readback", receipt["installed"]["firstOutcome"])
                self.assertEqual(1, calls.count("plan"))
                self.assertEqual(2, calls.count("advance"))  # one lost call, then the required no-op replay
        finally:
            operation.installed_command, operation.run_cli = original_installed, original_run

    def test_missing_retained_plan_and_arbitrary_404_never_settle(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        missing = {"schema": "fsgg.coordination.callable-isolated-operation-progress/1",
                   "operationIdentity": self.contract["identity"], "contractSha256": self.contract["contractSha256"],
                   "planSeal": plan["seal"], "stage": "cli-intent", "target": plan["target"], "checkpoints": []}
        missing = operation.sealed(missing)
        with tempfile.TemporaryDirectory() as scratch:
            state = pathlib.Path(scratch) / "state.json"
            operation.write_private(state, missing)
            client = FullOperationGitHub(self.contract, plan)
            original_installed = operation.installed_command
            operation.installed_command = lambda *_: pathlib.Path("/qualified/fsgg-coordination")
            try:
                with self.assertRaisesRegex(operation.Refused, "retained-plan-missing"):
                    operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN", state)
            finally:
                operation.installed_command = original_installed
            state.unlink()
            absent = FakeGitHub([(404, {})])
            with self.assertRaisesRegex(operation.Refused, "cleanup-readback-without-bound-intent"):
                operation.execute_identity_bound(absent, self.contract, plan, "/unused", "TOKEN", state)

    def test_public_prepare_and_validate_admission_commands_use_the_guarded_main_path(self):
        with tempfile.TemporaryDirectory() as scratch:
            create_path = pathlib.Path(scratch) / "create.json"
            prepared = subprocess.run(["python3", str(MODULE_PATH), "prepare-create", "--output", str(create_path)],
                                      cwd=ROOT, capture_output=True, text=True, check=False)
            self.assertEqual(0, prepared.returncode, prepared.stderr)
            plan = operation.read_json(create_path)
            grant, envelope, observation = self.grant_and_observation(plan)
            grant_path, envelope_path = pathlib.Path(scratch) / "grant.json", pathlib.Path(scratch) / "envelope.json"
            observation_path = pathlib.Path(scratch) / "observation.json"
            operation.write_private(grant_path, grant)
            operation.write_private(envelope_path, envelope)
            operation.write_private(observation_path, observation)
            admitted = subprocess.run(["python3", str(MODULE_PATH), "validate-admission", "--plan", str(create_path),
                "--grant", str(grant_path), "--grant-artifact-envelope", str(envelope_path),
                "--observation", str(observation_path), "--now", "2026-09-18T12:01:00Z"],
                cwd=ROOT, capture_output=True, text=True, check=False)
            self.assertEqual(0, admitted.returncode, admitted.stderr)
            self.assertIn('"admission":"valid"', admitted.stdout)

    def test_grant_artifact_archive_binds_exact_canonical_payload(self):
        plan = self.create_plan()
        grant, _, _ = self.grant_and_observation(plan)
        def archive(name, content, extra=None):
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as bundle:
                bundle.writestr(name, content)
                if extra is not None:
                    bundle.writestr("extra.json", extra)
            return buffer.getvalue()
        raw = operation.canonical(grant)
        self.assertEqual(grant, operation.grant_artifact_payload(archive(operation.GRANT_ARTIFACT_FILE, raw)))
        for payload in (
                archive("wrong.json", raw),
                archive(operation.GRANT_ARTIFACT_FILE, raw, b"{}"),
                b"not-a-zip"):
            with self.subTest(size=len(payload)), self.assertRaisesRegex(operation.Refused, "grant-artifact-content"):
                operation.grant_artifact_payload(payload)
        self.assertNotEqual(grant, operation.grant_artifact_payload(
            archive(operation.GRANT_ARTIFACT_FILE, b'{"changed":true}')))

    def live_artifact_fixture(self, archive_override=None, artifact_changes=None):
        plan = self.create_plan()
        grant, envelope, _ = self.grant_and_observation(plan)
        workflow_bytes = b"trusted authorization workflow bytes"
        grant["authority"]["workflowSha256"] = operation.digest(workflow_bytes)
        archive = archive_override if archive_override is not None else grant_archive(grant)
        envelope.update(artifactSha256=operation.digest(archive),
                        payloadSha256=operation.digest(operation.canonical(grant)))
        run = {"id": 6001, "run_attempt": 2, "conclusion": "success", "event": "workflow_dispatch",
               "head_sha": "a" * 40, "path": ".github/workflows/callable-isolated-operation-authorize.yml"}
        artifact = {"id": 6101, "name": envelope["artifactName"],
                    "digest": "sha256:" + operation.digest(archive), "workflow_run": {"id": 6001},
                    "expires_at": envelope["expiresAt"], "expired": False}
        if artifact_changes:
            artifact.update(artifact_changes)
        approvals = [{"state": "approved", "user": {"id": 1645484},
                      "environments": [{"name": "callable-isolated-operation", "id": 5001}]}]
        workflow = {"content": base64.b64encode(workflow_bytes).decode()}
        empty_repositories = (200, {"total_count": 0, "repositories": []})
        authority_repositories = (200, {"total_count": 1, "repositories": [{"id": 1, "full_name": "FS-GG/.github"}]})
        installation = {"account": {"login": "FS-GG", "type": "Organization"}, "repository_selection": "selected"}
        creation_installation = {"account": {"login": "FS-GG", "type": "Organization"}, "repository_selection": "all"}
        installation_responses = []
        for role in operation.ROLE_NAMES:
            installation_responses.append((200, creation_installation if role == "creation" else installation))
        clients = {
            "app-installation-observer": FakeGitHub(installation_responses),
            "authority-observer": FakeGitHub([(200, run), (200, approvals), (200, workflow),
                                                (200, artifact), authority_repositories], [(200, archive)]),
            "reviewer-membership-observer": FakeGitHub([(200, {"state": "active"}), empty_repositories]),
            "creation": FakeGitHub([empty_repositories, (404, {})]),
            "setup": FakeGitHub([empty_repositories]),
            "execution": FakeGitHub([empty_repositories]),
            "cleanup": FakeGitHub([empty_repositories]),
        }
        return plan, grant, envelope, clients

    def test_live_observation_binds_independent_artifact_envelope_and_canonical_grant(self):
        plan, grant, envelope, clients = self.live_artifact_fixture()
        observation = operation.live_observation(clients, self.contract, plan, grant, envelope)
        operation.validate_admission(self.contract, plan, grant, envelope, observation,
                                     operation.parse_time("2026-09-18T12:01:00Z", "now"))
        self.assertNotIn("artifact", grant)
        self.assertEqual(envelope["artifactId"], observation["grantArtifact"]["id"])
        self.assertTrue(all(method == "GET" for client in clients.values() for method, _, _ in client.calls))

    def test_live_artifact_substitution_and_malformed_observations_refuse_before_mutation(self):
        cases = [
            ("grant-artifact-envelope-readback", None, {"id": 999}),
            ("grant-artifact-envelope-readback", None, {"name": "wrong"}),
            ("grant-artifact-envelope-readback", None, {"digest": "sha256:" + "0" * 64}),
            ("grant-artifact-envelope-readback", None, {"workflow_run": {"id": 999}}),
            ("grant-artifact-envelope-readback", None, {"expired": True}),
            ("grant-artifact-content", b"not-a-zip", None),
            ("grant-artifact-content", grant_archive({"changed": True}, ("extra.json", b"{}")), None),
            ("grant-artifact-content-binding", grant_archive({"changed": True}), None),
        ]
        for expected, archive, changes in cases:
            with self.subTest(expected=expected, changes=changes):
                plan, grant, envelope, clients = self.live_artifact_fixture(archive, changes)
                with self.assertRaisesRegex(operation.Refused, expected):
                    operation.live_observation(clients, self.contract, plan, grant, envelope)
                self.assertTrue(all(method == "GET" for client in clients.values() for method, _, _ in client.calls))
        plan, grant, envelope, clients = self.live_artifact_fixture()
        envelope["workflowRunAttempt"] = 3
        with self.assertRaisesRegex(operation.Refused, "grant-artifact-envelope-binding"):
            operation.live_observation(clients, self.contract, plan, grant, envelope)
        self.assertEqual([], [call for client in clients.values() for call in client.calls])

    def test_public_execute_uses_distinct_tokens_and_reaches_resume_interpreter(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        grant, envelope, observation = self.grant_and_observation(plan)
        receipt = {"schema": "test", "receiptSha256": "e" * 64}
        with tempfile.TemporaryDirectory() as scratch:
            plan_path = pathlib.Path(scratch) / "plan.json"
            grant_path = pathlib.Path(scratch) / "grant.json"
            envelope_path = pathlib.Path(scratch) / "envelope.json"
            receipt_path = pathlib.Path(scratch) / "receipt.json"
            operation.write_private(plan_path, plan)
            operation.write_private(grant_path, grant)
            operation.write_private(envelope_path, envelope)
            argv = ["--contract", str(ROOT / "eng/callable-cli-isolated-operation-contract.json"),
                    "--proposal", str(ROOT / "eng/callable-cli-isolated-operation-proposal.json"),
                    "--preflight", str(ROOT / "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"),
                    "execute", "--plan", str(plan_path), "--grant", str(grant_path),
                    "--grant-artifact-envelope", str(envelope_path),
                    "--receipt", str(receipt_path), "--command", "/qualified/fsgg-coordination"]
            environment = {}
            for index, role in enumerate(operation.ROLE_NAMES):
                name = "TEST_" + role.upper().replace("-", "_")
                argv.extend([f"--{role}-token-env", name])
                environment[name] = f"token-{index}"
            made = []
            factory = lambda token, _base: (made.append(token) or object())
            with unittest.mock.patch.dict(operation.os.environ, environment, clear=False), \
                    unittest.mock.patch.object(operation, "live_observation", return_value=observation), \
                    unittest.mock.patch.object(operation, "execute_identity_bound", return_value=receipt) as execute:
                self.assertEqual(0, operation.main(argv, client_factory=factory,
                    now_provider=lambda: operation.parse_time("2026-09-18T12:01:00Z", "now")))
                self.assertEqual(0, operation.main(argv, client_factory=factory,
                    now_provider=lambda: operation.parse_time("2026-09-18T12:01:00Z", "now")))
                self.assertEqual(2, execute.call_count)
            self.assertEqual(len(operation.ROLE_NAMES) * 2, len(made))
            alias_environment = {name: "same-token" for name in environment}
            with unittest.mock.patch.dict(operation.os.environ, alias_environment, clear=False):
                self.assertEqual(3, operation.main(argv, client_factory=lambda *_: self.fail("client created")))

    def test_denied_or_malformed_installation_and_membership_observations_refuse(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        grant, envelope, observation = self.grant_and_observation(plan)
        denied = copy.deepcopy(observation)
        denied["reviewerMembership"] = {"userId": 1645484, "state": "unknown-403"}
        self.assert_refused("reviewer-membership-unproved", self.contract, plan, grant, envelope, denied)
        malformed = copy.deepcopy(observation)
        malformed["credentials"]["app-installation-observer"].pop("installationId")
        self.assert_refused("credential-identity", self.contract, plan, grant, envelope, malformed)
        source = MODULE_PATH.read_text()
        self.assertIn('app/installations/{identity.get(\'installationId\')}', source)
        self.assertNotIn('request("GET", "installation")', source)

    def test_unknown_native_settlement_stays_pending_and_never_deletes(self):
        plan = operation.prepare_operation(self.contract, self.creation_receipt())
        client = FullOperationGitHub(self.contract, plan)
        original_request = client.request
        def request(method, path, body=None):
            if "/contents/ordinary/" in path and method == "GET":
                state = operation.canonical({"schema": "fsgg.coordination.ordinary-delivery-journal/1", "stage": "effect-pending",
                    "mergeCommit": None, "operationId": self.contract["identity"], "planDigest": "c" * 64, "generation": 2})
                return 200, {"sha": "9" * 40, "content": base64.b64encode(state).decode()}
            return original_request(method, path, body)
        client.request = request
        original_installed, original_run = operation.installed_command, operation.run_cli
        class Result:
            def __init__(self, stdout): self.returncode, self.stdout, self.stderr = 0, stdout, b""
        operation.installed_command = lambda *_: pathlib.Path("/qualified/fsgg-coordination")
        operation.run_cli = lambda _command, arguments, _environment: Result(
            operation.canonical({"seal": "c" * 64}) if arguments[1] == "plan" else b"AdvanceSettled")
        try:
            with tempfile.TemporaryDirectory() as scratch, self.assertRaisesRegex(operation.Refused, "journal-not-settled"):
                operation.execute_identity_bound(client, self.contract, plan, "/qualified/fsgg-coordination", "TOKEN",
                                                  pathlib.Path(scratch) / "state.json")
            self.assertFalse(any(method == "DELETE" for method, _, _ in client.calls))
        finally:
            operation.installed_command, operation.run_cli = original_installed, original_run

    def test_cleanup_restart_settles_only_retained_delete_intent_and_replacement_id_refuses(self):
        plan = self.create_plan()
        created = {"id": 9001, "node_id": "R_synthetic", "full_name": self.contract["target"]["fullName"],
                   "owner": {"login": "FS-GG"}, "visibility": "public", "default_branch": "main", "archived": False,
                   "homepage": plan["request"]["body"]["homepage"], "created_at": "2026-09-18T12:00:01Z"}
        with tempfile.TemporaryDirectory() as scratch:
            state = pathlib.Path(scratch) / "creation.json"
            first = FakeGitHub([(404, {}), (201, {}), (200, created)])
            operation.execute_creation(first, self.contract, plan, state, operation.parse_time("2026-09-18T12:00:00Z", "now"))
            replacement = copy.deepcopy(created); replacement["id"] = 9002
            with self.assertRaisesRegex(operation.Refused, "creation-readback-replacement"):
                operation.execute_creation(FakeGitHub([(200, replacement), (200, replacement)]), self.contract, plan, state,
                                           operation.parse_time("2026-09-18T12:02:00Z", "now"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
