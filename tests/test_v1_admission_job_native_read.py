"""Synthetic complete native run/job/deployment joins; no live provider mutation."""

import base64
import copy
import dataclasses
import hashlib
import importlib.util
import pathlib
import unittest


SOURCE = pathlib.Path(__file__).resolve().parents[1] / "eng/github-v1-admission-job-native-read.py"
SPEC = importlib.util.spec_from_file_location("job_native", SOURCE)
NATIVE = importlib.util.module_from_spec(SPEC)
import sys
sys.modules[SPEC.name] = NATIVE
SPEC.loader.exec_module(NATIVE)

RUN_ID = 1001
JOB_ID = 2002
HEAD = "a" * 40
PATH = ".github/workflows/operating-v1-admission.yml"
ENV = "operating-v1-admission-service"
NODE = "EN_service"
JOB_URL = f"https://github.com/FS-GG/.github/actions/runs/{RUN_ID}/job/{JOB_ID}"
WORKFLOW = b"name: Operating V1 admission\n"
PREFIX = "repos/FS-GG/.github"


class Fixture:
    def __init__(self):
        self.calls = []
        self.data = {}
        self.links = {}
        self.data[f"{PREFIX}/actions/runs/{RUN_ID}"] = {
            "id": RUN_ID, "repository": {"id": NATIVE.REPOSITORY_ID},
            "head_repository": {"id": NATIVE.REPOSITORY_ID},
            "path": PATH, "head_sha": HEAD, "head_branch": "main",
            "event": "workflow_dispatch", "run_attempt": 1,
            "status": "in_progress", "conclusion": None, "actor": {"id": 1645484},
        }
        self.data[f"{PREFIX}/actions/runs/{RUN_ID}/attempts/1/jobs?per_page=100&page=1"] = {
            "total_count": 1,
            "jobs": [{
                "id": JOB_ID, "run_id": RUN_ID, "head_sha": HEAD, "name": "admit",
                "status": "in_progress", "conclusion": None, "html_url": JOB_URL,
                "check_run_url": f"https://api.github.com/{PREFIX}/check-runs/{JOB_ID}",
            }],
        }
        self.data[f"{PREFIX}/contents/{PATH}?ref={HEAD}"] = {
            "type": "file", "path": PATH, "encoding": "base64",
            "content": base64.b64encode(WORKFLOW).decode(),
        }
        environment = {
            "id": 3003, "node_id": NODE, "name": ENV,
            "protection_rules": [{"type": "branch_policy", "id": 77}],
            "deployment_branch_policy": {
                "custom_branch_policies": True, "protected_branches": False,
            },
        }
        branches = {
            "total_count": 1,
            "branch_policies": [{"id": 88, "name": "main", "type": "branch"}],
        }
        self.data[f"{PREFIX}/environments/{ENV}"] = environment
        self.data[f"{PREFIX}/environments/{ENV}/deployment-branch-policies?per_page=100"] = branches
        self.policy = NATIVE.InstalledPolicy(
            PATH, HEAD, hashlib.sha256(WORKFLOW).hexdigest(), "admit", 1645484,
            ENV, 3003, NODE, NATIVE._rules_digest(environment, branches),
        )
        self.deployment(4004, JOB_URL)

    def deployment(self, identifier, url):
        path = f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"
        deployment = {
            "id": identifier, "sha": HEAD, "environment": ENV,
            "original_environment": ENV, "ref": "main",
            "statuses_url": f"https://api.github.com/{PREFIX}/deployments/{identifier}/statuses",
        }
        self.data.setdefault(path, []).append(deployment)
        self.data[f"{PREFIX}/deployments/{identifier}/statuses?per_page=100&page=1"] = [
            {"state": "in_progress", "target_url": url, "log_url": url},
            {"state": "queued", "target_url": url, "log_url": url},
        ]

    def read(self, path):
        self.calls.append(path)
        if path not in self.data and (path.endswith("&page=2") or path.endswith("&page=3")):
            return NATIVE.NativeResponse([], None)
        return NATIVE.NativeResponse(copy.deepcopy(self.data[path]), self.links.get(path))


class NativeJobTests(unittest.TestCase):
    def test_two_complete_reads_bind_one_job_and_one_deployment(self):
        fixture = Fixture()
        result = NATIVE.collect_two(fixture.read, fixture.policy, RUN_ID, JOB_ID)
        self.assertEqual(result["corroborating_deployment_id"], 4004)
        self.assertEqual(fixture.calls.count(f"{PREFIX}/actions/runs/{RUN_ID}"), 2)
        self.assertEqual(fixture.calls.count(
            f"{PREFIX}/deployments/4004/statuses?per_page=100&page=1"), 2)
        self.assertEqual(fixture.calls.count(
            f"{PREFIX}/deployments/4004/statuses?per_page=100&page=2"), 2)

    def test_missing_and_duplicate_native_job_environment_join_refuse(self):
        fixture = Fixture()
        fixture.data[f"{PREFIX}/deployments/4004/statuses?per_page=100&page=1"][0]["target_url"] = "wrong"
        with self.assertRaises(NATIVE.Refused):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)
        fixture = Fixture()
        fixture.data[f"{PREFIX}/deployments/4004/statuses?per_page=100&page=1"][0]["state"] = "success"
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-status-binding"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)
        fixture = Fixture()
        fixture.deployment(4005, JOB_URL)
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-deployment-corroboration"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)

    def test_required_reviewer_rule_refuses_without_timed_native_approval_source(self):
        fixture = Fixture()
        environment = fixture.data[f"{PREFIX}/environments/{ENV}"]
        environment["protection_rules"].append({
            "type": "required_reviewers", "reviewers": [{"type": "User", "reviewer": {"id": 1645484}}],
        })
        branches = fixture.data[f"{PREFIX}/environments/{ENV}/deployment-branch-policies?per_page=100"]
        fixture.policy = dataclasses.replace(
            fixture.policy, environment_rules_sha256=NATIVE._rules_digest(environment, branches))
        path = f"{PREFIX}/actions/runs/{RUN_ID}/approvals?per_page=100&page=1"
        fixture.data[path] = [{
            "state": "approved", "user": {"id": 1645484},
            "environments": [{"id": 3003, "name": ENV}],
        }]
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-approval-time-unavailable"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)
        fixture.data[path][0]["state"] = "rejected"
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-approval-contradiction"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)

    def test_wrong_environment_node_rules_and_job_identity_refuse(self):
        for mutate in (
            lambda f: f.data[f"{PREFIX}/environments/{ENV}"].update(node_id="other"),
            lambda f: f.data[f"{PREFIX}/environments/{ENV}"]["protection_rules"].append(
                {"type": "wait_timer", "wait_timer": 0}),
            lambda f: f.data[f"{PREFIX}/actions/runs/{RUN_ID}/attempts/1/jobs?per_page=100&page=1"]["jobs"][0].update(id=9),
        ):
            fixture = Fixture()
            mutate(fixture)
            with self.assertRaises(NATIVE.Refused):
                NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)

    def test_incomplete_deployment_page_and_changed_second_read_refuse(self):
        fixture = Fixture()
        path = f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"
        fixture.data[path] = fixture.data[path] * 100
        fixture.data[path[:-1] + "2"] = fixture.data[path][:1]
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-page-terminal"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)

        fixture = Fixture()
        original_read = fixture.read
        reads = 0

        def moved_between_reads(path):
            nonlocal reads
            if path == f"{PREFIX}/actions/runs/{RUN_ID}":
                reads += 1
                if reads == 2:
                    fixture.data[f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"] = []
                    fixture.deployment(4005, JOB_URL)
            return original_read(path)

        with self.assertRaisesRegex(NATIVE.Refused, "native-job-two-read-drift"):
            NATIVE.collect_two(moved_between_reads, fixture.policy, RUN_ID, JOB_ID)

    def test_short_page_with_next_is_followed_and_unrelated_census_drift_refuses(self):
        fixture = Fixture()
        path = f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"
        fixture.links[path] = (
            f'<https://api.github.com/repositories/{NATIVE.REPOSITORY_ID}/deployments?'
            f'sha={HEAD}&environment={ENV}&per_page=100&page=2>; rel="next"'
        )
        fixture.data[path[:-1] + "2"] = []
        NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)
        self.assertIn(path[:-1] + "2", fixture.calls)

        fixture = Fixture()
        original_read = fixture.read
        run_reads = 0

        def changed_status(path):
            nonlocal run_reads
            if path == f"{PREFIX}/actions/runs/{RUN_ID}":
                run_reads += 1
                if run_reads == 2:
                    status_path = f"{PREFIX}/deployments/4004/statuses?per_page=100&page=1"
                    fixture.data[status_path][1]["irrelevant"] = "changed"
            return original_read(path)

        with self.assertRaisesRegex(NATIVE.Refused, "native-job-two-read-drift"):
            NATIVE.collect_two(changed_status, fixture.policy, RUN_ID, JOB_ID)

    def test_repeated_or_escaped_link_continuation_refuses(self):
        path = f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"
        next_link = (
            f'<https://api.github.com/repositories/{NATIVE.REPOSITORY_ID}/deployments?'
            f'sha={HEAD}&environment={ENV}&per_page=100&page=2>; rel="next"'
        )
        for link in (next_link + ", " + next_link,
                     next_link.replace("/deployments?", "/%64eployments?")):
            fixture = Fixture()
            fixture.links[path] = link
            with self.assertRaises(NATIVE.Refused):
                NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)

    def test_declared_later_last_without_next_refuses_even_if_probe_empty(self):
        fixture = Fixture()
        path = f"{PREFIX}/deployments?sha={HEAD}&environment={ENV}&per_page=100&page=1"
        fixture.links[path] = (
            f'<https://api.github.com/repositories/{NATIVE.REPOSITORY_ID}/deployments?'
            f'sha={HEAD}&environment={ENV}&per_page=100&page=3>; rel="last"'
        )
        with self.assertRaisesRegex(NATIVE.Refused, "native-job-link-last"):
            NATIVE.collect_once(fixture.read, fixture.policy, RUN_ID, JOB_ID)


if __name__ == "__main__":
    unittest.main()
