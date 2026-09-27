import base64
from contextlib import closing
import dataclasses
import datetime as dt
import hashlib
import json
import multiprocessing as mp
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "eng"))
from callable_isolated_v2_runtime import contracts, grant, coordinator

NOW = dt.datetime(2026, 9, 27, 12, 0, tzinfo=dt.timezone.utc)


def binding():
    return contracts.Binding("a" * 40, "b" * 40, "c" * 64, "d" * 64,
                             "FS-GG/example", 42, "e" * 64, "f" * 64,
                             123, 1, 456, 789, "FS-GG/FS.GG.Coordination",
                             "refs/heads/main", "journal/attempts.json", 8,
                             "9" * 40, "operation-1", b"{}")


class FixedClock:
    def __init__(self, *times):
        self.times = iter(times)
        self.last = times[-1]
    def now(self):
        try:
            self.last = next(self.times)
        except StopIteration:
            pass
        return self.last


def sign(temp, value):
    private = pathlib.Path(temp) / "private.pem"
    public = pathlib.Path(temp) / "public.der"
    payload = pathlib.Path(temp) / "payload"
    signature = pathlib.Path(temp) / "signature"
    subprocess.run(["openssl", "genpkey", "-algorithm", "Ed25519", "-out", str(private)],
                   check=True, capture_output=True)
    subprocess.run(["openssl", "pkey", "-in", str(private), "-pubout", "-outform", "DER", "-out", str(public)],
                   check=True, capture_output=True)
    key = public.read_bytes()[-32:]
    value["keyId"] = hashlib.sha256(key).hexdigest()
    payload.write_bytes(grant.canonical(value))
    subprocess.run(["openssl", "pkeyutl", "-sign", "-inkey", str(private),
                    "-rawin", "-in", str(payload), "-out", str(signature)],
                   check=True, capture_output=True)
    envelope = {"schema": grant.SCHEMA, "payload": value,
                "signature": base64.b64encode(signature.read_bytes()).decode("ascii")}
    return key, grant.canonical(envelope)


class Keys:
    def __init__(self, key):
        self.key = key
        self.calls = 0
    def active_public_key(self, key_id, issuer_actor_id, now):
        self.calls += 1
        return self.key


class RevokingKeys(Keys):
    def active_public_key(self, key_id, issuer_actor_id, now):
        self.calls += 1
        return self.key if self.calls == 1 else None


class MutableKeys(Keys):
    def __init__(self, key):
        super().__init__(key)
        self.revoked = False
    def active_public_key(self, key_id, issuer_actor_id, now):
        self.calls += 1
        return None if self.revoked else self.key


class MutableClock:
    def __init__(self, now):
        self.value = now
    def now(self):
        return self.value


class CountingPort:
    def __init__(self):
        self.calls = 0
    def request(self, *args):
        self.calls += 1
        raise AssertionError("GET before grant verification")
    def post_pull(self, *args):
        self.calls += 1
        raise AssertionError("POST before grant verification")
    def read_execution_token(self):
        self.calls += 1
        raise AssertionError("token before grant verification")


def compete(path, queue, index):
    selected = dataclasses.replace(binding(), operation_id=f"operation-{index}")
    queue.put((index, coordinator.AttemptJournal(path).reserve(selected, "a" * 64)))


def crash_after_reserve(path):
    import os
    coordinator.AttemptJournal(path).reserve(binding(), "a" * 64)
    os._exit(23)


class RuntimeTests(unittest.TestCase):
    def test_real_signature_all_bindings_and_zero_effect_on_bad_grant(self):
        with tempfile.TemporaryDirectory() as temp:
            selected = binding()
            claims = {"schema": grant.SCHEMA, "audience": grant.AUDIENCE,
                      "algorithm": "Ed25519", "keyId": "", "issuedAt": "2026-09-27T11:59:00Z",
                      "expiresAt": "2026-09-27T12:10:00Z", **selected.claims()}
            key, raw = sign(temp, claims)
            keys = Keys(key)
            self.assertEqual(grant.verify(raw, selected, keys, NOW), hashlib.sha256(raw).hexdigest())
            for altered in (dataclasses.replace(selected, source_tree="0" * 40),
                            dataclasses.replace(selected, artifact_sha256="0" * 64),
                            dataclasses.replace(selected, workflow_sha256="0" * 64),
                            dataclasses.replace(selected, target_repository="other/repo"),
                            dataclasses.replace(selected, request_sha256="0" * 64),
                            dataclasses.replace(selected, run_id=124),
                            dataclasses.replace(selected, approval_event_id=457),
                            dataclasses.replace(selected, issuer_actor_id=790),
                            dataclasses.replace(selected, journal_prior_generation=9)):
                with self.assertRaises(grant.Refused):
                    grant.verify(raw, altered, keys, NOW)
            value = __import__("json").loads(raw)
            value["payload"]["expiresAt"] = "2026-09-27T11:00:00Z"
            bad = grant.canonical(value)
            with self.assertRaises(grant.Refused):
                grant.verify(bad, selected, keys, NOW)
            expected = coordinator.operator.ExpectedPull(
                coordinator.operator.OPERATION_IDENTITY, 1, 42, "FS-GG/example",
                "refs/heads/source", "1" * 40, "refs/heads/main", "2" * 40)
            selected = dataclasses.replace(selected,
                canonical_request=json.dumps(coordinator.operator.pull_request_body(expected),
                    sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode())
            selected = dataclasses.replace(selected,
                request_sha256=contracts.digest(selected.canonical_request))
            read, write, token = CountingPort(), CountingPort(), CountingPort()
            journal = coordinator.AttemptJournal(pathlib.Path(temp) / "attempt.db")
            result = coordinator.execute_pull(selected, expected, bad, keys,
                                              read, write, token, journal, FixedClock(NOW))
            self.assertIsInstance(result, coordinator.operator.Unknown)
            self.assertEqual((read.calls, write.calls, token.calls), (0, 0, 0))
            self.assertFalse(journal.read(selected, hashlib.sha256(bad).hexdigest()))

    def test_crash_after_reservation_survives_process_exit(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "attempt.db"
            self.assertTrue(coordinator.AttemptJournal(path).establish_parent(binding()))
            child = mp.Process(target=crash_after_reserve, args=(path,))
            child.start()
            child.join(10)
            self.assertEqual(child.exitcode, 23)
            self.assertTrue(coordinator.AttemptJournal(path).read(binding(), "a" * 64))
            self.assertFalse(coordinator.AttemptJournal(path).reserve(binding(), "a" * 64))

    def test_competing_processes_commit_only_one_attempt(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "attempt.db"
            self.assertTrue(coordinator.AttemptJournal(path).establish_parent(binding()))
            queue = mp.Queue()
            processes = [mp.Process(target=compete, args=(path, queue, index))
                         for index in range(8)]
            for process in processes:
                process.start()
            answers = [queue.get(timeout=10) for _ in processes]
            for process in processes:
                process.join(10)
                self.assertEqual(process.exitcode, 0)
            winners = [index for index, reserved in answers if reserved]
            self.assertEqual(len(winners), 1)
            winner = dataclasses.replace(binding(), operation_id=f"operation-{winners[0]}")
            self.assertTrue(coordinator.AttemptJournal(path).read(winner, "a" * 64))
            self.assertFalse(coordinator.AttemptJournal(path).read(
                dataclasses.replace(binding(), journal_prior_head="0" * 40), "a" * 64))

    def test_parent_cas_refuses_stale_and_second_operation(self):
        with tempfile.TemporaryDirectory() as temp:
            journal = coordinator.AttemptJournal(pathlib.Path(temp) / "attempt.db")
            original = binding()
            other = dataclasses.replace(original, operation_id="operation-2")
            stale = dataclasses.replace(original, operation_id="operation-3",
                                        journal_prior_generation=7)
            self.assertFalse(journal.reserve(original, "a" * 64))
            self.assertTrue(journal.establish_parent(original))
            self.assertFalse(journal.reserve(stale, "a" * 64))
            self.assertTrue(journal.reserve(original, "a" * 64))
            self.assertFalse(journal.reserve(other, "b" * 64))


# Exercise the retained native parser through separate injected read/write ports.
# The counter lives outside the coordinator and records every attempted POST.
class ScriptedRead:
    def __init__(self, transport):
        self.transport = transport
    def request(self, method, path, body):
        return self.transport.request(method, path, body)


class ScriptedWrite:
    def __init__(self, transport, counter):
        self.transport, self.counter = transport, counter
    def post_pull(self, path, body, token):
        with self.counter.open("a") as output:
            output.write("POST\n")
        return self.transport.request("POST", path, json.loads(body))


class ScriptedToken:
    def __init__(self):
        self.calls = 0
    def read_execution_token(self):
        self.calls += 1
        return b"test-token"


class ChangingToken(ScriptedToken):
    def __init__(self, change):
        super().__init__()
        self.change = change
    def read_execution_token(self):
        token = super().read_execution_token()
        self.change()
        return token


class NativeIntegrationTests(unittest.TestCase):
    def _scenario(self, temp, post_event, *, consume_poststate=True, keys_type=Keys,
                  clock=None, token_factory=None):
        import importlib.util
        fixture_path = ROOT / "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
        spec = importlib.util.spec_from_file_location("retained_fixtures_extra", fixture_path)
        fixtures = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = fixtures
        spec.loader.exec_module(fixtures)
        op = coordinator.operator
        expected = op.ExpectedPull(op.OPERATION_IDENTITY, 1, 44, "FS-GG/disposable",
                                   "refs/heads/source", fixtures.SHA_A,
                                   "refs/heads/main", fixtures.SHA_B)
        census = op.NativeReadAdapter(op.OfflineTranscriptTransport(
            fixtures.pull_read_events())).read_pull_census(expected)
        request = json.dumps(op.pull_request_body(expected), sort_keys=True,
                             separators=(",", ":"), ensure_ascii=True).encode()
        selected = dataclasses.replace(binding(), target_repository=expected.repository,
            target_repository_id=44,
            target_prestate_sha256=contracts.digest(grant.canonical(dataclasses.asdict(census))),
            canonical_request=request, request_sha256=contracts.digest(request))
        claims = {"schema": grant.SCHEMA, "audience": grant.AUDIENCE,
                  "algorithm": "Ed25519", "keyId": "", "issuedAt": "2026-09-27T11:59:00Z",
                  "expiresAt": "2026-09-27T12:10:00Z", **selected.claims()}
        key, raw = sign(temp, claims)
        post = fixtures.pull_read_events((fixtures.pull_observed().pulls[0],))
        events = fixtures.pull_read_events() * 4 + [post_event(fixtures, expected)]
        events += post * (4 if consume_poststate else 2)
        transport = op.OfflineTranscriptTransport(events)
        reader = ScriptedRead(transport)
        counter = pathlib.Path(temp) / "posts"
        writer = ScriptedWrite(transport, counter)
        clock = clock or FixedClock(NOW, NOW, NOW)
        keys = keys_type(key)
        token = token_factory(keys, clock) if token_factory else ScriptedToken()
        journal = coordinator.AttemptJournal(pathlib.Path(temp) / "attempt.db")
        self.assertTrue(journal.establish_parent(selected))
        first = coordinator.execute_pull(selected, expected, raw, keys, reader,
            writer, token, journal, clock)
        return first, selected, expected, raw, key, reader, counter, token, journal

    def test_explicit_refusal_cannot_become_exact_on_recovery(self):
        with tempfile.TemporaryDirectory() as temp:
            def response(f, expected):
                return f.event("POST", "repos/FS-GG/disposable/pulls",
                               body=coordinator.operator.pull_request_body(expected),
                               status=401, value={"message": "refused"})
            first, selected, expected, raw, key, reader, counter, token, journal = \
                self._scenario(temp, response, consume_poststate=False)
            self.assertIsInstance(first, coordinator.operator.Unknown)
            recovered = coordinator.recover_pull(selected, expected, raw, Keys(key),
                                                 reader, journal, NOW)
            self.assertIsInstance(recovered, coordinator.operator.Unknown)
            self.assertEqual(counter.read_text(), "POST\n")
            self.assertEqual(token.calls, 1)
            # Simulate a process death after POST but before response persistence.
            # Pending outcome cannot turn an otherwise exact readback into success.
            with closing(sqlite3.connect(journal.path)) as db:
                db.execute("UPDATE attempts SET outcome=NULL")
                db.commit()
            pending = coordinator.recover_pull(selected, expected, raw, Keys(key),
                                               reader, journal, NOW)
            self.assertIsInstance(pending, coordinator.operator.Unknown)

    def test_contradictory_success_response_cannot_become_exact_on_recovery(self):
        with tempfile.TemporaryDirectory() as temp:
            def response(f, expected):
                return f.event("POST", "repos/FS-GG/disposable/pulls",
                               body=coordinator.operator.pull_request_body(expected),
                               status=201, value={"number": 9, "node_id": "PR_9"})
            first, selected, expected, raw, key, reader, counter, token, journal = \
                self._scenario(temp, response)
            self.assertIsInstance(first, coordinator.operator.Unknown)
            recovered = coordinator.recover_pull(selected, expected, raw, Keys(key),
                                                 reader, journal, NOW)
            self.assertIsInstance(recovered, coordinator.operator.Unknown)
            self.assertEqual(counter.read_text(), "POST\n")

    def test_revoked_or_expired_at_post_boundary_has_no_token_or_post(self):
        for keys_type, clock in ((RevokingKeys, FixedClock(NOW, NOW)),
                                 (Keys, FixedClock(NOW, NOW + dt.timedelta(minutes=11)))):
            with self.subTest(keys_type=keys_type), tempfile.TemporaryDirectory() as temp:
                def unused(f, expected):
                    return f.event("POST", "repos/FS-GG/disposable/pulls",
                                   body=coordinator.operator.pull_request_body(expected),
                                   status=201, value={})
                first, selected, expected, raw, key, reader, counter, token, journal = \
                    self._scenario(temp, unused, keys_type=keys_type, clock=clock)
                self.assertIsInstance(first, coordinator.operator.Unknown)
                self.assertFalse(counter.exists())
                self.assertEqual(token.calls, 0)
                self.assertTrue(journal.read(selected, hashlib.sha256(raw).hexdigest()))
                self.assertIsNone(journal.outcome(selected, hashlib.sha256(raw).hexdigest()))

    def test_token_lookup_cannot_carry_stale_authority_into_post(self):
        for kind in ("expiry", "revocation"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                def unused(f, expected):
                    return f.event("POST", "repos/FS-GG/disposable/pulls",
                                   body=coordinator.operator.pull_request_body(expected),
                                   status=201, value={})
                clock = MutableClock(NOW)
                def token_factory(keys, clock):
                    if kind == "expiry":
                        return ChangingToken(lambda: setattr(
                            clock, "value", NOW + dt.timedelta(minutes=11)))
                    return ChangingToken(lambda: setattr(keys, "revoked", True))
                first, selected, expected, raw, key, reader, counter, token, journal = \
                    self._scenario(temp, unused, keys_type=MutableKeys,
                                   clock=clock, token_factory=token_factory)
                self.assertIsInstance(first, coordinator.operator.Unknown)
                self.assertEqual(token.calls, 1)
                self.assertFalse(counter.exists())
                self.assertTrue(journal.read(selected, hashlib.sha256(raw).hexdigest()))
                self.assertIsNone(journal.outcome(selected, hashlib.sha256(raw).hexdigest()))

    def test_final_key_lookup_cannot_hide_expiry_or_revocation(self):
        for kind in ("expiry", "revocation"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                clock = MutableClock(NOW)
                class DelayedKeys(Keys):
                    def active_public_key(self, key_id, issuer_actor_id, now):
                        self.calls += 1
                        if kind == "expiry" and self.calls == 4:
                            clock.value = NOW + dt.timedelta(minutes=11)
                        if kind == "revocation" and self.calls == 5:
                            return None
                        return self.key
                def unused(f, expected):
                    return f.event("POST", "repos/FS-GG/disposable/pulls",
                                   body=coordinator.operator.pull_request_body(expected),
                                   status=201, value={})
                first, selected, expected, raw, key, reader, counter, token, journal = \
                    self._scenario(temp, unused, keys_type=DelayedKeys, clock=clock)
                self.assertIsInstance(first, coordinator.operator.Unknown)
                self.assertEqual(token.calls, 1)
                self.assertFalse(counter.exists())
                self.assertIsNone(journal.outcome(selected, hashlib.sha256(raw).hexdigest()))

    def test_one_post_and_recovery_only_reads(self):
        import importlib.util
        fixture_path = ROOT / "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
        spec = importlib.util.spec_from_file_location("retained_fixtures", fixture_path)
        fixtures = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = fixtures
        spec.loader.exec_module(fixtures)
        op = coordinator.operator
        expected = op.ExpectedPull(op.OPERATION_IDENTITY, 1, 44, "FS-GG/disposable",
                                   "refs/heads/source", fixtures.SHA_A,
                                   "refs/heads/main", fixtures.SHA_B)
        pre = op.NativeReadAdapter(op.OfflineTranscriptTransport(fixtures.pull_read_events()))
        census = pre.read_pull_census(expected)
        selected = dataclasses.replace(binding(), target_repository=expected.repository,
            target_repository_id=44,
            target_prestate_sha256=contracts.digest(grant.canonical(dataclasses.asdict(census))),
            canonical_request=json.dumps(op.pull_request_body(expected), sort_keys=True,
                separators=(",", ":"), ensure_ascii=True).encode())
        selected = dataclasses.replace(selected,
            request_sha256=contracts.digest(selected.canonical_request))
        with tempfile.TemporaryDirectory() as temp:
            claims = {"schema": grant.SCHEMA, "audience": grant.AUDIENCE,
                      "algorithm": "Ed25519", "keyId": "", "issuedAt": "2026-09-27T11:59:00Z",
                      "expiresAt": "2026-09-27T12:10:00Z", **selected.claims()}
            key, raw = sign(temp, claims)
            pull = fixtures.pull_observed().pulls[0]
            # Four absent reads: S1 pin and retained run_pull_once each read twice.
            post = fixtures.pull_read_events((pull,))
            events = (fixtures.pull_read_events() * 4 +
                      [fixtures.event("POST", "repos/FS-GG/disposable/pulls",
                                      body=op.pull_request_body(expected),
                                      error="lost response")] + post * 2 + post * 2)
            transport = op.OfflineTranscriptTransport(events)
            reader = ScriptedRead(transport)
            counter = pathlib.Path(temp) / "posts"
            writer = ScriptedWrite(transport, counter)
            token = ScriptedToken()
            journal = coordinator.AttemptJournal(pathlib.Path(temp) / "attempt.db")
            self.assertTrue(journal.establish_parent(selected))
            first = coordinator.execute_pull(selected, expected, raw, Keys(key), reader,
                                             writer, token, journal, FixedClock(NOW, NOW, NOW))
            self.assertIsInstance(first, op.ExactPull)
            self.assertEqual(counter.read_text(), "POST\n")
            self.assertEqual(token.calls, 1)
            recovered = coordinator.recover_pull(selected, expected, raw, Keys(key),
                                                 reader, journal, NOW)
            self.assertIsInstance(recovered, op.ExactPull)
            self.assertEqual(counter.read_text(), "POST\n")
            self.assertEqual(token.calls, 1)


if __name__ == "__main__":
    unittest.main()
