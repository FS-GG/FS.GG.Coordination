#!/usr/bin/env python3
import base64
import copy
import hashlib
import importlib.util
import io
import json
import pathlib
import sys
import unittest
import zipfile


sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("v1_native_read", ROOT / "github-v1-admission-protected-read.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
WORKFLOW = (ROOT.parent / "tests/FS.GG.Coordination.UnitTests/fixtures"
            / "gs2-v1-admission-protected-authorization.yml").read_bytes()
NATIVE_FIXTURE = ROOT.parent / "tests/FS.GG.Coordination.UnitTests/fixtures/v1-admission-native-read.json"
RUN_ID = 42
HEAD = "a" * 40


def zip_receipt(extra=False):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr(native.MEMBER, b'{"schema":"fixture"}\n')
        if extra:
            bundle.writestr("unexpected.json", b"{}")
    return stream.getvalue()


def fixture():
    prefix = f"repos/{native.REPOSITORY}"
    archive = zip_receipt()
    source = {
        f"{prefix}/actions/runs/{RUN_ID}": {
            "id": RUN_ID, "repository": {"id": native.REPOSITORY_ID}, "head_sha": HEAD,
            "head_branch": "main", "path": native.WORKFLOW, "event": "workflow_dispatch",
            "run_attempt": 1, "status": "completed", "conclusion": "success",
            "actor": {"id": native.ACCOUNTABLE_OWNER_ID},
        },
        f"{prefix}/contents/{native.WORKFLOW}?ref={HEAD}": {
            "type": "file", "encoding": "base64", "content": base64.b64encode(WORKFLOW).decode(),
        },
        f"{prefix}/environments/{native.ENVIRONMENT}": {
            "id": native.ENVIRONMENT_ID, "name": native.ENVIRONMENT,
            "deployment_branch_policy": {"custom_branch_policies": True, "protected_branches": False},
            "protection_rules": [
                {"type": "required_reviewers", "prevent_self_review": False,
                 "reviewers": [{"type": "User", "reviewer": {"id": native.ACCOUNTABLE_OWNER_ID}}]},
                {"type": "wait_timer", "wait_timer": 5},
                {"type": "branch_policy"},
            ],
        },
        f"{prefix}/environments/{native.ENVIRONMENT}/deployment-branch-policies?per_page=100": {
            "total_count": 1, "branch_policies": [{"name": "main", "type": "branch"}],
        },
        f"{prefix}/actions/runs/{RUN_ID}/approvals?per_page=100": [[{
            "state": "approved", "user": {"id": native.ACCOUNTABLE_OWNER_ID,
                                           "login": "EHotwagner", "type": "User"},
            "environments": [{"id": native.ENVIRONMENT_ID, "name": native.ENVIRONMENT}],
        }, {
            "state": "approved", "user": {"id": native.WAIT_TIMER_BOT_ID,
                                           "login": "github-actions[bot]", "type": "Bot"},
            "comment": "5 minute wait timer",
            "environments": [{"id": native.ENVIRONMENT_ID, "name": native.ENVIRONMENT}],
        }]],
        f"{prefix}/actions/runs/{RUN_ID}/artifacts?per_page=100": [{
            "total_count": 1,
            "artifacts": [{
                "id": 81, "name": f"gs2-v1-admission-protected-authorization-{RUN_ID}",
                "expired": False, "digest": "sha256:" + hashlib.sha256(archive).hexdigest(),
                "workflow_run": {"id": RUN_ID, "repository_id": native.REPOSITORY_ID,
                                 "head_repository_id": native.REPOSITORY_ID,
                                 "head_sha": HEAD, "head_branch": "main"},
            }],
        }],
    }
    return source, archive


def collect(source, archive):
    def read_json(path, paginate=False):
        value = source[path]
        assert paginate == isinstance(value, list)
        return copy.deepcopy(value)

    def read_bytes(path):
        assert path == f"repos/{native.REPOSITORY}/actions/artifacts/81/zip"
        return archive

    return native.collect(RUN_ID, read_json, read_bytes, "2026-09-23T14:00:00Z")


class ProtectedNativeReadTests(unittest.TestCase):
    def test_exact_reviewed_run_artifact_and_workflow(self):
        source, archive = fixture()
        evidence = collect(source, archive)
        self.assertEqual("fsgg.v1-admission-genesis-native-read/2", evidence["schema"])
        self.assertEqual(RUN_ID, evidence["artifactReadRunId"])
        self.assertEqual(native.ENVIRONMENT_ID, evidence["approvals"][0]["environmentIds"][0])
        self.assertEqual([native.ACCOUNTABLE_OWNER_ID, native.WAIT_TIMER_BOT_ID],
                         [item["reviewerId"] for item in evidence["approvals"]])
        self.assertEqual(WORKFLOW, base64.b64decode(evidence["workflowBytesBase64"]))
        self.assertEqual(b'{"schema":"fixture"}\n', base64.b64decode(evidence["artifactBytesBase64"]))
        self.assertEqual(NATIVE_FIXTURE.read_bytes(),
                         json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode() + b"\n")

    def test_changed_workflow_and_run_ref_refuse(self):
        source, archive = fixture()
        source[f"repos/{native.REPOSITORY}/contents/{native.WORKFLOW}?ref={HEAD}"]["content"] = base64.b64encode(b"altered").decode()
        with self.assertRaisesRegex(native.Refused, "native-workflow-drift"):
            collect(source, archive)
        source, archive = fixture()
        source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}"]["head_branch"] = "other"
        with self.assertRaisesRegex(native.Refused, "native-run-state"):
            collect(source, archive)

    def test_wrong_reviewer_environment_and_other_actor_refuse(self):
        source, archive = fixture()
        approval = source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/approvals?per_page=100"][0][0]
        approval["environments"][0]["id"] = 9
        with self.assertRaisesRegex(native.Refused, "native-approval-shape"):
            collect(source, archive)
        source, archive = fixture()
        source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}"]["actor"]["id"] = 777
        with self.assertRaisesRegex(native.Refused, "native-run-actor"):
            collect(source, archive)
        source, archive = fixture()
        approval = source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/approvals?per_page=100"][0][0]
        approval["user"]["id"] = 4456104
        with self.assertRaisesRegex(native.Refused, "native-approval-binding"):
            collect(source, archive)
        source, archive = fixture()
        rules = source[f"repos/{native.REPOSITORY}/environments/{native.ENVIRONMENT}"]["protection_rules"]
        rules[0]["prevent_self_review"] = True
        with self.assertRaisesRegex(native.Refused, "native-environment-reviewers"):
            collect(source, archive)
        source, archive = fixture()
        rules = source[f"repos/{native.REPOSITORY}/environments/{native.ENVIRONMENT}"]["protection_rules"]
        rules[1]["wait_timer"] = 0
        with self.assertRaisesRegex(native.Refused, "native-environment-rules"):
            collect(source, archive)

    def test_wait_timer_bot_is_required_and_exact(self):
        path = f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/approvals?per_page=100"
        source, archive = fixture()
        source[path][0].reverse()  # GitHub returned the bot before the owner in the live run.
        self.assertEqual([native.ACCOUNTABLE_OWNER_ID, native.WAIT_TIMER_BOT_ID],
                         [item["reviewerId"] for item in collect(source, archive)["approvals"]])
        source, archive = fixture()
        source[path][0].pop()
        with self.assertRaisesRegex(native.Refused, "native-approvals-count"):
            collect(source, archive)
        source, archive = fixture()
        source[path][0][1]["comment"] = "different timer"
        with self.assertRaisesRegex(native.Refused, "native-approval-binding"):
            collect(source, archive)
        source, archive = fixture()
        source[path][0][1]["user"]["id"] = 9
        with self.assertRaisesRegex(native.Refused, "native-approval-binding"):
            collect(source, archive)
        source, archive = fixture()
        source[path][0][1]["user"] = []
        with self.assertRaisesRegex(native.Refused, "native-approval-shape"):
            collect(source, archive)
        source, archive = fixture()
        source[path][0][1]["environments"] = [9]
        with self.assertRaisesRegex(native.Refused, "native-approval-shape"):
            collect(source, archive)
        source, archive = fixture()
        source[path][0].append(copy.deepcopy(source[path][0][0]))
        with self.assertRaisesRegex(native.Refused, "native-approvals-count"):
            collect(source, archive)

    def test_artifact_origin_digest_expiry_and_extra_member_refuse(self):
        source, archive = fixture()
        artifact = source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/artifacts?per_page=100"][0]["artifacts"][0]
        artifact["workflow_run"]["id"] = 43
        with self.assertRaisesRegex(native.Refused, "native-artifact-origin"):
            collect(source, archive)
        source, archive = fixture()
        artifact = source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/artifacts?per_page=100"][0]["artifacts"][0]
        artifact["expired"] = True
        with self.assertRaisesRegex(native.Refused, "native-artifact-identity"):
            collect(source, archive)
        source, archive = fixture()
        with self.assertRaisesRegex(native.Refused, "native-artifact-digest"):
            collect(source, archive + b"x")
        source, _ = fixture()
        archive = zip_receipt(extra=True)
        source[f"repos/{native.REPOSITORY}/actions/runs/{RUN_ID}/artifacts?per_page=100"][0]["artifacts"][0]["digest"] = "sha256:" + hashlib.sha256(archive).hexdigest()
        with self.assertRaisesRegex(native.Refused, "native-artifact-members"):
            collect(source, archive)


if __name__ == "__main__":
    unittest.main()
