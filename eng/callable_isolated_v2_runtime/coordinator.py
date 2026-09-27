"""Internal /5 one-attempt pull composer. No CLI, endpoint, key or credential flags."""
from __future__ import annotations

import dataclasses
from contextlib import closing
import sqlite3
from pathlib import Path
from .contracts import Binding, digest, operator_module
from .grant import canonical, verify, Refused

operator = operator_module()


class AttemptJournal:
    """SQLite durable attempt fence; competing processes share one operation key."""
    def __init__(self, path: Path):
        self.path = Path(path)

    def _connect(self):
        connection = sqlite3.connect(self.path, timeout=5, isolation_level=None)
        connection.execute("PRAGMA synchronous=FULL")
        connection.execute("CREATE TABLE IF NOT EXISTS attempts (operation_id TEXT PRIMARY KEY, binding_sha256 TEXT NOT NULL, grant_sha256 TEXT NOT NULL, attempt_may_have_started INTEGER NOT NULL CHECK(attempt_may_have_started=1))")
        return connection

    def reserve(self, binding: Binding, grant_sha256: str) -> bool:
        # A committed fence means the write may already have happened. No reset.
        with closing(self._connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            try:
                db.execute("INSERT INTO attempts VALUES (?, ?, ?, 1)",
                           (binding.operation_id, digest(canonical(binding.claims())), grant_sha256))
            except sqlite3.IntegrityError:
                db.rollback()
                return False
            db.commit()
        return True

    def read(self, binding: Binding, grant_sha256: str) -> bool:
        # A new connection is an independent committed read, not the writer's view.
        with closing(self._connect()) as db:
            row = db.execute("SELECT binding_sha256, grant_sha256, attempt_may_have_started FROM attempts WHERE operation_id=?",
                             (binding.operation_id,)).fetchone()
        return row == (digest(canonical(binding.claims())), grant_sha256, 1)


class _Transport:
    def __init__(self, read_port, write_port, token_port):
        if read_port is write_port or read_port is token_port or write_port is token_port:
            raise Refused("runtime-port-overlap")
        self.read_port, self.write_port, self.token_port = read_port, write_port, token_port

    def request(self, method, path, body):
        if method == "GET" and body is None:
            return self.read_port.request(method, path, body)
        if method == "POST" and type(path) is str and type(body) is dict:
            token = self.token_port.read_execution_token()
            if type(token) is not bytes or not token:
                raise Refused("runtime-token-unavailable")
            return self.write_port.post_pull(path, body, token)
        raise Refused("runtime-method")


def _valid_binding(binding: Binding, expected) -> bool:
    if type(binding) is not Binding or not operator._valid_pull(expected):
        return False
    if binding.target_repository != expected.repository or binding.target_repository_id != expected.repository_id:
        return False
    if type(binding.operation_id) is not str or not binding.operation_id:
        return False
    body = operator.pull_request_body(expected)
    if binding.request_sha256 != digest(canonical(body)):
        return False
    for name in ("source_revision", "source_tree", "artifact_sha256", "workflow_sha256",
                 "target_prestate_sha256", "journal_prior_head"):
        value = getattr(binding, name)
        if type(value) is not str or not value:
            return False
    for name in ("run_id", "run_attempt", "approval_event_id", "issuer_actor_id"):
        if type(getattr(binding, name)) is not int or getattr(binding, name) <= 0:
            return False
    return (type(binding.journal_prior_generation) is int
            and binding.journal_prior_generation >= 0
            and all(type(getattr(binding, n)) is str and getattr(binding, n)
                    for n in ("journal_repository", "journal_ref", "journal_path")))


def _prestate_matches(expected, read_port, binding):
    adapter = operator.NativeReadAdapter(read_port)
    observed = operator._two(lambda: adapter.read_pull_census(expected))
    if (type(observed) is not operator.PullCensus or observed.complete is not True
            or observed.repository_id != expected.repository_id
            or observed.source_branch_sha != expected.source_sha
            or observed.base_branch_sha != expected.base_sha
            or type(observed.pulls) is not tuple or observed.pulls):
        return False
    return digest(canonical(dataclasses.asdict(observed))) == binding.target_prestate_sha256


def execute_pull(binding: Binding, expected, raw_grant: bytes, key_reader,
                 read_port, write_port, token_port, journal: AttemptJournal, now):
    """Verify grant, observe absence, fence attempt, read fence, then permit one POST.

    Trusted adapters are installed by an internal composer. This function has no
    production CLI route and no caller-selected network origin or key bytes.
    """
    try:
        if not _valid_binding(binding, expected):
            return operator.Unknown("runtime-binding-invalid")
        grant_sha = verify(raw_grant, binding, key_reader, now)
        if not _prestate_matches(expected, read_port, binding):
            return operator.Unknown("runtime-prestate-mismatch")
        transport = _Transport(read_port, write_port, token_port)
        return operator.run_pull_once(
            expected, transport,
            lambda _key: journal.reserve(binding, grant_sha)
            and journal.read(binding, grant_sha))
    except Exception:
        return operator.Unknown("runtime-unavailable")


def recover_pull(binding: Binding, expected, raw_grant: bytes, key_reader,
                 read_port, journal: AttemptJournal, now):
    """Fresh invocation: verify the same grant and fence, then reread only."""
    try:
        if not _valid_binding(binding, expected):
            return operator.Unknown("recovery-binding-invalid")
        grant_sha = verify(raw_grant, binding, key_reader, now, allow_expired=True)
        if not journal.read(binding, grant_sha):
            return operator.Unknown("recovery-attempt-unproved")
        adapter = operator.NativeReadAdapter(read_port)
        return operator.classify_pull_after_one_attempt(
            expected, lambda: adapter.read_pull_census(expected))
    except Exception:
        return operator.Unknown("recovery-unavailable")
