#!/usr/bin/env python3
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


operation = load("ledger_operation", "github-ledger-operation.py")
transport = load("ledger_transport", "github-ledger-initialization-transport.py")
runner = load("ledger_runner", "github-ledger-monitor-runner.py")


def write(path, value, mode=0o600):
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n")
    path.chmod(mode)


def object_(kind, payload):
    return {"kind": kind, "oid": transport.oid(kind, payload), "bytesBase64": base64.b64encode(payload).decode()}


def plan_fixture(root):
    event = object_("blob", b"{}")
    head = object_("blob", b'{"head":1}')
    tree_bytes = b"100644 event.json\0" + bytes.fromhex(event["oid"]) + b"100644 head.json\0" + bytes.fromhex(head["oid"])
    tree = object_("tree", tree_bytes)
    commit = object_("commit", f"tree {tree['oid']}\nauthor FS.GG cutover <cutover@fs.gg> 1788948000 +0000\ncommitter FS.GG cutover <cutover@fs.gg> 1788948000 +0000\n\nInitialize\n".encode())
    value = {"schema": transport.PLAN_SCHEMA, "expectedRef": "absent", "ref": "refs/heads/fsgg/v2/journal/cutover/d5",
             "tag": "refs/tags/fsgg/v2/fleet-cutover/operating-v1/test", "commitOid": commit["oid"],
             "operationOrder": ["put-event-blob", "put-head-blob", "put-tree", "put-commit", "reread-objects",
                                "create-ref-expected-absent", "reread-ref", "create-tag-expected-absent", "reread-tag"],
             "objects": [event, head, tree, commit], "seal": "a" * 64}
    path = root / "plan.json"
    write(path, value)
    return path, value


class LiveOperationTests(unittest.TestCase):
    def test_transport_recomputes_every_git_oid_and_rejects_tamper(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            path, value = plan_fixture(root)
            plan, objects = transport.load_plan(path)
            self.assertEqual(plan["commitOid"], objects[-1]["oid"])
            value["objects"][0]["bytesBase64"] = base64.b64encode(b"changed").decode()
            write(path, value)
            with self.assertRaisesRegex(transport.Refused, "object-oid"):
                transport.load_plan(path)

    def test_transport_refuses_competing_ref_before_objects(self):
        with tempfile.TemporaryDirectory() as scratch:
            path, _ = plan_fixture(pathlib.Path(scratch))
            plan, objects = transport.load_plan(path)
            original_mint, original_read = transport.mint, transport.read_ref
            transport.mint = lambda _: "synthetic"
            transport.read_ref = lambda *_: "f" * 40
            try:
                with self.assertRaisesRegex(transport.Refused, "expected-absence-conflict"):
                    transport.apply(plan, objects, 3)
            finally:
                transport.mint, transport.read_ref = original_mint, original_read

    def test_tree_and_commit_parsers_preserve_exact_inputs(self):
        with tempfile.TemporaryDirectory() as scratch:
            path, _ = plan_fixture(pathlib.Path(scratch))
            _, objects = transport.load_plan(path)
            self.assertEqual(["event.json", "head.json"], [x["path"] for x in transport.parse_tree(objects[2]["bytes"])])
            parsed = transport.parse_commit(objects[3]["bytes"])
            self.assertEqual([], parsed["parents"])
            self.assertEqual("Initialize", parsed["message"])

    def test_transport_round_trip_validates_structured_tree_and_parentless_commit(self):
        with tempfile.TemporaryDirectory() as scratch:
            path, _ = plan_fixture(pathlib.Path(scratch))
            _, objects = transport.load_plan(path)
            by_oid = {item["oid"]: item for item in objects}

            def response(path, *_args, **_kwargs):
                item = next(value for key, value in by_oid.items() if key in path)
                if item["kind"] == "blob":
                    return 200, {"sha": item["oid"], "content": base64.b64encode(item["bytes"]).decode()}
                if item["kind"] == "tree":
                    return 200, {"sha": item["oid"], "tree": transport.parse_tree(item["bytes"])}
                parsed = transport.parse_commit(item["bytes"])
                return 200, {"sha": item["oid"], "message": parsed["message"], "tree": {"sha": parsed["tree"]},
                             "parents": [], "author": parsed["author"], "committer": parsed["committer"]}

            original = transport.request
            transport.request = response
            try:
                transport.verify_objects(objects)
                bad_tree = transport.parse_tree(objects[2]["bytes"])
                bad_tree[0] = dict(bad_tree[0], sha="f" * 40)
                transport.request = lambda path, *_args, **_kwargs: (
                    (200, {"sha": objects[2]["oid"], "tree": bad_tree}) if objects[2]["oid"] in path else response(path))
                with self.assertRaisesRegex(transport.Refused, "tree-readback"):
                    transport.verify_objects(objects)
            finally:
                transport.request = original

    def test_manifest_derivation_binds_accepted_sources_and_coherent_capture(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            receipt = {"schema": "fsgg.coordination.unit-acceptance/1", "unitId": "GS2-08.1", "state": "accepted",
                       "digest": "1" * 64, "artifacts": [{"name": "epoch-wire-protocol", "sha256": "2" * 64}]}
            policy = {"authorityRepositoryId": 1351660651, "fleetRef": operation.FLEET_REF,
                      "controlIssue": {"number": 2}, "sharedAppProductionBlocker": False, "applyAuthorized": False,
                      "ordinaryWriter": {"appId": 4882140}, "cutoverWriter": {"appId": 4882399}}
            bindings = {"ordinaryWriterAppId": 4882140, "ordinaryWriterInstallationId": 160261608,
                        "cutoverWriterAppId": 4882399, "cutoverWriterInstallationId": 160261436, "controlIssueNumber": 2}
            first = {"schema": "fsgg.github-ledger-protection-live-capture/v1", "repository": operation.AUTHORITY,
                     "repositoryId": 1351660651, "capturePass": 1, "gaps": [], "rawSetSha256": "3" * 64,
                     "normalizedSetSha256": "4" * 64, "bindings": bindings}
            paths = {name: root / name for name in ("accepted.json", "policy.json", "first.json", "second.json", "public.pem")}
            write(paths["accepted.json"], receipt); write(paths["policy.json"], policy); write(paths["first.json"], first)
            second = dict(first); second.update({"capturePass": 2, "continuity": "matched", "previousEvidenceSha256": hashlib.sha256(paths["first.json"].read_bytes()).hexdigest()})
            write(paths["second.json"], second)
            private = root / "test-private.pem"
            subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(private)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            subprocess.run(["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(paths["public.pem"])],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            paths["public.pem"].chmod(0o600)
            args = type("Args", (), {"accepted_receipt": paths["accepted.json"], "desired_policy": paths["policy.json"],
                       "first_capture": paths["first.json"], "second_capture": paths["second.json"], "authorizer_public_key": paths["public.pem"],
                       "authorizer_key_id": "independent-1", "source_revision": "a" * 40, "source_tree": "b" * 40,
                       "created_at": "2026-09-09T12:00:00Z", "output_dir": root / "out"})()
            operation.derive(args)
            manifest = json.loads((root / "out/initial-manifest.json").read_bytes())
            trust = json.loads((root / "out/trust-anchor.json").read_bytes())
            self.assertEqual("1" * 64, manifest["acceptedGenesis"]["receiptDigest"])
            self.assertEqual(operation.spki_sha(paths["public.pem"]), manifest["authorizer"]["publicKeySpkiSha256"])
            self.assertEqual(operation.AUTHORIZATION_WORKFLOW_REVISION, manifest["authorizationWorkflow"]["anchorRevision"])
            self.assertEqual(hashlib.sha256((root / "out/initial-manifest.json").read_bytes()).hexdigest(), trust["manifestSha256"])
            self.assertEqual(0, (root / "out/initializer-input.json").stat().st_mode & 0o077)
            initializer = json.loads((root / "out/initializer-input.json").read_bytes())
            initializer["expectedRef"] = "absent"
            payload = operation.canonical(initializer)
            operation.initializer_payload(payload, operation.spki_sha(paths["public.pem"]))
            initializer["authorizationWorkflowRevision"] = "f" * 40
            with self.assertRaisesRegex(operation.Refused, "initializer-payload-binding"):
                operation.initializer_payload(operation.canonical(initializer), operation.spki_sha(paths["public.pem"]))

    def test_protected_authorization_requires_an_eligible_reviewer_and_exact_run(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            receipt = {"schema": "fsgg.github-ledger-protected-authorization/1", "repository": "FS-GG/.github",
                       "environment": "fleet-cutover", "inputSha256": "1" * 64, "coordinationRevision": "a" * 40,
                       "dotgithubRevision": "b" * 40, "operationId": "gs2-08-2-live",
                       "runId": 17, "conclusion": "success", "approvedAt": "2026-09-09T12:00:00Z", "expiresAt": "2026-09-09T13:00:00Z"}
            path = root / "receipt.json"; write(path, receipt)
            args = type("Args", (), {"protected_receipt": path, "source_revision": "a" * 40,
                                     "operation_id": "gs2-08-2-live", "now": "2026-09-09T12:01:00Z"})()
            original = operation.gh_json
            workflow_bytes = b"workflow"
            original_workflow_sha = operation.AUTHORIZATION_WORKFLOW_SHA256
            operation.AUTHORIZATION_WORKFLOW_SHA256 = hashlib.sha256(workflow_bytes).hexdigest()
            def provider(call):
                joined = " ".join(call)
                if joined.endswith("/17"):
                    return {"conclusion": "success", "head_sha": "b" * 40}
                if joined.endswith("/approvals"):
                    return [{"state": "approved", "user": {"id": value}} for value in operation.REVIEWERS]
                if "/compare/" in joined:
                    return {"status": "ahead"}
                return {"content": base64.b64encode(workflow_bytes).decode()}
            operation.gh_json = provider
            try:
                self.assertEqual(17, operation.protected(args, "1" * 64)[0]["runId"])
                def drifted_workflow(call):
                    return {"content": base64.b64encode(b"changed").decode()} if "/contents/" in " ".join(call) else provider(call)
                operation.gh_json = drifted_workflow
                with self.assertRaisesRegex(operation.Refused, "protected-workflow-drift"):
                    operation.protected(args, "1" * 64)
                def ineligible(call):
                    value = provider(call)
                    return [{"state": "approved", "user": {"id": 999}}] if " ".join(call).endswith("/approvals") else value
                operation.gh_json = ineligible
                with self.assertRaisesRegex(operation.Refused, "protected-reviewers"):
                    operation.protected(args, "1" * 64)
            finally:
                operation.gh_json = original
                operation.AUTHORIZATION_WORKFLOW_SHA256 = original_workflow_sha

    def test_runner_preview_is_inert_and_rejects_public_config(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            source = root / "source/eng"; source.mkdir(parents=True)
            (source / "capture-github-ledger-protection.py").write_text("")
            config = root / "config.json"
            value = {"schema": "fsgg.github-ledger-external-runner-config/1", "runnerId": "host-1",
                     "sourceRoot": str(root / "source"), "store": str(root / "store"), "controlIssueNumber": 2,
                     "ordinaryCredentialCommand": ["ordinary"], "cutoverCredentialCommand": ["cutover"],
                     "alertCommand": ["alert"], "alertTarget": "ops-primary"}
            write(config, value)
            loaded = runner.load_config(config)
            stream = io.StringIO()
            with contextlib.redirect_stdout(stream):
                self.assertEqual(0, runner.preview(loaded))
            self.assertFalse(json.loads(stream.getvalue())["installed"])
            config.chmod(0o644)
            with self.assertRaisesRegex(runner.Refused, "config-must-be-private"):
                runner.load_config(config)

    def test_operation_scripts_never_print_secret_or_token_fields(self):
        for name in ("github-ledger-operation.py", "github-ledger-initialization-transport.py", "github-ledger-monitor-runner.py"):
            source = (ROOT / name).read_text().lower()
            self.assertNotIn('print(token', source)
            self.assertNotIn('print(completed.stdout', source)
            self.assertNotIn('privatekeypem', source)


if __name__ == "__main__":
    unittest.main()
