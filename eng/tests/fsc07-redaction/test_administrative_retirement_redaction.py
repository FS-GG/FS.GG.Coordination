"""Offline refusal-output controls for the native retirement adapter.

All GitHub and git calls are replaced before invoking an adapter entrypoint.
"""

import importlib.util
import pathlib
import subprocess
import sys
import traceback
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
        response = subprocess.CompletedProcess([], 1, SENTINEL.encode(), ("HTTP 500 " + SENTINEL).encode())
        with mock.patch.object(retirement.subprocess, "run", return_value=response) as run:
            with self.assertRaises(retirement.UnknownEffect) as caught:
                retirement.gh_request("repos/FS-GG/fixture/pulls/9", "PATCH", {"state": "closed"})
        self.assertEqual(1, run.call_count)
        self.assertIn("github-patch-unknown", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))

    def test_git_and_gh_process_exceptions_never_surface_raw_text(self):
        calls = [
            lambda: retirement.gh_request("repos/FS-GG/fixture/pulls/9", "PATCH", {"state": "closed"}),
            lambda: retirement.gh_pages("repos/FS-GG/fixture/pulls"),
            lambda: retirement.gh_graphql("FS-GG/fixture", 9),
            lambda: retirement.gh_graphql_mutation("disable-auto-merge", "PR_synthetic"),
            lambda: retirement.push_retirement_head({}, {}),
            lambda: retirement.native_run(["git", "status"], "git-command-unavailable"),
        ]
        for call in calls:
            for failure in (OSError(SENTINEL), subprocess.TimeoutExpired(
                    ["gh", SENTINEL], 1, output=SENTINEL.encode(), stderr=SENTINEL.encode())):
                with self.subTest(call=call, failure=type(failure).__name__):
                    with mock.patch.object(retirement.subprocess, "run", side_effect=failure):
                        with self.assertRaises(retirement.UnknownEffect) as caught:
                            call()
                    self.assertNotIn(SENTINEL, str(caught.exception))
                    self.assertNotIn(SENTINEL, "".join(traceback.format_exception(caught.exception)))

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
        response = subprocess.CompletedProcess([], 1, SENTINEL.encode(), ("remote: " + SENTINEL).encode())
        with (mock.patch.object(retirement, "observe", return_value=snapshot) as observe,
              mock.patch.object(retirement, "checkpoint", side_effect=lambda _c, v, _s, _p: v),
              mock.patch.object(retirement, "push_retirement_head", return_value=response) as push):
            with self.assertRaises(retirement.Refused) as caught:
                retirement.advance_retirement_head(config, value)
        self.assertEqual(2, observe.call_count)
        self.assertEqual(1, push.call_count)
        self.assertIn("retirement-head-push-refused", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))

    def test_native_readback_and_changed_filename_do_not_surface_values(self):
        config = {"operationId": "fsc07-test-operation", "branchRef": "refs/heads/fixture",
                  "baseRef": "refs/heads/main", "repository": "FS-GG/fixture"}
        value = {"stage": "planned", "cleanupRequired": False, "effects": []}
        first = {"permanentRule": None}
        second = {"permanentRule": {"id": 77, "name": SENTINEL}}
        with (mock.patch.object(retirement, "observe", side_effect=[first, second]),
              mock.patch.object(retirement, "checkpoint", side_effect=lambda _c, v, _s, _p, **_kw: v),
              mock.patch.object(retirement, "gh_request", return_value=({}, {}, b"{}")),
              mock.patch.object(retirement, "retain_census", return_value=second)):
            with self.assertRaises(retirement.Refused) as caught:
                retirement.create_rule(config, value, True)
        self.assertEqual("permanent-rule-readback", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))

        candidate = {"truncated": False, "tree": [
            {"type": "blob", "path": SENTINEL, "sha": "a" * 40}]}
        parent = {"truncated": False, "tree": []}
        files = [{"filename": SENTINEL, "status": "added", "sha": "a" * 40}]
        merged = {"truncated": False, "tree": [
            {"type": "blob", "path": SENTINEL, "sha": "b" * 40}]}
        with self.assertRaises(retirement.Refused) as caught:
            retirement.validate_changed_paths(candidate, parent, files, merged)
        self.assertEqual("merged-changed-path-mismatch", str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
