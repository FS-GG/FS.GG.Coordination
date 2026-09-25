#!/usr/bin/env python3
"""Fake native target policy/check/review controls for the inactive Main gate."""

from __future__ import annotations

import copy
import importlib.util
import pathlib
import sys
import unittest

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).resolve().parents[1] / "eng/github-v1-admission-target-eligibility.py"
spec = importlib.util.spec_from_file_location("v1_target_eligibility", SOURCE)
gate = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = gate
spec.loader.exec_module(gate)

BASE = "a" * 40
HEAD = "b" * 40
TEST_MERGE = "c" * 40
NOW = 1790323200  # 2026-09-25 08:00 UTC
P = gate.PREFIX


class FakeGitHub:
    def __init__(self):
        self.protection = {
            "enforce_admins": {"enabled": True},
            "required_conversation_resolution": {"enabled": False},
            "required_status_checks": {
                "checks": [{"context": "gate", "app_id": 15368}],
            },
            "required_pull_request_reviews": {
                "required_approving_review_count": 1,
                "dismiss_stale_reviews": True,
                "require_code_owner_reviews": False,
                "require_last_push_approval": False,
            },
        }
        self.effective = [{"type": "update", "ruleset_id": 9}]
        self.summaries = [{"id": 9, "target": "branch", "enforcement": "active"}]
        self.detail = {
            "id": 9, "target": "branch", "enforcement": "active",
            "conditions": {"ref_name": {"include": ["refs/heads/main"], "exclude": []}},
            "rules": [{"type": "update"}],
            "bypass_actors": [{"actor_id": 77, "actor_type": "Integration",
                               "bypass_mode": "pull_request"}],
        }
        self.checks = {
            HEAD: [{"id": 11, "name": "gate", "head_sha": HEAD,
                    "check_suite": {"id": 21},
                    "app": {"id": 15368}, "status": "completed",
                    "conclusion": "success", "completed_at": "2026-09-25T01:00:00Z"}],
            TEST_MERGE: [{"id": 12, "name": "gate", "head_sha": TEST_MERGE,
                          "check_suite": {"id": 22},
                          "app": {"id": 15368}, "status": "completed",
                          "conclusion": "success", "completed_at": "2026-09-25T01:10:00Z"}],
        }
        self.statuses = {HEAD: [], TEST_MERGE: []}
        self.test_has_suite = True
        self.reviews = [{"id": 101, "user": {"id": 42}, "state": "APPROVED",
                         "commit_id": HEAD, "submitted_at": "2026-09-25T01:20:00Z"}]
        self.parents = [BASE, HEAD]
        self.calls = []

    def policy(self):
        rules = {"effective": self.effective, "summaries": self.summaries,
                 "details": [self.detail]}
        return gate.InstalledTargetPolicy(
            gate.REPOSITORY_ID, 9, 77,
            gate._digest(self.protection), gate._digest(rules),
            (("gate", 15368),), (42,),
        )

    def read(self, path):
        self.calls.append(path)
        pr = {"number": 3695, "id": 700, "node_id": "PR_synthetic",
              "user": {"id": 43},
              "state": "open", "draft": False, "merged": False,
              "base": {"ref": "main", "sha": BASE,
                       "repo": {"id": gate.REPOSITORY_ID}},
              "head": {"ref": "routine/test", "sha": HEAD,
                       "repo": {"id": gate.REPOSITORY_ID}},
              "merge_commit_sha": TEST_MERGE}
        bodies = {
            f"{P}/pulls/3695": pr,
            f"{P}/branches/main": {"name": "main", "protected": True,
                                    "commit": {"sha": BASE}},
            f"{P}/branches/routine%2Ftest": {"name": "routine/test",
                                              "commit": {"sha": HEAD}},
            f"{P}/git/commits/{TEST_MERGE}": {
                "sha": TEST_MERGE,
                "parents": [{"sha": value} for value in self.parents],
            },
            f"{P}/git/commits/{HEAD}": {
                "sha": HEAD, "committer": {"date": "2026-09-25T00:00:00Z"},
            },
            f"{P}/branches/main/protection": self.protection,
            f"{P}/rules/branches/main": self.effective,
            f"{P}/rulesets/9": self.detail,
        }
        if path in bodies:
            body = bodies[path]
        elif path == f"{P}/rulesets?includes_parents=true&per_page=100&page=1":
            body = self.summaries
        elif path == f"{P}/rulesets?includes_parents=true&per_page=100&page=2":
            body = []
        elif path == f"{P}/pulls/3695/reviews?per_page=100&page=1":
            body = self.reviews
        elif path == f"{P}/pulls/3695/reviews?per_page=100&page=2":
            body = []
        else:
            body = None
            for commit in (HEAD, TEST_MERGE):
                suite_id = 21 if commit == HEAD else 22
                suite_path = f"{P}/commits/{commit}/check-suites?per_page=100&page=1"
                check_path = (
                    f"{P}/check-suites/{suite_id}/check-runs?per_page=100&filter=all&page=1"
                )
                status_path = f"{P}/commits/{commit}/statuses?per_page=100&page="
                if path == suite_path:
                    suites = ([{"id": suite_id, "head_sha": commit}]
                              if commit == HEAD or self.test_has_suite else [])
                    body = {"total_count": len(suites), "check_suites": suites}
                elif path == check_path:
                    body = {"total_count": len(self.checks[commit]),
                            "check_runs": self.checks[commit]}
                elif path == status_path + "1":
                    body = self.statuses[commit]
                elif path == status_path + "2":
                    body = []
            if body is None:
                raise AssertionError(path)
        return gate.NATIVE.NativeResponse(copy.deepcopy(body), None)


class TargetEligibilityTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakeGitHub()

    def test_complete_policy_checks_reviews_and_two_reads(self):
        result = gate.collect_two(self.fake.read, self.fake.policy(), 3695, NOW)
        self.assertEqual(result["head_sha"], HEAD)
        self.assertEqual(result["test_merge_sha"], TEST_MERGE)
        self.assertEqual(result["check_target"], "test_merge")
        self.assertEqual(len([x for x in self.fake.calls if x.endswith("/pulls/3695")]), 2)

    def test_complete_empty_test_merge_uses_head_checks(self):
        self.fake.test_has_suite = False
        self.fake.checks[TEST_MERGE] = []
        result = gate.collect_two(self.fake.read, self.fake.policy(), 3695, NOW)
        self.assertEqual(result["check_target"], "head")
        self.assertFalse(any("check-suites/22/check-runs" in path
                             for path in self.fake.calls))
        self.fake = FakeGitHub()
        self.fake.checks[TEST_MERGE][0]["name"] = "unrelated"
        with self.assertRaisesRegex(gate.Refused, "eligibility-check-shape"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_foreign_or_failed_required_check_refuses(self):
        for change in (lambda: self.fake.checks[HEAD][0]["app"].update(id=999),
                       lambda: self.fake.checks[HEAD][0].update(conclusion="failure")):
            self.fake = FakeGitHub()
            change()
            with self.assertRaisesRegex(gate.Refused, "eligibility-check-result"):
                gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_old_head_review_and_later_change_request_refuse(self):
        self.fake.reviews[0]["commit_id"] = BASE
        with self.assertRaisesRegex(gate.Refused, "eligibility-review-approval"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)
        self.fake = FakeGitHub()
        self.fake.reviews[0]["submitted_at"] = "2026-09-24T23:59:59Z"
        with self.assertRaisesRegex(gate.Refused, "eligibility-review-approval"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)
        self.fake = FakeGitHub()
        self.fake.reviews.append({"id": 102, "user": {"id": 42},
                                  "state": "CHANGES_REQUESTED", "commit_id": HEAD,
                                  "submitted_at": "2026-09-25T01:30:00Z"})
        with self.assertRaisesRegex(gate.Refused, "eligibility-review-blocking"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_unproven_conversation_or_bypass_refuses(self):
        self.fake.protection["required_conversation_resolution"]["enabled"] = True
        with self.assertRaisesRegex(gate.Refused, "eligibility-protection"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)
        self.fake = FakeGitHub()
        self.fake.detail["bypass_actors"].append(
            {"actor_id": 999, "actor_type": "Integration", "bypass_mode": "always"})
        with self.assertRaisesRegex(gate.Refused, "eligibility-exclusive-writer"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_moved_test_merge_and_overlapping_status_refuse(self):
        self.fake.parents = ["9" * 40, HEAD]
        with self.assertRaisesRegex(gate.Refused, "eligibility-test-merge"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)
        self.fake = FakeGitHub()
        self.fake.statuses[HEAD] = [{"id": 8, "context": "gate", "state": "success"}]
        with self.assertRaisesRegex(gate.Refused, "eligibility-overlapping-status"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_self_approval_and_foreign_suite_refuse(self):
        original = self.fake.read

        def self_author(path):
            result = original(path)
            if path == f"{P}/pulls/3695":
                result.body["user"]["id"] = 42
            return result

        with self.assertRaisesRegex(gate.Refused, "eligibility-self-review"):
            gate.collect_once(self_author, self.fake.policy(), 3695, NOW)
        self.fake = FakeGitHub()
        self.fake.checks[HEAD][0]["check_suite"]["id"] = 999
        with self.assertRaisesRegex(gate.NATIVE.Refused, "native-target-check-runs"):
            gate.collect_once(self.fake.read, self.fake.policy(), 3695, NOW)

    def test_unqualified_inherited_repository_selector_refuses(self):
        inherited = copy.deepcopy(self.fake.detail)
        inherited["conditions"]["repository_name"] = {
            "include": [".github"], "exclude": [],
        }
        with self.assertRaisesRegex(gate.Refused, "eligibility-rule-conditions"):
            gate._rule_applies_main(inherited)


if __name__ == "__main__":
    unittest.main()
