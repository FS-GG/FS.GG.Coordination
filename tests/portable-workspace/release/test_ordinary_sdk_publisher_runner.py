#!/usr/bin/env python3
"""Provisioning and maintained-runner controls; no network, credential or native effects."""
import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[3]
spec=importlib.util.spec_from_file_location('sdk_source_runner',ROOT/'eng/run-ordinary-sdk-publisher-controls.py')
R=importlib.util.module_from_spec(spec);spec.loader.exec_module(R)


class RunnerControls(unittest.TestCase):
    def test_maintained_gate_invokes_runner_and_retains_old_suites(self):
        gate=(ROOT/'eng/bootstrap-gates/compiler-and-tests.sh').read_text()
        ordered=['python3 tests/portable-workspace/release/test_execution_pair_release.py',
                 'python3 tests/portable-workspace/release/test_execution_pair_forward.py',
                 'python3 -I -B eng/run-ordinary-sdk-publisher-controls.py --output "$RUNNER_TEMP/compiler-and-tests/ordinary-sdk-publisher"',
                 'dotnet build FS.GG.Coordination.sln']
        positions=[gate.index(command) for command in ordered]
        self.assertEqual(positions,sorted(positions))
        runner=(ROOT/'eng/run-ordinary-sdk-publisher-controls.py').read_text()
        self.assertIn("'tests/portable-workspace/release/test_ordinary_sdk_publisher.py'",runner)
        self.assertIn("'tests/portable-workspace/release/test_ordinary_sdk_publisher_runner.py'",runner)
        import json,yaml
        plan=json.loads((ROOT/'eng/bootstrap-qualification-plan.json').read_text())
        owner=next(row for row in plan['jobs'] if row['id']=='compiler-and-tests')
        self.assertEqual(owner['entryPoint'],'bash eng/bootstrap-gates/compiler-and-tests.sh')
        workflow=yaml.safe_load((ROOT/'.github/workflows/bootstrap-qualification.yml').read_text())
        self.assertIn(owner['entryPoint'],[row.get('run') for row in workflow['jobs']['compiler-and-tests']['steps']])
    def test_inputs_are_actual_seven_engine_pins_and_canonical_lock(self):
        rows=R.required_inputs();self.assertEqual(len(rows),8)
        lock=next(row for row in rows if row['path']=='scripts/publication_writer_lock.py')
        self.assertEqual(lock['commit'],'c799a9f9b21d52eb9766cf0950722e3e55145d39')
        self.assertEqual(lock['sha256'],'dca3fe60e5ebf4a6ff33e52b1ede6ee255b377790688e0958cb4ebcc90df53e6')
    def test_missing_wrong_and_oversize_helper_refuse_before_tests(self):
        row=dict(path='scripts/fixture.py',repository='FS-GG/.github',commit='a'*40,sha256=hashlib.sha256(b'expected').hexdigest())
        for mode in ('missing','wrong','oversize'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);source=root/'source';source.mkdir()
                if mode!='missing':
                    (source/'scripts').mkdir();(source/row['path']).write_bytes(b'wrong' if mode=='wrong' else b'x'*1048577)
                reports,errors=R.provision(root/'verified',[row],input_root=source)
                self.assertEqual(reports,[]);self.assertEqual(len(errors),1);self.assertFalse((root/'verified'/row['path']).exists())
    def test_valid_bytes_are_copied_into_fresh_private_tree(self):
        row=dict(path='scripts/fixture.py',repository='FS-GG/.github',commit='a'*40,sha256=hashlib.sha256(b'expected').hexdigest())
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);source=root/'source';(source/'scripts').mkdir(parents=True);(source/row['path']).write_bytes(b'expected')
            reports,errors=R.provision(root/'verified',[row],input_root=source)
            self.assertEqual(len(reports),1);self.assertEqual(errors,[]);self.assertEqual((root/'verified'/row['path']).read_bytes(),b'expected')
            with self.assertRaises(FileExistsError):R.provision(root/'verified',[row],input_root=source)
    def test_all_preparation_errors_retained_and_child_not_launched(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            # Actual pinned inputs are absent; neither runner controls nor SDK child may start.
            with patch.object(R.subprocess,'run',side_effect=AssertionError('test child must not start')):
                code=R.run(root/'result',input_root=root/'absent')
            self.assertEqual(code,3)
            import json
            result=json.loads((root/'result/result.json').read_text())
            self.assertFalse(result['testsLaunched']);self.assertEqual(result['status'],'refused');self.assertEqual(result['inputs'],[]);self.assertEqual(len(result['preparationErrors']),8)
    def test_test_environment_has_no_inherited_credentials(self):
        with tempfile.TemporaryDirectory() as temporary,patch.dict(os.environ,{'GH_TOKEN':'not-a-token','ACTIONS_RUNTIME_TOKEN':'not-a-token','ROOT_PRIVATE_KEY':'not-a-key','GITHUB_ACTIONS':'true'}):
            env=R.test_environment(Path(temporary),Path(temporary)/'helpers')
            self.assertEqual(set(env),{'PATH','HOME','TMPDIR','PYTHONDONTWRITEBYTECODE','FSGG_SHARED_HELPER_ROOT'})
    def test_hosted_override_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary,patch.dict(os.environ,{'GITHUB_ACTIONS':'true'}):
            with self.assertRaises(R.F.Refused):R.run(Path(temporary)/'output',input_root=Path(temporary))


if __name__=='__main__':unittest.main(verbosity=2)
