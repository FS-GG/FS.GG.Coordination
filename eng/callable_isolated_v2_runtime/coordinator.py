"""Internal /5 one-attempt pull composer. No CLI, endpoint, key or credential flags."""
from __future__ import annotations

import dataclasses
from contextlib import closing
import base64
import json
import sqlite3
from pathlib import Path
from .contracts import Binding, digest, operator_module
from .grant import canonical, verify, Refused

operator = operator_module()


class AttemptJournal:
    """Local SQLite qualification backend; never selected by the installed composer."""
    def __init__(self, path: Path):
        self.path = Path(path)

    def _connect(self):
        connection = sqlite3.connect(self.path, timeout=5, isolation_level=None)
        connection.execute("PRAGMA synchronous=FULL")
        connection.execute("CREATE TABLE IF NOT EXISTS parents (repository TEXT NOT NULL, ref TEXT NOT NULL, path TEXT NOT NULL, generation INTEGER NOT NULL, head TEXT NOT NULL, PRIMARY KEY(repository, ref, path))")
        connection.execute("CREATE TABLE IF NOT EXISTS attempts (operation_id TEXT PRIMARY KEY, repository TEXT NOT NULL, ref TEXT NOT NULL, path TEXT NOT NULL, prior_generation INTEGER NOT NULL, prior_head TEXT NOT NULL, binding_sha256 TEXT NOT NULL, grant_sha256 TEXT NOT NULL, attempt_may_have_started INTEGER NOT NULL CHECK(attempt_may_have_started=1), outcome BLOB, UNIQUE(repository, ref, path, prior_generation, prior_head))")
        return connection

    def establish_parent(self, binding: Binding) -> bool:
        """Source-only bootstrap hook; S2 must supply the protected parent.

        An existing parent is never replaced here. Reservation requires the
        exact installed parent, so a caller cannot self-seed in reserve().
        """
        with closing(self._connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            try:
                db.execute("INSERT INTO parents VALUES (?, ?, ?, ?, ?)",
                           (binding.journal_repository, binding.journal_ref,
                            binding.journal_path, binding.journal_prior_generation,
                            binding.journal_prior_head))
            except sqlite3.IntegrityError:
                db.rollback()
                return False
            db.commit()
        return True

    def reserve(self, binding: Binding, grant_sha256: str) -> bool:
        # A committed fence means the write may already have happened. No reset.
        with closing(self._connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            parent = db.execute(
                "SELECT generation, head FROM parents WHERE repository=? AND ref=? AND path=?",
                (binding.journal_repository, binding.journal_ref,
                 binding.journal_path)).fetchone()
            if parent != (binding.journal_prior_generation, binding.journal_prior_head):
                db.rollback()
                return False
            try:
                db.execute("INSERT INTO attempts (operation_id, repository, ref, path, prior_generation, prior_head, binding_sha256, grant_sha256, attempt_may_have_started) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1)",
                           (binding.operation_id, binding.journal_repository,
                            binding.journal_ref, binding.journal_path,
                            binding.journal_prior_generation,
                            binding.journal_prior_head,
                            digest(canonical(binding.claims())), grant_sha256))
            except sqlite3.IntegrityError:
                db.rollback()
                return False
            db.commit()
        return True

    def record_outcome(self, binding: Binding, grant_sha256: str, outcome: object) -> bool:
        """Store one response or loss marker; pending remains ineligible on crash."""
        if type(outcome) is operator.HttpResponse:
            if (type(outcome.status) is not int or type(outcome.headers) is not tuple
                    or type(outcome.body) is not bytes or len(outcome.body) > 4_000_000):
                return False
            value = {"kind": "response", "status": outcome.status,
                     "headers": [list(pair) for pair in outcome.headers],
                     "body": base64.b64encode(outcome.body).decode("ascii")}
        elif outcome == "lost":
            value = {"kind": "lost"}
        else:
            value = {"kind": "invalid"}
        raw = canonical(value)
        with closing(self._connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            changed = db.execute(
                "UPDATE attempts SET outcome=? WHERE operation_id=? AND binding_sha256=? AND grant_sha256=? AND outcome IS NULL",
                (raw, binding.operation_id, digest(canonical(binding.claims())),
                 grant_sha256)).rowcount
            db.commit()
        return changed == 1

    def outcome(self, binding: Binding, grant_sha256: str):
        with closing(self._connect()) as db:
            row = db.execute(
                "SELECT outcome FROM attempts WHERE operation_id=? AND binding_sha256=? AND grant_sha256=?",
                (binding.operation_id, digest(canonical(binding.claims())),
                 grant_sha256)).fetchone()
        if row is None or row[0] is None:
            return None
        try:
            value = json.loads(row[0])
            if value == {"kind": "lost"}:
                return {"status": "lost"}
            if type(value) is not dict or set(value) != {"kind", "status", "headers", "body"} or value["kind"] != "response":
                return None
            headers = tuple(tuple(pair) for pair in value["headers"])
            body = base64.b64decode(value["body"], validate=True)
            return operator.HttpResponse(value["status"], headers, body)
        except (ValueError, TypeError):
            return None

    def read(self, binding: Binding, grant_sha256: str) -> bool:
        # A new connection is an independent committed read, not the writer's view.
        with closing(self._connect()) as db:
            row = db.execute("SELECT binding_sha256, grant_sha256, attempt_may_have_started FROM attempts WHERE operation_id=?",
                             (binding.operation_id,)).fetchone()
        return row == (digest(canonical(binding.claims())), grant_sha256, 1)


class _Transport:
    def __init__(self, read_port, write_port, token_port, clock_port,
                 binding, raw_grant, key_reader, journal, grant_sha):
        if read_port is write_port or read_port is token_port or write_port is token_port:
            raise Refused("runtime-port-overlap")
        self.read_port, self.write_port, self.token_port = read_port, write_port, token_port
        self.clock_port, self.binding, self.raw_grant = clock_port, binding, raw_grant
        self.key_reader, self.journal, self.grant_sha = key_reader, journal, grant_sha
        self.outcome_acknowledged = False
        self.acknowledged_outcome = None

    def request(self, method, path, body):
        if method == "GET" and body is None:
            return self.read_port.request(method, path, body)
        if method == "POST" and type(path) is str and type(body) is dict:
            # Fresh protected key status and expiry immediately before the effect.
            if not self.journal.read(self.binding, self.grant_sha):
                raise Refused("runtime-fence-drift")
            if verify(self.raw_grant, self.binding, self.key_reader,
                      self.clock_port.now(),
                      postcheck_clock=self.clock_port) != self.grant_sha:
                raise Refused("runtime-grant-drift")
            if (path != f"repos/{self.binding.target_repository}/pulls"
                    or json.loads(self.binding.canonical_request) != body):
                raise Refused("runtime-request-drift")
            token = self.token_port.read_execution_token()
            if type(token) is not bytes or not token:
                raise Refused("runtime-token-unavailable")
            # Token acquisition may block or mint. Its result confers no lasting
            # authority: recheck active key and expiry at the actual send edge.
            if verify(self.raw_grant, self.binding, self.key_reader,
                      self.clock_port.now(),
                      postcheck_clock=self.clock_port) != self.grant_sha:
                raise Refused("runtime-grant-drift")
            if not self.journal.read(self.binding, self.grant_sha):
                raise Refused("runtime-fence-drift")
            try:
                response = self.write_port.post_pull(path, self.binding.canonical_request, token)
            except Exception:
                self.outcome_acknowledged = self.journal.record_outcome(
                    self.binding, self.grant_sha, "lost") is True
                if self.outcome_acknowledged:
                    self.acknowledged_outcome = {"status": "lost"}
                raise
            if not self.journal.record_outcome(self.binding, self.grant_sha, response):
                raise Refused("runtime-outcome-unacknowledged")
            self.outcome_acknowledged = True
            self.acknowledged_outcome = response
            return response
        raise Refused("runtime-method")


def _valid_binding(binding: Binding, expected) -> bool:
    if type(binding) is not Binding or not operator._valid_pull(expected):
        return False
    if binding.target_repository != expected.repository or binding.target_repository_id != expected.repository_id:
        return False
    if type(binding.operation_id) is not str or not binding.operation_id:
        return False
    body = operator.pull_request_body(expected)
    request = json.dumps(body, sort_keys=True, separators=(",", ":"),
                         ensure_ascii=True, allow_nan=False).encode("ascii")
    if (type(binding.canonical_request) is not bytes
            or binding.canonical_request != request
            or binding.request_sha256 != digest(binding.canonical_request)):
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
                 read_port, write_port, token_port, journal: AttemptJournal,
                 clock_port):
    """Verify grant, observe absence, fence attempt, read fence, then permit one POST.

    Trusted adapters are installed by an internal composer. This function has no
    production CLI route and no caller-selected network origin or key bytes.
    """
    try:
        if not _valid_binding(binding, expected):
            return operator.Unknown("runtime-binding-invalid")
        grant_sha = verify(raw_grant, binding, key_reader, clock_port.now())
        if not _prestate_matches(expected, read_port, binding):
            return operator.Unknown("runtime-prestate-mismatch")
        transport = _Transport(read_port, write_port, token_port, clock_port,
                               binding, raw_grant, key_reader, journal, grant_sha)
        result = operator.run_pull_once(
            expected, transport,
            lambda _key: journal.reserve(binding, grant_sha)
            and journal.read(binding, grant_sha))
        outcome = journal.outcome(binding, grant_sha)
        if outcome is None:
            return operator.Unknown("runtime-outcome-unproved")
        if type(result) is operator.ExactPull:
            if not transport.outcome_acknowledged:
                return operator.Unknown("runtime-outcome-unacknowledged")
            if outcome != transport.acknowledged_outcome:
                return operator.Unknown("runtime-outcome-contradiction")
            if not operator._response_allows_readback(outcome):
                return operator.Unknown("runtime-outcome-ineligible")
        return result
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
        outcome = journal.outcome(binding, grant_sha)
        if outcome is None:
            return operator.Unknown("recovery-outcome-unproved")
        adapter = operator.NativeReadAdapter(read_port)
        return operator.classify_pull_after_one_attempt(
            expected, lambda: adapter.read_pull_census(expected), outcome)
    except Exception:
        return operator.Unknown("recovery-unavailable")
