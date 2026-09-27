#!/usr/bin/env python3
"""Process-level qualification for the GS2-09.9 /5 one-attempt runtime.

This file is also its own worker executable.  The tests deliberately cross
interpreter boundaries so SQLite durability and effect counts are observed by
fresh processes rather than inferred from objects retained by the test runner.
"""

from __future__ import annotations

import argparse
import base64
from contextlib import closing
import dataclasses
import datetime as dt
import hashlib
import importlib.util
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[3]
THIS = pathlib.Path(__file__).resolve()
sys.path.insert(0, str(ROOT / "eng"))

from callable_isolated_v2_runtime import contracts, coordinator, grant


NOW = dt.datetime(2026, 9, 27, 12, 0, tzinfo=dt.timezone.utc)
CRASH_AFTER_FENCE = 71
CRASH_AFTER_POST = 72


def _fixtures():
    path = ROOT / "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
    spec = importlib.util.spec_from_file_location("v5_process_qualification_fixtures", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _expected():
    f = _fixtures()
    op = coordinator.operator
    return op.ExpectedPull(op.OPERATION_IDENTITY, 1, 44, "FS-GG/disposable",
                           "refs/heads/source", f.SHA_A,
                           "refs/heads/main", f.SHA_B)


def _binding(operation_id: str):
    expected = _expected()
    f = _fixtures()
    op = coordinator.operator
    census = op.NativeReadAdapter(
        op.OfflineTranscriptTransport(f.pull_read_events())).read_pull_census(expected)
    request = json.dumps(op.pull_request_body(expected), sort_keys=True,
                         separators=(",", ":"), ensure_ascii=True).encode()
    return contracts.Binding(
        "a" * 40, "b" * 40, "c" * 64, "d" * 64,
        expected.repository, expected.repository_id,
        contracts.digest(grant.canonical(dataclasses.asdict(census))),
        contracts.digest(request), 123, 1, 456, 789,
        "FS-GG/FS.GG.Coordination", "refs/heads/main",
        "journal/attempts.json", 8, "9" * 40, operation_id, request)


def _json_binding(binding):
    value = dataclasses.asdict(binding)
    value["canonical_request"] = base64.b64encode(
        value["canonical_request"]).decode("ascii")
    return value


def _from_json_binding(value):
    value = dict(value)
    value["canonical_request"] = base64.b64decode(value["canonical_request"])
    return contracts.Binding(**value)


def _prepare(root: pathlib.Path, operation_ids: list[str]):
    private = root / "private.pem"
    public = root / "public.der"
    subprocess.run(["openssl", "genpkey", "-algorithm", "Ed25519",
                    "-out", str(private)], check=True, capture_output=True)
    subprocess.run(["openssl", "pkey", "-in", str(private), "-pubout",
                    "-outform", "DER", "-out", str(public)],
                   check=True, capture_output=True)
    key = public.read_bytes()[-32:]
    scenarios = {}
    for index, operation_id in enumerate(operation_ids):
        selected = _binding(operation_id)
        claims = {
            "schema": grant.SCHEMA,
            "audience": grant.AUDIENCE,
            "algorithm": "Ed25519",
            "keyId": hashlib.sha256(key).hexdigest(),
            "issuedAt": "2026-09-27T11:59:00Z",
            "expiresAt": "2026-09-27T12:10:00Z",
            **selected.claims(),
        }
        payload = root / f"payload-{index}"
        signature = root / f"signature-{index}"
        payload.write_bytes(grant.canonical(claims))
        subprocess.run(["openssl", "pkeyutl", "-sign", "-inkey", str(private),
                        "-rawin", "-in", str(payload), "-out", str(signature)],
                       check=True, capture_output=True)
        envelope = {
            "schema": grant.SCHEMA,
            "payload": claims,
            "signature": base64.b64encode(signature.read_bytes()).decode("ascii"),
        }
        scenarios[operation_id] = {
            "binding": _json_binding(selected),
            "grant": base64.b64encode(grant.canonical(envelope)).decode("ascii"),
        }
    (root / "scenario.json").write_text(json.dumps({
        "key": base64.b64encode(key).decode("ascii"),
        "operations": scenarios,
    }, sort_keys=True))
    journal = coordinator.AttemptJournal(root / "attempt.db")
    if not journal.establish_parent(_binding(operation_ids[0])):
        raise AssertionError("failed to establish the qualification parent")


def _load(root: pathlib.Path, operation_id: str):
    value = json.loads((root / "scenario.json").read_text())
    operation = value["operations"][operation_id]
    return (_from_json_binding(operation["binding"]),
            base64.b64decode(operation["grant"]),
            base64.b64decode(value["key"]))


class _Clock:
    def now(self):
        return NOW


class _Keys:
    def __init__(self, key: bytes):
        self.key = key

    def active_public_key(self, key_id, issuer_actor_id, now):
        return self.key


def _append_durable(path: pathlib.Path, line: bytes):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    try:
        os.write(descriptor, line)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _mark_effect(root: pathlib.Path):
    path = root / "effect-applied"
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    try:
        os.write(descriptor, b"pull-created\n")
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


class _ReadPort:
    def __init__(self, root: pathlib.Path, phase: str):
        f = _fixtures()
        absent = f.pull_read_events()
        present = f.pull_read_events((f.pull_observed().pulls[0],))
        if phase == "recover":
            events = (present if (root / "effect-applied").exists() else absent) * 2
        else:
            # Four complete absent observations cover the S1 prestate pin and
            # retained operator.  Two complete present observations are ready
            # for the winner's poststate classification.
            events = absent * 4 + present * 2
        self.transport = coordinator.operator.OfflineTranscriptTransport(events)

    def request(self, method, path, body):
        return self.transport.request(method, path, body)


class _TokenPort:
    def __init__(self, root: pathlib.Path, crash: bool):
        self.root = root
        self.crash = crash

    def read_execution_token(self):
        _append_durable(self.root / "token-count", b"TOKEN\n")
        if self.crash:
            os._exit(CRASH_AFTER_FENCE)
        return b"qualification-token"


class _WritePort:
    def __init__(self, root: pathlib.Path, mode: str):
        self.root = root
        self.mode = mode

    def post_pull(self, path, body, token):
        _append_durable(self.root / "post-count", b"POST\n")
        _mark_effect(self.root)
        if self.mode == "crash-after-post":
            os._exit(CRASH_AFTER_POST)
        if self.mode == "lost-response":
            raise OSError("response lost after provider applied the write")
        pull = _fixtures().pull_observed().pulls[0]
        raw = json.dumps(pull, sort_keys=True, separators=(",", ":")).encode()
        return coordinator.operator.HttpResponse(201, (), raw)


def _result(value):
    return {"type": type(value).__name__, "reason": getattr(value, "reason", None)}


def _attempt(root: pathlib.Path, operation_id: str, mode: str):
    selected, raw_grant, key = _load(root, operation_id)
    result = coordinator.execute_pull(
        selected, _expected(), raw_grant, _Keys(key), _ReadPort(root, "attempt"),
        _WritePort(root, mode),
        _TokenPort(root, mode == "crash-after-fence"),
        coordinator.AttemptJournal(root / "attempt.db"), _Clock())
    print(json.dumps(_result(result), sort_keys=True), flush=True)


def _recover(root: pathlib.Path, operation_id: str):
    selected, raw_grant, key = _load(root, operation_id)
    result = coordinator.recover_pull(
        selected, _expected(), raw_grant, _Keys(key),
        _ReadPort(root, "recover"),
        coordinator.AttemptJournal(root / "attempt.db"), NOW)
    print(json.dumps(_result(result), sort_keys=True), flush=True)


def _inspect(root: pathlib.Path, operation_id: str):
    selected, raw_grant, _ = _load(root, operation_id)
    grant_sha = hashlib.sha256(raw_grant).hexdigest()
    journal = coordinator.AttemptJournal(root / "attempt.db")
    outcome = journal.outcome(selected, grant_sha)
    with closing(sqlite3.connect(root / "attempt.db")) as database:
        count = database.execute("SELECT COUNT(*) FROM attempts").fetchone()[0]
        pending = database.execute(
            "SELECT COUNT(*) FROM attempts WHERE outcome IS NULL").fetchone()[0]
    print(json.dumps({
        "fence": journal.read(selected, grant_sha),
        "attempts": count,
        "pending": pending,
        "outcome": "lost" if outcome == {"status": "lost"}
                   else "response" if outcome is not None else None,
    }, sort_keys=True), flush=True)


def _worker(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("attempt", "recover", "inspect"))
    parser.add_argument("root", type=pathlib.Path)
    parser.add_argument("operation_id")
    parser.add_argument("--mode", default="normal",
                        choices=("normal", "crash-after-fence",
                                 "crash-after-post", "lost-response"))
    args = parser.parse_args(argv)
    if args.action == "attempt":
        _attempt(args.root, args.operation_id, args.mode)
    elif args.action == "recover":
        _recover(args.root, args.operation_id)
    else:
        _inspect(args.root, args.operation_id)
    return 0


class ProcessQualificationTests(unittest.TestCase):
    maxDiff = None

    def _run(self, root, action, operation_id="operation-1", mode=None):
        command = [sys.executable, str(THIS), "--worker", action,
                   str(root), operation_id]
        if mode is not None:
            command += ["--mode", mode]
        return subprocess.run(command, capture_output=True, text=True,
                              timeout=20, check=False)

    def _json(self, completed):
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return json.loads(completed.stdout)

    def _count(self, root, name):
        path = pathlib.Path(root) / name
        return len(path.read_text().splitlines()) if path.exists() else 0

    def test_crash_after_fence_before_post_is_durable_and_never_retried(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            _prepare(root, ["operation-1"])
            crashed = self._run(root, "attempt", mode="crash-after-fence")
            self.assertEqual(crashed.returncode, CRASH_AFTER_FENCE, crashed.stderr)
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 0)

            durable = self._json(self._run(root, "inspect"))
            self.assertEqual(durable, {"attempts": 1, "fence": True,
                                       "outcome": None, "pending": 1})
            retry = self._json(self._run(root, "attempt"))
            self.assertEqual(retry["type"], "Unknown")
            recovery = self._json(self._run(root, "recover"))
            self.assertEqual(recovery, {"reason": "recovery-outcome-unproved",
                                        "type": "Unknown"})
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 0)

    def test_crash_after_post_keeps_pending_outcome_and_never_resends(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            _prepare(root, ["operation-1"])
            crashed = self._run(root, "attempt", mode="crash-after-post")
            self.assertEqual(crashed.returncode, CRASH_AFTER_POST, crashed.stderr)
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 1)
            self.assertTrue((root / "effect-applied").exists())

            durable = self._json(self._run(root, "inspect"))
            self.assertEqual(durable, {"attempts": 1, "fence": True,
                                       "outcome": None, "pending": 1})
            retry = self._json(self._run(root, "attempt"))
            self.assertEqual(retry["type"], "Unknown")
            recovery = self._json(self._run(root, "recover"))
            self.assertEqual(recovery, {"reason": "recovery-outcome-unproved",
                                        "type": "Unknown"})
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 1)

    def test_lost_response_marker_allows_read_only_recovery_without_resend(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            _prepare(root, ["operation-1"])
            first = self._json(self._run(root, "attempt", mode="lost-response"))
            self.assertEqual(first["type"], "ExactPull")
            durable = self._json(self._run(root, "inspect"))
            self.assertEqual(durable, {"attempts": 1, "fence": True,
                                       "outcome": "lost", "pending": 0})
            recovered = self._json(self._run(root, "recover"))
            self.assertEqual(recovered["type"], "ExactPull")
            retry = self._json(self._run(root, "attempt"))
            self.assertEqual(retry["type"], "Unknown")
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 1)

    def test_competing_parent_attempts_commit_and_post_exactly_once(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            operation_ids = [f"operation-{index}" for index in range(8)]
            _prepare(root, operation_ids)
            processes = [subprocess.Popen(
                [sys.executable, str(THIS), "--worker", "attempt", str(root),
                 operation_id, "--mode", "lost-response"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                for operation_id in operation_ids]
            completed = []
            for process in processes:
                stdout, stderr = process.communicate(timeout=20)
                self.assertEqual(process.returncode, 0, stderr)
                completed.append(json.loads(stdout))
            self.assertEqual(sum(item["type"] == "ExactPull" for item in completed), 1)
            self.assertEqual(sum(item["type"] == "Unknown" for item in completed), 7)
            self.assertEqual(self._count(root, "token-count"), 1)
            self.assertEqual(self._count(root, "post-count"), 1)
            with closing(sqlite3.connect(root / "attempt.db")) as database:
                rows = database.execute(
                    "SELECT operation_id, attempt_may_have_started, outcome IS NOT NULL "
                    "FROM attempts").fetchall()
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0][1:], (1, 1))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--worker":
        raise SystemExit(_worker(sys.argv[2:]))
    unittest.main()
