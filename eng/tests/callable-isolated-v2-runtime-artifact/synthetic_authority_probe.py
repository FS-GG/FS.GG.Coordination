#!/usr/bin/env python3
"""Offline installed-runtime probe; imports runtime code only from the pyz."""

from __future__ import annotations

import base64
import dataclasses
import datetime as dt
import hashlib
import json
import pathlib
import subprocess
import sys
import threading


PYZ = pathlib.Path(sys.argv[1]).resolve()
STATE = pathlib.Path(sys.argv[2]).resolve()
sys.path[:] = [str(PYZ), *[item for item in sys.path if item]]

from callable_isolated_v2_runtime import adapters, contracts, coordinator, grant


NOW = dt.datetime(2026, 9, 27, 12, 0, tzinfo=dt.timezone.utc)
SHA_A = "a" * 40
SHA_B = "b" * 40


def provenance(authority_id: str, role: str, index: int):
    return adapters.RecordProvenance(
        authority_id, role, f"principal-{role}", f"{index:064x}",
        f"record-{role}", NOW - dt.timedelta(minutes=1),
        NOW + dt.timedelta(minutes=10),
    )


class ReadPort:
    def __init__(self, authority):
        self.authority = authority

    def request(self, method, path, body):
        if method != "GET" or body is not None:
            raise AssertionError("read-only-prestate")
        return self.authority.native_get(path)


class Authority:
    def __init__(self):
        self.authority_id = "9" * 64
        self.posts = 0
        self.gets = 0
        self.applied = False
        self.attempt = None
        self.journal_lock = threading.Lock()
        self.expected = coordinator.operator.ExpectedPull(
            coordinator.operator.OPERATION_IDENTITY, 1, 44,
            "FS-GG/disposable", "refs/heads/source", SHA_A,
            "refs/heads/main", SHA_B,
        )
        request = json.dumps(
            coordinator.operator.pull_request_body(self.expected),
            sort_keys=True, separators=(",", ":"), ensure_ascii=True,
        ).encode("ascii")
        census = coordinator.operator.NativeReadAdapter(
            ReadPort(self)).read_pull_census(self.expected)
        self.gets = 0
        self.binding = contracts.Binding(
            "3" * 40, "4" * 40, "5" * 64, "6" * 64,
            self.expected.repository, self.expected.repository_id,
            contracts.digest(grant.canonical(dataclasses.asdict(census))),
            contracts.digest(request), 101, 2, 103, 104,
            "FS-GG/.github", "refs/heads/main", "journal/runtime.json",
            9, "8" * 40, "operation-1", request,
        )
        self.binding_sha = contracts.digest(
            grant.canonical(self.binding.claims()))
        private_key = STATE / "private.pem"
        public_der = STATE / "public.der"
        subprocess.run(
            ["openssl", "genpkey", "-algorithm", "Ed25519", "-out",
             str(private_key)], check=True, capture_output=True)
        subprocess.run(
            ["openssl", "pkey", "-in", str(private_key), "-pubout",
             "-outform", "DER", "-out", str(public_der)], check=True,
            capture_output=True)
        self.public_key = public_der.read_bytes()[-32:]
        self.key_id = hashlib.sha256(self.public_key).hexdigest()
        payload = {
            "schema": grant.SCHEMA, "audience": grant.AUDIENCE,
            "algorithm": "Ed25519", "keyId": self.key_id,
            "issuedAt": "2026-09-27T11:59:00Z",
            "expiresAt": "2026-09-27T12:10:00Z",
            **self.binding.claims(),
        }
        payload_path = STATE / "payload"
        signature_path = STATE / "signature"
        payload_path.write_bytes(grant.canonical(payload))
        subprocess.run(
            ["openssl", "pkeyutl", "-sign", "-rawin", "-inkey",
             str(private_key), "-in", str(payload_path), "-out",
             str(signature_path)], check=True, capture_output=True)
        self.raw_grant = grant.canonical({
            "schema": grant.SCHEMA, "payload": payload,
            "signature": base64.b64encode(
                signature_path.read_bytes()).decode("ascii"),
        })
        scopes = tuple(
            adapters.ProtectedScope(
                self.authority_id, role, f"principal-{role}", f"{index:064x}",
                adapters.API_ORIGIN, self.expected.repository,
                self.expected.repository_id, 77, permissions,
                NOW + dt.timedelta(minutes=10),
            )
            for index, (role, permissions) in enumerate(
                adapters.ROLE_PERMISSIONS.items(), 1)
        )
        self.scopes = {scope.role: scope for scope in scopes}
        self.configuration = adapters.InstalledConfiguration(
            adapters.CONFIG_SCHEMA, True, True, self.authority_id,
            adapters.API_ORIGIN, 77, self.binding.source_revision,
            self.binding.source_tree, self.expected.source_ref,
            self.expected.base_ref, "", self.binding,
            self.expected, scopes,
            provenance(self.authority_id, "configuration", 1),
        )
        grant_sha = contracts.digest(self.raw_grant)
        self.grant_record = adapters.GrantRecord(
            self.binding.operation_id, self.binding_sha, grant_sha,
            self.key_id, 104, 101, 2, self.raw_grant,
            provenance(self.authority_id, "grant", 2),
        )
        self.issuer = adapters.IssuerRecord(
            self.binding.operation_id, self.binding_sha, grant_sha,
            self.key_id, 104, 101, 2, NOW - dt.timedelta(minutes=2),
            NOW + dt.timedelta(minutes=10), True,
            provenance(self.authority_id, "issuer", 3),
        )
        self.key = adapters.KeyRecord(
            self.key_id, 104, self.public_key, True, None,
            provenance(self.authority_id, "key", 4),
        )
        self.parent = adapters.ParentRecord(
            self.binding.journal_repository, self.binding.journal_ref,
            self.binding.journal_path, self.binding.journal_prior_generation,
            self.binding.journal_prior_head, self.binding_sha,
            provenance(self.authority_id, "parent", 5),
        )
        self.token = adapters.TokenLease(
            self.binding.operation_id, self.binding.target_repository,
            self.binding.target_repository_id, 77, "e" * 64, b"secret",
            NOW - dt.timedelta(minutes=1), NOW + dt.timedelta(minutes=5),
            provenance(self.authority_id, "token", 9),
        )

    def scope(self, role):
        return self.scopes[role]

    def read_installed_configuration(self):
        return self.configuration

    def read_grant(self, operation_id):
        return self.grant_record

    def read_issuer(self, operation_id):
        return self.issuer

    def read_active_key(self, key_id, issuer_actor_id):
        return self.key

    def read_parent(self, repository, ref, path):
        return self.parent

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

    def trusted_now(self):
        return NOW

    def _pull(self):
        body = coordinator.operator.pull_request_body(self.expected)
        return {
            "number": 8, "node_id": "PR_8", "state": "open",
            "draft": False, "merged": False,
            "url": "https://api.github.com/repos/FS-GG/disposable/pulls/8",
            "title": coordinator.operator.PULL_TITLE, "body": body["body"],
            "head": {"ref": "source", "sha": SHA_A,
                     "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
            "base": {"ref": "main", "sha": SHA_B,
                     "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
        }

    def native_get(self, path):
        self.gets += 1
        prefix = "repos/FS-GG/disposable"
        if path == prefix:
            value = {"id": 44, "full_name": "FS-GG/disposable"}
        elif path == f"{prefix}/git/ref/heads/source":
            value = {"ref": "refs/heads/source", "url":
                f"https://api.github.com/{prefix}/git/refs/heads/source",
                "object": {"type": "commit", "sha": SHA_A, "url":
                    f"https://api.github.com/{prefix}/git/commits/{SHA_A}"}}
        elif path == f"{prefix}/git/ref/heads/main":
            value = {"ref": "refs/heads/main", "url":
                f"https://api.github.com/{prefix}/git/refs/heads/main",
                "object": {"type": "commit", "sha": SHA_B, "url":
                    f"https://api.github.com/{prefix}/git/commits/{SHA_B}"}}
        elif path == f"{prefix}/pulls?state=open&per_page=100&page=1":
            value = [self._pull()] if self.applied else []
        elif path == f"{prefix}/pulls?state=open&per_page=100&page=2":
            value = []
        elif path == f"{prefix}/pulls/8" and self.applied:
            value = self._pull()
        else:
            raise AssertionError(f"unexpected read path: {path}")
        return coordinator.operator.HttpResponse(
            200, (), json.dumps(value, separators=(",", ":")).encode("ascii"))

    def issue_execution_token(self, operation_id):
        return self.token

    def native_post(self, path, body, token):
        self.posts += 1
        if self.posts > 1:
            raise AssertionError("more than one POST")
        self.applied = True
        response = self._pull()
        return coordinator.operator.HttpResponse(
            201, (), json.dumps(response).encode("ascii"))


authority = Authority()
first = adapters.execute_installed(authority)
if not isinstance(first, coordinator.operator.ExactPull) or authority.posts != 1:
    raise SystemExit(
        f"installed execution did not prove one exact POST: "
        f"result={first!r} posts={authority.posts} gets={authority.gets}")
reads_after_execution = authority.gets
recovered = adapters.recover_installed(authority)
if (not isinstance(recovered, coordinator.operator.ExactPull)
        or authority.posts != 1 or authority.gets <= reads_after_execution):
    raise SystemExit("installed recovery was not read-only")
print(f"OFFLINE_INSTALLED_RUNTIME_OK posts={authority.posts} gets={authority.gets}")
