"""Injected source controls only: no Quint, Java, CLR or model execution."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import unittest
import copy

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('choreo_diagnostics', ROOT / 'eng/retain-choreo-c2-diagnostics.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiagnosticControls(unittest.TestCase):
    def test_bootstrap_base_retains_command_cause_before_fixture_scratch_cleanup(self):
        for inherited in (False, True):
            with self.subTest(inherited=inherited), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                gates = root / 'eng/bootstrap-gates'
                gates.mkdir(parents=True)
                shutil.copyfile(ROOT / 'eng/bootstrap-gates/canonical-quint-shard.sh', gates / 'canonical-quint-shard.sh')
                (gates / 'canonical-quint.sh').write_text(
                    '#!/usr/bin/env bash\nset -eu\nmkdir -p "$FSGG_QUINT_SHARD_ROOT"\nprintf "{}\\n" > "$FSGG_QUINT_RECEIPT"\n')
                (root / 'eng/verify-choreo-c2-bounded.sh').write_text('''#!/usr/bin/env bash
set -eu
python3 - <<'PYFIXTURE'
import importlib.util, json, os, pathlib, shutil
source = pathlib.Path(os.environ['FIXTURE_COLLECTOR'])
spec = importlib.util.spec_from_file_location('fixture_collector', source)
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)
scratch = pathlib.Path.cwd() / 'fixture-scratch'
scratch.mkdir()
report = {'schema': collector.RESULT_SCHEMA, 'commandExit': 0, 'commandAccepted': False,
          'firstCause': {'type': 'ValueError', 'message': 'original post-TLC fixture cause'},
          'additionalFailures': [], 'ownerReceipt': {'cleanup': 'observed-empty-group'},
          'input': {'private': 'NEVER COPY'}}
(scratch / 'runner.log.attempt-1.command.json').write_text(json.dumps(report))
collector.capture(scratch, pathlib.Path(os.environ['FSGG_CHOREO_DIAGNOSTIC_ROOT']), 37)
shutil.rmtree(scratch)
PYFIXTURE
exit 37
''')
                archive = root / 'toolchain.fixture'
                archive.write_text('fixture-only; never executed or extracted')
                shard_root = root / 'receipts'
                destination = root / 'inherited-diagnostics' if inherited else shard_root / 'base-choreo-failure-diagnostics'
                env = dict(os.environ, FSGG_QUINT_SHARD='base', FSGG_QUINT_TOOLCHAIN_ARCHIVE=str(archive),
                           FSGG_QUINT_SHARD_ROOT=str(shard_root), FIXTURE_COLLECTOR=str(ROOT / 'eng/retain-choreo-c2-diagnostics.py'))
                env.pop('FSGG_CHOREO_DIAGNOSTIC_ROOT', None)
                if inherited:
                    env['FSGG_CHOREO_DIAGNOSTIC_ROOT'] = str(destination)
                result = subprocess.run(['bash', str(gates / 'canonical-quint-shard.sh')], cwd=root,
                                        env=env, capture_output=True, timeout=10)
                self.assertEqual(37, result.returncode, result.stderr)
                self.assertFalse((root / 'fixture-scratch').exists())
                artifact = json.loads((destination / 'failure.json').read_text())
                self.assertEqual(37, artifact['originalExitCode'])
                self.assertIn('original post-TLC fixture cause', json.dumps(artifact))
                self.assertNotIn('NEVER COPY', json.dumps(artifact))

    def test_compiler_detail_survives_filtered_bounded_copy(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            server = scratch / '_apalache-out/server/attempt'; server.mkdir(parents=True)
            (server / 'detailed.log').write_text('compiler error: missing operator\nAuthorization: Bearer sentinel\npassword=sentinel\nhttps://host/path?key=sentinel\n' + str(scratch))
            (server / 'key.pem').write_text('NEVER COPY')
            value = module.capture(scratch, root / 'diagnostics', 37)
            text = json.dumps(value)
            self.assertIn('missing operator', text)
            self.assertNotIn('sentinel', text)
            self.assertNotIn('NEVER COPY', text)
            self.assertNotIn(str(scratch), text)
            self.assertEqual(37, value['originalExitCode'])
            self.assertEqual('compiler-server', value['logs'][0]['source'])
            self.assertEqual(0o600, (root / 'diagnostics/failure.json').stat().st_mode & 0o777)

    def test_typed_command_cause_survives_without_input_or_arbitrary_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            report = {'schema': module.RESULT_SCHEMA, 'commandExit': 37, 'commandAccepted': False,
                      'firstCause': {'type': 'RuntimeError', 'message': 'original failed command'},
                      'additionalFailures': [{'type': 'ValueError', 'message': 'secondary reporting'}],
                      'ownerReceipt': {'firstCause': 'original owner', 'cleanup': 'observed-empty-group'},
                      'input': {'private': 'NEVER COPY'}}
            (scratch / 'runner.log.attempt-1.command.json').write_text(json.dumps(report))
            (scratch / 'foreign.json').write_text('NEVER COPY')
            value = module.capture(scratch, root / 'diagnostics', 37)
            text = json.dumps(value)
            self.assertIn('original failed command', text)
            self.assertIn('secondary reporting', text)
            self.assertIn('observed-empty-group', text)
            self.assertNotIn('NEVER COPY', text)
            self.assertEqual('owned-command', value['logs'][0]['source'])

    def test_large_file_retains_head_and_tail_with_explicit_omission(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            (scratch / 'runner.log').write_bytes(b'HEAD\n' + b'x' * 200000 + b'\nTAIL-CAUSE')
            value = module.capture(scratch, root / 'diagnostics', 1)
            self.assertTrue(value['logs'][0]['truncated'])
            self.assertIn('HEAD', value['logs'][0]['text'])
            self.assertIn('TAIL-CAUSE', value['logs'][0]['text'])
            self.assertLess((root / 'diagnostics/failure.json').stat().st_size, module.MAX_OUTPUT)

    def test_symlinks_and_non_logs_are_never_read(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            private = root / 'private'; private.write_text('NOT READ')
            (scratch / 'foreign.log').symlink_to(private)
            (scratch / '_apalache-out').symlink_to(root, target_is_directory=True)
            self.assertEqual([], module.capture(scratch, root / 'diagnostics', 1)['logs'])

    def test_collection_population_is_bounded(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            for i in range(140): (scratch / f'{i}.log').write_text('detail')
            value = module.capture(scratch, root / 'diagnostics', 1)
            self.assertEqual(16, len(value['logs']))
            self.assertIn('collection-bounded', value['omittedCoverage'])

    def test_expired_collection_reports_unknown_coverage(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            ticks = iter([0, 3, 3])
            value = module.capture(scratch, root / 'diagnostics', 1, clock=lambda: next(ticks))
            self.assertEqual([], value['logs'])
            self.assertIn('collection-bounded', value['omittedCoverage'])

    def test_existing_destination_and_success_refuse(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir(); dest = root / 'diagnostics'; dest.mkdir()
            with self.assertRaises(FileExistsError): module.capture(scratch, dest, 1)
            with self.assertRaises(ValueError): module.capture(scratch, root / 'other', 0)

    def run_gate(self, stale=False, success=False):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); binary = root / 'bin'; binary.mkdir()
            for name in ['java', 'sha256sum']:
                p = binary / name; p.write_text('#!/bin/sh\nexit 0\n'); p.chmod(0o755)
            quint = binary / 'fake-quint'
            quint.write_text('''#!/bin/sh
if [ "$1" = verify ]; then
  mkdir -p _apalache-out/server/attempt
  echo 'compiler detail: failing stage' > _apalache-out/server/attempt/detailed.log
  echo 'Authorization: Bearer synthetic-secret' >> _apalache-out/server/attempt/detailed.log
  if [ "$FAKE_SUCCESS" = yes ]; then
    echo '[ok] No violation found'
    echo '715 states generated, 593 distinct states found, 0 states left on queue.'
  else
    echo 'error: error'
    exit 37
  fi
fi
exit 0
'''); quint.chmod(0o755)
            dest = root / 'diagnostics'
            if stale: dest.mkdir()
            quint_home = root / 'quint-home'
            jar = quint_home / 'apalache-dist-0.56.1/apalache/lib/apalache.jar'
            jar.parent.mkdir(parents=True); jar.write_text('fake JAR; never executed')
            env = dict(os.environ, PATH=str(binary) + ':' + os.environ['PATH'], RUNNER_TEMP=str(root), FSGG_QUINT_BIN=str(quint), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(dest), FAKE_SUCCESS='yes' if success else 'no', JAVA_HOME=str(root), QUINT_HOME=str(quint_home))
            env.pop('FSGG_QUINT_TOOLCHAIN_ARCHIVE', None)
            result = subprocess.run(['bash', str(ROOT / 'eng/verify-choreo-c2-bounded.sh')], env=env, capture_output=True, text=True, timeout=10)
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))
            value = json.loads((dest / 'failure.json').read_text()) if (dest / 'failure.json').exists() else None
            return result, value

    def test_existing_disk_inventory_bounds_generated_scope_without_following_links(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root / 'regular').write_text('generated')
            outside = root.parent / 'outside-does-not-exist'
            (root / 'external').symlink_to(outside)
            observed = module.census(root, 100, clock=lambda: 1)
            self.assertEqual(3, observed['objects'])
            self.assertEqual(1, observed['fixtureSymlinks'])
            self.assertFalse(observed['linkTraversal'])
            (root / 'sparse').touch()
            with (root / 'sparse').open('r+b') as file: file.truncate(1073741825)
            with self.assertRaisesRegex(RuntimeError, 'disk-byte-cap'):
                module.census(root, 100, clock=lambda: 1)
            (root / 'sparse').unlink()
            with self.assertRaisesRegex(RuntimeError, 'disk-byte-cap'):
                module.census(root, 100, limit=0, clock=lambda: 1)
            with self.assertRaisesRegex(RuntimeError, 'disk-budget'):
                module.census(root, 1, clock=lambda: 1)
            fifo = root / 'fifo'; os.mkfifo(fifo)
            with self.assertRaisesRegex(RuntimeError, 'disk-special-file'):
                module.census(root, 100, clock=lambda: 1)

    def test_actual_gate_preserves_exit_and_cleans_scratch(self):
        result, value = self.run_gate()
        self.assertEqual(37, result.returncode, result.stderr)
        self.assertEqual(37, value['originalExitCode'])
        self.assertIn('failing stage', json.dumps(value))
        self.assertNotIn('synthetic-secret', json.dumps(value))
        self.assertIn('retained', result.stderr)
        self.assertNotIn('APALACHE_STARTUP_RETRY', result.stderr)

    def test_reporting_failure_preserves_first_exit_and_cleanup(self):
        result, value = self.run_gate(stale=True)
        self.assertEqual(37, result.returncode, result.stderr)
        self.assertIsNone(value)
        self.assertIn('unavailable', result.stderr)

    def test_success_exit_does_not_collect_and_still_cleans(self):
        # Exercise the actual EXIT implementation without launching any gate.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); dest = root / 'diagnostics'
            script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
            script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
            result = subprocess.run(['bash', '-c', script + '\nexit 0\n'], env=dict(os.environ, RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(dest)), capture_output=True, timeout=5)
            self.assertEqual(0, result.returncode)
            self.assertFalse(dest.exists())
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))

    def test_broken_reporting_sink_cannot_replace_original_exit(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
            script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
            with open('/dev/full', 'wb') as sink:
                result = subprocess.run(['bash', '-c', script + '\nexit 37\n'], env=dict(os.environ, RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(root / 'diagnostics')), stdout=subprocess.DEVNULL, stderr=sink, timeout=5)
            self.assertEqual(37, result.returncode)
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))

    def test_cleanup_failure_fails_success_and_preserves_original_failure(self):
        for original, expected in [(0, 73), (37, 37)]:
            with self.subTest(original=original), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); binary = root / 'bin'; binary.mkdir()
                fake_rm = binary / 'rm'
                fake_rm.write_text('#!/bin/sh\nexit 73\n')
                fake_rm.chmod(0o755)
                script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
                script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
                env = dict(os.environ, PATH=str(binary) + ':' + os.environ['PATH'], RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(root / 'diagnostics'))
                result = subprocess.run(['bash', '-c', script + f'\nexit {original}\n'], env=env, capture_output=True, text=True, timeout=5)
                self.assertEqual(expected, result.returncode, result.stderr)
                self.assertIn('CHOREO_SCRATCH_CLEANUP failed', result.stderr)
                self.assertTrue(list(root.glob('fsgg-choreo-c2-bounded.*')))
                if original:
                    value = json.loads((root / 'diagnostics/failure.json').read_text())
                    self.assertEqual(original, value['originalExitCode'])
                else:
                    self.assertFalse((root / 'diagnostics').exists())

    def test_collector_is_in_formal_source_identity_and_only_existing_artifact(self):
        plan = json.loads((ROOT / 'eng/bootstrap-qualification-plan.json').read_text())
        self.assertIn('eng/retain-choreo-c2-diagnostics.py', plan['formalReuse']['exactPaths'])
        self.assertIn('eng/shared_process_owner.py', plan['formalReuse']['exactPaths'])
        self.assertIn('eng/run-choreo-c2-tlc.sh', plan['formalReuse']['exactPaths'])
        script = (ROOT / 'eng/optimistic-run-formal-fragment.sh').read_text()
        self.assertIn('FSGG_CHOREO_DIAGNOSTIC_ROOT="$fragment/choreo-failure-diagnostics"', script)




class ParityDiagnosticControls(unittest.TestCase):
    def module(self):
        s = importlib.util.spec_from_file_location('parity_diagnostic_control', ROOT / 'eng/verify-choreo-c5-parity.py')
        m = importlib.util.module_from_spec(s); s.loader.exec_module(m)
        return m

    def test_actual_c5_exception_retains_details_before_original_cleanup(self):
        from unittest.mock import patch
        import io
        m = self.module(); observed = []; original = RuntimeError('original compile failure')
        with tempfile.TemporaryDirectory() as tmp:
            destination = Path(tmp) / 'choreo-failure-diagnostics'
            def fail(directory):
                observed.append(directory)
                logs = directory / '_apalache-out/server/attempt'; logs.mkdir(parents=True)
                (logs / 'detailed.log').write_text('compiler detail: failed translation\npassword=synthetic-c5-secret')
                raise original
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(destination)), patch.object(m, 'export_legacy', side_effect=fail), patch.object(m.sys, 'stderr', io.StringIO()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)
            self.assertFalse(observed[0].exists())
            artifact = Path(str(destination) + '-parity') / 'failure.json'
            text = artifact.read_text()
            self.assertIn('failed translation', text)
            self.assertNotIn('synthetic-c5-secret', text)
            self.assertLessEqual(artifact.stat().st_size, 96 * 1024)

    def test_c5_reporting_failure_preserves_exact_original_exception_and_cleanup(self):
        from unittest.mock import patch
        import io
        m = self.module(); observed = []; original = RuntimeError('first cause')
        def fail(directory): observed.append(directory); raise original
        with tempfile.TemporaryDirectory() as tmp:
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(Path(tmp) / 'diagnostics')), patch.object(m, 'export_legacy', side_effect=fail), patch.object(m.runpy, 'run_path', side_effect=OSError('reporting unavailable')), patch.object(m.sys, 'stderr', io.StringIO()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)
            self.assertFalse(observed[0].exists())

    def test_c5_cleanup_failure_cannot_green_success_or_replace_first_cause(self):
        from unittest.mock import patch
        import io
        for fails in [False, True]:
            m = self.module(); original = RuntimeError('first cause')
            with tempfile.TemporaryDirectory() as tmp:
                class FakeTemporary:
                    name = tmp
                    def cleanup(self): raise OSError('cleanup failed')
                with patch.object(m.tempfile, 'TemporaryDirectory', return_value=FakeTemporary()), patch.object(m, 'export_legacy', side_effect=original if fails else None), patch.object(m, 'legacy', return_value=[]), patch.object(m, 'choreo', return_value=[]), patch.object(m, 'compare'), patch.object(m, 'relative_retry', return_value=[]), patch.object(m, 'retain_failure'), patch.object(m.sys, 'stderr', io.StringIO()):
                    with self.assertRaises(RuntimeError if fails else OSError) as actual: m.main()
                if fails: self.assertIs(original, actual.exception)

    def test_joint_output_cap_reports_omission_without_losing_original_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            for i in range(16): (scratch / f'{i}.log').write_text('\N{SNOWMAN}' * 8192)
            value = module.capture(scratch, root / 'diagnostics', 37, max_output=96 * 1024)
            self.assertIn('output-byte-bound', value['omittedCoverage'])
            self.assertEqual(37, value['originalExitCode'])
            self.assertLessEqual((root / 'diagnostics/failure.json').stat().st_size, 96 * 1024)
            # Both runtime callers select half the original diagnostic output cap.
            gate = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text()
            parity = (ROOT / 'eng/verify-choreo-c5-parity.py').read_text()
            self.assertIn('"$status" 98304; then', gate)
            self.assertIn('max_output=96 * 1024', parity)
            self.assertEqual(192 * 1024, 2 * 96 * 1024)

    def test_c5_interrupted_or_broken_reporting_preserves_original_exception(self):
        from unittest.mock import patch
        m = self.module(); original = RuntimeError('first cause')
        class BrokenSink:
            def write(self, value): raise OSError('broken reporting')
        with tempfile.TemporaryDirectory() as tmp:
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(Path(tmp) / 'diagnostics')), patch.object(m, 'export_legacy', side_effect=original), patch.object(m.runpy, 'run_path', side_effect=SystemExit(99)), patch.object(m.sys, 'stderr', BrokenSink()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)


class CommandInterfaceControls(unittest.TestCase):
    def test_actual_retirement_path_fresh_sole_server_and_live_unknown_foreign_refusals(self):
        from unittest.mock import Mock, patch
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        leader={'pid':101,'start':20,'pgid':101,'state':'S'}
        for kind in ('sole','empty','foreign','multiple','replaced','changed-group','unknown','live','nonzero','generation-cap'):
            with self.subTest(kind=kind):
                child=Mock(returncode=None if kind == 'live' else 37 if kind == 'nonzero' else 0)
                rows=[] if kind == 'empty' else [server,leader] if kind in ('foreign','multiple') else [server]
                owner=Mock(); owner.census.return_value=rows
                owner.proc.return_value=dict(server,start=22) if kind == 'replaced' else dict(server,pgid=999) if kind == 'changed-group' else server
                if kind == 'unknown': owner.proc.side_effect=PermissionError('unknown birth')
                servers={(102,21),(103,22)} if kind == 'multiple' else {(102,21)}
                stops=[]; known=set(); checkpoint=Mock(); observation={'members':None}
                with patch.object(module.signal,'pidfd_send_signal') as send:
                    args=(owner,child,leader,servers,{(102,21):77},stops,checkpoint,known,1 if kind == 'generation-cap' else 6,observation)
                    if kind in ('replaced','changed-group','unknown','live','generation-cap'):
                        with self.assertRaises((ValueError,PermissionError)): module.retire_original_server(*args)
                    else:
                        members=module.retire_original_server(*args)
                        self.assertEqual({(r['pid'],r['start']) for r in rows},members)
                    if kind == 'sole':
                        send.assert_called_once_with(77,module.signal.SIGTERM)
                        self.assertEqual((102,21),(stops[0]['pid'],stops[0]['start']))
                    else:
                        send.assert_not_called(); self.assertEqual([],stops)
                    child.poll.assert_not_called()
                    if kind == 'live': self.assertIsNone(observation['members'])
                    else: self.assertEqual({(r['pid'],r['start']) for r in rows},observation['members'])

    def test_opt_in_owner_exit_hook_precedes_fresh_refusal_and_preserves_default_events(self):
        from unittest.mock import Mock, patch
        for opt_in in (False, True):
            for remaining in ([], [{'pid':102,'start':21,'pgid':101,'state':'S','rss':1}]):
                with self.subTest(opt_in=opt_in, remaining=remaining), tempfile.TemporaryDirectory() as tmp:
                    spec = importlib.util.spec_from_file_location('fixture_owner', ROOT / 'eng/shared_process_owner.py')
                    owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
                    leader = {'pid':101,'start':20,'pgid':101,'ppid':os.getpid(),'state':'S','rss':1}
                    child = Mock(pid=101, returncode=0); child.poll.return_value=0
                    events = []; observations = []
                    def hook(event, child, birth, known):
                        events.append(event)
                        if event == 'child-exit-observed':
                            self.assertGreater(child.poll.call_count,0)
                            self.assertEqual(1, census.call_count)
                            observations.append(dict(known))
                    with patch.object(owner.subprocess,'Popen',return_value=child), \
                         patch.object(owner,'proc',side_effect=lambda pid: leader if pid == 101 else dict(leader,pid=pid,pgid=pid)), \
                         patch.object(owner,'census',side_effect=[[leader],remaining,[],[],[]]) as census, \
                         patch.object(owner,'observed_escapes',return_value=[]), \
                         patch.object(owner.os,'killpg') as killpg:
                        log = str(Path(tmp)/'run.log')
                        if remaining:
                            with self.assertRaisesRegex(RuntimeError,'live owned-group descendants'):
                                owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                          control=hook,observe_child_exit=opt_in)
                        else:
                            owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                      control=hook,observe_child_exit=opt_in)
                    self.assertEqual(opt_in,'child-exit-observed' in events)
                    self.assertEqual(['before-popen','after-birth','poll'],events[:3])
                    self.assertEqual('after-cleanup',events[-1])
                    receipt = json.loads(Path(log+'.result.json').read_text())
                    self.assertEqual(remaining and [{'pid':102,'start':21,'pgid':101,'state':'S'}] or [],receipt['exitObservation']['members'])
                    self.assertEqual('observed-empty-group',receipt['cleanup'])

    def test_unknown_birth_and_hook_failure_do_not_license_terminal_success(self):
        from unittest.mock import Mock, patch
        for unknown in (False,True):
            with self.subTest(unknown=unknown), tempfile.TemporaryDirectory() as tmp:
                spec = importlib.util.spec_from_file_location('fixture_owner', ROOT / 'eng/shared_process_owner.py')
                owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
                leader = {'pid':101,'start':20,'pgid':101,'ppid':os.getpid(),'state':'S','rss':1}
                child=Mock(pid=101,returncode=0); child.poll.return_value=0
                original=PermissionError('terminal observation unavailable'); events=[]
                def hook(event,*args):
                    events.append(event)
                    if event == 'child-exit-observed': raise original
                with patch.object(owner.subprocess,'Popen',return_value=child), \
                     patch.object(owner,'proc',side_effect=lambda pid: None if unknown else dict(leader,pid=pid)), \
                     patch.object(owner,'census',return_value=[]), \
                     patch.object(owner,'observed_escapes',return_value=[]), \
                     patch.object(owner.os,'killpg') as killpg, patch.object(owner.os,'kill') as kill:
                    log=str(Path(tmp)/'run.log')
                    with self.assertRaises(BaseException) as caught:
                        owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                  control=hook,observe_child_exit=True)
                    if unknown:
                        self.assertNotIn('child-exit-observed',events)
                        killpg.assert_not_called(); kill.assert_not_called()
                    else:
                        self.assertIs(original,caught.exception)
                receipt=json.loads(Path(log+'.result.json').read_text())
                self.assertEqual(unknown,receipt['birthIdentityUnavailable'])
                self.assertIsNone(receipt['exitObservation'] if unknown else receipt['exitObservation']['members'])

    def test_lifecycle_projection_preserves_unknown_empty_and_refusing_identity(self):
        unknown=module.lifecycle_summary({'ownerReceipt':{}})
        self.assertIsNone(unknown['adapterExitMembership'])
        self.assertIsNone(unknown['ownerExitObservation'])
        value={'originalServerBirths':[{'pid':102,'start':21,'private':'NEVER COPY'}],
               'exitMembers':[], 'originalServerStopRequests':[],
               'ownerReceipt':{'exitObservation':{'code':0,'members':[{'pid':103,'start':22,'pgid':101,'state':'S','private':'NEVER COPY'}], 'omittedMembers':0}},
               'input':{'private':'NEVER COPY'}}
        summary=module.lifecycle_summary(value)
        self.assertEqual([],summary['adapterExitMembership']['members'])
        self.assertEqual(103,summary['ownerExitObservation']['membership']['members'][0]['pid'])
        self.assertNotIn('NEVER COPY',json.dumps(summary))
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); scratch=root/'scratch'; scratch.mkdir()
            value.update(schema=module.RESULT_SCHEMA,commandExit=0,commandAccepted=False,firstCause={'type':'RuntimeError','message':'live owned-group descendants'},additionalFailures=[])
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value))
            artifact=module.capture(scratch,root/'diagnostics',1)
            retained=json.loads(artifact['logs'][0]['text'])
            self.assertEqual(summary,retained['lifecycle'])
            self.assertNotIn('NEVER COPY',json.dumps(artifact))
            value['ownerReceipt']['exitObservation']['omittedMembers'] = {'private':'NEVER COPY'}
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value))
            malformed=module.capture(scratch,root/'malformed-diagnostics',1)
            self.assertIn('live owned-group descendants',malformed['logs'][0]['text'])
            self.assertIn('lifecycle-fields-unavailable',malformed['omittedCoverage'])
            self.assertNotIn('NEVER COPY',json.dumps(malformed))

    def test_denied_fd_observation_requires_fresh_terminal_original_birth(self):
        from unittest.mock import Mock, patch
        row = dict(pid=101, start=20, pgid=101, state='R')
        vectors = [(None, True), (dict(row, start=21), True),
                   (dict(row, state='Z'), True), (dict(row), False),
                   (dict(row, state='S'), False), (dict(row, state='Z', pgid=102), False)]
        for operation in ('directory', 'link'):
            for fresh, terminal in vectors:
                with self.subTest(operation=operation, fresh=fresh):
                    error = PermissionError('original denied FD observation')
                    observe = Mock(return_value=fresh)
                    additional = []
                    with patch.object(module.Path, 'iterdir', side_effect=error if operation == 'directory' else None,
                                      return_value=[Path('/proc/101/fd/7')]), \
                         patch.object(module.os, 'readlink', side_effect=error):
                        if terminal:
                            self.assertEqual([], module.owned_fd_targets(row, observe, additional))
                        else:
                            with self.assertRaises(PermissionError) as caught:
                                module.owned_fd_targets(row, observe, additional)
                            self.assertIs(error, caught.exception)
                        if fresh is not None and fresh['start'] == row['start'] and fresh['pgid'] != row['pgid']:
                            self.assertEqual('ValueError', additional[0]['type'])
                            self.assertIn('server group changed', additional[0]['message'])
                        else:
                            self.assertEqual([], additional)
                    observe.assert_called_once_with(row['pid'])

    def test_fd_observation_retains_targets_bounds_and_unknown_birth_failure(self):
        from unittest.mock import Mock, patch
        row = dict(pid=101, start=20, pgid=101, state='R')
        with patch.object(module.Path, 'iterdir', return_value=[Path('/proc/101/fd/7')]), \
             patch.object(module.os, 'readlink', return_value='socket:[123]'):
            self.assertEqual(['socket:[123]'], module.owned_fd_targets(row, Mock()))
        with patch.object(module.Path, 'iterdir', return_value=[Path('/proc/101/fd/7')] * 257):
            with self.assertRaisesRegex(ValueError, 'server FD cap'):
                module.owned_fd_targets(row, Mock())
        original = PermissionError('original denied FD observation')
        additional = []
        with patch.object(module.Path, 'iterdir', side_effect=original):
            with self.assertRaises(PermissionError) as caught:
                module.owned_fd_targets(row, Mock(side_effect=PermissionError('fresh birth unreadable')), additional)
            self.assertIs(original, caught.exception)
            self.assertEqual([module.failure_record(PermissionError('fresh birth unreadable'))], additional)

    def spec(self):
        return dict(schema=module.COMMAND_SCHEMA, role='verify', argv=['/quint','verify','/model'],
                    pins=[{'path':'/input'+str(i),'bytes':10,'sha256':'a'*64} for i in range(5)], aliases=[],
                    semanticToolchainId=module.TOOLCHAIN_ID, archiveSha256='b'*64,
                    started=100.0, workDeadline=250.0, totalDeadline=280.0, workSeconds=150,
                    cleanupSeconds=30, rssBytes=6*1024**3, logBytes=8*1024**2, maximumGenerations=6,
                    endpoint=19822, jvmArgs=['-Xmx1024m','-Xms64m'],
                    tlcConfig={'maxHeap':'-Xmx2G','stackSize':'-Xss64m','workers':1},
                    mutableScope={'cwd':'/scope','tmpdir':'/scope/tmp','diskBytes':1073741824,
                                  'inventorySeconds':1,'maximumEntries':25000,'sampleIntervalSeconds':.5,
                                  'javaToolOptions':'-Djava.io.tmpdir=/scope/tmp',
                                  'denoDir':'/scope/cache/deno','xdgCacheHome':'/scope/cache'})

    def test_actual_shape_separates_semantic_identity_from_archive_bytes(self):
        result = module.command_preflight(self.spec(), now=101)
        self.assertTrue(result['passed'])
        self.assertNotEqual(self.spec()['semanticToolchainId'], self.spec()['archiveSha256'])

    def test_resource_clock_and_actual_field_contradictions_refuse(self):
        replacements = [('role','watch'), ('semanticToolchainId','b'*64), ('archiveSha256','not-a-digest'),
                        ('workDeadline',251), ('totalDeadline',281), ('started',float('nan')),
                        ('jvmArgs',['-Xmx4096m']), ('rssBytes',2*1024**3), ('maximumGenerations',7),
                        ('tlcConfig',{'maxHeap':'-Xmx8G','stackSize':'-Xss515m','workers':'auto'})]
        for field, value in replacements:
            with self.subTest(field=field):
                spec = self.spec(); spec[field] = value
                with self.assertRaises(ValueError): module.command_preflight(spec, now=101)
        with self.assertRaises(ValueError): module.command_preflight(self.spec(), now=250)
        spec = self.spec(); del spec['pins']
        with self.assertRaises(ValueError): module.command_preflight(spec, now=101)

    def test_at_watch_does_not_repurpose_a_dispatch_runtime_entry(self):
        self.assertEqual('watch', module.command_role('credential-entry','watch'))
        with self.assertRaises(ValueError): module.command_role('dispatch-runtime-entry','watch')
        self.assertEqual('dispatch', module.command_role('dispatch-runtime-entry','dispatch'))

    def test_actual_launch_grant_uses_original_sixty_seconds(self):
        module.validate_launch_grant({'startedAtEpoch':1000,'launchBeforeEpoch':1060})
        for grant in [{'startedAtEpoch':1000,'absoluteDeadlineEpoch':1060},
                      {'startedAtEpoch':1000,'launchBeforeEpoch':1120},
                      {'startedAtEpoch':float('nan'),'launchBeforeEpoch':float('nan')}]:
            with self.assertRaises(ValueError): module.validate_launch_grant(grant)

    def test_actual_extraction_fields_do_not_establish_compiler_acceptance(self):
        receipt = dict(accepted=True, selectedManifestSha256='a'*64,
                       allSelectedBytesAndModesVerified=True, extractedCodeExecuted=False, providerRequests=0)
        self.assertEqual({'extractionAccepted':True,'compilerAccepted':False,'ciAccepted':False},
                         module.extraction_receipt_join(receipt,'a'*64))
        wrong = copy.deepcopy(receipt); del wrong['allSelectedBytesAndModesVerified']
        wrong['allBytesModesVerified'] = True
        with self.assertRaises(ValueError): module.extraction_receipt_join(wrong,'a'*64)
        with self.assertRaises(ValueError): module.extraction_receipt_join(receipt,'b'*64)
        with self.assertRaises(ValueError): module.extraction_receipt_join(receipt,None)
        wrong = copy.deepcopy(receipt); wrong['providerRequests'] = False
        with self.assertRaises(ValueError): module.extraction_receipt_join(wrong,'a'*64)

    def test_expected_server_retirement_cannot_accept_foreign_live_descendants(self):
        receipt = {'exit':0,'firstCause':'RuntimeError: child exited with live owned-group descendants',
                   'leader':{'pgid':101},'exitObservation':{'code':0,'omittedMembers':0,
                   'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}]}}
        stops = [{'pid':101,'start':20,'signal':15,'disposition':'sent'}]
        self.assertTrue(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},stops))
        self.assertFalse(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},[]))
        for servers, members in [(set(),set()), ({(101,20)},{(101,20),(102,21)}),
                                 ({(101,20)},{(101,19)}), ({(101,20)},None), ({(101,20),(102,21)},{(101,20),(102,21)})]:
            with self.subTest(servers=servers,members=members):
                self.assertFalse(module.expected_server_retirement(receipt,servers,members,stops))
        for observation in (None, {}, {'code':0,'members':None,'omittedMembers':None},
                            {'code':0,'members':[],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}, {'pid':102,'start':21,'pgid':101,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':19,'pgid':101,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':999,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}],'omittedMembers':1}):
            with self.subTest(ownerObservation=observation):
                later = copy.deepcopy(receipt); later['exitObservation'] = observation
                # Adapter sole-server membership and recorded stop never waive
                # the owner's later unknown/foreign/replaced/partial refusal.
                self.assertFalse(module.expected_server_retirement(later,{(101,20)},{(101,20)},stops))
        receipt['exit'] = 37
        self.assertFalse(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},stops))

    def test_first_failure_projection_is_serializable_bounded_and_filters_secrets(self):
        original = module.failure_record(RuntimeError('original compiler failure'))
        secondary = module.failure_record(OSError('report failed'))
        body = json.loads(json.dumps({'firstCause':original,'additionalFailures':[secondary]}))
        self.assertEqual('original compiler failure',body['firstCause']['message'])
        self.assertEqual('report failed',body['additionalFailures'][0]['message'])
        self.assertLessEqual(len(module.failure_record(ValueError('x'*4096))['message']),512)
        self.assertNotIn('synthetic-secret',module.failure_record(ValueError('Authorization: Bearer synthetic-secret'))['message'])


if __name__ == '__main__': unittest.main()
