#!/usr/bin/env python3
"""Prepare, authorize, sign and verify the private GS2-08.2 operation packet."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

AUTHORITY = "FS-GG/FS.GG.Coordination.Authority"
FLEET_REF = "refs/heads/fsgg/v2/journal/cutover/d5"
AUTHORIZATION_WORKFLOW = ".github/workflows/gs2-ledger-protected-authorization.yml"
AUTHORIZATION_WORKFLOW_REVISION = "c00b4636688f95024b80c588d2410ca40e11f6e6"
AUTHORIZATION_WORKFLOW_SHA256 = "a778801d66751c3890826b0ca015a81758f7733ff09e5b9c5bde55e1f86c3b8f"
REVIEWERS = {1645484, 4456104}
DIMENSIONS = {"settings", "custody", "initialization", "monitoring"}


class Refused(RuntimeError):
    pass


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def sha(value):
    return hashlib.sha256(value).hexdigest()


def read_regular(path):
    value = pathlib.Path(path)
    if value.is_symlink() or not value.is_file():
        raise Refused("input-must-be-regular-nonsymlink:" + str(value))
    return value.read_bytes()


def read_json(path):
    return json.loads(read_regular(path))


def write_private(path, value, raw=False):
    target = pathlib.Path(path)
    if target.exists() and target.is_symlink():
        raise Refused("output-symlink")
    target.write_bytes(value if raw else canonical(value) + b"\n")
    target.chmod(0o600)


def utc(value):
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise Refused("time-zone-required")
    return parsed.astimezone(dt.timezone.utc)


def spki_sha(path):
    public_key = pathlib.Path(path)
    read_regular(public_key)
    completed = subprocess.run(
        ["openssl", "pkey", "-pubin", "-in", str(public_key), "-outform", "DER"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False)
    if completed.returncode != 0 or not completed.stdout:
        raise Refused("authorizer-public-key-invalid")
    return sha(completed.stdout)


def derive(args):
    accepted_bytes = read_regular(args.accepted_receipt)
    accepted = json.loads(accepted_bytes)
    policy_bytes = read_regular(args.desired_policy)
    policy = json.loads(policy_bytes)
    capture1 = read_json(args.first_capture)
    capture2 = read_json(args.second_capture)
    if accepted.get("schema") != "fsgg.coordination.unit-acceptance/1" or accepted.get("unitId") != "GS2-08.1" or accepted.get("state") != "accepted":
        raise Refused("accepted-genesis-receipt")
    epoch = next((item.get("sha256") for item in accepted.get("artifacts", []) if item.get("name") == "epoch-wire-protocol"), None)
    if not isinstance(epoch, str) or not re.fullmatch(r"[0-9a-f]{64}", epoch):
        raise Refused("accepted-epoch-wire")
    if policy.get("authorityRepositoryId") != 1351660651 or policy.get("fleetRef") != FLEET_REF:
        raise Refused("desired-policy-authority")
    if (policy.get("controlIssue") or {}).get("number") != 2 or policy.get("sharedAppProductionBlocker") is not False or policy.get("applyAuthorized") is not False:
        raise Refused("desired-policy-live-binding")
    if (policy.get("ordinaryWriter") or {}).get("appId") != 4882140 or (policy.get("cutoverWriter") or {}).get("appId") != 4882399:
        raise Refused("desired-policy-app-separation")
    expected_bindings = {"ordinaryWriterAppId": 4882140, "ordinaryWriterInstallationId": 160261608,
                         "cutoverWriterAppId": 4882399, "cutoverWriterInstallationId": 160261436,
                         "controlIssueNumber": 2}
    for capture, pass_number in ((capture1, 1), (capture2, 2)):
        if (capture.get("schema") != "fsgg.github-ledger-protection-live-capture/v1"
                or capture.get("repositoryId") != 1351660651 or capture.get("repository") != AUTHORITY
                or capture.get("capturePass") != pass_number or capture.get("bindings") != expected_bindings):
            raise Refused("prestate-capture-binding")
    if capture1.get("gaps") or capture2.get("gaps") or capture2.get("continuity") != "matched":
        raise Refused("prestate-not-coherent")
    if capture2.get("previousEvidenceSha256") != sha(read_regular(args.first_capture)):
        raise Refused("prestate-link")
    if capture1.get("normalizedSetSha256") != capture2.get("normalizedSetSha256") or capture1.get("rawSetSha256") != capture2.get("rawSetSha256"):
        raise Refused("prestate-drift")
    public_sha = spki_sha(args.authorizer_public_key)
    source_binding = sha(canonical({"commit": args.source_revision, "tree": args.source_tree}))
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_revision) or not re.fullmatch(r"[0-9a-f]{40}", args.source_tree):
        raise Refused("source-binding")
    manifest = {
        "schema": "fsgg.github-ledger-initial-manifest/1",
        "acceptedGenesis": {"unit": "GS2-08.1", "receiptSha256": sha(accepted_bytes), "receiptDigest": accepted["digest"], "epochWireSha256": epoch},
        "authority": {"repository": AUTHORITY, "repositoryId": 1351660651, "fleetId": "fs-gg-production", "ref": FLEET_REF,
                      "phaseTagPrefix": "refs/tags/fsgg/v2/fleet-cutover/operating-v1/"},
        "source": {"commit": args.source_revision, "tree": args.source_tree, "bindingSha256": source_binding},
        "authorizationWorkflow": {"repository": "FS-GG/.github", "path": AUTHORIZATION_WORKFLOW,
                                  "anchorRevision": AUTHORIZATION_WORKFLOW_REVISION,
                                  "sha256": AUTHORIZATION_WORKFLOW_SHA256},
        "desiredPolicySha256": sha(policy_bytes),
        "authorizer": {"keyId": args.authorizer_key_id, "publicKeySpkiSha256": public_sha},
    }
    manifest_bytes = canonical(manifest) + b"\n"
    trust = {
        "schema": "fsgg.github-ledger-initial-trust/1",
        "manifestSha256": sha(manifest_bytes),
        "acceptedGenesisReceiptDigest": accepted["digest"],
        "authorizer": {"keyId": args.authorizer_key_id, "algorithm": "RSA-PSS-SHA256", "publicKeySpkiSha256": public_sha},
    }
    trust_bytes = canonical(trust) + b"\n"
    tag = "refs/tags/fsgg/v2/fleet-cutover/operating-v1/genesis-" + sha(manifest_bytes)[:16]
    created = utc(args.created_at).isoformat(timespec="microseconds").replace("+00:00", "Z")
    initializer = {
        "repositoryId": 1351660651, "repository": AUTHORITY, "fleetId": "fs-gg-production", "ref": FLEET_REF, "tag": tag,
        "manifestSha256": sha(manifest_bytes), "trustAnchorSha256": sha(trust_bytes), "sourceSha256": source_binding,
        "desiredPolicySha256": sha(policy_bytes), "firstCaptureSha256": sha(read_regular(args.first_capture)),
        "secondCaptureSha256": sha(read_regular(args.second_capture)), "authorizationKeyId": args.authorizer_key_id,
        "authorizationKeySpkiSha256": public_sha, "cutoverAppId": 4882399, "cutoverInstallationId": 160261436,
        "authorizationWorkflowRevision": AUTHORIZATION_WORKFLOW_REVISION,
        "authorizationWorkflowSha256": AUTHORIZATION_WORKFLOW_SHA256,
        "controlIssueNumber": 2, "createdAt": created, "authorName": "FS.GG cutover", "authorEmail": "cutover@fs.gg",
    }
    output = pathlib.Path(args.output_dir)
    if output.is_symlink():
        raise Refused("output-directory-symlink")
    output.mkdir(mode=0o700, parents=True, exist_ok=True)
    if output.stat().st_mode & 0o077:
        raise Refused("output-directory-not-private")
    write_private(output / "initial-manifest.json", manifest)
    write_private(output / "trust-anchor.json", trust)
    write_private(output / "initializer-input.json", initializer)
    print(canonical({"schema": "fsgg.github-ledger-operation-derivation/1", "manifestSha256": sha(manifest_bytes),
                     "trustAnchorSha256": sha(trust_bytes), "sourceSha256": source_binding, "tag": tag}).decode())


def gh_json(args):
    completed = subprocess.run(["gh", "api", *args], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False)
    if completed.returncode != 0:
        raise Refused("protected-approval-readback")
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise Refused("protected-approval-json") from error


def sign_fd(payload, key_fd):
    completed = subprocess.run(
        ["openssl", "dgst", "-sha256", "-sign", f"/proc/self/fd/{key_fd}", "-sigopt", "rsa_padding_mode:pss"],
        input=payload, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, pass_fds=(key_fd,), check=False)
    if completed.returncode != 0 or not completed.stdout:
        raise Refused("signing-refused")
    return completed.stdout


def protected(args, input_sha):
    receipt = read_json(args.protected_receipt)
    if receipt.get("schema") != "fsgg.github-ledger-protected-authorization/1" or receipt.get("repository") != "FS-GG/.github":
        raise Refused("protected-receipt")
    if receipt.get("environment") != "fleet-cutover" or receipt.get("inputSha256") != input_sha:
        raise Refused("protected-receipt-binding")
    if receipt.get("coordinationRevision") != args.source_revision or receipt.get("conclusion") != "success":
        raise Refused("protected-receipt-source")
    if receipt.get("operationId") != args.operation_id:
        raise Refused("protected-receipt-operation")
    run_id = receipt.get("runId")
    if not isinstance(run_id, int) or run_id <= 0:
        raise Refused("protected-receipt-run")
    run = gh_json([f"repos/FS-GG/.github/actions/runs/{run_id}"])
    if run.get("conclusion") != "success" or run.get("head_sha") != receipt.get("dotgithubRevision"):
        raise Refused("protected-run-readback")
    head = receipt["dotgithubRevision"]
    comparison = gh_json([f"repos/FS-GG/.github/compare/{AUTHORIZATION_WORKFLOW_REVISION}...{head}"])
    if comparison.get("status") not in ("ahead", "identical"):
        raise Refused("protected-workflow-lineage")
    workflow = gh_json([f"repos/FS-GG/.github/contents/{AUTHORIZATION_WORKFLOW}", "-X", "GET", "-f", f"ref={head}"])
    try:
        workflow_bytes = base64.b64decode(str(workflow.get("content", "")).replace("\n", ""), validate=True)
    except ValueError as error:
        raise Refused("protected-workflow-readback") from error
    if sha(workflow_bytes) != AUTHORIZATION_WORKFLOW_SHA256:
        raise Refused("protected-workflow-drift")
    approvals = gh_json([f"repos/FS-GG/.github/actions/runs/{run_id}/approvals"])
    approved = {item.get("user", {}).get("id") for item in approvals if item.get("state") == "approved"}
    if not approved or not approved.issubset(REVIEWERS):
        raise Refused("protected-reviewers")
    now = utc(args.now)
    authorized = utc(receipt["approvedAt"])
    expires = utc(receipt["expiresAt"])
    if not (authorized <= now < expires) or expires - authorized > dt.timedelta(hours=2):
        raise Refused("protected-receipt-expired")
    return receipt, authorized, expires


def initializer_payload(payload, public_spki):
    value = json.loads(payload)
    if canonical(value) != payload:
        raise Refused("initializer-payload-not-canonical")
    expected = {
        "repositoryId": 1351660651, "repository": AUTHORITY, "fleetId": "fs-gg-production", "ref": FLEET_REF,
        "expectedRef": "absent", "cutoverAppId": 4882399, "cutoverInstallationId": 160261436,
        "controlIssueNumber": 2, "authorizationKeySpkiSha256": public_spki,
        "authorizationWorkflowRevision": AUTHORIZATION_WORKFLOW_REVISION,
        "authorizationWorkflowSha256": AUTHORIZATION_WORKFLOW_SHA256,
    }
    if any(value.get(key) != expected_value for key, expected_value in expected.items()):
        raise Refused("initializer-payload-binding")
    return value


def authorize(args):
    payload = read_regular(args.payload)
    input_sha = sha(payload)
    public_spki = spki_sha(args.public_key)
    if public_spki != args.public_key_spki_sha256:
        raise Refused("authorizer-public-key")
    initializer_payload(payload, public_spki)
    _, authorized, expires = protected(args, input_sha)
    signature = sign_fd(payload, args.private_key_fd)
    write_private(args.output, {
        "schema": "fsgg.github-ledger-initialization-authorization/1", "keyId": args.key_id,
        "publicKeySpkiSha256": args.public_key_spki_sha256, "payloadBase64": base64.b64encode(payload).decode(),
        "signatureBase64": base64.b64encode(signature).decode(),
        "authorizedAt": authorized.isoformat().replace("+00:00", "Z"), "expiresAt": expires.isoformat().replace("+00:00", "Z"),
    })
    print(canonical({"schema": "fsgg.github-ledger-authorization-result/1", "inputSha256": input_sha,
                     "protectedRunId": read_json(args.protected_receipt)["runId"], "expiresAt": expires.isoformat().replace("+00:00", "Z")}).decode())


def evidence_payload(value):
    allowed = {"authority", "controlIssueNumber", "desiredPolicySha256", "dimension", "expiresAt", "fleetRef",
               "initializationSeal", "inputSha256", "monitorStoreId", "observedAt", "providerObservationSha256",
               "repositoryId", "runId", "signerPublicKeySpkiSha256"}
    if set(value) != allowed or value.get("dimension") not in DIMENSIONS or value.get("authority") != "FS-GG/fleet-cutover":
        raise Refused("operational-evidence-shape")
    return canonical(value)


def sign_evidence(args):
    value = read_json(args.input)
    payload = evidence_payload(value)
    observed, expires, now = utc(value["observedAt"]), utc(value["expiresAt"]), utc(args.now)
    if not (observed <= now < expires) or expires - observed > dt.timedelta(minutes=15):
        raise Refused("operational-evidence-expiry")
    if spki_sha(args.public_key) != value["signerPublicKeySpkiSha256"]:
        raise Refused("operational-evidence-key")
    signature = sign_fd(payload, args.private_key_fd)
    envelope = dict(value)
    envelope.update({"schema": "fsgg.github-ledger-operational-evidence/1", "signerKeyId": args.key_id,
                     "publicKeySpkiSha256": value["signerPublicKeySpkiSha256"], "payloadBase64": base64.b64encode(payload).decode(),
                     "signatureBase64": base64.b64encode(signature).decode()})
    write_private(args.output, envelope)
    print(canonical({"schema": "fsgg.github-ledger-operational-signing-result/1", "dimension": value["dimension"],
                     "payloadSha256": sha(payload)}).decode())


def verify_signature(public_key, payload, signature):
    with tempfile.NamedTemporaryFile(mode="wb", delete=True) as signature_file:
        signature_file.write(signature)
        signature_file.flush()
        completed = subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(public_key), "-signature", signature_file.name,
                                    "-sigopt", "rsa_padding_mode:pss"], input=payload, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL, check=False)
    return completed.returncode == 0


def accept(args):
    capture1_bytes, capture2_bytes = read_regular(args.first_capture), read_regular(args.second_capture)
    capture1, capture2 = json.loads(capture1_bytes), json.loads(capture2_bytes)
    if capture2.get("continuity") != "matched" or capture1.get("gaps") or capture2.get("gaps") or capture2.get("previousEvidenceSha256") != sha(capture1_bytes):
        raise Refused("poststate-not-coherent")
    plan = read_json(args.plan)
    transport = read_json(args.transport_receipt)
    monitor = read_json(args.monitor_receipt)
    runner = read_json(args.runner_receipt)
    if plan.get("schema") != "fsgg.github-ledger-initialization-plan/1" or not isinstance(plan.get("seal"), str):
        raise Refused("initialization-plan")
    if transport.get("outcome") != "verified" or transport.get("commitOid") != plan.get("commitOid") or transport.get("mode") != "verify":
        raise Refused("initialization-not-verified")
    if monitor.get("outcome") != "green":
        raise Refused("monitor-not-green")
    if runner.get("schema") != "fsgg.github-ledger-external-runner-receipt/1" or runner.get("intervalSeconds") != 300 or runner.get("watchdogSeconds") != 900:
        raise Refused("external-runner")
    if not runner.get("alertTarget") or runner.get("alertExercise") != "delivered" or runner.get("durable") is not True:
        raise Refused("external-runner-alert")
    evidence = [read_json(path) for path in args.evidence]
    if {item.get("dimension") for item in evidence} != DIMENSIONS or len(evidence) != 4:
        raise Refused("operational-evidence-population")
    public = pathlib.Path(args.public_key)
    signer_spki = spki_sha(public)
    desired_policy_sha = sha(read_regular(args.desired_policy))
    now = utc(args.now)
    contexts = set()
    evidence_digests = []
    for item in evidence:
        value = {key: item.get(key) for key in ("authority", "controlIssueNumber", "desiredPolicySha256", "dimension", "expiresAt", "fleetRef",
                                                          "initializationSeal", "inputSha256", "monitorStoreId", "observedAt", "providerObservationSha256",
                                                          "repositoryId", "runId", "signerPublicKeySpkiSha256")}
        payload = evidence_payload(value)
        if (item.get("schema") != "fsgg.github-ledger-operational-evidence/1"
                or item.get("publicKeySpkiSha256") != signer_spki or value["signerPublicKeySpkiSha256"] != signer_spki
                or value["repositoryId"] != 1351660651 or value["fleetRef"] != FLEET_REF
                or value["controlIssueNumber"] != 2 or value["desiredPolicySha256"] != desired_policy_sha
                or value["providerObservationSha256"] != sha(capture2_bytes)
                or value["initializationSeal"] != plan["seal"] or value["monitorStoreId"] != runner["storeId"]
                or value["inputSha256"] != plan.get("inputSha256")):
            raise Refused("operational-evidence-binding")
        if base64.b64decode(item.get("payloadBase64", "")) != payload or not verify_signature(public, payload, base64.b64decode(item.get("signatureBase64", ""))):
            raise Refused("operational-evidence-signature")
        if not (utc(item["observedAt"]) <= now < utc(item["expiresAt"])):
            raise Refused("operational-evidence-stale")
        contexts.add(canonical({key: value[key] for key in value if key != "dimension"}))
        evidence_digests.append(sha(canonical(item) + b"\n"))
    if len(contexts) != 1:
        raise Refused("operational-evidence-context")
    receipt = {"schema": "fsgg.coordination.unit-acceptance-candidate/1", "unitId": "GS2-08.2", "state": "candidate",
               "sourceRevision": args.source_revision, "sourceTree": args.source_tree, "manifestSha256": args.manifest_sha256,
               "trustAnchorSha256": args.trust_anchor_sha256, "desiredPolicySha256": desired_policy_sha,
               "poststate": {"firstSha256": sha(capture1_bytes), "secondSha256": sha(capture2_bytes),
                             "normalizedSetSha256": capture2["normalizedSetSha256"]},
               "initialization": {"seal": plan["seal"], "commitOid": plan["commitOid"], "tag": plan["tag"]},
               "monitor": {"runId": monitor["runId"], "storeId": runner["storeId"], "runnerId": runner["runnerId"]},
               "operationalEvidenceSha256": sorted(evidence_digests), "createdAt": args.now}
    write_private(args.output, receipt)
    print(canonical({"schema": "fsgg.github-ledger-acceptance-production/1", "candidateSha256": sha(canonical(receipt) + b"\n")}).decode())


def parser():
    root = argparse.ArgumentParser()
    commands = root.add_subparsers(dest="command", required=True)
    derive_parser = commands.add_parser("derive")
    for name in ("accepted-receipt", "desired-policy", "first-capture", "second-capture", "authorizer-public-key", "authorizer-key-id", "source-revision", "source-tree", "created-at", "output-dir"):
        derive_parser.add_argument("--" + name, required=True)
    derive_parser.set_defaults(run=derive)
    authorize_parser = commands.add_parser("authorize")
    for name in ("payload", "protected-receipt", "public-key", "public-key-spki-sha256", "key-id", "source-revision", "operation-id", "now", "output"):
        authorize_parser.add_argument("--" + name, required=True)
    authorize_parser.add_argument("--private-key-fd", type=int, required=True)
    authorize_parser.set_defaults(run=authorize)
    evidence_parser = commands.add_parser("sign-evidence")
    for name in ("input", "public-key", "key-id", "now", "output"):
        evidence_parser.add_argument("--" + name, required=True)
    evidence_parser.add_argument("--private-key-fd", type=int, required=True)
    evidence_parser.set_defaults(run=sign_evidence)
    accept_parser = commands.add_parser("acceptance")
    for name in ("first-capture", "second-capture", "plan", "transport-receipt", "monitor-receipt", "runner-receipt", "public-key", "source-revision", "source-tree", "manifest-sha256", "trust-anchor-sha256", "desired-policy", "now", "output"):
        accept_parser.add_argument("--" + name, required=True)
    accept_parser.add_argument("--evidence", action="append", required=True)
    accept_parser.set_defaults(run=accept)
    return root


def main():
    try:
        args = parser().parse_args()
        if hasattr(args, "private_key_fd") and args.private_key_fd < 3:
            raise Refused("private-key-fd-must-be-at-least-3")
        args.run(args)
        return 0
    except (Refused, OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print("github ledger operation refused: " + str(error), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
