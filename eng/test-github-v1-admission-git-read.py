#!/usr/bin/env python3
import base64
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest


sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("v1_git_read", ROOT / "github-v1-admission-git-read.py")
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)
GIT_FIXTURE = ROOT.parent / "tests/FS.GG.Coordination.UnitTests/fixtures/v1-admission-git-read.json"


def git(store, *args, input_bytes=None):
    environment = dict(os.environ)
    environment.update({"GIT_AUTHOR_NAME": "FS.GG test", "GIT_AUTHOR_EMAIL": "test@fs.gg",
                        "GIT_COMMITTER_NAME": "FS.GG test", "GIT_COMMITTER_EMAIL": "test@fs.gg",
                        "GIT_AUTHOR_DATE": "2026-09-23T14:00:00+00:00",
                        "GIT_COMMITTER_DATE": "2026-09-23T14:00:00+00:00"})
    result = subprocess.run(["git", f"--git-dir={store}", *args], input=input_bytes,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=environment, check=False, timeout=10)
    if result.returncode:
        raise AssertionError(result.stderr.decode())
    return result.stdout.decode().strip()


def fixture(root):
    store = root / "authority.git"
    subprocess.run(["git", "init", "--bare", str(store)], stdout=subprocess.DEVNULL,
                   stderr=subprocess.DEVNULL, check=True)
    manifest = "a" * 64
    trust = "b" * 64
    event = (f'{{"fleetId":"fs-gg-production","manifestSha256":"{manifest}",'
             f'"phase":"OperatingV1","schema":"fsgg.github-substrate.epoch-event/1",'
             f'"trustAnchorSha256":"{trust}"}}').encode()
    head = b'{"head":1}'
    event_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=event)
    head_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=head)
    tree = git(store, "mktree", input_bytes=(f"100644 blob {event_oid}\tevent.json\n"
                                             f"100644 blob {head_oid}\thead.json\n").encode())
    commit = git(store, "commit-tree", tree, input_bytes=b"Initialize test fleet\n")
    git(store, "update-ref", reader.CUTOVER_REF, commit)
    git(store, "update-ref", reader.TAG_PREFIX + manifest[:16], commit)
    return store, commit, event, head


def collect(store, read_refs=reader.refs):
    return reader.collect(str(store), lambda: reader.REPOSITORY_ID, read_refs,
                          "2026-09-23T14:00:00Z")


def install_genesis(store):
    event = b'{"kind":"initialize"}'
    head = b'{"generation":1}'
    event_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=event)
    head_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=head)
    tree = git(store, "mktree", input_bytes=(f"100644 blob {event_oid}\tevent.json\n"
                                             f"100644 blob {head_oid}\thead.json\n").encode())
    commit = git(store, "commit-tree", tree, input_bytes=b"Initialize admission genesis\n")
    git(store, "update-ref", reader.OPERATION_REF, commit)
    return commit, event, head


def install_claim(store):
    claim_id = "issue:fs-gg/repo#42"
    payload = claim_id.encode()
    digest = hashlib.sha256(str(len(payload)).encode() + b":" + payload).hexdigest()
    ref = reader.CLAIM_PREFIX + digest[:2]
    event = b'{"generation":1,"payload":"claim"}\n'
    event_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=event)
    head = {"aggregateDigest": digest, "aggregateId": claim_id,
            "eventDigest": hashlib.sha256(event).hexdigest(), "generation": 1,
            "journalKind": "claim", "priorHeadDigest": None, "schemaVersion": 1,
            "shard": digest[:2], "snapshotDigest": None, "terminal": False}
    head_bytes = json.dumps(head, sort_keys=True, separators=(",", ":")).encode() + b"\n"
    head_oid = git(store, "hash-object", "-w", "--stdin", input_bytes=head_bytes)
    tree = git(store, "mktree", input_bytes=(f"100644 blob {event_oid}\tevent.json\n"
                                             f"100644 blob {head_oid}\thead.json\n").encode())
    commit = git(store, "commit-tree", tree, input_bytes=b"claim fixture\n")
    git(store, "update-ref", ref, commit)
    return ref, commit


class AuthorityGitReadTests(unittest.TestCase):
    def test_exact_raw_git_and_stable_absence(self):
        with tempfile.TemporaryDirectory() as directory:
            store, commit, event, head = fixture(pathlib.Path(directory))
            result = collect(store)
            self.assertEqual(commit, result["cutover"]["firstHead"])
            self.assertEqual(commit, result["cutover"]["secondHead"])
            self.assertEqual([commit], result["cutover"]["ancestry"])
            self.assertEqual(event, base64.b64decode(result["cutover"]["eventBytesBase64"]))
            self.assertEqual(head, base64.b64decode(result["cutover"]["headBytesBase64"]))
            self.assertEqual("deleted", result["operation"]["observation"])
            self.assertEqual([], result["cutover"]["claimRefs"])
            self.assertEqual(GIT_FIXTURE.read_bytes(),
                             json.dumps(result, sort_keys=True, separators=(",", ":")).encode() + b"\n")

    def test_operation_claim_and_tag_drift_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            store, commit, _, _ = fixture(pathlib.Path(directory))
            git(store, "update-ref", reader.OPERATION_REF, commit)
            with self.assertRaisesRegex(reader.Refused, "authority-operation-census-not-empty"):
                collect(store)
            git(store, "update-ref", "-d", reader.OPERATION_REF)
            git(store, "update-ref", reader.CLAIM_PREFIX + "one", commit)
            with self.assertRaisesRegex(reader.Refused, "authority-claim-census-not-empty"):
                collect(store)
            git(store, "update-ref", "-d", reader.CLAIM_PREFIX + "one")
            git(store, "update-ref", "-d", reader.TAG_PREFIX + "a" * 16)
            with self.assertRaisesRegex(reader.Refused, "authority-genesis-tag-census"):
                collect(store)

    def test_moved_ref_census_and_wrong_repository_id_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            store, _, _, _ = fixture(pathlib.Path(directory))
            reads = 0

            def moving(remote):
                nonlocal reads
                reads += 1
                observed = reader.refs(remote)
                if reads == 2:
                    observed[reader.CUTOVER_REF] = "f" * 40
                return observed

            with self.assertRaisesRegex(reader.Refused, "authority-ref-census-moved"):
                collect(store, moving)
            with self.assertRaisesRegex(reader.Refused, "authority-repository-id"):
                reader.collect(str(store), lambda: 0, reader.refs)

    def test_installed_genesis_has_two_stable_refs_and_exact_raw_objects(self):
        with tempfile.TemporaryDirectory() as directory:
            store, cutover, _, _ = fixture(pathlib.Path(directory))
            commit, event, head = install_genesis(store)
            result = reader.collect_installed(commit, str(store), lambda: reader.REPOSITORY_ID,
                                              reader.refs, "2026-09-23T14:00:00Z")
            self.assertEqual("fsgg.v1-admission-genesis-installed-read/1", result["schema"])
            self.assertEqual(cutover, result["cutoverFirstHead"])
            self.assertEqual(cutover, result["cutoverSecondHead"])
            self.assertEqual(commit, result["operation"]["firstHead"])
            self.assertEqual(commit, result["operation"]["secondHead"])
            self.assertEqual(event, base64.b64decode(result["operation"]["eventBytesBase64"]))
            self.assertEqual(head, base64.b64decode(result["operation"]["headBytesBase64"]))

    def test_operating_authority_requires_stable_installed_operation_and_no_claims(self):
        with tempfile.TemporaryDirectory() as directory:
            store, cutover, _, _ = fixture(pathlib.Path(directory))
            with self.assertRaisesRegex(reader.Refused, "operation-census-not-installed"):
                reader.collect_operating(str(store), lambda: reader.REPOSITORY_ID)
            operation, _, _ = install_genesis(store)
            result = reader.collect_operating(str(store), lambda: reader.REPOSITORY_ID,
                                              reader.refs, "2026-09-23T14:00:00Z")
            self.assertEqual("fsgg.v1-admission-operating-git-read/1", result["schema"])
            self.assertEqual(cutover, result["cutover"]["firstHead"])
            self.assertEqual({"ref": reader.OPERATION_REF, "firstHead": operation,
                              "secondHead": operation, "observation": "present"},
                             result["operation"])
            claim_ref, claim_head = install_claim(store)
            with_claim = reader.collect_operating(str(store), lambda: reader.REPOSITORY_ID)
            self.assertEqual([claim_ref], with_claim["cutover"]["claimRefs"])
            self.assertEqual(claim_head, with_claim["claims"][0]["firstHead"])
            self.assertEqual(1, len(with_claim["claims"][0]["commits"]))
            git(store, "update-ref", reader.CLAIM_PREFIX + "wrong", operation)
            with self.assertRaisesRegex(reader.Refused, "claim-census-shape"):
                reader.collect_operating(str(store), lambda: reader.REPOSITORY_ID)
            git(store, "update-ref", "-d", reader.CLAIM_PREFIX + "wrong")
            reads = 0

            def moving(remote):
                nonlocal reads
                reads += 1
                observed = reader.refs(remote)
                if reads == 2:
                    observed[reader.OPERATION_REF] = cutover
                return observed

            with self.assertRaisesRegex(reader.Refused, "ref-census-moved"):
                reader.collect_operating(str(store), lambda: reader.REPOSITORY_ID, moving)

    def test_installed_genesis_ref_parent_and_census_drift_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            store, cutover, _, _ = fixture(pathlib.Path(directory))
            commit, _, _ = install_genesis(store)
            with self.assertRaisesRegex(reader.Refused, "admission-competing-ref"):
                reader.collect_installed(cutover, str(store), lambda: reader.REPOSITORY_ID)
            with self.assertRaisesRegex(reader.Refused, "authority-repository-id"):
                reader.collect_installed(commit, str(store), lambda: 0)
            git(store, "update-ref", reader.OPERATION_PREFIX + "another", commit)
            with self.assertRaisesRegex(reader.Refused, "admission-operation-census"):
                reader.collect_installed(commit, str(store), lambda: reader.REPOSITORY_ID)
            git(store, "update-ref", "-d", reader.OPERATION_PREFIX + "another")
            git(store, "update-ref", reader.CLAIM_PREFIX + "one", commit)
            with self.assertRaisesRegex(reader.Refused, "authority-claim-census-not-empty"):
                reader.collect_installed(commit, str(store), lambda: reader.REPOSITORY_ID)
            git(store, "update-ref", "-d", reader.CLAIM_PREFIX + "one")

            reads = 0
            def moving(remote):
                nonlocal reads
                reads += 1
                observed = reader.refs(remote)
                if reads == 2:
                    observed[reader.OPERATION_REF] = cutover
                return observed
            with self.assertRaisesRegex(reader.Refused, "authority-ref-census-moved"):
                reader.collect_installed(commit, str(store), lambda: reader.REPOSITORY_ID,
                                         moving)
            tree = git(store, "rev-parse", f"{commit}^{{tree}}")
            parented = git(store, "commit-tree", tree, "-p", cutover,
                           input_bytes=b"Not a genesis\n")
            git(store, "update-ref", reader.OPERATION_REF, parented)
            with self.assertRaisesRegex(reader.Refused, "admission-genesis-has-parent"):
                reader.collect_installed(parented, str(store), lambda: reader.REPOSITORY_ID)


if __name__ == "__main__":
    unittest.main()
