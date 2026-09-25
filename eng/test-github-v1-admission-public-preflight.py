#!/usr/bin/env python3
"""Public-only join controls; the native and signature readers have own tests."""

from __future__ import annotations

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-public-preflight.py")
spec = importlib.util.spec_from_file_location("v1_admission_public_preflight", SOURCE)
preflight = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = preflight
spec.loader.exec_module(preflight)


class PublicPreflightTests(unittest.TestCase):
    def setUp(self):
        self.typed = b"synthetic typed admission bytes\n"
        self.plan = {
            "version": 1, "kind": "github_pr_merge", "repository_id": 1269292704,
            "pr_number": 3695, "pr_id": 700, "pr_node_id": "PR_synthetic",
            "base_sha": "a" * 40, "head_sha": "b" * 40,
            "run_id": 123, "check_run_id": 456, "nonce": "ab" * 16,
            "typed_admission_sha256": hashlib.sha256(self.typed).hexdigest(),
        }
        self.job_policy = preflight.JOB.InstalledPolicy(
            ".github/workflows/operating-v1.yml", "c" * 40, "d" * 64,
            "admission", 77, "operating-v1-runtime", 88, "EN_synthetic", "e" * 64,
        )
        self.registered = preflight.OIDC.RegisteredServicePolicy(
            self.job_policy.workflow_path, self.job_policy.workflow_sha,
            self.job_policy.actor_id, self.job_policy.environment_name,
            self.job_policy.environment_node_id, "synthetic-subject",
        )
        self.job = {
            "workflow_path": self.job_policy.workflow_path,
            "run_head": self.job_policy.workflow_sha,
            "workflow_sha": self.job_policy.workflow_sha,
            "run_id": 123, "run_attempt": 1, "actor_id": 77,
            "check_run_id": 456, "environment_name": self.job_policy.environment_name,
            "environment_node_id": self.job_policy.environment_node_id,
            "complete_census_sha256": "f" * 64,
        }
        self.target = {
            key: self.plan[key] for key in
            ("repository_id", "pr_number", "pr_id", "pr_node_id", "base_sha", "head_sha")
        }
        self.target["complete_census_sha256"] = "0" * 64

    def sealed(self, plan=None):
        return json.dumps(self.plan if plan is None else plan, sort_keys=True,
                          separators=(",", ":")).encode() + b"\n"

    def verify(self, sealed=None, typed=None):
        with (mock.patch.object(preflight.JOB, "collect_once", return_value=self.job) as job,
              mock.patch.object(preflight.TARGET, "collect_once", return_value=self.target) as target,
              mock.patch.object(preflight.OIDC, "verify_signed_job", return_value=
                                preflight.OIDC.SignedJobProof("jti", 10, 20, "aud", "1" * 64)) as signed):
            result = preflight.verify_public_preflight(
                self.sealed() if sealed is None else sealed,
                self.typed if typed is None else typed,
                "signed-synthetic", b"public-jwks", self.job_policy,
                self.registered, object(), 12,
            )
            return result, job, target, signed

    def test_exact_public_join_and_audience(self):
        result, job, target, signed = self.verify()
        self.assertEqual(result.sealed_plan_sha256, hashlib.sha256(self.sealed()).hexdigest())
        self.assertEqual(result.token_id, "jti")
        self.assertEqual(result.job_census_sha256, "f" * 64)
        self.assertEqual(result.target_census_sha256, "0" * 64)
        self.assertEqual(job.call_count, 2)
        self.assertEqual(target.call_count, 2)
        self.assertEqual(signed.call_args.args[4],
                         preflight.OIDC.plan_audience(self.sealed(), self.plan["nonce"]))

    def test_noncanonical_duplicate_and_changed_typed_bytes_refuse(self):
        with self.assertRaises(preflight.Refused):
            self.verify(self.sealed().rstrip(b"\n"))
        with self.assertRaises(preflight.Refused):
            self.verify(b'{"version":1,"version":1}\n')
        with self.assertRaises(preflight.Refused):
            self.verify(typed=self.typed + b"changed")

    def test_native_target_drift_refuses_before_oidc(self):
        self.target["base_sha"] = "9" * 40
        with (mock.patch.object(preflight.JOB, "collect_once", return_value=self.job),
              mock.patch.object(preflight.TARGET, "collect_once", return_value=self.target),
              mock.patch.object(preflight.OIDC, "verify_signed_job") as signed):
            with self.assertRaises(preflight.Refused):
                preflight.verify_public_preflight(
                    self.sealed(), self.typed, "signed", b"public-jwks",
                    self.job_policy, self.registered, object(), 12,
                )
            signed.assert_not_called()

    def test_foreign_installed_policy_refuses_before_native_read(self):
        foreign = dataclasses.replace(self.registered, environment_name="foreign")
        with mock.patch.object(preflight.JOB, "collect_once") as job:
            with self.assertRaises(preflight.Refused):
                preflight.verify_public_preflight(
                    self.sealed(), self.typed, "signed", b"public-jwks",
                    self.job_policy, foreign, object(), 12,
                )
            job.assert_not_called()

    def test_job_change_between_combined_passes_refuses_before_oidc(self):
        changed = dict(self.job, complete_census_sha256="9" * 64)
        with (mock.patch.object(preflight.JOB, "collect_once",
                                side_effect=[self.job, changed]),
              mock.patch.object(preflight.TARGET, "collect_once", return_value=self.target),
              mock.patch.object(preflight.OIDC, "verify_signed_job") as signed):
            with self.assertRaisesRegex(preflight.Refused, "public-plan-two-read-drift"):
                preflight.verify_public_preflight(
                    self.sealed(), self.typed, "signed", b"public-jwks",
                    self.job_policy, self.registered, object(), 12,
                )
            signed.assert_not_called()


if __name__ == "__main__":
    unittest.main()
