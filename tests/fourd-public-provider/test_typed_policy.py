#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("typed_policy", ROOT / "eng/fourd-public-provider/typed_policy.py")
typed = importlib.util.module_from_spec(SPEC); assert SPEC.loader; SPEC.loader.exec_module(typed)
EXE = ROOT / "eng/fourd-public-provider/typed/publish/FourD.Typed"
def admission():
    value = {key:"fixture" for key in (
        "repository","repositoryId","environment","workflowPath","workflowRef","placementSha","phase","operationId",
        "runId","runAttempt","runNonce","originalActorId","triggeringActorId","fourdRepository","fourdRepositoryId",
        "fourdSourceSha","fourdSourceTree","fourdInventorySha256","p2SourceSha","p2SourceTree","capacityRunId",
        "capacityRunAttempt","capacityArtifactId","capacityArtifactDigest","sealerRecipeSha","sealerSha256",
        "custodyPublicKeySha256","nativePolicySha256","custodyPolicySha256","environmentReadbackSha256","issuedAt","expiresAt")}
    value.update(schema="fsgg.fourd.public-provider-admission/2", repository="FS-GG/FS.GG.Coordination",
        repositoryId="1346720714", environment="fourd-native-private-source",
        workflowPath=".github/workflows/fourd-public-provider-qualification.yml", phase="qualification",
        operationId="fourd-portable-technical", fourdRepository="FS-GG/FS.GG.FourD", fourdRepositoryId="1390568106",
        placementSha="a"*40, runId="12345", runAttempt="2",
        fourdSourceSha="d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264",
        fourdSourceTree="ae626190a30a784db8968157a1ef1c9c5c499770",
        fourdInventorySha256="bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49",
        p2SourceSha="c069263c3e9e8780b1596eee82d2f6c017daa8df", p2SourceTree="d525a227f5df61b551e09915df51d7d4bd9ec11e",
        sourceCapsule={})
    return value

def admit(runner, root, work, value=None):
    return typed.admit(runner=runner, source_root=root, work=work, admission=value or admission(),
        placement_sha="a"*40, run_id="12345", run_attempt="2",
        source_sha="d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264",
        source_tree="ae626190a30a784db8968157a1ef1c9c5c499770",
        inventory_sha256="bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49")

class Result:
    def __init__(self, process):
        self.returncode = process.returncode; self.stdout = process.stdout; self.stderr = process.stderr

class ActualRunner:
    def run(self, argv, *, cwd, env, timeout, capture):
        process = subprocess.run(argv, cwd=cwd, env=env, timeout=timeout, capture_output=True, check=False)
        pathlib.Path(capture).write_text(json.dumps({"returncode":process.returncode}))
        return Result(process)

class TypedPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not EXE.exists():
            subprocess.run(["dotnet", "publish", str(ROOT / "eng/fourd-public-provider/typed/FourD.Typed.fsproj"),
                            "-c", "Release", "-o", str(EXE.parent)], check=True)

    def test_real_compiled_consumer_admits_exact_identity_and_refuses_stale_effect(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"] = str(EXE)
            try:
                state = admit(ActualRunner(), ROOT, work)
                self.assertTrue(state["effectEligible"])
                stale = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="stale",
                    observation={"kind":"invalidate", "identity":"b" * 64, "cost":1}, state=state)
                self.assertFalse(stale["effectEligible"])
                with self.assertRaisesRegex(typed.TypedPolicyRefusal, "typed-policy-refused"):
                    typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="effect",
                        observation={"kind":"begin-effect", "resource":"native", "acknowledged":False, "cost":1}, state=stale)
            finally:
                os.environ.pop("FSGG_FOURD_TYPED_POLICY", None)

    def test_unacknowledged_effect_and_incomplete_cleanup_never_succeed(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"] = str(EXE)
            try:
                state = admit(ActualRunner(), ROOT, work)
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="begin",
                    observation={"kind":"begin-effect", "resource":"effect", "acknowledged":False, "cost":1}, state=state)
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="cancel",
                    observation={"kind":"cancel", "cost":1}, state=state)
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="close-one",
                    observation={"kind":"close", "resource":"source-plaintext", "cost":1}, state=state)
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="finish",
                    observation={"kind":"finish", "cost":1}, state=state)
                self.assertEqual("cleanup-failed", state["phase"])
                self.assertEqual("unknown", state["outcome"])
                self.assertFalse(state["cleanupComplete"]); self.assertFalse(state["successful"])
            finally:
                os.environ.pop("FSGG_FOURD_TYPED_POLICY", None)

    def test_compiled_join_refuses_stale_observed_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"] = str(EXE)
            try:
                with self.assertRaisesRegex(typed.TypedPolicyRefusal, "typed-source-join-refused"):
                    typed.admit(runner=ActualRunner(), source_root=ROOT, work=work, admission=admission(),
                        placement_sha="a"*40, run_id="12345", run_attempt="2", source_sha="0"*40,
                        source_tree="ae626190a30a784db8968157a1ef1c9c5c499770",
                        inventory_sha256="bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49")
            finally: os.environ.pop("FSGG_FOURD_TYPED_POLICY", None)

if __name__ == "__main__": unittest.main()
