#!/usr/bin/env python3
"""Fake-provider controls for the narrow ordinary-App admission transport."""

from __future__ import annotations

import base64
import importlib.util
import json
import os
import pathlib
import sys
import unittest


ROOT = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    "v1_admission_provider_transport", ROOT / "github-v1-admission-provider-transport.py")
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
NOW = 1_790_182_400


def b64url(value):
    return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).rstrip(b"=").decode()


JWT = ".".join((b64url({"alg": "RS256", "typ": "JWT"}),
                b64url({"iat": NOW - 60, "exp": NOW + 540, "iss": transport.APP_ID}),
                "signature"))


class FakeProvider:
    def __init__(self):
        self.app_id = transport.APP_ID
        self.repository_id = transport.REPOSITORY_ID
        self.permissions = {"contents": "write", "metadata": "read"}
        self.objects = {}
        self.ref = None
        self.fail_create = False
        self.move_on_create = None
        self.lose_create_response = False
        self.calls = []

    def send(self, path, token, method="GET", body=None):
        self.calls.append((path, token, method, body))
        if path == "/app":
            return 200, {"id": self.app_id, "slug": transport.APP_SLUG}
        if path == f"/app/installations/{transport.INSTALLATION_ID}":
            return 200, {"id": transport.INSTALLATION_ID, "app_id": self.app_id,
                         "account": {"login": "FS-GG"}, "suspended_at": None,
                         "repository_selection": "selected", "permissions": self.permissions}
        if path == f"/app/installations/{transport.INSTALLATION_ID}/access_tokens":
            return 201, {"token": "scoped-token", "permissions": {"contents": "write", "metadata": "read"},
                         "repository_selection": "selected"}
        if path == "/installation/repositories?per_page=100":
            return 200, {"total_count": 1, "repositories": [
                {"id": self.repository_id, "full_name": transport.REPOSITORY}]}
        if path == f"/repos/{transport.REPOSITORY}/git/ref/heads/fsgg/v2/journal/operation/79":
            return (404, None) if self.ref is None else (200, {"object": {"sha": self.ref}})
        if path == f"/repos/{transport.REPOSITORY}/git/refs":
            if self.move_on_create is not None:
                self.ref = self.move_on_create
                return 422, None
            if self.fail_create:
                return 422, None
            self.ref = body["sha"]
            if self.lose_create_response:
                raise transport.Refused("admission-provider-indeterminate")
            return 201, {"ref": body["ref"], "object": {"sha": self.ref}}
        prefix = f"/repos/{transport.REPOSITORY}/git/"
        if path.startswith(prefix):
            tail = path.removeprefix(prefix).split("/")
            kind = {"blobs": "blob", "trees": "tree", "commits": "commit"}.get(tail[0])
            if kind is None:
                return 404, None
            if method == "POST" and len(tail) == 1:
                if kind == "blob":
                    raw = base64.b64decode(body["content"])
                elif kind == "tree":
                    raw = b"".join(b"100644 " + item["path"].encode() + b"\0"
                                   + bytes.fromhex(item["sha"]) for item in body["tree"])
                else:
                    raw = (f"tree {body['tree']}\nauthor FS.GG Coordination <coordination@fs.gg> 0 +0000\n"
                           "committer FS.GG Coordination <coordination@fs.gg> 0 +0000\n\n"
                           + body["message"]).encode()
                oid = transport.git_oid(kind, raw)
                self.objects[kind, oid] = (raw, body)
                return 201, {"sha": oid}
            if method == "GET" and len(tail) == 2 and (kind, tail[1]) in self.objects:
                raw, stored = self.objects[kind, tail[1]]
                if kind == "blob":
                    return 200, {"sha": tail[1], "content": base64.b64encode(raw).decode(),
                                 "encoding": "base64"}
                if kind == "tree":
                    return 200, {"sha": tail[1], "tree": stored["tree"], "truncated": False}
                return 200, {"sha": tail[1], "tree": {"sha": stored["tree"]}, "parents": [],
                             "author": stored["author"], "committer": stored["committer"],
                             "message": stored["message"]}
        return 404, None


def objects():
    event = b'{"event":"genesis"}\n'
    head = b'{"head":"genesis"}\n'
    event_oid = transport.git_oid("blob", event)
    head_oid = transport.git_oid("blob", head)
    tree = (b"100644 event.json\0" + bytes.fromhex(event_oid)
            + b"100644 head.json\0" + bytes.fromhex(head_oid))
    tree_oid = transport.git_oid("tree", tree)
    commit = (f"tree {tree_oid}\nauthor FS.GG Coordination <coordination@fs.gg> 0 +0000\n"
              "committer FS.GG Coordination <coordination@fs.gg> 0 +0000\n\n"
              "fsgg admission test\n").encode()
    return [("blob", event_oid, event), ("blob", head_oid, head),
            ("tree", tree_oid, tree), ("commit", transport.git_oid("commit", commit), commit)]


class TransportTests(unittest.TestCase):
    def test_exact_scope_objects_and_ref(self):
        fake = FakeProvider()
        port = transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
        expected = objects()
        for kind, oid, raw in expected:
            self.assertEqual(oid, port.put_object(kind, oid, raw))
            self.assertEqual(raw, port.read_object(kind, oid))
        self.assertIsNone(port.read_ref(transport.OPERATION_REF))
        port.create_ref_expected_absent(transport.OPERATION_REF, expected[-1][1])
        self.assertEqual(expected[-1][1], port.read_ref(transport.OPERATION_REF))
        self.assertEqual([transport.REPOSITORY_ID], fake.calls[2][3]["repository_ids"])
        self.assertEqual({"contents": "write"}, fake.calls[2][3]["permissions"])
        self.assertTrue(all(token == JWT for _, token, _, _ in fake.calls[:3]))
        self.assertTrue(all(token == "scoped-token" for _, token, _, _ in fake.calls[3:]))

    def test_wrong_app_installation_or_scope_refused_before_write(self):
        for change in ("app", "repository", "permission"):
            with self.subTest(change=change):
                fake = FakeProvider()
                if change == "app":
                    fake.app_id = 4882399
                elif change == "repository":
                    fake.repository_id = 1
                else:
                    fake.permissions = {"contents": "write", "actions": "write"}
                with self.assertRaises(transport.Refused):
                    transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
                self.assertFalse(any(method == "POST" and "/git/" in path
                                     for path, _, method, _ in fake.calls))

    def test_out_of_scope_ref_and_wrong_object_rejected_locally(self):
        fake = FakeProvider()
        port = transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
        count = len(fake.calls)
        with self.assertRaises(transport.Refused):
            port.read_ref("refs/heads/main")
        with self.assertRaises(transport.Refused):
            port.put_object("blob", "0" * 40, b"wrong")
        self.assertEqual(count, len(fake.calls))

    def test_competing_or_absent_create_is_not_success(self):
        expected = objects()[-1][1]
        for competing in ("1" * 40, None):
            with self.subTest(competing=competing):
                fake = FakeProvider()
                fake.move_on_create = competing
                fake.fail_create = competing is None
                port = transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
                with self.assertRaises(transport.Refused):
                    port.create_ref_expected_absent(transport.OPERATION_REF, expected)

    def test_lost_create_response_requires_exact_native_readback(self):
        fake = FakeProvider()
        fake.lose_create_response = True
        port = transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
        commit = objects()[-1][1]
        port.create_ref_expected_absent(transport.OPERATION_REF, commit)
        self.assertEqual(commit, port.read_ref(transport.OPERATION_REF))

    def test_object_readback_hash_and_jwt_fail_closed(self):
        fake = FakeProvider()
        port = transport.OrdinaryAdmissionTransport(JWT, fake.send, NOW)
        kind, oid, raw = objects()[0]
        port.put_object(kind, oid, raw)
        fake.objects[kind, oid] = (b"drift", {})
        with self.assertRaises(transport.Refused):
            port.read_object(kind, oid)
        with self.assertRaises(transport.Refused):
            transport.OrdinaryAdmissionTransport("not-a-jwt", fake.send, NOW)

    def test_host_jwt_descriptor_has_no_pem_or_environment_path(self):
        read_fd, write_fd = os.pipe()
        try:
            os.write(write_fd, JWT.encode())
            os.close(write_fd)
            self.assertEqual(JWT, transport.read_app_jwt_fd(read_fd))
        finally:
            os.close(read_fd)
        with self.assertRaises(transport.Refused):
            transport.read_app_jwt_fd(0)


if __name__ == "__main__":
    unittest.main()
