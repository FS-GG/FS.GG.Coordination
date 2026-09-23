#!/usr/bin/env python3
"""Independent fake-native controls for the v1 admission source collector."""

from __future__ import annotations

import importlib.util
import pathlib
import sys
import unittest


ROOT = pathlib.Path(__file__).resolve().parent
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("v1_source", ROOT / "github-v1-admission-source-read.py")
source = importlib.util.module_from_spec(spec)
spec.loader.exec_module(source)
COMMIT = "a" * 40
TREE = "b" * 40
MAIN = "c" * 40


class FakeNative:
    def __init__(self):
        self.repository_id = source.REPOSITORY_ID
        self.main_heads = [MAIN, MAIN]
        self.status = "ahead"
        self.merge_base = COMMIT
        self.compare_head = MAIN
        self.calls = []

    def read(self, path):
        self.calls.append(path)
        if path == "":
            return {"id": self.repository_id, "full_name": source.REPOSITORY}
        if path == "git/ref/heads/main":
            return {"ref": "refs/heads/main",
                    "object": {"type": "commit", "sha": self.main_heads.pop(0)}}
        if path == f"git/commits/{COMMIT}":
            return {"sha": COMMIT, "tree": {"sha": TREE}}
        if path == f"compare/{COMMIT}...{MAIN}":
            return {"status": self.status, "base_commit": {"sha": COMMIT},
                    "head_commit": {"sha": self.compare_head},
                    "merge_base_commit": {"sha": self.merge_base}}
        raise AssertionError(path)


class SourceReadTests(unittest.TestCase):
    def test_ancestor_binds_repository_tree_and_two_main_heads(self):
        native = FakeNative()
        value = source.collect(COMMIT, native.read, "2026-09-23T14:00:00Z")
        self.assertEqual(source.REPOSITORY_ID, value["repositoryId"])
        self.assertEqual(TREE, value["sourceTree"])
        self.assertEqual(["", "git/ref/heads/main", f"git/commits/{COMMIT}",
                          f"compare/{COMMIT}...{MAIN}", "git/ref/heads/main"], native.calls)
        self.assertEqual(("ahead", COMMIT), (value["compareStatus"], value["mergeBase"]))

    def test_exact_main_needs_no_comparison(self):
        native = FakeNative()
        native.main_heads = [COMMIT, COMMIT]
        value = source.collect(COMMIT, native.read, "2026-09-23T14:00:00Z")
        self.assertEqual("identical", value["compareStatus"])
        self.assertFalse(any(call.startswith("compare/") for call in native.calls))

    def test_diverged_is_reported_not_promoted_to_ancestor(self):
        native = FakeNative()
        native.status = "diverged"
        native.merge_base = "d" * 40
        value = source.collect(COMMIT, native.read, "2026-09-23T14:00:00Z")
        self.assertEqual(("diverged", "d" * 40), (value["compareStatus"], value["mergeBase"]))

    def test_identity_moved_main_and_compare_mismatch_fail_closed(self):
        for drift in ("repository", "main", "comparison"):
            with self.subTest(drift=drift):
                native = FakeNative()
                if drift == "repository":
                    native.repository_id = 1
                elif drift == "main":
                    native.main_heads[1] = "d" * 40
                else:
                    native.compare_head = "e" * 40
                with self.assertRaises(source.Refused):
                    source.collect(COMMIT, native.read, "2026-09-23T14:00:00Z")


if __name__ == "__main__":
    unittest.main()
