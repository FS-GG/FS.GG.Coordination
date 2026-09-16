#!/usr/bin/env python3
"""Cheap adversarial controls for the administrative-retirement operator."""

import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("retirement", ROOT / "eng/administrative-retirement.py")
retirement = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(retirement)


class Fixture:
    def __init__(self, root):
        self.root = pathlib.Path(root)
        source = self.root / "source"
        source.mkdir()
        subprocess.run(["git", "init", "--quiet", "-b", "main", str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "config", "user.email", "fixture@example.invalid"], check=True)
        subprocess.run(["git", "-C", str(source), "config", "user.name", "fixture"], check=True)
        (source / "subject.txt").write_text("parent\n")
        subprocess.run(["git", "-C", str(source), "add", "."], check=True)
        subprocess.run(["git", "-C", str(source), "commit", "--quiet", "-m", "parent"], check=True)
        self.parent = self.git(source, "rev-parse", "HEAD")
        (source / "subject.txt").write_text("candidate\n")
        subprocess.run(["git", "-C", str(source), "commit", "--quiet", "-am", "candidate"], check=True)
        self.head = self.git(source, "rev-parse", "HEAD")
        self.tree = self.git(source, "rev-parse", "HEAD^{tree}")
        tombstone_env = dict(os.environ, GIT_AUTHOR_NAME="FS-GG administrative retirement", GIT_AUTHOR_EMAIL="retirement@fs.gg", GIT_AUTHOR_DATE="2000-01-01T00:00:00Z", GIT_COMMITTER_NAME="FS-GG administrative retirement", GIT_COMMITTER_EMAIL="retirement@fs.gg", GIT_COMMITTER_DATE="2000-01-01T00:00:00Z")
        self.retirement = subprocess.check_output(["git", "-C", str(source), "commit-tree", self.tree, "-p", self.head, "-m", "Administrative retirement tombstone fixture-retirement-0001 authority=" + "c" * 64], text=True, env=tombstone_env).strip()
        subprocess.run(["git", "-C", str(source), "update-ref", "refs/heads/retirement-tombstone", self.retirement], check=True)
        bundle = self.root / "candidate.bundle"
        subprocess.run(["git", "-C", str(source), "bundle", "create", str(bundle), "refs/heads/retirement-tombstone"], check=True)
        exclusion = self.root / "exclusion.json"
        exclusion.write_text(json.dumps({"schema": "fsgg.coordination.retired-subject-exclusion/1", "repository": "FS-GG/fixture", "issueNumber": 7, "candidateHead": self.head, "excluded": True}, sort_keys=True, separators=(",", ":")))
        manifest = self.root / "archive.json"
        manifest.write_text(json.dumps({"schema": retirement.ARCHIVE_SCHEMA, "repository": "FS-GG/fixture", "issueNumber": 7, "pullRequestNumber": 9, "candidateHead": self.head, "candidateTree": self.tree, "candidateParent": self.parent, "retirementHead": self.retirement, "retirementTree": self.tree, "retirementParent": self.head, "gitBundle": str(bundle), "gitBundleSha256": retirement.sha(bundle.read_bytes()), "capturedAt": "2026-09-15T00:00:00Z"}, sort_keys=True, separators=(",", ":")))
        evidence = self.root / "private"
        evidence.mkdir(mode=0o700)
        self.config = {"schema": retirement.CONFIG_SCHEMA, "repository": "FS-GG/fixture", "repositoryId": 11, "issueNumber": 7, "pullRequestNumber": 9, "branchRef": "refs/heads/fixture/candidate", "baseRef": "refs/heads/fixture/base", "candidateHead": self.head, "candidateTree": self.tree, "candidateParent": self.parent, "retirementHead": self.retirement, "retirementTree": self.tree, "retirementParent": self.head, "acceptedClientCommit": "a" * 40, "acceptedClientArtifactDigest": "b" * 64, "operationAuthorityDigest": "c" * 64, "operationId": "fixture-retirement-0001", "archiveManifest": str(manifest), "subjectExclusion": str(exclusion), "checkpoint": str(evidence / "checkpoint.json")}

    @staticmethod
    def git(root, *args):
        return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


class AdministrativeRetirementTests(unittest.TestCase):
    def snapshot(self, fixture, state):
        permanent_payload, _ = retirement.expected_rule(fixture.config, True)
        temporary_payload, _ = retirement.expected_rule(fixture.config, False)
        permanent = retirement.normalized_rule(dict(permanent_payload, id=42)) if state["permanent"] else None
        temporary = retirement.normalized_rule(dict(temporary_payload, id=41)) if state["temporary"] else None
        raw = {kind: retirement.canonical({"kind": kind}) for kind in retirement.CENSUS_KINDS}
        census = {"schema": "fsgg.coordination.administrative-retirement-native-census/1", "complete": True, "kinds": retirement.CENSUS_KINDS, "digests": {kind: retirement.sha(raw[kind]) for kind in retirement.CENSUS_KINDS}}
        return {"observedAt": retirement.utc_now(), "repository": fixture.config["repository"], "repositoryId": 11, "branchRef": fixture.config["branchRef"], "observedBranchHead": state.get("head", fixture.head), "retirementCommitObserved": state.get("head", fixture.head) == fixture.retirement, "protectedBaseRef": fixture.config["baseRef"], "candidateHead": fixture.head, "candidateTree": fixture.tree, "candidateParent": fixture.parent, "pullRequestDisposition": state["pr"], "pullRequestHead": state.get("head", fixture.head), "pullRequestNodeId": "PR_fixture", "mergeCommit": None, "mergedCandidateHead": None, "mergedCandidateTree": None, "mergedCandidateParent": None, "deliveredPathDigest": None, "autoMergeEnabled": False, "mergeQueueEntry": None, "issueState": state["issue"], "issueStateReason": state["reason"], "permanentRule": permanent, "temporaryRule": temporary, "baseProtectionDigest": "d" * 64, "subjectExclusion": {"digest": "e" * 64, "location": fixture.config["subjectExclusion"]}, "nativeCensus": census, "nativeCensusRaw": raw, "nativeCensusDigest": retirement.sha(retirement.canonical(census))}

    def test_transport_sends_json_on_stdin_with_input_and_deadline(self):
        completed = subprocess.CompletedProcess([], 0, stdout=b'{"ok":true}', stderr=b"")
        with mock.patch.object(retirement.subprocess, "run", return_value=completed) as run:
            value, _, _ = retirement.gh_request("repos/FS-GG/fixture/pulls/9", "PATCH", {"state": "closed"})
        self.assertEqual({"ok": True}, value)
        self.assertIn("--input", run.call_args.args[0])
        self.assertEqual(b'{"state":"closed"}', run.call_args.kwargs["input"])
        self.assertEqual(retirement.COMMAND_TIMEOUT_SECONDS, run.call_args.kwargs["timeout"])

    def test_archive_closure_and_current_checkpoint_binding(self):
        with tempfile.TemporaryDirectory() as root:
            fixture = Fixture(root)
            state = {"temporary": False, "permanent": False, "pr": "open", "head": fixture.head, "issue": "open", "reason": None}
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)):
                retirement.plan(fixture.config)
            altered = dict(fixture.config, pullRequestNumber=10)
            with self.assertRaisesRegex(retirement.Refused, "checkpoint-current-config-mismatch"):
                retirement.load_checkpoint(altered)
            manifest = pathlib.Path(fixture.config["archiveManifest"])
            changed = json.loads(manifest.read_text()); changed["candidateTree"] = "f" * 40
            manifest.write_text(json.dumps(changed))
            with self.assertRaisesRegex(retirement.Refused, "archive-binding|archive-closure"):
                retirement.validate_archive(fixture.config)

    def test_initial_plan_and_retirement_closure_are_exact(self):
        with tempfile.TemporaryDirectory() as root:
            fixture = Fixture(root)
            state = {"temporary": False, "permanent": False, "pr": "open", "head": fixture.retirement, "issue": "open", "reason": None}
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)):
                with self.assertRaisesRegex(retirement.Refused, "initial-open-candidate-mismatch"):
                    retirement.plan(fixture.config)
            state.update(pr="closed-unmerged", head=fixture.head)
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)):
                with self.assertRaisesRegex(retirement.Refused, "initial-pull-request-not-actionable"):
                    retirement.plan(fixture.config)
            manifest = pathlib.Path(fixture.config["archiveManifest"])
            changed = json.loads(manifest.read_text())
            changed["retirementParent"] = fixture.parent
            manifest.write_text(json.dumps(changed))
            with self.assertRaisesRegex(retirement.Refused, "archive-binding|archive-closure"):
                retirement.validate_archive(fixture.config)

    def test_merged_delivery_binds_exact_candidate_changed_paths(self):
        blob_parent = "1" * 40
        blob_candidate = "2" * 40
        base_only = "3" * 40
        parent = {"truncated": False, "tree": [{"path": "subject.txt", "mode": "100644", "type": "blob", "sha": blob_parent}]}
        candidate = {"truncated": False, "tree": [{"path": "subject.txt", "mode": "100644", "type": "blob", "sha": blob_candidate}]}
        files = [{"filename": "subject.txt", "status": "modified", "sha": blob_candidate}]
        # A protected-base advance may contribute unrelated paths to the merge tree.
        merged = {"truncated": False, "tree": candidate["tree"] + [{"path": "base-only.txt", "mode": "100644", "type": "blob", "sha": base_only}]}
        digest = retirement.validate_changed_paths(candidate, parent, files, merged)
        self.assertRegex(digest, r"^[0-9a-f]{64}$")
        stale = {"truncated": False, "tree": [{"path": "subject.txt", "mode": "100644", "type": "blob", "sha": blob_parent}]}
        with self.assertRaisesRegex(retirement.Refused, "merged-changed-path-mismatch"):
            retirement.validate_changed_paths(candidate, parent, files, stale)
        with self.assertRaisesRegex(retirement.Refused, "candidate-changed-path-census-mismatch"):
            retirement.validate_changed_paths(candidate, parent, [])
        with self.assertRaisesRegex(retirement.Refused, "tree-incomplete"):
            retirement.validate_changed_paths(dict(candidate, truncated=True), parent, files)

    def test_lost_cleanup_response_reconciles_without_recreating_hold(self):
        with tempfile.TemporaryDirectory() as root:
            fixture = Fixture(root)
            state = {"temporary": False, "permanent": False, "pr": "open", "issue": "open", "reason": None, "lost": False, "temporaryCreates": 0, "headPushes": 0}
            def request(path, method="GET", body=None):
                if method == "POST" and path.endswith("/rulesets"):
                    if body["name"].endswith("temporary-main-hold"):
                        state["temporary"] = True; state["temporaryCreates"] += 1
                    else: state["permanent"] = True
                elif method == "PATCH" and "/pulls/" in path: state["pr"] = "closed-unmerged"
                elif method == "PATCH" and "/issues/" in path: state["issue"] = "closed"; state["reason"] = body["state_reason"]
                elif method == "DELETE":
                    state["temporary"] = False
                    if not state["lost"]:
                        state["lost"] = True
                        raise retirement.UnknownEffect("injected-delete-response-loss")
                return {}, {}, b"{}"
            def push_head(config, archive):
                state["headPushes"] += 1
                state["head"] = fixture.retirement
                raise retirement.UnknownEffect("injected-head-advance-response-loss")
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)), mock.patch.object(retirement, "gh_request", side_effect=request), mock.patch.object(retirement, "push_retirement_head", side_effect=push_head):
                retirement.plan(fixture.config)
                self.assertEqual("planned", retirement.plan(fixture.config)["stage"])
                retirement.execute(fixture.config, "temporary-hold")
                retirement.execute(fixture.config, "permanent-fence")
                retirement.execute(fixture.config, "settled")
                with self.assertRaisesRegex(retirement.UnknownEffect, "response-loss"):
                    retirement.execute(fixture.config)
                self.assertTrue(retirement.plan(fixture.config)["cleanupRequired"])
                real_checkpoint = retirement.checkpoint
                crashed = {"value": False}
                def crash_before_completed_write(config, value, stage, pending=None, **changes):
                    if stage == "completed" and not crashed["value"]:
                        crashed["value"] = True
                        raise RuntimeError("injected-post-delete-crash")
                    return real_checkpoint(config, value, stage, pending, **changes)
                with mock.patch.object(retirement, "checkpoint", side_effect=crash_before_completed_write):
                    with self.assertRaisesRegex(RuntimeError, "post-delete-crash"):
                        retirement.execute(fixture.config)
                result = retirement.execute(fixture.config)
                replay = retirement.execute(fixture.config)
            self.assertEqual(1, state["temporaryCreates"])
            self.assertEqual(1, state["headPushes"])
            self.assertEqual("AdministrativelyRetiredWithLostHistory", result["result"])
            self.assertEqual(result["typedReceipt"]["ReceiptDigest"], replay["typedReceipt"]["ReceiptDigest"])
            self.assertFalse(json.loads(pathlib.Path(fixture.config["checkpoint"]).read_text())["cleanupRequired"])
            if os.environ.get("FSGG_RETIREMENT_FIXTURE_OUTPUT"):
                pathlib.Path(os.environ["FSGG_RETIREMENT_FIXTURE_OUTPUT"]).write_bytes(retirement.canonical(result))
            checkpoint = json.loads(pathlib.Path(fixture.config["checkpoint"]).read_text())
            effect_names = [effect["name"] for effect in checkpoint["effects"]]
            self.assertEqual(len(effect_names), len(set(effect_names)))
            advance = next(effect for effect in checkpoint["effects"] if effect["name"] == "retirement-head-advanced")
            expected_advance = retirement.sha(retirement.canonical({"ref": fixture.config["branchRef"], "old": fixture.head, "new": fixture.retirement}))
            self.assertEqual(expected_advance, advance["requestDigest"])
            raw_artifact = pathlib.Path(checkpoint["completedSnapshot"]["nativeCensus"]["artifacts"]["repository"]["location"])
            original_raw = raw_artifact.read_bytes()
            raw_artifact.write_bytes(b"{}")
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)):
                with self.assertRaisesRegex(retirement.Refused, "native-census-raw-binding"):
                    retirement.verify(fixture.config)
            raw_artifact.write_bytes(original_raw)
            pathlib.Path(checkpoint["completedSnapshot"]["nativeCensusLocation"]).write_text("{}")
            with mock.patch.object(retirement, "observe", side_effect=lambda config: self.snapshot(fixture, state)):
                with self.assertRaisesRegex(retirement.Refused, "native-census-artifact"):
                    retirement.verify(fixture.config)

    def test_wrong_scope_bypass_and_incomplete_census_refuse(self):
        payload = retirement.rule_payload("fixture", "refs/heads/right", retirement.PERMANENT_RULES)
        observed = retirement.normalized_rule(dict(payload, id=1))
        self.assertTrue(retirement.rule_matches(observed, payload))
        wrong = dict(payload); wrong["conditions"] = {"ref_name": {"include": ["refs/heads/wrong"], "exclude": []}}
        self.assertFalse(retirement.rule_matches(observed, wrong))
        bypass = dict(payload, bypass_actors=[{"actor_id": 1, "actor_type": "Team", "bypass_mode": "always"}])
        self.assertFalse(retirement.rule_matches(observed, bypass))
        omitted_default = dict(payload); omitted_default["rules"] = [{"type": item["type"]} if item["type"] == "update" else item for item in payload["rules"]]
        self.assertEqual(retirement.normalized_rule(dict(omitted_default, id=1)), retirement.normalized_rule(dict(payload, id=1)))
        fetch_and_merge = dict(payload); fetch_and_merge["rules"] = [{"type": item["type"], "parameters": {"update_allows_fetch_and_merge": True}} if item["type"] == "update" else item for item in payload["rules"]]
        self.assertNotEqual(retirement.normalized_rule(dict(fetch_and_merge, id=1)), retirement.normalized_rule(dict(payload, id=1)))
        null_parameters = dict(payload); null_parameters["rules"] = [{"type": item["type"], "parameters": None} if item["type"] == "update" else item for item in payload["rules"]]
        self.assertNotEqual(retirement.normalized_rule(dict(null_parameters, id=1)), retirement.normalized_rule(dict(payload, id=1)))
        snapshot = {"nativeCensus": {"complete": False}}
        self.assertFalse(snapshot["nativeCensus"]["complete"])


if __name__ == "__main__":
    unittest.main()
