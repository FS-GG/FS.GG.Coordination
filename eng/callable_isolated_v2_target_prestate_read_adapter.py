"""Two complete injected native absent-state reads; no HTTP or POST port."""

from __future__ import annotations

import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import sys
from typing import Any, Protocol

import callable_isolated_v2_effect_candidate as candidate

SCHEMA = "fsgg.coordination.callable-isolated-v2-target-prestate/1"
SOURCE = pathlib.Path(__file__).with_name("callable-cli-isolated-operation-v2.py")
SPEC = importlib.util.spec_from_file_location("callable_isolated_native_v2_prestate", SOURCE)
NATIVE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = NATIVE
SPEC.loader.exec_module(NATIVE)


class Refused(ValueError):
    """Fixed refusal without provider or credential contents."""


class NativeReadPort(Protocol):
    """Future protected selected-repository read-only transport; none supplied."""

    def scope(self) -> dict[str, Any]: ...

    def get(self, path: str) -> NATIVE.HttpResponse: ...


class ProtectedPrestateWitness(Protocol):
    """Future independent immutable census witness; none supplied."""

    def read_prestate(self, run_id: int, run_attempt: int,
                      prestate_sha256: str,
                      transcript_sha256: str) -> dict[str, Any]: ...


def _positive(value: Any) -> bool:
    return type(value) is int and value > 0


def _exact(value: Any, keys: set[str], reason: str) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise Refused(reason)
    return value


def _time(value: Any) -> dt.datetime:
    try:
        return candidate._time(value)
    except candidate.Refused:
        raise Refused("prestate-time") from None


def _digest(value: Any) -> str:
    try:
        raw = json.dumps(value, sort_keys=True, separators=(",", ":"),
                         ensure_ascii=True, allow_nan=False).encode("ascii")
    except (TypeError, ValueError, UnicodeError):
        raise Refused("prestate-digest-invalid") from None
    return hashlib.sha256(raw).hexdigest()


def _scope(port: NativeReadPort, target: dict[str, Any],
           now: dt.datetime) -> dict[str, Any]:
    try:
        value = port.scope()
    except Exception:
        raise Refused("prestate-scope-unavailable") from None
    value = _exact(value, {"principalId", "credentialId", "repository",
                           "repositoryId", "installationId", "permissions",
                           "expiresAt"}, "prestate-scope-shape")
    if (type(value["principalId"]) is not str or not value["principalId"]
            or not candidate._hex(value["credentialId"], candidate.HEX64)
            or value["repository"] != target["repository"]
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != target["repositoryId"]
            or type(value["installationId"]) is not int
            or value["installationId"] != target["installationId"]
            or value["permissions"] != {"metadata": "read", "contents": "read",
                                        "pull_requests": "read"}
            or _time(value["expiresAt"]) <= now):
        raise Refused("prestate-scope-binding")
    return value


class _GetOnly:
    def __init__(self, port: NativeReadPort):
        self.port = port
        self.reads = 0

    def request(self, method: str, path: str, body: object) -> NATIVE.HttpResponse:
        if method != "GET" or body is not None:
            raise Refused("prestate-dispatch-closed")
        self.reads += 1
        try:
            return self.port.get(path)
        except Exception:
            raise Refused("prestate-read-unavailable") from None


class CompleteTargetPrestateAdapter:
    """Observe no matching PR marker twice using the native v2 reader."""

    def __init__(self, port: NativeReadPort,
                 witness: ProtectedPrestateWitness,
                 selected: dict[str, Any], run_id: int, run_attempt: int,
                 now: dt.datetime):
        if (port is None or witness is None or port is witness
                or not _positive(run_id) or not _positive(run_attempt)
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("prestate-selection-invalid")
        selected = _exact(selected, {"repository", "repositoryId", "nodeId",
                    "installationId", "sourceRef", "sourceSha", "baseRef",
                    "baseSha", "prestateSha256"}, "prestate-target-shape")
        if (type(selected["repository"]) is not str
                or candidate.REPOSITORY.fullmatch(selected["repository"]) is None
                or selected["repository"] == "FS-GG/.github"
                or not _positive(selected["repositoryId"])
                or not _positive(selected["installationId"])
                or type(selected["nodeId"]) is not str or not selected["nodeId"]
                or not candidate._branch(selected["sourceRef"])
                or not candidate._branch(selected["baseRef"])
                or selected["sourceRef"] == selected["baseRef"]
                or any(not candidate._hex(selected[key], candidate.HEX40)
                       for key in ("sourceSha", "baseSha"))
                or not candidate._hex(selected["prestateSha256"],
                                      candidate.HEX64)):
            raise Refused("prestate-target-binding")
        self.port = port
        self.witness = witness
        self.selected = dict(selected)
        self.run_id = run_id
        self.run_attempt = run_attempt
        self.now = now

    def observe_prestate(self) -> dict[str, Any]:
        selected = self.selected
        scope_before = _scope(self.port, selected, self.now)
        expected = NATIVE.ExpectedPull(candidate.IDENTITY, 1,
            selected["repositoryId"], selected["repository"],
            selected["sourceRef"], selected["sourceSha"],
            selected["baseRef"], selected["baseSha"])
        if not NATIVE._valid_pull(expected):
            raise Refused("prestate-native-selection")
        get_only = _GetOnly(self.port)
        try:
            first = NATIVE.NativeReadAdapter(get_only).read_pull_census(expected)
            second = NATIVE.NativeReadAdapter(get_only).read_pull_census(expected)
        except Exception:
            raise Refused("prestate-native-incomplete") from None
        if (type(first) is not NATIVE.PullCensus
                or type(second) is not NATIVE.PullCensus
                or first.complete is not True or second.complete is not True
                or first.repository_id != selected["repositoryId"]
                or second.repository_id != selected["repositoryId"]
                or first.source_branch_sha != selected["sourceSha"]
                or second.source_branch_sha != selected["sourceSha"]
                or first.base_branch_sha != selected["baseSha"]
                or second.base_branch_sha != selected["baseSha"]
                or first.pulls != () or second.pulls != ()
                or not candidate._hex(first.transcript_sha256, candidate.HEX64)
                or first.transcript_sha256 != second.transcript_sha256
                or get_only.reads < 16):
            raise Refused("prestate-native-mismatch")
        scope_after = _scope(self.port, selected, self.now)
        if scope_after != scope_before:
            raise Refused("prestate-scope-drift")
        digest = _digest({"schema": SCHEMA, "repository": selected["repository"],
            "repositoryId": selected["repositoryId"],
            "nodeId": selected["nodeId"],
            "installationId": selected["installationId"],
            "sourceRef": selected["sourceRef"],
            "sourceSha": selected["sourceSha"],
            "baseRef": selected["baseRef"],
            "baseSha": selected["baseSha"],
            "runId": self.run_id, "runAttempt": self.run_attempt,
            "transcriptSha256": first.transcript_sha256})
        if digest != selected["prestateSha256"]:
            raise Refused("prestate-selected-digest")
        try:
            proof = self.witness.read_prestate(
                self.run_id, self.run_attempt, digest,
                first.transcript_sha256)
        except Exception:
            raise Refused("prestate-witness-unavailable") from None
        proof = _exact(proof, {"schema", "complete", "principalId",
                       "credentialId", "recordId", "runId", "runAttempt",
                       "repository", "repositoryId", "nodeId",
                       "installationId", "sourceRef", "sourceSha",
                       "baseRef", "baseSha", "prestateSha256",
                       "transcriptSha256", "observedAt", "expiresAt"},
                       "prestate-witness-shape")
        if (proof["schema"] != SCHEMA or proof["complete"] is not True
                or type(proof["principalId"]) is not str
                or not proof["principalId"]
                or proof["principalId"] == scope_after["principalId"]
                or not candidate._hex(proof["credentialId"], candidate.HEX64)
                or proof["credentialId"] == scope_after["credentialId"]
                or not _positive(proof["recordId"])
                or not _positive(proof["runId"])
                or not _positive(proof["runAttempt"])
                or proof["runId"] != self.run_id
                or proof["runAttempt"] != self.run_attempt
                or any(proof[key] != selected[key] for key in
                       ("repository", "repositoryId", "nodeId",
                        "installationId", "sourceRef", "sourceSha",
                        "baseRef", "baseSha", "prestateSha256"))
                or proof["transcriptSha256"] != first.transcript_sha256):
            raise Refused("prestate-witness-binding")
        observed = _time(proof["observedAt"])
        expires = _time(proof["expiresAt"])
        if not self.now - dt.timedelta(minutes=30) <= observed <= self.now < expires:
            raise Refused("prestate-witness-time")
        return {"schema": SCHEMA, "complete": True,
                "principalId": scope_after["principalId"],
                "credentialId": scope_after["credentialId"],
                "recordId": proof["recordId"],
                "runId": self.run_id, "runAttempt": self.run_attempt,
                "target": dict(selected),
                "transcriptSha256": first.transcript_sha256,
                "observedAt": self.now.strftime("%Y-%m-%dT%H:%M:%SZ")}
