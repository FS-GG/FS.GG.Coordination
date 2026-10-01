#!/usr/bin/env python3
import os, pathlib, subprocess, tempfile, unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODEL = ROOT / "eng/fourd-public-provider/typed/FourDOperation.qnt"
PROJECT = ROOT / "tests/fourd-public-provider/typed/FourD.Typed.Tests.fsproj"

class CorrespondenceTests(unittest.TestCase):
    def test_actual_quint_itf_is_independent_expected_trace_for_fsharp_reducer(self):
        with tempfile.TemporaryDirectory() as temporary:
            pattern = pathlib.Path(temporary) / "trace-{seq}.itf.json"
            subprocess.run(["quint","typecheck",str(MODEL)],check=True,cwd=ROOT)
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
                    result=subprocess.run(["dotnet","run","--project",str(PROJECT),"--no-restore"],cwd=ROOT,env=env,
                        capture_output=True,text=True,timeout=60,check=False)
                    self.assertEqual(0,result.returncode,result.stdout+result.stderr)
                    self.assertIn("FsQuint correspondence: PASS",result.stdout)

if __name__ == "__main__": unittest.main()
