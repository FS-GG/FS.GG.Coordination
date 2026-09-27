"""Source-only projection of injected GitHub REST reads into S5 candidate ports.

The transport implementations and their credentials are outside this module.
GitHub artifact objects do not report an attempt, source tree, or workflow
digest. Those fields are joins over independently checked run, commit, content,
and complete artifact-list reads. The run's ``updated_at`` is used only as an
observed upper time bound for the completed run; REST does not provide a
distinct exact run-completion timestamp. The artifact-list membership and
creation interval are the available first-attempt evidence. A matching
fixture is not provider custody.
"""

from __future__ import annotations

import base64
import copy
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_runtime_candidate_witness as witness
import verify_callable_isolated_v2_runtime_candidate_workflow as workflow
from callable_isolated_v2_workflow_read_adapter import Response


class Refused(ValueError):
    """Fixed refusal without raw response or credential material."""


class ReadTransport(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def get(self, path: str) -> Response: ...


class BundleTransport(Protocol):
    def scope(self) -> dict[str, Any]: ...
    def download(self, artifact_id: int, url: str) -> bytes: ...


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise Refused("candidate-json-duplicate")
        result[key] = value
    return result


def _nonfinite(_value: str) -> None:
    raise Refused("candidate-json-nonfinite")


def _read(transport: ReadTransport, path: str) -> dict[str, Any]:
    try:
        response = transport.get(path)
    except Exception:
        raise Refused("candidate-read-unavailable") from None
    if (type(response) is not Response or response.status != 200
            or type(response.status) is not int or type(response.body) is not bytes
            or not 0 < len(response.body) <= 1_000_000
            or type(response.headers) is not tuple
            or any(type(pair) is not tuple or len(pair) != 2
                   or any(type(part) is not str for part in pair)
                   or pair[0].lower() in {"link", "location"}
                   for pair in response.headers)):
        raise Refused("candidate-response-invalid")
    try:
        value = json.loads(response.body.decode("utf-8"),
                           object_pairs_hook=_unique, parse_constant=_nonfinite)
    except (ValueError, UnicodeError):
        raise Refused("candidate-json-invalid") from None
    if type(value) is not dict:
        raise Refused("candidate-json-shape")
    return value


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _scope(transport: object, repository_id: int, permissions: list[str],
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = copy.deepcopy(transport.scope())
    except Exception:
        raise Refused("candidate-scope-unavailable") from None
    if (type(value) is not dict or set(value) != {"principalId", "credentialId",
            "repository", "repositoryId", "permissions", "expiresAt"}
            or type(value["principalId"]) is not str or not value["principalId"]
            or type(value["credentialId"]) is not str
            or witness.HEX64.fullmatch(value["credentialId"]) is None
            or value["repository"] != witness.REPOSITORY
            or value["repositoryId"] != repository_id
            or value["permissions"] != permissions):
        raise Refused("candidate-scope-binding")
    try:
        if witness._time(value["expiresAt"]) <= now:
            raise Refused("candidate-scope-expired")
    except witness.Refused:
        raise Refused("candidate-scope-expired") from None
    return value


class NativeCandidateProducerAdapter:
    """Normalize a complete first-attempt run and its sole artifact."""

    def __init__(self, transport: ReadTransport, selection: witness.Selection,
                 now: dt.datetime):
        if (transport is None or type(selection) is not witness.Selection
                or selection.run_attempt != 1
                or type(now) is not dt.datetime
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("candidate-selection")
        self.transport, self.selection, self.now = transport, selection, now
        self._run: dict[str, Any] | None = None
        self._artifact: dict[str, Any] | None = None

    def scope(self) -> dict[str, Any]:
        return _scope(self.transport, self.selection.repository_id,
                      ["actions:read", "metadata:read"], self.now)

    def read_run(self, run_id: int, attempt: int) -> dict[str, Any]:
        s = self.selection
        if run_id != s.run_id or attempt != 1 or self._run is not None:
            raise Refused("candidate-run-selection")
        before = self.scope()
        root = f"repos/{witness.REPOSITORY}"
        run = _read(self.transport, f"{root}/actions/runs/{run_id}/attempts/1")
        repository = run.get("repository")
        actor = run.get("actor")
        if (run.get("id") != s.run_id or type(run.get("id")) is not int
                or run.get("run_attempt") != 1
                or type(run.get("run_attempt")) is not int
                or run.get("head_sha") != s.source_sha
                or run.get("workflow_id") != s.workflow_id
                or type(run.get("workflow_id")) is not int
                or run.get("event") != "workflow_dispatch"
                or run.get("status") != "completed"
                or run.get("conclusion") != "success"
                or run.get("path") not in (workflow.WORKFLOW,
                                           workflow.WORKFLOW + "@main")
                or type(repository) is not dict
                or repository.get("id") != s.repository_id
                or type(repository.get("id")) is not int
                or repository.get("full_name") != witness.REPOSITORY
                or type(actor) is not dict or actor.get("id") != s.actor_id
                or type(actor.get("id")) is not int):
            raise Refused("candidate-run-binding")
        commit = _read(self.transport, f"{root}/git/commits/{s.source_sha}")
        tree = commit.get("tree")
        if (commit.get("sha") != s.source_sha or type(tree) is not dict
                or tree.get("sha") != s.source_tree):
            raise Refused("candidate-tree-binding")
        content = _read(self.transport,
            f"{root}/contents/{workflow.WORKFLOW}?ref={s.source_sha}")
        try:
            encoded = content["content"]
            if type(encoded) is not str or content.get("encoding") != "base64":
                raise ValueError()
            compact = encoded.replace("\n", "")
            raw = base64.b64decode(compact, validate=True)
            if base64.b64encode(raw).decode("ascii") != compact:
                raise ValueError()
        except (KeyError, ValueError, UnicodeError):
            raise Refused("candidate-workflow-content") from None
        if (content.get("type") != "file" or content.get("path") != workflow.WORKFLOW
                or content.get("size") != len(raw)
                or type(content.get("size")) is not int
                or content.get("sha") != hashlib.sha1(
                    b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
                or hashlib.sha256(raw).hexdigest() != s.workflow_sha256):
            raise Refused("candidate-workflow-binding")
        try:
            workflow.verify(raw, s.workflow_sha256)
        except workflow.Refused:
            raise Refused("candidate-workflow-binding") from None
        ids: list[int] = []
        entries: list[dict[str, Any]] = []
        total: int | None = None
        for page in range(1, 12):
            listing = _read(self.transport,
                f"{root}/actions/runs/{run_id}/artifacts?per_page=100&page={page}")
            count, artifacts = listing.get("total_count"), listing.get("artifacts")
            if (type(count) is not int or count < 1 or count > 1000
                    or type(artifacts) is not list or len(artifacts) > 100
                    or (total is not None and count != total)
                    or (total is None and not artifacts)):
                raise Refused("candidate-artifact-pages")
            total = count
            for entry in artifacts:
                if type(entry) is not dict or not _positive(entry.get("id")):
                    raise Refused("candidate-artifact-pages")
                ids.append(entry["id"])
                entries.append(entry)
            if len(ids) >= total:
                break
            if not artifacts:
                raise Refused("candidate-artifact-pages")
        if (total is None or len(ids) != total or len(ids) != len(set(ids))
                or ids != [s.artifact_id]):
            raise Refused("candidate-artifact-membership")
        if self.scope() != before:
            raise Refused("candidate-scope-drift")
        self._artifact = copy.deepcopy(entries[0])
        self._run = {
            "schema": witness.RUN_SCHEMA, "complete": True,
            "principalId": before["principalId"],
            "credentialId": before["credentialId"],
            "repository": witness.REPOSITORY, "repositoryId": s.repository_id,
            "runId": s.run_id, "runAttempt": 1, "headSha": s.source_sha,
            "headTree": s.source_tree, "event": run["event"],
            "status": run["status"], "conclusion": run["conclusion"],
            "actorId": s.actor_id, "workflowId": s.workflow_id,
            "workflowPath": workflow.WORKFLOW,
            "workflowSha256": s.workflow_sha256, "artifactIds": ids,
            "createdAt": run.get("created_at"),
            "completedAt": run.get("updated_at"),
        }
        return copy.deepcopy(self._run)

    def read_artifact(self, artifact_id: int) -> dict[str, Any]:
        s = self.selection
        if artifact_id != s.artifact_id or self._run is None or self._artifact is None:
            raise Refused("candidate-artifact-selection")
        before = self.scope()
        detail = _read(self.transport,
            f"repos/{witness.REPOSITORY}/actions/artifacts/{artifact_id}")
        fields = ("id", "name", "size_in_bytes", "digest", "expired",
                  "created_at", "expires_at", "archive_download_url")
        if any(detail.get(key) != self._artifact.get(key) for key in fields):
            raise Refused("candidate-artifact-detail")
        linked = detail.get("workflow_run")
        if (detail.get("id") != artifact_id or type(detail.get("id")) is not int
                or type(linked) is not dict or linked.get("id") != s.run_id
                or type(linked.get("id")) is not int
                or linked.get("repository_id") != s.repository_id
                or linked.get("head_sha") != s.source_sha):
            raise Refused("candidate-artifact-run")
        if self.scope() != before:
            raise Refused("candidate-scope-drift")
        return {
            "schema": witness.RECORD_SCHEMA, "complete": True,
            "principalId": before["principalId"],
            "credentialId": before["credentialId"],
            "repository": witness.REPOSITORY, "repositoryId": s.repository_id,
            "artifactId": artifact_id, "name": detail["name"],
            "runId": s.run_id, "runAttempt": 1,
            "headSha": s.source_sha, "headTree": s.source_tree,
            "workflowId": s.workflow_id, "expired": detail["expired"],
            "size": detail["size_in_bytes"], "digest": detail["digest"],
            "downloadUrl": detail["archive_download_url"],
            "createdAt": detail["created_at"],
            "expiresAt": detail["expires_at"],
        }


class NativeCandidateDownloadAdapter:
    """Separate read-only bundle custody; does not hold a token or write path."""

    def __init__(self, transport: BundleTransport, repository_id: int,
                 now: dt.datetime):
        if (transport is None or not _positive(repository_id)
                or type(now) is not dt.datetime
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("candidate-download-selection")
        self.transport, self.repository_id, self.now = transport, repository_id, now

    def scope(self) -> dict[str, Any]:
        return _scope(self.transport, self.repository_id, ["actions:read"], self.now)

    def download(self, artifact_id: int, url: str) -> bytes:
        expected = (f"{witness.API}/repos/{witness.REPOSITORY}/actions/"
                    f"artifacts/{artifact_id}/zip")
        if not _positive(artifact_id) or url != expected:
            raise Refused("candidate-download-binding")
        before = self.scope()
        try:
            raw = self.transport.download(artifact_id, url)
        except Exception:
            raise Refused("candidate-download-unavailable") from None
        if type(raw) is not bytes or not 0 < len(raw) <= witness.MAX_BUNDLE:
            raise Refused("candidate-download-size")
        if self.scope() != before:
            raise Refused("candidate-scope-drift")
        return raw
