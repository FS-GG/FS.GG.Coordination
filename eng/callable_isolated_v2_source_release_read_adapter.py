"""Injected source/release observer; no HTTP, artifact download or install."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import io
import json
import zipfile
from typing import Any, Protocol

import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_workflow_read_adapter as workflow

API = "https://api.github.com"
REPOSITORY = "FS-GG/FS.GG.Coordination"
MEMBERS = {
    "operator": "eng/callable-cli-isolated-operation-v2.py",
    "controls": "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py",
    "archive": "dist/callable-isolated-v2-effect.pyz",
}
SCHEMA = "fsgg.coordination.callable-isolated-v2-source-release-attestation/1"
MAX_JSON = 1_000_000
MAX_BUNDLE = 12_000_000


class Refused(ValueError):
    """Fixed refusal without provider response or credential contents."""


class ReleaseReadTransport(Protocol):
    """Future read-only GitHub App transport; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def get(self, path: str) -> workflow.Response: ...


class ProtectedBundleReader(Protocol):
    """Future protected Actions artifact download custody; none supplied."""

    def read_bundle(self, artifact_id: int, download_url: str) -> bytes: ...


class ProtectedReleaseAttestation(Protocol):
    """Future independent artifact/runtime attestation; none supplied."""

    def read_release(self, revision: str, run_id: int, artifact_id: int,
                     response_sha256: tuple[str, str, str],
                     bundle_sha256: str,
                     member_sha256: tuple[str, str, str]) -> dict[str, Any]: ...


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _scope(transport: ReleaseReadTransport, repository_id: int,
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = transport.scope()
    except Exception:
        raise Refused("source-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "repositoryId", "permissions", "expiresAt"},
                   "source-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != repository_id
            or value["permissions"] != {"actions": "read", "contents": "read",
                                        "metadata": "read"}):
        raise Refused("source-scope-binding")
    try:
        if candidate._time(value["expiresAt"]) <= now:
            raise Refused("source-scope-expired")
    except candidate.Refused:
        raise Refused("source-scope-expired") from None
    return copy.deepcopy(value)


def _read(transport: ReleaseReadTransport, path: str) -> tuple[Any, str]:
    try:
        response = transport.get(path)
    except Exception:
        raise Refused("source-read-unavailable") from None
    if (type(response) is not workflow.Response or type(response.status) is not int
            or response.status != 200 or type(response.headers) is not tuple
            or type(response.body) is not bytes or len(response.body) > MAX_JSON
            or any(type(pair) is not tuple or len(pair) != 2
                   or any(type(item) is not str for item in pair)
                   for pair in response.headers)
            or any(key.lower() in {"location", "link"}
                   for key, _ in response.headers)):
        raise Refused("source-response-invalid")
    try:
        value = json.loads(response.body.decode("utf-8"),
                           object_pairs_hook=workflow._unique,
                           parse_constant=workflow._nonfinite)
    except (UnicodeError, ValueError):
        raise Refused("source-json-invalid") from None
    return value, hashlib.sha256(response.body).hexdigest()


def _bundle(raw: bytes) -> tuple[dict[str, bytes], str]:
    if type(raw) is not bytes or not 0 < len(raw) <= MAX_BUNDLE:
        raise Refused("source-bundle-invalid")
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            infos = archive.infolist()
            if len(infos) != 3 or {item.filename for item in infos} != set(MEMBERS.values()):
                raise Refused("source-bundle-members")
            result: dict[str, bytes] = {}
            for role, name in MEMBERS.items():
                info = archive.getinfo(name)
                mode = (info.external_attr >> 16) & 0o170000
                if (info.is_dir() or mode not in (0, 0o100000)
                        or info.file_size <= 0
                        or info.file_size > observers.MAX_BLOB
                        or info.compress_size > MAX_BUNDLE):
                    raise Refused("source-bundle-member")
                data = archive.read(info)
                if len(data) != info.file_size:
                    raise Refused("source-bundle-member")
                result[role] = data
    except Refused:
        raise
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile,
            OverflowError, KeyError):
        raise Refused("source-bundle-invalid") from None
    return result, hashlib.sha256(raw).hexdigest()


class SourceReleaseReadAdapter:
    """Bind protected source/run/artifact objects and a three-member bundle."""

    def __init__(self, transport: ReleaseReadTransport,
                 bundle_reader: ProtectedBundleReader,
                 attestor: ProtectedReleaseAttestation,
                 revision: str, repository_id: int, run_id: int,
                 run_attempt: int, artifact_id: int, producer_actor_id: int,
                 release_workflow_path: str, candidate_sha256: str,
                 now: dt.datetime):
        if (transport is None or bundle_reader is None or attestor is None
                or len({id(transport), id(bundle_reader), id(attestor)}) != 3
                or not candidate._hex(revision, candidate.HEX40)
                or not all(_positive(item) for item in
                           (repository_id, run_id, run_attempt, artifact_id,
                            producer_actor_id))
                or type(release_workflow_path) is not str
                or not release_workflow_path.startswith(".github/workflows/")
                or not release_workflow_path.endswith(".yml")
                or ".." in release_workflow_path.split("/")
                or not candidate._hex(candidate_sha256, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("source-selection-invalid")
        self.transport = transport
        self.bundle_reader = bundle_reader
        self.attestor = attestor
        self.revision = revision
        self.repository_id = repository_id
        self.run_id = run_id
        self.run_attempt = run_attempt
        self.artifact_id = artifact_id
        self.producer_actor_id = producer_actor_id
        self.release_workflow_path = release_workflow_path
        self.candidate_sha256 = candidate_sha256
        self.now = now

    def observe_source_release(self) -> dict[str, Any]:
        scope_before = _scope(self.transport, self.repository_id, self.now)
        commit_path = f"repos/{REPOSITORY}/git/commits/{self.revision}"
        commit, commit_sha = _read(self.transport, commit_path)
        tree = commit.get("tree") if type(commit) is dict else None
        if (type(commit) is not dict or commit.get("sha") != self.revision
                or commit.get("url") != f"{API}/{commit_path}"
                or type(tree) is not dict
                or not candidate._hex(tree.get("sha"), candidate.HEX40)
                or tree.get("url") !=
                   f"{API}/repos/{REPOSITORY}/git/trees/{tree['sha']}"):
            raise Refused("source-commit-binding")
        run_path = (f"repos/{REPOSITORY}/actions/runs/{self.run_id}/"
                    f"attempts/{self.run_attempt}")
        run, run_sha = _read(self.transport, run_path)
        actor = run.get("actor") if type(run) is dict else None
        triggering = run.get("triggering_actor") if type(run) is dict else None
        run_repo = run.get("repository") if type(run) is dict else None
        head_repo = run.get("head_repository") if type(run) is dict else None
        if (type(run) is not dict or type(run.get("id")) is not int
                or run["id"] != self.run_id
                or type(run.get("run_attempt")) is not int
                or run["run_attempt"] != self.run_attempt
                or run.get("head_sha") != self.revision
                or run.get("head_branch") != "main"
                or run.get("event") != "workflow_dispatch"
                or run.get("status") != "completed"
                or run.get("conclusion") != "success"
                or run.get("path") != self.release_workflow_path
                or type(run_repo) is not dict
                or run_repo.get("id") != self.repository_id
                or run_repo.get("full_name") != REPOSITORY
                or type(head_repo) is not dict
                or head_repo.get("id") != self.repository_id
                or head_repo.get("full_name") != REPOSITORY
                or type(actor) is not dict or type(actor.get("id")) is not int
                or actor["id"] != self.producer_actor_id
                or triggering != actor):
            raise Refused("source-run-binding")
        artifact_path = f"repos/{REPOSITORY}/actions/artifacts/{self.artifact_id}"
        artifact, artifact_sha = _read(self.transport, artifact_path)
        artifact_run = artifact.get("workflow_run") if type(artifact) is dict else None
        download_url = f"{API}/{artifact_path}/zip"
        if (type(artifact) is not dict
                or type(artifact.get("id")) is not int
                or artifact["id"] != self.artifact_id
                or artifact.get("name") != "callable-isolated-v2-effect"
                or artifact.get("url") != f"{API}/{artifact_path}"
                or artifact.get("archive_download_url") != download_url
                or artifact.get("expired") is not False
                or not _positive(artifact.get("size_in_bytes"))
                or type(artifact_run) is not dict
                or artifact_run.get("id") != self.run_id
                or artifact_run.get("repository_id") != self.repository_id
                or artifact_run.get("head_repository_id") != self.repository_id
                or artifact_run.get("head_sha") != self.revision):
            raise Refused("source-artifact-binding")
        scope_after = _scope(self.transport, self.repository_id, self.now)
        if scope_after != scope_before:
            raise Refused("source-scope-drift")
        try:
            raw_bundle = self.bundle_reader.read_bundle(self.artifact_id,
                                                        download_url)
        except Exception:
            raise Refused("source-bundle-unavailable") from None
        blobs, bundle_sha = _bundle(raw_bundle)
        if len(raw_bundle) != artifact["size_in_bytes"]:
            raise Refused("source-bundle-size")
        published_digest = artifact.get("digest")
        if published_digest != f"sha256:{bundle_sha}":
            raise Refused("source-bundle-digest")
        member_hashes = tuple(hashlib.sha256(blobs[role]).hexdigest()
                              for role in ("operator", "controls", "archive"))
        if member_hashes[2] == candidate.INSPECT_ONLY_ARCHIVE:
            raise Refused("source-inspect-only-archive")
        response_hashes = (commit_sha, run_sha, artifact_sha)
        try:
            attested = self.attestor.read_release(
                self.revision, self.run_id, self.artifact_id,
                response_hashes, bundle_sha, member_hashes)
        except Exception:
            raise Refused("source-attestation-unavailable") from None
        attested = _exact(attested, {"schema", "complete", "principalId",
                    "credentialId", "recordId", "revision", "sourceTree",
                    "repositoryId", "runId", "runAttempt", "artifactId",
                    "producerActorId", "workflowPath", "responseSha256",
                    "bundleSha256", "memberSha256", "runtime", "observedAt"},
                    "source-attestation-shape")
        runtime = _exact(attested["runtime"], {"runnerImage",
                    "imageAttestationSha256", "interpreterSha256",
                    "closureSha256"}, "source-runtime-shape")
        if (attested["schema"] != SCHEMA or attested["complete"] is not True
                or type(attested["principalId"]) is not str
                or not attested["principalId"]
                or attested["principalId"] == scope_after["principalId"]
                or not candidate._hex(attested["credentialId"], candidate.HEX64)
                or attested["credentialId"] == scope_after["credentialId"]
                or not _positive(attested["recordId"])
                or attested["revision"] != self.revision
                or attested["sourceTree"] != tree["sha"]
                or attested["repositoryId"] != self.repository_id
                or attested["runId"] != self.run_id
                or attested["runAttempt"] != self.run_attempt
                or attested["artifactId"] != self.artifact_id
                or attested["producerActorId"] != self.producer_actor_id
                or attested["workflowPath"] != self.release_workflow_path
                or attested["responseSha256"] != list(response_hashes)
                or type(attested["responseSha256"]) is not list
                or attested["bundleSha256"] != bundle_sha
                or attested["memberSha256"] != list(member_hashes)
                or type(attested["memberSha256"]) is not list
                or type(runtime["runnerImage"]) is not str
                or candidate.OCI.fullmatch(runtime["runnerImage"]) is None
                or any(not candidate._hex(runtime[key], candidate.HEX64)
                       for key in ("imageAttestationSha256", "interpreterSha256",
                                   "closureSha256"))):
            raise Refused("source-attestation-binding")
        try:
            observed_at = candidate._time(attested["observedAt"])
        except candidate.Refused:
            raise Refused("source-attestation-time") from None
        if not self.now - dt.timedelta(minutes=30) <= observed_at <= self.now:
            raise Refused("source-attestation-time")
        return {"envelope": {
            "schema": observers.SCHEMA, "role": "source-release", "complete": True,
            "principalId": scope_after["principalId"],
            "credentialId": scope_after["credentialId"],
            "recordId": self.artifact_id,
            "candidateSha256": self.candidate_sha256,
            "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "facts": {"coordinationRevision": self.revision,
                      "sourceTree": tree["sha"],
                      "operatorSha256": member_hashes[0],
                      "controlsSha256": member_hashes[1],
                      "effectArchiveSha256": member_hashes[2],
                      "runtime": runtime, "producerRunId": self.run_id,
                      "artifactId": self.artifact_id,
                      "producerActorId": self.producer_actor_id}},
            "blobs": blobs}
