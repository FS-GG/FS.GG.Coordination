"""Closed proposed vault entry must refuse before any protected port."""

import contextlib
import dataclasses
import io
import json
import os
import pathlib
import socket
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_effect_scaffold as builder
import callable_isolated_v2_v5_vault_no_grant_entry as entry


class DenyPort:
    def __getattribute__(self, name):
        raise AssertionError(f"protected-port-access:{name}")


class VaultNoGrantEntryTests(unittest.TestCase):
    def test_no_grant_and_untrusted_grant_touch_no_port(self):
        for grant, reason in ((None, "no-grant"),
                              (b"UNTRUSTED_GRANT_SECRET", "protected-vault-unavailable"),
                              (DenyPort(), "protected-vault-unavailable")):
            with self.subTest(reason=reason):
                with mock.patch("builtins.open", side_effect=AssertionError("file")), \
                     mock.patch.object(os, "getenv", side_effect=AssertionError("token")), \
                     mock.patch.object(socket.socket, "connect",
                                       side_effect=AssertionError("provider")), \
                     mock.patch.object(sqlite3, "connect",
                                       side_effect=AssertionError("journal")):
                    result = entry.inspect_closed(DenyPort(), DenyPort(),
                                                   DenyPort(), grant)
                self.assertEqual(result.reason, reason)
                self.assertEqual(result.exit_code, 78)
                self.assertFalse(result.authorized)
                self.assertFalse(result.can_dispatch)
                self.assertEqual(result.live_effects, 0)

    def test_direct_main_and_alternate_args_refuse_without_secret(self):
        for args, reason in ((["execute-native-pull"], "no-grant"),
                             ([], "entry-closed"),
                             (["execute-native-pull", "--grant=SECRET"],
                              "entry-closed")):
            with self.subTest(args=args):
                output = io.StringIO()
                with mock.patch.object(os, "getenv", side_effect=AssertionError("token")), \
                     mock.patch.object(socket.socket, "connect",
                                       side_effect=AssertionError("provider")), \
                     mock.patch.object(sqlite3, "connect",
                                       side_effect=AssertionError("journal")), \
                     contextlib.redirect_stdout(output):
                    code = entry.main(args)
                self.assertEqual(code, 78)
                self.assertNotIn("SECRET", output.getvalue())
                value = json.loads(output.getvalue())
                self.assertEqual(value["reason"], reason)
                self.assertEqual(value["authorized"], False)
                self.assertEqual(value["canDispatch"], False)
                self.assertEqual(value["liveEffects"], 0)

    def test_isolated_file_entry_refuses_from_clean_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            environment = {"PATH": os.environ.get("PATH", ""),
                           "GS2_EXECUTION_TOKEN": "SECRET_SENTINEL"}
            process = subprocess.run([sys.executable, "-I", "-S",
                str(ENG / "callable_isolated_v2_v5_vault_no_grant_entry.py"),
                "execute-native-pull"], cwd=directory, env=environment,
                text=True, capture_output=True, check=False)
        self.assertEqual(process.returncode, 78)
        self.assertEqual(process.stderr, "")
        self.assertNotIn("SECRET_SENTINEL", process.stdout)
        self.assertEqual(json.loads(process.stdout)["reason"], "no-grant")

    def test_result_fields_cannot_be_replaced_with_authority(self):
        result = entry.inspect_closed(None, None, None, None)
        for field, value in (("authorized", True), ("can_dispatch", True),
                             ("live_effects", 1), ("exit_code", 0)):
            with self.subTest(field=field):
                with self.assertRaises((TypeError, ValueError)):
                    dataclasses.replace(result, **{field: value})

    def test_proposed_entry_is_not_in_closed_archive_or_enabled_workflow(self):
        self.assertNotIn("callable_isolated_v2_v5_vault_no_grant_entry.py",
                         builder.MEMBERS.values())
        for path in (builder.WORKFLOW,
                     ".github/workflows/callable-isolated-v2-effect-release.yml"):
            workflow = (ROOT / path).read_text()
            self.assertIn("if: ${{ false }}", workflow)
            self.assertIn("run: exit 78", workflow)


if __name__ == "__main__":
    unittest.main()
