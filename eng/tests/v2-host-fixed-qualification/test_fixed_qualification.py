from __future__ import annotations

from datetime import datetime, timedelta, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("v2_host_fixed_qualification", ROOT / "eng/v2-host-fixed-qualification.py")
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

SOURCE = "a" * 40


def canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def archive(path: Path, root: str, payload_name: str, payload: bytes, schema: str) -> tuple[str, str]:
    manifest = canonical({
        "schema": schema, "sourceRevision": SOURCE,
        "payload": {"path": f"{root}/{payload_name}", "bytes": len(payload), "sha256": sha(payload)},
    })
    with zipfile.ZipFile(path, "w") as zipped:
        zipped.writestr(f"{root}/{payload_name}", payload)
        zipped.writestr(f"{root}/manifest.json", manifest)
    return sha(path.read_bytes()), sha(payload)


class Fixture:
    def __init__(self, root: Path, host_mode: str = "pass"):
        self.root = root
        self.artifacts = root / "artifacts"
        for name in ("host-candidate", "host-verification", "runner-candidate", "runner-verification"):
            (self.artifacts / name).mkdir(parents=True)
        host_script = """#!/usr/bin/env python3
from datetime import datetime,timezone
import hashlib,json,os,subprocess,sys,time
args=sys.argv[1:]
if args[0] != 'qualify-fixed-job': sys.exit(20)
values={args[i]:args[i+1] for i in range(1,len(args),2)}
profile_bytes=open(values['--profile'],'rb').read()
profile=json.loads(profile_bytes)
runner=open(profile['runnerExecutable'],'rb').read()
if b'old-runner' in runner: sys.exit(21)
if 'MODE' == 'timeout': time.sleep(10)
p=profile['providerExecutable']
for call in (['--version'],['login','status']):
 r=subprocess.run([p,*call],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 if r.returncode: sys.exit(22)
if 'MODE' == 'mismatch': sys.exit(23)
os.mkdir(profile['disposableWorkspace'])
os.rmdir(profile['disposableWorkspace'])
now=datetime.now(timezone.utc).isoformat()
diagnostic={'schema':'fsgg.orchestration.served-compatibility-diagnostic/1','scope':'compatibility-diagnostic-only','adapterVersion':'codex-subscription-exec/1','versionState':'matched'}
cleanup={'processTreeTerminationRequired':True,'processTreeTerminated':True,'workspaceRemovalAttempted':True,'workspaceRemoved':True}
result={'schema':'fsgg.orchestration.host-fixed-qualification-result/1','operation':profile['operation'],'profileRevision':profile['revision'],'profileSha256':hashlib.sha256(profile_bytes).hexdigest(),'hostExecutableSha256':profile['hostExecutableSha256'],'runnerExecutableSha256':profile['runnerExecutableSha256'],'providerExecutableSha256':profile['providerExecutableSha256'],'runnerProtocol':profile['expectedRunnerProtocol'],'adapterVersion':profile['expectedAdapterVersion'],'credentialScope':profile['credentialScope'],'environmentAllowList':profile['environmentAllowList'],'maximumRuntimeSeconds':profile['maximumRuntimeSeconds'],'startedAt':now,'completedAt':now,'disposition':'passed','detail':'passed','diagnostic':diagnostic,'cleanup':cleanup}
open(values['--result'],'wb').write((json.dumps(result,sort_keys=True,separators=(',',':'))+'\\n').encode())
""".replace("MODE", host_mode)
        host_payload = host_script.encode()
        runner_payload = b"#!/bin/sh\nexit 0\n"
        host_archive_name = f"fsgg-coord-orchestration-host-linux-x64-{SOURCE}.zip"
        runner_archive_name = f"fsgg-coord-orchestration-runner-linux-x64-{SOURCE}.zip"
        host_archive_sha, host_payload_sha = archive(
            self.artifacts / "host-candidate" / host_archive_name,
            subject.HOST_ROOT, subject.HOST_PAYLOAD, host_payload, subject.HOST_SCHEMA)
        runner_archive_sha, runner_payload_sha = archive(
            self.artifacts / "runner-candidate" / runner_archive_name,
            subject.RUNNER_ROOT, subject.RUNNER_PAYLOAD, runner_payload, subject.RUNNER_SCHEMA)
        self.host = {
            "artifactId": 101, "artifactName": f"orchestration-host-linux-x64-{SOURCE}",
            "artifactDigest": "c" * 64,
            "archiveFile": host_archive_name, "archiveSha256": host_archive_sha,
            "payloadSha256": host_payload_sha, "runId": 201,
            "verificationArtifactId": 301, "verificationArtifactName": f"orchestration-host-verification-{SOURCE}",
            "verificationSha256": "0" * 64,
        }
        self.runner = {
            "artifactId": 102, "artifactName": f"orchestration-runner-client-linux-x64-{SOURCE}",
            "artifactDigest": "d" * 64,
            "archiveFile": runner_archive_name, "archiveSha256": runner_archive_sha,
            "payloadSha256": runner_payload_sha, "runId": 202,
            "verificationArtifactId": 302, "verificationArtifactName": f"orchestration-runner-client-verification-{SOURCE}",
            "verificationSha256": "0" * 64,
        }
        self._verification("host", self.host, subject.HOST_VERIFICATION)
        self._verification("runner", self.runner, subject.RUNNER_VERIFICATION)
        now = datetime.now(timezone.utc).replace(microsecond=0)
        self.profile = {
            "schema": subject.SCHEMA, "repository": subject.REPOSITORY, "sourceRevision": SOURCE,
            "validFrom": (now - timedelta(minutes=1)).isoformat().replace("+00:00", "Z"),
            "expiresAt": (now + timedelta(days=1)).isoformat().replace("+00:00", "Z"),
            "host": self.host,
            "operation": {"kind": "executor-compatibility-diagnostic", "cleanupRequired": True,
                          "expectedCodexVersion": "0.154.0", "timeoutSeconds": 1, "runner": self.runner},
        }
        self.profile_path = root / "profile.json"
        self.write_profile()

    def _verification(self, role: str, component: dict, schema: str) -> None:
        value = {
            "schema": schema, "candidate": SOURCE,
            "artifact": {
                "id": component["artifactId"], "name": component["artifactName"],
                "url": (f"https://github.com/{subject.REPOSITORY}/actions/runs/{component['runId']}"
                        f"/artifacts/{component['artifactId']}"),
                "digest": component["artifactDigest"],
            },
            "archive": {"sha256": component["archiveSha256"]},
            "payload": {"sha256": component["payloadSha256"]},
        }
        path = self.artifacts / f"{role}-verification" / "verification.json"
        path.write_bytes(canonical(value))
        component["verificationSha256"] = sha(path.read_bytes())

    def write_profile(self) -> None:
        self.profile_path.write_bytes(canonical(self.profile))


class FixedQualificationTests(unittest.TestCase):
    def test_positive_diagnostic_records_zero_model_sessions_and_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary))
            output = Path(temporary) / "result"
            subject.qualify(fixture.profile_path, fixture.artifacts, output, "b" * 40)
            result = json.loads((output / "result.json").read_bytes())
            self.assertEqual(0, result["modelSessions"])
            self.assertEqual(0, result["followUpWork"])
            self.assertEqual("b" * 40, result["workflowRevision"])
            self.assertEqual("complete", result["cleanup"])
            self.assertEqual(["version", "login-status"], result["providerObservations"])
            self.assertEqual(6, result["untouchedSurfaceCount"])

    def test_wrong_artifact_is_refused_before_host_execution(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary))
            archive_path = fixture.artifacts / "runner-candidate" / fixture.runner["archiveFile"]
            archive_path.write_bytes(archive_path.read_bytes() + b"drift")
            with self.assertRaisesRegex(SystemExit, "V2HQ-ARTIFACT runner archive digest differs"):
                subject.qualify(fixture.profile_path, fixture.artifacts, Path(temporary) / "result", "b" * 40)

    def test_expired_profile_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary))
            past = datetime.now(timezone.utc).replace(microsecond=0) - timedelta(days=2)
            fixture.profile["validFrom"] = past.isoformat().replace("+00:00", "Z")
            fixture.profile["expiresAt"] = (past + timedelta(days=1)).isoformat().replace("+00:00", "Z")
            fixture.write_profile()
            with self.assertRaisesRegex(SystemExit, "V2HQ-EXPIRED"):
                subject.load_profile(fixture.profile_path)

    def test_version_injection_and_recipe_fields_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary))
            fixture.profile["operation"]["expectedCodexVersion"] = "0.154.0; touch /tmp/owned"
            fixture.write_profile()
            with self.assertRaisesRegex(SystemExit, "V2HQ-INJECTION"):
                subject.load_profile(fixture.profile_path)
            fixture = Fixture(Path(temporary) / "second")
            fixture.profile["operation"]["recipe"] = "echo owned"
            fixture.write_profile()
            with self.assertRaisesRegex(SystemExit, "V2HQ-INJECTION"):
                subject.load_profile(fixture.profile_path)

    def test_old_runner_and_version_mismatch_fail_closed(self):
        for mode, mutation in (("pass", b"old-runner"), ("mismatch", None)):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                fixture = Fixture(Path(temporary), host_mode=mode)
                if mutation:
                    runner_archive = fixture.artifacts / "runner-candidate" / fixture.runner["archiveFile"]
                    runner_archive_sha, runner_payload_sha = archive(
                        runner_archive, subject.RUNNER_ROOT, subject.RUNNER_PAYLOAD, mutation,
                        subject.RUNNER_SCHEMA)
                    fixture.runner["archiveSha256"] = runner_archive_sha
                    fixture.runner["payloadSha256"] = runner_payload_sha
                    fixture._verification("runner", fixture.runner, subject.RUNNER_VERIFICATION)
                    fixture.write_profile()
                with self.assertRaisesRegex(SystemExit, "V2HQ-HOST exit=(21|23)"):
                    subject.qualify(fixture.profile_path, fixture.artifacts, Path(temporary) / "result", "b" * 40)

    def test_timeout_kills_the_host_process_group(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary), host_mode="timeout")
            with self.assertRaisesRegex(SystemExit, "V2HQ-TIMEOUT"):
                subject.qualify(fixture.profile_path, fixture.artifacts, Path(temporary) / "result", "b" * 40)

    def test_cancellation_kills_the_host_process_group(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Fixture(Path(temporary), host_mode="timeout")
            fixture.profile["operation"]["timeoutSeconds"] = 10
            fixture.write_profile()
            process = subprocess.Popen([
                "python3", str(ROOT / "eng/v2-host-fixed-qualification.py"), "qualify",
                "--profile", str(fixture.profile_path), "--artifacts", str(fixture.artifacts),
                "--output", str(Path(temporary) / "result"),
                "--workflow-revision", "b" * 40,
            ], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            time.sleep(0.5)
            process.terminate()
            stdout, stderr = process.communicate(timeout=5)
            self.assertNotEqual(0, process.returncode)
            self.assertEqual("", stdout)
            self.assertIn("V2HQ-CANCELLED", stderr)

    def test_workflow_has_no_operator_parameters_or_operational_secrets(self):
        workflow = (ROOT / ".github/workflows/v2-host-fixed-qualification.yml").read_text()
        self.assertIn("  workflow_dispatch:\n", workflow)
        self.assertNotIn("inputs:", workflow)
        self.assertNotIn("secrets.", workflow)
        self.assertNotIn("run-name:", workflow)
        self.assertEqual(4, workflow.count("artifact-ids:"))
        self.assertIn("permissions:\n  actions: read\n  contents: read", workflow)
        self.assertIn('--workflow-revision "$GITHUB_SHA"', workflow)


if __name__ == "__main__":
    unittest.main()
