"""Injected read-only GitHub workflow/run observer for an inactive v2 candidate.

The transport must later be supplied by a separately reviewed protected
reader. This module has no credential lookup, HTTP client, journal, grant or
provider write. Its output remains an observation, never dispatch authority.
"""

from __future__ import annotations

import base64
import copy
import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Any, Protocol

import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate

REPOSITORY = "FS-GG/.github"
API = "https://api.github.com"
MAX_RESPONSE = 1_000_000


class Refused(ValueError):
    """Fixed refusal reason with no raw API or credential data."""


@dataclasses.dataclass(frozen=True)
class Response:
    status: int
    headers: tuple[tuple[str, str], ...]
    body: bytes


class ScopedReadTransport(Protocol):
    """Future authenticated, fixed-host GET-only App transport.

    `scope` must be independently established from the protected provider;
    a fake's claim is only contract data. No implementation is supplied.
    """

    def scope(self) -> dict[str, Any]: ...

    def get(self, path: str) -> Response: ...


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise Refused("workflow-json-duplicate")
        result[key] = value
    return result


def _nonfinite(_value: str) -> None:
    raise Refused("workflow-json-nonfinite")


def _object(value: Any) -> dict[str, Any]:
    if type(value) is not dict:
        raise Refused("workflow-response-shape")
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _get(transport: ScopedReadTransport, path: str) -> dict[str, Any]:
    try:
        response = transport.get(path)
    except Exception:
        raise Refused("workflow-read-unavailable") from None
    if (type(response) is not Response or type(response.status) is not int
            or response.status != 200 or type(response.headers) is not tuple
            or type(response.body) is not bytes or len(response.body) > MAX_RESPONSE
            or any(type(pair) is not tuple or len(pair) != 2
                   or any(type(item) is not str for item in pair)
                   for pair in response.headers)
            or any(key.lower() in {"location", "link"}
                   for key, _ in response.headers)):
        raise Refused("workflow-response-invalid")
    try:
        return _object(json.loads(response.body.decode("utf-8"),
                                  object_pairs_hook=_unique, parse_constant=_nonfinite))
    except Refused:
        raise
    except (UnicodeError, ValueError):
        raise Refused("workflow-json-invalid") from None


def _scope(transport: ScopedReadTransport, repository_id: int,
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = transport.scope()
    except Exception:
        raise Refused("workflow-scope-unavailable") from None
    if (type(value) is not dict or set(value) != {"principalId", "credentialId",
            "repository", "repositoryId", "permissions", "expiresAt"}
            or type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != repository_id
            or value["permissions"] != {"actions": "read", "contents": "read",
                                        "metadata": "read"}):
        raise Refused("workflow-scope-invalid")
    try:
        if candidate._time(value["expiresAt"]) <= now:
            raise Refused("workflow-scope-expired")
    except candidate.Refused:
        raise Refused("workflow-scope-expired") from None
    return copy.deepcopy(value)


def _git_blob_sha(raw: bytes) -> str:
    return hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()


class WorkflowRunReadAdapter:
    """Derive one #587 workflow observation from two injected REST reads."""

    def __init__(self, transport: ScopedReadTransport, repository_id: int,
                 run_id: int, run_attempt: int, workflow_revision: str,
                 workflow_sha256: str, candidate_sha256: str,
                 now: dt.datetime):
        if (not all(_positive(value) for value in
                    (repository_id, run_id, run_attempt))
                or not candidate._hex(workflow_revision, candidate.HEX40)
                or not candidate._hex(workflow_sha256, candidate.HEX64)
                or not candidate._hex(candidate_sha256, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("workflow-selection-invalid")
        self.transport = transport
        self.repository_id = repository_id
        self.run_id = run_id
        self.run_attempt = run_attempt
        self.revision = workflow_revision
        self.expected_sha256 = workflow_sha256
        self.candidate_sha256 = candidate_sha256
        self.now = now

    def observe_workflow(self) -> dict[str, Any]:
        scope_before = _scope(self.transport, self.repository_id, self.now)
        run_path = (f"repos/{REPOSITORY}/actions/runs/{self.run_id}/"
                    f"attempts/{self.run_attempt}")
        run = _get(self.transport, run_path)
        actor = run.get("actor")
        triggering_actor = run.get("triggering_actor")
        repository = run.get("repository")
        head_repository = run.get("head_repository")
        workflow_id = run.get("workflow_id")
        run_url = f"{API}/repos/{REPOSITORY}/actions/runs/{self.run_id}"
        if (run.get("id") != self.run_id or type(run.get("id")) is not int
                or run.get("run_attempt") != self.run_attempt
                or type(run.get("run_attempt")) is not int
                or run.get("event") != "workflow_dispatch"
                or run.get("head_branch") != "main"
                or run.get("head_sha") != self.revision
                or run.get("path") != candidate.WORKFLOW_PATH + "@main"
                or run.get("url") != run_url
                or not _positive(workflow_id)
                or run.get("workflow_url") !=
                f"{API}/repos/{REPOSITORY}/actions/workflows/{workflow_id}"
                or type(actor) is not dict or not _positive(actor.get("id"))
                or type(triggering_actor) is not dict
                or triggering_actor.get("id") != actor["id"]
                or type(triggering_actor.get("id")) is not int
                or any(type(item) is not dict
                       or type(item.get("id")) is not int
                       or item["id"] != self.repository_id
                       or item.get("full_name") != REPOSITORY
                       for item in (repository, head_repository))):
            raise Refused("workflow-run-binding")
        content_path = (f"repos/{REPOSITORY}/contents/{candidate.WORKFLOW_PATH}"
                        f"?ref={self.revision}")
        content = _get(self.transport, content_path)
        encoded = content.get("content")
        try:
            if (type(encoded) is not str
                    or re.fullmatch(r"[A-Za-z0-9+/=\n]+", encoded) is None):
                raise ValueError()
            compact = encoded.replace("\n", "")
            raw = base64.b64decode(compact, validate=True)
            if base64.b64encode(raw).decode("ascii") != compact:
                raise ValueError()
        except (ValueError, UnicodeError):
            raise Refused("workflow-content-encoding") from None
        blob_sha = _git_blob_sha(raw)
        if (not raw or len(raw) > observers.MAX_BLOB
                or content.get("type") != "file"
                or content.get("encoding") != "base64"
                or content.get("path") != candidate.WORKFLOW_PATH
                or content.get("name") != "callable-isolated-v2-execute.yml"
                or type(content.get("size")) is not int
                or content["size"] != len(raw)
                or content.get("sha") != blob_sha
                or content.get("url") !=
                f"{API}/repos/{REPOSITORY}/contents/{candidate.WORKFLOW_PATH}"
                or content.get("git_url") !=
                f"{API}/repos/{REPOSITORY}/git/blobs/{blob_sha}"
                or hashlib.sha256(raw).hexdigest() != self.expected_sha256):
            raise Refused("workflow-content-binding")
        scope_after = _scope(self.transport, self.repository_id, self.now)
        if scope_after != scope_before:
            raise Refused("workflow-scope-drift")
        return {
            "envelope": {
                "schema": observers.SCHEMA, "role": "workflow", "complete": True,
                "principalId": scope_after["principalId"],
                "credentialId": scope_after["credentialId"],
                "recordId": self.run_id,
                "candidateSha256": self.candidate_sha256,
                "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ"),
                "facts": {"workflowRevision": self.revision,
                          "workflowPath": candidate.WORKFLOW_PATH,
                          "workflowSha256": self.expected_sha256,
                          "runId": self.run_id, "runAttempt": self.run_attempt,
                          "dispatchActorId": actor["id"]},
            },
            "blobs": {"workflow": raw},
        }
