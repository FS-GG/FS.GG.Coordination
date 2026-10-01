#!/usr/bin/env python3
import base64
import datetime as dt
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("fourd_public_qualify", ROOT / "eng/fourd-public-provider/qualify.py")
qualify = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = qualify
SPEC.loader.exec_module(qualify)
WORKFLOW = ROOT / ".github/workflows/fourd-public-provider-qualification.yml"


def context(attempt="2"):
    return {
        "GITHUB_REPOSITORY": qualify.EXPECTED_REPOSITORY,
        "GITHUB_REF": qualify.EXPECTED_REF,
        "GITHUB_EVENT_NAME": "workflow_dispatch",
        "GITHUB_SHA": qualify.PLACEMENT_SHA,
        "GITHUB_WORKFLOW_REF": qualify.EXPECTED_WORKFLOW_REF,
        "GITHUB_WORKFLOW_SHA": qualify.PLACEMENT_SHA,
        "GITHUB_RUN_ID": "12345",
        "GITHUB_RUN_ATTEMPT": attempt,
        "GITHUB_ACTOR_ID": "17",
        "GITHUB_ACTOR": "fsgg-owner", "GITHUB_TRIGGERING_ACTOR": "fsgg-owner",
    }


def valid_admission(now):
    value = {
        "schema": qualify.ADMISSION_SCHEMA, "repository": qualify.EXPECTED_REPOSITORY,
        "repositoryId": qualify.EXPECTED_REPOSITORY_ID, "environment": qualify.EXPECTED_ENVIRONMENT,
        "workflowPath": qualify.EXPECTED_WORKFLOW, "workflowRef": qualify.EXPECTED_WORKFLOW_REF,
        "placementSha": qualify.PLACEMENT_SHA, "phase": "qualification",
        "operationId": "fourd-portable-technical", "runId": "12345", "runAttempt": "2",
        "runNonce": "run-12345-02", "originalActorId": "17", "triggeringActorId": "17",
        "fourdRepository": qualify.FOURD_REPOSITORY, "fourdRepositoryId": qualify.FOURD_REPOSITORY_ID,
        "fourdSourceSha": qualify.FOURD_SHA, "fourdSourceTree": qualify.FOURD_TREE,
        "fourdInventorySha256": qualify.FOURD_INVENTORY, "p2SourceSha": qualify.P2_SHA,
        "p2SourceTree": qualify.P2_TREE, "capacityRunId": "100", "capacityRunAttempt": "1",
        "capacityArtifactId": "200", "capacityArtifactDigest": "a" * 64,
        "deployKeyId": "300", "publicKeyFingerprint": qualify.KNOWN_HOST_FINGERPRINT,
        "readOnly": True, "sealerRecipeSha": qualify.SEALER_RECIPE_SHA,
        "sealerSha256": qualify.SEALER_SHA256, "custodyPublicKeySha256": qualify.PUBLIC_KEY_SHA256,
        "nativePolicySha256": qualify.NATIVE_POLICY_SHA256,
        "custodyPolicySha256": qualify.CUSTODY_POLICY_SHA256,
        "environmentReadbackSha256": "b" * 64,
        "issuedAt": (now - dt.timedelta(minutes=1)).isoformat().replace("+00:00", "Z"),
        "expiresAt": (now + dt.timedelta(minutes=30)).isoformat().replace("+00:00", "Z"),
    }
    key = b"-----BEGIN OPENSSH PRIVATE KEY-----\nfixture\n-----END OPENSSH PRIVATE KEY-----\n"
    env = context(); env[qualify.ADMISSION_SECRET] = base64.b64encode(json.dumps(value).encode()).decode()
    env[qualify.KEY_SECRET] = base64.b64encode(key).decode()
    return env, value, key


class AdmissionTests(unittest.TestCase):
    def test_attempt_one_without_secrets_is_reservation_only(self):
        self.assertEqual((None, None), qualify.admission(context("1")))

    def test_later_missing_partial_extra_and_stale_admission_refuse(self):
        now = dt.datetime.now(dt.timezone.utc)
        with self.assertRaisesRegex(qualify.Refusal, "admission-missing"):
            qualify.admission(context("2"), now)
        env, value, _ = valid_admission(now)
        env.pop(qualify.KEY_SECRET)
        with self.assertRaisesRegex(qualify.Refusal, "admission-partial"):
            qualify.admission(env, now)
        env, value, _ = valid_admission(now)
        value["extra"] = True
        env[qualify.ADMISSION_SECRET] = base64.b64encode(json.dumps(value).encode()).decode()
        with self.assertRaisesRegex(qualify.Refusal, "admission-shape"):
            qualify.admission(env, now)
        env, value, _ = valid_admission(now)
        value["issuedAt"] = (now-dt.timedelta(hours=2)).isoformat().replace("+00:00", "Z")
        value["expiresAt"] = (now-dt.timedelta(hours=1)).isoformat().replace("+00:00", "Z")
        env[qualify.ADMISSION_SECRET] = base64.b64encode(json.dumps(value).encode()).decode()
        with self.assertRaisesRegex(qualify.Refusal, "admission-time"):
            qualify.admission(env, now)

    def test_exact_actor_run_key_readonly_and_all_pins_bind(self):
        now = dt.datetime.now(dt.timezone.utc)
        env, expected, key = valid_admission(now)
        actual, actual_key = qualify.admission(env, now)
        self.assertEqual(expected, actual); self.assertEqual(key, actual_key)
        for field, wrong in (("triggeringActorId", "18"), ("readOnly", False),
                             ("fourdInventorySha256", "0" * 64), ("publicKeyFingerprint", "wrong")):
            changed = dict(expected); changed[field] = wrong
            bad = dict(env); bad[qualify.ADMISSION_SECRET] = base64.b64encode(json.dumps(changed).encode()).decode()
            with self.subTest(field=field), self.assertRaises(qualify.Refusal):
                qualify.admission(bad, now)

    def test_distinct_rerun_actor_uses_bounded_public_numeric_readback(self):
        class Response:
            status = 200
            def __enter__(self): return self
            def __exit__(self, *_args): return None
            def read(self, _limit): return b'{"login":"rerun-owner","id":29}'
        env = context(); env["GITHUB_TRIGGERING_ACTOR"] = "rerun-owner"
        with mock.patch.object(qualify.urllib.request, "urlopen", return_value=Response()) as readback:
            self.assertEqual("29", qualify.triggering_actor_id(env))
        request = readback.call_args.args[0]
        self.assertEqual("https://api.github.com/users/rerun-owner", request.full_url)


class SilentProcessTests(unittest.TestCase):
    def test_private_canary_is_hashed_not_copied_to_capture_or_exception(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); capture = root / "capture.json"
            secret = "PRIVATE-CANARY-DO-NOT-PRINT"
            result = qualify.Effects().run(
                ["/usr/bin/python3", "-c", f"import sys;print('{secret}');sys.stderr.write('{secret}')"],
                cwd=root, env={"PATH": "/usr/bin:/bin"}, timeout=5, capture=capture)
            self.assertEqual(0, result.returncode)
            self.assertNotIn(secret, capture.read_text())
            saved = json.loads(capture.read_text())
            self.assertEqual({"returncode", "stderrSha256", "stderrTruncated", "stdoutSha256", "stdoutTruncated",
                              "stdoutTailBase64", "stderrTailBase64"}, set(saved))
            self.assertIn(secret.encode(), base64.b64decode(saved["stdoutTailBase64"]))

    def test_timeout_settles_owned_process_group(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); capture = root / "capture.json"
            started = dt.datetime.now()
            result = qualify.Effects().run(
                ["/usr/bin/python3", "-c", "import subprocess,time;subprocess.Popen(['sleep','30']);time.sleep(30)"],
                cwd=root, env={"PATH": "/usr/bin:/bin"}, timeout=1, capture=capture)
            self.assertLess((dt.datetime.now()-started).total_seconds(), 8)
            self.assertNotEqual(0, result.returncode)

    def test_cancellation_blocks_new_work_but_allows_bounded_cleanup_child(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); effects = qualify.Effects()
            effects.request_cancel(); effects.request_cancel()
            with self.assertRaisesRegex(qualify.Refusal, "cancelled"):
                effects.run(["/bin/true"], cwd=root, env={"PATH":"/usr/bin:/bin"}, timeout=1,
                            capture=root / "blocked.json")
            result = effects.run(["/bin/true"], cwd=root, env={"PATH":"/usr/bin:/bin"}, timeout=1,
                                 capture=root / "cleanup.json", allow_cancelled=True)
            self.assertEqual(0, result.returncode)
            self.assertTrue(effects.settle())

    def test_successful_parent_with_live_group_descendant_is_refused_and_settled(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); capture = root / "capture.json"
            with self.assertRaisesRegex(qualify.Refusal, "child-scope-refused"):
                qualify.Effects().run(
                    ["/usr/bin/python3", "-c", "import subprocess;subprocess.Popen(['sleep','30'])"],
                    cwd=root, env={"PATH":"/usr/bin:/bin"}, timeout=3, capture=capture)


class ExecuteTests(unittest.TestCase):
    def args(self, root):
        return type("Args", (), {"output": str(root / "public/result.json"),
            "capacity_result": str(root / "capacity.json"), "public_source": str(ROOT),
            "setup_dotnet": str(root / "setup"), "p2_source": str(root / "p2"),
            "known_hosts": str(ROOT / "eng/fourd-public-provider/github-known-hosts"),
            "private_root": str(root / "private")})()

    def test_capacity_refusal_and_reservation_never_acquire_private_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); args = self.args(root)
            (root / "capacity.json").write_text('{"schema":"wrong"}\n')
            with mock.patch.object(qualify, "verify_public_tools") as tools, mock.patch.object(qualify, "acquire_source") as acquire:
                self.assertEqual(2, qualify.execute(args, context("1"), qualify.Effects()))
                tools.assert_not_called(); acquire.assert_not_called()
            result = json.loads((root / "public/result.json").read_text())
            self.assertFalse(result["capacityPassed"]); self.assertFalse(result["qualified"])
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); args = self.args(root)
            (root / "capacity.json").write_text(json.dumps({"schema":"fsgg.fourd.public-provider-capacity/1","phase":"capacity","capacityScreenPassed":True,"qualified":False}))
            with mock.patch.object(qualify, "verify_public_tools"), mock.patch.object(qualify, "acquire_source") as acquire:
                self.assertEqual(0, qualify.execute(args, context("1"), qualify.Effects()))
                acquire.assert_not_called()
            result = json.loads((root / "public/result.json").read_text())
            self.assertEqual("awaiting-exact-admission", result["outcome"])
            self.assertEqual(0, qualify.verify_upload(root / "public", context("1")))
            (root / "public/unexpected").write_text("x")
            self.assertEqual(2, qualify.verify_upload(root / "public", context("1")))

    def test_actual_route_preflight_refusal_cleans_and_skips_sdk_build_image_and_p2(self):
        class FakeEffects:
            def __init__(self): self.calls = []
            def run(self, argv, **kwargs):
                self.calls.append((list(argv), kwargs))
                if "preflight" in argv:
                    return qualify.RunResult(1, b"", b"private failure")
                if "cleanup" in argv:
                    return qualify.RunResult(0, b'{"schema":"fsgg.fourd.native-cleanup/1","complete":true}', b"")
                return qualify.RunResult(0, b"", b"")
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); source = root / "source"; source.mkdir()
            private = root / "private"; private.mkdir(mode=0o700); (private / "home").mkdir(); (private / "capture").mkdir()
            effects = FakeEffects(); progress = qualify.base_result(context(), "failed")
            admission_value = {"runId":"12345", "runAttempt":"2"}
            with self.assertRaisesRegex(qualify.Refusal, "rootless-preflight-failed"):
                qualify.run_private_route(source, private, root / "setup", root / "p2",
                                           admission_value, effects, progress)
            flattened = [item for argv, _ in effects.calls for item in argv]
            self.assertIn("preflight", flattened); self.assertIn("cleanup", flattened)
            self.assertNotIn("/usr/bin/node", flattened); self.assertNotIn("fresh-load", flattened)
            self.assertNotIn("fsi", flattened); self.assertTrue(progress["cleanupComplete"])


class SourceContractTests(unittest.TestCase):
    def test_known_host_bytes_and_pinned_installer_entry_are_frozen(self):
        known = ROOT / "eng/fourd-public-provider/github-known-hosts"
        self.assertEqual(qualify.KNOWN_HOST_SHA256, qualify._sha(known))
        checked = subprocess.run(["ssh-keygen", "-lf", str(known), "-E", "sha256"], check=True, capture_output=True, text=True)
        self.assertIn(qualify.KNOWN_HOST_FINGERPRINT, checked.stdout)
        self.assertEqual("a98b56852c35b8e3190ac28c8c2271da59106c68", qualify.SETUP_DOTNET_SHA)
        self.assertEqual("2f84c07aa0be5d2b1fce196bd549bb707b7ec01afa238533dee50c9e4b794569", qualify.SETUP_DOTNET_ENTRY_SHA256)

    def test_capacity_job_has_no_environment_or_secret_reference_and_jobs_are_exclusive(self):
        text = WORKFLOW.read_text()
        capacity_text = text.split("  capacity:\n", 1)[1].split("\n  qualification:\n", 1)[0]
        qualification_text = text.split("\n  qualification:\n", 1)[1]
        self.assertNotIn("environment:", capacity_text)
        self.assertNotIn("secrets.", capacity_text)
        self.assertNotIn(qualify.ADMISSION_SECRET, capacity_text)
        self.assertNotIn(qualify.KEY_SECRET, capacity_text)
        self.assertIn("inputs.phase == 'capacity'", capacity_text)
        self.assertIn("inputs.phase == 'qualification'", qualification_text)
        self.assertIn("timeout-minutes: 180", qualification_text)

    def test_workflow_has_no_dynamic_remote_or_tofu_and_preserves_exact_route_pins(self):
        text = WORKFLOW.read_text(); helper = (ROOT / "eng/fourd-public-provider/qualify.py").read_text()
        self.assertNotIn("ssh-keyscan", text + helper)
        self.assertNotIn("ssh-agent", text + helper)
        self.assertIn("IdentitiesOnly=yes", helper); self.assertIn("IdentityAgent=none", helper)
        self.assertIn(qualify.FOURD_SHA, helper); self.assertIn(qualify.P2_SHA, text + helper)
        self.assertIn('("10.0.400", "10.0.401")', helper)
        self.assertIn('"preflight"', helper); self.assertLess(helper.index('"preflight"'), helper.index('"10.0.400"'))
        self.assertEqual(1, text.count("secrets.FSGG_FOURD_PUBLIC_PROVIDER_ADMISSION_JSON_B64"))
        self.assertEqual(1, text.count("secrets.FSGG_FOURD_READONLY_DEPLOY_KEY_B64"))

    def test_capacity_helper_remains_byte_exact(self):
        self.assertEqual("fc267c92058b4eb4f51dcd3fba90a76df1d83bf449f89637466e97370321f2b8",
                         qualify._sha(ROOT / "eng/fourd-public-provider/capacity.py"))


if __name__ == "__main__":
    unittest.main()
