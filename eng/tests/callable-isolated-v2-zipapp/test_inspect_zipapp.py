"""Exact archive and clean-install no-effect controls for the inspect zipapp."""

from __future__ import annotations

import contextlib
import copy
import datetime as dt
import hashlib
import http.server
import io
import json
import os
import pathlib
import runpy
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
import unittest.mock
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_zipapp as builder  # noqa: E402
import callable_isolated_v2_entry as entry  # noqa: E402


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _fixture() -> tuple[dict, dict, bytes]:
    helpers = runpy.run_path(str(ENG / "tests/callable-isolated-v2-grant/test_grant_parser.py"))
    value = copy.deepcopy(helpers["fixture"]())
    now = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
    value["authority"]["approvedAt"] = (now - dt.timedelta(minutes=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
    value["authority"]["expiresAt"] = (now + dt.timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%SZ")
    value["credential"]["expiresAt"] = (now + dt.timedelta(minutes=15)).strftime("%Y-%m-%dT%H:%M:%SZ")
    return value, helpers["replay"](value), helpers["canonical"](value)


def _inputs(directory: pathlib.Path) -> tuple[list[str], tuple[pathlib.Path, ...]]:
    value, replay, raw = _fixture()
    grant = directory / "grant.json"
    expected = directory / "expected.json"
    observation = directory / "replay.json"
    grant.write_bytes(raw)
    expected.write_bytes(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())
    observation.write_bytes(json.dumps(replay, sort_keys=True, separators=(",", ":")).encode())
    return (["inspect-grant", "--grant", str(grant), "--digest", _sha(raw),
             "--expected", str(expected), "--replay", str(observation)],
            (grant, expected, observation))


class InspectZipappTests(unittest.TestCase):
    def test_two_clean_builds_are_identical_and_match_committed_manifest(self):
        with tempfile.TemporaryDirectory() as left_dir, tempfile.TemporaryDirectory() as right_dir:
            left = pathlib.Path(left_dir) / builder.ARCHIVE_NAME
            right = pathlib.Path(right_dir) / builder.ARCHIVE_NAME
            first = builder.build(left)
            second = builder.build(right)
            self.assertEqual(left.read_bytes(), right.read_bytes())
            self.assertEqual(first, second)
            committed = json.loads((ROOT / "work/gs2-09-9-isolated-operator-rotation/zipapp-manifest.json").read_text())
            self.assertEqual(first, committed)
            self.assertEqual(first["archiveSha256"], _sha(left.read_bytes()))
            with zipfile.ZipFile(left) as archive:
                self.assertEqual(archive.namelist(), sorted(builder.MEMBERS))
                for member in first["members"]:
                    info = archive.getinfo(member["path"])
                    self.assertEqual(info.date_time, builder.FIXED_TIME)
                    self.assertEqual(info.compress_type, zipfile.ZIP_STORED)
                    self.assertEqual(info.external_attr >> 16, 0o100644)
                    self.assertEqual(_sha(archive.read(member["path"])), member["sha256"])
                    self.assertEqual(_sha((ROOT / member["source"]).read_bytes()), member["sha256"])

    def test_clean_installed_archive_has_no_token_post_or_journal_effect(self):
        requests: list[str] = []

        class Canary(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                requests.append(self.path)
                self.send_response(500)
                self.end_headers()

            def log_message(self, _format, *_args):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Canary)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as temporary:
                directory = pathlib.Path(temporary)
                installed = directory / builder.ARCHIVE_NAME
                builder.build(installed)
                work = directory / "clean-cwd"
                work.mkdir()
                journal = directory / "native-journal-sentinel"
                journal.write_text("untouched")
                env = os.environ.copy()
                env.update({
                    "GITHUB_TOKEN": "secret-token-sentinel",
                    "GH_TOKEN": "secret-token-sentinel",
                    "V2_ISOLATED_EXECUTION_TOKEN": "secret-token-sentinel",
                    "FSGG_V2_JOURNAL_PATH": str(journal),
                    "GITHUB_API_URL": f"http://127.0.0.1:{server.server_port}",
                    "PYTHONPATH": str(ENG),
                })

                def invoke(*arguments: str) -> subprocess.CompletedProcess[str]:
                    return subprocess.run([sys.executable, "-I", str(installed), *arguments],
                                          cwd=work, env=env, capture_output=True, text=True,
                                          timeout=10, check=False)

                absent = invoke("inspect-grant")
                self.assertEqual(absent.returncode, 2)
                self.assertEqual(absent.stdout, "")
                self.assertEqual(absent.stderr.strip(), "inspect-refused:inspect-arguments")
                for invalid in ((), ("execute-native-pull",),
                                ("inspect-grant", "--token-env", "V2_ISOLATED_EXECUTION_TOKEN")):
                    refused = invoke(*invalid)
                    self.assertEqual(refused.returncode, 2)
                    self.assertEqual(refused.stdout, "")

                arguments, paths = _inputs(directory)
                before = {path.name: path.read_bytes() for path in (*paths, journal, installed)}
                exact = invoke(*arguments)
                self.assertEqual(exact.returncode, 0, exact.stderr)
                result = json.loads(exact.stdout)
                self.assertEqual(result["state"], "parsed-not-authorized")
                self.assertIs(result["authorized"], False)
                self.assertIs(result["canDispatch"], False)
                self.assertEqual(result["liveEffects"], 0)
                self.assertEqual(exact.stderr, "")
                self.assertEqual(before, {path.name: path.read_bytes() for path in (*paths, journal, installed)})
                self.assertEqual(list(work.iterdir()), [])
                self.assertEqual(requests, [])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_entry_never_reads_token_or_effect_ports_without_authority(self):
        class DenyEnvironment(dict):
            reads: list[str] = []

            def __getitem__(self, key):
                self.reads.append(key)
                raise AssertionError("environment-read")

            def get(self, key, default=None):
                self.reads.append(key)
                raise AssertionError("environment-read")

            def __contains__(self, key):
                self.reads.append(key)
                raise AssertionError("environment-read")

        with tempfile.TemporaryDirectory() as temporary:
            arguments, paths = _inputs(pathlib.Path(temporary))
            allowed = {str(path) for path in paths}
            opened: list[str] = []
            actual_open = os.open

            def guarded_open(path, flags, *rest):
                opened.append(str(path))
                if str(path) not in allowed:
                    raise AssertionError("native-journal-or-foreign-file")
                return actual_open(path, flags, *rest)

            environment = DenyEnvironment()
            with (unittest.mock.patch.object(entry.os, "environ", environment),
                  unittest.mock.patch.object(entry.os, "open", side_effect=guarded_open),
                  unittest.mock.patch.object(socket.socket, "connect") as connect,
                  contextlib.redirect_stdout(io.StringIO()) as stdout,
                  contextlib.redirect_stderr(io.StringIO()) as stderr):
                self.assertEqual(entry.main(["inspect-grant"]), 2)
                self.assertEqual(opened, [])
                self.assertEqual(entry.main(arguments), 0, stderr.getvalue())
                self.assertEqual(set(opened), allowed)
                self.assertEqual(len(opened), 3)
                self.assertEqual(json.loads(stdout.getvalue())["canDispatch"], False)
                self.assertEqual(environment.reads, [])
                connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
