#!/usr/bin/env python3
"""Adversarial exact-diff fixtures for the two-profile CI pilot."""

import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock


HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("optimistic_profile", HERE / "optimistic-profile.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ProfileFixtures(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.repo = pathlib.Path(self.scratch.name)
        subprocess.run(["git", "init", "-q", str(self.repo)], check=True)
        subprocess.run(["git", "config", "user.name", "Fixture"], cwd=self.repo, check=True)
        subprocess.run(["git", "config", "user.email", "fixture@example.test"], cwd=self.repo, check=True)
        (self.repo / "README.md").write_text("original\n")
        (self.repo / "src/FS.GG.Coordination.Cli").mkdir(parents=True)
        (self.repo / "src/FS.GG.Coordination.Cli/ObserverViewCommand.fs").write_text("read only\n")
        (self.repo / "src/FS.GG.Coordination.Cli/DeliveryCommand.fs").write_text("authority\n")
        self.commit()
        self.base = self.rev()
        self.selection_path = self.repo / "selection.json"
        self.obligation_path = self.repo / "obligation.json"
        self.profile_path = self.repo / "profile.json"
        self.output_path = self.repo / "github-output"
        self.root_before = module.ROOT
        module.ROOT = self.repo
        self.addCleanup(setattr, module, "ROOT", self.root_before)

    def commit(self):
        subprocess.run(["git", "add", "-A"], cwd=self.repo, check=True)
        subprocess.run(["git", "commit", "-qm", "fixture"], cwd=self.repo, check=True)

    def rev(self):
        return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=self.repo).decode().strip()

    def select(self, event="pull_request", disposition="reused", authentic=True, expired=False, event_base=None):
        now = datetime.now(timezone.utc)
        self.obligation_path.write_text(json.dumps({
            "candidate": self.rev(), "baseRevision": self.base, "obligationSha256": "a" * 64
        }))
        self.selection_path.write_text(json.dumps({
            "schema": "fsgg.coordination.qualification-selection/1",
            "candidateObligationSha256": "a" * 64,
            "disposition": disposition, "coherentState": "pending",
            "semanticDelta": {"empty": True}, "selectionSha256": "b" * 64,
            "prior": {"authentic": authentic, "complete": True, "executedReceiptSha256": "c" * 64,
                      "runId": 123, "attempt": 1,
                      "completedAt": (now - timedelta(days=3)).isoformat(),
                      "expiresAt": (now - timedelta(days=1) if expired else now + timedelta(days=30)).isoformat()}
        }))
        with mock.patch.dict(os.environ, {
            "GITHUB_EVENT_NAME": event, "FSGG_READY_PR": "true", "FSGG_PR_BASE_SHA": event_base or self.base,
            "GITHUB_OUTPUT": str(self.output_path)
        }):
            module.profile(self.selection_path, self.obligation_path, self.profile_path)
        return json.loads(self.profile_path.read_text())

    def test_audited_exact_changes_get_scoped_matrix_and_bound_donor(self):
        (self.repo / "README.md").write_text("updated\n")
        (self.repo / "src/FS.GG.Coordination.Cli/ObserverViewCommand.fs").write_text("read only view\n")
        self.commit()
        result = self.select()
        self.assertEqual("scoped", result["profile"])
        self.assertEqual("c" * 64, result["fullDonorReceiptSha256"])
        matrix = json.loads(self.output_path.read_text().split("matrix=", 1)[1])
        self.assertEqual(6, len(matrix["include"]))
        self.assertEqual({"kind": "formal", "shard": "base"}, matrix["include"][0])
        with mock.patch.dict(os.environ, {"GITHUB_EVENT_NAME": "pull_request", "FSGG_READY_PR": "true", "FSGG_PR_BASE_SHA": self.base}):
            with mock.patch.object(module, "verify_full_donor") as donor:
                module.verify_scoped(self.selection_path, self.obligation_path, self.profile_path)
                donor.assert_called_once()

    def test_authority_change_falls_full(self):
        (self.repo / "src/FS.GG.Coordination.Cli/DeliveryCommand.fs").write_text("changed authority\n")
        self.commit()
        result = self.select()
        self.assertEqual("full", result["profile"])
        matrix = json.loads(self.output_path.read_text().split("matrix=", 1)[1])
        self.assertEqual(25, len(matrix["include"]))

    def test_added_unknown_path_and_stale_base_fall_full(self):
        (self.repo / "README.md").write_text("updated\n")
        (self.repo / "unknown.txt").write_text("new\n")
        self.commit()
        self.assertEqual("full", self.select()["profile"])
        self.base = "0" * 40
        self.assertEqual("full", self.select()["profile"])

    def test_current_or_inauthentic_prior_falls_full(self):
        (self.repo / "README.md").write_text("updated\n")
        self.commit()
        self.assertEqual("full", self.select(disposition="current")["profile"])
        self.assertEqual("full", self.select(authentic=False)["profile"])
        self.assertEqual("full", self.select(expired=True)["profile"])

    def test_non_pr_event_falls_full(self):
        (self.repo / "README.md").write_text("updated\n")
        self.commit()
        for event in ("push", "schedule", "merge_group", "workflow_dispatch"):
            self.assertEqual("full", self.select(event=event)["profile"])

    def test_pr_base_mismatch_falls_full(self):
        (self.repo / "README.md").write_text("updated\n")
        self.commit()
        self.assertEqual("full", self.select(event_base="0" * 40)["profile"])
        self.assertEqual("scoped", self.select()["profile"])
        with mock.patch.dict(os.environ, {"GITHUB_EVENT_NAME": "pull_request", "FSGG_READY_PR": "true", "FSGG_PR_BASE_SHA": "0" * 40}):
            with self.assertRaisesRegex(ValueError, "PR base"):
                module.verify_scoped(self.selection_path, self.obligation_path, self.profile_path)

    def test_tampered_profile_rejected(self):
        (self.repo / "README.md").write_text("updated\n")
        self.commit()
        result = self.select()
        result["fullDonorReceiptSha256"] = "d" * 64
        self.profile_path.write_text(json.dumps(result))
        with mock.patch.dict(os.environ, {"GITHUB_EVENT_NAME": "pull_request", "FSGG_READY_PR": "true", "FSGG_PR_BASE_SHA": self.base}):
            with self.assertRaisesRegex(ValueError, "digest differs"):
                module.verify_scoped(self.selection_path, self.obligation_path, self.profile_path)

    def test_missing_full_donor_artifact_rejected(self):
        (self.repo / "README.md").write_text("updated\n")
        self.commit()
        self.select()
        prior = json.loads(self.selection_path.read_text())["prior"]
        run = {"id": 123, "run_attempt": 1, "status": "completed", "conclusion": "success",
               "updated_at": prior["completedAt"], "head_sha": "d" * 40,
               "path": ".github/workflows/optimistic-parallel-validation.yml@refs/heads/main",
               "repository": {"full_name": "FS-GG/FS.GG.Coordination"}}
        responses = [json.dumps(run).encode(), b'{"total_count":0,"artifacts":[]}']
        with mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": "FS-GG/FS.GG.Coordination", "GITHUB_RUN_ID": "999"}):
            with mock.patch.object(module.subprocess, "check_output", side_effect=responses):
                with self.assertRaisesRegex(ValueError, "artifact is missing"):
                    module.verify_full_donor(prior, self.selection_path, self.obligation_path)


if __name__ == "__main__":
    unittest.main()
