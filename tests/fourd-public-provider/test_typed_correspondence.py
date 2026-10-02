#!/usr/bin/env python3
import hashlib, json, os, pathlib, subprocess, tempfile, unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
PROJECT_EXE = ROOT / "eng/fourd-public-provider/typed/publish/FourD.Typed"
PROJECT = ROOT / "tests/fourd-public-provider/typed/FourD.Typed.Tests.fsproj"
MODEL_SHA256 = "34281b39c0cb75e0aef820a2faefce43c403d2f0c4d64994f293afe9ab7855ab"
ROOT_PACKET_ADAPTER_SHA256 = "44a0821440e95fe550b13e1dbf0e52713e50a00f9520b128162d333f8e46958f"
ROOT_PACKET_OPERATION_SHA256 = "87b6f94397d06d0a1370afed28596719d7850906b398c7f4a8e4629b62e9c00c"

def named_digest(entries):
    digest=hashlib.sha256()
    for name,value in entries:
        digest.update(name.encode()+b"\0"+value+b"\0")
    return digest.hexdigest()

class CorrespondenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["dotnet", "publish",
                        str(ROOT / "eng/fourd-public-provider/typed/FourD.Typed.fsproj"),
                        "-c", "Release", "-o", str(PROJECT_EXE.parent)], check=True, cwd=ROOT)
        subprocess.run(["dotnet", "restore", str(PROJECT), "--locked-mode"], check=True, cwd=ROOT)

    def test_fsharp_literate_source_generates_only_ephemeral_deterministic_quint(self):
        tracked = subprocess.run(["git", "ls-files", "*.qnt"], cwd=ROOT, check=True,
                                 capture_output=True, text=True).stdout.splitlines()
        self.assertEqual([], tracked)
        with tempfile.TemporaryDirectory() as temporary:
            first = pathlib.Path(temporary) / "first.qnt"
            second = pathlib.Path(temporary) / "second.qnt"
            subprocess.run([str(PROJECT_EXE), "generate-quint", str(first)], check=True, cwd=ROOT)
            subprocess.run([str(PROJECT_EXE), "generate-quint", str(second)], check=True, cwd=ROOT)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            before = first.read_bytes()
            self.assertEqual(MODEL_SHA256, hashlib.sha256(before).hexdigest())
            refused = subprocess.run([str(PROJECT_EXE), "generate-quint", str(first)], cwd=ROOT, check=False)
            self.assertEqual(2, refused.returncode)
            self.assertEqual(before, first.read_bytes())
            self.assertIn(b"module FourDOperationCorrespondence", before)

    def test_actual_quint_itf_is_independent_expected_trace_for_fsharp_reducer(self):
        with tempfile.TemporaryDirectory() as temporary:
            model = pathlib.Path(temporary) / "FourDOperation.qnt"
            subprocess.run([str(PROJECT_EXE), "generate-quint", str(model)], check=True, cwd=ROOT)
            subprocess.run(["quint","typecheck",str(model)],check=True,cwd=ROOT)
            digest=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
            tool_version=subprocess.run(["quint","--version"],check=True,capture_output=True).stdout
            source=lambda path:(path.as_posix(),path.read_bytes())
            fixed={"FSGG_FOURD_QUINT_TOOL_SHA256":hashlib.sha256(tool_version).hexdigest(),
                   "FSGG_FOURD_QUINT_CONTRACT_SHA256":named_digest([source(ROOT/"eng/fourd-public-provider/typed/Join.fs"),source(ROOT/"eng/fourd-public-provider/typed/RunObservation.fs")]),
                   "FSGG_FOURD_QUINT_ADAPTER_SHA256":named_digest([source(ROOT/"eng/fourd-public-provider/typed_policy.py"),("root-packet/mechanism_adapter.py",ROOT_PACKET_ADAPTER_SHA256.encode()),("root-packet/root_operation.py",ROOT_PACKET_OPERATION_SHA256.encode())]),
                   "FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256":named_digest([source(ROOT/"eng/fourd-public-provider/typed/Policy.fs"),source(ROOT/"eng/fourd-public-provider/typed/RunObservation.fs"),source(ROOT/"eng/fourd-public-provider/typed/Program.fs")])}
            scenarios=(("success","FourDOperationCorrespondence",9),("stale","FourDOperationStaleCorrespondence",4),
                       ("cancelled","FourDOperationCancelledCorrespondence",4),("unknown","FourDOperationUnknownCorrespondence",8),
                       ("cleanup-failure","FourDOperationCleanupFailureCorrespondence",7),
                       ("run-success","FourDRunSuccessCorrespondence",11),
                       ("run-cancel-race","FourDRunCancelRaceCorrespondence",11),
                       ("run-deadline","FourDRunDeadlineCorrespondence",5),
                       ("run-cancel-accepted","FourDRunCancelAcceptedCorrespondence",11),
                       ("run-wrong-identity","FourDRunWrongIdentityCorrespondence",3),
                       ("run-partial-jobs","FourDRunPartialJobsCorrespondence",10),
                       ("run-signal-cancel","FourDRunSignalCancelCorrespondence",12),
                       ("run-retired-active-signal","FourDRunRetiredActiveSignalCorrespondence",13),
                       ("run-late-signal","FourDRunLateSignalCorrespondence",12))
            for scenario,module,steps in scenarios:
                with self.subTest(scenario=scenario):
                    selected=pathlib.Path(temporary)/f"{scenario}-{{seq}}.itf.json"
                    subprocess.run(["quint","run",str(model),"--main",module,"--max-samples","1","--max-steps",str(steps),
                        "--seed","20261001","--out-itf",str(selected),"--n-traces","1","--verbosity","0"],check=True,cwd=ROOT)
                    trace=pathlib.Path(str(selected).replace("{seq}","0"));env=dict(os.environ)
                    env["FSGG_FOURD_QUINT_ITF"]=str(trace);env["FSGG_FOURD_QUINT_SCENARIO"]=scenario
                    env.update(fixed);env["FSGG_FOURD_QUINT_PROFILE_SHA256"]=hashlib.sha256(
                        json.dumps({"modelSha256":digest(model),"module":module,"scenario":scenario,
                                    "seed":"20261001","steps":steps},sort_keys=True,separators=(",",":")).encode()).hexdigest()
                    result=subprocess.run(["dotnet","run","--project",str(PROJECT),"--no-restore"],cwd=ROOT,env=env,
                        capture_output=True,text=True,timeout=60,check=False)
                    self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    self.assertIn("FsQuint correspondence: PASS",result.stdout)

    def test_root_run_witnesses_are_reachable_under_pinned_bounds(self):
        with tempfile.TemporaryDirectory() as temporary:
            model=pathlib.Path(temporary)/"FourDOperation.qnt"
            subprocess.run([str(PROJECT_EXE),"generate-quint",str(model)],check=True,cwd=ROOT)
            modules=(("FourDRunSuccessCorrespondence",11),("FourDRunCancelRaceCorrespondence",11),
                     ("FourDRunDeadlineCorrespondence",5),("FourDRunCancelAcceptedCorrespondence",11),
                     ("FourDRunWrongIdentityCorrespondence",3),("FourDRunPartialJobsCorrespondence",10))
            modules += (("FourDRunSignalCancelCorrespondence",12),("FourDRunRetiredActiveSignalCorrespondence",13),
                        ("FourDRunLateSignalCorrespondence",12))
            for module,steps in modules:
                with self.subTest(module=module):
                    result=subprocess.run(["quint","run",str(model),"--main",module,"--max-samples","100",
                        "--max-steps",str(steps),"--seed","20261001","--invariant","invariant",
                        "--witnesses","witness","--verbosity","1"],cwd=ROOT,capture_output=True,text=True,
                        timeout=120,check=False)
                    self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    self.assertRegex(result.stdout,r"witness was witnessed in (?:[1-9][0-9]*) trace\(s\)")
            general=subprocess.run(["quint","run",str(model),"--main","FourDRunObservation","--max-samples","5000",
                "--max-steps","32","--seed","20261002","--invariant","invariant",
                "--witnesses","witnessPostCleanupTerminal","--verbosity","1"],cwd=ROOT,capture_output=True,text=True,
                timeout=120,check=False)
            self.assertEqual(0,general.returncode,general.stdout+general.stderr)
            self.assertRegex(general.stdout,r"witnessPostCleanupTerminal was witnessed in (?:[1-9][0-9]*) trace\(s\)")

if __name__ == "__main__": unittest.main()
