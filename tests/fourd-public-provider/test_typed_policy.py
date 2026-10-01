#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
import datetime as dt
import hashlib

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
        workflowRef="FS-GG/FS.GG.Coordination/.github/workflows/fourd-public-provider-qualification.yml@refs/heads/qualification/fourd-native-20261001",
        operationId="fourd-portable-technical", fourdRepository="FS-GG/FS.GG.FourD", fourdRepositoryId="1390568106",
        placementSha="a"*40, runId="12345", runAttempt="2", originalActorId="17", triggeringActorId="17",
        fourdSourceSha="d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264",
        fourdSourceTree="ae626190a30a784db8968157a1ef1c9c5c499770",
        fourdInventorySha256="bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49",
        p2SourceSha="c069263c3e9e8780b1596eee82d2f6c017daa8df", p2SourceTree="d525a227f5df61b551e09915df51d7d4bd9ec11e",
        nativePolicySha256="92796150e503ee06f7dcda8e363d65d8d26a66563b142d8d1a58bfcfbd69ee45",
        custodyPolicySha256="50cd8ee14ce64f5073f68286bf70f5a4e15d919728e31abaa1674e6ab3abd46b",
        sealerRecipeSha="88d65daa1a1262d563c2312698e4a5e57109a824",
        sealerSha256="4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d",
        custodyPublicKeySha256="de40516580a5e6e97c154a8889c3ac6edf047b695ff5d4187386aeaccd61d3e7",
        capacityRunId="100",capacityRunAttempt="1",capacityArtifactId="200",capacityArtifactDigest="4"*64,
        environmentReadbackSha256="5"*64,
        issuedAt="2026-10-01T16:00:00Z", expiresAt="2026-10-01T16:30:00Z",
        sourceCapsule={"transport":"coordination-release-asset","releaseId":"1","assetId":"2","tag":"fixture-tag",
            "name":"fourd-source-fixture.capsule.json","ciphertextBytes":2,"ciphertextSha256":"1"*64,
            "descriptor":{"schema":"fsgg.fourd.source-capsule-descriptor/1","purpose":"fourd-source-acquisition",
                "placementSha":"a"*40,"runId":"12345","runAttempt":"2","runNonce":"fixture",
                "coordinationRepositoryId":"1346720714","fourdRepositoryId":"1390568106",
                "sourceSha":"d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264","sourceTree":"ae626190a30a784db8968157a1ef1c9c5c499770",
                "inventorySha256":"bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49",
                "plaintextBytes":2,"plaintextSha256":"6"*64,"recipientPublicKeySha256":"3"*64,
                "sealerSha256":"4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d",
                "issuedAt":"2026-10-01T16:00:00Z","expiresAt":"2026-10-01T16:30:00Z"},
            "descriptorSha256":"2"*64,"recipientPublicKeySha256":"3"*64})
    return value

def admit(runner, root, work, value=None):
    return typed.admit(runner=runner, source_root=root, work=work, admission=value or admission(),
        placement_sha="a"*40, run_id="12345", run_attempt="2",
        source_sha="d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264",
        source_tree="ae626190a30a784db8968157a1ef1c9c5c499770",
        inventory_sha256="bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49",
        observed_now="2026-10-01T16:01:00Z")

class Result:
    def __init__(self, process):
        self.returncode = process.returncode; self.stdout = process.stdout; self.stderr = process.stderr

class ActualRunner:
    def run(self, argv, *, cwd, env, timeout, capture, **_kwargs):
        process = subprocess.run(argv, cwd=cwd, env=env, timeout=timeout, capture_output=True, check=False)
        pathlib.Path(capture).write_text(json.dumps({"returncode":process.returncode}))
        return Result(process)

class TypedPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not EXE.exists():
            subprocess.run(["dotnet", "publish", str(ROOT / "eng/fourd-public-provider/typed/FourD.Typed.fsproj"),
                            "-c", "Release", "-o", str(EXE.parent)], check=True)
        names=("FourD.Typed","FourD.Typed.dll","FourD.Typed.deps.json","FourD.Typed.runtimeconfig.json","FSharp.Core.dll")
        binding={"schema":"fsgg.fourd.typed-policy-binding/1","sourceSha":"fixture",
                 "files":{name:hashlib.sha256((EXE.parent/name).read_bytes()).hexdigest() for name in names}}
        (EXE.parent/"typed-policy-binding.json").write_text(json.dumps(binding));os.chmod(EXE.parent/"typed-policy-binding.json",0o600)

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
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="cleanup",
                    observation={"kind":"begin-cleanup", "cancelled":False, "cost":1}, state=state)
                state = typed.transition(runner=ActualRunner(), source_root=ROOT, work=work, name="close-one",
                    observation={"kind":"close", "resource":"source-join", "cost":1}, state=state)
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

    def test_compiled_join_refuses_malformed_authority_fields(self):
        for field, bad in (("issuedAt","expired-invalid"),("originalActorId","wrong"),
                           ("nativePolicySha256","wrong"),("sourceCapsule",{})):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temporary:
                work=pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
                changed=admission(); changed[field]=bad
                try:
                    with self.assertRaisesRegex(typed.TypedPolicyRefusal,"typed-source-join-refused"):
                        admit(ActualRunner(),ROOT,work,changed)
                finally: os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_missing_identity_and_duplicate_json_refuse_before_effect(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); output=root/"output.json"
            missing=root/"missing.json"; missing.write_text(json.dumps({"schema":typed.REQUEST_SCHEMA,
                "state":{"phase":"admitted","owned":[],"closed":[],"budgetRemaining":5},
                "observation":{"kind":"begin-effect","resource":"native-route","acknowledged":True,"cost":1}}))
            process=subprocess.run([str(EXE),"transition",str(missing),str(output)],check=False)
            self.assertEqual(2,process.returncode);self.assertFalse(output.exists())
            duplicate=root/"duplicate.json";duplicate.write_text('{"schema":"fsgg.fourd.typed-operation-request/1","schema":"fsgg.fourd.typed-operation-request/1","budget":5,"observation":{"kind":"cancel","cost":1}}')
            self.assertEqual(2,subprocess.run([str(EXE),"transition",str(duplicate),str(output)],check=False).returncode)

    def test_production_entrypoint_requires_finished_typed_state_for_success(self):
        source=(ROOT/"eng/fourd-public-provider/qualify.py").read_text()
        begin=source.index('name="begin-effect"',source.index('def execute('))
        effect=source.index('run_private_route(',begin)
        finish=source.index('name="finish"',effect)
        success=source.index('result.update(outcome="sealed-native-evidence"',finish)
        self.assertLess(begin,effect);self.assertLess(effect,finish);self.assertLess(finish,success)
        self.assertIn('typed_state.get("successful") is not True',source[finish:success])

if __name__ == "__main__": unittest.main()
