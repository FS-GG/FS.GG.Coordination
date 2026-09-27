"""Protected host adapters for the internal GS2-09.9 /5 composer.

The installed configuration is read from the protected authority.  Callers do
not supply an API origin, public key, token, target, journal path, or adapter.
The host implementation and its records remain an S3 qualification concern;
this module only defines and enforces the source boundary they must satisfy.
"""
from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
import json
import re
from typing import Protocol

from . import coordinator
from .contracts import Binding, digest
from .grant import canonical, verify


API_ORIGIN = "https://api.github.com"
CONFIG_SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-installation/2"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
ROLE_PERMISSIONS = {
    "configuration": ("runtime-configuration:read",),
    "grant": ("runtime-grant:read",),
    "issuer": ("runtime-issuer:read",),
    "key": ("runtime-active-key:read",),
    "parent": ("runtime-journal-parent:read",),
    "journal": ("runtime-journal:reserve", "runtime-journal:read", "runtime-journal:write"),
    "clock": ("trusted-time:read",),
    "native-read": ("metadata:read", "contents:read", "pull_requests:read"),
    "token": ("execution-token:issue",),
    "native-write": ("metadata:read", "pull_requests:write"),
}


class Refused(ValueError):
    """Fixed refusal without exposing protected record or credential data."""


@dataclasses.dataclass(frozen=True)
class ProtectedScope:
    authority_id: str
    role: str
    principal_id: str
    credential_id: str
    api_origin: str
    repository: str
    repository_id: int
    installation_id: int
    permissions: tuple[str, ...]
    expires_at: dt.datetime


@dataclasses.dataclass(frozen=True)
class RecordProvenance:
    authority_id: str
    role: str
    principal_id: str
    credential_id: str
    record_id: str
    observed_at: dt.datetime
    expires_at: dt.datetime


@dataclasses.dataclass(frozen=True)
class InstalledConfiguration:
    schema: str
    complete: bool
    immutable: bool
    authority_id: str
    api_origin: str
    installation_id: int
    revision: str
    source_tree: str
    source_ref: str
    base_ref: str
    journal_database_path: str  # Must be empty; retained to reject legacy local-path installs.
    binding: Binding
    expected: object
    scopes: tuple[ProtectedScope, ...]
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class GrantRecord:
    operation_id: str
    binding_sha256: str
    grant_sha256: str
    key_id: str
    issuer_actor_id: int
    run_id: int
    run_attempt: int
    raw_grant: bytes
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class IssuerRecord:
    operation_id: str
    binding_sha256: str
    grant_sha256: str
    key_id: str
    issuer_actor_id: int
    run_id: int
    run_attempt: int
    issued_at: dt.datetime
    expires_at: dt.datetime
    active: bool
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class KeyRecord:
    key_id: str
    issuer_actor_id: int
    public_key: bytes
    active: bool
    revoked_at: dt.datetime | None
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class ParentRecord:
    repository: str
    ref: str
    path: str
    generation: int
    head: str
    binding_sha256: str
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class TokenLease:
    operation_id: str
    repository: str
    repository_id: int
    installation_id: int
    token_id: str
    token: bytes = dataclasses.field(repr=False)
    issued_at: dt.datetime
    expires_at: dt.datetime
    provenance: RecordProvenance


@dataclasses.dataclass(frozen=True)
class CommittedAttempt:
    """An independently read, committed host record for one irreversible attempt."""
    operation_id: str
    repository: str
    ref: str
    path: str
    prior_generation: int
    prior_head: str
    binding_sha256: str
    grant_sha256: str
    attempt_may_have_started: bool
    outcome: object | None
    provenance: RecordProvenance


class ProtectedAuthority(Protocol):
    """One host-owned capability; an implementation is not supplied here.

    reserve_attempt must atomically compare the protected parent generation/head
    and insert a permanent may-have-started fence for both operation ID and
    parent tuple. False means a definite rejection; uncertainty must raise.
    read_committed_attempt must use an independent committed read, never a
    reservation caller's uncommitted or cached view. persist_attempt_outcome
    must durably store one immutable outcome before returning True. A failed or
    uncertain acknowledgement must retain the fence and may return False or
    raise. None of these methods may fall back to local process storage.
    """

    def scope(self, role: str) -> ProtectedScope: ...
    def read_installed_configuration(self) -> InstalledConfiguration: ...
    def read_grant(self, operation_id: str) -> GrantRecord: ...
    def read_issuer(self, operation_id: str) -> IssuerRecord: ...
    def read_active_key(self, key_id: str, issuer_actor_id: int) -> KeyRecord: ...
    def read_parent(self, repository: str, ref: str, path: str) -> ParentRecord: ...
    def reserve_attempt(self, binding: Binding, grant_sha256: str) -> bool: ...
    def read_committed_attempt(self, operation_id: str) -> CommittedAttempt | None: ...
    def persist_attempt_outcome(self, operation_id: str, binding_sha256: str,
                                grant_sha256: str, outcome: object) -> bool: ...
    def trusted_now(self) -> dt.datetime: ...
    def native_get(self, path: str): ...
    def issue_execution_token(self, operation_id: str) -> TokenLease: ...
    def native_post(self, path: str, canonical_request: bytes, token: bytes): ...


def _utc(value: object) -> bool:
    return (type(value) is dt.datetime and value.tzinfo is not None
            and value.utcoffset() == dt.timedelta(0))


def _positive(value: object) -> bool:
    return type(value) is int and value > 0


def _binding_sha(binding: Binding) -> str:
    return digest(canonical(binding.claims()))


def _valid_outcome(value: object) -> bool:
    if value is None or value == "lost":
        return True
    return (type(value) is coordinator.operator.HttpResponse
            and type(value.status) is int
            and type(value.headers) is tuple
            and all(type(pair) is tuple and len(pair) == 2
                    and all(type(item) is str for item in pair)
                    for pair in value.headers)
            and type(value.body) is bytes and len(value.body) <= 4_000_000)


def _copy_exact(value, expected_type, reason: str):
    if type(value) is not expected_type:
        raise Refused(reason)
    return copy.deepcopy(value)


class _Installation:
    def __init__(self, authority: ProtectedAuthority,
                 configuration: InstalledConfiguration):
        self.authority = authority
        self.configuration = configuration
        self.scopes = {scope.role: scope for scope in configuration.scopes}
        self.parent_provenance_expires_at: dt.datetime | None = None
        self.journal_attestation_expires_at: dt.datetime | None = None

    def scope(self, role: str, now: dt.datetime) -> ProtectedScope:
        expected = self.scopes.get(role)
        if expected is None:
            raise Refused("protected-role-missing")
        try:
            observed = self.authority.scope(role)
        except Exception:
            raise Refused("protected-scope-unavailable") from None
        if (type(observed) is not ProtectedScope or observed != expected
                or observed.authority_id != self.configuration.authority_id
                or observed.role != role
                or type(observed.principal_id) is not str
                or not observed.principal_id
                or HEX64.fullmatch(observed.credential_id or "") is None
                or observed.api_origin != API_ORIGIN
                or observed.repository != self.configuration.binding.target_repository
                or observed.repository_id != self.configuration.binding.target_repository_id
                or observed.installation_id != self.configuration.installation_id
                or observed.permissions != ROLE_PERMISSIONS[role]
                or not _utc(observed.expires_at)
                or not now < observed.expires_at <= now + dt.timedelta(minutes=30)):
            raise Refused("protected-scope-invalid")
        return copy.deepcopy(observed)

    def record(self, provenance: object, role: str,
               now: dt.datetime) -> RecordProvenance:
        scope = self.scope(role, now)
        value = _copy_exact(provenance, RecordProvenance,
                            "protected-provenance-shape")
        if (value.authority_id != self.configuration.authority_id
                or value.role != role
                or value.principal_id != scope.principal_id
                or value.credential_id != scope.credential_id
                or type(value.record_id) is not str or not value.record_id
                or not _utc(value.observed_at) or not _utc(value.expires_at)
                or not now - dt.timedelta(minutes=30) <= value.observed_at <= now
                or not now < value.expires_at <= value.observed_at + dt.timedelta(minutes=30)):
            raise Refused("protected-provenance-invalid")
        return value


class TrustedClock:
    def __init__(self, installation: _Installation):
        self.installation = installation
        self._last: dt.datetime | None = None

    def now(self) -> dt.datetime:
        try:
            value = self.installation.authority.trusted_now()
        except Exception:
            raise Refused("protected-clock-unavailable") from None
        if not _utc(value) or (self._last is not None and value < self._last):
            raise Refused("protected-clock-invalid")
        self.installation.scope("clock", value)
        self._last = value
        return value

    @property
    def current(self) -> dt.datetime:
        if self._last is None:
            raise Refused("protected-clock-unsampled")
        return self._last


def _load_installation(authority: ProtectedAuthority) -> tuple[_Installation, TrustedClock]:
    if authority is None:
        raise Refused("protected-authority-missing")
    try:
        first = authority.read_installed_configuration()
        second = authority.read_installed_configuration()
    except Exception:
        raise Refused("protected-configuration-unavailable") from None
    first = _copy_exact(first, InstalledConfiguration,
                        "protected-configuration-shape")
    if type(second) is not InstalledConfiguration or second != first:
        raise Refused("protected-configuration-drift")
    binding = first.binding
    expected = first.expected
    roles = tuple(scope.role for scope in first.scopes
                  if type(scope) is ProtectedScope)
    principals = tuple(scope.principal_id for scope in first.scopes
                       if type(scope) is ProtectedScope)
    credentials = tuple(scope.credential_id for scope in first.scopes
                        if type(scope) is ProtectedScope)
    if (first.schema != CONFIG_SCHEMA or first.complete is not True
            or first.immutable is not True
            or HEX64.fullmatch(first.authority_id or "") is None
            or first.api_origin != API_ORIGIN
            or not _positive(first.installation_id)
            or HEX40.fullmatch(first.revision or "") is None
            or HEX40.fullmatch(first.source_tree or "") is None
            or type(binding) is not Binding
            or type(expected) is not coordinator.operator.ExpectedPull
            or not coordinator._valid_binding(binding, expected)
            or binding.source_revision != first.revision
            or binding.source_tree != first.source_tree
            or expected.repository != binding.target_repository
            or expected.repository_id != binding.target_repository_id
            or expected.source_ref != first.source_ref
            or expected.base_ref != first.base_ref
            or first.journal_database_path != ""
            or len(first.scopes) != len(ROLE_PERMISSIONS)
            or set(roles) != set(ROLE_PERMISSIONS) or len(set(roles)) != len(roles)
            or len(set(principals)) != len(principals)
            or len(set(credentials)) != len(credentials)):
        raise Refused("protected-configuration-invalid")
    installation = _Installation(authority, first)
    clock = TrustedClock(installation)
    now = clock.now()
    for role in ROLE_PERMISSIONS:
        installation.scope(role, now)
    installation.record(first.provenance, "configuration", now)
    return installation, clock


class ProtectedGrantReader:
    def __init__(self, installation: _Installation, clock: TrustedClock):
        self.installation, self.clock = installation, clock
        self._last_deadline: dt.datetime | None = None

    def read(self) -> GrantRecord:
        binding = self.installation.configuration.binding
        now = self.clock.now()
        self.installation.scope("grant", now)
        try:
            value = self.installation.authority.read_grant(binding.operation_id)
        except Exception:
            raise Refused("protected-grant-unavailable") from None
        value = _copy_exact(value, GrantRecord, "protected-grant-shape")
        self.installation.record(value.provenance, "grant", now)
        try:
            envelope = json.loads(value.raw_grant.decode("utf-8"))
            payload = envelope["payload"]
        except (UnicodeError, ValueError, TypeError, KeyError):
            raise Refused("protected-grant-invalid") from None
        if (value.operation_id != binding.operation_id
                or value.binding_sha256 != _binding_sha(binding)
                or type(value.raw_grant) is not bytes
                or not 0 < len(value.raw_grant) <= 8192
                or value.grant_sha256 != digest(value.raw_grant)
                or HEX64.fullmatch(value.key_id or "") is None
                or value.issuer_actor_id != binding.issuer_actor_id
                or value.run_id != binding.run_id
                or value.run_attempt != binding.run_attempt
                or type(payload) is not dict
                or payload.get("keyId") != value.key_id
                or payload.get("issuerActorId") != value.issuer_actor_id):
            raise Refused("protected-grant-invalid")
        self.installation.scope("grant", self.clock.now())
        return value


class ProtectedIssuerKeyReader:
    def __init__(self, installation: _Installation, clock: TrustedClock,
                 grant_record: GrantRecord):
        self.installation = installation
        self.clock = clock
        self.grant_record = grant_record
        self.authorization_expires_at: dt.datetime | None = None

    def active_public_key(self, key_id: str, issuer_actor_id: int,
                          now: dt.datetime) -> bytes | None:
        if (not _utc(now) or key_id != self.grant_record.key_id
                or issuer_actor_id != self.grant_record.issuer_actor_id):
            raise Refused("protected-key-selection")
        self.installation.scope("issuer", now)
        self.installation.scope("key", now)
        try:
            issuer = self.installation.authority.read_issuer(
                self.grant_record.operation_id)
            key = self.installation.authority.read_active_key(
                key_id, issuer_actor_id)
        except Exception:
            raise Refused("protected-key-unavailable") from None
        issuer = _copy_exact(issuer, IssuerRecord, "protected-issuer-shape")
        key = _copy_exact(key, KeyRecord, "protected-key-shape")
        self.installation.record(issuer.provenance, "issuer", now)
        self.installation.record(key.provenance, "key", now)
        grant_record = self.grant_record
        if (issuer.operation_id != grant_record.operation_id
                or issuer.binding_sha256 != grant_record.binding_sha256
                or issuer.grant_sha256 != grant_record.grant_sha256
                or issuer.key_id != key_id
                or issuer.issuer_actor_id != issuer_actor_id
                or issuer.run_id != grant_record.run_id
                or issuer.run_attempt != grant_record.run_attempt
                or issuer.active is not True
                or not _utc(issuer.issued_at) or not _utc(issuer.expires_at)
                or not issuer.issued_at < issuer.expires_at
                or not issuer.issued_at <= now < issuer.expires_at
                or issuer.expires_at > issuer.issued_at + dt.timedelta(minutes=30)
                or key.key_id != key_id or key.issuer_actor_id != issuer_actor_id
                or key.active is not True or key.revoked_at is not None
                or type(key.public_key) is not bytes or len(key.public_key) != 32
                or hashlib.sha256(key.public_key).hexdigest() != key_id):
            raise Refused("protected-key-invalid")
        after = self.clock.now()
        issuer_scope = self.installation.scope("issuer", after)
        key_scope = self.installation.scope("key", after)
        final_now = self.clock.now()
        deadline = min(issuer.expires_at, issuer.provenance.expires_at,
                       key.provenance.expires_at, issuer_scope.expires_at,
                       key_scope.expires_at)
        if not final_now < deadline:
            raise Refused("protected-key-invalid")
        if self.authorization_expires_at is not None:
            deadline = min(deadline, self.authorization_expires_at)
        self.authorization_expires_at = deadline
        return key.public_key


class ProtectedNativeReadPort:
    def __init__(self, installation: _Installation, clock: TrustedClock):
        self.installation, self.clock = installation, clock

    def request(self, method: str, path: str, body):
        repository = self.installation.configuration.binding.target_repository
        prefix = f"repos/{repository}"
        if (method != "GET" or body is not None or type(path) is not str
                or not (path == prefix or path.startswith(prefix + "/")
                        or path.startswith(prefix + "?"))
                or "://" in path or path.startswith("/") or ".." in path):
            raise Refused("protected-read-selection")
        now = self.clock.now()
        self.installation.scope("native-read", now)
        try:
            response = self.installation.authority.native_get(path)
        except Exception:
            raise Refused("protected-read-unavailable") from None
        if type(response) is not coordinator.operator.HttpResponse:
            raise Refused("protected-read-response")
        self.installation.scope("native-read", self.clock.now())
        return response


class ProtectedTokenPort:
    def __init__(self, installation: _Installation, clock: TrustedClock):
        self.installation, self.clock = installation, clock
        self._lease: TokenLease | None = None
        self._scope_expires_at: dt.datetime | None = None
        self._consumed = False

    def read_execution_token(self) -> bytes:
        if self._lease is not None:
            raise Refused("protected-token-reuse")
        config = self.installation.configuration
        binding = config.binding
        now = self.clock.now()
        self.installation.scope("token", now)
        try:
            lease = self.installation.authority.issue_execution_token(
                binding.operation_id)
        except Exception:
            raise Refused("protected-token-unavailable") from None
        lease = _copy_exact(lease, TokenLease, "protected-token-shape")
        self.installation.record(lease.provenance, "token", now)
        if (lease.operation_id != binding.operation_id
                or lease.repository != binding.target_repository
                or lease.repository_id != binding.target_repository_id
                or lease.installation_id != config.installation_id
                or HEX64.fullmatch(lease.token_id or "") is None
                or type(lease.token) is not bytes or not lease.token
                or not _utc(lease.issued_at) or not _utc(lease.expires_at)
                or not lease.issued_at <= now < lease.expires_at
                or lease.expires_at > lease.issued_at + dt.timedelta(minutes=30)):
            raise Refused("protected-token-invalid")
        token_scope = self.installation.scope("token", self.clock.now())
        self._lease = lease
        self._scope_expires_at = token_scope.expires_at
        return lease.token

    def consume(self, token: bytes, now: dt.datetime) -> None:
        if (self._lease is None or self._consumed
                or type(token) is not bytes or token != self._lease.token
                or not _utc(now) or self._scope_expires_at is None
                or not now < min(self._lease.expires_at,
                                 self._lease.provenance.expires_at,
                                 self._scope_expires_at)):
            raise Refused("protected-token-selection")
        self._consumed = True


class ProtectedNativeWritePort:
    def __init__(self, installation: _Installation, clock: TrustedClock,
                 token_port: ProtectedTokenPort,
                 key_reader: ProtectedIssuerKeyReader,
                 grant_record: GrantRecord):
        self.installation, self.clock = installation, clock
        self.token_port = token_port
        self.key_reader = key_reader
        self.grant_record = grant_record
        self._sent = False

    def post_pull(self, path: str, canonical_request: bytes, token: bytes):
        binding = self.installation.configuration.binding
        if (self._sent or path != f"repos/{binding.target_repository}/pulls"
                or type(canonical_request) is not bytes
                or canonical_request != binding.canonical_request):
            raise Refused("protected-write-selection")
        now = self.clock.now()
        write_scope = self.installation.scope("native-write", now)
        # The protected write-scope read may block.  Repeat grant, issuer and
        # active-key verification after it so the final protected check is the
        # one immediately adjacent to the provider call.
        try:
            grant_sha = verify(self.grant_record.raw_grant, binding,
                self.key_reader, self.clock.now(), postcheck_clock=self.clock)
        except Exception:
            raise Refused("protected-write-authority") from None
        if grant_sha != self.grant_record.grant_sha256:
            raise Refused("protected-write-authority")
        final_now = self.clock.current
        key_deadline = self.key_reader.authorization_expires_at
        parent_deadline = self.installation.parent_provenance_expires_at
        journal_deadline = self.installation.journal_attestation_expires_at
        if (key_deadline is None or parent_deadline is None
                or journal_deadline is None
                or not final_now < min(key_deadline, parent_deadline,
                    journal_deadline,
                    self.installation.configuration.provenance.expires_at,
                    self.grant_record.provenance.expires_at)):
            raise Refused("protected-write-authority")
        if not final_now < write_scope.expires_at:
            raise Refused("protected-write-scope-expired")
        self.token_port.consume(token, final_now)
        self._sent = True
        try:
            response = self.installation.authority.native_post(
                path, binding.canonical_request, token)
        except Exception:
            raise
        if type(response) is not coordinator.operator.HttpResponse:
            raise Refused("protected-write-response")
        # A provider response is durable outcome evidence.  Authority changing
        # after the POST cannot turn a known refusal or contradiction into a
        # transport loss; the next operation will requalify from scratch.
        return response


class ProtectedAttemptJournal:
    """Host CAS and independent committed replay; no filesystem fallback."""

    def __init__(self, installation: _Installation, clock: TrustedClock):
        self.installation, self.clock = installation, clock

    def _qualified_record(self) -> ParentRecord:
        binding = self.installation.configuration.binding
        now = self.clock.now()
        self.installation.scope("parent", now)
        try:
            first = self.installation.authority.read_parent(
                binding.journal_repository, binding.journal_ref,
                binding.journal_path)
            second = self.installation.authority.read_parent(
                binding.journal_repository, binding.journal_ref,
                binding.journal_path)
        except Exception:
            raise Refused("protected-parent-unavailable") from None
        first = _copy_exact(first, ParentRecord, "protected-parent-shape")
        if type(second) is not ParentRecord or second != first:
            raise Refused("protected-parent-drift")
        self.installation.record(first.provenance, "parent", now)
        if (first.repository != binding.journal_repository
                or first.ref != binding.journal_ref
                or first.path != binding.journal_path
                or first.generation != binding.journal_prior_generation
                or first.head != binding.journal_prior_head
                or first.binding_sha256 != _binding_sha(binding)):
            raise Refused("protected-parent-invalid")
        after = self.clock.now()
        parent_scope = self.installation.scope("parent", after)
        final_now = self.clock.now()
        if not (final_now < first.provenance.expires_at
                and final_now < parent_scope.expires_at):
            raise Refused("protected-parent-invalid")
        return first

    def qualify_parent(self) -> None:
        parent = self._qualified_record()
        self.installation.parent_provenance_expires_at = parent.provenance.expires_at

    def _committed(self) -> CommittedAttempt | None:
        self._last_deadline = None
        binding = self.installation.configuration.binding
        now = self.clock.now()
        self.installation.scope("journal", now)
        try:
            value = self.installation.authority.read_committed_attempt(
                binding.operation_id)
        except Exception:
            raise Refused("protected-journal-read-unavailable") from None
        if value is None:
            return None
        value = _copy_exact(value, CommittedAttempt, "protected-journal-shape")
        if (value.operation_id != binding.operation_id
                or value.repository != binding.journal_repository
                or value.ref != binding.journal_ref
                or value.path != binding.journal_path
                or value.prior_generation != binding.journal_prior_generation
                or value.prior_head != binding.journal_prior_head
                or value.binding_sha256 != _binding_sha(binding)
                or HEX64.fullmatch(value.grant_sha256 or "") is None
                or value.attempt_may_have_started is not True
                or not _valid_outcome(value.outcome)):
            raise Refused("protected-journal-invalid")
        final_now = self.clock.now()
        final_scope = self.installation.scope("journal", final_now)
        self.installation.record(value.provenance, "journal", final_now)
        if not final_now < final_scope.expires_at:
            raise Refused("protected-journal-expired")
        self._last_deadline = min(
            value.provenance.expires_at, final_scope.expires_at)
        return value

    def reserve(self, binding: Binding, grant_sha256: str) -> bool:
        if binding != self.installation.configuration.binding or HEX64.fullmatch(grant_sha256 or "") is None:
            raise Refused("protected-journal-selection")
        self.qualify_parent()
        now = self.clock.now()
        self.installation.scope("journal", now)
        try:
            admitted = self.installation.authority.reserve_attempt(binding, grant_sha256)
        except Exception:
            raise Refused("protected-journal-reservation-unknown") from None
        if type(admitted) is not bool:
            raise Refused("protected-journal-reservation-unknown")
        return admitted

    def read(self, binding: Binding, grant_sha256: str) -> bool:
        self.installation.journal_attestation_expires_at = None
        if binding != self.installation.configuration.binding:
            return False
        value = self._committed()
        if value is None or value.grant_sha256 != grant_sha256:
            return False
        self.installation.journal_attestation_expires_at = self._last_deadline
        return True

    def record_outcome(self, binding: Binding, grant_sha256: str,
                       outcome: object) -> bool:
        if (binding != self.installation.configuration.binding
                or outcome is None or not _valid_outcome(outcome)):
            return False
        now = self.clock.now()
        self.installation.scope("journal", now)
        try:
            acknowledged = self.installation.authority.persist_attempt_outcome(
                binding.operation_id, _binding_sha(binding), grant_sha256, outcome)
        except Exception:
            return False
        if acknowledged is not True:
            return False
        value = self._committed()
        return value is not None and value.grant_sha256 == grant_sha256 and value.outcome == outcome

    def outcome(self, binding: Binding, grant_sha256: str):
        if binding != self.installation.configuration.binding:
            return None
        value = self._committed()
        if value is None or value.grant_sha256 != grant_sha256:
            return None
        if value.outcome == "lost":
            return {"status": "lost"}
        return value.outcome


@dataclasses.dataclass(frozen=True)
class InstalledRuntime:
    binding: Binding
    expected: object
    raw_grant: bytes
    key_reader: ProtectedIssuerKeyReader
    read_port: ProtectedNativeReadPort
    write_port: ProtectedNativeWritePort
    token_port: ProtectedTokenPort
    journal: ProtectedAttemptJournal
    clock_port: TrustedClock


def compose_installed(authority: ProtectedAuthority) -> InstalledRuntime:
    """Qualify protected records and require the host journal capability."""
    installation, clock = _load_installation(authority)
    grant_record = ProtectedGrantReader(installation, clock).read()
    key_reader = ProtectedIssuerKeyReader(installation, clock, grant_record)
    try:
        verified = verify(grant_record.raw_grant,
            installation.configuration.binding, key_reader, clock.now(),
            postcheck_clock=clock)
    except Exception:
        raise Refused("protected-grant-unverified") from None
    if verified != grant_record.grant_sha256:
        raise Refused("protected-grant-unverified")
    journal = ProtectedAttemptJournal(installation, clock)
    journal.qualify_parent()
    # A missing capability must refuse before any token can be requested.
    if not callable(getattr(authority, "reserve_attempt", None)) or not callable(
            getattr(authority, "read_committed_attempt", None)) or not callable(
            getattr(authority, "persist_attempt_outcome", None)):
        raise Refused("protected-journal-capability-missing")
    read_port = ProtectedNativeReadPort(installation, clock)
    token_port = ProtectedTokenPort(installation, clock)
    write_port = ProtectedNativeWritePort(installation, clock, token_port,
                                          key_reader, grant_record)
    return InstalledRuntime(installation.configuration.binding,
        installation.configuration.expected, grant_record.raw_grant,
        key_reader, read_port, write_port, token_port, journal, clock)


def execute_installed(authority: ProtectedAuthority):
    """Run S1 only after every source-owned adapter has qualified."""
    try:
        runtime = compose_installed(authority)
    except Exception:
        return coordinator.operator.Unknown("protected-adapters-unavailable")
    return coordinator.execute_pull(runtime.binding, runtime.expected,
        runtime.raw_grant, runtime.key_reader, runtime.read_port,
        runtime.write_port, runtime.token_port, runtime.journal,
        runtime.clock_port)


@dataclasses.dataclass(frozen=True)
class InstalledRecovery:
    binding: Binding
    expected: object
    raw_grant: bytes
    key_reader: ProtectedIssuerKeyReader
    read_port: ProtectedNativeReadPort
    journal: ProtectedAttemptJournal
    clock_port: TrustedClock


def compose_recovery(authority: ProtectedAuthority) -> InstalledRecovery:
    """Qualify the same protected records for read-only recovery."""
    installation, clock = _load_installation(authority)
    grant_record = ProtectedGrantReader(installation, clock).read()
    key_reader = ProtectedIssuerKeyReader(installation, clock, grant_record)
    journal = ProtectedAttemptJournal(installation, clock)
    journal.qualify_parent()
    if not callable(getattr(authority, "read_committed_attempt", None)):
        raise Refused("protected-journal-capability-missing")
    return InstalledRecovery(installation.configuration.binding,
        installation.configuration.expected, grant_record.raw_grant,
        key_reader, ProtectedNativeReadPort(installation, clock), journal,
        clock)


def recover_installed(authority: ProtectedAuthority):
    """Run S1 recovery through qualified readers and an existing parent only."""
    try:
        runtime = compose_recovery(authority)
        now = runtime.clock_port.now()
    except Exception:
        return coordinator.operator.Unknown("protected-recovery-unavailable")
    return coordinator.recover_pull(runtime.binding, runtime.expected,
        runtime.raw_grant, runtime.key_reader, runtime.read_port,
        runtime.journal, now)
