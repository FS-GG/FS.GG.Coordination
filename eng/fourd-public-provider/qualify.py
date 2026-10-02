#!/usr/bin/env python3
"""Fail-closed public wrapper for the exact private FourD qualification route."""

from __future__ import annotations

import argparse
import base64
import binascii
import ctypes
import datetime as dt
import errno
import hashlib
import json
import os
import pathlib
import re
import selectors
import shutil
import signal
import stat
import subprocess
import tarfile
import threading
import time
import urllib.request
import urllib.parse
import importlib.util
from dataclasses import dataclass
from typing import Mapping, Sequence

SCHEMA = "fsgg.fourd.public-provider-qualification/1"
ADMISSION_SCHEMA = "fsgg.fourd.public-provider-admission/2"
EXPECTED_REPOSITORY = "FS-GG/FS.GG.Coordination"
EXPECTED_REPOSITORY_ID = "1346720714"
EXPECTED_REF = "refs/heads/qualification/fourd-native-20261001"
EXPECTED_ENVIRONMENT = "fourd-native-private-source"
EXPECTED_WORKFLOW = ".github/workflows/fourd-public-provider-qualification.yml"
EXPECTED_WORKFLOW_REF = f"{EXPECTED_REPOSITORY}/{EXPECTED_WORKFLOW}@{EXPECTED_REF}"
FOURD_REPOSITORY = "FS-GG/FS.GG.FourD"
FOURD_REPOSITORY_ID = "1390568106"
FOURD_SHA = "d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264"
FOURD_TREE = "ae626190a30a784db8968157a1ef1c9c5c499770"
FOURD_INVENTORY = "bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49"
P2_SHA = "c069263c3e9e8780b1596eee82d2f6c017daa8df"
P2_TREE = "d525a227f5df61b551e09915df51d7d4bd9ec11e"
SETUP_DOTNET_SHA = "a98b56852c35b8e3190ac28c8c2271da59106c68"
SETUP_DOTNET_TREE = "15c9fa70dcd5f24ac632b4f7d10f2187e490c88b"
SETUP_DOTNET_ACTION_SHA256 = "75999c0bf863f23de88dad047ccf176b5089454cf7e3264c3fee5bc47eba4cf1"
SETUP_DOTNET_ENTRY_SHA256 = "2f84c07aa0be5d2b1fce196bd549bb707b7ec01afa238533dee50c9e4b794569"
KNOWN_HOST_SHA256 = "6233fddbb0a29afc8c4e8c699733c1a188c3a41f2fb63a2640653dc4aea624ce"
KNOWN_HOST_FINGERPRINT = "SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU"
NATIVE_POLICY_SHA256 = "92796150e503ee06f7dcda8e363d65d8d26a66563b142d8d1a58bfcfbd69ee45"
CUSTODY_POLICY_SHA256 = "50cd8ee14ce64f5073f68286bf70f5a4e15d919728e31abaa1674e6ab3abd46b"
SEALER_RECIPE_SHA = "88d65daa1a1262d563c2312698e4a5e57109a824"
SEALER_SHA256 = "4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d"
PUBLIC_KEY_SHA256 = "de40516580a5e6e97c154a8889c3ac6edf047b695ff5d4187386aeaccd61d3e7"
ADMISSION_SECRET = "FSGG_FOURD_PUBLIC_PROVIDER_ADMISSION_JSON_B64"
KEY_SECRET = "FSGG_FOURD_SOURCE_CAPSULE_PRIVATE_KEY_B64"
MAX_ADMISSION = 32 * 1024
MAX_KEY = 16 * 1024
MAX_CAPTURE = 128 * 1024
MAX_RESULT = 8 * 1024
NATIVE_SUPERVISOR_GRACE = 240
TERM_SETTLE_SECONDS = 5
KILL_SETTLE_SECONDS = 10
WAIT_SECONDS = 1
DRAIN_SECONDS = 5
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
DECIMAL = re.compile(r"[1-9][0-9]*\Z")
NONCE = re.compile(r"[a-z0-9][a-z0-9-]{7,47}\Z")
LOGIN = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\Z")

ADMISSION_KEYS = {
    "schema", "repository", "repositoryId", "environment", "workflowPath", "workflowRef",
    "placementSha", "phase", "operationId", "runId", "runAttempt", "runNonce",
    "originalActorId", "triggeringActorId", "fourdRepository", "fourdRepositoryId",
    "fourdSourceSha", "fourdSourceTree", "fourdInventorySha256", "p2SourceSha",
    "p2SourceTree", "capacityRunId", "capacityRunAttempt", "capacityArtifactId",
    "capacityArtifactDigest", "sourceCapsule",
    "sealerRecipeSha", "sealerSha256", "custodyPublicKeySha256", "nativePolicySha256",
    "custodyPolicySha256", "environmentReadbackSha256", "issuedAt", "expiresAt",
}
SOURCE_CAPSULE_KEYS = {"transport", "releaseId", "assetId", "tag", "name", "ciphertextBytes",
                       "ciphertextSha256", "descriptor", "descriptorSha256", "recipientPublicKeySha256"}


class Refusal(RuntimeError):
    pass


class AcquisitionRefusal(RuntimeError):
    def __init__(self, callsite: str, original: BaseException, cleanup_complete: bool):
        super().__init__("source-acquisition-refused")
        self.callsite = callsite
        self.original = original
        self.cleanup_complete = cleanup_complete


def source_module():
    name = "fsgg_fourd_source_capsule"
    if name in __import__("sys").modules:
        return __import__("sys").modules[name]
    path = pathlib.Path(__file__).with_name("source_capsule.py")
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise Refusal("source-capsule-module-refused")
    module = importlib.util.module_from_spec(spec)
    __import__("sys").modules[name] = module
    spec.loader.exec_module(module)
    return module


def typed_module():
    name = "fsgg_fourd_typed_policy"
    if name in __import__("sys").modules:
        return __import__("sys").modules[name]
    path = pathlib.Path(__file__).with_name("typed_policy.py")
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise Refusal("typed-policy-module-refused")
    module = importlib.util.module_from_spec(spec)
    __import__("sys").modules[name] = module
    spec.loader.exec_module(module)
    return module


def _os_category(error: OSError) -> str:
    if isinstance(error, FileNotFoundError) or error.errno == errno.ENOENT: return "os-not-found"
    if isinstance(error, PermissionError) or error.errno in (errno.EACCES, errno.EPERM): return "os-permission"
    if isinstance(error, TimeoutError) or error.errno == errno.ETIMEDOUT: return "os-timeout"
    return "os-io"


def _failure_observation(error: BaseException, callsite: str, cleanup_complete: bool) -> tuple[str, str, str | None, bool]:
    if isinstance(error, AcquisitionRefusal):
        return _failure_observation(error.original, error.callsite, cleanup_complete and error.cleanup_complete)
    if isinstance(error, Refusal):
        return callsite, "closed-refusal", str(error), cleanup_complete
    capsule_type = getattr(source_module(), "CapsuleRefusal", ())
    if isinstance(error, capsule_type):
        return callsite, "capsule-refusal", str(error), cleanup_complete
    if isinstance(error, OSError):
        return callsite, _os_category(error), None, cleanup_complete
    return callsite, "unexpected", None, cleanup_complete


def project_failure(*, runner, public_source: pathlib.Path, public: pathlib.Path, name: str,
                    error: BaseException, callsite: str, cleanup_complete: bool) -> tuple[str, bool]:
    observed_callsite, category, token, observed_cleanup = _failure_observation(error, callsite, cleanup_complete)
    work = public / ("typed-failure-" + name)
    try:
        _private_dir(work)
        value = typed_module().project_failure(runner=runner, source_root=public_source, work=work, name=name,
            callsite=observed_callsite, category=category, token=token, cleanup_complete=observed_cleanup)
        code = value.get("failureCode")
        if value.get("ready") is not False or not isinstance(code, str) or not code or len(code) > 64:
            raise Refusal("typed-failure-projection-refused")
        return code, value.get("cleanupComplete") is True
    except Exception:
        return "qualification-refused", observed_cleanup
    finally:
        shutil.rmtree(work, ignore_errors=True)


def verify_fixed_tools(*, runner, public_source: pathlib.Path, public: pathlib.Path) -> None:
    for name, path in (("git", pathlib.Path("/usr/bin/git")), ("node", pathlib.Path("/usr/bin/node")),
                       ("openssl", pathlib.Path("/usr/bin/openssl"))):
        category = "available"
        try:
            if not path.is_file(): category = "os-not-found"
            elif not os.access(path, os.X_OK): category = "os-permission"
        except OSError as error:
            category = _os_category(error)
        work = public / ("typed-tool-" + name)
        try:
            _private_dir(work)
            value = typed_module().project_failure(runner=runner, source_root=public_source, work=work,
                name="tool-"+name, callsite="tool-"+name, category=category, token=None,
                cleanup_complete=True)
            if value.get("ready") is not True:
                code=value.get("failureCode")
                raise Refusal(code if isinstance(code,str) and code else "qualification-refused")
        finally:
            shutil.rmtree(work, ignore_errors=True)


@dataclass(frozen=True)
class RunResult:
    returncode: int
    stdout: bytes
    stderr: bytes


@dataclass
class HeldFile:
    path: pathlib.Path
    fd: int
    device: int
    inode: int

    @classmethod
    def create(cls, path: pathlib.Path, data: bytes) -> "HeldFile":
        fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            view = memoryview(data)
            while view:
                written = os.write(fd, view)
                if written <= 0: raise Refusal("private-write-refused")
                view = view[written:]
            os.fsync(fd); os.lseek(fd, 0, os.SEEK_SET)
            value = os.fstat(fd); os.set_inheritable(fd, True)
            return cls(path, fd, value.st_dev, value.st_ino)
        except BaseException:
            os.close(fd)
            try: path.unlink()
            except OSError: pass
            raise

    @classmethod
    def open(cls, path: pathlib.Path) -> "HeldFile":
        fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW)
        value = os.fstat(fd)
        if (not stat.S_ISREG(value.st_mode) or value.st_uid != os.getuid()
                or value.st_nlink != 1 or stat.S_IMODE(value.st_mode) != 0o600):
            os.close(fd); raise Refusal("source-held-file-refused")
        os.set_inheritable(fd, True)
        return cls(path, fd, value.st_dev, value.st_ino)

    def read(self, limit: int) -> bytes:
        value = os.fstat(self.fd)
        if value.st_size > limit: raise Refusal("source-held-file-refused")
        os.lseek(self.fd, 0, os.SEEK_SET); result = bytearray()
        while len(result) <= limit:
            block = os.read(self.fd, min(1024 * 1024, limit + 1 - len(result)))
            if not block: return bytes(result)
            result.extend(block)
        raise Refusal("source-held-file-refused")

    def wipe_close(self) -> bool:
        complete = True
        try:
            size = os.fstat(self.fd).st_size; os.lseek(self.fd, 0, os.SEEK_SET)
            remaining = size
            zero = b"\0" * min(1024 * 1024, max(1, size))
            while remaining:
                written = os.write(self.fd, zero[:min(len(zero), remaining)])
                if written <= 0: complete = False; break
                remaining -= written
            os.fsync(self.fd)
        except OSError: complete = False
        try: os.close(self.fd)
        except OSError: complete = False
        try:
            current = self.path.lstat()
            if (current.st_dev, current.st_ino) == (self.device, self.inode): self.path.unlink()
            else: complete = False
        except FileNotFoundError: complete = False
        except OSError: complete = False
        return complete


def _sha(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def _private_dir(path: pathlib.Path) -> None:
    path.mkdir(mode=0o700, parents=True, exist_ok=False)
    if path.is_symlink() or (path.stat().st_mode & 0o777) != 0o700 or path.stat().st_uid != os.getuid():
        raise Refusal("private-root-refused")


def _write_new(path: pathlib.Path, data: bytes) -> None:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        view = memoryview(data)
        while view:
            written = os.write(fd, view)
            if written <= 0: raise Refusal("private-write-refused")
            view = view[written:]
        os.fsync(fd)
    finally:
        os.close(fd)


def base_result(context: Mapping[str, str], outcome: str) -> dict[str, object]:
    return {
        "schema": SCHEMA, "phase": "qualification", "outcome": outcome,
        "runId": context.get("GITHUB_RUN_ID", "unknown"),
        "runAttempt": context.get("GITHUB_RUN_ATTEMPT", "unknown"),
        "placementSha": context.get("GITHUB_SHA", "unknown"),
        "placementTree": None,
        "fourdSourceSha": FOURD_SHA, "fourdSourceTree": FOURD_TREE,
        "capacityPassed": False, "rootlessPreflightPassed": False,
        "nativeAccepted": False, "cleanupComplete": False,
        "custodyOutcome": "not-started", "archiveComplete": False,
        "manifestSha256": None, "qualified": False,
        "rootReadback": "pending-root-readback",
        "credentialRevocation": "pending-root-readback",
        "failureCode": None,
    }


def write_result(path: pathlib.Path, result: Mapping[str, object]) -> None:
    encoded = json.dumps(result, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
    if len(encoded) > MAX_RESULT:
        raise Refusal("public-result-overflow")
    _write_new(path, encoded)


def _decode(name: str, text: str, limit: int) -> bytes:
    if not text or len(text) > 4 * ((limit + 2) // 3) + 4:
        raise Refusal(f"{name}-refused")
    try:
        value = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError) as error:
        raise Refusal(f"{name}-refused") from error
    if not value or len(value) > limit:
        raise Refusal(f"{name}-refused")
    return value


def _instant(value: object) -> dt.datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise Refusal("admission-time-refused")
    try:
        parsed = dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as error:
        raise Refusal("admission-time-refused") from error
    return parsed


def triggering_actor_id(environ: Mapping[str, str]) -> str:
    actor, triggering = environ.get("GITHUB_ACTOR", ""), environ.get("GITHUB_TRIGGERING_ACTOR", "")
    actor_id = environ.get("GITHUB_ACTOR_ID", "")
    if not LOGIN.fullmatch(actor) or not LOGIN.fullmatch(triggering) or not DECIMAL.fullmatch(actor_id):
        raise Refusal("actor-context-refused")
    if actor.casefold() == triggering.casefold(): return actor_id
    request = urllib.request.Request(
        "https://api.github.com/users/" + triggering,
        headers={"Accept": "application/vnd.github+json", "User-Agent": "fsgg-fourd-public-provider/1",
                 "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            if response.status != 200: raise Refusal("triggering-actor-readback-refused")
            raw = response.read(65537)
    except Refusal: raise
    except Exception as error: raise Refusal("triggering-actor-readback-refused") from error
    if len(raw) > 65536: raise Refusal("triggering-actor-readback-refused")
    try: value = json.loads(raw)
    except json.JSONDecodeError as error: raise Refusal("triggering-actor-readback-refused") from error
    if (not isinstance(value, dict) or str(value.get("login", "")).casefold() != triggering.casefold()
            or type(value.get("id")) is not int or value["id"] <= 0):
        raise Refusal("triggering-actor-readback-refused")
    return str(value["id"])


def execution_context(environ: Mapping[str, str]) -> tuple[str, str]:
    sha = environ.get("GITHUB_SHA", "")
    if (environ.get("GITHUB_REPOSITORY") != EXPECTED_REPOSITORY
            or environ.get("GITHUB_REPOSITORY_ID") != EXPECTED_REPOSITORY_ID
            or environ.get("GITHUB_REF") != EXPECTED_REF
            or environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or environ.get("GITHUB_WORKFLOW_REF") != EXPECTED_WORKFLOW_REF
            or environ.get("GITHUB_WORKFLOW_SHA") != sha
            or not HEX40.fullmatch(sha)):
        raise Refusal("execution-context-refused")
    for name in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_ACTOR_ID"):
        if not DECIMAL.fullmatch(environ.get(name, "")):
            raise Refusal("execution-context-refused")
    return sha, environ["GITHUB_RUN_ATTEMPT"]


def admission(environ: Mapping[str, str], now: dt.datetime | None = None) -> tuple[dict[str, object] | None, bytes | None]:
    placement_sha, attempt = execution_context(environ)
    encoded_admission = environ.get(ADMISSION_SECRET, "")
    encoded_key = environ.get(KEY_SECRET, "")
    if attempt == "1":
        return None, None
    if not encoded_admission and not encoded_key:
        raise Refusal("admission-missing")
    if not encoded_admission or not encoded_key:
        raise Refusal("admission-partial")
    raw = _decode("admission", encoded_admission, MAX_ADMISSION)
    key = _decode("source-recipient-key", encoded_key, MAX_KEY)
    current_triggering_actor_id = triggering_actor_id(environ)
    try:
        def closed_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
            result: dict[str, object] = {}
            for key_name, item in pairs:
                if key_name in result: raise Refusal("admission-json-refused")
                result[key_name] = item
            return result
        value = json.loads(raw, object_pairs_hook=closed_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Refusal("admission-json-refused") from error
    if not isinstance(value, dict) or set(value) != ADMISSION_KEYS:
        raise Refusal("admission-shape-refused")
    exact = {
        "schema": ADMISSION_SCHEMA, "repository": EXPECTED_REPOSITORY,
        "repositoryId": EXPECTED_REPOSITORY_ID, "environment": EXPECTED_ENVIRONMENT,
        "workflowPath": EXPECTED_WORKFLOW, "workflowRef": EXPECTED_WORKFLOW_REF,
        "placementSha": placement_sha, "phase": "qualification",
        "operationId": "fourd-portable-technical",
        "runId": environ.get("GITHUB_RUN_ID"), "runAttempt": attempt,
        "originalActorId": environ.get("GITHUB_ACTOR_ID"),
        "triggeringActorId": current_triggering_actor_id,
        "fourdRepository": FOURD_REPOSITORY, "fourdRepositoryId": FOURD_REPOSITORY_ID,
        "fourdSourceSha": FOURD_SHA, "fourdSourceTree": FOURD_TREE,
        "fourdInventorySha256": FOURD_INVENTORY, "p2SourceSha": P2_SHA,
        "p2SourceTree": P2_TREE, "sealerRecipeSha": SEALER_RECIPE_SHA,
        "sealerSha256": SEALER_SHA256, "custodyPublicKeySha256": PUBLIC_KEY_SHA256,
        "nativePolicySha256": NATIVE_POLICY_SHA256, "custodyPolicySha256": CUSTODY_POLICY_SHA256,
    }
    for field, expected in exact.items():
        if value.get(field) != expected:
            raise Refusal("admission-binding-refused")
    for field in ("runId", "runAttempt", "originalActorId", "triggeringActorId", "capacityRunId", "capacityRunAttempt", "capacityArtifactId"):
        if not isinstance(value[field], str) or not DECIMAL.fullmatch(value[field]):
            raise Refusal("admission-identifier-refused")
    if not isinstance(value["runNonce"], str) or not NONCE.fullmatch(value["runNonce"]):
        raise Refusal("admission-nonce-refused")
    capsule = value["sourceCapsule"]
    if not isinstance(capsule, dict) or set(capsule) != SOURCE_CAPSULE_KEYS:
        raise Refusal("admission-capsule-shape-refused")
    if capsule.get("transport") != "coordination-release-asset":
        raise Refusal("admission-capsule-transport-refused")
    for field in ("releaseId", "assetId"):
        if not isinstance(capsule[field], str) or not DECIMAL.fullmatch(capsule[field]):
            raise Refusal("admission-capsule-identifier-refused")
    for field in ("ciphertextSha256", "descriptorSha256", "recipientPublicKeySha256"):
        if not isinstance(capsule[field], str) or not HEX64.fullmatch(capsule[field]):
            raise Refusal("admission-capsule-digest-refused")
    if (not isinstance(capsule["tag"], str) or not NONCE.fullmatch(capsule["tag"])
            or not isinstance(capsule["name"], str) or not re.fullmatch(r"fourd-source-[a-z0-9-]{8,64}\.capsule\.json", capsule["name"])):
        raise Refusal("admission-capsule-name-refused")
    if type(capsule["ciphertextBytes"]) is not int or not 2 <= capsule["ciphertextBytes"] <= 24 * 1024 * 1024:
        raise Refusal("admission-capsule-size-refused")
    descriptor = capsule["descriptor"]
    descriptor_raw = json.dumps(descriptor, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
    if hashlib.sha256(descriptor_raw).hexdigest() != capsule["descriptorSha256"]:
        raise Refusal("admission-descriptor-digest-refused")
    source_module().validate_descriptor(descriptor, {
        "placementSha": placement_sha, "runId": value["runId"], "runAttempt": attempt,
        "runNonce": value["runNonce"], "coordinationRepositoryId": EXPECTED_REPOSITORY_ID,
        "fourdRepositoryId": FOURD_REPOSITORY_ID, "sourceSha": FOURD_SHA, "sourceTree": FOURD_TREE,
        "inventorySha256": FOURD_INVENTORY, "recipientPublicKeySha256": capsule["recipientPublicKeySha256"],
        "sealerSha256": SEALER_SHA256, "issuedAt": value["issuedAt"], "expiresAt": value["expiresAt"],
    })
    for field in ("capacityArtifactDigest", "environmentReadbackSha256"):
        if not isinstance(value[field], str) or not HEX64.fullmatch(value[field]):
            raise Refusal("admission-digest-refused")
    issued, expires = _instant(value["issuedAt"]), _instant(value["expiresAt"])
    current = now or dt.datetime.now(dt.timezone.utc)
    if expires <= issued or expires - issued > dt.timedelta(minutes=45) or current < issued or current >= expires:
        raise Refusal("admission-time-refused")
    if not key.startswith(b"-----BEGIN PRIVATE KEY-----\n") or not key.endswith(b"-----END PRIVATE KEY-----\n"):
        raise Refusal("source-recipient-key-format-refused")
    return value, key


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    start_ticks: int


def _process_observation(pid: int) -> tuple[str, ProcessIdentity | None]:
    try:
        raw = pathlib.Path(f"/proc/{pid}/stat").read_text()
    except (FileNotFoundError, ProcessLookupError):
        return "dead", None
    except (PermissionError, OSError):
        return "unknown", None
    close = raw.rfind(")")
    if close < 1:
        return "unknown", None
    fields = raw[close + 2:].split()
    if len(fields) < 20:
        return "unknown", None
    if fields[0] == "Z":
        return "dead", None
    try:
        return "live", ProcessIdentity(pid, int(fields[19]))
    except ValueError:
        return "unknown", None


def _process_identity(pid: int) -> ProcessIdentity | None:
    state, identity = _process_observation(pid)
    return identity if state == "live" else None


def _child_pids(pid: int) -> tuple[int, ...] | None:
    try:
        raw = pathlib.Path(f"/proc/{pid}/task/{pid}/children").read_text().strip()
    except (FileNotFoundError, ProcessLookupError):
        return ()
    except (PermissionError, OSError):
        return None
    try: return tuple(int(value) for value in raw.split()) if raw else ()
    except ValueError: return None


class Effects:
    """Finite Linux child execution with PID/start identity descendant custody."""
    def __init__(self) -> None:
        self.cancelled = False
        self.owner_pid = os.getpid()
        self.owned: dict[int, ProcessIdentity] = {}
        self.unknown_pids: set[int] = set()
        self.topology_unknown = False
        initial_children = _child_pids(self.owner_pid)
        if initial_children is None: raise Refusal("child-topology-refused")
        baseline: set[ProcessIdentity] = set()
        for pid in initial_children:
            state, identity = _process_observation(pid)
            if state == "unknown": raise Refusal("child-topology-refused")
            if state == "live":
                assert identity is not None
                baseline.add(identity)
        self._baseline = baseline
        libc = ctypes.CDLL(None, use_errno=True)
        if libc.prctl(36, 1, 0, 0, 0) != 0:  # PR_SET_CHILD_SUBREAPER
            raise Refusal("child-subreaper-refused")

    def request_cancel(self, *_args: object) -> None:
        self.cancelled = True

    @staticmethod
    def _state(identity: ProcessIdentity) -> str:
        state, current = _process_observation(identity.pid)
        if state != "live": return state
        return "live" if current == identity else "dead"

    @staticmethod
    def _signal(identity: ProcessIdentity, selected: signal.Signals) -> None:
        if Effects._state(identity) == "live":
            try: os.kill(identity.pid, selected)
            except ProcessLookupError: pass

    def _unknown_discovery(self, pid: int) -> None:
        # A PID discovered through an owned topology edge is relevant to this
        # scope, but unreadable stat data does not establish a start identity
        # and therefore grants no authority to signal it.
        self.unknown_pids.add(pid)
        self.topology_unknown = True

    def _discover(self, root_pid: int | None = None) -> None:
        queue: list[int] = []
        if root_pid is not None: queue.append(root_pid)
        queue.extend(identity.pid for identity in tuple(self.owned.values()) if self._state(identity) == "live")
        owner_children = _child_pids(self.owner_pid)
        if owner_children is None:
            self.topology_unknown = True
            owner_children = ()
        for pid in owner_children:
            if pid in self.unknown_pids: continue
            state, identity = _process_observation(pid)
            if state == "unknown":
                if pid not in self.owned: self._unknown_discovery(pid)
            elif state == "live":
                assert identity is not None
                if identity not in self._baseline: queue.append(pid)
        seen: set[int] = set()
        while queue:
            pid = queue.pop()
            if pid in seen: continue
            seen.add(pid)
            if pid in self.unknown_pids: continue
            state, identity = _process_observation(pid)
            if state == "unknown":
                if pid not in self.owned: self._unknown_discovery(pid)
                continue
            if state == "dead": continue
            assert identity is not None
            prior = self.owned.get(pid)
            if prior is not None and prior != identity:
                raise Refusal("child-identity-refused")
            self.owned[pid] = identity
            children = _child_pids(pid)
            if children is None: self.topology_unknown = True
            else: queue.extend(children)
        self._forget_dead()

    def _forget_dead(self) -> None:
        for pid, identity in tuple(self.owned.items()):
            if self._state(identity) == "dead": self.owned.pop(pid, None)

    def _unresolved(self) -> tuple[ProcessIdentity, ...]:
        self._discover()
        return tuple(identity for identity in self.owned.values() if self._state(identity) != "dead")

    def _scope_clear(self) -> bool:
        return not self._unresolved() and not self.topology_unknown

    def _terminate(self, root: ProcessIdentity, grace: float, pump, total_deadline: float) -> bool:
        # The coordinating parent receives TERM first and retains its admitted
        # cleanup grace. Descendants are observed continuously, then boundedly
        # terminated only if that protocol did not settle them.
        self._signal(root, signal.SIGTERM)
        deadline = min(total_deadline, time.monotonic() + max(0.0, grace))
        while time.monotonic() < deadline:
            self._discover(root.pid); pump()
            if self._scope_clear(): return True
            time.sleep(0.02)
        for identity in self._unresolved(): self._signal(identity, signal.SIGTERM)
        term_deadline = min(total_deadline, time.monotonic() + TERM_SETTLE_SECONDS)
        while time.monotonic() < term_deadline:
            self._discover(); pump()
            if self._scope_clear(): return True
            time.sleep(0.02)
        for identity in self._unresolved(): self._signal(identity, signal.SIGKILL)
        kill_deadline = min(total_deadline, time.monotonic() + KILL_SETTLE_SECONDS)
        while time.monotonic() < kill_deadline:
            self._discover(); pump()
            if self._scope_clear(): return True
            time.sleep(0.02)
        return self._scope_clear()

    def run(self, argv: Sequence[str], *, cwd: pathlib.Path, env: Mapping[str, str], timeout: float,
            capture: pathlib.Path | None, allow_cancelled: bool = False,
            termination_grace: float = 10, total_timeout: float | None = None,
            pass_fds: Sequence[int] = ()) -> RunResult:
        if self.cancelled and not allow_cancelled:
            raise Refusal("cancelled")
        reserve = termination_grace + TERM_SETTLE_SECONDS + KILL_SETTLE_SECONDS + WAIT_SECONDS + DRAIN_SECONDS
        started = time.monotonic()
        if total_timeout is not None and total_timeout < reserve + 1:
            raise Refusal("child-total-deadline-refused")
        total_deadline = started + (total_timeout if total_timeout is not None else timeout + reserve)
        execution_deadline = min(started + timeout, total_deadline - reserve)
        before = set(self.owned.values())
        process = subprocess.Popen(list(argv), cwd=cwd, env=dict(env), stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True,
                                   bufsize=0, pass_fds=tuple(pass_fds))
        root = _process_identity(process.pid)
        if root is None: raise Refusal("child-identity-refused")
        self.owned[root.pid] = root
        assert process.stdout is not None and process.stderr is not None
        selector = selectors.DefaultSelector()
        streams = (process.stdout, process.stderr)
        buffers = [bytearray(), bytearray()]
        totals = [0, 0]
        digests = [hashlib.sha256(), hashlib.sha256()]
        for slot, stream in enumerate(streams):
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ, slot)

        def pump(wait: float = 0.0) -> None:
            remaining = 256 * 1024
            for key, _mask in selector.select(wait)[:2]:
                slot = key.data
                while remaining > 0:
                    try: chunk = os.read(key.fileobj.fileno(), 65536)
                    except BlockingIOError: break
                    if not chunk:
                        try: selector.unregister(key.fileobj)
                        except KeyError: pass
                        break
                    remaining -= len(chunk)
                    totals[slot] += len(chunk); digests[slot].update(chunk)
                    buffers[slot].extend(chunk)
                    if len(buffers[slot]) > MAX_CAPTURE:
                        del buffers[slot][:-MAX_CAPTURE]

        forced = False
        settled = True
        while process.poll() is None and time.monotonic() < execution_deadline and (allow_cancelled or not self.cancelled):
            self._discover(root.pid); pump(0.02)
        self._discover(root.pid); pump()
        if process.poll() is None:
            forced = True
            settled = self._terminate(root, termination_grace, pump, total_deadline - WAIT_SECONDS - DRAIN_SECONDS)
        else:
            # A direct parent can exit after starting a new session. The
            # subreaper adopts it; sample briefly before declaring success.
            discovery_deadline = time.monotonic() + 0.25
            while time.monotonic() < discovery_deadline:
                self._discover(root.pid); pump(0.02)
            unresolved = tuple(identity for identity in self._unresolved() if identity != root)
            if unresolved:
                forced = True
                settled = self._terminate(root, 0, pump, total_deadline - WAIT_SECONDS - DRAIN_SECONDS)
        try: process.wait(timeout=max(0, min(WAIT_SECONDS, total_deadline - DRAIN_SECONDS - time.monotonic())))
        except subprocess.TimeoutExpired: settled = False
        drain_deadline = min(total_deadline, time.monotonic() + DRAIN_SECONDS)
        while selector.get_map() and time.monotonic() < drain_deadline:
            pump(0.05)
        if selector.get_map(): settled = False
        for stream in streams:
            try: selector.unregister(stream)
            except KeyError: pass
            stream.close()
        selector.close()
        self._discover()
        survivors = tuple(identity for identity in self.owned.values()
                          if identity not in before and self._state(identity) != "dead")
        if survivors or self.topology_unknown: settled = False
        payload = json.dumps({
            "returncode": process.returncode,
            "stdoutTruncated": totals[0] > MAX_CAPTURE, "stderrTruncated": totals[1] > MAX_CAPTURE,
            "stdoutSha256": digests[0].hexdigest(), "stderrSha256": digests[1].hexdigest(),
            "stdoutTailBase64": base64.b64encode(buffers[0]).decode(),
            "stderrTailBase64": base64.b64encode(buffers[1]).decode()},
            sort_keys=True, separators=(",", ":")).encode() + b"\n"
        if capture is not None: _write_new(capture, payload)
        if not settled: raise Refusal("child-scope-refused")
        if self.cancelled and not allow_cancelled: raise Refusal("cancelled")
        if forced and process.returncode == 0: raise Refusal("child-scope-refused")
        return RunResult(process.returncode or 0, bytes(buffers[0]), bytes(buffers[1]))

    def settle(self, grace: float = 0) -> bool:
        unresolved = self._unresolved()
        if not unresolved: return not self.topology_unknown
        root = unresolved[0]
        total = time.monotonic() + grace + TERM_SETTLE_SECONDS + KILL_SETTLE_SECONDS
        return self._terminate(root, grace, lambda: None, total) and self._scope_clear()

def verify_capacity(path: pathlib.Path) -> None:
    value = json.loads(path.read_bytes())
    if (value.get("schema") != "fsgg.fourd.public-provider-capacity/1" or value.get("phase") != "capacity"
            or value.get("capacityScreenPassed") is not True or value.get("qualified") is not False):
        raise Refusal("capacity-refused")


def verify_public_tools(root: pathlib.Path, setup: pathlib.Path, p2: pathlib.Path) -> None:
    known = root / "eng/fourd-public-provider/github-known-hosts"
    if _sha(known) != KNOWN_HOST_SHA256:
        raise Refusal("known-host-refused")
    checks = ((setup, SETUP_DOTNET_SHA, SETUP_DOTNET_TREE), (p2, P2_SHA, P2_TREE))
    safe_env = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"}
    for directory, revision, tree in checks:
        head = subprocess.run(["/usr/bin/git", "-C", str(directory), "rev-parse", "HEAD"], check=True, stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL, env=safe_env).stdout.decode().strip()
        actual_tree = subprocess.run(["/usr/bin/git", "-C", str(directory), "rev-parse", "HEAD^{tree}"], check=True, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, env=safe_env).stdout.decode().strip()
        if (head, actual_tree) != (revision, tree): raise Refusal("public-tool-source-refused")
        status = subprocess.run(["/usr/bin/git", "-C", str(directory), "status", "--porcelain", "--untracked-files=all"],
                                check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=safe_env).stdout
        if status: raise Refusal("public-tool-source-refused")
    if _sha(setup / "action.yml") != SETUP_DOTNET_ACTION_SHA256 or _sha(setup / "dist/setup/index.js") != SETUP_DOTNET_ENTRY_SHA256:
        raise Refusal("setup-dotnet-bytes-refused")


def verify_public_checkout(root: pathlib.Path, environ: Mapping[str, str], effects: Effects,
                           capture: pathlib.Path) -> tuple[str, str]:
    expected, _attempt = execution_context(environ)
    safe_env = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"}
    values: list[str] = []
    for name, argv in (("head", ["/usr/bin/git", "rev-parse", "HEAD"]),
                       ("tree", ["/usr/bin/git", "rev-parse", "HEAD^{tree}"])):
        result = effects.run(argv, cwd=root, env=safe_env, timeout=30, capture=capture / f"public-{name}.json")
        if result.returncode:
            raise Refusal("public-placement-refused")
        try: value = result.stdout.decode("ascii", "strict").strip()
        except UnicodeDecodeError as error: raise Refusal("public-placement-refused") from error
        values.append(value)
    status = effects.run(["/usr/bin/git", "status", "--porcelain", "--untracked-files=all"],
                         cwd=root, env=safe_env, timeout=30, capture=capture / "public-status.json")
    if status.returncode or status.stdout or values[0] != expected or not HEX40.fullmatch(values[1]):
        raise Refusal("public-placement-refused")
    return values[0], values[1]


class StrictRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, hosts: set[str]): super().__init__(); self.hosts = hosts; self.count = 0
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlparse(newurl)
        self.count += 1
        if self.count > 4 or parsed.scheme != "https" or parsed.hostname not in self.hosts:
            raise Refusal("source-download-redirect-refused")
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def bounded_public_read(request: urllib.request.Request, *, limit: int, deadline: float,
                        hosts: set[str], effects: Effects) -> tuple[bytes, str]:
    remaining = deadline - time.monotonic()
    if remaining <= 0 or effects.cancelled: raise Refusal("source-download-timeout")
    redirect = StrictRedirect(hosts); opener = urllib.request.build_opener(redirect)
    try:
        with opener.open(request, timeout=min(15.0, remaining)) as response:
            final = urllib.parse.urlparse(response.geturl())
            if final.scheme != "https" or final.hostname not in hosts: raise Refusal("source-download-redirect-refused")
            value = bytearray()
            while len(value) <= limit:
                if time.monotonic() >= deadline or effects.cancelled: raise Refusal("source-download-timeout")
                block = response.read(min(65536, limit + 1 - len(value)))
                if not block: return bytes(value), response.geturl()
                value.extend(block)
    except Refusal: raise
    except Exception as error: raise Refusal("source-download-refused") from error
    raise Refusal("source-download-overflow")


def acquire_source(key: bytes, admission_value: Mapping[str, object], private: pathlib.Path,
                   _known_hosts: pathlib.Path, effects: Effects, env: Mapping[str, str]) -> pathlib.Path:
    deadline = time.monotonic() + 600
    credentials, source, capture = private / "credentials", private / "source", private / "capture"
    _private_dir(credentials); _private_dir(capture)
    key_path = credentials / "source-recipient.pem"
    capsule_path = credentials / "source.capsule.json"
    plaintext_path = credentials / "source.snapshot.json"
    held: list[HeldFile] = []
    safe_env = {"PATH": "/usr/bin:/bin", "HOME": str(private / "home"), "LANG": "C.UTF-8",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_TERMINAL_PROMPT": "0"}
    capsule = admission_value["sourceCapsule"]
    original: BaseException | None = None
    failure_callsite = "source-recipient"
    receipt: dict[str, object] | None = None
    try:
        key_file = HeldFile.create(key_path, key); held.append(key_file)
        public = effects.run(["/usr/bin/openssl", "pkey", "-in", f"/proc/self/fd/{key_file.fd}", "-pubout"], cwd=private,
                             env=safe_env, timeout=min(15, max(1, deadline-time.monotonic())),
                             capture=capture / "source-key-readback.json", pass_fds=(key_file.fd,))
        if public.returncode or hashlib.sha256(public.stdout).hexdigest() != capsule["recipientPublicKeySha256"]:
            raise Refusal("source-recipient-key-refused")
        failure_callsite = "source-release-metadata"
        metadata_url = "https://api.github.com/repos/FS-GG/FS.GG.Coordination/releases/" + capsule["releaseId"]
        metadata_request = urllib.request.Request(metadata_url, headers={"Accept":"application/vnd.github+json",
            "User-Agent":"fsgg-fourd-source-acquisition/1", "X-GitHub-Api-Version":"2022-11-28"})
        metadata_raw, _ = bounded_public_read(metadata_request, limit=256 * 1024, deadline=deadline,
                                               hosts={"api.github.com"}, effects=effects)
        try: metadata = json.loads(metadata_raw)
        except json.JSONDecodeError as error: raise Refusal("source-release-readback-refused") from error
        if not isinstance(metadata, dict): raise Refusal("source-release-readback-refused")
        assets = metadata.get("assets")
        matching = [item for item in assets or [] if isinstance(item, dict) and str(item.get("id")) == capsule["assetId"]]
        if (str(metadata.get("id")) != capsule["releaseId"] or metadata.get("tag_name") != capsule["tag"]
                or len(matching) != 1 or matching[0].get("name") != capsule["name"]
                or matching[0].get("size") != capsule["ciphertextBytes"]):
            raise Refusal("source-release-binding-refused")
        failure_callsite = "source-download"
        asset_url = "https://api.github.com/repos/FS-GG/FS.GG.Coordination/releases/assets/" + capsule["assetId"]
        request = urllib.request.Request(asset_url, headers={"Accept":"application/octet-stream",
            "User-Agent":"fsgg-fourd-source-acquisition/1", "X-GitHub-Api-Version":"2022-11-28"})
        raw, _ = bounded_public_read(request, limit=24 * 1024 * 1024, deadline=deadline,
            hosts={"api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"},
            effects=effects)
        if len(raw) != capsule["ciphertextBytes"] or hashlib.sha256(raw).hexdigest() != capsule["ciphertextSha256"]:
            raise Refusal("source-download-binding-refused")
        failure_callsite = "source-outer-capsule"
        module = source_module()
        descriptor_raw = json.dumps(capsule["descriptor"], sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
        profile = hashlib.sha256(descriptor_raw).hexdigest()
        module.validate_outer_capsule(raw, str(admission_value["runNonce"]) + "-source", FOURD_SHA, profile)
        capsule_file = HeldFile.create(capsule_path, raw); held.append(capsule_file)
        failure_callsite = "source-unseal"
        unseal_failure: BaseException | None = None
        try:
            remaining = deadline - time.monotonic()
            if remaining <= 46: raise Refusal("source-acquisition-timeout")
            unseal = effects.run(["/usr/bin/node", str(pathlib.Path(__file__).with_name("seal_native_custody.mjs")),
                                  "unseal", f"/proc/self/fd/{capsule_file.fd}", f"/proc/self/fd/{key_file.fd}", str(plaintext_path),
                                  str(admission_value["runNonce"]) + "-source", FOURD_SHA, profile],
                                 cwd=private, env=safe_env, timeout=min(120, remaining-26),
                                 total_timeout=remaining, capture=capture / "source-unseal.json",
                                 pass_fds=(capsule_file.fd, key_file.fd))
            if unseal.returncode: raise Refusal("source-unseal-refused")
        except BaseException as error:
            unseal_failure = error
        if plaintext_path.exists():
            try: held.append(HeldFile.open(plaintext_path))
            except BaseException as error:
                if unseal_failure is None: unseal_failure = error
        if unseal_failure is not None: raise unseal_failure
        failure_callsite = "source-plaintext"
        plaintext_file = held[-1]
        if plaintext_file.path != plaintext_path: raise Refusal("source-plaintext-refused")
        plaintext = plaintext_file.read(16 * 1024 * 1024)
        descriptor = capsule["descriptor"]
        if len(plaintext) != descriptor["plaintextBytes"] or hashlib.sha256(plaintext).hexdigest() != descriptor["plaintextSha256"]:
            raise Refusal("source-plaintext-binding-refused")
        reserve = NATIVE_SUPERVISOR_GRACE + TERM_SETTLE_SECONDS + KILL_SETTLE_SECONDS + WAIT_SECONDS + DRAIN_SECONDS
        failure_callsite = "source-reconstruction"
        def reconstruction_runner(argv: list[str], cwd: pathlib.Path, _timeout: float) -> bytes:
            remaining = deadline - time.monotonic()
            if remaining <= reserve + 1 or effects.cancelled: raise module.CapsuleRefusal("git-reconstruction-timeout")
            result = effects.run(argv, cwd=cwd, env=safe_env, timeout=min(30, remaining-reserve),
                                 total_timeout=remaining, capture=None)
            if result.returncode or len(result.stdout) > 65536: raise module.CapsuleRefusal("git-reconstruction-refused")
            return result.stdout
        module.reconstruct(plaintext, source, {"sourceSha":FOURD_SHA, "sourceTree":FOURD_TREE,
                           "inventorySha256":FOURD_INVENTORY}, runner=reconstruction_runner,
                           deadline_monotonic=deadline)
        if not effects.settle(): raise Refusal("child-scope-refused")
        receipt = {"schema":"fsgg.fourd.source-acquisition-receipt/1", "sourceSha":FOURD_SHA,
                   "sourceTree":FOURD_TREE, "inventorySha256":FOURD_INVENTORY,
                   "descriptorSha256":capsule["descriptorSha256"], "ciphertextSha256":capsule["ciphertextSha256"],
                   "assetId":capsule["assetId"], "preparationComplete":True, "sourceKeyAbsent":True}
    except BaseException as error:
        original = error
    finally:
        try: scope_settled = effects.settle()
        except BaseException: scope_settled = False
        if plaintext_path.exists() and all(item.path != plaintext_path for item in held):
            try: held.append(HeldFile.open(plaintext_path))
            except BaseException: pass
        cleanup_complete = scope_settled
        for item in reversed(held):
            if not item.wipe_close(): cleanup_complete = False
        try: credentials.rmdir()
        except OSError: cleanup_complete = False
        if isinstance(env, dict): env.pop(ADMISSION_SECRET, None); env.pop(KEY_SECRET, None)
        os.environ.pop(ADMISSION_SECRET, None); os.environ.pop(KEY_SECRET, None)
        key = b""  # noqa: F841
    if not cleanup_complete or credentials.exists():
        raise AcquisitionRefusal(failure_callsite, original or Refusal("source-cleanup-refused"), False) from original
    if isinstance(original, Refusal): raise original
    if original is not None: raise AcquisitionRefusal(failure_callsite, original, True) from original
    assert receipt is not None
    _write_new(private / "source-acquisition.json", json.dumps(receipt, sort_keys=True, separators=(",", ":")).encode() + b"\n")
    return source

def git_inventory(source: pathlib.Path, effects: Effects, env: Mapping[str, str], capture: pathlib.Path) -> str:
    program = r'''import hashlib,subprocess,sys
raw=subprocess.run(["/usr/bin/git","ls-tree","-rz","--full-tree","HEAD"],check=True,stdout=subprocess.PIPE).stdout
objects=[]
for entry in raw.split(b"\0"):
    if not entry: continue
    meta,name=entry.split(b"\t",1); mode,kind,oid=meta.split(b" ")
    if mode==b"160000" or kind!=b"blob" or b"\n" in name: raise SystemExit(2)
    objects.append((name,oid))
digest=hashlib.sha256()
for name,oid in sorted(objects):
    digest.update(name); digest.update(b"\0")
    child=subprocess.run(["/usr/bin/git","cat-file","blob",oid.decode("ascii")],check=True,stdout=subprocess.PIPE)
    digest.update(child.stdout); digest.update(b"\0")
print(digest.hexdigest())'''
    result = effects.run(["/usr/bin/python3", "-c", program], cwd=source, env=env, timeout=300, capture=capture)
    try: value = result.stdout.decode("ascii", "strict").strip()
    except UnicodeDecodeError as error: raise Refusal("source-inventory-refused") from error
    if result.returncode or not HEX64.fullmatch(value): raise Refusal("source-inventory-refused")
    return value


def validate_checkout(source: pathlib.Path, effects: Effects, env: Mapping[str, str], capture: pathlib.Path) -> None:
    status = effects.run(["/usr/bin/git", "status", "--porcelain", "--untracked-files=all"], cwd=source, env=env,
                         timeout=60, capture=capture / "status.json")
    if status.returncode or status.stdout:
        raise Refusal("source-cleanliness-refused")
    listing = effects.run(["/usr/bin/git", "ls-files", "-sz"], cwd=source, env=env,
                          timeout=60, capture=capture / "index.json")
    if listing.returncode or len(listing.stdout) >= MAX_CAPTURE:
        raise Refusal("source-index-refused")
    root = source.resolve()
    for record in listing.stdout.split(b"\0"):
        if not record: continue
        meta, raw_name = record.split(b"\t", 1)
        mode = meta.split(b" ", 1)[0]
        name = os.fsdecode(raw_name)
        path = source / name
        if mode == b"160000" or not path.exists() and not path.is_symlink():
            raise Refusal("source-index-refused")
        if mode == b"120000":
            resolved = path.resolve(strict=False)
            if resolved != root and root not in resolved.parents:
                raise Refusal("source-symlink-refused")
        elif path.is_file():
            with path.open("rb") as stream:
                if stream.read(128).startswith(b"version https://git-lfs.github.com/spec/v1"):
                    raise Refusal("source-lfs-refused")


def _require_success(result: RunResult, code: str) -> None:
    if result.returncode:
        raise Refusal(code)


def require_owned_file(path: pathlib.Path, code: str) -> None:
    value = path.lstat()
    if (not path.is_file() or path.is_symlink() or value.st_uid != os.getuid()
            or value.st_nlink != 1 or value.st_mode & 0o777 != 0o600):
        raise Refusal(code)


def remove_owned_root(path: pathlib.Path, identity: tuple[int, int] | None) -> bool:
    if identity is None: return True
    try:
        value = path.lstat()
        if path.is_symlink() or value.st_uid != os.getuid() or (value.st_dev, value.st_ino) != identity: return False
        shutil.rmtree(path); return not path.exists()
    except OSError: return False


def archive_owned_tree(source: pathlib.Path, output_path: pathlib.Path) -> None:
    root = source.resolve()
    entries: list[pathlib.Path] = []
    for item in source.rglob("*"):
        if len(entries) >= 4096: raise Refusal("native-journal-overflow")
        entries.append(item)
    entries.sort(key=lambda item: item.relative_to(source).as_posix())
    total = 0
    for item in entries:
        value = item.lstat()
        if item.is_symlink() or value.st_uid != os.getuid() or not (item.is_dir() or item.is_file()):
            raise Refusal("native-journal-refused")
        if item.resolve(strict=False) != root and root not in item.resolve(strict=False).parents:
            raise Refusal("native-journal-refused")
        if item.is_file(): total += value.st_size
    if total > 8 * 1024**2: raise Refusal("native-journal-overflow")
    with tarfile.open(output_path, "w") as archive:
        archive.add(source, arcname="journal-v1", recursive=False)
        for item in entries:
            archive.add(item, arcname="journal-v1/" + item.relative_to(source).as_posix(), recursive=False)
    os.chmod(output_path, 0o600)


def load_owned_json(path: pathlib.Path, limit: int = 1024 * 1024) -> dict[str, object]:
    require_owned_file(path, "private-json-refused")
    if path.stat().st_size > limit: raise Refusal("private-json-refused")
    try: value = json.loads(path.read_bytes())
    except json.JSONDecodeError as error: raise Refusal("private-json-refused") from error
    if not isinstance(value, dict): raise Refusal("private-json-refused")
    return value


def verify_native_archive(archive: pathlib.Path, binding: Mapping[str, object]) -> None:
    require_owned_file(archive, "native-archive-refused")
    if (not 2 <= archive.stat().st_size <= 4 * 1024**3
            or binding.get("archivePath") != str(archive)
            or binding.get("archiveSha256") != _sha(archive)
            or type(binding.get("archiveBytes")) is not int
            or binding.get("archiveBytes") != archive.stat().st_size):
        raise Refusal("native-archive-refused")


def run_private_route(source: pathlib.Path, private: pathlib.Path, setup: pathlib.Path, p2: pathlib.Path,
                      admission_value: Mapping[str, object], effects: Effects,
                      progress: dict[str, object]) -> tuple[pathlib.Path, pathlib.Path, dict[str, object]]:
    state, evidence, capture = private / "state", private / "evidence", private / "capture"
    _private_dir(state); _private_dir(evidence)
    run_id, attempt = str(admission_value["runId"]), str(admission_value["runAttempt"])
    base_env = {"PATH": "/usr/bin:/bin", "HOME": str(private / "home"), "LANG": "C.UTF-8",
                "PYTHONDONTWRITEBYTECODE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}
    for name in ("HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "RUNNER_TEMP", "RUNNER_TOOL_CACHE", "GITHUB_ENV", "GITHUB_PATH"):
        if name in os.environ: base_env[name] = os.environ[name]
    qualification_deadline = time.monotonic() + 6300
    def budget(stage_limit: int, reserve_after: float = 900) -> float:
        remaining = qualification_deadline - time.monotonic() - reserve_after
        if remaining < 1: raise Refusal("qualification-deadline-refused")
        return min(float(stage_limit), remaining)
    def native_budget(stage_limit: int, reserve_after: float = 900) -> tuple[float, float]:
        total = budget(stage_limit, reserve_after)
        reserve = (NATIVE_SUPERVISOR_GRACE + TERM_SETTLE_SECONDS + KILL_SETTLE_SECONDS
                   + WAIT_SECONDS + DRAIN_SECONDS)
        if total < reserve + 1: raise Refusal("qualification-deadline-refused")
        return total - reserve, total
    commands = [
        (["/usr/bin/python3", "tests/portable-workspace/validate-source-preparation.py"], "source-validator"),
        (["/usr/bin/python3", "tests/portable-workspace/test_native_image.py"], "source-tests"),
    ]
    for argv, name in commands:
        _require_success(effects.run(argv, cwd=source, env=base_env, timeout=budget(300), capture=capture / f"{name}.json"), f"{name}-failed")
    native = source / "eng/portable-workspace/native-image.py"
    locations = ["--source", str(source), "--expected-source", FOURD_SHA, "--state", str(state),
                 "--podman", "/usr/bin/podman", "--build-runroot", f"/tmp/f4b-{run_id}-{attempt}",
                 "--fresh-runroot", f"/tmp/f4f-{run_id}-{attempt}", "--image-tmpdir", f"/dev/shm/f4t-{run_id}-{attempt}",
                 "--git", "/usr/bin/git", "--tar", "/usr/bin/tar"]
    accepted: dict[str, object] | None = None
    route_failure: BaseException | None = None
    archive = state / "build" / f"fsgg-fourd-portable-{FOURD_SHA}.oci.tar"
    binding = state / "native-image-binding.json"
    try:
        preflight_execution, preflight_total = native_budget(900)
        preflight = effects.run(["/usr/bin/python3", str(native), "preflight", *locations], cwd=source, env=base_env,
                                timeout=preflight_execution, total_timeout=preflight_total,
                                capture=capture / "preflight.json", termination_grace=NATIVE_SUPERVISOR_GRACE)
        _require_success(preflight, "rootless-preflight-failed")
        try: preflight_value = json.loads(preflight.stdout)
        except json.JSONDecodeError as error: raise Refusal("rootless-preflight-output-refused") from error
        if (preflight_value.get("schema") != "fsgg.fourd.native-image-preflight/1"
                or preflight_value.get("sourceRevision") != FOURD_SHA
                or preflight_value.get("sourceTree") != FOURD_TREE
                or preflight_value.get("sourceInventorySha256") != FOURD_INVENTORY
                or preflight_value.get("operationUidGid32768Mapped") is not True
                or preflight_value.get("namespaceRootMapsLaunchingUser") is not True
                or preflight_value.get("noSudoOrHostFallback") is not True):
            raise Refusal("rootless-preflight-output-refused")
        progress["rootlessPreflightPassed"] = True
        for version in ("10.0.400", "10.0.401"):
            dotnet_env = dict(base_env); dotnet_env["INPUT_DOTNET-VERSION"] = version; dotnet_env["INPUT_CACHE"] = "false"
            _require_success(effects.run(["/usr/bin/node", str(setup / "dist/setup/index.js")], cwd=private, env=dotnet_env, timeout=budget(900), capture=capture / f"setup-dotnet-{version}.json"), "sdk-install-failed")
        project = p2 / "src/FS.GG.Coordination.Orchestration.Execution/FS.GG.Coordination.Orchestration.Execution.fsproj"
        version = effects.run(["/usr/bin/dotnet", "--version"], cwd=p2, env=base_env, timeout=budget(30), capture=capture / "p2-sdk-version.json")
        if version.returncode or version.stdout.decode("ascii", "strict").strip() != "10.0.400": raise Refusal("p2-sdk-version-refused")
        for argv, name in ((["/usr/bin/dotnet", "restore", str(project), "--locked-mode"], "p2-restore"),
                           (["/usr/bin/dotnet", "build", str(project), "--configuration", "Release", "--no-restore", "--warnaserror"], "p2-build")):
            _require_success(effects.run(argv, cwd=p2, env=base_env, timeout=budget(900), capture=capture / f"{name}.json"), f"{name}-failed")
        fresh_execution, fresh_total = native_budget(5400)
        _require_success(effects.run(["/usr/bin/python3", str(native), "fresh-load", *locations, "--timeout", "4200"],
                                     cwd=source, env=base_env, timeout=fresh_execution, total_timeout=fresh_total,
                                     capture=capture / "fresh-load.json", termination_grace=NATIVE_SUPERVISOR_GRACE),
                         "fresh-load-failed")
        dll = p2 / "src/FS.GG.Coordination.Orchestration.Execution/bin/Release/net10.0/FS.GG.Coordination.Orchestration.Execution.dll"
        akka = private / "home/.nuget/packages/akka/1.5.71/lib/net6.0/Akka.dll"
        p2_result = evidence / "p2-result.json"
        fsi = ["/usr/bin/dotnet", "fsi", f"--reference:{akka}", f"--reference:{dll}", str(source / "eng/portable-workspace/native-p2-qualification.fsx"), "--",
               "--source", str(source), "--image-binding", str(binding), "--state-dir", str(state / "executor"), "--evidence", str(p2_result),
               "--executor-dll", str(dll), "--podman", "/usr/bin/podman", "--git", "/usr/bin/git", "--tar", "/usr/bin/tar"]
        _require_success(effects.run(fsi, cwd=source, env=base_env, timeout=budget(1200), capture=capture / "p2.json"), "native-p2-failed")
        accepted = validate_native(p2_result, binding)
    except BaseException as error:
        route_failure = error
    finally:
        snapshot_failure: BaseException | None = None
        try:
            journal = state / "executor/executor/journal-v1"
            if journal.is_dir():
                archive_owned_tree(journal, evidence / "p2-journal.tar")
        except BaseException as error:
            snapshot_failure = error
        cleanup_execution, cleanup_total = native_budget(900, 0)
        cleanup = effects.run(["/usr/bin/python3", str(native), "cleanup", "--state", str(state), "--podman", "/usr/bin/podman",
                               "--build-runroot", f"/tmp/f4b-{run_id}-{attempt}", "--fresh-runroot", f"/tmp/f4f-{run_id}-{attempt}",
                               "--image-tmpdir", f"/dev/shm/f4t-{run_id}-{attempt}"],
                              cwd=source, env=base_env, timeout=cleanup_execution, total_timeout=cleanup_total,
                              capture=capture / "cleanup.json", allow_cancelled=True,
                              termination_grace=NATIVE_SUPERVISOR_GRACE)
        if cleanup.returncode: raise Refusal("native-cleanup-failed") from route_failure
        try: cleanup_value = json.loads(cleanup.stdout)
        except json.JSONDecodeError as error: raise Refusal("native-cleanup-failed") from error
        if cleanup_value.get("schema") != "fsgg.fourd.native-cleanup/1" or cleanup_value.get("complete") is not True:
            raise Refusal("native-cleanup-failed")
        progress["cleanupComplete"] = True
        if route_failure is None and snapshot_failure is not None: route_failure = snapshot_failure
    if route_failure is not None: raise route_failure
    assert accepted is not None
    if not effects.settle(): raise Refusal("child-scope-refused")
    verify_native_archive(archive, accepted)
    evidence_tar = private / "native-evidence.tar"
    retained = [("evidence", evidence), ("capture", capture)]
    total_retained = 0
    for _label, directory in retained:
        items: list[pathlib.Path] = []
        for item in directory.iterdir():
            if len(items) >= 128: raise Refusal("native-evidence-overflow")
            items.append(item)
        for item in items:
            if not item.is_file() or item.is_symlink() or item.stat().st_uid != os.getuid() or item.stat().st_nlink != 1:
                raise Refusal("native-evidence-refused")
            total_retained += item.stat().st_size
    if total_retained > 8 * 1024**2: raise Refusal("native-evidence-overflow")
    with tarfile.open(evidence_tar, "w") as output:
        for label, directory in retained:
            for item in sorted(directory.iterdir()):
                output.add(item, arcname=f"{label}/{item.name}", recursive=False)
        receipts = ((binding, "native/native-image-binding.json"),
                    (state / "preflight.json", "native/preflight.json"),
                    (state / "cleanup.json", "native/cleanup.json"),
                    (state / "build/source-preparation.json", "native/source-preparation.json"))
        for receipt, name in receipts:
            require_owned_file(receipt, "native-evidence-refused")
            if receipt.stat().st_size > 1024 * 1024: raise Refusal("native-evidence-overflow")
            output.add(receipt, arcname=name, recursive=False)
    os.chmod(evidence_tar, 0o600)
    if evidence_tar.stat().st_size > 16 * 1024**2: raise Refusal("native-evidence-overflow")
    require_owned_file(evidence_tar, "native-evidence-refused")
    return archive, evidence_tar, accepted


def validate_native(p2_path: pathlib.Path, binding_path: pathlib.Path) -> dict[str, object]:
    p2, image = load_owned_json(p2_path), load_owned_json(binding_path)
    digest = re.compile(r"sha256:[0-9a-f]{64}\Z")
    identities = (image.get("imageReference"), image.get("manifestDigest"), image.get("buildConfigId"),
                  image.get("configDigest"), image.get("imageId"))
    if (image.get("schema") != "fsgg.fourd.native-image-binding/1"
            or image.get("qualified") is not False
            or image.get("sourceRevision") != FOURD_SHA or image.get("sourceTree") != FOURD_TREE
            or image.get("sourceInventorySha256") != FOURD_INVENTORY
            or not all(isinstance(value, str) and value for value in identities)
            or not digest.fullmatch(str(image["manifestDigest"]))
            or not all(digest.fullmatch(str(image[name])) for name in ("buildConfigId", "configDigest", "imageId"))
            or not str(image["imageReference"]).endswith("@" + str(image["manifestDigest"]))
            or not isinstance(image.get("archiveSha256"), str) or not HEX64.fullmatch(str(image["archiveSha256"]))
            or type(image.get("archiveBytes")) is not int or not 2 <= int(image["archiveBytes"]) <= 4 * 1024**3):
        raise Refusal("native-acceptance-refused")
    prelaunch = p2.get("prelaunchRefusals")
    checks = [p2.get("schema") == "fsgg.fourd.native-p2-qualification/1", p2.get("qualified") is True,
              p2.get("outcome") == "passed", p2.get("sourceRevision") == image.get("sourceRevision") == FOURD_SHA,
              p2.get("sourceTree") == image.get("sourceTree") == FOURD_TREE,
              p2.get("imageReference") == image.get("imageReference"), p2.get("manifestDigest") == image.get("manifestDigest"),
              image.get("buildConfigId") == image.get("configDigest") == image.get("imageId"),
              image.get("temporaryMountNamespaceExited") is True, image.get("postNamespaceIdentityVerified") is True,
              p2.get("executionStarted") is True, p2.get("cleanupCompleted") is True,
              p2.get("duplicateSuppressedAfterReconstruction") is True, p2.get("settledRecoveryWithoutRelaunch") is True,
              type(p2.get("remainingExecutionRoots")) is int and p2.get("remainingExecutionRoots") == 0,
              isinstance(prelaunch, list) and all(isinstance(item, str) for item in prelaunch)
              and set(prelaunch) == {"source", "scope", "operation"} and len(prelaunch) == 3]
    if not all(checks): raise Refusal("native-acceptance-refused")
    return image


def settle_typed_failure(*, runner: Effects, public_source: pathlib.Path, work: pathlib.Path,
                         state: Mapping[str, object] | None, cleanup_complete: bool,
                         closed_resources: set[str] | None = None) -> bool:
    if state is None:
        return cleanup_complete
    try:
        current=dict(state)
        if current.get("phase") not in {"cleanup","finished","cleanup-failed"}:
            current=typed_module().transition(runner=runner,source_root=public_source,work=work,name="failure-cleanup",
                observation={"kind":"begin-cleanup","cancelled":bool(runner.cancelled)},state=current)
        observed_closed = set(current.get("owned",())) if cleanup_complete and closed_resources is None else set(closed_resources or ())
        if current.get("phase")=="cleanup":
            for resource in tuple(current.get("owned",())):
                if resource in observed_closed and resource not in set(current.get("closed",())):
                    current=typed_module().transition(runner=runner,source_root=public_source,work=work,
                        name="failure-close-"+str(resource),observation={"kind":"close","resource":resource},state=current)
        if current.get("phase")=="cleanup":
            current=typed_module().transition(runner=runner,source_root=public_source,work=work,name="failure-finish",
                observation={"kind":"finish"},state=current)
        return cleanup_complete and current.get("cleanupComplete") is True
    except Exception:
        return False


def project_typed_cancellation(*, runner: Effects, public_source: pathlib.Path, work: pathlib.Path,
                               state: Mapping[str, object], name: str) -> dict[str, object]:
    if runner.cancelled and state.get("cancelled") is not True:
        return typed_module().transition(runner=runner, source_root=public_source, work=work, name=name,
                                         observation={"kind":"cancel"}, state=state)
    return dict(state)


def acquire(args: argparse.Namespace, environ: dict[str, str] | None = None, effects: Effects | None = None) -> int:
    """Run the only process that receives the source-recipient private key."""
    env = environ if environ is not None else dict(os.environ)
    output = pathlib.Path(args.output); public = output.parent
    public.mkdir(mode=0o700, parents=True, exist_ok=False)
    result = base_result(env, "failed"); runner = effects or Effects(); private = pathlib.Path(args.private_root)
    private_identity: tuple[int, int] | None = None; prepared = False
    typed_state: dict[str, object] | None = None; typed_work: pathlib.Path | None = None
    failure_callsite = "capacity"; public_source = pathlib.Path(args.public_source)
    old_handlers = {sig: signal.getsignal(sig) for sig in (signal.SIGINT, signal.SIGTERM)}
    for sig in old_handlers: signal.signal(sig, runner.request_cancel)
    try:
        verify_capacity(pathlib.Path(args.capacity_result)); result["capacityPassed"] = True
        failure_callsite = "public-placement"
        capture = public / "placement-capture"; _private_dir(capture)
        placement_sha, placement_tree = verify_public_checkout(public_source, env, runner, capture)
        shutil.rmtree(capture); result.update(placementSha=placement_sha, placementTree=placement_tree)
        failure_callsite = "public-tools"
        verify_public_tools(public_source, pathlib.Path(args.setup_dotnet), pathlib.Path(args.p2_source))
        failure_callsite = "admission"
        admitted, key = admission(env)
        if admitted is None:
            result["outcome"] = "awaiting-exact-admission"; write_result(output, result); return 0
        verify_fixed_tools(runner=runner, public_source=public_source, public=public)
        _private_dir(private); private_stat = private.lstat(); private_identity = (private_stat.st_dev, private_stat.st_ino)
        (private / "home").mkdir(mode=0o700)
        typed_work = public / "typed-acquisition"; _private_dir(typed_work)
        failure_callsite = "typed-admission"
        try:
            typed_state = typed_module().admit(runner=runner, source_root=public_source, work=typed_work,
                                               admission=admitted, placement_sha=placement_sha,
                                               run_id=str(admitted["runId"]), run_attempt=str(admitted["runAttempt"]),
                                               source_sha=FOURD_SHA, source_tree=FOURD_TREE,
                                               inventory_sha256=FOURD_INVENTORY)
            typed_state = typed_module().transition(
                runner=runner, source_root=public_source, work=typed_work, name="begin-source-effect",
                observation={"kind":"begin-effect", "resource":"source-acquisition", "acknowledged":False}, state=typed_state)
        except Exception as error:
            raise Refusal("typed-policy-admission-refused") from error
        failure_callsite = "source-recipient"
        source = acquire_source(key or b"", admitted, private, pathlib.Path(args.known_hosts), runner, env)
        failure_callsite = "source-validation"
        safe_env = {"PATH":"/usr/bin:/bin", "HOME":str(private / "home"), "LANG":"C.UTF-8"}
        validate_checkout(source, runner, safe_env, private / "capture")
        if git_inventory(source, runner, safe_env, private / "capture/inventory.json") != FOURD_INVENTORY:
            raise Refusal("source-binding-refused")
        if not runner.settle(): raise Refusal("child-scope-refused")
        try:
            typed_state = typed_module().transition(runner=runner, source_root=public_source, work=typed_work,
                name="observe-source-success", observation={"kind":"observe-success"}, state=typed_state)
            typed_state = typed_module().transition(runner=runner, source_root=public_source, work=typed_work,
                name="begin-source-cleanup", observation={"kind":"begin-cleanup", "cancelled":bool(runner.cancelled)}, state=typed_state)
            for resource,name in (("source-join","close-source-join"),("source-acquisition","close-source-acquisition")):
                typed_state=typed_module().transition(runner=runner,source_root=public_source,work=typed_work,name=name,
                    observation={"kind":"close","resource":resource},state=typed_state)
                typed_state=project_typed_cancellation(runner=runner,public_source=public_source,work=typed_work,
                    state=typed_state,name=name+"-cancel")
            typed_state=typed_module().transition(runner=runner,source_root=public_source,work=typed_work,name="finish-source",
                observation={"kind":"finish"},state=typed_state)
            typed_state=project_typed_cancellation(runner=runner,public_source=public_source,work=typed_work,
                state=typed_state,name="finish-source-cancel")
        except Exception as error:
            raise Refusal("typed-policy-cleanup-refused") from error
        if runner.cancelled or typed_state.get("successful") is not True or typed_state.get("cleanupComplete") is not True:
            raise Refusal("typed-policy-cleanup-refused")
        _write_new(private / "typed-operation-state.json",
                   json.dumps(typed_state, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n")
        _write_new(private / "admitted-route.json",
                   json.dumps(admitted, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n")
        prepared = True
        shutil.rmtree(typed_work); typed_work = None
        return 0
    except Exception as error:
        cleanup_ok = remove_owned_root(private, private_identity)
        if isinstance(error, AcquisitionRefusal): cleanup_ok = cleanup_ok and error.cleanup_complete
        if typed_work is not None:
            cleanup_ok=settle_typed_failure(runner=runner,public_source=public_source,work=typed_work,
                                            state=typed_state,cleanup_complete=cleanup_ok)
        if typed_work is not None: shutil.rmtree(typed_work, ignore_errors=True)
        code, _observed_cleanup = project_failure(runner=runner, public_source=public_source, public=public,
            name="acquire", error=error, callsite=failure_callsite, cleanup_complete=cleanup_ok)
        original = error.original if isinstance(error, AcquisitionRefusal) else error
        result["outcome"] = "refused" if isinstance(original, Refusal) else "failed"
        result["failureCode"] = code
        write_result(output, result); return 2
    finally:
        env.pop(ADMISSION_SECRET, None); env.pop(KEY_SECRET, None)
        os.environ.pop(ADMISSION_SECRET, None); os.environ.pop(KEY_SECRET, None)
        settled = runner.settle()
        if not settled and prepared: remove_owned_root(private, private_identity)
        for sig, handler in old_handlers.items(): signal.signal(sig, handler)


def load_prepared(private: pathlib.Path, env: Mapping[str, str]) -> tuple[dict[str, object], pathlib.Path]:
    if env.get(ADMISSION_SECRET) or env.get(KEY_SECRET): raise Refusal("native-secret-boundary-refused")
    admission_path, receipt_path, source = private / "admitted-route.json", private / "source-acquisition.json", private / "source"
    require_owned_file(admission_path, "source-receipt-refused"); require_owned_file(receipt_path, "source-receipt-refused")
    admitted, receipt = json.loads(admission_path.read_bytes()), json.loads(receipt_path.read_bytes())
    if (not isinstance(admitted, dict) or set(admitted) != ADMISSION_KEYS or admitted.get("schema") != ADMISSION_SCHEMA
            or admitted.get("placementSha") != env.get("GITHUB_SHA") or admitted.get("runId") != env.get("GITHUB_RUN_ID")
            or admitted.get("runAttempt") != env.get("GITHUB_RUN_ATTEMPT")):
        raise Refusal("source-receipt-refused")
    fixed = {"repository":EXPECTED_REPOSITORY, "repositoryId":EXPECTED_REPOSITORY_ID,
             "environment":EXPECTED_ENVIRONMENT, "workflowPath":EXPECTED_WORKFLOW,
             "workflowRef":EXPECTED_WORKFLOW_REF, "phase":"qualification",
             "operationId":"fourd-portable-technical", "fourdRepository":FOURD_REPOSITORY,
             "fourdRepositoryId":FOURD_REPOSITORY_ID, "fourdSourceSha":FOURD_SHA,
             "fourdSourceTree":FOURD_TREE, "fourdInventorySha256":FOURD_INVENTORY,
             "p2SourceSha":P2_SHA, "p2SourceTree":P2_TREE, "sealerRecipeSha":SEALER_RECIPE_SHA,
             "sealerSha256":SEALER_SHA256, "custodyPublicKeySha256":PUBLIC_KEY_SHA256,
             "nativePolicySha256":NATIVE_POLICY_SHA256, "custodyPolicySha256":CUSTODY_POLICY_SHA256}
    if any(admitted.get(key) != value for key,value in fixed.items()): raise Refusal("source-receipt-refused")
    issued,expires=_instant(admitted.get("issuedAt")),_instant(admitted.get("expiresAt")); current=dt.datetime.now(dt.timezone.utc)
    if expires<=issued or expires-issued>dt.timedelta(minutes=45) or current<issued or current>=expires:
        raise Refusal("source-receipt-refused")
    expected_receipt = {"schema":"fsgg.fourd.source-acquisition-receipt/1", "sourceSha":FOURD_SHA,
        "sourceTree":FOURD_TREE, "inventorySha256":FOURD_INVENTORY,
        "descriptorSha256":admitted["sourceCapsule"]["descriptorSha256"],
        "ciphertextSha256":admitted["sourceCapsule"]["ciphertextSha256"],
        "assetId":admitted["sourceCapsule"]["assetId"], "preparationComplete":True, "sourceKeyAbsent":True}
    if receipt != expected_receipt or not source.is_dir(): raise Refusal("source-receipt-refused")
    return admitted, source


def execute(args: argparse.Namespace, environ: dict[str, str] | None = None, effects: Effects | None = None) -> int:
    env = environ if environ is not None else dict(os.environ)
    output = pathlib.Path(args.output); public = output.parent
    if not public.exists(): public.mkdir(mode=0o700, parents=True, exist_ok=False)
    if public.is_symlink() or public.stat().st_uid != os.getuid() or public.stat().st_mode & 0o777 != 0o700:
        return 2
    result = base_result(env, "failed"); runner = effects or Effects(); private: pathlib.Path | None = None
    typed_state: dict[str, object] | None = None; typed_work: pathlib.Path | None = None
    old_handlers = {sig: signal.getsignal(sig) for sig in (signal.SIGINT, signal.SIGTERM)}
    for sig in old_handlers: signal.signal(sig, runner.request_cancel)
    try:
        verify_capacity(pathlib.Path(args.capacity_result)); result["capacityPassed"] = True
        public_source = pathlib.Path(args.public_source)
        public_capture = public / "placement-capture"
        _private_dir(public_capture)
        placement_sha, placement_tree = verify_public_checkout(public_source, env, runner, public_capture)
        shutil.rmtree(public_capture)
        result["placementSha"] = placement_sha; result["placementTree"] = placement_tree
        known_hosts = pathlib.Path(args.known_hosts)
        fixed_known_hosts = public_source / "eng/fourd-public-provider/github-known-hosts"
        if known_hosts.resolve() != fixed_known_hosts.resolve(): raise Refusal("known-host-refused")
        verify_public_tools(public_source, pathlib.Path(args.setup_dotnet), pathlib.Path(args.p2_source))
        private = pathlib.Path(args.private_root)
        admitted, source = load_prepared(private, env)
        safe_env = {"PATH": "/usr/bin:/bin", "HOME": str(private / "home"), "LANG": "C.UTF-8"}
        head_result = runner.run(["/usr/bin/git", "rev-parse", "HEAD"], cwd=source, env=safe_env, timeout=30,
                                 capture=private / "capture/head.json")
        tree_result = runner.run(["/usr/bin/git", "rev-parse", "HEAD^{tree}"], cwd=source, env=safe_env, timeout=30,
                                 capture=private / "capture/tree.json")
        if head_result.returncode or tree_result.returncode: raise Refusal("source-binding-refused")
        head = head_result.stdout.decode("ascii", "strict").strip()
        tree = tree_result.stdout.decode("ascii", "strict").strip()
        validate_checkout(source, runner, safe_env, private / "capture")
        if (head, tree) != (FOURD_SHA, FOURD_TREE) or git_inventory(source, runner, safe_env, private / "capture/inventory.json") != FOURD_INVENTORY:
            raise Refusal("source-binding-refused")
        private_inventory = runner.run(["/usr/bin/python3", "eng/portable-workspace/source-inventory.py", "--git", str(source), "--revision", "HEAD"],
                                       cwd=source, env=safe_env, timeout=300, capture=private / "capture/private-inventory.json")
        if private_inventory.returncode or private_inventory.stdout.decode("ascii", "strict").strip() != FOURD_INVENTORY:
            raise Refusal("source-private-inventory-refused")
        try:
            typed_work=private/"typed-native";_private_dir(typed_work)
            typed_state=typed_module().admit(runner=runner,source_root=public_source,work=typed_work,
                admission=admitted,placement_sha=placement_sha,run_id=str(admitted["runId"]),
                run_attempt=str(admitted["runAttempt"]),source_sha=head,source_tree=tree,
                inventory_sha256=FOURD_INVENTORY)
            typed_state = typed_module().transition(
                runner=runner, source_root=public_source, work=typed_work, name="begin-effect",
                observation={"kind":"begin-effect", "resource":"native-route", "acknowledged":False, "cost":1},
                state=typed_state)
        except Exception as error:
            raise Refusal("typed-policy-effect-refused") from error
        archive, evidence, binding = run_private_route(source, private, pathlib.Path(args.setup_dotnet), pathlib.Path(args.p2_source), admitted, runner, result)
        try:
            typed_state = typed_module().transition(
                runner=runner, source_root=public_source, work=typed_work, name="observe-success",
                observation={"kind":"observe-success", "cost":1}, state=typed_state)
        except Exception as error:
            raise Refusal("typed-policy-outcome-refused") from error
        if typed_state.get("outcome") != "success" or typed_state.get("effectAcknowledged") is not True:
            raise Refusal("typed-policy-outcome-refused")
        result["nativeAccepted"] = True
        if not runner.settle(): raise Refusal("child-scope-refused")
        custody_path = public_source / "eng/fourd-public-provider/custody.py"
        if _sha(custody_path) != CUSTODY_POLICY_SHA256: raise Refusal("custody-module-refused")
        try:
            from custody import seal_series, verify_staging  # type: ignore
        except ImportError as error:
            raise Refusal("custody-module-refused") from error
        identity = {"schema": "fsgg.fourd.custody-identity/1", "runId": admitted["runId"], "runAttempt": admitted["runAttempt"],
                    "runNonce": admitted["runNonce"], "placementSha": placement_sha, "fourdSourceSha": FOURD_SHA,
                    "fourdSourceTree": FOURD_TREE, "fourdInventorySha256": FOURD_INVENTORY, "p2SourceSha": P2_SHA,
                    "p2SourceTree": P2_TREE, "nativePolicySha256": NATIVE_POLICY_SHA256, "sealerSha256": SEALER_SHA256,
                    "publicKeySha256": PUBLIC_KEY_SHA256, "nativeBindingSha256": _sha(private / "state/native-image-binding.json"),
                    "nativeEvidenceSha256": _sha(evidence)}
        private_sealed = private / "sealed-stage"
        custody = seal_series(archive=archive, evidence=evidence, scratch=private / "seal-scratch", output=private_sealed,
                              identity=identity, deadline_monotonic=time.monotonic() + 1200)
        verified = verify_staging(private_sealed, identity)
        if custody != verified or not verified.archiveComplete: raise Refusal("custody-verification-refused")
        manifest = load_owned_json(private_sealed / verified.manifestName)
        archive_record = manifest.get("archive")
        if (not isinstance(archive_record, dict) or archive_record.get("sha256") != binding["archiveSha256"]
                or archive_record.get("bytes") != binding["archiveBytes"]):
            raise Refusal("custody-verification-refused")
        if not runner.settle(): raise Refusal("child-scope-refused")
        sealed = public / "sealed-stage"
        os.rename(private_sealed, sealed)
        try:
            moved = verify_staging(sealed, identity)
            if moved != verified: raise Refusal("custody-verification-refused")
        except BaseException:
            shutil.rmtree(sealed, ignore_errors=True)
            raise
        try:
            typed_state = typed_module().transition(
                runner=runner, source_root=public_source, work=typed_work, name="begin-cleanup",
                observation={"kind":"begin-cleanup", "cancelled":bool(runner.cancelled), "cost":1}, state=typed_state)
            for resource, name in (("source-join", "close-source-join"), ("native-route", "close-native-route")):
                typed_state = typed_module().transition(
                    runner=runner, source_root=public_source, work=typed_work, name=name,
                    observation={"kind":"close", "resource":resource, "cost":1}, state=typed_state)
                typed_state=project_typed_cancellation(runner=runner,public_source=public_source,work=typed_work,
                    state=typed_state,name=name+"-cancel")
            typed_state = typed_module().transition(
                runner=runner, source_root=public_source, work=typed_work, name="finish",
                observation={"kind":"finish", "cost":1}, state=typed_state)
            typed_state=project_typed_cancellation(runner=runner,public_source=public_source,work=typed_work,
                state=typed_state,name="finish-cancel")
        except Exception as error:
            shutil.rmtree(sealed, ignore_errors=True)
            raise Refusal("typed-policy-cleanup-refused") from error
        if runner.cancelled or typed_state.get("successful") is not True or typed_state.get("cleanupComplete") is not True:
            shutil.rmtree(sealed, ignore_errors=True)
            raise Refusal("typed-policy-cleanup-refused")
        result.update(outcome="sealed-native-evidence", custodyOutcome=verified.outcome, archiveComplete=True,
                      manifestSha256=verified.manifestSha256, qualified=True)
        return 0
    except Refusal as error:
        local_settled=runner.settle()
        native_cleaned=result.get("cleanupComplete") is True
        observed_closed={"source-join"} if local_settled else set()
        if local_settled and native_cleaned: observed_closed.add("native-route")
        typed_cleanup=local_settled and native_cleaned
        if typed_work is not None:
            typed_cleanup=settle_typed_failure(runner=runner,public_source=pathlib.Path(args.public_source),work=typed_work,
                                               state=typed_state,cleanup_complete=typed_cleanup,
                                               closed_resources=observed_closed)
        result["failureCode"] = str(error) if str(error) in {
            "admission-missing", "admission-partial", "admission-binding-refused", "admission-time-refused",
            "capacity-refused", "public-tool-source-refused", "known-host-refused", "rootless-preflight-failed",
            "source-acquisition-refused", "source-binding-refused", "native-acceptance-refused", "native-cleanup-failed",
            "custody-module-refused", "custody-verification-refused", "cancelled"} else "qualification-refused"
        if not typed_cleanup and result["failureCode"] != "native-cleanup-failed": result["failureCode"]="cleanup-refused"
        result["outcome"] = "refused" if not result["rootlessPreflightPassed"] else "failed"
        return 2
    except Exception:
        local_settled=runner.settle()
        native_cleaned=result.get("cleanupComplete") is True
        observed_closed={"source-join"} if local_settled else set()
        if local_settled and native_cleaned: observed_closed.add("native-route")
        typed_cleanup=local_settled and native_cleaned
        if typed_work is not None:
            typed_cleanup=settle_typed_failure(runner=runner,public_source=pathlib.Path(args.public_source),work=typed_work,
                                               state=typed_state,cleanup_complete=typed_cleanup,
                                               closed_resources=observed_closed)
        result["failureCode"] = "qualification-refused"
        if not typed_cleanup: result["failureCode"]="cleanup-refused"
        result["outcome"] = "failed"
        return 2
    finally:
        settled = runner.settle()
        if not settled: result["cleanupComplete"] = False; result["qualified"] = False; result["failureCode"] = "cleanup-refused"
        for sig, handler in old_handlers.items(): signal.signal(sig, handler)
        write_result(output, result)


def verify_upload(root: pathlib.Path, public_source: pathlib.Path, environ: Mapping[str, str],
                  effects: Effects | None = None) -> int:
    validation_capture = root.parent / (root.name + "-validation")
    try:
        runner = effects or Effects()
        _private_dir(validation_capture)
        placement_sha, placement_tree = verify_public_checkout(public_source, environ, runner, validation_capture)
        shutil.rmtree(validation_capture)
        custody_path = public_source / "eng/fourd-public-provider/custody.py"
        if _sha(custody_path) != CUSTODY_POLICY_SHA256: raise Refusal("custody-module-refused")
        result_path = root / "result.json"
        require_owned_file(result_path, "public-result-refused")
        if result_path.stat().st_size > MAX_RESULT: raise Refusal("public-result-refused")
        result = json.loads(result_path.read_bytes())
        if (set(result) != set(base_result(environ, "failed")) or result.get("schema") != SCHEMA
                or result.get("runId") != environ.get("GITHUB_RUN_ID")
                or result.get("runAttempt") != environ.get("GITHUB_RUN_ATTEMPT")
                or result.get("placementSha") != placement_sha or result.get("placementTree") != placement_tree
                or result.get("rootReadback") != "pending-root-readback"
                or result.get("credentialRevocation") != "pending-root-readback"):
            raise Refusal("public-result-refused")
        qualified = result.get("qualified") is True
        expected_names = {"result.json", "sealed-stage"} if qualified else {"result.json"}
        if {item.name for item in root.iterdir()} != expected_names: raise Refusal("public-output-refused")
        if not qualified:
            if (result.get("outcome") not in {"awaiting-exact-admission", "refused", "failed"}
                    or result.get("archiveComplete") is not False or result.get("manifestSha256") is not None):
                raise Refusal("public-output-refused")
            return 0
        sealed = root / "sealed-stage"
        manifest_path = sealed / "manifest.json"
        manifest = json.loads(manifest_path.read_bytes())
        identity = manifest.get("identity")
        if not isinstance(identity, dict): raise Refusal("public-manifest-refused")
        fixed = {"schema": "fsgg.fourd.custody-identity/1", "runId": environ.get("GITHUB_RUN_ID"),
                 "runAttempt": environ.get("GITHUB_RUN_ATTEMPT"), "placementSha": placement_sha,
                 "fourdSourceSha": FOURD_SHA, "fourdSourceTree": FOURD_TREE,
                 "fourdInventorySha256": FOURD_INVENTORY, "p2SourceSha": P2_SHA, "p2SourceTree": P2_TREE,
                 "nativePolicySha256": NATIVE_POLICY_SHA256, "sealerSha256": SEALER_SHA256,
                 "publicKeySha256": PUBLIC_KEY_SHA256}
        if any(identity.get(name) != value for name, value in fixed.items()): raise Refusal("public-manifest-refused")
        if set(identity) != {"schema", "runId", "runAttempt", "runNonce", "placementSha", "fourdSourceSha",
                             "fourdSourceTree", "fourdInventorySha256", "p2SourceSha", "p2SourceTree",
                             "nativePolicySha256", "sealerSha256", "publicKeySha256", "nativeBindingSha256",
                             "nativeEvidenceSha256"}:
            raise Refusal("public-manifest-refused")
        if not NONCE.fullmatch(str(identity["runNonce"])) or not all(
                HEX64.fullmatch(str(identity[name])) for name in ("nativeBindingSha256", "nativeEvidenceSha256")):
            raise Refusal("public-manifest-refused")
        from custody import verify_staging  # type: ignore
        verified = verify_staging(sealed, identity)
        if (not verified.archiveComplete or verified.manifestSha256 != result.get("manifestSha256")
                or verified.outcome != result.get("custodyOutcome")
                or result.get("outcome") != "sealed-native-evidence" or result.get("archiveComplete") is not True
                or result.get("nativeAccepted") is not True or result.get("cleanupComplete") is not True):
            raise Refusal("public-output-refused")
        return 0 if runner.settle() else 2
    except Exception:
        return 2
    finally:
        if validation_capture.is_dir(): shutil.rmtree(validation_capture, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("acquire", "execute", "verify-upload"))
    parser.add_argument("--capacity-result"); parser.add_argument("--public-source")
    parser.add_argument("--setup-dotnet"); parser.add_argument("--p2-source")
    parser.add_argument("--known-hosts"); parser.add_argument("--private-root")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.command == "verify-upload":
        if not args.public_source: return 2
        return verify_upload(pathlib.Path(args.output), pathlib.Path(args.public_source), os.environ)
    if not all((args.capacity_result, args.public_source, args.setup_dotnet, args.p2_source, args.known_hosts, args.private_root)):
        return 2
    if args.command == "acquire":
        return acquire(args)
    return execute(args)


if __name__ == "__main__":
    raise SystemExit(main())
