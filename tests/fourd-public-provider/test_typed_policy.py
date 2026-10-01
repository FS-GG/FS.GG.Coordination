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
        placementSha="a"*40, runId="12345", runAttempt="2", runNonce="fixture-1", originalActorId="17", triggeringActorId="17",
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
            "name":"fourd-source-fixture-1.capsule.json","ciphertextBytes":2,"ciphertextSha256":"1"*64,
            "descriptor":{"schema":"fsgg.fourd.source-capsule-descriptor/1","purpose":"fourd-source-acquisition",
                "placementSha":"a"*40,"runId":"12345","runAttempt":"2","runNonce":"fixture-1",
                "coordinationRepositoryId":"1346720714","fourdRepositoryId":"1390568106",
                "sourceSha":"d5d8b6d242b13dd79007fcbbb6e5ee4069fd3264","sourceTree":"ae626190a30a784db8968157a1ef1c9c5c499770",
                "inventorySha256":"bf3ec0ab2fe639bc9f4bc53da8f33c8adf8f237a505f9eee6eca0002b1cd1c49",
                "plaintextBytes":2,"plaintextSha256":"6"*64,"recipientPublicKeySha256":"3"*64,
                "sealerSha256":"4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d",
                "issuedAt":"2026-10-01T16:00:00Z","expiresAt":"2026-10-01T16:30:00Z"},
            "descriptorSha256":"pending","recipientPublicKeySha256":"3"*64})
    descriptor=value["sourceCapsule"]["descriptor"]
    value["sourceCapsule"]["descriptorSha256"]=hashlib.sha256((json.dumps(descriptor,sort_keys=True,separators=(",",":"))+"\n").encode()).hexdigest()
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

    def test_compiled_join_closes_and_hashes_capsule_descriptor_and_transport_names(self):
        cases=[]
        changed=admission();changed["sourceCapsule"]["descriptor"]["extra"]=True;cases.append(changed)
        changed=admission();changed["sourceCapsule"]["descriptorSha256"]="0"*64;cases.append(changed)
        changed=admission();changed["sourceCapsule"]["descriptor"]["runNonce"]="different-1";cases.append(changed)
        changed=admission();changed["sourceCapsule"]["tag"]="short";cases.append(changed)
        changed=admission();changed["sourceCapsule"]["name"]="unexpected.json";cases.append(changed)
        for index,changed in enumerate(cases):
            descriptor=changed["sourceCapsule"]["descriptor"]
            if index in (0,2):
                changed["sourceCapsule"]["descriptorSha256"]=hashlib.sha256(
                    (json.dumps(descriptor,sort_keys=True,separators=(",",":"))+"\n").encode()).hexdigest()
            with self.subTest(index=index),tempfile.TemporaryDirectory() as temporary:
                os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
                try:
                    with self.assertRaisesRegex(typed.TypedPolicyRefusal,"typed-source-join-refused"):
                        admit(ActualRunner(),ROOT,pathlib.Path(temporary),changed)
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

    def test_compiled_state_requires_equal_identity_chain_and_truthful_derived_fields(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                admitted=admit(ActualRunner(),ROOT,root)
                for name,changed in (
                    ("identity",dict(admitted,validatedIdentity="b"*64)),
                    ("eligibility",dict(admitted,effectEligible=False)),
                    ("success",dict(admitted,successful=True))):
                    request={"schema":typed.REQUEST_SCHEMA,"state":changed,
                             "observation":{"kind":"begin-effect","resource":"native","acknowledged":False,"cost":1}}
                    source=root/f"{name}.json"; output=root/f"{name}.out"
                    source.write_text(json.dumps(request,sort_keys=True,separators=(",",":")))
                    self.assertEqual(2,subprocess.run([str(EXE),"transition",str(source),str(output)],check=False).returncode)
                    self.assertFalse(output.exists())
            finally: os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_early_cancel_roundtrips_and_cancelled_success_is_never_successful(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                acquired=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="acquire-only",
                    observation={"kind":"acquire","identity":"a"*64,"resource":"source","cost":1})
                cleaning=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="early-clean",
                    observation={"kind":"begin-cleanup","cancelled":True,"cost":1},state=acquired)
                closed=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="early-close",
                    observation={"kind":"close","resource":"source","cost":1},state=cleaning)
                finished=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="early-finish",
                    observation={"kind":"finish","cost":1},state=closed)
                self.assertTrue(finished["cleanupComplete"]); self.assertFalse(finished["successful"])
                state=admit(ActualRunner(),ROOT,root)
                state=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="cancelled-effect",
                    observation={"kind":"begin-effect","resource":"effect","acknowledged":True,"cost":1},state=state)
                state=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="cancelled-clean",
                    observation={"kind":"begin-cleanup","cancelled":True,"cost":1},state=state)
                for resource in ("source-join","effect"):
                    state=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="cancelled-close-"+resource,
                        observation={"kind":"close","resource":resource,"cost":1},state=state)
                state=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="cancelled-finish",
                    observation={"kind":"finish","cost":1},state=state)
                self.assertTrue(state["cleanupComplete"]); self.assertFalse(state["successful"])
                state=dict(state,cancelled=False,successful=True)
                state=typed.transition(runner=ActualRunner(),source_root=ROOT,work=root,name="late-cancel",
                    observation={"kind":"cancel","cost":1},state=state)
                self.assertEqual("finished",state["phase"]);self.assertTrue(state["cleanupComplete"])
                self.assertTrue(state["cancelled"]);self.assertFalse(state["successful"])
            finally: os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_compiled_root_identity_binds_placement_tree(self):
        context={"placementSha":"a"*40,"runId":"12345","runAttempt":"2","runNonce":"fixture-1",
                 "originalActorId":"17","triggeringActorId":"17","capacityRunId":"100",
                 "capacityRunAttempt":"1","capacityArtifactId":"200","capacityArtifactDigest":"4"*64,
                 "environmentReadbackSha256":"5"*64}
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); identities=[]
            for index,tree in enumerate(("b"*40,"c"*40)):
                observed={"placementRefSha":"a"*40,"placementCommitSha":"a"*40,"placementTree":tree,
                    "reservationRunId":"12345","reservationRunAttempt":"1","reservationHeadSha":"a"*40,
                    "reservationStatus":"completed","reservationConclusion":"success","originalActorId":"17",
                    "triggeringActorId":"17","reservationArtifactId":"201","reservationArtifactDigest":"6"*64,
                    "reservationResultRunId":"12345","reservationResultRunAttempt":"1",
                    "reservationResultPlacementSha":"a"*40,"reservationResultPlacementTree":tree,
                    "reservationResultOutcome":"awaiting-exact-admission","reservationResultQualified":False,
                    "environmentReadbackSha256":"5"*64,"capacityRunId":"100","capacityRunAttempt":"1",
                    "capacityRunConclusion":"success","capacityArtifactId":"200","capacityArtifactDigest":"4"*64,
                    "secretCount":0,"releaseCount":0}
                request={"schema":"fsgg.fourd.typed-root-join-request/1","context":context,"observed":observed,
                         "placementSha":"a"*40,"placementTree":tree,"runId":"12345"}
                source=root/f"root-{index}.json";output=root/f"root-{index}.out"
                source.write_text(json.dumps(request,sort_keys=True,separators=(",",":")))
                self.assertEqual(0,subprocess.run([str(EXE),"validate-root",str(source),str(output)],check=False).returncode)
                identities.append(json.loads(output.read_text())["identity"])
            self.assertNotEqual(*identities)
            request["observed"]["reservationConclusion"]="failure"
            source=root/"root-tampered.json";output=root/"root-tampered.out"
            source.write_text(json.dumps(request,sort_keys=True,separators=(",",":")))
            self.assertEqual(2,subprocess.run([str(EXE),"validate-root",str(source),str(output)],check=False).returncode)

    def test_production_entrypoint_requires_finished_typed_state_for_success(self):
        source=(ROOT/"eng/fourd-public-provider/qualify.py").read_text()
        begin=source.index('name="begin-effect"',source.index('def execute('))
        effect=source.index('run_private_route(',begin)
        finish=source.index('name="finish"',effect)
        success=source.index('result.update(outcome="sealed-native-evidence"',finish)
        self.assertLess(begin,effect);self.assertLess(effect,finish);self.assertLess(finish,success)
        self.assertIn('typed_state.get("successful") is not True',source[finish:success])

if __name__ == "__main__": unittest.main()
