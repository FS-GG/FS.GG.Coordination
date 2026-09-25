#!/usr/bin/env python3
"""Adversarial offline controls for the inactive versioned readback proof."""

import dataclasses
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest
import urllib.error


SOURCE = pathlib.Path(__file__).resolve().parents[2] / "callable-cli-isolated-operation-v2.py"
SPEC = importlib.util.spec_from_file_location("isolated_operation_v2", SOURCE)
operator = importlib.util.module_from_spec(SPEC)
import sys
sys.modules[SPEC.name] = operator
SPEC.loader.exec_module(operator)

SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40
DIGEST = "d" * 64
SENTINEL = "FSC07_OFFLINE_CREDENTIAL_SENTINEL"


def pull_expected():
    return operator.ExpectedPull(operator.OPERATION_IDENTITY, 1, 44,
                                 "FS-GG/disposable", "refs/heads/source", SHA_A,
                                 "refs/heads/main", SHA_B)


def pull_observed():
    return operator.PullCensus(True, 44, SHA_A, SHA_B, ({
        "number": 8, "node_id": "PR_8", "state": "open", "draft": False,
        "merged": False,
        "head": {"ref": "source", "sha": SHA_A,
                 "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
        "base": {"ref": "main", "sha": SHA_B,
                 "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
    },), DIGEST)


def protection_expected():
    return operator.ExpectedProtection(operator.OPERATION_IDENTITY, 1, 44,
                                       "main", "required-check", 17)


def protection_observed():
    return operator.ProtectionReadback(True, 44, "main", True, {
        "required_status_checks": {
            "strict": True,
            "checks": [{"context": "required-check", "app_id": 17}],
        },
        "enforce_admins": {"enabled": False},
        "required_pull_request_reviews": None,
        "restrictions": None,
        "allow_force_pushes": {"enabled": False},
        "allow_deletions": {"enabled": False},
    }, DIGEST)


class VersionedReadbackTests(unittest.TestCase):
    def assert_unknown(self, value):
        self.assertIsInstance(value, operator.Unknown)
        self.assertNotIn(SENTINEL, repr(value))

    def test_pull_accepts_only_two_complete_exact_reads(self):
        reads = iter((pull_observed(), pull_observed()))
        result = operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: next(reads),
            provider_response={"status": "unknown", "body": SENTINEL})
        self.assertEqual(result, operator.ExactPull(8, "PR_8", DIGEST))

    def test_pull_rejects_wrong_head_base_repo_and_identity(self):
        observed = pull_observed()
        for wrong in (
            dataclasses.replace(observed, source_branch_sha=SHA_C),
            dataclasses.replace(observed, base_branch_sha=SHA_C),
            dataclasses.replace(observed, repository_id=45),
        ):
            with self.subTest(wrong=wrong):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: wrong))
        for side, field, value in (
            ("head", "ref", "foreign"), ("head", "sha", SHA_C),
            ("head", "repo", {"id": 44, "full_name": "FS-GG/foreign"}),
            ("base", "ref", "foreign"), ("base", "sha", SHA_C),
            ("base", "repo", {"id": 45, "full_name": "FS-GG/disposable"}),
        ):
            pull = dict(observed.pulls[0])
            pull[side] = {**pull[side], field: value}
            wrong = dataclasses.replace(observed, pulls=(pull,))
            with self.subTest(side=side, field=field):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: wrong))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            dataclasses.replace(pull_expected(), operation_identity="old"),
            lambda: observed))

    def test_pull_rejects_duplicate_incomplete_drift_and_retry(self):
        observed = pull_observed()
        for wrong in (
            dataclasses.replace(observed, pulls=observed.pulls * 2),
            dataclasses.replace(observed, complete=False),
            dataclasses.replace(observed, transcript_sha256=""),
        ):
            self.assert_unknown(operator.classify_pull_after_one_attempt(
                pull_expected(), lambda: wrong))
        reads = iter((observed, dataclasses.replace(observed, base_branch_sha=SHA_C)))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: next(reads)))
        shared = pull_observed()
        count = 0
        def reused_mutating_read():
            nonlocal count
            count += 1
            if count == 2:
                shared.pulls[0]["base"]["sha"] = SHA_C
            return shared
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), reused_mutating_read))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            dataclasses.replace(pull_expected(), write_attempts=2), lambda: observed))

    def test_protection_rejects_force_push_deletion_and_policy_drift(self):
        observed = protection_observed()
        self.assertIsInstance(operator.classify_protection_after_one_attempt(
            protection_expected(), lambda: observed), operator.ExactProtection)
        for key, value in (
            ("allow_force_pushes", {"enabled": True}),
            ("allow_deletions", {"enabled": True}),
            ("allow_force_pushes", None),
            ("required_pull_request_reviews", {"required_approving_review_count": 0}),
            ("required_status_checks", {"strict": True, "checks": [
                {"context": "required-check", "app_id": 18}]}),
        ):
            wrong = dataclasses.replace(observed, policy={**observed.policy, key: value})
            with self.subTest(key=key, value=value):
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: wrong,
                    provider_response={"status": 200}))
        reads = iter((observed, dataclasses.replace(observed, branch="other")))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            protection_expected(), lambda: next(reads)))

    def test_protection_rejects_wrong_branch_repo_incomplete_and_retry(self):
        observed = protection_observed()
        for wrong in (
            dataclasses.replace(observed, repository_id=45),
            dataclasses.replace(observed, branch="foreign"),
            dataclasses.replace(observed, protected=False),
            dataclasses.replace(observed, complete=False),
            dataclasses.replace(observed, transcript_sha256="bad"),
        ):
            self.assert_unknown(operator.classify_protection_after_one_attempt(
                protection_expected(), lambda: wrong))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            dataclasses.replace(protection_expected(), write_attempts=2),
            lambda: observed))

    def test_exception_text_never_escapes(self):
        def failed():
            raise urllib.error.URLError(SENTINEL)
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), failed))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            protection_expected(), failed))

    def test_inspect_binds_v5_and_historical_preflight_without_effect(self):
        root = SOURCE.parents[1]
        preflight_path = root / operator.HISTORICAL_PREFLIGHT
        historical = {
            "path": operator.HISTORICAL_PREFLIGHT,
            "sha256": hashlib.sha256(preflight_path.read_bytes()).hexdigest(),
            "operationIdentity": operator.HISTORICAL_IDENTITY,
            "authority": "historical-observation-only",
        }
        contract = {
            "schema": "fsgg.coordination.callable-isolated-operation-contract/5",
            "identity": operator.OPERATION_IDENTITY,
            "state": "prepared-not-authorized", "authorized": False,
            "source": {"operationSource": operator.SOURCE,
                       "operationSourceSha256": hashlib.sha256(SOURCE.read_bytes()).hexdigest()},
            "qualificationControls": {
                "path": operator.CONTROLS,
                "sha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
            },
            "historicalPreflight": historical,
        }
        contract["contractSha256"] = operator._digest(contract)
        proposal = {
            "schema": "fsgg.coordination.callable-isolated-operation-proposal/5",
            "identity": operator.OPERATION_IDENTITY,
            "state": "prepared-not-authorized", "authorized": False,
            "contract": {"sha256": contract["contractSha256"],
                         "operationSourceSha256": contract["source"]["operationSourceSha256"]},
            "historicalPreflight": historical,
        }
        proposal["proposalSha256"] = operator._digest(proposal)
        with tempfile.TemporaryDirectory() as temp:
            contract_path = pathlib.Path(temp) / "contract.json"
            proposal_path = pathlib.Path(temp) / "proposal.json"
            contract_path.write_text(json.dumps(contract))
            proposal_path.write_text(json.dumps(proposal))
            command = [sys.executable, str(SOURCE), "--contract", str(contract_path),
                       "--proposal", str(proposal_path), "--preflight",
                       str(preflight_path), "inspect"]
            positive = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertEqual(positive.returncode, 0, positive.stderr)
            result = json.loads(positive.stdout)
            self.assertIs(result["authorized"], False)
            self.assertEqual(result["liveEffects"], 0)
            self.assertIs(result["historicalObservationOnly"], True)
            contract["source"]["operationSourceSha256"] = "0" * 64
            contract["contractSha256"] = operator._digest({
                key: value for key, value in contract.items() if key != "contractSha256"})
            contract_path.write_text(json.dumps(contract))
            negative = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertEqual(negative.returncode, 2)
            self.assertEqual(negative.stderr.strip(), "inspect-source-drift")
            self.assertNotIn(SENTINEL, negative.stderr)


if __name__ == "__main__":
    unittest.main()
