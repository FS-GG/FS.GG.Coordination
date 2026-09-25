"""Deterministic local v5 refusal archive, with no protected installation."""

import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_v5_no_grant as builder
import verify_callable_isolated_v2_v5_no_grant as verifier


def source(relative):
    return (ROOT / relative).read_bytes()


class V5NoGrantArtifactTests(unittest.TestCase):
    def test_two_builds_match_approved_manifest_and_single_member(self):
        with tempfile.TemporaryDirectory() as left_dir, \
             tempfile.TemporaryDirectory() as right_dir:
            left = pathlib.Path(left_dir) / builder.ARCHIVE_NAME
            right = pathlib.Path(right_dir) / builder.ARCHIVE_NAME
            first = builder.build(left)
            self.assertEqual(first, builder.build(right))
            self.assertEqual(left.read_bytes(), right.read_bytes())
            manifest_raw = source(builder.MANIFEST)
            self.assertEqual(json.loads(manifest_raw), first)
            self.assertEqual(first["archiveSha256"],
                             hashlib.sha256(left.read_bytes()).hexdigest())
            with zipfile.ZipFile(left) as archive:
                self.assertEqual(archive.namelist(), ["__main__.py"])
                self.assertEqual(archive.read("__main__.py"),
                                 source(builder.ENTRY_SOURCE))
            checked = verifier.verify(left.read_bytes(), manifest_raw,
                hashlib.sha256(manifest_raw).hexdigest(),
                source(builder.ENTRY_SOURCE),
                source("eng/build_callable_isolated_v2_v5_no_grant.py"),
                source(builder.WORKFLOW))
            self.assertTrue(checked["verified"])
            self.assertFalse(checked["authorized"])
            self.assertFalse(checked["canDispatch"])

    def test_source_or_workflow_mutation_refuses_before_write(self):
        actual = builder._source
        for changed in (builder.ENTRY_SOURCE, builder.WORKFLOW):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temp:
                archive = pathlib.Path(temp) / builder.ARCHIVE_NAME
                def substituted(relative):
                    raw = actual(relative)
                    return raw + b"\n# unreviewed\n" if relative == changed else raw
                with mock.patch.object(builder, "_source", side_effect=substituted):
                    with self.assertRaises(ValueError):
                        builder.build(archive)
                self.assertFalse(archive.exists())

    def test_wrong_archive_member_builder_or_approval_refuses(self):
        with tempfile.TemporaryDirectory() as temp:
            archive = pathlib.Path(temp) / builder.ARCHIVE_NAME
            builder.build(archive)
            raw = archive.read_bytes()
            manifest = source(builder.MANIFEST)
            approved = hashlib.sha256(manifest).hexdigest()
            entry = source(builder.ENTRY_SOURCE)
            build_source = source("eng/build_callable_isolated_v2_v5_no_grant.py")
            workflow = source(builder.WORKFLOW)
            def refused(a=raw, m=manifest, p=approved, e=entry,
                        b=build_source, w=workflow):
                with self.assertRaises(verifier.Refused):
                    verifier.verify(a, m, p, e, b, w)
            refused(a=raw + b"FOREIGN_TRAILING_BYTES")
            refused(m=manifest + b" ")
            refused(p="0" * 64)
            refused(e=entry + b"# changed\n")
            refused(b=build_source + b"# changed\n")
            refused(w=workflow.replace(b"if: ${{ false }}",
                                       b"if: ${{ true }}"))
            foreign = json.loads(manifest)
            foreign["members"][0]["source"] = "eng/foreign.py"
            resealed = (json.dumps(foreign, sort_keys=True,
                        separators=(",", ":")) + "\n").encode()
            refused(m=resealed, p=hashlib.sha256(resealed).hexdigest())

    def test_clean_local_install_refuses_without_token_journal_or_socket(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = pathlib.Path(temp)
            archive = directory / builder.ARCHIVE_NAME
            builder.build(archive)
            token = directory / "token-sentinel"
            journal = directory / "journal-sentinel"
            token.write_text("SECRET_SENTINEL")
            journal.write_text("untouched")
            work = directory / "clean"
            work.mkdir()
            before = (archive.read_bytes(), token.read_bytes(),
                      journal.read_bytes())
            environment = os.environ.copy()
            environment.update({"GITHUB_TOKEN": "SECRET_SENTINEL",
                "GH_TOKEN": "SECRET_SENTINEL",
                "V2_ISOLATED_EXECUTION_TOKEN_FILE": str(token),
                "FSGG_V2_JOURNAL_PATH": str(journal),
                "GITHUB_API_URL": "http://127.0.0.1:1",
                "PYTHONPATH": str(ENG)})
            for args in (("execute-native-pull",),
                         ("execute-native-pull", "--grant", str(token))):
                result = subprocess.run([sys.executable, "-I", "-S",
                    str(archive), *args], cwd=work, env=environment,
                    capture_output=True, text=True, timeout=10, check=False)
                self.assertEqual(result.returncode, 78, result.stderr)
                self.assertEqual(result.stderr, "")
                self.assertNotIn("SECRET_SENTINEL", result.stdout)
                self.assertFalse(json.loads(result.stdout)["canDispatch"])
            audit_script = """
import runpy, sys
archive, token, journal = sys.argv[1:]
def audit(event, args):
    if event == 'open' and str(args[0]) in (token, journal):
        raise RuntimeError('protected-file-read')
    if event in ('socket.connect', 'sqlite3.connect'):
        raise RuntimeError('native-effect-port')
sys.addaudithook(audit)
sys.argv = [archive, 'execute-native-pull']
try:
    runpy.run_path(archive, run_name='__main__')
except SystemExit as stopped:
    assert stopped.code == 78
"""
            audited = subprocess.run([sys.executable, "-I", "-S", "-c",
                audit_script, str(archive), str(token), str(journal)],
                cwd=work, env=environment, capture_output=True,
                text=True, timeout=10, check=False)
            self.assertEqual(audited.returncode, 0, audited.stderr)
            self.assertEqual(json.loads(audited.stdout)["reason"], "no-grant")
            self.assertEqual((archive.read_bytes(), token.read_bytes(),
                              journal.read_bytes()), before)
            self.assertEqual(list(work.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
