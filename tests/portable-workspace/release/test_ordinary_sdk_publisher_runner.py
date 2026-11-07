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
spec=importlib.util.spec_from_file_location('sdk_runner_publisher',ROOT/'eng/ordinary_sdk_publisher.py')
P=importlib.util.module_from_spec(spec);spec.loader.exec_module(P)
ORIGINAL_RUN=42399710679
ORIGINAL_PUBLISHER='15c3dc9107cd0dabcc45a18896573cb2eeab9d71'
ORIGINAL_CONTENT='sha256:7bdc436f7bf3691567980e8d51c4a1239564248bbb26355d4a6d0fee01108978'


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
    def test_forward_state_replaces_only_the_unforwarded_committed_control(self):
        command,replaced=R.suite_command({'forward':None})
        self.assertEqual(replaced,[]);self.assertEqual(command[-1],str(ROOT/R.SUITE))
        command,replaced=R.suite_command({'forward':{'originalRun':1}})
        self.assertEqual(replaced,['Controls.test_committed_enabled_admission_binds_exactly_the_accepted_candidate_and_live_policy'])
        self.assertEqual(command[-2:],[str(ROOT/R.SUITE),*replaced])
        source=(ROOT/R.SUITE).read_text()
        body=source.split('def '+replaced[0].split('.')[1],1)[1].split('\n    def ',1)[0]
        self.assertIn("self.assertIsNone(committed['forward'])",body)
        import subprocess,sys
        helper=os.environ.get('FSGG_SHARED_HELPER_ROOT')
        self.assertTrue(helper and Path(helper,'scripts/release_successor_execution.py').is_file(),'runner supplies the verified helper root')
        with tempfile.TemporaryDirectory() as temporary:
            # With the real helper the suite imports, so the bootstrap itself must refuse an unknown name.
            env={'PATH':os.environ.get('PATH','/usr/bin:/bin'),'FSGG_SHARED_HELPER_ROOT':helper,'HOME':temporary,'TMPDIR':temporary}
            result=subprocess.run([sys.executable,'-I','-B','-c',R.SUITE_BOOT,str(ROOT/R.SUITE),'Controls.test_absent'],cwd=temporary,env=env,capture_output=True,timeout=60)
            self.assertNotEqual(result.returncode,0);self.assertIn(b'replaced control absent',result.stderr)
    def test_runner_requires_the_exact_declared_population(self):
        runner=(ROOT/'eng/run-ordinary-sdk-publisher-controls.py').read_text()
        self.assertIn("int(counts[0])==declared_tests(SUITE)-len(replaced)",runner)
        self.assertIn("int(runner_counts[0])==declared_tests('tests/portable-workspace/release/test_ordinary_sdk_publisher_runner.py')",runner)
        self.assertNotIn('>=32',runner);self.assertNotIn('>=7',runner)
        self.assertGreaterEqual(R.declared_tests(R.SUITE),42)
    def test_committed_forward_admission_is_the_exact_original_continuation(self):
        import json
        a=P.strict_json((ROOT/P.ADMISSION).read_bytes())
        if a['forward'] is None:
            self.assertTrue(a['enabled'] is False or a['selection'] is not None);return
        f=P.forward_header(a);s=P.selection(a)
        self.assertEqual({k:f[k] for k in ('originalRun','originalPublisher','contentId')},dict(originalRun=ORIGINAL_RUN,originalPublisher=ORIGINAL_PUBLISHER,contentId=ORIGINAL_CONTENT))
        original=P.runtime_selection(s,{'sha':ORIGINAL_PUBLISHER,'runId':ORIGINAL_RUN})
        self.assertEqual(P.content_id(original),ORIGINAL_CONTENT)
        suffix='/run-'+str(ORIGINAL_RUN)+'/publisher-'+ORIGINAL_PUBLISHER
        self.assertEqual(f['checkpoint'],{'generation':5,'effects':{'github'+suffix:'verified','nuget'+suffix:'verified'}})
        desired=json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_bytes());desired['id']=21633398
        self.assertEqual(f['tagEvidence']['evidenceSha256'],P.sha(P.canonical(desired)))
        self.assertEqual(s['publisherFiles'],{n:P.sha((ROOT/n).read_bytes()) for n in P.B_PATHS-{P.ADMISSION}})
        self.assertTrue(all(len(v['reference'])<=200 for v in (f['tagEvidence'],f['authorization'])))
    def test_hosted_override_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary,patch.dict(os.environ,{'GITHUB_ACTIONS':'true'}):
            with self.assertRaises(R.F.Refused):R.run(Path(temporary)/'output',input_root=Path(temporary))


if __name__=='__main__':unittest.main(verbosity=2)
