#!/usr/bin/env python3
import base64
import contextlib
import datetime as dt
import gzip
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
    def test_compiled_initializer_payload_is_exactly_accepted_and_mutations_refuse(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            private = root / "private.pem"
            public = root / "public.pem"
            subprocess.run(
                ["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(private)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            subprocess.run(
                ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            private.chmod(0o600)
            public.chmod(0o600)
            public_spki = operation.spki_sha(public)
            workflow_bytes = b"test-protected-authorization-workflow"
            original_workflow_sha = operation.AUTHORIZATION_WORKFLOW_SHA256
            operation.AUTHORIZATION_WORKFLOW_SHA256 = hashlib.sha256(workflow_bytes).hexdigest()

            initializer = {
                "repositoryId": 1351660651, "repository": operation.AUTHORITY, "fleetId": "fs-gg-production",
                "ref": operation.FLEET_REF, "tag": "refs/tags/fsgg/v2/fleet-cutover/operating-v1/test",
                "manifestSha256": "1" * 64, "trustAnchorSha256": "2" * 64, "sourceSha256": "3" * 64,
                "desiredPolicySha256": "4" * 64, "firstCaptureSha256": "5" * 64,
                "secondCaptureSha256": "6" * 64, "authorizationKeyId": "integration-test",
                "authorizationKeySpkiSha256": public_spki, "cutoverAppId": 4882399,
                "cutoverInstallationId": 160261436,
                "authorizationWorkflowRevision": operation.AUTHORIZATION_WORKFLOW_REVISION,
                "authorizationWorkflowSha256": operation.AUTHORIZATION_WORKFLOW_SHA256,
                "controlIssueNumber": 2, "createdAt": "2026-09-09T12:00:00+00:00",
                "authorName": "FS.GG cutover", "authorEmail": "cutover@fs.gg",
            }
            input_path = root / "initializer-input.json"
            payload_path = root / "initializer-payload.json"
            write(input_path, initializer)
            compiled = subprocess.run(
                ["dotnet", "run", "--project", "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj",
                 "-c", "Release", "--", "ledger-protection", "initialize", "payload",
                 "--input", str(input_path), "--output", str(payload_path)],
                cwd=ROOT.parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
            self.assertEqual(0, compiled.returncode, compiled.stderr.decode())
            payload = payload_path.read_bytes()
            self.assertEqual(operation.canonical(json.loads(payload)), payload)
            self.assertFalse(payload.endswith(b"\n"))
            self.assertNotIn(b"\\u002B", payload)

            receipt_path = root / "protected-receipt.json"
            receipt = {
                "schema": "fsgg.github-ledger-protected-authorization/1", "repository": "FS-GG/.github",
                "environment": "fleet-cutover", "inputSha256": operation.sha(payload),
                "coordinationRevision": "a" * 40, "dotgithubRevision": "b" * 40,
                "operationId": "gs2-08-2-byte-contract-test", "runId": 17, "conclusion": "success",
                "approvedAt": "2026-09-09T12:00:00Z", "expiresAt": "2026-09-09T13:00:00Z",
            }
            write(receipt_path, receipt)
            output = root / "authorization.json"
            key_fd = os.open(private, os.O_RDONLY)
            args = type("Args", (), {
                "payload": payload_path, "protected_receipt": receipt_path, "public_key": public,
                "public_key_spki_sha256": public_spki, "key_id": "integration-test",
                "source_revision": "a" * 40, "operation_id": "gs2-08-2-byte-contract-test",
                "now": "2026-09-09T12:01:00Z", "output": output, "private_key_fd": key_fd,
            })()
            original_gh = operation.gh_json

            def provider(call):
                joined = " ".join(call)
                if joined.endswith("/17"):
                    return {"conclusion": "success", "head_sha": "b" * 40}
                if joined.endswith("/approvals"):
                    return [{"state": "approved", "user": {"id": 1645484}}]
                if "/compare/" in joined:
                    return {"status": "ahead"}
                return {"content": base64.b64encode(workflow_bytes).decode()}

            operation.gh_json = provider
            try:
                operation.authorize(args)
                authorization = json.loads(output.read_bytes())
                self.assertEqual(payload, base64.b64decode(authorization["payloadBase64"]))

                payload_path.write_bytes(payload + b"\n")
                with self.assertRaisesRegex(operation.Refused, "initializer-payload-not-canonical"):
                    operation.authorize(args)

                payload_path.write_bytes(payload.replace(b"+", b"\\u002B", 1))
                with self.assertRaisesRegex(operation.Refused, "initializer-payload-not-canonical"):
                    operation.authorize(args)

                changed = json.loads(payload)
                changed["controlIssueNumber"] = 3
                payload_path.write_bytes(operation.canonical(changed))
                with self.assertRaisesRegex(operation.Refused, "initializer-payload-binding"):
                    operation.authorize(args)

                changed = json.loads(payload)
                changed["extra"] = "refuse"
                payload_path.write_bytes(operation.canonical(changed))
                with self.assertRaisesRegex(operation.Refused, "initializer-payload-shape"):
                    operation.authorize(args)
            finally:
                os.close(key_fd)
                operation.gh_json = original_gh
                operation.AUTHORIZATION_WORKFLOW_SHA256 = original_workflow_sha

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
            self.assertEqual("Initialize\n", parsed["message"])

    def test_commit_post_preserves_terminal_lf_and_exact_git_oid(self):
        with tempfile.TemporaryDirectory() as scratch:
            path, _ = plan_fixture(pathlib.Path(scratch))
            _, objects = transport.load_plan(path)
            commit = objects[3]
            posted = []
            message_mutation = [None]

            def raw_commit(value):
                def person(name):
                    item = value[name]
                    stamp = dt.datetime.fromisoformat(item["date"])
                    seconds = int(stamp.timestamp())
                    offset = stamp.strftime("%z")
                    return f'{item["name"]} <{item["email"]}> {seconds} {offset}'

                return (
                    f'tree {value["tree"]}\n'
                    f'author {person("author")}\n'
                    f'committer {person("committer")}\n\n'
                    f'{value["message"]}'
                ).encode()

            def response(_path, _token, _method, value):
                posted.append(value)
                received = dict(value)
                if message_mutation[0] is not None:
                    received["message"] = message_mutation[0]
                return 201, {"sha": transport.oid("commit", raw_commit(received))}

            original = transport.request
            transport.request = response
            try:
                transport.put_commit(commit, "synthetic")
                self.assertEqual("Initialize\n", posted[0]["message"])
                self.assertEqual(commit["bytes"], raw_commit(posted[0]))

                for changed in ("Initialize", "Initialize\n\n"):
                    with self.subTest(message=repr(changed)):
                        message_mutation[0] = changed
                        with self.assertRaisesRegex(transport.Refused, "commit-object-mismatch"):
                            transport.put_commit(commit, "synthetic")
            finally:
                transport.request = original

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
                return 200, {"sha": item["oid"], "message": parsed["message"][:-1], "tree": {"sha": parsed["tree"]},
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

    def test_commit_readback_allows_only_one_terminal_lf_elision(self):
        with tempfile.TemporaryDirectory() as scratch:
            path, _ = plan_fixture(pathlib.Path(scratch))
            _, objects = transport.load_plan(path)
            commit = objects[3]
            parsed = transport.parse_commit(commit["bytes"])

            def live_value(**changes):
                value = {
                    "sha": commit["oid"],
                    "message": parsed["message"][:-1],
                    "tree": {"sha": parsed["tree"]},
                    "parents": [],
                    "author": dict(parsed["author"]),
                    "committer": dict(parsed["committer"]),
                }
                value.update(changes)
                return value

            original = transport.request
            try:
                transport.request = lambda *_args, **_kwargs: (200, live_value())
                transport.verify_objects([commit])

                mutations = [
                    ("content", {"message": "Changed"}, "commit-readback"),
                    ("internal-newline", {"message": "Init\nialize"}, "commit-readback"),
                    ("extra-byte", {"message": parsed["message"] + "x"}, "commit-readback"),
                    ("extra-lf", {"message": parsed["message"] + "\n"}, "commit-readback"),
                    ("tree", {"tree": {"sha": "f" * 40}}, "commit-readback"),
                    ("parents", {"parents": [{"sha": "f" * 40}]}, "commit-readback"),
                    ("author", {"author": dict(parsed["author"], name="changed")}, "commit-readback"),
                    ("committer", {"committer": dict(parsed["committer"], email="changed@example.com")}, "commit-readback"),
                    ("sha", {"sha": "f" * 40}, "object-readback"),
                ]
                for name, changes, refusal in mutations:
                    with self.subTest(name=name):
                        transport.request = lambda *_args, changes=changes, **_kwargs: (200, live_value(**changes))
                        with self.assertRaisesRegex(transport.Refused, refusal):
                            transport.verify_objects([commit])
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

    def test_runner_uses_completed_capture_clock_and_refuses_invalid_clock(self):
        with tempfile.TemporaryDirectory() as scratch:
            capture = pathlib.Path(scratch) / "capture.json"
            write(capture, {"capturedAt": "2026-09-09T13:16:42Z"})
            self.assertEqual("2026-09-09T13:16:42Z", runner.capture_observed_at(capture))
            write(capture, {"capturedAt": "2026-09-09T13:16:42"})
            with self.assertRaisesRegex(runner.Refused, "capture-observed-at"):
                runner.capture_observed_at(capture)

    def test_runner_retries_incoherent_capture_and_retains_private_evidence(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = pathlib.Path(scratch)
            store = root / "store"; store.mkdir(mode=0o700)
            private = root / "private"; private.mkdir(mode=0o700)
            calls = []
            original_invoke, original_sleep = runner.invoke_capture, runner.time.sleep
            def invoke(_config, output, previous=None):
                calls.append(previous)
                value = {"capturedAt": "2026-09-09T14:00:00Z", "gaps": ["bound-control-issue"] if len(calls) == 1 else []}
                write(output, value)
                return 2 if len(calls) == 1 else 0
            runner.invoke_capture = invoke
            runner.time.sleep = lambda _: None
            try:
                result = runner.coherent_capture({}, store, private)
            finally:
                runner.invoke_capture, runner.time.sleep = original_invoke, original_sleep
            self.assertEqual(3, len(calls))
            self.assertEqual([], json.loads(result.read_bytes())["gaps"])
            retained = list((store / "capture-failures").glob("*.json.gz"))
            self.assertEqual(1, len(retained))
            self.assertEqual(0, retained[0].stat().st_mode & 0o077)
            self.assertEqual(["bound-control-issue"], json.loads(gzip.decompress(retained[0].read_bytes()))["gaps"])
            persistent_calls = []
            def refuse(_config, output, previous=None):
                persistent_calls.append(previous)
                write(output, {"gaps": ["provider-read"]})
                return 2
            runner.invoke_capture = refuse
            runner.time.sleep = lambda _: None
            try:
                with self.assertRaisesRegex(runner.Refused, "capture-refused:attempts=3"):
                    runner.coherent_capture({}, store, private)
            finally:
                runner.invoke_capture, runner.time.sleep = original_invoke, original_sleep
            self.assertEqual(3, len(persistent_calls))

    def test_watchdog_reads_the_monitor_heartbeat_schema(self):
        with tempfile.TemporaryDirectory() as scratch:
            store = pathlib.Path(scratch)
            database = sqlite3.connect(store / "monitor.sqlite3")
            database.executescript("""
                create table heartbeat(id integer primary key, observed_at text not null, outcome text not null, run_id text not null);
                create table incidents(id text primary key, kind text not null, opened_at text not null);
                create table outbox(id text primary key, incident_id text not null, created_at text not null, delivered_at text, attempts integer not null default 0);
                insert into heartbeat values(1,'2026-09-09T12:55:00Z','green','run-1');
            """)
            database.close()
            config = {"store": str(store), "runnerId": "host-1", "alertCommand": ["unused"], "alertTarget": "ops-primary"}
            stream = io.StringIO()
            with contextlib.redirect_stdout(stream):
                self.assertEqual(0, runner.watchdog(config, "2026-09-09T13:00:00Z"))
            self.assertEqual("fresh", json.loads(stream.getvalue())["outcome"])

    def test_operating_recipe_uses_staggered_wall_clock_timers(self):
        recipe = (ROOT.parent / "docs/operations/github-ledger-operational-completeness.md").read_text()
        self.assertIn("OnCalendar=*:0/5", recipe)
        self.assertIn("OnCalendar=*:2/5", recipe)
        self.assertNotIn("\nOnUnitActiveSec=", recipe)

    def test_operation_scripts_never_print_secret_or_token_fields(self):
        for name in ("github-ledger-operation.py", "github-ledger-initialization-transport.py", "github-ledger-monitor-runner.py"):
            source = (ROOT / name).read_text().lower()
            self.assertNotIn('print(token', source)
            self.assertNotIn('print(completed.stdout', source)
            self.assertNotIn('privatekeypem', source)


if __name__ == "__main__":
    unittest.main()
