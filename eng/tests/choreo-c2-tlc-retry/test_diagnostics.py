"""Injected source controls only: no Quint, Java, CLR or model execution."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

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
            env = dict(os.environ, PATH=str(binary) + ':' + os.environ['PATH'], RUNNER_TEMP=str(root), FSGG_QUINT_BIN=str(quint), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(dest), FAKE_SUCCESS='yes' if success else 'no')
            env.pop('FSGG_QUINT_TOOLCHAIN_ARCHIVE', None)
            result = subprocess.run(['bash', str(ROOT / 'eng/verify-choreo-c2-bounded.sh')], env=env, capture_output=True, text=True, timeout=10)
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))
            value = json.loads((dest / 'failure.json').read_text()) if (dest / 'failure.json').exists() else None
            return result, value

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
        script = (ROOT / 'eng/optimistic-run-formal-fragment.sh').read_text()
        self.assertIn('FSGG_CHOREO_DIAGNOSTIC_ROOT="$fragment/choreo-failure-diagnostics"', script)


if __name__ == '__main__': unittest.main()
