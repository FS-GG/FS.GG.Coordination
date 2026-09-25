"""Canonical v5 issuer envelope checked through an injected verifier port.

The verifier port is deliberately unimplemented here. A fake ``True`` proves
only source contract consistency, never protected signature authenticity.
"""

from __future__ import annotations

import base64
import binascii
import copy
import dataclasses
import datetime as dt
import hashlib
import json
from typing import Any

import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_git_tree_membership as tree
import callable_isolated_v2_v5_install_approval as install_approval
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_runner_issuer as issuer

ENVELOPE_SCHEMA = "fsgg.coordination.v5-no-grant-issuer-envelope/1"
PAYLOAD_SCHEMA = "fsgg.coordination.v5-no-grant-issuer-payload/1"
AUDIENCE = "fsgg.coordination.v5-no-grant-runner/1"
POLICY = {"keyId", "nonce", "algorithm", "audience"}
ENVELOPE = {"schema", "payload", "signature"}
PAYLOAD = {"schema", "repository", "repositoryId", "revision",
           "sourceTree", "treeIdentityEventId", "artifactId",
           "manifestSha256", "archiveSha256", "installApprovalEventId",
           "runId", "runAttempt", "runnerActorId", "imageDigest",
           "interpreterSha256", "runtimeClosureSha256", "installPath",
           "issuerEventId", "issuerActorId", "keyId", "nonce",
           "algorithm", "audience", "issuedAt", "expiresAt"}
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "keyId", "algorithm", "expiresAt"}
PATHS = {builder.ENTRY_SOURCE: "entry",
         "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
         "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
         builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}


class Refused(ValueError):
    """Fixed refusal without signed bytes or reader credential contents."""


@dataclasses.dataclass(frozen=True)
class EnvelopeWitness:
    envelope_sha256: str
    key_id: str
    issuer_event_id: int
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("v5-envelope-duplicate")
        value[key] = item
    return value


def _nonfinite(_value):
    raise Refused("v5-envelope-nonfinite")


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-envelope-shape")


def _canonical(value):
    try:
        return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=True, allow_nan=False) + "\n").encode(
                               "ascii")
    except (ValueError, TypeError, UnicodeError):
        raise Refused("v5-envelope-json") from None


def _positive(value):
    return type(value) is int and value > 0


def qualify(selection: candidate.Selection, membership: tree.TreeWitness,
            readback: installed.Readback,
            approval: install_approval.Approval,
            issued: issuer.IssuerWitness,
            policy: dict, raw: bytes, verifier_port,
            now: dt.datetime) -> EnvelopeWitness:
    """Bind signed bytes to closed results; fake verification is not authority."""
    try:
        if (type(selection) is not candidate.Selection
                or type(membership) is not tree.TreeWitness
                or type(readback) is not installed.Readback
                or type(approval) is not install_approval.Approval
                or type(issued) is not issuer.IssuerWitness):
            raise Refused("v5-envelope-prior")
        originals = (selection, membership, readback, approval, issued, policy)
        selection, membership, readback, approval, issued, policy = copy.deepcopy(
            originals)
        _shape(policy, POLICY)
        if (type(raw) is not bytes or not 0 < len(raw) <= 8192
                or verifier_port is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)
                or not candidate._hex(policy["keyId"], candidate.HEX64)
                or not candidate._hex(policy["nonce"], candidate.HEX64)
                or policy["algorithm"] != "Ed25519"
                or policy["audience"] != AUDIENCE):
            raise Refused("v5-envelope-policy")
        try:
            value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                               parse_constant=_nonfinite)
        except Refused:
            raise
        except (UnicodeError, ValueError, TypeError):
            raise Refused("v5-envelope-json") from None
        _shape(value, ENVELOPE)
        if raw != _canonical(value) or value["schema"] != ENVELOPE_SCHEMA:
            raise Refused("v5-envelope-canonical")
        payload = value["payload"]
        _shape(payload, PAYLOAD)
        signature_text = value["signature"]
        if type(signature_text) is not str:
            raise Refused("v5-envelope-signature")
        try:
            signature = base64.b64decode(signature_text, validate=True)
        except (binascii.Error, ValueError):
            raise Refused("v5-envelope-signature") from None
        if (len(signature) != 64
                or base64.b64encode(signature).decode("ascii") != signature_text):
            raise Refused("v5-envelope-signature")
        expected_files = tuple(sorted((path, candidate.PINNED_BYTES[name])
                                      for path, name in PATHS.items()))
        if (any(result.authorized is not False
                or result.can_dispatch is not False
                or type(result.live_effects) is not int
                or result.live_effects != 0 for result in
                (selection, membership, readback, approval, issued))
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or membership.revision != selection.revision
                or membership.source_tree != selection.source_tree
                or membership.source_files != expected_files
                or not _positive(membership.identity_event_id)
                or readback.revision != selection.revision
                or readback.source_tree != selection.source_tree
                or readback.repository_id != selection.repository_id
                or readback.artifact_id != selection.artifact_id
                or readback.archive_sha256 != selection.archive_sha256
                or readback.manifest_sha256 != selection.manifest_sha256
                or approval.revision != selection.revision
                or approval.artifact_id != selection.artifact_id
                or issued.revision != selection.revision
                or issued.run_id != readback.run_id
                or issued.run_attempt != readback.run_attempt
                or not all(_positive(value) for value in
                           (selection.repository_id, selection.artifact_id,
                            approval.event_id, readback.run_id,
                            readback.run_attempt, readback.runner_actor_id,
                            issued.event_id, issued.issuer_actor_id))
                or not all(candidate._hex(getattr(readback, key),
                                            candidate.HEX64) for key in
                           ("image_digest", "interpreter_sha256",
                            "runtime_closure_sha256"))
                or not installed._path(readback.install_path)):
            raise Refused("v5-envelope-selection")
        expected = {"schema": PAYLOAD_SCHEMA,
            "repository": candidate.REPOSITORY,
            "repositoryId": selection.repository_id,
            "revision": selection.revision,
            "sourceTree": selection.source_tree,
            "treeIdentityEventId": membership.identity_event_id,
            "artifactId": selection.artifact_id,
            "manifestSha256": selection.manifest_sha256,
            "archiveSha256": selection.archive_sha256,
            "installApprovalEventId": approval.event_id,
            "runId": readback.run_id,
            "runAttempt": readback.run_attempt,
            "runnerActorId": readback.runner_actor_id,
            "imageDigest": readback.image_digest,
            "interpreterSha256": readback.interpreter_sha256,
            "runtimeClosureSha256": readback.runtime_closure_sha256,
            "installPath": readback.install_path,
            "issuerEventId": issued.event_id,
            "issuerActorId": issued.issuer_actor_id,
            "keyId": policy["keyId"], "nonce": policy["nonce"],
            "algorithm": "Ed25519", "audience": AUDIENCE,
            "issuedAt": issued.issued_at,
            "expiresAt": issued.expires_at}
        if any(type(payload[key]) is not type(value)
               or payload[key] != value for key, value in expected.items()):
            raise Refused("v5-envelope-claims")
        approved = candidate._time(approval.approved_at)
        issued_at = candidate._time(issued.issued_at)
        expires = candidate._time(issued.expires_at)
        started = candidate._time(readback.command_started_at)
        if not (now - dt.timedelta(minutes=30) <= approved <= issued_at
                <= started <= now < expires
                <= issued_at + dt.timedelta(minutes=30)):
            raise Refused("v5-envelope-time")
        scope = copy.deepcopy(verifier_port.scope())
        _shape(scope, SCOPE)
        principals = (selection.source_reader_principal,
            selection.review_reader_principal,
            membership.artifact_reader_principal,
            membership.workflow_reader_principal,
            membership.git_reader_principal,
            membership.identity_reader_principal,
            readback.probe_reader_principal,
            readback.audit_reader_principal,
            approval.reader_principal, issued.reader_principal,
            scope["principalId"])
        credentials = (selection.source_credential_id,
            selection.review_credential_id,
            membership.artifact_credential_id,
            membership.workflow_credential_id,
            membership.git_credential_id,
            membership.identity_credential_id,
            readback.probe_credential_id,
            readback.audit_credential_id,
            approval.reader_credential_id, issued.reader_credential_id,
            scope["credentialId"])
        if (any(type(item) is not str or not item for item in principals)
                or any(not candidate._hex(item, candidate.HEX64)
                       for item in credentials)
                or len(set(principals)) != len(principals)
                or len(set(credentials)) != len(credentials)
                or scope["repository"] != candidate.REPOSITORY
                or type(scope["repositoryId"]) is not int
                or scope["repositoryId"] != selection.repository_id
                or type(scope["permissions"]) is not list
                or scope["permissions"] != ["verify-signature"]
                or scope["keyId"] != policy["keyId"]
                or scope["algorithm"] != "Ed25519"
                or not now < candidate._time(scope["expiresAt"])
                       <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-envelope-verifier-scope")
        verified = verifier_port.verify(policy["keyId"], "Ed25519",
                                        _canonical(payload), signature)
        if verified is not True:
            raise Refused("v5-envelope-unverified")
        if (verifier_port.scope() != scope
                or originals != (selection, membership, readback,
                                 approval, issued, policy)):
            raise Refused("v5-envelope-drift")
        return EnvelopeWitness(hashlib.sha256(raw).hexdigest(),
                               policy["keyId"], issued.event_id)
    except Refused:
        raise
    except Exception:
        raise Refused("v5-envelope-unavailable") from None
