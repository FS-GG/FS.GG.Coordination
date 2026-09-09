#!/usr/bin/env python3
"""Focused, offline controls for the GS2-08.2 App-authenticated capture path."""

import importlib.util
import pathlib
import sys
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).with_name("capture-github-ledger-protection.py")
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("ledger_capture", SCRIPT)
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)


class AppCaptureTests(unittest.TestCase):
    def test_token_refusal_is_fail_closed_and_sanitized(self):
        with mock.patch.object(CAPTURE, "mint_installation_token", return_value=None):
            raw, value = CAPTURE.app_selected_repositories(4882140, 160261608, 3)
        self.assertEqual(b"", raw)
        self.assertEqual("unknown", value["state"])
        self.assertEqual("app-installation-token-refused", value["reason"])
        self.assertNotIn("token", value)

    def test_mint_response_bytes_and_token_are_not_retained(self):
        signing = mock.Mock(returncode=0, stdout=b"signature")
        secret = "installation-token-must-not-be-retained"
        with mock.patch.object(CAPTURE.subprocess, "run", return_value=signing), mock.patch.object(
            CAPTURE, "app_request", return_value=(b"response-containing-" + secret.encode(), {"token": secret})
        ) as request:
            token = CAPTURE.mint_installation_token(4882140, 160261608, 3)
        self.assertEqual(secret, token)
        self.assertEqual("POST", request.call_args.args[2])
        self.assertFalse(request.call_args.kwargs["retain_raw"])
        self.assertNotIn(secret, repr(request.call_args))

    def test_selected_repository_pages_refuse_after_incomplete_read(self):
        first = {"total_count": 101, "repositories": [{"full_name": "FS-GG/FS.GG.Coordination.Authority"}] * 100}
        denied = CAPTURE.provider_refusal(403)
        with mock.patch.object(CAPTURE, "mint_installation_token", return_value="ephemeral"), mock.patch.object(
            CAPTURE, "app_request", side_effect=[(b"page-one", first), (b"", denied)]
        ):
            raw, value = CAPTURE.app_selected_repositories(4882140, 160261608, 3)
        self.assertEqual(b"page-one", raw)
        self.assertEqual("unknown", value["state"])

    def test_exact_full_page_terminates_when_total_count_is_satisfied(self):
        page = {"total_count": 100, "repositories": [{"full_name": "FS-GG/FS.GG.Coordination.Authority"}] * 100}
        with mock.patch.object(CAPTURE, "mint_installation_token", return_value="ephemeral"), mock.patch.object(
            CAPTURE, "app_request", return_value=(b"complete-page", page)
        ) as request:
            raw, values = CAPTURE.app_selected_repositories(4882140, 160261608, 3)
        self.assertEqual(b"complete-page", raw)
        self.assertEqual([page], values)
        request.assert_called_once()

    def test_binding_change_breaks_two_pass_continuity(self):
        old = {"ordinaryWriterAppId": 1, "cutoverWriterAppId": 2, "controlIssueNumber": None}
        new = {"ordinaryWriterAppId": 1, "cutoverWriterAppId": 2, "controlIssueNumber": 7}
        continuity, gaps = CAPTURE.compare_continuity({"normalizedSetSha256": "a", "bindings": old}, "a", new)
        self.assertEqual("drift", continuity)
        self.assertEqual(["binding-drift"], gaps)


if __name__ == "__main__":
    result = unittest.main(exit=False, verbosity=0).result
    if result.wasSuccessful():
        print("CAPTURE_APP_AUTH_TESTS_OK tests=5 secrets_retained=0")
    raise SystemExit(0 if result.wasSuccessful() else 1)
