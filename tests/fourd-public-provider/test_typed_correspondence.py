#!/usr/bin/env python3
import hashlib, json, os, pathlib, subprocess, tempfile, unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODEL = ROOT / "eng/fourd-public-provider/typed/FourDOperation.qnt"
PROJECT = ROOT / "tests/fourd-public-provider/typed/FourD.Typed.Tests.fsproj"

class CorrespondenceTests(unittest.TestCase):
    def test_actual_quint_itf_is_independent_expected_trace_for_fsharp_reducer(self):
        with tempfile.TemporaryDirectory() as temporary:
            pattern = pathlib.Path(temporary) / "trace-{seq}.itf.json"
            subprocess.run(["quint","typecheck",str(MODEL)],check=True,cwd=ROOT)
            digest=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
            tool_version=subprocess.run(["quint","--version"],check=True,capture_output=True).stdout
            fixed={"FSGG_FOURD_QUINT_TOOL_SHA256":hashlib.sha256(tool_version).hexdigest(),
                   "FSGG_FOURD_QUINT_CONTRACT_SHA256":digest(ROOT/"eng/fourd-public-provider/typed/Join.fs"),
                   "FSGG_FOURD_QUINT_ADAPTER_SHA256":digest(ROOT/"eng/fourd-public-provider/typed_policy.py"),
                   "FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256":digest(ROOT/"eng/fourd-public-provider/typed/Policy.fs")}
            scenarios=(("success","FourDOperationCorrespondence",9),("stale","FourDOperationStaleCorrespondence",4),
                       ("cancelled","FourDOperationCancelledCorrespondence",4),("unknown","FourDOperationUnknownCorrespondence",8),
                       ("cleanup-failure","FourDOperationCleanupFailureCorrespondence",7))
            for scenario,module,steps in scenarios:
                with self.subTest(scenario=scenario):
                    selected=pathlib.Path(temporary)/f"{scenario}-{{seq}}.itf.json"
                    subprocess.run(["quint","run",str(MODEL),"--main",module,"--max-samples","1","--max-steps",str(steps),
                        "--seed","20261001","--out-itf",str(selected),"--n-traces","1","--verbosity","0"],check=True,cwd=ROOT)
                    trace=pathlib.Path(str(selected).replace("{seq}","0"));env=dict(os.environ)
                    env["FSGG_FOURD_QUINT_ITF"]=str(trace);env["FSGG_FOURD_QUINT_SCENARIO"]=scenario
                    env.update(fixed);env["FSGG_FOURD_QUINT_PROFILE_SHA256"]=hashlib.sha256(
                        json.dumps({"modelSha256":digest(MODEL),"module":module,"scenario":scenario,
                                    "seed":"20261001","steps":steps},sort_keys=True,separators=(",",":")).encode()).hexdigest()
                    result=subprocess.run(["dotnet","run","--project",str(PROJECT),"--no-restore"],cwd=ROOT,env=env,
                        capture_output=True,text=True,timeout=60,check=False)
                    self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    self.assertIn("FsQuint correspondence: PASS",result.stdout)

if __name__ == "__main__": unittest.main()
