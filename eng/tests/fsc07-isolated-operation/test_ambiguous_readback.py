"""Offline characterization of one-attempt isolated-operation setup writes.

The operation source is sealed by its checked-in contract. These cases exercise
that exact source with scripted responses; they never construct a live client.
"""

from __future__ import annotations

import base64
import importlib.util
import pathlib
import unittest


SOURCE = pathlib.Path(__file__).resolve().parents[2] / "callable-cli-isolated-operation.py"
SPEC = importlib.util.spec_from_file_location("isolated_operation", SOURCE)
operation = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(operation)
UNKNOWN = operation.Refused("github-outcome-unknown-requires-readback")
REPOSITORY = "FS-GG/isolated-synthetic"


class ScriptedClient:
    def __init__(self, replies):
        self.replies = list(replies)
        self.calls = []

    def request(self, method, path, body=None):
        self.calls.append((method, path, body))
        if not self.replies:
            raise AssertionError("unexpected request after scripted readback")
        reply = self.replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply


class AmbiguousReadbackTests(unittest.TestCase):
    def assert_one_write(self, client, method):
        self.assertEqual(1, sum(call[0] == method for call in client.calls))
        self.assertFalse(client.replies)

    def test_ref_creation_unknown_outcome_requires_matching_authoritative_readback(self):
        ref = "refs/heads/fsgg/v2/policy/callable-isolated"
        head = "a" * 40
        for observed, expected in [((404, {}), "ref-pending-requires-readback"),
                                   ((200, {"object": {"sha": "b" * 40}}), "ref-pending-requires-readback")]:
            with self.subTest(expected=expected, observed=observed):
                client = ScriptedClient([(404, {}), UNKNOWN, observed])
                with self.assertRaisesRegex(operation.Refused, expected):
                    operation.ensure_ref(client, REPOSITORY, ref, head)
                self.assert_one_write(client, "POST")
        settled = ScriptedClient([(404, {}), UNKNOWN, (200, {"object": {"sha": head}})])
        operation.ensure_ref(settled, REPOSITORY, ref, head)
        self.assert_one_write(settled, "POST")

    def test_content_unknown_outcome_requires_exact_bytes(self):
        path, branch, payload = "synthetic.txt", "main", b"expected payload"
        different = (200, {"sha": "a" * 40, "content": base64.b64encode(b"other payload").decode()})
        missing = (404, {})
        for observed in (missing, different):
            with self.subTest(observed=observed):
                client = ScriptedClient([missing, UNKNOWN, observed])
                with self.assertRaisesRegex(operation.Refused, "content-pending-requires-readback"):
                    operation.ensure_content(client, REPOSITORY, path, branch, payload)
                self.assert_one_write(client, "PUT")
        same = (200, {"sha": "a" * 40, "content": base64.b64encode(payload).decode()})
        settled = ScriptedClient([missing, UNKNOWN, same])
        operation.ensure_content(settled, REPOSITORY, path, branch, payload)
        self.assert_one_write(settled, "PUT")

    def test_pull_unknown_outcome_never_retries_create(self):
        empty = (200, [])
        pull = {"number": 7, "node_id": "PR_synthetic"}
        for observed, expected in [(empty, "pull-request-pending-requires-readback"),
                                   ((200, [pull, pull]), "pull-request-pending-requires-readback")]:
            with self.subTest(expected=expected):
                client = ScriptedClient([empty, UNKNOWN, observed])
                with self.assertRaisesRegex(operation.Refused, expected):
                    operation.ensure_pull_request(client, REPOSITORY, "FS-GG", "main")
                self.assert_one_write(client, "POST")
        settled = ScriptedClient([empty, UNKNOWN, (200, [pull])])
        self.assertEqual(pull, operation.ensure_pull_request(settled, REPOSITORY, "FS-GG", "main"))
        self.assert_one_write(settled, "POST")

    def test_protection_unknown_outcome_requires_matching_readback(self):
        absent = (404, {})
        desired = operation.protection_body(15368)
        conflict = (200, operation.protection_body(99999))
        for observed in (absent, conflict):
            with self.subTest(observed=observed):
                client = ScriptedClient([absent, UNKNOWN, observed])
                with self.assertRaisesRegex(operation.Refused, "branch-protection-pending-requires-readback"):
                    operation.ensure_protection(client, REPOSITORY, "main", 15368)
                self.assert_one_write(client, "PUT")
        settled = ScriptedClient([absent, UNKNOWN, (200, desired)])
        operation.ensure_protection(settled, REPOSITORY, "main", 15368)
        self.assert_one_write(settled, "PUT")


if __name__ == "__main__":
    unittest.main()
