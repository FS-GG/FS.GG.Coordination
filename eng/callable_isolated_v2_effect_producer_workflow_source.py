"""Bind a future producer workflow pin to a selected Git tree, read-only."""

from __future__ import annotations

import dataclasses
import datetime as dt
import hashlib
from typing import Any

import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_git_tree_witness as git_tree
import verify_callable_isolated_v2_effect_producer_workflow as closed_workflow

WORKFLOW = ".github/workflows/callable-isolated-v2-effect-release.yml"
RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-producer-workflow-source/1"
SOURCE_PATHS = (set(builder.MEMBERS.values()) |
                {builder.WORKFLOW, builder.MANIFEST,
                 "eng/build_callable_isolated_v2_effect_scaffold.py",
                 builder.NATIVE_SOURCE})


class Refused(ValueError):
    """Fixed refusal without source bytes or reader identity contents."""


@dataclasses.dataclass(frozen=True)
class WorkflowSourceResult:
    coordination_revision: str
    source_tree: str
    repository_id: int
    identity_event_id: int
    workflow_path: str
    workflow_sha256: str
    workflow_blob_oid: str
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def qualify(source_tree: git_tree.TreeWitnessResult,
            git_port: git_tree.GitObjectPort, selection: dict[str, Any],
            now: dt.datetime) -> WorkflowSourceResult:
    """Require the selected producer workflow's exact Git blob; no dispatch."""
    selection = dict(_exact(selection, {"repositoryId", "identityEventId",
        "workflowSha256", "gitReaderPrincipalId", "gitReaderCredentialId",
        "sourceReaderPrincipalId", "sourceReaderCredentialId"},
        "workflow-source-selection-shape"))
    if (type(source_tree) is not git_tree.TreeWitnessResult
            or source_tree.schema != git_tree.RESULT_SCHEMA
            or source_tree.authorized is not False
            or source_tree.can_dispatch is not False
            or type(source_tree.live_effects) is not int
            or source_tree.live_effects != 0
            or not candidate._hex(source_tree.coordination_revision,
                                  candidate.HEX40)
            or not candidate._hex(source_tree.source_tree, candidate.HEX40)
            or type(source_tree.identity_event_id) is not int
            or source_tree.identity_event_id != selection["identityEventId"]
            or type(source_tree.source_files) is not tuple
            or len(source_tree.source_files) != len(SOURCE_PATHS)
            or any(type(item) is not tuple or len(item) != 2
                   or type(item[0]) is not str
                   or not candidate._hex(item[1], candidate.HEX64)
                   for item in source_tree.source_files)
            or {item[0] for item in source_tree.source_files} != SOURCE_PATHS
            or type(selection["repositoryId"]) is not int
            or selection["repositoryId"] <= 0
            or type(selection["identityEventId"]) is not int
            or selection["identityEventId"] <= 0
            or not candidate._hex(selection["workflowSha256"], candidate.HEX64)
            or not all(type(selection[key]) is str and selection[key]
                       for key in ("gitReaderPrincipalId",
                                   "sourceReaderPrincipalId"))
            or not all(candidate._hex(selection[key], candidate.HEX64)
                       for key in ("gitReaderCredentialId",
                                   "sourceReaderCredentialId"))
            or selection["gitReaderPrincipalId"] ==
               selection["sourceReaderPrincipalId"]
            or selection["gitReaderCredentialId"] ==
               selection["sourceReaderCredentialId"]
            or git_port is None
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("workflow-source-selection-invalid")
    try:
        scope_before = git_tree._scope(git_port, selection["repositoryId"],
                                       ["contents:read", "metadata:read"], now)
    except git_tree.Refused:
        raise Refused("workflow-source-scope") from None
    if (scope_before["principalId"] != selection["gitReaderPrincipalId"]
            or scope_before["credentialId"] != selection["gitReaderCredentialId"]):
        raise Refused("workflow-source-reader-binding")
    try:
        commit = git_tree._read(git_port, "commit",
                                source_tree.coordination_revision)
        if not commit.startswith(b"tree " + source_tree.source_tree.encode() + b"\n"):
            raise Refused("workflow-source-commit-tree")
        raw = git_tree._blob_for_path(git_port, source_tree.source_tree,
                                      WORKFLOW)
    except git_tree.Refused:
        raise Refused("workflow-source-git-object") from None
    if (type(raw) is not bytes or not 0 < len(raw) <= 128_000
            or hashlib.sha256(raw).hexdigest() !=
               selection["workflowSha256"]):
        raise Refused("workflow-source-bytes")
    try:
        closed_workflow.verify(raw, selection["workflowSha256"])
    except closed_workflow.Refused:
        raise Refused("workflow-source-not-closed-candidate") from None
    try:
        scope_after = git_tree._scope(git_port, selection["repositoryId"],
                                      ["contents:read", "metadata:read"], now)
    except git_tree.Refused:
        raise Refused("workflow-source-scope") from None
    if scope_after != scope_before:
        raise Refused("workflow-source-scope-drift")
    return WorkflowSourceResult(source_tree.coordination_revision,
        source_tree.source_tree, selection["repositoryId"],
        selection["identityEventId"], WORKFLOW,
        selection["workflowSha256"], git_tree._oid("blob", raw))
