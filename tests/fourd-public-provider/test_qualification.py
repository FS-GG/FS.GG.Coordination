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
        "GITHUB_REPOSITORY_ID": qualify.EXPECTED_REPOSITORY_ID,
        "GITHUB_REF": qualify.EXPECTED_REF,
        "GITHUB_EVENT_NAME": "workflow_dispatch",
        "GITHUB_SHA": "a" * 40,
        "GITHUB_WORKFLOW_REF": qualify.EXPECTED_WORKFLOW_REF,
        "GITHUB_WORKFLOW_SHA": "a" * 40,
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
        "placementSha": "a" * 40, "phase": "qualification",
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

    def test_attempt_one_ignores_unexpected_secret_authority(self):
        env = context("1"); env[qualify.ADMISSION_SECRET] = "not-base64"; env[qualify.KEY_SECRET] = "not-base64"
        self.assertEqual((None, None), qualify.admission(env))

    def test_repository_id_and_workflow_sha_are_server_bound(self):
        for field, value in (("GITHUB_REPOSITORY_ID", "9"), ("GITHUB_WORKFLOW_SHA", "b" * 40)):
            env = context(); env[field] = value
            with self.subTest(field=field), self.assertRaisesRegex(qualify.Refusal, "execution-context"):
                qualify.execution_context(env)

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
    def test_reused_pid_is_old_identity_dead_and_new_process_is_never_signalled(self):
        old = qualify.ProcessIdentity(4242, 10)
        with mock.patch.object(qualify, "_process_observation",
                               return_value=("live", qualify.ProcessIdentity(4242, 11))), \
             mock.patch.object(qualify.os, "kill") as sent:
            self.assertEqual("dead", qualify.Effects._state(old))
            qualify.Effects._signal(old, __import__("signal").SIGTERM)
            sent.assert_not_called()

    def test_unreadable_or_malformed_owned_identity_is_unknown_and_never_signalled(self):
        for observation in ("permission", "malformed"):
            with self.subTest(observation=observation), tempfile.TemporaryDirectory() as temporary:
                child = subprocess.Popen(["sleep", "30"], start_new_session=True)
                try:
                    effects = qualify.Effects(); identity = qualify._process_identity(child.pid)
                    self.assertIsNotNone(identity); effects.owned[child.pid] = identity
                    original = pathlib.Path.read_text
                    def read_text(path, *args, **kwargs):
                        if str(path) == f"/proc/{child.pid}/stat":
                            if observation == "permission": raise PermissionError("fixture")
                            return "malformed"
                        return original(path, *args, **kwargs)
                    with mock.patch.object(pathlib.Path, "read_text", read_text), \
                         mock.patch.object(qualify, "TERM_SETTLE_SECONDS", 0.02), \
                         mock.patch.object(qualify, "KILL_SETTLE_SECONDS", 0.02), \
                         mock.patch.object(qualify.os, "kill") as sent:
                        self.assertFalse(effects.settle())
                        sent.assert_not_called()
                    self.assertIsNone(child.poll())
                    self.assertIn(child.pid, effects.owned)
                    self.assertTrue(effects.settle())
                finally:
                    if child.poll() is None: child.terminate()
                    child.wait(timeout=2)

    def test_total_deadline_refuses_before_launch_when_settlement_reserve_is_missing(self):
        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(qualify.subprocess, "Popen") as launched:
            with self.assertRaisesRegex(qualify.Refusal, "child-total-deadline"):
                qualify.Effects().run(["/bin/true"], cwd=pathlib.Path(temporary), env={"PATH":"/usr/bin:/bin"},
                                      timeout=1, total_timeout=20, termination_grace=10, capture=None)
            launched.assert_not_called()

    def test_continuous_output_cannot_starve_total_deadline(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); started = __import__("time").monotonic()
            with mock.patch.object(qualify, "TERM_SETTLE_SECONDS", 0.05), \
                 mock.patch.object(qualify, "KILL_SETTLE_SECONDS", 0.05), \
                 mock.patch.object(qualify, "WAIT_SECONDS", 0.05), \
                 mock.patch.object(qualify, "DRAIN_SECONDS", 0.05):
                result = qualify.Effects().run(
                    ["/usr/bin/python3", "-c", "import os; b=b'x'*65536\nwhile True: os.write(1,b)"],
                    cwd=root, env={"PATH":"/usr/bin:/bin"}, timeout=0.15, total_timeout=1.3,
                    termination_grace=0.05, capture=root / "capture.json")
            self.assertNotEqual(0, result.returncode)
            self.assertLess(__import__("time").monotonic() - started, 1.5)
            self.assertLessEqual(len(result.stdout), qualify.MAX_CAPTURE)

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
                cwd=root, env={"PATH": "/usr/bin:/bin"}, timeout=1, capture=capture, termination_grace=1)
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

    def test_new_session_descendant_with_inherited_pipe_is_discovered_and_settled(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); capture = root / "capture.json"
            program = "import subprocess;subprocess.Popen(['sleep','30'],start_new_session=True)"
            with self.assertRaisesRegex(qualify.Refusal, "child-scope-refused"):
                qualify.Effects().run(["/usr/bin/python3", "-c", program], cwd=root,
                                      env={"PATH":"/usr/bin:/bin"}, timeout=3, capture=capture)
            self.assertLessEqual(len(base64.b64decode(json.loads(capture.read_text())["stdoutTailBase64"])), qualify.MAX_CAPTURE)


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
            with mock.patch.object(qualify, "verify_public_checkout", return_value=("a" * 40, "b" * 40)), \
                 mock.patch.object(qualify, "verify_public_tools"), mock.patch.object(qualify, "acquire_source") as acquire:
                self.assertEqual(0, qualify.execute(args, context("1"), qualify.Effects()))
                acquire.assert_not_called()
            result = json.loads((root / "public/result.json").read_text())
            self.assertEqual("awaiting-exact-admission", result["outcome"])
            with mock.patch.object(qualify, "verify_public_checkout", return_value=("a" * 40, "b" * 40)), \
                 mock.patch.object(qualify, "_sha", return_value=qualify.CUSTODY_POLICY_SHA256):
                result = json.loads((root / "public/result.json").read_text()); result["placementTree"] = "b" * 40
                (root / "public/result.json").unlink(); qualify.write_result(root / "public/result.json", result)
                self.assertEqual(0, qualify.verify_upload(root / "public", ROOT, context("1"), qualify.Effects()))
            (root / "public/unexpected").write_text("x")
            with mock.patch.object(qualify, "verify_public_checkout", return_value=("a" * 40, "b" * 40)), \
                 mock.patch.object(qualify, "_sha", return_value=qualify.CUSTODY_POLICY_SHA256):
                self.assertEqual(2, qualify.verify_upload(root / "public", ROOT, context("1"), qualify.Effects()))

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
            native_calls = [kwargs for argv, kwargs in effects.calls if "preflight" in argv or "cleanup" in argv]
            self.assertTrue(native_calls)
            self.assertTrue(all(call["termination_grace"] >= 240 for call in native_calls))
            reserve = (qualify.NATIVE_SUPERVISOR_GRACE + qualify.TERM_SETTLE_SECONDS
                       + qualify.KILL_SETTLE_SECONDS + qualify.WAIT_SECONDS + qualify.DRAIN_SECONDS)
            self.assertTrue(all(call["total_timeout"] >= call["timeout"] + reserve for call in native_calls))

    def test_private_route_success_uses_real_native_validator_and_receipt_custody(self):
        class FakeEffects:
            def __init__(self, root): self.root = root; self.calls = []
            @staticmethod
            def put(path, value):
                path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                path.write_text(json.dumps(value)); path.chmod(0o600)
            def run(self, argv, **kwargs):
                self.calls.append((list(argv), kwargs))
                capture = kwargs.get("capture")
                if capture is not None: self.put(capture, {"closed":True})
                if "preflight" in argv:
                    value = {"schema":"fsgg.fourd.native-image-preflight/1", "sourceRevision":qualify.FOURD_SHA,
                             "sourceTree":qualify.FOURD_TREE, "sourceInventorySha256":qualify.FOURD_INVENTORY,
                             "operationUidGid32768Mapped":True, "namespaceRootMapsLaunchingUser":True,
                             "noSudoOrHostFallback":True}
                    self.put(self.root / "state/preflight.json", value)
                    return qualify.RunResult(0, json.dumps(value).encode(), b"")
                if argv[:2] == ["/usr/bin/dotnet", "--version"]:
                    return qualify.RunResult(0, b"10.0.400\n", b"")
                if "fresh-load" in argv:
                    archive = self.root / "state/build" / f"fsgg-fourd-portable-{qualify.FOURD_SHA}.oci.tar"
                    archive.parent.mkdir(mode=0o700, parents=True, exist_ok=True); archive.write_bytes(b"oci"); archive.chmod(0o600)
                    manifest = "sha256:" + "1" * 64; config = "sha256:" + "2" * 64
                    binding = {"schema":"fsgg.fourd.native-image-binding/1", "qualified":False,
                               "sourceRevision":qualify.FOURD_SHA,
                               "sourceTree":qualify.FOURD_TREE, "sourceInventorySha256":qualify.FOURD_INVENTORY,
                               "imageReference":"localhost/fsgg@"+manifest, "manifestDigest":manifest,
                               "buildConfigId":config, "configDigest":config, "imageId":config,
                               "archivePath":str(archive), "archiveSha256":qualify._sha(archive), "archiveBytes":archive.stat().st_size,
                               "temporaryMountNamespaceExited":True, "postNamespaceIdentityVerified":True}
                    self.put(self.root / "state/native-image-binding.json", binding)
                    self.put(self.root / "state/build/source-preparation.json", {"schema":"preparation"})
                if "fsi" in argv:
                    binding = json.loads((self.root / "state/native-image-binding.json").read_text())
                    p2 = {"schema":"fsgg.fourd.native-p2-qualification/1", "qualified":True, "outcome":"passed",
                          "sourceRevision":qualify.FOURD_SHA, "sourceTree":qualify.FOURD_TREE,
                          "imageReference":binding["imageReference"], "manifestDigest":binding["manifestDigest"],
                          "executionStarted":True, "cleanupCompleted":True,
                          "duplicateSuppressedAfterReconstruction":True, "settledRecoveryWithoutRelaunch":True,
                          "remainingExecutionRoots":0, "prelaunchRefusals":["source","scope","operation"]}
                    self.put(self.root / "evidence/p2-result.json", p2)
                if "cleanup" in argv:
                    value = {"schema":"fsgg.fourd.native-cleanup/1", "complete":True}
                    self.put(self.root / "state/cleanup.json", value)
                    return qualify.RunResult(0, json.dumps(value).encode(), b"")
                return qualify.RunResult(0, b"", b"")
            def settle(self): return True
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); source = root / "source"; source.mkdir()
            private = root / "private"; private.mkdir(mode=0o700); (private / "home").mkdir(mode=0o700)
            (private / "capture").mkdir(mode=0o700)
            effects = FakeEffects(private); progress = qualify.base_result(context(), "failed")
            archive, evidence, binding = qualify.run_private_route(
                source, private, root / "setup", root / "p2", {"runId":"12345","runAttempt":"2"}, effects, progress)
            self.assertEqual(binding["archiveSha256"], qualify._sha(archive)); self.assertTrue(evidence.is_file())
            with __import__("tarfile").open(evidence) as retained:
                self.assertTrue({"native/native-image-binding.json", "native/preflight.json", "native/cleanup.json",
                                 "native/source-preparation.json"}.issubset(retained.getnames()))
            custody_spec = importlib.util.spec_from_file_location("fourd_test_custody", ROOT / "eng/fourd-public-provider/custody.py")
            custody = importlib.util.module_from_spec(custody_spec); assert custody_spec.loader is not None
            sys.modules[custody_spec.name] = custody; custody_spec.loader.exec_module(custody)
            identity = {"schema":"fsgg.fourd.custody-identity/1", "runId":"12345", "runAttempt":"2",
                        "runNonce":"run-12345-02", "placementSha":"a" * 40,
                        "fourdSourceSha":qualify.FOURD_SHA, "fourdSourceTree":qualify.FOURD_TREE,
                        "fourdInventorySha256":qualify.FOURD_INVENTORY, "p2SourceSha":qualify.P2_SHA,
                        "p2SourceTree":qualify.P2_TREE, "nativePolicySha256":qualify.NATIVE_POLICY_SHA256,
                        "sealerSha256":qualify.SEALER_SHA256, "publicKeySha256":qualify.PUBLIC_KEY_SHA256,
                        "nativeBindingSha256":qualify._sha(private / "state/native-image-binding.json"),
                        "nativeEvidenceSha256":qualify._sha(evidence)}
            sealed = custody.seal_series(archive=archive, evidence=evidence, scratch=private / "test-scratch",
                                         output=private / "test-sealed", identity=identity,
                                         deadline_monotonic=__import__("time").monotonic() + 120)
            self.assertEqual(sealed, custody.verify_staging(private / "test-sealed", identity))
            manifest = json.loads((private / "test-sealed/manifest.json").read_text())
            self.assertEqual((binding["archiveSha256"], binding["archiveBytes"]),
                             (manifest["archive"]["sha256"], manifest["archive"]["bytes"]))


class NativeAcceptanceTests(unittest.TestCase):
    def values(self):
        manifest = "sha256:" + "1" * 64; config = "sha256:" + "2" * 64
        image = {"schema":"fsgg.fourd.native-image-binding/1", "qualified":False, "sourceRevision":qualify.FOURD_SHA,
                 "sourceTree":qualify.FOURD_TREE, "sourceInventorySha256":qualify.FOURD_INVENTORY,
                 "imageReference":"localhost/fsgg@" + manifest, "manifestDigest":manifest,
                 "buildConfigId":config, "configDigest":config, "imageId":config,
                 "archiveSha256":"3" * 64, "archiveBytes":4096,
                 "temporaryMountNamespaceExited":True, "postNamespaceIdentityVerified":True}
        p2 = {"schema":"fsgg.fourd.native-p2-qualification/1", "qualified":True, "outcome":"passed",
              "sourceRevision":qualify.FOURD_SHA, "sourceTree":qualify.FOURD_TREE,
              "imageReference":image["imageReference"], "manifestDigest":manifest,
              "executionStarted":True, "cleanupCompleted":True,
              "duplicateSuppressedAfterReconstruction":True, "settledRecoveryWithoutRelaunch":True,
              "remainingExecutionRoots":0, "prelaunchRefusals":["source","scope","operation"]}
        return p2, image

    def write(self, root, p2, image):
        for name, value in (("p2.json", p2), ("binding.json", image)):
            path = root / name; path.write_text(json.dumps(value)); path.chmod(0o600)
        return root / "p2.json", root / "binding.json"

    def test_all_five_image_identities_and_archive_types_are_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); p2, image = self.values()
            paths = self.write(root, p2, image)
            self.assertEqual(image, qualify.validate_native(*paths))
            for field in ("imageReference", "manifestDigest", "buildConfigId", "configDigest", "imageId"):
                changed = dict(image); changed[field] = None
                paths = self.write(root, p2, changed)
                with self.subTest(field=field), self.assertRaisesRegex(qualify.Refusal, "native-acceptance"):
                    qualify.validate_native(*paths)
            changed = dict(image); changed["archiveBytes"] = True
            with self.assertRaisesRegex(qualify.Refusal, "native-acceptance"):
                qualify.validate_native(*self.write(root, p2, changed))

    def test_boolean_remaining_process_count_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); p2, image = self.values(); p2["remainingExecutionRoots"] = False
            with self.assertRaisesRegex(qualify.Refusal, "native-acceptance"):
                qualify.validate_native(*self.write(root, p2, image))

    def test_original_archive_hash_and_length_are_rechecked(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = pathlib.Path(temporary) / "image.oci.tar"; archive.write_bytes(b"oci"); archive.chmod(0o600)
            binding = {"archivePath":str(archive), "archiveSha256":qualify._sha(archive),
                       "archiveBytes":archive.stat().st_size}
            qualify.verify_native_archive(archive, binding)
            for field, value in (("archiveSha256", "0" * 64), ("archiveBytes", 4), ("archiveBytes", True)):
                changed = dict(binding); changed[field] = value
                with self.subTest(field=field, value=value), self.assertRaisesRegex(qualify.Refusal, "native-archive"):
                    qualify.verify_native_archive(archive, changed)


class UploadEligibilityTests(unittest.TestCase):
    def qualified_output(self, root):
        output = root / "public"; output.mkdir(mode=0o700)
        private = root / "private"; private.mkdir(mode=0o700)
        archive = private / "archive"; archive.write_bytes(b"oci"); archive.chmod(0o600)
        evidence = private / "evidence"; evidence.write_bytes(b"evidence"); evidence.chmod(0o600)
        identity = {"schema":"fsgg.fourd.custody-identity/1", "runId":"12345", "runAttempt":"2",
                    "runNonce":"run-12345-02", "placementSha":"a" * 40,
                    "fourdSourceSha":qualify.FOURD_SHA, "fourdSourceTree":qualify.FOURD_TREE,
                    "fourdInventorySha256":qualify.FOURD_INVENTORY, "p2SourceSha":qualify.P2_SHA,
                    "p2SourceTree":qualify.P2_TREE, "nativePolicySha256":qualify.NATIVE_POLICY_SHA256,
                    "sealerSha256":qualify.SEALER_SHA256, "publicKeySha256":qualify.PUBLIC_KEY_SHA256,
                    "nativeBindingSha256":"4" * 64, "nativeEvidenceSha256":qualify._sha(evidence)}
        spec = importlib.util.spec_from_file_location("custody", ROOT / "eng/fourd-public-provider/custody.py")
        custody = importlib.util.module_from_spec(spec); assert spec.loader is not None
        sys.modules["custody"] = custody; spec.loader.exec_module(custody)
        sealed = custody.seal_series(archive=archive, evidence=evidence, scratch=output / "scratch",
                                     output=output / "sealed-stage", identity=identity,
                                     deadline_monotonic=__import__("time").monotonic() + 120)
        result = qualify.base_result(context(), "sealed-native-evidence")
        result.update(placementTree="b" * 40, capacityPassed=True, rootlessPreflightPassed=True,
                      nativeAccepted=True, cleanupComplete=True, custodyOutcome=sealed.outcome,
                      archiveComplete=True, manifestSha256=sealed.manifestSha256, qualified=True)
        qualify.write_result(output / "result.json", result)
        return output

    def verified(self, output):
        with mock.patch.object(qualify, "verify_public_checkout", return_value=("a" * 40, "b" * 40)), \
             mock.patch.object(qualify, "_sha", return_value=qualify.CUSTODY_POLICY_SHA256):
            return qualify.verify_upload(output, ROOT, context(), qualify.Effects())

    def test_altered_and_partial_staging_and_malformed_result_are_ineligible(self):
        for mutation in ("altered", "partial", "malformed"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                output = self.qualified_output(pathlib.Path(temporary))
                self.assertEqual(0, self.verified(output))
                if mutation == "altered":
                    capsule = next((output / "sealed-stage").glob("*.capsule.json"))
                    raw = bytearray(capsule.read_bytes()); raw[-2] ^= 1; capsule.write_bytes(raw); capsule.chmod(0o600)
                elif mutation == "partial":
                    (output / "sealed-stage/manifest.json").unlink()
                else:
                    (output / "result.json").write_text("{malformed"); (output / "result.json").chmod(0o600)
                self.assertEqual(2, self.verified(output))


class SourceContractTests(unittest.TestCase):
    def test_public_checkout_binds_server_sha_tree_and_cleanliness(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); repo = root / "repo"; repo.mkdir()
            env = {"PATH":"/usr/bin:/bin", "GIT_AUTHOR_NAME":"t", "GIT_AUTHOR_EMAIL":"t@example.invalid",
                   "GIT_COMMITTER_NAME":"t", "GIT_COMMITTER_EMAIL":"t@example.invalid"}
            subprocess.run(["git","init","-q"],cwd=repo,check=True,env=env)
            (repo / "tracked").write_text("x"); subprocess.run(["git","add","tracked"],cwd=repo,check=True,env=env)
            subprocess.run(["git","commit","-qm","fixture"],cwd=repo,check=True,env=env)
            sha = subprocess.check_output(["git","rev-parse","HEAD"],cwd=repo,text=True).strip()
            tree = subprocess.check_output(["git","rev-parse","HEAD^{tree}"],cwd=repo,text=True).strip()
            ctx = context(); ctx["GITHUB_SHA"] = sha; ctx["GITHUB_WORKFLOW_SHA"] = sha
            capture = root / "capture"; capture.mkdir(mode=0o700)
            self.assertEqual((sha, tree), qualify.verify_public_checkout(repo, ctx, qualify.Effects(), capture))
            (repo / "untracked").write_text("contamination")
            capture2 = root / "capture2"; capture2.mkdir(mode=0o700)
            with self.assertRaisesRegex(qualify.Refusal, "public-placement"):
                qualify.verify_public_checkout(repo, ctx, qualify.Effects(), capture2)

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
        self.assertIn("id: public-validation", text)
        self.assertIn("if: steps.public-validation.outcome == 'success'", text)
        self.assertNotIn("placementSha\": PLACEMENT_SHA", helper)

    def test_capacity_helper_remains_byte_exact(self):
        self.assertEqual("fc267c92058b4eb4f51dcd3fba90a76df1d83bf449f89637466e97370321f2b8",
                         qualify._sha(ROOT / "eng/fourd-public-provider/capacity.py"))


if __name__ == "__main__":
    unittest.main()
