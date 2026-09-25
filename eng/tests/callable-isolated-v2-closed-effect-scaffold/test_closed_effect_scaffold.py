"""No-grant installed effect-scaffold controls; no protected service is used."""

import ast
import contextlib
import hashlib
import http.server
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_effect_closed as closed
import callable_isolated_v2_effect_entry as entry


class DenyPorts:
    def __init__(self):
        self.calls = []

    def read_execution_token(self):
        self.calls.append("token")
        raise AssertionError("token-read")

    def reserve_attempt(self):
        self.calls.append("cas")
        raise AssertionError("cas-write")

    def post_pull(self):
        self.calls.append("post")
        raise AssertionError("provider-post")


class ClosedScaffoldTests(unittest.TestCase):
    def test_no_grant_and_untrusted_grant_never_enter_effect_ports(self):
        ports = DenyPorts()
        for grant in (None, b"SYNTHETIC_UNTRUSTED_GRANT"):
            result = closed.execute_native_pull(grant, ports)
            self.assertFalse(result.authorized)
            self.assertFalse(result.can_dispatch)
            self.assertEqual(result.live_effects, 0)
        self.assertEqual(ports.calls, [])
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(entry.main(["execute-native-pull"]), 78)
        self.assertEqual(json.loads(output.getvalue())["reason"], "no-grant")

    def test_two_builds_match_exact_manifest_and_closed_workflow(self):
        with tempfile.TemporaryDirectory() as left_dir, tempfile.TemporaryDirectory() as right_dir:
            left = pathlib.Path(left_dir) / builder.ARCHIVE_NAME
            right = pathlib.Path(right_dir) / builder.ARCHIVE_NAME
            first = builder.build(left)
            self.assertEqual(first, builder.build(right))
            self.assertEqual(left.read_bytes(), right.read_bytes())
            committed = json.loads((ROOT / builder.MANIFEST).read_text())
            self.assertEqual(first, committed)
            self.assertEqual(first["archiveSha256"], hashlib.sha256(left.read_bytes()).hexdigest())
            self.assertEqual(first["workflowSha256"], hashlib.sha256(
                (ROOT / builder.WORKFLOW).read_bytes()).hexdigest())
            with zipfile.ZipFile(left) as archive:
                self.assertEqual(archive.namelist(), sorted(builder.MEMBERS))
                for item in first["members"]:
                    self.assertEqual(hashlib.sha256(archive.read(item["path"])).hexdigest(),
                                     item["sha256"])
            workflow = (ROOT / builder.WORKFLOW).read_text()
            self.assertIn("if: ${{ false }}", workflow)
            self.assertIn("permissions: {}", workflow)
            self.assertNotIn("secrets.", workflow)

    def test_wrong_member_or_enabled_workflow_refuses_before_archive_write(self):
        actual = builder._source
        for changed in ("eng/callable-cli-isolated-operation-v2.py",
                        builder.WORKFLOW):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temp:
                output = pathlib.Path(temp) / builder.ARCHIVE_NAME

                def substituted(relative):
                    raw = actual(relative)
                    return raw + b"\n# unreviewed mutation\n" if relative == changed else raw

                with mock.patch.object(builder, "_source", side_effect=substituted):
                    with self.assertRaisesRegex(ValueError,
                                                "effect-source-or-workflow-drift"):
                        builder.build(output)
                self.assertFalse(output.exists())

    def test_clean_installed_no_grant_is_zero_post_and_zero_journal_change(self):
        requests = []

        class Canary(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                requests.append(self.path)
                self.send_response(500)
                self.end_headers()

            def log_message(self, *_args):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Canary)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as temporary:
                directory = pathlib.Path(temporary)
                installed = directory / builder.ARCHIVE_NAME
                builder.build(installed)
                token_file = directory / "token-sentinel"
                token_file.write_text("SYNTHETIC_SECRET_SENTINEL")
                journal = directory / "journal-sentinel"
                journal.write_text("untouched")
                work = directory / "empty-workdir"
                work.mkdir()
                before = {path: path.read_bytes() for path in (installed, token_file, journal)}
                environment = os.environ.copy()
                environment.update({"GITHUB_TOKEN": "SYNTHETIC_SECRET_SENTINEL",
                    "GH_TOKEN": "SYNTHETIC_SECRET_SENTINEL",
                    "V2_ISOLATED_EXECUTION_TOKEN_FILE": str(token_file),
                    "FSGG_V2_JOURNAL_PATH": str(journal),
                    "GITHUB_API_URL": f"http://127.0.0.1:{server.server_port}",
                    "PYTHONPATH": str(ENG)})
                for args in (("execute-native-pull",),
                             ("execute-native-pull", "--grant", str(token_file))):
                    result = subprocess.run([sys.executable, "-I", "-S", str(installed), *args],
                        cwd=work, env=environment, capture_output=True, text=True,
                        timeout=10, check=False)
                    self.assertEqual(result.returncode, 78, result.stderr)
                    self.assertNotIn("SYNTHETIC_SECRET_SENTINEL",
                                     result.stdout + result.stderr)
                    self.assertEqual(result.stderr, "")
                self.assertEqual(before, {path: path.read_bytes()
                                          for path in (installed, token_file, journal)})
                self.assertEqual(list(work.iterdir()), [])
                self.assertEqual(requests, [])
                entry_tree = ast.dump(ast.parse((ENG /
                    "callable_isolated_v2_effect_entry.py").read_text()))
                self.assertFalse(any(name in entry_tree for name in
                    ("read_execution_token", "reserve_attempt", "post_pull")))
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
                    audit_script, str(installed), str(token_file), str(journal)],
                    cwd=work, env=environment, capture_output=True, text=True,
                    timeout=10, check=False)
                self.assertEqual(audited.returncode, 0, audited.stderr)
                self.assertEqual(json.loads(audited.stdout)["reason"], "no-grant")
                self.assertEqual(audited.stderr, "")
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
