#!/usr/bin/env python3
"""Local bare-Git controls for the exact-parent admission append transport."""

import base64
import copy
import importlib.util
import pathlib
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-journal-cas.py")
spec = importlib.util.spec_from_file_location("v1_admission_journal_cas", SOURCE)
cas = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cas)


def object_item(kind, raw):
    return {"kind": kind, "oid": cas.git_oid(kind, raw),
            "bytesBase64": base64.b64encode(raw).decode("ascii")}


class CasTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="fsgg-v1-cas-test-")
        self.addCleanup(self.directory.cleanup)
        self.remote = pathlib.Path(self.directory.name) / "authority.git"
        cas.git(None, ["init", "--bare", str(self.remote)])
        self.root = self.commit(None, "root")
        cas.git(self.remote, ["update-ref", cas.REF, self.root])
        self.plan = self.planned(self.root, "admit-fixture")

    def commit(self, parent, operation):
        event = b'{"schema":"fixture"}\n'
        head = b'{"generation":1}\n'
        event_oid = cas.git_oid("blob", event)
        head_oid = cas.git_oid("blob", head)
        tree = (b"100644 event.json\0" + bytes.fromhex(event_oid)
                + b"100644 head.json\0" + bytes.fromhex(head_oid))
        tree_oid = cas.git_oid("tree", tree)
        parent_line = f"parent {parent}\n" if parent else ""
        commit = (f"tree {tree_oid}\n{parent_line}author {cas.PERSON}\n"
                  f"committer {cas.PERSON}\n\nfsgg admission {operation}\n").encode()
        for kind, raw in [("blob", event), ("blob", head),
                          ("tree", tree), ("commit", commit)]:
            cas.git(self.remote, ["hash-object", "-w", "-t", kind, "--stdin"], raw)
        return cas.git_oid("commit", commit)

    def planned(self, parent, operation):
        event = b'{"schema":"fixture-append"}\n'
        head = b'{"generation":2}\n'
        event_item = object_item("blob", event)
        head_item = object_item("blob", head)
        tree = (b"100644 event.json\0" + bytes.fromhex(event_item["oid"])
                + b"100644 head.json\0" + bytes.fromhex(head_item["oid"]))
        tree_item = object_item("tree", tree)
        commit = (f"tree {tree_item['oid']}\nparent {parent}\n"
                  f"author {cas.PERSON}\ncommitter {cas.PERSON}\n\n"
                  f"fsgg admission {operation}\n").encode()
        commit_item = object_item("commit", commit)
        return {"schema": "fsgg.v1-admission-journal-cas/1",
                "repositoryId": cas.REPOSITORY_ID, "ref": cas.REF,
                "expectedParent": parent, "proposedCommit": commit_item["oid"],
                "operationId": operation,
                "objects": [event_item, head_item, tree_item, commit_item]}

    def head(self):
        return cas.git(self.remote, ["rev-parse", cas.REF]).decode().strip()

    def test_exact_parent_append_and_duplicate_refuse(self):
        self.assertEqual("accepted", cas.append_local_fixture(self.plan, str(self.remote)))
        self.assertEqual(self.plan["proposedCommit"], self.head())
        with self.assertRaisesRegex(cas.ParentConflict, "parent-moved"):
            cas.append_local_fixture(self.plan, str(self.remote))

    def test_stale_parent_and_racing_sibling_never_overwrite(self):
        competitor = self.commit(self.root, "competing")
        cas.git(self.remote, ["update-ref", cas.REF, competitor])
        with self.assertRaises(cas.ParentConflict):
            cas.append_local_fixture(self.plan, str(self.remote))
        self.assertEqual(competitor, self.head())
        cas.git(self.remote, ["update-ref", cas.REF, self.root])

        def race(store, remote, expected, proposed):
            cas.git(self.remote, ["update-ref", cas.REF, competitor])
            return cas.push_local(store, remote, expected, proposed)

        self.assertEqual("response-unknown",
                         cas.append_local_fixture(self.plan, str(self.remote), race))
        self.assertEqual(competitor, self.head())

    def test_lost_success_stays_unknown_until_independent_readback(self):
        def lost_success(store, remote, expected, proposed):
            self.assertTrue(cas.push_local(store, remote, expected, proposed))
            return False

        self.assertEqual("response-unknown",
                         cas.append_local_fixture(self.plan, str(self.remote), lost_success))
        self.assertEqual(self.plan["proposedCommit"], self.head())

    def test_plan_tampering_and_network_remote_refuse_before_push(self):
        for mutation in [lambda p: p.update(ref="refs/heads/main"),
                         lambda p: p.update(expectedParent="a" * 40),
                         lambda p: p.update(operationId="different"),
                         lambda p: p["objects"][0].update(bytesBase64="AA=="),
                         lambda p: p["objects"].append(p["objects"][0])]:
            changed = copy.deepcopy(self.plan)
            mutation(changed)
            with self.assertRaises(cas.Refused):
                cas.append_local_fixture(changed, str(self.remote))
        with self.assertRaisesRegex(cas.Refused, "local-remote"):
            cas.append_local_fixture(self.plan, "https://github.com/FS-GG/FS.GG.Coordination.Authority.git")
        self.assertEqual(self.root, self.head())


if __name__ == "__main__":
    unittest.main()
