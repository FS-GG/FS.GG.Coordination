"""Injected target/App-scope reader; no provider or execution credential exists."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
from typing import Any, Protocol

import callable_isolated_v2_candidate_observers as observers
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_workflow_read_adapter as workflow

API = "https://api.github.com"
SCHEMA = "fsgg.coordination.callable-isolated-v2-target-attestation/1"
MAX_RESPONSE = 1_000_000
PERMISSIONS = {"metadata": "read", "contents": "read", "pull_requests": "write"}


class Refused(ValueError):
    """Fixed refusal without provider or credential contents."""


class TargetReadTransport(Protocol):
    """Future read-only selected-repository transport; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def get(self, path: str) -> workflow.Response: ...


class ProtectedTargetAttestation(Protocol):
    """Future separate target prestate and execution scope reader; none supplied."""

    def read_target(self, target_sha256: str,
                    response_sha256: tuple[str, str, str, str]) -> dict[str, Any]: ...


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _scope(transport: TargetReadTransport, repository_id: int,
           installation_id: int, now: dt.datetime) -> dict[str, Any]:
    try:
        value = transport.scope()
    except Exception:
        raise Refused("target-reader-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "installationId",
                           "repositoryIds", "permissions", "expiresAt"},
                   "target-reader-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or not _positive(value["installationId"])
            or value["installationId"] != installation_id
            or value["repositoryIds"] != [repository_id]
            or type(value["repositoryIds"]) is not list
            or value["permissions"] != {"metadata": "read", "contents": "read"}):
        raise Refused("target-reader-scope-binding")
    try:
        if candidate._time(value["expiresAt"]) <= now:
            raise Refused("target-reader-scope-expired")
    except candidate.Refused:
        raise Refused("target-reader-scope-expired") from None
    return copy.deepcopy(value)


def _read(transport: TargetReadTransport, path: str) -> tuple[Any, str]:
    try:
        response = transport.get(path)
    except Exception:
        raise Refused("target-read-unavailable") from None
    if (type(response) is not workflow.Response or response.status != 200
            or type(response.status) is not int or type(response.headers) is not tuple
            or type(response.body) is not bytes
            or len(response.body) > MAX_RESPONSE
            or any(type(pair) is not tuple or len(pair) != 2
                   or any(type(item) is not str for item in pair)
                   for pair in response.headers)
            or any(key.lower() in {"location", "link"}
                   for key, _ in response.headers)):
        raise Refused("target-response-invalid")
    try:
        value = json.loads(response.body.decode("utf-8"),
                           object_pairs_hook=workflow._unique,
                           parse_constant=workflow._nonfinite)
    except (UnicodeError, ValueError):
        raise Refused("target-json-invalid") from None
    return value, hashlib.sha256(response.body).hexdigest()


class TargetScopeReadAdapter:
    """Derive #587 target facts only when a separate protected attestor agrees."""

    def __init__(self, transport: TargetReadTransport,
                 attestor: ProtectedTargetAttestation,
                 selected: dict[str, Any], candidate_sha256: str,
                 now: dt.datetime):
        if (transport is None or attestor is None or transport is attestor
                or not candidate._hex(candidate_sha256, candidate.HEX64)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("target-selection-invalid")
        selected = _exact(selected, {"repository", "repositoryId", "nodeId",
                "installationId", "sourceRef", "sourceSha", "baseRef", "baseSha",
                "prestateSha256"}, "target-selection-shape")
        repository = selected["repository"]
        if (type(repository) is not str
                or candidate.REPOSITORY.fullmatch(repository) is None
                or repository == "FS-GG/.github"
                or repository.split("/", 1)[1] in {".", ".."}
                or not _positive(selected["repositoryId"])
                or not _positive(selected["installationId"])
                or type(selected["nodeId"]) is not str
                or not selected["nodeId"]
                or not candidate._branch(selected["sourceRef"])
                or not candidate._branch(selected["baseRef"])
                or selected["sourceRef"] == selected["baseRef"]
                or any(not candidate._hex(selected[key], candidate.HEX40)
                       for key in ("sourceSha", "baseSha"))
                or not candidate._hex(selected["prestateSha256"], candidate.HEX64)):
            raise Refused("target-selection-invalid")
        self.transport = transport
        self.attestor = attestor
        self.selected = copy.deepcopy(selected)
        self.candidate_sha256 = candidate_sha256
        self.now = now

    def observe_target_scope(self) -> dict[str, Any]:
        selected = self.selected
        repo = selected["repository"]
        repo_id = selected["repositoryId"]
        installation_id = selected["installationId"]
        scope_before = _scope(self.transport, repo_id, installation_id, self.now)
        repository, repository_sha = _read(self.transport, f"repos/{repo}")
        if (type(repository) is not dict or type(repository.get("id")) is not int
                or repository["id"] != repo_id
                or repository.get("node_id") != selected["nodeId"]
                or repository.get("full_name") != repo
                or repository.get("name") != repo.split("/", 1)[1]
                or repository.get("url") != f"{API}/repos/{repo}"
                or repository.get("archived") is not False
                or repository.get("disabled") is not False):
            raise Refused("target-repository-binding")
        accessible, accessible_sha = _read(self.transport,
                                            "installation/repositories?per_page=100")
        if (type(accessible) is not dict
                or type(accessible.get("total_count")) is not int
                or accessible["total_count"] != 1
                or type(accessible.get("repositories")) is not list
                or len(accessible["repositories"]) != 1):
            raise Refused("target-selection-incomplete")
        only = accessible["repositories"][0]
        if (type(only) is not dict or type(only.get("id")) is not int
                or only["id"] != repo_id
                or only.get("node_id") != selected["nodeId"]
                or only.get("full_name") != repo
                or only.get("url") != f"{API}/repos/{repo}"):
            raise Refused("target-selection-binding")
        ref_hashes = []
        for kind in ("source", "base"):
            ref = selected[f"{kind}Ref"]
            sha = selected[f"{kind}Sha"]
            path = f"repos/{repo}/git/ref/{ref.removeprefix('refs/')}"
            value, digest = _read(self.transport, path)
            obj = value.get("object") if type(value) is dict else None
            if (type(value) is not dict or value.get("ref") != ref
                    or value.get("url") != f"{API}/{path}"
                    or type(obj) is not dict or obj.get("type") != "commit"
                    or obj.get("sha") != sha
                    or obj.get("url") != f"{API}/repos/{repo}/git/commits/{sha}"):
                raise Refused("target-ref-binding")
            ref_hashes.append(digest)
        scope_after = _scope(self.transport, repo_id, installation_id, self.now)
        if scope_after != scope_before:
            raise Refused("target-reader-scope-drift")
        target_sha = hashlib.sha256(json.dumps(
            selected, sort_keys=True, separators=(",", ":"), ensure_ascii=True
        ).encode("ascii")).hexdigest()
        hashes = (repository_sha, accessible_sha, *ref_hashes)
        try:
            attested = self.attestor.read_target(target_sha, hashes)
        except Exception:
            raise Refused("target-attestation-unavailable") from None
        attested = _exact(attested, {"schema", "complete", "principalId",
                "credentialId", "recordId", "targetSha256", "responseSha256",
                "prestateSha256", "credential"}, "target-attestation-shape")
        credential = _exact(attested["credential"],
            {"kind", "installationId", "repositoryIds", "permissions", "expiresAt"},
            "target-credential-shape")
        if (attested["schema"] != SCHEMA or attested["complete"] is not True
                or type(attested["principalId"]) is not str
                or not attested["principalId"]
                or attested["principalId"] == scope_after["principalId"]
                or not candidate._hex(attested["credentialId"], candidate.HEX64)
                or attested["credentialId"] == scope_after["credentialId"]
                or not _positive(attested["recordId"])
                or attested["targetSha256"] != target_sha
                or type(attested["responseSha256"]) is not list
                or attested["responseSha256"] != list(hashes)
                or attested["prestateSha256"] != selected["prestateSha256"]
                or credential["kind"] != "github-app-installation"
                or credential["installationId"] != installation_id
                or type(credential["repositoryIds"]) is not list
                or credential["repositoryIds"] != [repo_id]
                or credential["permissions"] != PERMISSIONS):
            raise Refused("target-attestation-binding")
        try:
            expiry = candidate._time(credential["expiresAt"])
        except candidate.Refused:
            raise Refused("target-credential-expired") from None
        if not self.now < expiry <= self.now + dt.timedelta(hours=1):
            raise Refused("target-credential-expired")
        return {"envelope": {
            "schema": observers.SCHEMA, "role": "target-scope", "complete": True,
            "principalId": scope_after["principalId"],
            "credentialId": scope_after["credentialId"],
            "recordId": attested["recordId"],
            "candidateSha256": self.candidate_sha256,
            "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "facts": {"target": selected, "credential": credential}},
            "blobs": {}}
