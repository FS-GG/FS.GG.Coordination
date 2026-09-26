#!/usr/bin/env python3
"""Fake-provider controls for the one-shot post-genesis CAS process boundary."""

import base64
import importlib.util
import json
import os
import pathlib
import sys
import tempfile
import unittest


sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-cas-process.py")
spec = importlib.util.spec_from_file_location("v1_admission_cas_process", SOURCE)
process = importlib.util.module_from_spec(spec)
spec.loader.exec_module(process)
cas = process.cas


def item(kind, raw):
    return {"kind": kind, "oid": cas.git_oid(kind, raw),
            "bytesBase64": base64.b64encode(raw).decode("ascii")}


def plan():
    parent = "a" * 40
    event = item("blob", b'{"event":"fixture"}\n')
    head = item("blob", b'{"generation":2}\n')
    tree_raw = (b"100644 event.json\0" + bytes.fromhex(event["oid"])
                + b"100644 head.json\0" + bytes.fromhex(head["oid"]))
    tree = item("tree", tree_raw)
    commit_raw = (f"tree {tree['oid']}\nparent {parent}\n"
                  f"author {cas.PERSON}\ncommitter {cas.PERSON}\n\n"
                  "fsgg admission fixture-op\n").encode()
    commit = item("commit", commit_raw)
    return {"schema": "fsgg.v1-admission-journal-cas/1",
            "repositoryId": cas.REPOSITORY_ID, "ref": cas.REF,
            "expectedParent": parent, "proposedCommit": commit["oid"],
            "operationId": "fixture-op", "objects": [event, head, tree, commit]}


class ProcessTests(unittest.TestCase):
    def pipe(self, content=b"synthetic.jwt.value"):
        reader, writer = os.pipe()
        os.write(writer, content)
        os.close(writer)
        self.addCleanup(os.close, reader)
        return reader

    def test_plan_precedes_secret_consumption_and_duplicate_fields_refuse(self):
        descriptor = self.pipe()
        with self.assertRaisesRegex(cas.Refused, "duplicate-field"):
            process.execute(b'{"schema":1,"schema":2}', descriptor,
                            lambda *_: self.fail("invalid plan reached append"))
        self.assertEqual(b"synthetic.jwt.value", os.read(descriptor, 8192))

    def test_scoped_handoff_returns_unknown_even_after_apparent_success(self):
        received = []
        raw = json.dumps(plan(), sort_keys=True, separators=(",", ":")).encode()
        result = process.execute(raw, self.pipe(),
                                 lambda value, jwt: received.append((value, jwt)))
        self.assertEqual({"schema": "fsgg.v1-admission-cas-process-result/1",
                          "outcome": "response-unknown"}, result)
        self.assertEqual(plan(), received[0][0])
        self.assertEqual("synthetic.jwt.value", received[0][1])
        self.assertNotIn(received[0][1], json.dumps(result))

    def test_lost_response_and_preflight_exception_never_claim_acceptance(self):
        raw = json.dumps(plan()).encode()
        calls = []

        def lost(value, jwt):
            calls.append(value["proposedCommit"])
            raise TimeoutError("provider response lost")

        result = process.execute(raw, self.pipe(), lost)
        self.assertEqual([plan()["proposedCommit"]], calls)
        self.assertEqual("response-unknown", result["outcome"])
        changed = plan()
        changed["ref"] = "refs/heads/main"
        with self.assertRaises(cas.Refused):
            process.execute(json.dumps(changed).encode(), self.pipe(), lost)
        self.assertEqual(1, len(calls))

    def test_regular_file_and_missing_descriptor_refuse_before_append(self):
        raw = json.dumps(plan()).encode()
        with tempfile.TemporaryFile() as regular:
            regular.write(b"synthetic.jwt.value")
            regular.seek(0)
            with self.assertRaisesRegex(cas.Refused, "not-pipe"):
                process.execute(raw, regular.fileno(),
                                lambda *_: self.fail("file token reached append"))
        with self.assertRaisesRegex(cas.Refused, "jwt-fd"):
            process.execute(raw, 99999, lambda *_: self.fail("missing fd reached append"))

    def test_secret_pipe_without_eof_has_a_bounded_refusal(self):
        reader, writer = os.pipe()
        self.addCleanup(os.close, reader)
        self.addCleanup(os.close, writer)
        os.write(writer, b"synthetic.jwt.value")
        with self.assertRaisesRegex(cas.Refused, "jwt-timeout"):
            process.read_jwt_pipe(reader, timeout_seconds=0.01)


if __name__ == "__main__":
    unittest.main()
