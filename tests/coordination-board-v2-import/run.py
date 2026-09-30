#!/usr/bin/env python3
import importlib.util
import json
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("board_v2", ROOT / "tools" / "coordination-board-v2-import.py")
M = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(M)


def issue(row):
    ref, node, updated, *_ = row
    repo, number = ref.split("#")
    return {"id": node, "number": int(number), "updatedAt": updated, "state": "OPEN", "repository": {"nameWithOwner": repo}}


def inspect(project=None, *, remaining=100, next_page=False):
    return {"data": {"viewer": {"login": "operator"}, "organization": {"id": "O_org", "login": "FS-GG",
        "projectsV2": {"totalCount": 0 if project is None else 1, "pageInfo": {"hasNextPage": next_page, "endCursor": "cursor" if next_page else None}, "nodes": [] if project is None else [project]}},
        "nodes": [issue(row) for row in M.PILOT], "rateLimit": {"limit": 5000, "remaining": remaining, "resetAt": "2026-09-30T12:00:00Z", "cost": 1}}}


def field(name, kind, options=()):
    return {"__typename": "ProjectV2SingleSelectField" if options else "ProjectV2Field", "id": "F_" + name, "name": name, "dataType": kind,
            **({"options": [{"id": "O_" + value.replace(" ", "_"), "name": value} for value in options]} if options else {})}


def connection(nodes):
    return {"totalCount": len(nodes), "pageInfo": {"hasNextPage": False, "endCursor": None}, "nodes": nodes}


def project(fields=None, items=None, marker=M.MARKER, identity="PVT_v2", number=2):
    return {"id": identity, "number": number, "title": M.TITLE, "shortDescription": marker, "closed": False,
            "createdAt": "2026-09-30T05:00:00Z", "creator": {"login": "operator"},
            "fields": connection(fields or []), "items": connection(items or [])}


class Sender:
    def __init__(self, replies): self.replies, self.calls = list(replies), []
    def __call__(self, document, variables):
        self.calls.append((document, variables))
        return self.replies.pop(0)


class Tests(unittest.TestCase):
    def test_preflight_is_fixed_and_metered(self):
        sender = Sender([(200, inspect())]); result = M.preflight(M.Client("x", sender))
        self.assertEqual([r[0] for r in M.PILOT], result["pilot"])
        self.assertFalse(result["effectAuthorized"])
        self.assertEqual("unknown-not-probed", result["createCapability"])
        self.assertEqual(M.OWNER, sender.calls[0][1]["owner"])

    def test_incomplete_project_page_refuses_absence(self):
        with self.assertRaisesRegex(M.Refusal, "incomplete-projects"):
            M.preflight(M.Client("x", Sender([(200, inspect(next_page=True))])))

    def test_legacy_identity_refuses(self):
        with self.assertRaisesRegex(M.Refusal, "legacy-project-mutation-refused"):
            M.preflight(M.Client("x", Sender([(200, inspect(project(identity=M.LEGACY_ID, number=1)))])))

    def test_stale_source_issue_refuses(self):
        payload = inspect(); payload["data"]["nodes"][0]["updatedAt"] = "2026-09-30T00:00:00Z"
        with self.assertRaisesRegex(M.Refusal, "pilot-source-stale"):
            M.preflight(M.Client("x", Sender([(200, payload)])))

    def test_exact_schema_requires_owned_fields_and_options(self):
        exact = project([field(name, kind, options) for name, (kind, options) in M.FIELDS.items()])
        self.assertTrue(M.exact_schema(exact))
        exact["fields"]["nodes"][0]["options"][0]["name"] = "Todo"
        self.assertFalse(M.exact_schema(exact))

    def test_existing_target_without_bound_state_refuses_execute(self):
        exact = project([field(name, kind, options) for name, (kind, options) in M.FIELDS.items()])
        client = M.Client("x", Sender([(200, inspect(exact))]))
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaisesRegex(M.Refusal, "unbound-existing-target-refused"):
                M.execute(client, pathlib.Path(root) / "state.json", "a" * 40)

    def test_missing_rate_budget_refuses(self):
        payload = inspect(); del payload["data"]["rateLimit"]
        with self.assertRaisesRegex(M.Refusal, "missing-rate-budget"):
            M.preflight(M.Client("x", Sender([(200, payload)])))

    def test_duplicate_target_refuses(self):
        payload = inspect(project())
        payload["data"]["organization"]["projectsV2"]["nodes"].append(project(identity="PVT_other", number=3))
        payload["data"]["organization"]["projectsV2"]["totalCount"] = 2
        with self.assertRaisesRegex(M.Refusal, "duplicate-target-project"):
            M.preflight(M.Client("x", Sender([(200, payload)])))

    def test_duplicate_membership_refuses_without_mutation(self):
        fields = [field(name, kind, options) for name, (kind, options) in M.FIELDS.items()]
        one_item = {"id": "PVTI_one", "content": issue(M.PILOT[0]), "fieldValues": connection([])}
        duplicate = {**one_item, "id": "PVTI_duplicate"}
        target = project(fields, [one_item, duplicate])
        sender = Sender([(200, inspect(target))])
        with tempfile.TemporaryDirectory() as root:
            state_path = pathlib.Path(root) / "state.json"
            M.write_state(state_path, {"schema": "fsgg.coordination-board-v2-operation-state/v1",
                "manifestHead": M.MANIFEST_HEAD, "stage": "schema-ready", "admissionSha": "a" * 40,
                "projectId": "PVT_v2", "projectNumber": 2})
            with self.assertRaisesRegex(M.Refusal, "duplicate-membership"):
                M.execute(M.Client("x", sender), state_path, "a" * 40)
        self.assertEqual(1, len(sender.calls))

    def test_fixed_mutations_cannot_name_legacy_project(self):
        default = project([field("Status", "SINGLE_SELECT", ("Todo", "In Progress", "Done"))], marker=None)
        schema_document, _ = M.schema_mutation(default)
        add_document, _ = M.add_mutation(list(M.PILOT))
        for document in (M.CREATE, schema_document, add_document):
            self.assertNotIn(M.LEGACY_ID, document)
        self.assertEqual("10d6e84ebf1c33e75d5e0e2d2c25c43c0d6b893dc12d14a145ba51842a029e35", M.MANIFEST_SHA256)

    def test_pending_creation_refuses_an_older_same_title_project(self):
        default = project([field("Status", "SINGLE_SELECT", ("Todo", "In Progress", "Done"))], marker=None)
        sender = Sender([(200, inspect(default))])
        with tempfile.TemporaryDirectory() as root:
            state_path = pathlib.Path(root) / "state.json"
            M.write_state(state_path, {"schema": "fsgg.coordination-board-v2-operation-state/v1",
                "manifestHead": M.MANIFEST_HEAD, "stage": "create-pending", "admissionSha": "a" * 40,
                "startedAt": "2026-09-30T06:00:00+00:00", "ownerId": "O_org"})
            with self.assertRaisesRegex(M.Refusal, "uncertain-created-project-time"):
                M.execute(M.Client("x", sender), state_path, "a" * 40)
        self.assertEqual(1, len(sender.calls))

    def test_workflow_uses_environment_scoped_app_not_worker_pat(self):
        workflow = (ROOT / ".github" / "workflows" / "coordination-board-v2-import.yml").read_text()
        self.assertIn("environment: coordination-board-v2-import", workflow)
        self.assertIn("permission-organization-projects: write", workflow)
        self.assertIn("            .github\n            FS.GG.Coordination\n", workflow)
        self.assertIn("GH_TOKEN: ${{ steps.app-token.outputs.token }}", workflow)
        self.assertNotIn("secrets.GH_TOKEN", workflow)
        self.assertIn("if: github.ref == 'refs/heads/main'", workflow)

    def test_workflow_pins_actions_and_serializes_effects(self):
        workflow = (ROOT / ".github" / "workflows" / "coordination-board-v2-import.yml").read_text()
        self.assertIn("group: coordination-board-v2-fixed-import", workflow)
        self.assertIn("cancel-in-progress: false", workflow)
        self.assertEqual(2, workflow.count("WORKFLOW_REVISION: ${{ job.workflow_sha }}"))
        self.assertNotIn("github.workflow_sha", workflow)
        self.assertEqual(2, workflow.count('test -n "$WORKFLOW_REVISION"'))
        for action in ("actions/checkout@", "actions/download-artifact@", "actions/upload-artifact@", "actions/create-github-app-token@"):
            positions = [line.strip().split("@", 1)[1] for line in workflow.splitlines() if action in line]
            self.assertTrue(positions)
            self.assertTrue(all(len(value) == 40 and all(c in "0123456789abcdef" for c in value) for value in positions))

    def test_workflow_keeps_effect_target_fixed(self):
        workflow = (ROOT / ".github" / "workflows" / "coordination-board-v2-import.yml").read_text()
        self.assertNotIn("--owner", workflow)
        self.assertNotIn("--project", workflow)
        self.assertNotIn("--issue", workflow)
        self.assertIn('--admitted-source "$GITHUB_SHA"', workflow)


if __name__ == "__main__": unittest.main()
