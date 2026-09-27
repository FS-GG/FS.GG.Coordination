"""Source-only protected loader for the exact GS2-09.9 runtime candidate.

The host must supply the expected identity and protected authority independently
of the candidate witness. This module does not implement either authority.
"""

from __future__ import annotations

import dataclasses
import fcntl
import hashlib
import importlib
import os
import sys
import zipimport
from typing import Protocol

import callable_isolated_v2_runtime_candidate_witness as candidate
import verify_callable_isolated_v2_runtime_artifact as artifact_check


MODULES = (
    "callable_isolated_v2_retained_operator",
    "callable_isolated_v2_runtime",
    "callable_isolated_v2_runtime.adapters",
    "callable_isolated_v2_runtime.contracts",
    "callable_isolated_v2_runtime.coordinator",
    "callable_isolated_v2_runtime.grant",
)
SEALS = (fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW | fcntl.F_SEAL_SHRINK
         | fcntl.F_SEAL_SEAL)


class Refused(ValueError):
    """Fixed refusal without reflecting candidate or protected bytes."""


@dataclasses.dataclass(frozen=True)
class ExpectedRuntime:
    """Host-owned expectation, established outside the candidate producer."""

    repository: str
    repository_id: int
    source_revision: str
    source_tree: str
    workflow_sha256: str
    producer_run_id: int
    producer_run_attempt: int
    artifact_id: int
    bundle_sha256: str
    archive_sha256: str
    manifest_sha256: str
    execution_run_id: int
    execution_run_attempt: int


class ProtectedHost(Protocol):
    def expected_runtime(self) -> ExpectedRuntime: ...
    def read_candidate_bundle(self, artifact_id: int) -> bytes: ...
    def execution_run(self) -> tuple[int, int]: ...
    def protected_authority(self) -> object: ...


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _positive(value: object) -> bool:
    return type(value) is int and value > 0


def _expectation(value: object, witness: object) -> ExpectedRuntime:
    if (type(value) is not ExpectedRuntime
            or type(witness) is not candidate.CandidateWitness
            or witness.schema != candidate.RESULT_SCHEMA
            or value.repository != candidate.REPOSITORY
            or not _positive(value.repository_id)
            or not all(_positive(getattr(value, name)) for name in (
                "producer_run_id", "producer_run_attempt", "artifact_id",
                "execution_run_id", "execution_run_attempt"))
            or candidate.HEX40.fullmatch(value.source_revision) is None
            or candidate.HEX40.fullmatch(value.source_tree) is None
            or any(candidate.HEX64.fullmatch(getattr(value, name)) is None
                   for name in ("workflow_sha256", "bundle_sha256",
                                "archive_sha256", "manifest_sha256"))
            or (value.producer_run_id, value.producer_run_attempt)
               == (value.execution_run_id, value.execution_run_attempt)
            or witness.authorized is not False
            or witness.can_dispatch_effect is not False
            or type(witness.live_effects) is not int
            or witness.live_effects != 0
            or (witness.repository, witness.repository_id,
                witness.source_sha, witness.source_tree,
                witness.workflow_sha256, witness.run_id,
                witness.run_attempt, witness.artifact_id,
                witness.bundle_sha256, witness.archive_sha256,
                witness.manifest_sha256)
               != (value.repository, value.repository_id,
                   value.source_revision, value.source_tree,
                   value.workflow_sha256, value.producer_run_id,
                   value.producer_run_attempt, value.artifact_id,
                   value.bundle_sha256, value.archive_sha256,
                   value.manifest_sha256)):
        raise Refused("runtime-host-identity")
    return value


def _bundle(raw: object, expected: ExpectedRuntime) -> bytes:
    if type(raw) is not bytes or _sha(raw) != expected.bundle_sha256:
        raise Refused("runtime-host-bundle")
    try:
        archive, manifest = candidate._read_bundle(raw)
        checked = artifact_check.verify_candidate_bytes(archive, manifest)
    except (candidate.Refused, artifact_check.Refused):
        raise Refused("runtime-host-candidate") from None
    if (_sha(archive) != expected.archive_sha256
            or _sha(manifest) != expected.manifest_sha256
            or checked["sourceRevision"] != expected.source_revision
            or checked["sourceTree"] != expected.source_tree
            or checked["archiveSha256"] != expected.archive_sha256):
        raise Refused("runtime-host-source-binding")
    return archive


def _sealed_archive(archive: bytes) -> tuple[int, str]:
    try:
        fd = os.memfd_create("fsgg-runtime", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        try:
            with os.fdopen(os.dup(fd), "wb") as stream:
                stream.write(archive)
            fcntl.fcntl(fd, fcntl.F_ADD_SEALS, SEALS)
            if fcntl.fcntl(fd, fcntl.F_GET_SEALS) & SEALS != SEALS:
                raise OSError("unsealed")
            return fd, f"/proc/self/fd/{fd}"
        except BaseException:
            os.close(fd)
            raise
    except (AttributeError, OSError):
        raise Refused("runtime-host-sealed-file-unavailable") from None


def _check_file(fd: int, archive: bytes) -> None:
    try:
        os.lseek(fd, 0, os.SEEK_SET)
        with os.fdopen(os.dup(fd), "rb") as stream:
            if stream.read(len(archive) + 1) != archive:
                raise Refused("runtime-host-file-drift")
    except OSError:
        raise Refused("runtime-host-file-drift") from None


def _load(path: str):
    if any(name in sys.modules for name in MODULES):
        raise Refused("runtime-host-import-shadow")
    try:
        zipimport._zip_directory_cache.pop(path, None)
        importer = zipimport.zipimporter(path)
        if not importer.find_spec("callable_isolated_v2_runtime"):
            raise Refused("runtime-host-import-roster")
        sys.path.insert(0, path)
        runtime = importlib.import_module("callable_isolated_v2_runtime.adapters")
        importlib.import_module("callable_isolated_v2_runtime.contracts").operator_module()
        for name in MODULES:
            module = sys.modules.get(name)
            if module is None or not str(getattr(module, "__file__", "")).startswith(path + "/"):
                raise Refused("runtime-host-import-shadow")
        if not callable(runtime.execute_installed) or not callable(runtime.recover_installed):
            raise Refused("runtime-host-import-roster")
        return runtime
    except Refused:
        raise
    except Exception:
        raise Refused("runtime-host-import") from None
    finally:
        if path in sys.path:
            sys.path.remove(path)


def run(host: ProtectedHost, witness: candidate.CandidateWitness,
        *, recovery: bool = False):
    """Verify host identity and immutable archive before runtime composition.

    Candidate evidence is only an identity projection. A missing host authority
    refuses before the runtime can read a journal, mint a token or use a provider.
    """
    if host is None or type(recovery) is not bool:
        raise Refused("runtime-host-unavailable")
    try:
        expected = _expectation(host.expected_runtime(), witness)
        archive = _bundle(host.read_candidate_bundle(expected.artifact_id), expected)
        execution_run = host.execution_run()
        if (type(execution_run) is not tuple or len(execution_run) != 2
                or any(not _positive(item) for item in execution_run)
                or execution_run != (expected.execution_run_id,
                                     expected.execution_run_attempt)):
            raise Refused("runtime-host-execution-run")
        authority = host.protected_authority()
    except Refused:
        raise
    except Exception:
        raise Refused("runtime-host-unavailable") from None
    if authority is None:
        raise Refused("runtime-host-authority-unavailable")
    if any(name in sys.modules for name in MODULES):
        raise Refused("runtime-host-import-shadow")
    fd, path = _sealed_archive(archive)
    try:
        _check_file(fd, archive)
        runtime = _load(path)
        _check_file(fd, archive)
        return (runtime.recover_installed if recovery else
                runtime.execute_installed)(authority)
    finally:
        for name in reversed(MODULES):
            module = sys.modules.get(name)
            if str(getattr(module, "__file__", "")).startswith(path + "/"):
                sys.modules.pop(name, None)
        zipimport._zip_directory_cache.pop(path, None)
        os.close(fd)
