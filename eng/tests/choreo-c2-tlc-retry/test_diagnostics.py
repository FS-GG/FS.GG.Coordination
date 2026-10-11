"""Injected source controls only: no Quint, Java, CLR or model execution."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import copy

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('choreo_diagnostics', ROOT / 'eng/retain-choreo-c2-diagnostics.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiagnosticControls(unittest.TestCase):
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
        receipt = {'exit':0,'firstCause':'RuntimeError: child exited with live owned-group descendants'}
        self.assertTrue(module.expected_server_retirement(receipt,{(101,20)},{(101,20)}))
        for servers, members in [(set(),set()), ({(101,20)},{(101,20),(102,21)}),
                                 ({(101,20)},{(101,19)}), ({(101,20),(102,21)},{(101,20),(102,21)})]:
            with self.subTest(servers=servers,members=members):
                self.assertFalse(module.expected_server_retirement(receipt,servers,members))
        receipt['exit'] = 37
        self.assertFalse(module.expected_server_retirement(receipt,{(101,20)},{(101,20)}))

    def test_first_failure_projection_is_serializable_bounded_and_filters_secrets(self):
        original = module.failure_record(RuntimeError('original compiler failure'))
        secondary = module.failure_record(OSError('report failed'))
        body = json.loads(json.dumps({'firstCause':original,'additionalFailures':[secondary]}))
        self.assertEqual('original compiler failure',body['firstCause']['message'])
        self.assertEqual('report failed',body['additionalFailures'][0]['message'])
        self.assertLessEqual(len(module.failure_record(ValueError('x'*4096))['message']),512)
        self.assertNotIn('synthetic-secret',module.failure_record(ValueError('Authorization: Bearer synthetic-secret'))['message'])


if __name__ == '__main__': unittest.main()
