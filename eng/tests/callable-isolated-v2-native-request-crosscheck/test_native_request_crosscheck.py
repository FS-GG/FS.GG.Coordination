"""The closed plan and provisional native operator must derive one PR request."""

import dataclasses
import hashlib
import importlib.util
import os
import pathlib
import socket
import sqlite3
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-operation-plan-read"))
from test_operation_plan_read_adapter import FakePort, FakeSeal, NOW, canonical, fixture
import callable_isolated_v2_operation_plan_read_adapter as plan

SPEC = importlib.util.spec_from_file_location("isolated_native_request_crosscheck",
    ENG / "callable-cli-isolated-operation-v2.py")
native = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = native
SPEC.loader.exec_module(native)


class NativeRequestCrosscheckTests(unittest.TestCase):
    def selected(self):
        envelopes, record, scope, seal = fixture()
        observed = plan.OperationPlanReadAdapter(
            FakePort(canonical(record), scope), FakeSeal(seal), *envelopes,
            500, "f" * 64, NOW).observe_operation_plan()
        self.assertEqual(observed["envelope"]["facts"]["operation"],
                         record["operation"])
        target = record["target"]
        expected = native.ExpectedPull(native.OPERATION_IDENTITY, 1,
            target["repositoryId"], target["repository"],
            target["sourceRef"], target["sourceSha"],
            target["baseRef"], target["baseSha"])
        return record, expected

    def test_plan_request_is_exact_native_request_and_digest(self):
        record, expected = self.selected()
        with (mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect",
                                side_effect=AssertionError("provider")),
              mock.patch.object(sqlite3, "connect",
                                side_effect=AssertionError("journal"))):
            body = native.pull_request_body(expected)
        self.assertEqual(plan.TITLE, native.PULL_TITLE)
        self.assertEqual(body, record["request"])
        self.assertEqual(hashlib.sha256(canonical(body)).hexdigest(),
                         record["operation"]["requestSha256"])
        self.assertEqual(record["operation"]["identity"],
                         native.OPERATION_IDENTITY)
        self.assertEqual(record["operation"]["method"], "POST")
        self.assertEqual(record["operation"]["path"],
                         f"repos/{expected.repository}/pulls")
        self.assertEqual(record["operation"]["maxProviderWrites"], 1)

    def test_each_native_target_coordinate_changes_sealed_request(self):
        record, expected = self.selected()
        for field, changed in (("repository_id", 301),
                               ("repository", "FS-GG/foreign"),
                               ("source_ref", "refs/heads/foreign"),
                               ("source_sha", "a" * 40),
                               ("base_ref", "refs/heads/foreign"),
                               ("base_sha", "b" * 40)):
            with self.subTest(field=field):
                body = native.pull_request_body(dataclasses.replace(
                    expected, **{field: changed}))
                self.assertNotEqual(body, record["request"])
                self.assertNotEqual(hashlib.sha256(canonical(body)).hexdigest(),
                                    record["operation"]["requestSha256"])

    def test_resealed_request_for_foreign_native_target_still_refuses(self):
        envelopes, record, scope, seal = fixture()
        target = record["target"]
        foreign = native.ExpectedPull(native.OPERATION_IDENTITY, 1,
            target["repositoryId"], target["repository"],
            target["sourceRef"], "a" * 40,
            target["baseRef"], target["baseSha"])
        record["request"] = native.pull_request_body(foreign)
        record["operation"]["requestSha256"] = hashlib.sha256(
            canonical(record["request"])).hexdigest()
        with self.assertRaises(plan.Refused):
            plan.OperationPlanReadAdapter(
                FakePort(canonical(record), scope), FakeSeal(seal), *envelopes,
                500, "f" * 64, NOW).observe_operation_plan()


if __name__ == "__main__":
    unittest.main()
