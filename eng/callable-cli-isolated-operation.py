#!/usr/bin/env python3
"""Prepare and guard the V2-CALL-01.4b isolated GitHub operation.

The checked-in contract is deliberately not an authorization.  Mutation commands
require a short-lived protected grant and fresh GitHub authority/capability
observations bound to the exact sealed plan.  The offline commands never contact
GitHub and are the only commands exercised by source qualification.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_CONTRACT = ROOT / "eng/callable-cli-isolated-operation-contract.json"
DEFAULT_PROPOSAL = ROOT / "eng/callable-cli-isolated-operation-proposal.json"
DEFAULT_PREFLIGHT = ROOT / "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
SHA256 = re.compile(r"^[0-9a-f]{64}$")
OID = re.compile(r"^[0-9a-f]{40}$")
SOURCE_BRANCH = "fsgg/v2-call-01-4b/source"
POLICY_REF = "refs/heads/fsgg/v2/policy/callable-isolated"
EPOCH_REF = "refs/heads/fsgg/v2/epoch/callable-isolated"
CHECK_NAME = "callable-synthetic"
WORKFLOW_BYTES = b"""name: callable-synthetic
on:
  pull_request:
permissions:
  contents: read
jobs:
  callable-synthetic:
    name: callable-synthetic
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""
SOURCE_BYTES = b"V2-CALL-01.4b synthetic ordinary source delivery\n"
EPOCH_BYTES = b'{"complete":true,"generation":1,"phase":"OpenV2"}'
GRANT_ARTIFACT_NAME = "callable-isolated-operation-grant"
GRANT_ARTIFACT_FILE = "callable-isolated-operation-grant.json"
MAX_GRANT_BYTES = 128 * 1024
ROLE_NAMES = (
    "app-installation-observer", "authority-observer", "reviewer-membership-observer",
    "creation", "setup", "execution", "cleanup",
)


class Refused(RuntimeError):
    pass


def canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def grant_artifact_payload(archive: bytes) -> dict[str, object]:
    if len(archive) > MAX_GRANT_BYTES * 2:
        raise Refused("grant-artifact-archive-too-large")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            entries = bundle.infolist()
            if (len(entries) != 1 or entries[0].filename != GRANT_ARTIFACT_FILE
                    or entries[0].is_dir() or entries[0].file_size > MAX_GRANT_BYTES
                    or entries[0].compress_size > MAX_GRANT_BYTES
                    or entries[0].external_attr >> 16 & 0o170000 == 0o120000):
                raise Refused("grant-artifact-content")
            raw = bundle.read(entries[0])
    except (zipfile.BadZipFile, RuntimeError, KeyError) as error:
        raise Refused("grant-artifact-content") from error
    try:
        value = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise Refused("grant-artifact-content") from error
    if not isinstance(value, dict) or canonical(value) != raw.rstrip(b"\n"):
        raise Refused("grant-artifact-content")
    return value


def read_regular(path: str | pathlib.Path) -> bytes:
    item = pathlib.Path(path)
    if item.is_symlink() or not item.is_file():
        raise Refused(f"input-must-be-regular-nonsymlink:{item}")
    return item.read_bytes()


def read_json(path: str | pathlib.Path) -> dict[str, object]:
    try:
        value = json.loads(read_regular(path))
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise Refused(f"invalid-json:{path}") from error
    if not isinstance(value, dict):
        raise Refused(f"object-required:{path}")
    return value


def write_private(path: str | pathlib.Path, value: dict[str, object]) -> None:
    target = pathlib.Path(path)
    if target.exists() and target.is_symlink():
        raise Refused("output-symlink")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(canonical(value) + b"\n")
    target.chmod(0o600)


def write_private_bytes(path: str | pathlib.Path, value: bytes) -> None:
    target = pathlib.Path(path)
    if target.exists() and target.is_symlink():
        raise Refused("output-symlink")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(value)
    target.chmod(0o600)


def without(value: dict[str, object], field: str) -> dict[str, object]:
    return {key: item for key, item in value.items() if key != field}


def verify_digest(value: dict[str, object], field: str, label: str) -> str:
    expected = value.get(field)
    actual = digest(canonical(without(value, field)))
    if expected != actual:
        raise Refused(f"{label}-digest")
    return actual


def sealed(value: dict[str, object]) -> dict[str, object]:
    result = dict(value)
    result["seal"] = digest(canonical(value))
    return result


def parse_time(value: object, label: str) -> dt.datetime:
    if not isinstance(value, str):
        raise Refused(label)
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise Refused(label) from error
    if parsed.tzinfo is None:
        raise Refused(label)
    return parsed.astimezone(dt.timezone.utc)


def utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def timestamp(value: dt.datetime) -> str:
    return value.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def require_unexpired(grant: dict[str, object], now: dt.datetime) -> None:
    approved = parse_time(grant.get("approvedAt"), "grant-approved-time")
    expires = parse_time(grant.get("expiresAt"), "grant-expiry")
    if not (approved <= now < expires) or expires - approved > dt.timedelta(hours=2):
        raise Refused("grant-expired")


def validate_artifact_envelope(contract: dict[str, object], grant: dict[str, object], envelope: dict[str, object]) -> None:
    """Validate executor-supplied server coordinates without making them part of the grant payload."""
    authority = grant.get("authority")
    required = contract["authorization"]
    expected_fields = {
        "schema", "repository", "artifactId", "artifactName", "artifactSha256", "payloadSha256",
        "workflowRunId", "workflowRunAttempt", "expiresAt",
    }
    if set(envelope) != expected_fields or envelope.get("schema") != required["grantArtifactEnvelopeSchema"]:
        raise Refused("grant-artifact-envelope-schema")
    if not isinstance(authority, dict):
        raise Refused("grant-authority")
    expected_name = f"{required['grantArtifactNamePrefix']}-{authority.get('runId')}-{authority.get('runAttempt')}"
    if (envelope.get("repository") != required["repository"]
            or not isinstance(envelope.get("artifactId"), int) or envelope["artifactId"] <= 0
            or envelope.get("artifactName") != expected_name
            or not SHA256.fullmatch(str(envelope.get("artifactSha256", "")))
            or envelope.get("payloadSha256") != digest(canonical(grant))
            or envelope.get("workflowRunId") != authority.get("runId")
            or envelope.get("workflowRunAttempt") != authority.get("runAttempt")):
        raise Refused("grant-artifact-envelope-binding")
    parse_time(envelope.get("expiresAt"), "grant-artifact-expiry")


def load_contract(path: str | pathlib.Path) -> dict[str, object]:
    value = read_json(path)
    if value.get("schema") != "fsgg.coordination.callable-isolated-operation-contract/4":
        raise Refused("contract-schema")
    if value.get("state") != "prepared-not-authorized" or value.get("authorized") is not False:
        raise Refused("contract-must-remain-unauthorized")
    verify_digest(value, "contractSha256", "contract")
    source = value.get("source")
    if not isinstance(source, dict) or not SHA256.fullmatch(str(source.get("operationSourceSha256", ""))):
        raise Refused("contract-source-binding")
    actual_source = digest(read_regular(ROOT / "eng/callable-cli-isolated-operation.py"))
    if source["operationSourceSha256"] != actual_source:
        raise Refused("operation-source-drift")
    return value


def validate_preflight(contract: dict[str, object], value: dict[str, object]) -> None:
    if value.get("schema") != "fsgg.coordination.callable-isolated-operation-preflight/1":
        raise Refused("preflight-schema")
    verify_digest(value, "evidenceSha256", "preflight")
    if value.get("operationIdentity") != contract.get("identity") or value.get("authorized") is not False:
        raise Refused("preflight-operation-binding")
    disposition = value.get("disposition")
    facts = value.get("facts")
    if disposition != "refused-no-compatible-admitted-target" or not isinstance(facts, dict):
        raise Refused("preflight-disposition")
    expected = {
        "accessibleRepositoryCount": 16,
        "compatibleTargetCount": 0,
        "proposedTargetHttpStatus": 404,
        "privateSandboxRequiredStatusChecksHttpStatus": 403,
        "organizationInstallationHttpStatus": 403,
        "actorMembershipHttpStatus": 403,
        "actorId": 1645484,
    }
    if any(facts.get(key) != item for key, item in expected.items()):
        raise Refused("preflight-fact-drift")
    if facts.get("proposedTarget404Meaning") != "unobserved-target-only-not-ownership-reservation-or-authority":
        raise Refused("preflight-absence-inference")


def load_proposal(path: str | pathlib.Path, contract: dict[str, object], preflight: dict[str, object]) -> dict[str, object]:
    value = read_json(path)
    if value.get("schema") != "fsgg.coordination.callable-isolated-operation-proposal/4":
        raise Refused("proposal-schema")
    verify_digest(value, "proposalSha256", "proposal")
    if value.get("identity") != contract.get("identity") or value.get("state") != "prepared-not-authorized" or value.get("authorized") is not False:
        raise Refused("proposal-state")
    binding = value.get("contract")
    if (not isinstance(binding, dict) or binding.get("sha256") != contract.get("contractSha256")
            or binding.get("operationSourceSha256") != contract["source"]["operationSourceSha256"]):
        raise Refused("proposal-contract-binding")
    observed = value.get("preflight")
    if (not isinstance(observed, dict) or observed.get("sha256") != preflight.get("evidenceSha256")
            or observed.get("disposition") != preflight.get("disposition")):
        raise Refused("proposal-preflight-binding")
    phases = value.get("phases")
    if not isinstance(phases, list) or [item.get("id") for item in phases if isinstance(item, dict)] != ["creation", "identity-bound-operation"]:
        raise Refused("proposal-phase-order")
    return value


def prepare_create(contract: dict[str, object]) -> dict[str, object]:
    target = contract["target"]
    creation = contract["creation"]
    return sealed({
        "schema": "fsgg.coordination.callable-isolated-creation-plan/1",
        "operationIdentity": contract["identity"],
        "contractSha256": contract["contractSha256"],
        "phase": "creation",
        "authorized": False,
        "attemptIdentity": creation["attemptIdentity"],
        "target": {"fullName": target["fullName"], "owner": target["owner"], "name": target["name"]},
        "expectedPrestate": {"httpStatus": 404, "meaning": "proven-absent-only"},
        "request": creation["request"],
        "requiredGrant": contract["authorization"],
    })


def creation_identity(contract: dict[str, object], receipt: dict[str, object]) -> dict[str, object]:
    if receipt.get("schema") != "fsgg.coordination.callable-isolated-creation-receipt/1":
        raise Refused("creation-receipt-schema")
    verify_digest(receipt, "receiptSha256", "creation-receipt")
    if receipt.get("contractSha256") != contract.get("contractSha256"):
        raise Refused("creation-receipt-contract")
    target = receipt.get("target")
    expected = contract["target"]
    if not isinstance(target, dict):
        raise Refused("creation-target")
    if target.get("fullName") != expected["fullName"] or target.get("owner") != expected["owner"]:
        raise Refused("creation-target-name")
    if not isinstance(target.get("repositoryId"), int) or target["repositoryId"] <= 0:
        raise Refused("creation-target-id")
    if target.get("visibility") != "public" or target.get("syntheticOnly") is not True:
        raise Refused("creation-target-isolation")
    if receipt.get("authoritativeReadback") is not True or receipt.get("creationPlanSeal") is None:
        raise Refused("creation-readback")
    return target


def prepare_operation(contract: dict[str, object], receipt: dict[str, object]) -> dict[str, object]:
    target = creation_identity(contract, receipt)
    package = contract["package"]
    receiver = contract["receiver"]
    return sealed({
        "schema": "fsgg.coordination.callable-isolated-execution-plan/1",
        "operationIdentity": contract["identity"],
        "contractSha256": contract["contractSha256"],
        "phase": "identity-bound-operation",
        "authorized": False,
        "target": target,
        "creationReceiptSha256": receipt["receiptSha256"],
        "package": package,
        "receiver": receiver,
        "stages": contract["operationStages"],
        "requiredGrant": contract["authorization"],
        "acceptance": contract["acceptance"],
    })


def validate_plan(contract: dict[str, object], plan: dict[str, object]) -> None:
    if plan.get("operationIdentity") != contract.get("identity"):
        raise Refused("plan-operation")
    if plan.get("contractSha256") != contract.get("contractSha256"):
        raise Refused("plan-contract")
    if plan.get("authorized") is not False:
        raise Refused("plan-cannot-self-authorize")
    verify_digest(plan, "seal", "plan")
    if plan.get("phase") not in {"creation", "identity-bound-operation"}:
        raise Refused("plan-phase")
    if plan.get("phase") == "creation":
        if canonical(plan) != canonical(prepare_create(contract)):
            raise Refused("creation-plan-not-canonical-contract")
        return
    required = {
        "schema": "fsgg.coordination.callable-isolated-execution-plan/1",
        "operationIdentity": contract["identity"],
        "contractSha256": contract["contractSha256"],
        "phase": "identity-bound-operation",
        "authorized": False,
        "package": contract["package"],
        "receiver": contract["receiver"],
        "stages": contract["operationStages"],
        "requiredGrant": contract["authorization"],
        "acceptance": contract["acceptance"],
    }
    if any(plan.get(key) != value for key, value in required.items()):
        raise Refused("operation-plan-not-canonical-contract")
    target = plan.get("target")
    if (not isinstance(target, dict) or target.get("fullName") != contract["target"]["fullName"]
            or target.get("owner") != contract["target"]["owner"]
            or target.get("visibility") != "public" or target.get("syntheticOnly") is not True
            or not isinstance(target.get("repositoryId"), int) or target["repositoryId"] <= 0
            or not isinstance(plan.get("creationReceiptSha256"), str)
            or not SHA256.fullmatch(plan["creationReceiptSha256"])):
        raise Refused("operation-plan-target-binding")


def validate_admission(contract: dict[str, object], plan: dict[str, object], grant: dict[str, object], artifact_envelope: dict[str, object], observation: dict[str, object], now: dt.datetime) -> None:
    validate_plan(contract, plan)
    if grant.get("schema") != "fsgg.coordination.callable-isolated-operation-grant/1" or grant.get("authorized") is not True:
        raise Refused("grant-not-authorized")
    if grant.get("operationIdentity") != contract.get("identity") or grant.get("phase") != plan.get("phase"):
        raise Refused("grant-operation")
    if grant.get("contractSha256") != contract.get("contractSha256") or grant.get("planSeal") != plan.get("seal"):
        raise Refused("grant-plan-binding")
    if grant.get("sourceSha256") != contract["source"]["operationSourceSha256"]:
        raise Refused("grant-source-binding")
    if "artifact" in grant:
        raise Refused("grant-artifact-self-reference")
    require_unexpired(grant, now)
    authority = grant.get("authority")
    required = contract["authorization"]
    if not isinstance(authority, dict):
        raise Refused("grant-authority")
    for field in ("repository", "workflowPath", "environment"):
        if authority.get(field) != required[field]:
            raise Refused("grant-authority")
    if (not isinstance(authority.get("environmentId"), int) or authority["environmentId"] <= 0
            or not isinstance(authority.get("runId"), int) or not isinstance(authority.get("runAttempt"), int)
            or authority["runAttempt"] <= 0 or not OID.fullmatch(str(authority.get("workflowRevision", "")))
            or not SHA256.fullmatch(str(authority.get("workflowSha256", "")))):
        raise Refused("grant-authority-identity")
    validate_artifact_envelope(contract, grant, artifact_envelope)
    if observation.get("schema") != "fsgg.coordination.callable-isolated-admission-observation/1" or observation.get("complete") is not True:
        raise Refused("admission-observation-incomplete")
    if observation.get("contractSha256") != contract.get("contractSha256") or observation.get("planSeal") != plan.get("seal"):
        raise Refused("admission-observation-binding")
    run = observation.get("protectedRun")
    if (not isinstance(run, dict) or run.get("id") != authority["runId"]
            or run.get("attempt") != authority["runAttempt"] or run.get("conclusion") != "success"
            or run.get("event") != "workflow_dispatch"):
        raise Refused("protected-run")
    if run.get("headSha") != authority["workflowRevision"] or run.get("path") != required["workflowPath"]:
        raise Refused("protected-run-source")
    workflow = observation.get("workflow")
    if not isinstance(workflow, dict) or workflow.get("sha256") != authority["workflowSha256"] or workflow.get("revision") != authority["workflowRevision"]:
        raise Refused("protected-workflow")
    observed_artifact = observation.get("grantArtifact")
    if (not isinstance(observed_artifact, dict)
            or observed_artifact.get("repository") != artifact_envelope["repository"]
            or observed_artifact.get("id") != artifact_envelope["artifactId"]
            or observed_artifact.get("name") != artifact_envelope["artifactName"]
            or observed_artifact.get("sha256") != artifact_envelope["artifactSha256"]
            or observed_artifact.get("payloadSha256") != artifact_envelope["payloadSha256"]
            or observed_artifact.get("workflowRunId") != artifact_envelope["workflowRunId"]
            or observed_artifact.get("workflowRunAttempt") != artifact_envelope["workflowRunAttempt"]
            or observed_artifact.get("expiresAt") != artifact_envelope["expiresAt"]
            or observed_artifact.get("expired") is not False
            or parse_time(observed_artifact.get("expiresAt"), "grant-artifact-expiry") <= now):
        raise Refused("grant-artifact-readback")
    approvals = observation.get("approvals")
    reviewer = contract["authorization"]["requiredReviewer"]
    if not isinstance(approvals, list) or not any(
            item.get("state") == "approved" and item.get("userId") == reviewer["id"]
            and item.get("environment") == required["environment"]
            and item.get("environmentId") == authority["environmentId"]
            for item in approvals if isinstance(item, dict)):
        raise Refused("protected-approval")
    membership = observation.get("reviewerMembership")
    if not isinstance(membership, dict) or membership.get("userId") != reviewer["id"] or membership.get("state") != "active":
        raise Refused("reviewer-membership-unproved")
    credentials = observation.get("credentials")
    granted_credentials = grant.get("credentials")
    role_contracts = required.get("credentialRoles")
    if (not isinstance(credentials, dict) or not isinstance(granted_credentials, dict)
            or not isinstance(role_contracts, dict) or set(credentials) != set(ROLE_NAMES)
            or set(granted_credentials) != set(ROLE_NAMES)):
        raise Refused("credential-role-set")
    for role in ROLE_NAMES:
        credential = credentials[role]
        granted = granted_credentials[role]
        role_contract = role_contracts[role]
        if not isinstance(credential, dict) or not isinstance(granted, dict) or not isinstance(role_contract, dict):
            raise Refused("credential-role")
        if credential.get("kind") != role_contract.get("kind") or credential.get("kind") != granted.get("kind"):
            raise Refused("credential-kind")
        if credential.get("appId") != granted.get("appId") or credential.get("installationId") != granted.get("installationId"):
            raise Refused("credential-identity")
        if credential.get("account") != role_contract.get("account") or credential.get("accountType") != "Organization":
            raise Refused("credential-owner")
        if credential.get("permissions") != role_contract.get("permissions") or granted.get("permissions") != role_contract.get("permissions"):
            raise Refused("credential-permission")
        expires = parse_time(credential.get("expiresAt"), "credential-expiry")
        if expires <= now or credential.get("expiresAt") != granted.get("expiresAt"):
            raise Refused("credential-expired")
    creation_credential = credentials["creation"]
    if (creation_credential.get("repositorySelection") != "all"
            or creation_credential.get("organizationScope") != contract["target"]["owner"]
            or creation_credential.get("canCreateRepositories") is not True):
        raise Refused("creation-credential-scope")
    if credentials["authority-observer"].get("repositoryFullNames") != [required["repository"]]:
        raise Refused("authority-credential-scope")
    if plan["phase"] == "creation":
        observed_target = observation.get("target")
        if not isinstance(observed_target, dict) or observed_target.get("httpStatus") != 404:
            raise Refused("creation-prestate-not-absent")
    if plan["phase"] == "identity-bound-operation":
        target = plan.get("target")
        if not isinstance(target, dict):
            raise Refused("operation-credential-scope")
        for role in ("setup", "execution", "cleanup"):
            repositories = credentials[role].get("repositoryIds")
            names = credentials[role].get("repositoryFullNames")
            if repositories != [target.get("repositoryId")] or names != [target.get("fullName")]:
                raise Refused("operation-credential-scope")
        observed_target = observation.get("target")
        if not isinstance(observed_target, dict) or any(observed_target.get(key) != target.get(key) for key in ("repositoryId", "fullName", "visibility")):
            raise Refused("target-identity-drift")
    capabilities = observation.get("capabilities")
    if not isinstance(capabilities, dict) or any(capabilities.get(name) is not True for name in contract["authorization"]["requiredCapabilities"][plan["phase"]]):
        raise Refused("required-capability-unproved")


class GitHub:
    def __init__(self, token: str, api_base: str = "https://api.github.com/") -> None:
        if not token:
            raise Refused("missing-github-token")
        self.token = token
        self.api_base = api_base.rstrip("/") + "/"

    def request(self, method: str, path: str, body: object | None = None) -> tuple[int, object]:
        data = None if body is None else canonical(body)
        request = urllib.request.Request(
            self.api_base + path.lstrip("/"), data=data, method=method,
            headers={"accept": "application/vnd.github+json", "authorization": f"Bearer {self.token}", "content-type": "application/json", "user-agent": "fsgg-coordination-callable-isolated/1", "x-github-api-version": "2022-11-28"})
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else {}
        except urllib.error.HTTPError as error:
            raw = error.read()
            try:
                value = json.loads(raw) if raw else {}
            except json.JSONDecodeError:
                value = {}
            return error.code, value
        except (urllib.error.URLError, TimeoutError) as error:
            raise Refused("github-outcome-unknown-requires-readback") from error

    def request_bytes(self, method: str, path: str) -> tuple[int, bytes]:
        request = urllib.request.Request(
            self.api_base + path.lstrip("/"), method=method,
            headers={"accept": "application/vnd.github+json", "authorization": f"Bearer {self.token}",
                     "user-agent": "fsgg-coordination-callable-isolated/1", "x-github-api-version": "2022-11-28"})
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.read()
        except (urllib.error.URLError, TimeoutError) as error:
            raise Refused("github-outcome-unknown-requires-readback") from error


def expect_json(status: int, value: object, expected: int, label: str) -> dict[str, object]:
    if status != expected or not isinstance(value, dict):
        raise Refused(f"{label}:{status}")
    return value


def ref_path(full_name: str, ref: str) -> str:
    value = ref.removeprefix("refs/")
    return f"repos/{full_name}/git/ref/{value}"


def observe_ref(client: GitHub, full_name: str, ref: str) -> str | None:
    status, value = client.request("GET", ref_path(full_name, ref))
    if status == 404:
        return None
    root = expect_json(status, value, 200, "ref-readback")
    item = root.get("object")
    sha = item.get("sha") if isinstance(item, dict) else None
    if not isinstance(sha, str) or not OID.fullmatch(sha):
        raise Refused("ref-readback-sha")
    return sha


def ensure_ref(client: GitHub, full_name: str, ref: str, expected_sha: str) -> None:
    current = observe_ref(client, full_name, ref)
    if current == expected_sha:
        return
    if current is not None:
        raise Refused("ref-identity-conflict")
    try:
        status, _ = client.request("POST", f"repos/{full_name}/git/refs", {"ref": ref, "sha": expected_sha})
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        status = 0
    observed = observe_ref(client, full_name, ref)
    if observed == expected_sha:
        return
    if status in {200, 201}:
        raise Refused("ref-response-without-readback")
    raise Refused("ref-pending-requires-readback")


def read_content(client: GitHub, full_name: str, path: str, ref: str) -> tuple[str, bytes] | None:
    status, value = client.request("GET", f"repos/{full_name}/contents/{path}?ref={ref}")
    if status == 404:
        return None
    root = expect_json(status, value, 200, "content-readback")
    try:
        content = base64.b64decode(str(root.get("content", "")).replace("\n", ""), validate=True)
    except ValueError as error:
        raise Refused("content-readback-base64") from error
    blob_sha = root.get("sha")
    if not isinstance(blob_sha, str) or not OID.fullmatch(blob_sha):
        raise Refused("content-readback-sha")
    return blob_sha, content


def ensure_content(client: GitHub, full_name: str, path: str, branch: str, content: bytes) -> None:
    existing = read_content(client, full_name, path, branch)
    if existing is not None:
        if existing[1] != content:
            raise Refused("setup-content-conflict")
        return
    body = {"branch": branch, "content": base64.b64encode(content).decode(), "message": f"Add {path} for V2-CALL-01.4b"}
    try:
        status, _ = client.request("PUT", f"repos/{full_name}/contents/{path}", body)
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        status = 0
    observed = read_content(client, full_name, path, branch)
    if observed is not None and observed[1] == content:
        return
    if status in {200, 201}:
        raise Refused("content-response-without-readback")
    raise Refused("content-pending-requires-readback")


def journal_address(repository_id: int, node_id: str) -> tuple[str, str]:
    identity = f"ordinary:{repository_id}:{node_id}".lower().encode()
    payload = str(len(identity)).encode() + b":" + identity
    value = digest(payload)
    return f"refs/heads/fsgg/v2/journal/operation/{value[:2]}", value


def ensure_pull_request(client: GitHub, full_name: str, owner: str, base: str, retained: dict[str, object] | None = None) -> dict[str, object]:
    if retained is not None:
        number = retained.get("number")
        if not isinstance(number, int):
            raise Refused("retained-pull-request-identity")
        status, value = client.request("GET", f"repos/{full_name}/pulls/{number}")
        root = expect_json(status, value, 200, "retained-pull-request-readback")
        if (root.get("node_id") != retained.get("nodeId")
                or (root.get("head") or {}).get("sha") != retained.get("headSha")
                or (root.get("base") or {}).get("sha") != retained.get("baseSha")):
            raise Refused("retained-pull-request-identity")
        return root
    query = f"repos/{full_name}/pulls?state=open&head={owner}:{SOURCE_BRANCH}&base={base}"
    status, existing = client.request("GET", query)
    if status != 200 or not isinstance(existing, list):
        raise Refused(f"pull-request-readback:{status}")
    if len(existing) > 1:
        raise Refused("pull-request-duplicate")
    if len(existing) == 1:
        return existing[0]
    body = {"title": "V2-CALL-01.4b synthetic delivery", "head": SOURCE_BRANCH, "base": base,
            "body": "Synthetic-only disposable qualification target; no production authority."}
    try:
        status, _ = client.request("POST", f"repos/{full_name}/pulls", body)
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        status = 0
    read_status, observed = client.request("GET", query)
    if read_status == 200 and isinstance(observed, list) and len(observed) == 1:
        return observed[0]
    if status in {200, 201}:
        raise Refused("pull-request-response-without-readback")
    raise Refused("pull-request-pending-requires-readback")


def observe_check(client: GitHub, full_name: str, head_sha: str) -> tuple[int, str]:
    status, value = client.request("GET", f"repos/{full_name}/commits/{head_sha}/check-runs?per_page=100")
    root = expect_json(status, value, 200, "check-readback")
    runs = root.get("check_runs")
    total = root.get("total_count")
    if not isinstance(runs, list) or not isinstance(total, int) or total != len(runs):
        raise Refused("check-pagination-incomplete")
    matched = [item for item in runs if isinstance(item, dict) and item.get("name") == CHECK_NAME]
    if len(matched) != 1:
        raise Refused("required-check-identity")
    item = matched[0]
    app = item.get("app")
    app_id = app.get("id") if isinstance(app, dict) else None
    if item.get("status") != "completed" or item.get("conclusion") != "success" or not isinstance(app_id, int):
        raise Refused("required-check-not-passed")
    return app_id, CHECK_NAME


def protection_body(app_id: int) -> dict[str, object]:
    return {
        "required_status_checks": {"strict": True, "checks": [{"context": CHECK_NAME, "app_id": app_id}]},
        "enforce_admins": False, "required_pull_request_reviews": None, "restrictions": None,
    }


def protection_matches(value: object, app_id: int) -> bool:
    if not isinstance(value, dict):
        return False
    required = value.get("required_status_checks")
    checks = required.get("checks") if isinstance(required, dict) else None
    admins = value.get("enforce_admins")
    return (isinstance(required, dict) and required.get("strict") is True
            and isinstance(checks, list) and len(checks) == 1
            and checks[0].get("context") == CHECK_NAME and checks[0].get("app_id") == app_id
            and (admins is False or isinstance(admins, dict) and admins.get("enabled") is False)
            and value.get("required_pull_request_reviews") is None and value.get("restrictions") is None)


def journal_protection_body() -> dict[str, object]:
    return {"required_status_checks": None, "enforce_admins": False,
            "required_pull_request_reviews": None, "restrictions": None,
            "allow_force_pushes": False, "allow_deletions": False}


def journal_protection_matches(value: object) -> bool:
    if not isinstance(value, dict):
        return False
    admins = value.get("enforce_admins")
    forces = value.get("allow_force_pushes")
    deletes = value.get("allow_deletions")
    return (value.get("required_status_checks") is None
            and (admins is False or isinstance(admins, dict) and admins.get("enabled") is False)
            and value.get("required_pull_request_reviews") is None and value.get("restrictions") is None
            and (forces is False or isinstance(forces, dict) and forces.get("enabled") is False)
            and (deletes is False or isinstance(deletes, dict) and deletes.get("enabled") is False))


def ensure_protection(client: GitHub, full_name: str, base: str, app_id: int) -> None:
    path = f"repos/{full_name}/branches/{base}/protection"
    status, current = client.request("GET", path)
    if status == 200:
        if not protection_matches(current, app_id):
            raise Refused("branch-protection-conflict")
        return
    if status != 404:
        raise Refused(f"branch-protection-capability:{status}")
    try:
        write_status, _ = client.request("PUT", path, protection_body(app_id))
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        write_status = 0
    read_status, observed = client.request("GET", path)
    if read_status == 200 and protection_matches(observed, app_id):
        return
    if write_status in {200, 201}:
        raise Refused("branch-protection-response-without-readback")
    raise Refused("branch-protection-pending-requires-readback")


def ensure_journal_protection(client: GitHub, full_name: str, journal_ref: str) -> None:
    branch = journal_ref.removeprefix("refs/heads/")
    path = f"repos/{full_name}/branches/{branch}/protection"
    status, current = client.request("GET", path)
    if status == 200:
        if not journal_protection_matches(current):
            raise Refused("journal-protection-conflict")
        return
    if status != 404:
        raise Refused(f"journal-protection-capability:{status}")
    try:
        write_status, _ = client.request("PUT", path, journal_protection_body())
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        write_status = 0
    read_status, observed = client.request("GET", path)
    if read_status == 200 and journal_protection_matches(observed):
        return
    if write_status in {200, 201}:
        raise Refused("journal-protection-response-without-readback")
    raise Refused("journal-protection-pending-requires-readback")


def installed_command(command: str, expected_sha: str) -> pathlib.Path:
    executable = pathlib.Path(command).resolve()
    if executable.is_symlink() or not executable.is_file():
        raise Refused("installed-command-missing")
    roots = list((executable.parent / ".store" / "fs.gg.coordination.cli" / "0.1.0").rglob("FS.GG.Coordination.Cli.dll"))
    if len(roots) != 1 or digest(read_regular(roots[0])) != expected_sha:
        raise Refused("installed-command-digest")
    return executable


def run_cli(command: pathlib.Path, arguments: list[str], token_environment: str) -> subprocess.CompletedProcess[bytes]:
    environment = os.environ.copy()
    if not environment.get(token_environment):
        raise Refused("missing-github-token")
    return subprocess.run([str(command), *arguments], cwd=pathlib.Path.cwd(), env=environment,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, check=False)


def installation_repositories(client: GitHub, label: str) -> list[dict[str, object]]:
    status, value = client.request("GET", "installation/repositories?per_page=100")
    if status != 200 or not isinstance(value, dict):
        raise Refused(f"{label}-repository-readback:{status}")
    repositories = value.get("repositories")
    if (not isinstance(repositories, list) or not isinstance(value.get("total_count"), int)
            or value["total_count"] != len(repositories)):
        raise Refused(f"{label}-repository-pagination-incomplete")
    return [item for item in repositories if isinstance(item, dict)]


def live_observation(clients: dict[str, GitHub], contract: dict[str, object], plan: dict[str, object], grant: dict[str, object], artifact_envelope: dict[str, object], tool_command: str | None = None) -> dict[str, object]:
    authority = grant["authority"]
    if set(clients) != set(ROLE_NAMES):
        raise Refused("credential-client-role-set")
    validate_artifact_envelope(contract, grant, artifact_envelope)
    authority_client = clients["authority-observer"]
    status, run = authority_client.request("GET", f"repos/{authority['repository']}/actions/runs/{authority['runId']}")
    if status != 200:
        raise Refused(f"protected-run-readback:{status}")
    status, approvals_raw = authority_client.request("GET", f"repos/{authority['repository']}/actions/runs/{authority['runId']}/approvals")
    if status != 200 or not isinstance(approvals_raw, list):
        raise Refused(f"protected-approval-readback:{status}")
    status, workflow = authority_client.request("GET", f"repos/{authority['repository']}/contents/{authority['workflowPath']}?ref={authority['workflowRevision']}")
    if status != 200 or not isinstance(workflow, dict):
        raise Refused(f"protected-workflow-readback:{status}")
    try:
        workflow_bytes = base64.b64decode(str(workflow.get("content", "")).replace("\n", ""), validate=True)
    except ValueError as error:
        raise Refused("protected-workflow-content") from error
    artifact_id = artifact_envelope["artifactId"]
    status, artifact = authority_client.request("GET", f"repos/{authority['repository']}/actions/artifacts/{artifact_id}")
    if status != 200 or not isinstance(artifact, dict):
        raise Refused(f"grant-artifact-readback:{status}")
    status, artifact_archive = authority_client.request_bytes(
        "GET", f"repos/{authority['repository']}/actions/artifacts/{artifact_id}/zip")
    if status != 200:
        raise Refused(f"grant-artifact-content-readback:{status}")
    artifact_sha256 = str(artifact.get("digest", "")).removeprefix("sha256:")
    if (artifact.get("id") != artifact_envelope["artifactId"]
            or artifact.get("name") != artifact_envelope["artifactName"]
            or artifact_sha256 != artifact_envelope["artifactSha256"]
            or digest(artifact_archive) != artifact_envelope["artifactSha256"]
            or (artifact.get("workflow_run") or {}).get("id") != artifact_envelope["workflowRunId"]
            or artifact.get("expires_at") != artifact_envelope["expiresAt"]
            or artifact.get("expired") is not False):
        raise Refused("grant-artifact-envelope-readback")
    artifact_grant = grant_artifact_payload(artifact_archive)
    if artifact_grant != grant:
        raise Refused("grant-artifact-content-binding")
    reviewer = contract["authorization"]["requiredReviewer"]
    status, membership = clients["reviewer-membership-observer"].request("GET", f"orgs/{contract['target']['owner']}/memberships/{reviewer['login']}")
    if status != 200:
        raise Refused(f"reviewer-membership-readback:{status}")
    granted_credentials = grant.get("credentials") or {}
    installations = {}
    for role in ROLE_NAMES:
        identity = granted_credentials.get(role) or {}
        status, installation = clients["app-installation-observer"].request(
            "GET", f"app/installations/{identity.get('installationId')}")
        if status != 200 or not isinstance(installation, dict):
            raise Refused(f"credential-readback:{role}:{status}")
        installations[role] = installation
    role_repositories = {
        role: installation_repositories(clients[role], role)
        for role in ROLE_NAMES if role != "app-installation-observer"
    }
    target_observation = None
    phase_client = clients["creation"] if plan["phase"] == "creation" else clients["setup"]
    target_status, target = phase_client.request("GET", f"repos/{contract['target']['fullName']}")
    if plan["phase"] == "creation":
        target_observation = {"httpStatus": target_status}
        creation_installation = installations["creation"]
        capabilities = {
            "createRepository": creation_installation.get("repository_selection") == "all" and creation_installation.get("account", {}).get("login") == contract["target"]["owner"],
            "publicRepository": contract["target"]["visibility"] == "public",
            "authoritativeReadback": target_status == 404,
        }
    elif target_status == 200 and isinstance(target, dict):
        target_observation = {"repositoryId": target.get("id"), "fullName": target.get("full_name"), "visibility": target.get("visibility")}
        base = target.get("default_branch")
        protection_status, _ = clients["setup"].request("GET", f"repos/{contract['target']['fullName']}/branches/{base}/protection")
        capabilities = {
            "branchProtection": protection_status in {200, 404},
            "checks": True,
            "contents": True,
            "deleteRepository": True,
            "installedCli": tool_command is not None and bool(installed_command(tool_command, contract["package"]["installedManagedCommandSha256"])),
            "journalRefs": True,
            "pullRequests": True,
            "workflows": True,
        }
    else:
        capabilities = {}
    return {
        "schema": "fsgg.coordination.callable-isolated-admission-observation/1",
        "complete": True,
        "contractSha256": contract["contractSha256"],
        "planSeal": plan["seal"],
        "protectedRun": {"id": run.get("id"), "attempt": run.get("run_attempt"), "conclusion": run.get("conclusion"), "event": run.get("event"), "headSha": run.get("head_sha"), "path": run.get("path")},
        "workflow": {"revision": authority["workflowRevision"], "sha256": digest(workflow_bytes)},
        "grantArtifact": {"repository": authority["repository"], "id": artifact.get("id"), "name": artifact.get("name"),
                          "sha256": artifact_sha256,
                          "payloadSha256": digest(canonical(artifact_grant)),
                          "workflowRunId": (artifact.get("workflow_run") or {}).get("id"),
                          "workflowRunAttempt": run.get("run_attempt"), "expiresAt": artifact.get("expires_at"),
                          "expired": artifact.get("expired")},
        "approvals": [{"state": item.get("state"), "userId": (item.get("user") or {}).get("id"),
                       "environment": ((item.get("environments") or [{}])[0]).get("name"),
                       "environmentId": ((item.get("environments") or [{}])[0]).get("id")}
                      for item in approvals_raw if isinstance(item, dict)],
        "reviewerMembership": {"userId": reviewer["id"], "state": membership.get("state")},
        "credentials": {
            role: {
                **granted_credentials[role],
                "account": (installations[role].get("account") or {}).get("login"),
                "accountType": (installations[role].get("account") or {}).get("type"),
                "repositorySelection": installations[role].get("repository_selection"),
                "repositoryIds": [item.get("id") for item in role_repositories.get(role, [])],
                "repositoryFullNames": [item.get("full_name") for item in role_repositories.get(role, [])],
                **({"organizationScope": (installations[role].get("account") or {}).get("login"),
                    "canCreateRepositories": installations[role].get("repository_selection") == "all"}
                   if role == "creation" else {}),
            }
            for role in ROLE_NAMES
        },
        "target": target_observation,
        "capabilities": capabilities,
    }


def creation_state(contract: dict[str, object], plan: dict[str, object], intent_at: dt.datetime) -> dict[str, object]:
    return sealed({
        "schema": "fsgg.coordination.callable-isolated-creation-progress/1",
        "operationIdentity": contract["identity"], "contractSha256": contract["contractSha256"],
        "planSeal": plan["seal"], "attemptIdentity": plan["attemptIdentity"],
        "requestSha256": digest(canonical(plan["request"])), "intentAt": timestamp(intent_at),
        "stage": "intent-persisted",
    })


def bound_progress(path: str | pathlib.Path, schema: str, contract: dict[str, object], plan: dict[str, object]) -> dict[str, object] | None:
    item = pathlib.Path(path)
    if not item.exists():
        return None
    value = read_json(item)
    if (value.get("schema") != schema or value.get("operationIdentity") != contract["identity"]
            or value.get("contractSha256") != contract["contractSha256"] or value.get("planSeal") != plan["seal"]):
        raise Refused("progress-binding")
    verify_digest(value, "seal", "progress")
    return value


def execute_creation(client: GitHub, contract: dict[str, object], plan: dict[str, object], state_path: str | pathlib.Path, now: dt.datetime) -> dict[str, object]:
    validate_plan(contract, plan)
    target = contract["target"]
    progress = bound_progress(state_path, "fsgg.coordination.callable-isolated-creation-progress/1", contract, plan)
    status, before = client.request("GET", f"repos/{target['fullName']}")
    if progress is None:
        if status != 404:
            raise Refused("creation-prestate-not-absent")
        progress = creation_state(contract, plan, now)
        write_private(state_path, progress)
    elif progress.get("requestSha256") != digest(canonical(plan["request"])):
        raise Refused("creation-progress-request")
    if status == 404:
        request = plan["request"]
    elif status == 200:
        request = None
    else:
        raise Refused(f"creation-readback-unknown:{status}")
    try:
        status = 0 if request is None else client.request(request["method"], request["path"], request["body"])[0]
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        status = 0
    read_status, created = client.request("GET", f"repos/{target['fullName']}")
    if read_status != 200 or not isinstance(created, dict):
        if status in {200, 201}:
            raise Refused("creation-response-without-authoritative-readback")
        raise Refused("creation-pending-requires-readback")
    observed = {
        "fullName": created.get("full_name"), "owner": (created.get("owner") or {}).get("login"),
        "repositoryId": created.get("id"), "nodeId": created.get("node_id"),
        "visibility": created.get("visibility"), "defaultBranch": created.get("default_branch"), "syntheticOnly": True,
    }
    retained_target = progress.get("target")
    if isinstance(retained_target, dict) and retained_target.get("repositoryId") != observed["repositoryId"]:
        raise Refused("creation-readback-replacement")
    marker = plan["request"]["body"]["homepage"]
    created_at = parse_time(created.get("created_at"), "creation-created-at")
    intent_at = parse_time(progress.get("intentAt"), "creation-intent-at")
    if (observed["fullName"] != target["fullName"] or observed["owner"] != target["owner"]
            or observed["visibility"] != "public" or created.get("homepage") != marker
            or created_at < intent_at or created.get("archived") is True):
        raise Refused("creation-readback-identity")
    receipt = {
        "schema": "fsgg.coordination.callable-isolated-creation-receipt/1", "contractSha256": contract["contractSha256"],
        "creationPlanSeal": plan["seal"], "authoritativeReadback": True, "target": observed,
    }
    receipt["receiptSha256"] = digest(canonical(receipt))
    settled = dict(progress)
    settled.pop("seal", None)
    settled.update({"stage": "settled", "target": observed, "creationReceipt": receipt})
    write_private(state_path, sealed(settled))
    return receipt


def execute_identity_bound(client: GitHub, contract: dict[str, object], plan: dict[str, object], command: str,
                           token_environment: str, receipt_path: str | pathlib.Path, cleanup_client: GitHub | None = None,
                           grant: dict[str, object] | None = None, now_provider=utc_now) -> dict[str, object]:
    validate_plan(contract, plan)
    cleanup_client = cleanup_client or client
    target = plan["target"]
    if plan["stages"] != ["setup", "installed-execution", "independent-readback", "cleanup"]:
        raise Refused("operation-stage-order")
    retained = read_json(receipt_path) if pathlib.Path(receipt_path).is_file() else None
    if retained is not None:
        if (retained.get("contractSha256") != contract["contractSha256"] or retained.get("planSeal") != plan["seal"]
                or retained.get("operationIdentity") != contract["identity"]):
            raise Refused("progress-binding")
        verify_digest(retained, "receiptSha256" if retained.get("schema", "").endswith("receipt/1") else "seal", "operation-progress")
    status, observed = client.request("GET", f"repos/{target['fullName']}")
    if status == 404:
        cleanup = retained.get("cleanup") if isinstance(retained, dict) else None
        if not isinstance(cleanup, dict) or cleanup.get("state") not in {"intent-persisted", "settled"} or cleanup.get("expectedRepositoryId") != target["repositoryId"]:
            raise Refused("cleanup-readback-without-bound-intent")
        if retained.get("schema") != "fsgg.coordination.callable-isolated-operation-receipt/1":
            raise Refused("cleanup-missing-retained-evidence")
        retained["cleanup"] = {"state": "settled", "expectedRepositoryId": target["repositoryId"], "repositoryHttpStatus": 404}
        retained["receiptSha256"] = digest(canonical(without(retained, "receiptSha256")))
        write_private(receipt_path, retained)
        return retained
    if status != 200 or not isinstance(observed, dict) or observed.get("id") != target["repositoryId"] or observed.get("visibility") != "public":
        raise Refused("operation-target-readback")
    base = target.get("defaultBranch")
    if base != "main" or observed.get("default_branch") != base:
        raise Refused("operation-default-branch")
    if retained is None:
        retained = sealed({
            "schema": "fsgg.coordination.callable-isolated-operation-progress/1",
            "operationIdentity": contract["identity"], "contractSha256": contract["contractSha256"],
            "planSeal": plan["seal"], "stage": "setup-intent",
            "target": {"repositoryId": target["repositoryId"], "fullName": target["fullName"]},
            "checkpoints": [],
        })
        write_private(receipt_path, retained)
    if grant is not None:
        require_unexpired(grant, now_provider())
    base_sha = observe_ref(client, target["fullName"], f"refs/heads/{base}")
    if base_sha is None:
        raise Refused("operation-base-missing")
    source_ref = f"refs/heads/{SOURCE_BRANCH}"
    if observe_ref(client, target["fullName"], source_ref) is None:
        ensure_ref(client, target["fullName"], source_ref, base_sha)
    ensure_content(client, target["fullName"], ".github/workflows/callable-synthetic.yml", SOURCE_BRANCH, WORKFLOW_BYTES)
    ensure_content(client, target["fullName"], "synthetic.txt", SOURCE_BRANCH, SOURCE_BYTES)
    ensure_content(client, target["fullName"], "epoch.json", SOURCE_BRANCH, EPOCH_BYTES)
    source_sha = observe_ref(client, target["fullName"], source_ref)
    if source_sha is None:
        raise Refused("operation-source-missing")
    tree_status, tree = client.request("GET", f"repos/{target['fullName']}/git/trees/{source_sha}?recursive=1")
    tree_root = expect_json(tree_status, tree, 200, "operation-tree-readback")
    if tree_root.get("truncated") is True:
        raise Refused("operation-tree-incomplete")
    blobs = {item.get("path") for item in tree_root.get("tree", []) if isinstance(item, dict) and item.get("type") == "blob"}
    if blobs != {"README.md", ".github/workflows/callable-synthetic.yml", "synthetic.txt", "epoch.json"}:
        raise Refused("operation-tree-unexpected-content")
    retained_pull = retained.get("pullRequest") if isinstance(retained.get("pullRequest"), dict) else None
    pull = ensure_pull_request(client, target["fullName"], target["owner"], base, retained_pull)
    number, node_id, head, pull_base = pull.get("number"), pull.get("node_id"), pull.get("head"), pull.get("base")
    if (not isinstance(number, int) or not isinstance(node_id, str) or not isinstance(head, dict)
            or head.get("sha") != source_sha or not isinstance(pull_base, dict) or pull_base.get("sha") != base_sha):
        raise Refused("pull-request-identity")
    app_id, check_name = observe_check(client, target["fullName"], source_sha)
    ensure_protection(client, target["fullName"], base, app_id)
    ensure_ref(client, target["fullName"], POLICY_REF, source_sha)
    ensure_ref(client, target["fullName"], EPOCH_REF, source_sha)
    journal_ref, journal_digest = journal_address(target["repositoryId"], node_id)
    ensure_ref(client, target["fullName"], journal_ref, source_sha)
    ensure_journal_protection(client, target["fullName"], journal_ref)
    executable = installed_command(command, contract["package"]["installedManagedCommandSha256"])
    common = ["delivery", "--provider", "github", "--repository", target["fullName"], "--pr", str(number),
              "--token-env", token_environment, "--policy-ref", POLICY_REF,
              "--epoch-repository", target["fullName"], "--epoch-ref", EPOCH_REF, "--epoch-path", "epoch.json",
              "--journal-repository", target["fullName"]]
    encoded_plan = retained.get("installedPlanBase64")
    if encoded_plan is None:
        if retained.get("stage") in {"cli-intent", "native-readback", "cleanup-intent"}:
            raise Refused("retained-plan-missing")
        planned = run_cli(executable, ["delivery", "plan", *common[1:]], token_environment)
        if planned.returncode != 0 or not planned.stdout:
            raise Refused("installed-plan-refused")
        installed_plan = planned.stdout
        try:
            installed_plan_value = json.loads(installed_plan)
        except json.JSONDecodeError as error:
            raise Refused("installed-plan-json") from error
        installed_plan_seal = installed_plan_value.get("seal")
        if not SHA256.fullmatch(str(installed_plan_seal or "")):
            raise Refused("installed-plan-seal")
        progress = without(retained, "seal")
        progress.update({"stage": "cli-intent", "baseSha": base_sha, "sourceSha": source_sha,
                         "pullRequest": {"number": number, "nodeId": node_id, "headSha": source_sha, "baseSha": base_sha},
                         "journalRef": journal_ref, "journalAddress": journal_digest,
                         "installedPlanBase64": base64.b64encode(installed_plan).decode(),
                         "installedPlanSha256": digest(installed_plan), "installedPlanSeal": installed_plan_seal})
        retained = sealed(progress)
        write_private(receipt_path, retained)
    else:
        try:
            installed_plan = base64.b64decode(str(encoded_plan), validate=True)
        except ValueError as error:
            raise Refused("retained-plan-invalid") from error
        if digest(installed_plan) != retained.get("installedPlanSha256") or not SHA256.fullmatch(str(retained.get("installedPlanSeal", ""))):
            raise Refused("retained-plan-digest")
        expected_pull = retained.get("pullRequest")
        if not isinstance(expected_pull, dict) or expected_pull != {"number": number, "nodeId": node_id, "headSha": source_sha, "baseSha": base_sha}:
            raise Refused("retained-pull-request-identity")
    plan_file = pathlib.Path(str(receipt_path) + ".installed-plan.json")
    write_private_bytes(plan_file, installed_plan)
    pull_status, pull_readback = client.request("GET", f"repos/{target['fullName']}/pulls/{number}")
    pull_value = expect_json(pull_status, pull_readback, 200, "pull-readback-before-dispatch")
    already_merged = pull_value.get("merged") is True
    if not already_merged:
        if grant is not None:
            require_unexpired(grant, now_provider())
        try:
            advanced = run_cli(executable, ["delivery", "advance", *common[1:], "--plan", str(plan_file)], token_environment)
        except (subprocess.TimeoutExpired, OSError) as error:
            raise Refused("installed-advance-outcome-unknown-requires-readback") from error
        if advanced.returncode != 0 or b"AdvancePending" in advanced.stdout or b"AdvanceSettled" not in advanced.stdout:
            raise Refused("installed-advance-not-settled")
        pull_status, pull_readback = client.request("GET", f"repos/{target['fullName']}/pulls/{number}")
        pull_value = expect_json(pull_status, pull_readback, 200, "merged-pull-readback")
    merge_commit = pull_value.get("merge_commit_sha")
    if pull_value.get("node_id") != node_id or pull_value.get("merged") is not True or not isinstance(merge_commit, str) or not OID.fullmatch(merge_commit):
        raise Refused("merged-pull-not-settled")
    journal_status, journal = client.request("GET", f"repos/{target['fullName']}/contents/ordinary/{journal_digest}.json?ref={journal_ref}")
    journal_value = expect_json(journal_status, journal, 200, "journal-readback")
    try:
        journal_bytes = base64.b64decode(str(journal_value.get("content", "")).replace("\n", ""), validate=True)
        journal_state = json.loads(journal_bytes)
    except (ValueError, json.JSONDecodeError) as error:
        raise Refused("journal-readback-content") from error
    if (journal_state.get("schema") != "fsgg.coordination.ordinary-delivery-journal/1"
            or journal_state.get("stage") != "settled" or journal_state.get("mergeCommit") != merge_commit
            or journal_state.get("operationId") != contract["identity"]
            or journal_state.get("planDigest") != retained.get("installedPlanSeal")
            or not isinstance(journal_state.get("generation"), int) or journal_state["generation"] < 1
            or not OID.fullmatch(str(journal_value.get("sha", "")))):
        raise Refused("journal-not-settled")
    replayed = run_cli(executable, ["delivery", "advance", *common[1:], "--plan", str(plan_file)], token_environment)
    if replayed.returncode != 0 or b"AdvanceAlreadySettled" not in replayed.stdout:
        raise Refused("installed-replay-not-noop")
    receipt = {"schema": "fsgg.coordination.callable-isolated-operation-receipt/1",
        "operationIdentity": contract["identity"], "contractSha256": contract["contractSha256"], "planSeal": plan["seal"],
        "target": {"repositoryId": target["repositoryId"], "fullName": target["fullName"], "visibility": "public"},
        "source": {"baseSha": base_sha, "headSha": source_sha, "pullRequestNumber": number, "pullRequestNodeId": node_id,
                   "requiredCheck": check_name, "requiredCheckAppId": app_id, "policyRevision": source_sha,
                   "epochCommit": source_sha, "epochGeneration": 1, "journalRef": journal_ref},
        "installed": {"package": contract["package"], "planSha256": retained["installedPlanSha256"],
                      "planSeal": retained["installedPlanSeal"], "planBytesBase64": retained["installedPlanBase64"],
                      "firstOutcome": "native-readback" if already_merged else "AdvanceSettled", "freshProcessOutcome": "AdvanceAlreadySettled"},
        "nativeReadback": {"mergeCommit": merge_commit, "journalSha256": digest(journal_bytes),
                           "journalBlobSha": journal_value["sha"], "journalGeneration": journal_state["generation"],
                           "journalOperationId": journal_state["operationId"], "journalPlanDigest": journal_state["planDigest"],
                           "journalStage": "settled"},
        "cleanup": {"state": "intent-persisted", "expectedRepositoryId": target["repositoryId"]}}
    receipt["receiptSha256"] = digest(canonical(receipt))
    write_private(receipt_path, receipt)
    if grant is not None:
        require_unexpired(grant, now_provider())
    verify_status, verify_target = cleanup_client.request("GET", f"repos/{target['fullName']}")
    if verify_status != 200 or not isinstance(verify_target, dict) or verify_target.get("id") != target["repositoryId"]:
        raise Refused("cleanup-target-identity-drift")
    try:
        delete_status, _ = cleanup_client.request("DELETE", f"repos/{target['fullName']}")
    except Refused as error:
        if str(error) != "github-outcome-unknown-requires-readback":
            raise
        delete_status = 0
    final_status, _ = cleanup_client.request("GET", f"repos/{target['fullName']}")
    if final_status != 404:
        if delete_status == 204:
            raise Refused("cleanup-response-without-readback")
        raise Refused("cleanup-pending-requires-readback")
    receipt["cleanup"] = {"state": "settled", "expectedRepositoryId": target["repositoryId"], "repositoryHttpStatus": 404}
    receipt["receiptSha256"] = digest(canonical(without(receipt, "receiptSha256")))
    write_private(receipt_path, receipt)
    return receipt


def inspect(contract: dict[str, object], preflight: dict[str, object], proposal: dict[str, object] | None = None) -> dict[str, object]:
    validate_preflight(contract, preflight)
    if proposal is None or proposal.get("proposalSha256") is None:
        raise Refused("proposal-required")
    return {
        "schema": "fsgg.coordination.callable-isolated-operation-inspection/1",
        "operationIdentity": contract["identity"], "contractSha256": contract["contractSha256"],
        "state": "prepared-not-authorized", "authorized": False,
        "disposition": preflight["disposition"], "liveEffects": 0,
    }


def main(argv: list[str] | None = None, client_factory=GitHub, now_provider=utc_now) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--contract", default=str(DEFAULT_CONTRACT))
    parser.add_argument("--proposal", default=str(DEFAULT_PROPOSAL))
    parser.add_argument("--preflight", default=str(DEFAULT_PREFLIGHT))
    commands = parser.add_subparsers(dest="action", required=True)
    commands.add_parser("inspect")
    create = commands.add_parser("prepare-create")
    create.add_argument("--output", required=True)
    operation = commands.add_parser("prepare-operation")
    operation.add_argument("--creation-receipt", required=True)
    operation.add_argument("--output", required=True)
    admission = commands.add_parser("validate-admission")
    admission.add_argument("--plan", required=True)
    admission.add_argument("--grant", required=True)
    admission.add_argument("--grant-artifact-envelope", required=True)
    admission.add_argument("--observation", required=True)
    admission.add_argument("--now", required=True)
    execute = commands.add_parser("execute")
    execute.add_argument("--plan", required=True)
    execute.add_argument("--grant", required=True)
    execute.add_argument("--grant-artifact-envelope", required=True)
    execute.add_argument("--api-base", default="https://api.github.com/")
    for role in ROLE_NAMES:
        execute.add_argument(f"--{role}-token-env", required=True)
    execute.add_argument("--receipt")
    execute.add_argument("--command", dest="tool_command")
    args = parser.parse_args(argv)
    try:
        contract = load_contract(args.contract)
        preflight = read_json(args.preflight)
        proposal = load_proposal(args.proposal, contract, preflight)
        if args.action == "inspect":
            print(canonical(inspect(contract, preflight, proposal)).decode())
        elif args.action == "prepare-create":
            write_private(args.output, prepare_create(contract))
        elif args.action == "prepare-operation":
            write_private(args.output, prepare_operation(contract, read_json(args.creation_receipt)))
        elif args.action == "validate-admission":
            validate_admission(contract, read_json(args.plan), read_json(args.grant),
                               read_json(args.grant_artifact_envelope), read_json(args.observation),
                               parse_time(args.now, "now"))
            print('{"admission":"valid","effects":0}')
        else:
            if not args.receipt:
                raise Refused("receipt-path-required")
            plan = read_json(args.plan)
            grant = read_json(args.grant)
            artifact_envelope = read_json(args.grant_artifact_envelope)
            token_environments = {role: getattr(args, role.replace("-", "_") + "_token_env") for role in ROLE_NAMES}
            tokens = {role: os.environ.get(environment, "") for role, environment in token_environments.items()}
            if any(not token for token in tokens.values()):
                raise Refused("missing-github-token")
            if len(set(tokens.values())) != len(ROLE_NAMES):
                raise Refused("credential-token-role-alias")
            clients = {role: client_factory(tokens[role], args.api_base) for role in ROLE_NAMES}
            observation = live_observation(clients, contract, plan, grant, artifact_envelope, args.tool_command)
            validate_admission(contract, plan, grant, artifact_envelope, observation, now_provider())
            require_unexpired(grant, now_provider())
            if plan["phase"] == "creation":
                receipt = execute_creation(clients["creation"], contract, plan, args.receipt, now_provider())
            else:
                if not args.tool_command:
                    raise Refused("installed-command-required")
                receipt = execute_identity_bound(
                    clients["setup"], contract, plan, args.tool_command,
                    token_environments["execution"], args.receipt, clients["cleanup"], grant, now_provider)
            write_private(args.receipt, receipt)
            print(canonical({"outcome": "settled", "receiptSha256": receipt["receiptSha256"]}).decode())
        return 0
    except Refused as error:
        print(f"operation-refused:{error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
