#!/usr/bin/env python3
"""Local Git-object controls for the read-only installed admission journal collector."""

import importlib.util
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-journal-read.py")
spec = importlib.util.spec_from_file_location("v1_admission_journal_read", SOURCE)
journal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(journal)


class JournalReadTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="fsgg-v1-admission-test-")
        self.addCleanup(self.directory.cleanup)
        self.repository = pathlib.Path(self.directory.name) / "authority.git"
        self.git(["init", "--bare", str(self.repository)], in_repo=False)
        self.root = self.commit(None)
        self.head = self.commit(self.root)
        self.git(["update-ref", journal.git_read.OPERATION_REF, self.head])
        self.git(["update-ref", journal.git_read.CUTOVER_REF, self.root])

    def git(self, arguments, data=None, in_repo=True):
        command = ["git"]
        if in_repo:
            command.append(f"--git-dir={self.repository}")
        environment = dict(os.environ)
        environment.update({"GIT_AUTHOR_NAME": "test", "GIT_AUTHOR_EMAIL": "test@example.invalid",
                            "GIT_COMMITTER_NAME": "test", "GIT_COMMITTER_EMAIL": "test@example.invalid",
                            "GIT_AUTHOR_DATE": "@0 +0000", "GIT_COMMITTER_DATE": "@0 +0000"})
        result = subprocess.run([*command, *arguments], input=data, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, env=environment, check=False)
        self.assertEqual(0, result.returncode, result.stderr.decode())
        return result.stdout.strip().decode()

    def blob(self, contents):
        return self.git(["hash-object", "-w", "--stdin"], contents)

    def commit(self, parent, extra=False):
        event = self.blob(b'{"schema":"fixture"}\n')
        head = self.blob(b'{"generation":1}\n')
        entries = (f"100644 blob {event}\tevent.json\n"
                   f"100644 blob {head}\thead.json\n")
        if extra:
            entries += f"100644 blob {event}\textra.json\n"
        tree = self.git(["mktree"], entries.encode())
        arguments = ["commit-tree", tree, "-m", "fsgg admission fixture"]
        if parent is not None:
            arguments += ["-p", parent]
        return self.git(arguments)

    def collect(self, read_refs=journal.git_read.refs):
        return journal.collect(str(self.repository), lambda: journal.git_read.REPOSITORY_ID,
                               read_refs, "2026-09-23T14:00:00Z")

    def test_reads_exact_root_and_append_in_oldest_first_order(self):
        evidence = self.collect()
        self.assertEqual(journal.SCHEMA, evidence["schema"])
        self.assertEqual(self.head, evidence["firstHead"])
        self.assertEqual([self.root, self.head],
                         [entry["commitOid"] for entry in evidence["commits"]])
        self.assertEqual(evidence["firstHead"], evidence["secondHead"])

    def test_absence_and_moved_ref_are_failed_reads(self):
        self.git(["update-ref", "-d", journal.git_read.OPERATION_REF])
        with self.assertRaisesRegex(journal.git_read.Refused, "not-installed"):
            self.collect()
        self.git(["update-ref", journal.git_read.OPERATION_REF, self.head])
        calls = 0
        def moved(remote):
            nonlocal calls
            calls += 1
            result = journal.git_read.refs(remote)
            if calls == 2:
                result[journal.git_read.OPERATION_REF] = self.root
            return result
        with self.assertRaisesRegex(journal.git_read.Refused, "refs-moved"):
            self.collect(moved)

    def test_extra_tree_entry_and_history_ceiling_refuse(self):
        wrong = self.commit(self.root, extra=True)
        self.git(["update-ref", journal.git_read.OPERATION_REF, wrong])
        with self.assertRaisesRegex(journal.git_read.Refused, "authority-tree-entries"):
            self.collect()
        self.git(["update-ref", journal.git_read.OPERATION_REF, self.head])
        prior = journal.MAX_COMMITS
        journal.MAX_COMMITS = 1
        try:
            with self.assertRaisesRegex(journal.git_read.Refused, "history-bound"):
                self.collect()
        finally:
            journal.MAX_COMMITS = prior


if __name__ == "__main__":
    unittest.main()
