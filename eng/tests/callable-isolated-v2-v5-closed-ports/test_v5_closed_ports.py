"""Source-only v5 port proposal cannot reach an authority or effect port."""

import contextlib
import dataclasses
import io
import os
import pathlib
import socket
import sqlite3
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import callable_isolated_v2_effect_v5_ports as proposal
import build_callable_isolated_v2_effect_scaffold as builder


class DenyAccess:
    def __getattribute__(self, name):
        raise AssertionError(f"port-access:{name}")


class ClosedV5PortTests(unittest.TestCase):
    def test_closed_result_cannot_be_constructed_or_replaced_as_authority(self):
        for changed in ({"authorized": True}, {"can_dispatch": True},
                        {"live_effects": 1}, {"exit_code": 0},
                        {"schema": "foreign"}):
            with self.subTest(changed=changed):
                with self.assertRaises((TypeError, ValueError)):
                    proposal.ClosedV5Decision("caller", **changed)
                with self.assertRaises((TypeError, ValueError)):
                    dataclasses.replace(proposal.ClosedV5Decision("caller"),
                                        **changed)

    def test_colliding_role_objects_still_cannot_open_any_port(self):
        shared = DenyAccess()
        ports = proposal.ProposedPorts(*([shared] * len(dataclasses.fields(
            proposal.ProposedPorts))))
        result = proposal.inspect_closed(shared, shared, ports,
                                         b"SYNTHETIC_UNTRUSTED_GRANT")
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.exit_code, 78)

    def test_absent_and_untrusted_grant_refuse_without_port_access(self):
        for grant in (None, b"SYNTHETIC_UNTRUSTED_GRANT", DenyAccess()):
            with self.subTest(grant_type=type(grant).__name__):
                with (mock.patch("builtins.open", side_effect=AssertionError("file")),
                      mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
                      mock.patch.object(socket.socket, "connect",
                                        side_effect=AssertionError("provider")),
                      mock.patch.object(sqlite3, "connect",
                                        side_effect=AssertionError("journal")),
                      contextlib.redirect_stdout(io.StringIO()) as output):
                    result = proposal.inspect_closed(DenyAccess(), DenyAccess(),
                                                     DenyAccess(), grant)
                self.assertEqual(output.getvalue(), "")
                self.assertFalse(result.authorized)
                self.assertFalse(result.can_dispatch)
                self.assertEqual(result.live_effects, 0)
                self.assertEqual(result.exit_code, 78)
                self.assertEqual(result.schema, proposal.RESULT_SCHEMA)
                self.assertEqual(result.reason, "no-grant" if grant is None
                                 else "v5-protected-authority-unavailable")

    def test_typed_proposal_remains_outside_closed_installed_artifact(self):
        self.assertNotIn("callable_isolated_v2_effect_v5_ports.py",
                         builder.MEMBERS.values())
        self.assertFalse(hasattr(proposal, "execute_native_pull"))
        self.assertFalse(hasattr(proposal, "dispatch"))
        entry = (ENG / "callable_isolated_v2_effect_entry.py").read_text()
        self.assertIn("return 78", entry)
        for path in (builder.WORKFLOW,
                     ".github/workflows/callable-isolated-v2-effect-release.yml"):
            workflow = (ROOT / path).read_text()
            self.assertIn("if: ${{ false }}", workflow)
            self.assertIn("run: exit 78", workflow)


if __name__ == "__main__":
    unittest.main()
