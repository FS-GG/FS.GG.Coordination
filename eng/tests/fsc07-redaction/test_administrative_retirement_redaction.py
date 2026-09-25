"""Offline refusal-output controls for the native retirement adapter.

All GitHub and git calls are replaced before invoking an adapter entrypoint.
"""

import importlib.util
import pathlib
import subprocess
import sys
import unittest
from unittest import mock


sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).resolve().parents[2] / "administrative-retirement.py"
SPEC = importlib.util.spec_from_file_location("fsc07_administrative_retirement", SOURCE)
retirement = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(retirement)
SENTINEL = "FSC07_OFFLINE_CREDENTIAL_SENTINEL"


class RedactionBoundaryTests(unittest.TestCase):
    def test_gh_error_does_not_surface_command_stderr(self):
        response = subprocess.CompletedProcess([], 1, b"", ("HTTP 500 " + SENTINEL).encode())
        with mock.patch.object(retirement.subprocess, "run", return_value=response) as run:
            with self.assertRaises(retirement.UnknownEffect) as caught:
                retirement.gh_request("repos/FS-GG/fixture/pulls/9", "PATCH", {"state": "closed"})
        self.assertEqual(1, run.call_count)
        self.assertIn("github-patch-unknown", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))

    def test_404_readback_remains_absent_without_exposing_stderr(self):
        response = subprocess.CompletedProcess([], 1, b"", ("HTTP 404 " + SENTINEL).encode())
        with mock.patch.object(retirement.subprocess, "run", return_value=response):
            value, _, _ = retirement.gh_request("repos/FS-GG/fixture/branches/old", allow_not_found=True)
        self.assertEqual({"status": "absent", "httpStatus": 404}, value)
        self.assertNotIn(SENTINEL, repr(value))

    def test_push_refusal_does_not_surface_command_stderr_or_retry(self):
        config = {"branchRef": "refs/heads/fixture", "candidateHead": "a" * 40, "retirementHead": "b" * 40}
        value = {"stage": "test", "archive": {}, "effects": []}
        snapshot = {
            "pullRequestDisposition": "open", "observedBranchHead": config["candidateHead"],
            "pullRequestHead": config["candidateHead"], "retirementCommitObserved": False,
        }
        response = subprocess.CompletedProcess([], 1, b"", ("remote: " + SENTINEL).encode())
        with (mock.patch.object(retirement, "observe", return_value=snapshot) as observe,
              mock.patch.object(retirement, "checkpoint", side_effect=lambda _c, v, _s, _p: v),
              mock.patch.object(retirement, "push_retirement_head", return_value=response) as push):
            with self.assertRaises(retirement.Refused) as caught:
                retirement.advance_retirement_head(config, value)
        self.assertEqual(2, observe.call_count)
        self.assertEqual(1, push.call_count)
        self.assertIn("retirement-head-push-refused", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
