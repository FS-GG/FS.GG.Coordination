"""Pure fake independent approval for a closed v5 no-grant install probe."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
from typing import Any

import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_git_tree_membership as tree
import callable_isolated_v2_v5_installed_refusal as installed
import callable_isolated_v2_v5_no_grant_selection as candidate

APPROVAL_SCHEMA = "fsgg.coordination.v5-no-grant-install-approval/1"
SELECTED = {"eventId", "reviewerActorId"}
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "expiresAt"}
RECORD = {"schema", "complete", "principalId", "credentialId",
          "eventId", "repository", "repositoryId", "revision",
          "sourceTree", "treeIdentityEventId", "artifactId",
          "manifestSha256", "archiveSha256", "runnerActorId",
          "auditActorId", "auditEventId", "imageDigest",
          "interpreterSha256", "runtimeClosureSha256", "installPath",
          "reviewerActorId", "decision", "reviewedAt", "observedAt",
          "expiresAt"}
SOURCE_PATHS = {builder.ENTRY_SOURCE: "entry",
                "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
                "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
                builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}


class Refused(ValueError):
    """Fixed refusal without event, source or credential contents."""


@dataclasses.dataclass(frozen=True)
class Approval:
    revision: str
    artifact_id: int
    event_id: int
    reviewer_actor_id: int
    approved_at: str
    expires_at: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-install-approval-shape")


def _positive(value: Any):
    return type(value) is int and value > 0


def qualify(selection: candidate.Selection, membership: tree.TreeWitness,
            readback: installed.Readback, selected: dict, approval_port,
            now: dt.datetime) -> Approval:
    """Compare a separate supplied event; no protected approval is issued."""
    try:
        if (type(selection) is not candidate.Selection
                or type(membership) is not tree.TreeWitness
                or type(readback) is not installed.Readback):
            raise Refused("v5-install-approval-prior")
        original_selection = selection
        original_membership = membership
        original_readback = readback
        original_selected = selected
        selection = copy.deepcopy(selection)
        membership = copy.deepcopy(membership)
        readback = copy.deepcopy(readback)
        selected = copy.deepcopy(selected)
        _shape(selected, SELECTED)
        expected_files = tuple(sorted((path, candidate.PINNED_BYTES[name])
                                      for path, name in SOURCE_PATHS.items()))
        if (selection.authorized is not False
                or selection.can_dispatch is not False
                or type(selection.live_effects) is not int
                or selection.live_effects != 0
                or membership.authorized is not False
                or membership.can_dispatch is not False
                or type(membership.live_effects) is not int
                or membership.live_effects != 0
                or readback.authorized is not False
                or readback.can_dispatch is not False
                or type(readback.live_effects) is not int
                or readback.live_effects != 0
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or membership.revision != selection.revision
                or membership.source_tree != selection.source_tree
                or membership.source_files != expected_files
                or not _positive(membership.identity_event_id)
                or readback.archive_sha256 != selection.archive_sha256
                or readback.manifest_sha256 != selection.manifest_sha256
                or readback.revision != selection.revision
                or readback.source_tree != selection.source_tree
                or readback.repository_id != selection.repository_id
                or readback.artifact_id != selection.artifact_id
                or not all(_positive(getattr(readback, key)) for key in
                           ("runner_actor_id", "audit_actor_id",
                            "audit_event_id", "run_id"))
                or not all(candidate._hex(getattr(readback, key),
                                            candidate.HEX64) for key in
                           ("image_digest", "interpreter_sha256",
                            "runtime_closure_sha256"))
                or not installed._path(readback.install_path)
                or not _positive(selected["eventId"])
                or not _positive(selected["reviewerActorId"])
                or selected["reviewerActorId"] in
                   (selection.producer_actor_id, selection.reviewer_actor_id,
                    readback.runner_actor_id, readback.audit_actor_id)
                or approval_port is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-install-approval-selection")
        scope = copy.deepcopy(approval_port.scope())
        _shape(scope, SCOPE)
        principals = (selection.source_reader_principal,
            selection.review_reader_principal,
            membership.artifact_reader_principal,
            membership.workflow_reader_principal,
            membership.git_reader_principal,
            membership.identity_reader_principal,
            readback.probe_reader_principal,
            readback.audit_reader_principal,
            scope["principalId"])
        credentials = (selection.source_credential_id,
            selection.review_credential_id,
            membership.artifact_credential_id,
            membership.workflow_credential_id,
            membership.git_credential_id,
            membership.identity_credential_id,
            readback.probe_credential_id,
            readback.audit_credential_id,
            scope["credentialId"])
        if (type(scope["principalId"]) is not str or not scope["principalId"]
                or not candidate._hex(scope["credentialId"], candidate.HEX64)
                or any(type(value) is not str or not value
                       for value in principals)
                or any(not candidate._hex(value, candidate.HEX64)
                       for value in credentials)
                or len(set(principals)) != len(principals)
                or len(set(credentials)) != len(credentials)
                or scope["repository"] != candidate.REPOSITORY
                or type(scope["repositoryId"]) is not int
                or scope["repositoryId"] != selection.repository_id
                or type(scope["permissions"]) is not list
                or scope["permissions"] != ["read-install-approval"]
                or not now < candidate._time(scope["expiresAt"])
                       <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-install-approval-scope")
        event = copy.deepcopy(approval_port.read(selected["eventId"]))
        _shape(event, RECORD)
        expected = {"repository": candidate.REPOSITORY,
            "repositoryId": selection.repository_id,
            "revision": selection.revision,
            "sourceTree": selection.source_tree,
            "treeIdentityEventId": membership.identity_event_id,
            "artifactId": selection.artifact_id,
            "manifestSha256": selection.manifest_sha256,
            "archiveSha256": selection.archive_sha256,
            "runnerActorId": readback.runner_actor_id,
            "auditActorId": readback.audit_actor_id,
            "auditEventId": readback.audit_event_id,
            "imageDigest": readback.image_digest,
            "interpreterSha256": readback.interpreter_sha256,
            "runtimeClosureSha256": readback.runtime_closure_sha256,
            "installPath": readback.install_path,
            "reviewerActorId": selected["reviewerActorId"],
            "eventId": selected["eventId"]}
        if (event["schema"] != APPROVAL_SCHEMA
                or event["complete"] is not True
                or event["principalId"] != scope["principalId"]
                or event["credentialId"] != scope["credentialId"]
                or event["decision"] != "install-no-grant-refusal"
                or any(type(event[key]) is not type(value)
                       or event[key] != value
                       for key, value in expected.items())):
            raise Refused("v5-install-approval-binding")
        release_reviewed = candidate._time(selection.reviewed_at)
        release_expires = candidate._time(selection.expires_at)
        approved = candidate._time(event["reviewedAt"])
        observed = candidate._time(event["observedAt"])
        expires = candidate._time(event["expiresAt"])
        started = candidate._time(readback.command_started_at)
        completed = candidate._time(readback.command_completed_at)
        if not (now - dt.timedelta(minutes=30) <= release_reviewed
                <= approved <= observed <= started <= completed <= now
                < release_expires <= release_reviewed + dt.timedelta(minutes=30)
                and now < expires <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-install-approval-time")
        if (approval_port.scope() != scope
                or original_selection != selection
                or original_membership != membership
                or original_readback != readback
                or original_selected != selected):
            raise Refused("v5-install-approval-drift")
        return Approval(selection.revision, selection.artifact_id,
                        selected["eventId"], selected["reviewerActorId"],
                        event["reviewedAt"], event["expiresAt"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-install-approval-unavailable") from None
