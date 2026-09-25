"""Typed v5 native-effect port proposal. No port is implemented or invoked here.

These caller-supplied shapes are review inputs, never authenticated evidence or
an execution grant. The module is deliberately outside the installed archive.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

RESULT_SCHEMA = "fsgg.coordination.callable-isolated-v2-v5-port-proposal/1"


@dataclass(frozen=True)
class ReleaseCoordinates:
    coordination_revision: str
    source_tree: str
    operator_sha256: str
    contract_sha256: str
    proposal_sha256: str
    controls_sha256: str
    archive_sha256: str
    installed_entry_path: str
    installed_entry_sha256: str
    package_id: str | None
    package_version: str | None
    package_sha256: str | None
    protected_workflow_revision: str
    protected_workflow_path: str
    protected_workflow_sha256: str
    producer_run_id: int
    producer_run_attempt: int
    artifact_id: int
    reviewer_event_id: int
    runner_image_sha256: str
    runner_attestation_sha256: str
    interpreter_sha256: str
    runtime_closure_sha256: str


@dataclass(frozen=True)
class NativeCoordinates:
    operation_identity: str
    operation_id: str
    canonical_request: bytes
    request_sha256: str
    method: str
    path: str
    max_provider_writes: int
    repository: str
    repository_id: int
    repository_node_id: str
    installation_id: int
    source_ref: str
    source_sha: str
    base_ref: str
    base_sha: str
    prestate_sha256: str
    setup_receipt_sha256: str
    run_id: int
    run_attempt: int
    environment_id: int
    dispatch_actor_id: int
    reviewer_actor_id: int
    approval_event_id: int
    issuer_actor_id: int
    grant_sha256: str
    grant_expires_at: str
    journal_repository: str
    journal_ref: str
    journal_path: str
    journal_prior_generation: int
    journal_prior_head: str


@dataclass(frozen=True)
class AppScopeRecord:
    installation_id: int
    repository_ids: tuple[int, ...]
    permissions: tuple[str, ...]
    issued_at: str
    expires_at: str
    credential_id: str  # Public identifier only; never the token.


@dataclass(frozen=True)
class JournalRecord:
    operation_id: str
    grant_sha256: str
    target_sha256: str
    request_sha256: str
    prior_generation: int
    prior_head: str
    committed_generation: int
    committed_head: str
    attempt_may_have_started: bool


@dataclass(frozen=True)
class ApprovalRecord:
    run_id: int
    run_attempt: int
    environment_id: int
    dispatch_actor_id: int
    reviewer_actor_id: int
    approval_event_id: int
    reviewed_at: str
    expires_at: str


@dataclass(frozen=True)
class GrantRecord:
    artifact_id: int
    artifact_sha256: str
    payload_sha256: str
    run_id: int
    run_attempt: int
    approval_event_id: int
    issuer_actor_id: int
    expires_at: str


@dataclass(frozen=True)
class TargetPrestateRecord:
    repository: str
    repository_id: int
    repository_node_id: str
    installation_id: int
    source_ref: str
    source_sha: str
    base_ref: str
    base_sha: str
    absent_census_sha256: str
    policy_sha256: str
    observed_at: str


@dataclass(frozen=True)
class NativeResultRecord:
    repository_id: int
    response_status: int | None
    provider_post_count: int
    first_snapshot_sha256: str
    second_snapshot_sha256: str
    complete: bool
    classification: str


class SourceReadPort(Protocol):
    def read_release(self) -> ReleaseCoordinates: ...


class ApprovalReadPort(Protocol):
    def read_approval(self, event_id: int) -> ApprovalRecord: ...


class GrantReadPort(Protocol):
    def read_grant(self, artifact_id: int) -> GrantRecord: ...


class IssuerReadPort(Protocol):
    def read_issuance(self, artifact_id: int) -> GrantRecord: ...


class TargetReadPort(Protocol):
    def read_prestate(self, repository_id: int) -> TargetPrestateRecord: ...


class AppScopeReadPort(Protocol):
    def read_effective_scope(self, installation_id: int) -> AppScopeRecord: ...


class JournalReplayReadPort(Protocol):
    def read_committed(self, operation_id: str) -> JournalRecord: ...


class NativeReadbackPort(Protocol):
    def read_complete_poststate(self, repository_id: int) -> NativeResultRecord: ...


class JournalWritePort(Protocol):
    def reserve_attempt(self, intent: JournalRecord) -> JournalRecord: ...


class ExecutionTokenPort(Protocol):
    def read_execution_token(self) -> bytes: ...


class ProviderWritePort(Protocol):
    def post_pull(self, path: str, canonical_request: bytes) -> object: ...


@dataclass(frozen=True)
class ProposedPorts:
    source_reader: SourceReadPort
    approval_reader: ApprovalReadPort
    grant_reader: GrantReadPort
    issuer_reader: IssuerReadPort
    target_reader: TargetReadPort
    app_scope_reader: AppScopeReadPort
    journal_replay_reader: JournalReplayReadPort
    native_readback_reader: NativeReadbackPort
    journal_writer: JournalWritePort
    execution_token: ExecutionTokenPort
    provider_writer: ProviderWritePort


@dataclass(frozen=True)
class ClosedV5Decision:
    reason: str
    schema: str = RESULT_SCHEMA
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0
    exit_code: int = 78


def inspect_closed(_release: ReleaseCoordinates | None,
                   _native: NativeCoordinates | None,
                   _ports: ProposedPorts | None,
                   grant: bytes | None) -> ClosedV5Decision:
    """Refuse without inspecting grant bytes, coordinates or any proposed port."""
    if grant is None:
        return ClosedV5Decision("no-grant")
    return ClosedV5Decision("v5-protected-authority-unavailable")
