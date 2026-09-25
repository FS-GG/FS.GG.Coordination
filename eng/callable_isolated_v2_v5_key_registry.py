"""Pure fake key-registry witness for a closed v5 issuer envelope.

The registry reader is injected. A matching record does not authenticate the
registry, approve a protected key, or verify an Ed25519 signature.
"""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
from typing import Any

import callable_isolated_v2_v5_install_approval as install_approval
import callable_isolated_v2_v5_issuer_envelope as envelope
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_runner_issuer as issuer

RECORD_SCHEMA = "fsgg.coordination.v5-no-grant-key-registry/1"
SELECTED = {"registryEventId", "reviewerActorId", "keyId"}
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "expiresAt"}
RECORD = {"schema", "complete", "principalId", "credentialId",
          "eventId", "repository", "repositoryId", "revision",
          "sourceTree", "artifactId", "installApprovalEventId",
          "issuerEventId", "issuerActorId", "runId", "runAttempt",
          "envelopeSha256", "nonce", "keyId", "publicKeyBytes",
          "algorithm", "audience", "status", "revokedAt",
          "reviewerActorId", "approvedAt", "observedAt", "expiresAt"}


class Refused(ValueError):
    """Fixed refusal without key, event or credential contents."""


@dataclasses.dataclass(frozen=True)
class KeyWitness:
    key_id: str
    registry_event_id: int
    reviewer_actor_id: int
    public_key_sha256: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-key-registry-shape")


def _positive(value: Any):
    return type(value) is int and value > 0


def qualify(selection: candidate.Selection,
            approval: install_approval.Approval,
            issued: issuer.IssuerWitness,
            checked: envelope.EnvelopeWitness,
            selected: dict, registry_port,
            now: dt.datetime) -> KeyWitness:
    """Compare injected registry metadata only; no protected key is selected."""
    try:
        if (type(selection) is not candidate.Selection
                or type(approval) is not install_approval.Approval
                or type(issued) is not issuer.IssuerWitness
                or type(checked) is not envelope.EnvelopeWitness):
            raise Refused("v5-key-registry-prior")
        originals = (selection, approval, issued, checked, selected)
        selection, approval, issued, checked, selected = copy.deepcopy(
            originals)
        _shape(selected, SELECTED)
        if (any(result.authorized is not False
                or result.can_dispatch is not False
                or type(result.live_effects) is not int
                or result.live_effects != 0 for result in
                (selection, approval, issued, checked))
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or approval.revision != selection.revision
                or approval.artifact_id != selection.artifact_id
                or issued.revision != selection.revision
                or checked.issuer_event_id != issued.event_id
                or checked.key_id != selected["keyId"]
                or not candidate._hex(checked.envelope_sha256, candidate.HEX64)
                or not candidate._hex(checked.nonce, candidate.HEX64)
                or not candidate._hex(selected["keyId"], candidate.HEX64)
                or not all(_positive(value) for value in
                           (selection.repository_id, selection.artifact_id,
                            selection.producer_actor_id,
                            selection.reviewer_actor_id, approval.event_id,
                            approval.reviewer_actor_id, issued.event_id,
                            issued.issuer_actor_id, issued.run_id,
                            issued.run_attempt, selected["registryEventId"],
                            selected["reviewerActorId"]))
                or selected["reviewerActorId"] in
                   (selection.producer_actor_id,
                    selection.reviewer_actor_id,
                    approval.reviewer_actor_id,
                    issued.issuer_actor_id)
                or registry_port is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-key-registry-selection")
        scope = copy.deepcopy(registry_port.scope())
        _shape(scope, SCOPE)
        principals = (selection.source_reader_principal,
                      selection.review_reader_principal,
                      approval.reader_principal,
                      issued.reader_principal,
                      checked.verifier_reader_principal,
                      scope["principalId"])
        credentials = (selection.source_credential_id,
                       selection.review_credential_id,
                       approval.reader_credential_id,
                       issued.reader_credential_id,
                       checked.verifier_credential_id,
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
                or scope["permissions"] != ["read-key-registry"]
                or not now < candidate._time(scope["expiresAt"])
                       <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-key-registry-scope")
        record = copy.deepcopy(registry_port.read(selected["registryEventId"]))
        _shape(record, RECORD)
        expected = {"eventId": selected["registryEventId"],
            "repository": candidate.REPOSITORY,
            "repositoryId": selection.repository_id,
            "revision": selection.revision,
            "sourceTree": selection.source_tree,
            "artifactId": selection.artifact_id,
            "installApprovalEventId": approval.event_id,
            "issuerEventId": issued.event_id,
            "issuerActorId": issued.issuer_actor_id,
            "runId": issued.run_id,
            "runAttempt": issued.run_attempt,
            "envelopeSha256": checked.envelope_sha256,
            "nonce": checked.nonce,
            "keyId": selected["keyId"],
            "algorithm": "Ed25519",
            "audience": envelope.AUDIENCE,
            "reviewerActorId": selected["reviewerActorId"]}
        if (record["schema"] != RECORD_SCHEMA
                or record["complete"] is not True
                or record["principalId"] != scope["principalId"]
                or record["credentialId"] != scope["credentialId"]
                or record["status"] != "active"
                or record["revokedAt"] is not None
                or any(type(record[key]) is not type(value)
                       or record[key] != value
                       for key, value in expected.items())):
            raise Refused("v5-key-registry-binding")
        public_key = record["publicKeyBytes"]
        if (type(public_key) is not bytes or len(public_key) != 32
                or public_key == bytes(32)
                or hashlib.sha256(public_key).hexdigest() != selected["keyId"]):
            raise Refused("v5-key-registry-public-key")
        prior_approval = candidate._time(approval.approved_at)
        approved = candidate._time(record["approvedAt"])
        observed = candidate._time(record["observedAt"])
        signed_at = candidate._time(issued.issued_at)
        expires = candidate._time(record["expiresAt"])
        if not (now - dt.timedelta(minutes=30) <= prior_approval
                <= approved <= observed <= signed_at <= now < expires
                <= approved + dt.timedelta(minutes=30)):
            raise Refused("v5-key-registry-time")
        if (registry_port.scope() != scope
                or originals != (selection, approval, issued,
                                 checked, selected)):
            raise Refused("v5-key-registry-drift")
        return KeyWitness(selected["keyId"], selected["registryEventId"],
                          selected["reviewerActorId"],
                          hashlib.sha256(public_key).hexdigest())
    except Refused:
        raise
    except Exception:
        raise Refused("v5-key-registry-unavailable") from None
