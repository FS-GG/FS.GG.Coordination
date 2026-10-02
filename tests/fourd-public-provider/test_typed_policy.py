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
import base64

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

def run_expected(identity="a"*64):
    return {"rootJoinIdentity":identity,"repository":"FS-GG/FS.GG.Coordination","repositoryId":1346720714,
            "runId":12345,"workflowPath":".github/workflows/fourd-public-provider-qualification.yml",
            "event":"workflow_dispatch","headBranch":"qualification/fourd-native-20261001","headSha":"b"*40,
            "originalActorId":17,"triggeringActorId":17,"producerSha256":"c"*64,"packetManifestSha256":"d"*64}

def run_snapshot(attempt,status,conclusion,**changes):
    value=dict(run_expected());value.pop("rootJoinIdentity");value.update(
        attempt=attempt,status=status,conclusion=conclusion)
    value.update(changes);return value

def observe_run(work,index,lifecycle,run_state,event,expected=None,accepted=True):
    event=dict(event)
    if event["kind"] in ("observe-current","observe-target") and "snapshot" in event:
        value=event.pop("snapshot"); status=event.pop("httpStatus")
        raw=None if value is None else {"repository":{"full_name":value["repository"],"id":value["repositoryId"]},
            "id":value["runId"],"run_attempt":value["attempt"],"path":value["workflowPath"],"event":value["event"],
            "head_branch":value["headBranch"],"head_sha":value["headSha"],"actor":{"id":value["originalActorId"]},
            "triggering_actor":{"id":value["triggeringActorId"]},"status":value["status"],"conclusion":value["conclusion"]}
        body=json.dumps(raw,separators=(",",":"),sort_keys=True).encode()
        event["response"]={"httpStatus":status,"bodyBase64":base64.b64encode(body).decode(),"linkHeaders":[],
                           "endpointRunId":12345,"endpointAttempt":0 if event["kind"]=="observe-current" else 2}
    if event["kind"]=="observe-jobs" and "jobs" in event:
        value=event.pop("jobs");status=event.pop("httpStatus")
        raw={"total_count":value["totalCount"],"jobs":[dict(id=j["id"],run_id=12345,run_attempt=2,
             head_sha="b"*40,name=j["name"],status=j["status"],conclusion=j["conclusion"]) for j in value["jobs"]]}
        body=json.dumps(raw,separators=(",",":"),sort_keys=True).encode()
        event["response"]={"httpStatus":status,"bodyBase64":base64.b64encode(body).decode(),"linkHeaders":[],"endpointRunId":12345,"endpointAttempt":2}
    if event["kind"]=="enter-cleanup":event.setdefault("signalCancelled",False)
    if event["kind"]=="record-resources-retired" and "allClosed" in event:
        closed=event.pop("allClosed")
        event["facts"]={"secretCount":0 if closed else 1,"releaseCount":0,"assetStatus":"404","releaseStatus":"404",
                        "tagStatus":"404","privateKeyAbsent":True,"publicKeyAbsent":True,"failureCount":0 if closed else 1}
    request={"schema":"fsgg.fourd.root-run-request/1","lifecycleState":lifecycle,"runState":run_state,
             "expected":expected or run_expected(),"event":event}
    source=work/f"observe-{index}.json";output=work/f"observe-{index}.out"
    source.write_text(json.dumps(request,sort_keys=True,separators=(",",":")))
    process=subprocess.run([str(EXE),"observe-run",str(source),str(output)],check=False,capture_output=True)
    if accepted:
        if process.returncode != 0: raise AssertionError(process.stderr)
        return json.loads(output.read_text())
    if process.returncode == 0: raise AssertionError("typed observation unexpectedly accepted")
    return None

def effect_lifecycle(work):
    runner=ActualRunner();identity="a"*64
    state=typed.transition(runner=runner,source_root=ROOT,work=work,name="run-acquire",
        observation={"kind":"acquire","identity":identity,"resource":"transport-intent","cost":0},budget=2700)
    state=typed.transition(runner=runner,source_root=ROOT,work=work,name="run-validate",
        observation={"kind":"validate","identity":identity,"cost":0},state=state)
    state=typed.transition(runner=runner,source_root=ROOT,work=work,name="run-admit",
        observation={"kind":"admit","identity":identity,"cost":0},state=state)
    state=typed.transition(runner=runner,source_root=ROOT,work=work,name="run-effect",
        observation={"kind":"begin-effect","resource":"remote-operation","acknowledged":False,"cost":0},state=state)
    # Run protocol fixtures start their explicit absolute monotonic trace at zero.
    state["budgetRemaining"]=2700
    return state

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

    def test_root_run_stale_attempt_then_exact_target_success_requires_full_settlement(self):
        with tempfile.TemporaryDirectory() as temporary:
            work=pathlib.Path(temporary);os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                lifecycle=effect_lifecycle(work);state=None
                events=[
                    {"kind":"record-rerun-intent","elapsedSeconds":1},
                    {"kind":"record-rerun-result","elapsedSeconds":2,"acknowledged":True},
                    {"kind":"observe-current","elapsedSeconds":3,"sequence":1,"httpStatus":200,"snapshot":run_snapshot(1,"queued",None)},
                    {"kind":"observe-current","elapsedSeconds":23,"sequence":2,"httpStatus":200,"snapshot":run_snapshot(2,"in_progress",None)},
                    {"kind":"observe-current","elapsedSeconds":43,"sequence":3,"httpStatus":200,"snapshot":run_snapshot(2,"completed","success")},
                    {"kind":"observe-target","elapsedSeconds":44,"sequence":4,"httpStatus":200,"snapshot":run_snapshot(2,"completed","success")},
                    {"kind":"observe-jobs","elapsedSeconds":45,"sequence":5,"httpStatus":200,"jobs":{"totalCount":2,"pageCount":1,"hasNextPage":False,"jobs":[
                        {"id":1,"name":"capacity","status":"completed","conclusion":"skipped"},
                        {"id":2,"name":"qualification","status":"completed","conclusion":"success"}]}},
                    {"kind":"observe-current","elapsedSeconds":46,"sequence":6,"httpStatus":200,"snapshot":run_snapshot(2,"completed","success")},
                    {"kind":"enter-cleanup","elapsedSeconds":47},
                    {"kind":"record-resources-retired","elapsedSeconds":48,"allClosed":True},
                    {"kind":"record-result-readback","elapsedSeconds":49,"sealedEvidence":True,"acknowledged":True}]
                actions=[]
                for index,event in enumerate(events):
                    decision=observe_run(work,index,lifecycle,state,event);actions.append(decision["action"])
                    lifecycle,state=decision["lifecycleState"],decision["runState"]
                self.assertEqual(["issue-rerun","observe-current","wait","wait","observe-target-and-jobs",
                                  "observe-target-and-jobs","observe-current","retire-resources",
                                  "retire-resources","continue-result-readback","complete"],actions)
                self.assertEqual("settled",state["settlement"]);self.assertTrue(state["resultAccepted"])
                self.assertIn("remote-operation",lifecycle["closed"]);self.assertEqual("success",lifecycle["outcome"])
                signal=observe_run(work,"late-signal",lifecycle,state,{"kind":"record-signal-cancellation","elapsedSeconds":49})
                self.assertFalse(signal["runState"]["resultAccepted"]);self.assertTrue(signal["runState"]["stickyCancelled"])
                self.assertEqual("finish-refused",signal["action"])
                replay=observe_run(work,"late-signal-replay",signal["lifecycleState"],signal["runState"],
                                   {"kind":"record-signal-cancellation","elapsedSeconds":49})
                self.assertEqual("finish-refused",replay["action"]);self.assertFalse(replay["runState"]["resultAccepted"])
            finally:os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_root_run_poll_clock_identity_attempt_and_partial_jobs_refuse(self):
        cases=[]
        with tempfile.TemporaryDirectory() as temporary:
            work=pathlib.Path(temporary);os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                lifecycle=effect_lifecycle(work)
                first=observe_run(work,0,lifecycle,None,{"kind":"record-rerun-intent","elapsedSeconds":1})
                second=observe_run(work,1,first["lifecycleState"],first["runState"],{"kind":"record-rerun-result","elapsedSeconds":2,"acknowledged":True})
                stale=observe_run(work,2,second["lifecycleState"],second["runState"],{"kind":"observe-current","elapsedSeconds":3,"sequence":1,"httpStatus":200,"snapshot":run_snapshot(1,"completed","success")})
                observe_run(work,"early",stale["lifecycleState"],stale["runState"],{"kind":"observe-current","elapsedSeconds":22,"sequence":2,"httpStatus":200,"snapshot":run_snapshot(2,"in_progress",None)},accepted=False)
                for label,snapshot in (("attempt3",run_snapshot(3,"completed","success")),
                                       ("actor",run_snapshot(2,"in_progress",None,originalActorId=99)),
                                       ("source",run_snapshot(2,"in_progress",None,headSha="c"*40)),
                                       ("ref",run_snapshot(2,"in_progress",None,headBranch="other")),
                                       ("workflow",run_snapshot(2,"in_progress",None,workflowPath="other.yml")),
                                       ("completed-null",run_snapshot(2,"completed",None)),
                                       ("active-success",run_snapshot(2,"in_progress","success"))):
                    decision=observe_run(work,label,stale["lifecycleState"],stale["runState"],{"kind":"observe-current","elapsedSeconds":23,"sequence":2,"httpStatus":200,"snapshot":snapshot})
                    self.assertEqual("refuse",decision["action"]);self.assertFalse(decision["runState"]["resourcesRetired"])
            finally:os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_cancel_refusal_never_settles_but_later_exact_failure_census_can_close(self):
        with tempfile.TemporaryDirectory() as temporary:
            work=pathlib.Path(temporary);os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                lifecycle=effect_lifecycle(work);state=None
                events=[
                    {"kind":"record-rerun-intent","elapsedSeconds":1},
                    {"kind":"record-rerun-result","elapsedSeconds":2,"acknowledged":True},
                    {"kind":"observe-current","elapsedSeconds":3,"sequence":1,"httpStatus":200,"snapshot":run_snapshot(2,"in_progress",None)},
                    {"kind":"enter-cleanup","elapsedSeconds":23},
                    {"kind":"observe-current","elapsedSeconds":24,"sequence":2,"httpStatus":200,"snapshot":run_snapshot(2,"in_progress",None)},
                    {"kind":"record-cancel-result","elapsedSeconds":25,"accepted":False},
                    {"kind":"record-resources-retired","elapsedSeconds":26,"allClosed":True},
                    {"kind":"observe-current","elapsedSeconds":46,"sequence":3,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")},
                    {"kind":"observe-target","elapsedSeconds":47,"sequence":4,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")},
                    {"kind":"observe-jobs","elapsedSeconds":48,"sequence":5,"httpStatus":200,"jobs":{"totalCount":2,"pageCount":1,"hasNextPage":False,"jobs":[
                        {"id":1,"name":"capacity","status":"completed","conclusion":"skipped"},
                        {"id":2,"name":"qualification","status":"completed","conclusion":"failure"}]}},
                    {"kind":"observe-current","elapsedSeconds":49,"sequence":6,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")}]
                actions=[]
                for index,event in enumerate(events):
                    decision=observe_run(work,index,lifecycle,state,event);actions.append(decision["action"])
                    lifecycle,state=decision["lifecycleState"],decision["runState"]
                self.assertEqual("observe-current",actions[3]);self.assertEqual("cancel-once",actions[4]);self.assertEqual("retire-resources",actions[5])
                self.assertEqual("finish-refused",actions[-1]);self.assertEqual("settled",state["settlement"])
                self.assertEqual("refused",state["cancel"]);self.assertIn("remote-operation",lifecycle["closed"])
                self.assertNotEqual("success",lifecycle["outcome"])
            finally:os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_cleanup_after_exact_terminal_candidate_does_not_issue_cancel(self):
        with tempfile.TemporaryDirectory() as temporary:
            work=pathlib.Path(temporary);os.environ["FSGG_FOURD_TYPED_POLICY"]=str(EXE)
            try:
                lifecycle=effect_lifecycle(work)
                one=observe_run(work,0,lifecycle,None,{"kind":"record-rerun-intent","elapsedSeconds":1})
                two=observe_run(work,1,one["lifecycleState"],one["runState"],{"kind":"record-rerun-result","elapsedSeconds":2,"acknowledged":True})
                terminal=observe_run(work,2,two["lifecycleState"],two["runState"],{"kind":"observe-current","elapsedSeconds":3,"sequence":1,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")})
                cleanup=observe_run(work,3,terminal["lifecycleState"],terminal["runState"],{"kind":"enter-cleanup","elapsedSeconds":4})
                self.assertEqual("retire-resources",cleanup["action"]);self.assertEqual("none",cleanup["runState"]["cancel"])
            finally:os.environ.pop("FSGG_FOURD_TYPED_POLICY",None)

    def test_serialized_impossible_state_duplicate_body_and_next_link_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            work=pathlib.Path(temporary);lifecycle=effect_lifecycle(work)
            one=observe_run(work,0,lifecycle,None,{"kind":"record-rerun-intent","elapsedSeconds":1})
            two=observe_run(work,1,one["lifecycleState"],one["runState"],{"kind":"record-rerun-result","elapsedSeconds":2,"acknowledged":True})
            forged=dict(two["runState"],rerun="not-issued",seenTarget2=True,latestSequence=0,
                        targetConclusion="success",settlement="settled",resourcesRetired=True)
            observe_run(work,"forged",two["lifecycleState"],forged,
                        {"kind":"record-result-readback","elapsedSeconds":3,"sealedEvidence":True,"acknowledged":True},accepted=False)
            duplicate=b'{"id":12345,"id":12345}'
            response={"httpStatus":200,"bodyBase64":base64.b64encode(duplicate).decode(),"linkHeaders":[],
                      "endpointRunId":12345,"endpointAttempt":0}
            observe_run(work,"duplicate",two["lifecycleState"],two["runState"],
                        {"kind":"observe-current","elapsedSeconds":3,"sequence":1,"response":response},accepted=False)
            terminal=observe_run(work,2,two["lifecycleState"],two["runState"],
                {"kind":"observe-current","elapsedSeconds":3,"sequence":1,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")})
            target=observe_run(work,3,terminal["lifecycleState"],terminal["runState"],
                {"kind":"observe-target","elapsedSeconds":4,"sequence":2,"httpStatus":200,"snapshot":run_snapshot(2,"completed","failure")})
            raw={"total_count":2,"jobs":[{"id":1,"run_id":12345,"run_attempt":2,"head_sha":"b"*40,"name":"capacity","status":"completed","conclusion":"skipped"},{"id":2,"run_id":12345,"run_attempt":2,"head_sha":"b"*40,"name":"qualification","status":"completed","conclusion":"failure"}]}
            body=json.dumps(raw,separators=(",",":")).encode();response={"httpStatus":200,"bodyBase64":base64.b64encode(body).decode(),
                "linkHeaders":["<https://api.github.com/x?page=2>; rel=\"next\""],"endpointRunId":12345,"endpointAttempt":2}
            decision=observe_run(work,4,target["lifecycleState"],target["runState"],{"kind":"observe-jobs","elapsedSeconds":5,"sequence":3,"response":response})
            self.assertEqual("run-jobs-census-refused",decision["runState"]["refusal"])
            response=dict(response,endpointRunId=99999,linkHeaders=[])
            observe_run(work,"wrong-endpoint",target["lifecycleState"],target["runState"],
                        {"kind":"observe-jobs","elapsedSeconds":5,"sequence":3,"response":response},accepted=False)

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
                         "placementSha":"a"*40,"placementTree":tree,"runId":"12345",
                         "producerSha256":"c"*64,"packetManifestSha256":"d"*64}
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
