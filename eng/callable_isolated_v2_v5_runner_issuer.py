"""Pure fake issuer-event join for the closed v5 no-grant runner."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
from typing import Any

import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_git_tree_membership as tree
import callable_isolated_v2_v5_install_approval as install_approval
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_no_grant_selection as candidate

ISSUER_SCHEMA = "fsgg.coordination.v5-no-grant-runner-issuer/1"
SELECTED = {"eventId", "issuerActorId"}
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "expiresAt"}
RECORD = {"schema", "complete", "principalId", "credentialId",
          "eventId", "issuerActorId", "repository", "repositoryId",
          "revision", "sourceTree", "artifactId",
          "installApprovalEventId", "runId", "runAttempt",
          "runnerActorId", "imageDigest", "interpreterSha256",
          "runtimeClosureSha256", "installPath", "issuedAt",
          "observedAt", "expiresAt"}
PATHS = {builder.ENTRY_SOURCE: "entry",
         "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
         "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
         builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}


class Refused(ValueError):
    """Fixed refusal without event, source or credential contents."""


@dataclasses.dataclass(frozen=True)
class IssuerWitness:
    revision: str
    run_id: int
    run_attempt: int
    event_id: int
    issuer_actor_id: int
    issued_at: str
    expires_at: str
    reader_principal: str
    reader_credential_id: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-issuer-shape")


def _positive(value: Any):
    return type(value) is int and value > 0


def qualify(selection: candidate.Selection, membership: tree.TreeWitness,
            readback: installed.Readback,
            approval: install_approval.Approval, selected: dict,
            issuer_port, now: dt.datetime) -> IssuerWitness:
    """Compare supplied issuer evidence only; no real identity is conferred."""
    try:
        if (type(selection) is not candidate.Selection
                or type(membership) is not tree.TreeWitness
                or type(readback) is not installed.Readback
                or type(approval) is not install_approval.Approval):
            raise Refused("v5-issuer-prior")
        originals = (selection, membership, readback, approval, selected)
        selection, membership, readback, approval, selected = copy.deepcopy(
            originals)
        _shape(selected, SELECTED)
        expected_files = tuple(sorted((path, candidate.PINNED_BYTES[name])
                                      for path, name in PATHS.items()))
        if (any(result.authorized is not False
                or result.can_dispatch is not False
                or type(result.live_effects) is not int
                or result.live_effects != 0 for result in
                (selection, membership, readback, approval))
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or membership.revision != selection.revision
                or membership.source_tree != selection.source_tree
                or membership.source_files != expected_files
                or readback.revision != selection.revision
                or readback.source_tree != selection.source_tree
                or readback.repository_id != selection.repository_id
                or readback.artifact_id != selection.artifact_id
                or readback.archive_sha256 != selection.archive_sha256
                or readback.manifest_sha256 != selection.manifest_sha256
                or approval.revision != selection.revision
                or approval.artifact_id != selection.artifact_id
                or not all(_positive(value) for value in
                           (selection.repository_id, selection.producer_actor_id,
                            selection.reviewer_actor_id, readback.run_id,
                            readback.run_attempt, readback.runner_actor_id,
                            readback.audit_actor_id, approval.event_id,
                            approval.reviewer_actor_id, selected["eventId"],
                            selected["issuerActorId"]))
                or not all(candidate._hex(getattr(readback, key),
                                            candidate.HEX64) for key in
                           ("image_digest", "interpreter_sha256",
                            "runtime_closure_sha256"))
                or not installed._path(readback.install_path)
                or selected["issuerActorId"] in
                   (selection.producer_actor_id, selection.reviewer_actor_id,
                    readback.runner_actor_id, readback.audit_actor_id,
                    approval.reviewer_actor_id)
                or issuer_port is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-issuer-selection")
        scope = copy.deepcopy(issuer_port.scope())
        _shape(scope, SCOPE)
        principals = (selection.source_reader_principal,
            selection.review_reader_principal,
            membership.artifact_reader_principal,
            membership.workflow_reader_principal,
            membership.git_reader_principal,
            membership.identity_reader_principal,
            readback.probe_reader_principal,
            readback.audit_reader_principal,
            approval.reader_principal, scope["principalId"])
        credentials = (selection.source_credential_id,
            selection.review_credential_id,
            membership.artifact_credential_id,
            membership.workflow_credential_id,
            membership.git_credential_id,
            membership.identity_credential_id,
            readback.probe_credential_id,
            readback.audit_credential_id,
            approval.reader_credential_id, scope["credentialId"])
        if (any(type(value) is not str or not value
                for value in principals)
                or any(not candidate._hex(value, candidate.HEX64)
                       for value in credentials)
                or len(set(principals)) != len(principals)
                or len(set(credentials)) != len(credentials)
                or scope["repository"] != candidate.REPOSITORY
                or type(scope["repositoryId"]) is not int
                or scope["repositoryId"] != selection.repository_id
                or type(scope["permissions"]) is not list
                or scope["permissions"] != ["read-run-attestation"]
                or not now < candidate._time(scope["expiresAt"])
                       <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-issuer-scope")
        record = copy.deepcopy(issuer_port.read(selected["eventId"]))
        _shape(record, RECORD)
        expected = {"eventId": selected["eventId"],
            "issuerActorId": selected["issuerActorId"],
            "repository": candidate.REPOSITORY,
            "repositoryId": selection.repository_id,
            "revision": selection.revision,
            "sourceTree": selection.source_tree,
            "artifactId": selection.artifact_id,
            "installApprovalEventId": approval.event_id,
            "runId": readback.run_id,
            "runAttempt": readback.run_attempt,
            "runnerActorId": readback.runner_actor_id,
            "imageDigest": readback.image_digest,
            "interpreterSha256": readback.interpreter_sha256,
            "runtimeClosureSha256": readback.runtime_closure_sha256,
            "installPath": readback.install_path}
        if (record["schema"] != ISSUER_SCHEMA
                or record["complete"] is not True
                or record["principalId"] != scope["principalId"]
                or record["credentialId"] != scope["credentialId"]
                or any(type(record[key]) is not type(value)
                       or record[key] != value
                       for key, value in expected.items())):
            raise Refused("v5-issuer-binding")
        release_reviewed = candidate._time(selection.reviewed_at)
        release_expires = candidate._time(selection.expires_at)
        approved = candidate._time(approval.approved_at)
        approval_expires = candidate._time(approval.expires_at)
        issued = candidate._time(record["issuedAt"])
        observed = candidate._time(record["observedAt"])
        issuer_expires = candidate._time(record["expiresAt"])
        started = candidate._time(readback.command_started_at)
        completed = candidate._time(readback.command_completed_at)
        if not (now - dt.timedelta(minutes=30) <= release_reviewed
                <= approved <= issued <= observed <= started <= completed <= now
                < release_expires <= release_reviewed + dt.timedelta(minutes=30)
                and now < approval_expires <= approved + dt.timedelta(minutes=30)
                and now < issuer_expires <= issued + dt.timedelta(minutes=30)):
            raise Refused("v5-issuer-time")
        if (issuer_port.scope() != scope
                or originals != (selection, membership, readback,
                                 approval, selected)):
            raise Refused("v5-issuer-drift")
        return IssuerWitness(selection.revision, readback.run_id,
                             readback.run_attempt, selected["eventId"],
                             selected["issuerActorId"], record["issuedAt"],
                             record["expiresAt"], scope["principalId"],
                             scope["credentialId"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-issuer-unavailable") from None
