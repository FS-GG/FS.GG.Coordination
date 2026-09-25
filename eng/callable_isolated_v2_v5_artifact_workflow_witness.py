"""Pure fake-port producer artifact and disabled workflow source witness."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
from typing import Any

import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_installed_refusal as installed

ARTIFACT_SCHEMA = "fsgg.coordination.v5-no-grant-artifact-observation/1"
WORKFLOW_SCHEMA = "fsgg.coordination.v5-no-grant-workflow-observation/1"
SCOPE = {"principalId", "credentialId", "repository", "repositoryId",
         "permissions", "expiresAt"}
COMMON = {"repository", "repositoryId", "revision", "sourceTree"}
ARTIFACT = COMMON | {"schema", "complete", "principalId", "credentialId",
                     "producerRunId", "producerRunAttempt", "producerActorId",
                     "artifactId", "artifactName", "manifestSha256",
                     "archiveSha256", "archiveSize", "archiveBytes",
                     "immutable", "createdAt", "expiresAt"}
WORKFLOW = COMMON | {"schema", "complete", "principalId", "credentialId",
                     "workflowPath", "workflowSha256", "workflowBytes",
                     "immutable", "observedAt"}


class Refused(ValueError):
    """Fixed refusal without source, archive or credential material."""


@dataclasses.dataclass(frozen=True)
class Witness:
    revision: str
    source_tree: str
    artifact_id: int
    archive_sha256: str
    workflow_sha256: str
    artifact_reader_principal: str
    artifact_credential_id: str
    workflow_reader_principal: str
    workflow_credential_id: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _shape(value: Any, keys: set[str]):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-producer-shape")


def _scope(value: Any, permission: str, selection, now):
    _shape(value, SCOPE)
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != candidate.REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != selection.repository_id
            or type(value["permissions"]) is not list
            or value["permissions"] != [permission]
            or not now < candidate._time(value["expiresAt"])
                   <= now + dt.timedelta(minutes=30)):
        raise Refused("v5-producer-scope")


def qualify(selection: candidate.Selection, readback: installed.Readback,
            artifact_port, workflow_port, now: dt.datetime) -> Witness:
    """Compare supplied records only; no provider, file or authority access."""
    try:
        if (type(selection) is not candidate.Selection
                or type(readback) is not installed.Readback):
            raise Refused("v5-producer-prior-result")
        original_selection = selection
        original_readback = readback
        selection = copy.deepcopy(selection)
        readback = copy.deepcopy(readback)
        if (selection.authorized is not False
                or selection.can_dispatch is not False
                or type(selection.live_effects) is not int
                or selection.live_effects != 0
                or readback.authorized is not False
                or readback.can_dispatch is not False
                or type(readback.live_effects) is not int
                or readback.live_effects != 0
                or readback.archive_sha256 != selection.archive_sha256
                or readback.manifest_sha256 != selection.manifest_sha256
                or readback.revision != selection.revision
                or type(readback.artifact_id) is not int
                or readback.artifact_id != selection.artifact_id
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not all(type(getattr(selection, key)) is int
                           and getattr(selection, key) > 0 for key in
                           ("repository_id", "artifact_id", "producer_run_id",
                            "producer_run_attempt", "producer_actor_id",
                            "reviewer_actor_id", "review_event_id"))
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or artifact_port is None or workflow_port is None
                or artifact_port is workflow_port
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-producer-selection")
        reviewed = candidate._time(selection.reviewed_at)
        expires = candidate._time(selection.expires_at)
        if not (now - dt.timedelta(minutes=30) <= reviewed <= now < expires):
            raise Refused("v5-producer-review-time")
        artifact_scope = copy.deepcopy(artifact_port.scope())
        workflow_scope = copy.deepcopy(workflow_port.scope())
        _scope(artifact_scope, "actions:read", selection, now)
        _scope(workflow_scope, "contents:read", selection, now)
        if (artifact_scope["principalId"] == workflow_scope["principalId"]
                or artifact_scope["credentialId"] == workflow_scope["credentialId"]
                or any(scope["principalId"] in
                       (selection.source_reader_principal,
                        selection.review_reader_principal)
                       or scope["credentialId"] in
                       (selection.source_credential_id,
                        selection.review_credential_id)
                       for scope in (artifact_scope, workflow_scope))):
            raise Refused("v5-producer-reader-custody")
        artifact = copy.deepcopy(artifact_port.read(selection.artifact_id))
        workflow = copy.deepcopy(workflow_port.read(selection.revision,
                                                   builder.WORKFLOW))
        _shape(artifact, ARTIFACT)
        _shape(workflow, WORKFLOW)
        common = {"repository": candidate.REPOSITORY,
                  "repositoryId": selection.repository_id,
                  "revision": selection.revision,
                  "sourceTree": selection.source_tree}
        for record, scope, schema in ((artifact, artifact_scope,
                                       ARTIFACT_SCHEMA),
                                      (workflow, workflow_scope,
                                       WORKFLOW_SCHEMA)):
            if (record["schema"] != schema or record["complete"] is not True
                    or record["immutable"] is not True
                    or record["principalId"] != scope["principalId"]
                    or record["credentialId"] != scope["credentialId"]
                    or any(type(record[key]) is not type(value)
                           or record[key] != value
                           for key, value in common.items())):
                raise Refused("v5-producer-record")
        archive = artifact["archiveBytes"]
        source = workflow["workflowBytes"]
        if (type(artifact["producerRunId"]) is not int
                or artifact["producerRunId"] != selection.producer_run_id
                or type(artifact["producerRunAttempt"]) is not int
                or artifact["producerRunAttempt"] !=
                   selection.producer_run_attempt
                or type(artifact["producerActorId"]) is not int
                or artifact["producerActorId"] != selection.producer_actor_id
                or type(artifact["artifactId"]) is not int
                or artifact["artifactId"] != selection.artifact_id
                or artifact["artifactName"] != builder.ARCHIVE_NAME
                or artifact["manifestSha256"] != selection.manifest_sha256
                or artifact["archiveSha256"] != selection.archive_sha256
                or type(archive) is not bytes
                or not 0 < len(archive) <= 256_000
                or type(artifact["archiveSize"]) is not int
                or artifact["archiveSize"] != len(archive)
                or hashlib.sha256(archive).hexdigest() !=
                   candidate.PINNED_BYTES["archive"]):
            raise Refused("v5-producer-artifact")
        if (workflow["workflowPath"] != builder.WORKFLOW
                or workflow["workflowSha256"] !=
                   candidate.PINNED_BYTES["workflow"]
                or type(source) is not bytes
                or not 0 < len(source) <= 128_000
                or hashlib.sha256(source).hexdigest() !=
                   candidate.PINNED_BYTES["workflow"]
                or b"if: ${{ false }}" not in source
                or b"run: exit 78" not in source):
            raise Refused("v5-producer-workflow")
        created = candidate._time(artifact["createdAt"])
        artifact_expires = candidate._time(artifact["expiresAt"])
        observed = candidate._time(workflow["observedAt"])
        if not (now - dt.timedelta(minutes=30) <= created <= reviewed
                <= observed <= now < artifact_expires
                <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-producer-time")
        if (artifact_port.scope() != artifact_scope
                or workflow_port.scope() != workflow_scope
                or original_selection != selection
                or original_readback != readback):
            raise Refused("v5-producer-drift")
        return Witness(selection.revision, selection.source_tree,
                       selection.artifact_id, selection.archive_sha256,
                       candidate.PINNED_BYTES["workflow"],
                       artifact_scope["principalId"],
                       artifact_scope["credentialId"],
                       workflow_scope["principalId"],
                       workflow_scope["credentialId"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-producer-unavailable") from None
