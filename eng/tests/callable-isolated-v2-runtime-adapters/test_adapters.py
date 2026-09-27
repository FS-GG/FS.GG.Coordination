"""Focused qualification tests for the protected S2 runtime adapters."""
from __future__ import annotations

import base64
import dataclasses
import datetime as dt
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import threading
from concurrent.futures import ThreadPoolExecutor
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))

from callable_isolated_v2_runtime import adapters, contracts, coordinator, grant


NOW = dt.datetime(2026, 9, 27, 12, 0, tzinfo=dt.timezone.utc)


def provenance(authority_id, role, index):
    return adapters.RecordProvenance(authority_id, role, f"principal-{role}",
        f"{index:064x}", f"record-{role}", NOW - dt.timedelta(minutes=1),
        NOW + dt.timedelta(minutes=10))


class Authority:
    def __init__(self, root: pathlib.Path):
        self.authority_id = "a" * 64
        self.now = NOW
        private_key = root / "private.pem"
        public_der = root / "public.der"
        subprocess.run(["openssl", "genpkey", "-algorithm", "Ed25519",
                        "-out", str(private_key)], check=True,
                       capture_output=True)
        subprocess.run(["openssl", "pkey", "-in", str(private_key),
                        "-pubout", "-outform", "DER", "-out",
                        str(public_der)], check=True, capture_output=True)
        self.public_key = public_der.read_bytes()[-32:]
        self.key_id = hashlib.sha256(self.public_key).hexdigest()
        expected = coordinator.operator.ExpectedPull(
            coordinator.operator.OPERATION_IDENTITY, 1, 42, "FS-GG/target",
            "refs/heads/source", "1" * 40,
            "refs/heads/main", "2" * 40)
        request = json.dumps(coordinator.operator.pull_request_body(expected),
            sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")
        self.binding = contracts.Binding(
            "3" * 40, "4" * 40, "5" * 64, "6" * 64,
            expected.repository, expected.repository_id, "7" * 64,
            contracts.digest(request), 101, 2, 103, 104,
            "FS-GG/.github", "refs/heads/main", "journal/runtime.json",
            9, "8" * 40, "operation-1", request)
        self.binding_sha = contracts.digest(grant.canonical(self.binding.claims()))
        payload = {"schema": grant.SCHEMA, "audience": grant.AUDIENCE,
            "algorithm": "Ed25519", "keyId": self.key_id,
            "issuedAt": "2026-09-27T11:59:00Z",
            "expiresAt": "2026-09-27T12:10:00Z", **self.binding.claims()}
        payload_path = root / "payload"
        signature_path = root / "signature"
        payload_path.write_bytes(grant.canonical(payload))
        subprocess.run(["openssl", "pkeyutl", "-sign", "-rawin",
                        "-inkey", str(private_key), "-in", str(payload_path),
                        "-out", str(signature_path)], check=True,
                       capture_output=True)
        self.raw_grant = grant.canonical({"schema": grant.SCHEMA,
            "payload": payload, "signature": base64.b64encode(
                signature_path.read_bytes()).decode("ascii")})
        scopes = tuple(adapters.ProtectedScope(
            self.authority_id, role, f"principal-{role}", f"{index:064x}",
            adapters.API_ORIGIN, expected.repository, expected.repository_id,
            77, permissions, NOW + dt.timedelta(minutes=10))
            for index, (role, permissions) in enumerate(
                adapters.ROLE_PERMISSIONS.items(), 1))
        self.scopes = {item.role: item for item in scopes}
        self.configuration = adapters.InstalledConfiguration(
            adapters.CONFIG_SCHEMA, True, True, self.authority_id,
            adapters.API_ORIGIN, 77, self.binding.source_revision,
            self.binding.source_tree, expected.source_ref, expected.base_ref,
            "", self.binding, expected, scopes,
            provenance(self.authority_id, "configuration", 1))
        self.grant_record = adapters.GrantRecord(
            self.binding.operation_id, self.binding_sha,
            contracts.digest(self.raw_grant), self.key_id, 104, 101, 2,
            self.raw_grant, provenance(self.authority_id, "grant", 2))
        self.issuer = adapters.IssuerRecord(
            self.binding.operation_id, self.binding_sha,
            self.grant_record.grant_sha256, self.key_id, 104, 101, 2,
            NOW - dt.timedelta(minutes=2), NOW + dt.timedelta(minutes=10),
            True, provenance(self.authority_id, "issuer", 3))
        self.key = adapters.KeyRecord(self.key_id, 104, self.public_key,
            True, None, provenance(self.authority_id, "key", 4))
        self.parent = adapters.ParentRecord(
            self.binding.journal_repository, self.binding.journal_ref,
            self.binding.journal_path, self.binding.journal_prior_generation,
            self.binding.journal_prior_head, self.binding_sha,
            provenance(self.authority_id, "parent", 5))
        self.token = adapters.TokenLease(
            self.binding.operation_id, self.binding.target_repository,
            self.binding.target_repository_id, 77, "e" * 64, b"secret",
            NOW - dt.timedelta(minutes=1), NOW + dt.timedelta(minutes=5),
            provenance(self.authority_id, "token", 9))
        self.gets = []
        self.posts = []
        self.token_calls = 0
        self.attempt = None
        self.journal_lock = threading.Lock()

    def scope(self, role): return self.scopes[role]
    def read_installed_configuration(self): return self.configuration
    def read_grant(self, operation_id): return self.grant_record
    def read_issuer(self, operation_id): return self.issuer
    def read_active_key(self, key_id, issuer_actor_id): return self.key
    def read_parent(self, repository, ref, path): return self.parent
    def reserve_attempt(self, binding, grant_sha256):
        with self.journal_lock:
            if self.attempt is not None or binding != self.binding:
                return False
            self.attempt = adapters.CommittedAttempt(
                binding.operation_id, binding.journal_repository,
                binding.journal_ref, binding.journal_path,
                binding.journal_prior_generation, binding.journal_prior_head,
                self.binding_sha, grant_sha256, True, None,
                provenance(self.authority_id, "journal", 6))
            return True
    def read_committed_attempt(self, operation_id):
        with self.journal_lock:
            return self.attempt
    def persist_attempt_outcome(self, operation_id, binding_sha256,
                                grant_sha256, outcome):
        with self.journal_lock:
            if (self.attempt is None or self.attempt.operation_id != operation_id
                    or self.attempt.binding_sha256 != binding_sha256
                    or self.attempt.grant_sha256 != grant_sha256
                    or self.attempt.outcome is not None):
                return False
            self.attempt = dataclasses.replace(self.attempt, outcome=outcome)
            return True
    def trusted_now(self): return self.now
    def native_get(self, path):
        self.gets.append(path)
        return coordinator.operator.HttpResponse(200, (), b"{}")
    def issue_execution_token(self, operation_id):
        self.token_calls += 1
        return self.token
    def native_post(self, path, body, token):
        self.posts.append((path, body, token))
        return coordinator.operator.HttpResponse(201, (), b"{}")


class ProtectedAdapterTests(unittest.TestCase):
    def test_installed_requires_host_journal_and_rejects_local_path(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            authority.configuration = dataclasses.replace(
                authority.configuration, journal_database_path=str(
                    pathlib.Path(temp) / "runtime.db"))
            self.assertIsInstance(adapters.execute_installed(authority),
                                  coordinator.operator.Unknown)
            self.assertFalse((pathlib.Path(temp) / "runtime.db").exists())
            authority.configuration = dataclasses.replace(
                authority.configuration, journal_database_path="")
            authority.reserve_attempt = None
            self.assertIsInstance(adapters.execute_installed(authority),
                                  coordinator.operator.Unknown)
            self.assertEqual(authority.posts, [])

    def test_concurrent_host_reservations_admit_only_one(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            journals = [adapters.compose_installed(authority).journal
                        for _ in range(12)]
            with ThreadPoolExecutor(max_workers=12) as pool:
                admitted = list(pool.map(lambda journal: journal.reserve(
                    authority.binding, authority.grant_record.grant_sha256),
                    journals))
            self.assertEqual(admitted.count(True), 1)
            self.assertEqual(admitted.count(False), 11)
            self.assertTrue(all(journal.read(authority.binding,
                authority.grant_record.grant_sha256) for journal in journals))
            self.assertEqual(authority.posts, [])

    def test_missing_or_mismatched_committed_replay_never_mints_token(self):
        for mode in ("missing", "binding", "grant"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                authority = Authority(pathlib.Path(temp))
                runtime = adapters.compose_installed(authority)
                if mode == "missing":
                    authority.read_committed_attempt = lambda _operation: None
                elif mode == "binding":
                    original = authority.read_committed_attempt
                    authority.read_committed_attempt = lambda operation: (
                        dataclasses.replace(original(operation),
                            binding_sha256="f" * 64))
                else:
                    original = authority.read_committed_attempt
                    authority.read_committed_attempt = lambda operation: (
                        dataclasses.replace(original(operation),
                            grant_sha256="f" * 64))
                def attempt(expected, transport, reserve):
                    if not reserve("key"):
                        return coordinator.operator.Unknown("reserve-refused")
                    return transport.request("POST", f"repos/{expected.repository}/pulls",
                        coordinator.operator.pull_request_body(expected))
                with (mock.patch.object(coordinator, "_prestate_matches", return_value=True),
                      mock.patch.object(coordinator.operator, "run_pull_once", side_effect=attempt)):
                    result = coordinator.execute_pull(runtime.binding, runtime.expected,
                        runtime.raw_grant, runtime.key_reader, runtime.read_port,
                        runtime.write_port, runtime.token_port, runtime.journal,
                        runtime.clock_port)
                self.assertIsInstance(result, coordinator.operator.Unknown)
                self.assertEqual(authority.posts, [])
                self.assertIsNone(runtime.token_port._lease)

    def test_unacknowledged_outcome_keeps_fence_and_recovery_read_only(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            authority.persist_attempt_outcome = lambda *_args: False
            def attempt(expected, transport, reserve):
                if not reserve("key"):
                    return coordinator.operator.Unknown("reserve-refused")
                return transport.request("POST", f"repos/{expected.repository}/pulls",
                    coordinator.operator.pull_request_body(expected))
            with (mock.patch.object(coordinator, "_prestate_matches", return_value=True),
                  mock.patch.object(coordinator.operator, "run_pull_once", side_effect=attempt)):
                result = coordinator.execute_pull(runtime.binding, runtime.expected,
                    runtime.raw_grant, runtime.key_reader, runtime.read_port,
                    runtime.write_port, runtime.token_port, runtime.journal,
                    runtime.clock_port)
            self.assertIsInstance(result, coordinator.operator.Unknown)
            self.assertEqual(len(authority.posts), 1)
            self.assertIsNotNone(authority.attempt)
            self.assertIsNone(authority.attempt.outcome)
            self.assertFalse(runtime.journal.reserve(
                runtime.binding, authority.grant_record.grant_sha256))
            recovered = adapters.recover_installed(authority)
            self.assertIsInstance(recovered, coordinator.operator.Unknown)
            self.assertEqual(len(authority.posts), 1)
            self.assertEqual(authority.token_calls, 1)

    def test_lost_response_keeps_committed_fence_and_recovery_has_no_token(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            def lost_post(path, body, token):
                authority.posts.append((path, body, token))
                raise OSError("lost after send")
            authority.native_post = lost_post
            def attempt(expected, transport, reserve):
                if not reserve("key"):
                    return coordinator.operator.Unknown("reserve-refused")
                return transport.request("POST", f"repos/{expected.repository}/pulls",
                    coordinator.operator.pull_request_body(expected))
            with (mock.patch.object(coordinator, "_prestate_matches", return_value=True),
                  mock.patch.object(coordinator.operator, "run_pull_once", side_effect=attempt)):
                result = coordinator.execute_pull(runtime.binding, runtime.expected,
                    runtime.raw_grant, runtime.key_reader, runtime.read_port,
                    runtime.write_port, runtime.token_port, runtime.journal,
                    runtime.clock_port)
            self.assertIsInstance(result, coordinator.operator.Unknown)
            self.assertEqual(runtime.journal.outcome(runtime.binding,
                authority.grant_record.grant_sha256), {"status": "lost"})
            self.assertIsInstance(adapters.recover_installed(authority),
                                  coordinator.operator.Unknown)
            self.assertEqual(len(authority.posts), 1)
            self.assertEqual(authority.token_calls, 1)
            self.assertFalse(runtime.journal.reserve(runtime.binding,
                authority.grant_record.grant_sha256))

    def test_compose_uses_stored_parent_and_separate_scoped_ports(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            self.assertEqual(runtime.binding, authority.binding)
            self.assertEqual(runtime.raw_grant, authority.raw_grant)
            self.assertEqual(runtime.key_reader.active_public_key(
                authority.key_id, 104, NOW), authority.public_key)
            response = runtime.read_port.request(
                "GET", "repos/FS-GG/target", None)
            self.assertEqual(response.status, 200)
            token = runtime.token_port.read_execution_token()
            response = runtime.write_port.post_pull(
                "repos/FS-GG/target/pulls",
                authority.binding.canonical_request, token)
            self.assertEqual(response.status, 201)
            self.assertEqual(len(authority.posts), 1)
            self.assertIsNot(runtime.read_port, runtime.write_port)
            self.assertIsNot(runtime.write_port, runtime.token_port)
            self.assertTrue(runtime.journal.reserve(
                authority.binding, authority.grant_record.grant_sha256))

    def test_parent_missing_or_drifting_fails_before_token_or_provider(self):
        for mode in ("missing", "drift"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                authority = Authority(pathlib.Path(temp))
                if mode == "missing":
                    def unavailable(*_args): raise LookupError()
                    authority.read_parent = unavailable
                else:
                    calls = 0
                    def drifting(*_args):
                        nonlocal calls
                        calls += 1
                        if calls == 1:
                            return authority.parent
                        return dataclasses.replace(authority.parent, generation=10)
                    authority.read_parent = drifting
                with self.assertRaises(adapters.Refused):
                    adapters.compose_installed(authority)
                self.assertEqual(authority.posts, [])
                self.assertEqual(authority.gets, [])
                self.assertIsNone(authority.attempt)

    def test_role_scope_drift_and_role_reuse_fail_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            authority.scopes["parent"] = dataclasses.replace(
                authority.scopes["parent"], api_origin="https://example.invalid")
            with self.assertRaises(adapters.Refused):
                adapters.compose_installed(authority)
            self.assertEqual(authority.posts, [])
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            scopes = list(authority.configuration.scopes)
            scopes[-1] = dataclasses.replace(scopes[-1],
                principal_id=scopes[-2].principal_id)
            authority.configuration = dataclasses.replace(
                authority.configuration, scopes=tuple(scopes))
            authority.scopes = {scope.role: scope for scope in scopes}
            with self.assertRaises(adapters.Refused):
                adapters.compose_installed(authority)

    def test_no_authority_and_caller_selected_token_or_target_are_refused(self):
        result = adapters.execute_installed(None)
        self.assertIsInstance(result, coordinator.operator.Unknown)
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            with self.assertRaises(adapters.Refused):
                runtime.read_port.request("GET", "repos/FS-GG/other", None)
            with self.assertRaises(adapters.Refused):
                runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                    authority.binding.canonical_request, b"caller-token")
            self.assertEqual(authority.posts, [])

    def test_protected_key_revocation_is_checked_on_each_lookup(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            self.assertEqual(runtime.key_reader.active_public_key(
                authority.key_id, 104, NOW), authority.public_key)
            authority.key = dataclasses.replace(authority.key,
                active=False, revoked_at=NOW)
            with self.assertRaises(adapters.Refused):
                runtime.key_reader.active_public_key(authority.key_id, 104, NOW)

    def test_issuer_expiring_during_key_read_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            authority.issuer = dataclasses.replace(authority.issuer,
                expires_at=NOW + dt.timedelta(seconds=1))

            def delayed_key_read(key_id, issuer_actor_id):
                authority.now = authority.issuer.expires_at
                return authority.key

            authority.read_active_key = delayed_key_read
            with self.assertRaisesRegex(adapters.Refused, "protected-key-invalid"):
                runtime.key_reader.active_public_key(authority.key_id, 104, NOW)

    def test_invalid_grant_does_not_poison_corrected_parent_bootstrap(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            valid_grant = authority.raw_grant
            valid_record = authority.grant_record
            valid_issuer = authority.issuer
            envelope = json.loads(valid_grant)
            envelope["signature"] = base64.b64encode(bytes(64)).decode("ascii")
            invalid = grant.canonical(envelope)
            authority.raw_grant = invalid
            authority.grant_record = dataclasses.replace(valid_record,
                raw_grant=invalid, grant_sha256=contracts.digest(invalid))
            authority.issuer = dataclasses.replace(valid_issuer,
                grant_sha256=contracts.digest(invalid))
            with self.assertRaises(adapters.Refused):
                adapters.compose_installed(authority)
            self.assertIsNone(authority.attempt)
            authority.raw_grant = valid_grant
            authority.grant_record = valid_record
            authority.issuer = valid_issuer
            runtime = adapters.compose_installed(authority)
            self.assertEqual(runtime.raw_grant, valid_grant)

    def test_expired_issuer_refuses_before_parent_bootstrap(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            authority.issuer = dataclasses.replace(
                authority.issuer, expires_at=NOW)
            with self.assertRaises(adapters.Refused):
                adapters.compose_installed(authority)
            self.assertIsNone(authority.attempt)

    def test_parent_expiring_during_authority_read_refuses_bootstrap(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            authority.parent = dataclasses.replace(authority.parent,
                provenance=dataclasses.replace(authority.parent.provenance,
                    expires_at=NOW + dt.timedelta(seconds=30)))
            original_read = authority.read_parent
            reads = 0

            def delayed_second_read(repository, ref, path):
                nonlocal reads
                reads += 1
                value = original_read(repository, ref, path)
                if reads == 2:
                    authority.now = NOW + dt.timedelta(seconds=31)
                return value

            authority.read_parent = delayed_second_read
            with self.assertRaises(adapters.Refused):
                adapters.compose_installed(authority)
            self.assertEqual(reads, 2)
            self.assertIsNone(authority.attempt)

    def test_recovery_composes_only_from_existing_qualified_parent(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            adapters.compose_installed(authority)
            recovered = adapters.compose_recovery(authority)
            self.assertEqual(recovered.binding, authority.binding)
            self.assertEqual(recovered.raw_grant, authority.raw_grant)
            self.assertEqual(authority.posts, [])
            result = adapters.recover_installed(authority)
            self.assertIsInstance(result, coordinator.operator.Unknown)
            self.assertEqual(authority.posts, [])
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            adapters.compose_recovery(authority)
            self.assertIsNone(authority.attempt)

    def test_write_edge_rechecks_key_after_token_acquisition(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            token = runtime.token_port.read_execution_token()
            authority.key = dataclasses.replace(authority.key,
                active=False, revoked_at=NOW)
            with self.assertRaises(adapters.Refused):
                runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                    authority.binding.canonical_request, token)
            self.assertEqual(authority.posts, [])

    def test_blocking_write_scope_expiry_race_has_zero_posts(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)
            token = runtime.token_port.read_execution_token()
            original_scope = authority.scope
            advanced = False

            def blocking_scope(role):
                nonlocal advanced
                value = original_scope(role)
                if role == "native-write" and not advanced:
                    advanced = True
                    authority.now = NOW + dt.timedelta(minutes=11)
                return value

            authority.scope = blocking_scope
            with self.assertRaises(adapters.Refused):
                runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                    authority.binding.canonical_request, token)
            self.assertEqual(authority.posts, [])

    def test_slow_final_key_lookup_cannot_outlive_retained_write_scope(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            scopes = list(authority.configuration.scopes)
            index = next(index for index, scope in enumerate(scopes)
                         if scope.role == "native-write")
            scopes[index] = dataclasses.replace(scopes[index],
                expires_at=NOW + dt.timedelta(seconds=30))
            authority.configuration = dataclasses.replace(
                authority.configuration, scopes=tuple(scopes))
            authority.scopes = {scope.role: scope for scope in scopes}
            runtime = adapters.compose_installed(authority)
            token = runtime.token_port.read_execution_token()
            original_key_read = authority.read_active_key

            def slow_key_read(key_id, issuer_actor_id):
                value = original_key_read(key_id, issuer_actor_id)
                authority.now = NOW + dt.timedelta(minutes=1)
                return value

            authority.read_active_key = slow_key_read
            with self.assertRaises(adapters.Refused):
                runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                    authority.binding.canonical_request, token)
            self.assertEqual(authority.posts, [])

    def test_final_key_lookup_refuses_expired_record_provenance(self):
        for role in ("issuer", "key"):
            with self.subTest(role=role), tempfile.TemporaryDirectory() as temp:
                authority = Authority(pathlib.Path(temp))
                runtime = adapters.compose_installed(authority)
                token = runtime.token_port.read_execution_token()
                record = getattr(authority, role)
                setattr(authority, role, dataclasses.replace(record,
                    provenance=dataclasses.replace(record.provenance,
                        expires_at=NOW + dt.timedelta(seconds=30))))
                original_key_read = authority.read_active_key
                reads = 0

                def slow_second_key_read(key_id, issuer_actor_id):
                    nonlocal reads
                    reads += 1
                    value = original_key_read(key_id, issuer_actor_id)
                    if reads == 2:
                        authority.now = NOW + dt.timedelta(seconds=31)
                    return value

                authority.read_active_key = slow_second_key_read
                with self.assertRaisesRegex(adapters.Refused,
                                            "protected-write-authority"):
                    runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                        authority.binding.canonical_request, token)
                self.assertEqual(reads, 2)
                self.assertEqual(authority.posts, [])

    def test_final_clock_sample_refuses_expired_send_authority(self):
        deadlines = ("issuer", "issuer-provenance", "key-provenance",
                     "issuer-scope", "key-scope", "configuration-provenance",
                     "grant-provenance", "token-provenance",
                     "parent-provenance")
        expires_at = NOW + dt.timedelta(seconds=30)
        for deadline in deadlines:
            with self.subTest(deadline=deadline), tempfile.TemporaryDirectory() as temp:
                authority = Authority(pathlib.Path(temp))
                if deadline == "issuer":
                    authority.issuer = dataclasses.replace(authority.issuer,
                        expires_at=expires_at)
                elif deadline.endswith("-scope"):
                    role = deadline.removesuffix("-scope")
                    scopes = tuple(dataclasses.replace(scope,
                        expires_at=expires_at) if scope.role == role else scope
                        for scope in authority.configuration.scopes)
                    authority.configuration = dataclasses.replace(
                        authority.configuration, scopes=scopes)
                    authority.scopes = {scope.role: scope for scope in scopes}
                else:
                    role = deadline.removesuffix("-provenance")
                    name = {"configuration": "configuration",
                            "grant": "grant_record", "token": "token"}.get(role, role)
                    record = getattr(authority, name)
                    setattr(authority, name, dataclasses.replace(record,
                        provenance=dataclasses.replace(record.provenance,
                            expires_at=expires_at)))
                runtime = adapters.compose_installed(authority)
                token = runtime.token_port.read_execution_token()
                original_key_lookup = runtime.key_reader.active_public_key
                lookups = 0

                def advance_after_second_lookup(key_id, issuer_actor_id, now):
                    nonlocal lookups
                    value = original_key_lookup(key_id, issuer_actor_id, now)
                    lookups += 1
                    if lookups == 2:
                        authority.now = expires_at + dt.timedelta(seconds=1)
                    return value

                runtime.key_reader.active_public_key = advance_after_second_lookup
                with self.assertRaises(adapters.Refused):
                    runtime.write_port.post_pull("repos/FS-GG/target/pulls",
                        authority.binding.canonical_request, token)
                self.assertEqual(lookups, 2)
                self.assertEqual(authority.posts, [])

    def test_known_401_survives_post_scope_drift_and_cannot_be_exact(self):
        with tempfile.TemporaryDirectory() as temp:
            authority = Authority(pathlib.Path(temp))
            runtime = adapters.compose_installed(authority)

            def post_then_drift(path, body, token):
                authority.posts.append((path, body, token))
                authority.scopes["native-write"] = dataclasses.replace(
                    authority.scopes["native-write"],
                    expires_at=NOW)
                return coordinator.operator.HttpResponse(401, (), b"refused")

            authority.native_post = post_then_drift

            def synthetic_exact(expected, transport, reserve):
                self.assertTrue(reserve("retained-key"))
                response = transport.request("POST",
                    f"repos/{expected.repository}/pulls",
                    coordinator.operator.pull_request_body(expected))
                self.assertEqual(response.status, 401)
                return coordinator.operator.ExactPull(9, "PR_9", "f" * 64)

            with (mock.patch.object(coordinator, "_prestate_matches",
                                    return_value=True),
                  mock.patch.object(coordinator.operator, "run_pull_once",
                                    side_effect=synthetic_exact)):
                result = coordinator.execute_pull(runtime.binding,
                    runtime.expected, runtime.raw_grant, runtime.key_reader,
                    runtime.read_port, runtime.write_port,
                    runtime.token_port, runtime.journal, runtime.clock_port)
            self.assertNotIsInstance(result, coordinator.operator.ExactPull)
            self.assertEqual(len(authority.posts), 1)
            outcome = runtime.journal.outcome(
                runtime.binding, authority.grant_record.grant_sha256)
            self.assertIsInstance(outcome, coordinator.operator.HttpResponse)
            self.assertEqual(outcome.status, 401)


if __name__ == "__main__":
    unittest.main()
