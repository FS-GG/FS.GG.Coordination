#!/usr/bin/env python3
"""Inert orchestration controls; never launch CLR or fetch callback source."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
DOTNET = r'''#!/usr/bin/env python3
import json, os, pathlib, sys, time
args = sys.argv[1:]
stage = args[0]
if stage == 'test':
    name = args[args.index('--logger') + 1].split('=')[1].removesuffix('.trx')
    stage = name
with open(os.environ['COMMAND_LOG'], 'a') as out: out.write(stage + '\n')
if os.environ.get('FAIL_STAGE') == stage: sys.exit(17)
if os.environ.get('SLEEP_STAGE') == stage: time.sleep(30)
if stage not in ('restore', 'build'):
    mode = os.environ.get('CENSUS_MODE', '') if os.environ.get('BAD_COHORT', stage) == stage else ''
    directory = pathlib.Path(args[args.index('--results-directory') + 1])
    if mode == 'missing': sys.exit(0)
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (stage + '.trx')
    names = json.loads(pathlib.Path('eng/utel-callback-cases.json').read_text())['cases'] if stage == 'finite-custody-transport' else ['a']
    if mode == 'wrong-cases': names = ['wrong']
    count = 0 if mode == 'zero' else len(names)
    passed = 0 if mode == 'failed' else count
    import xml.etree.ElementTree as ET
    root = ET.Element('TestRun')
    ET.SubElement(root, 'Counters', total=str(count), passed=str(passed), failed=str(count-passed))
    for name in names: ET.SubElement(root, 'UnitTestResult', testName=name, outcome='Passed')
    path.write_bytes(ET.tostring(root))
    if mode == 'malformed': path.write_text('<broken')
    if mode == 'stale': os.utime(path, (1, 1))
'''
PYTHON = r'''#!/usr/bin/env python3
import os, pathlib, sys
if len(sys.argv) > 2 and sys.argv[1] == 'eng/utel-callback-fixture.py' and sys.argv[2] == 'retain' and os.environ.get('FAIL_RETAIN'):
    sys.exit(23)
if len(sys.argv) > 2 and sys.argv[1] == 'eng/utel-callback-fixture.py' and sys.argv[2] == 'prepare':
    with open(os.environ['COMMAND_LOG'], 'a') as out: out.write('fixture\n')
    if os.environ.get('FAIL_FIXTURE'): sys.exit(19)
    (pathlib.Path(os.environ['RUNNER_TEMP']) / 'compiler-and-tests').mkdir()
    sys.exit(0)
os.execv(os.environ['REAL_PYTHON'], [os.environ['REAL_PYTHON']] + sys.argv[1:])
'''


class SourcePreflight(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='source-preflight-controls-')
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.repo = self.root / 'repo'
        self.repo.mkdir()
        for relative in ['eng/bootstrap-gates/source-preflight.sh', 'eng/validate-test-census.py',
                         'eng/utel-callback-fixture.py', 'eng/utel-callback-source.json',
                         'eng/utel-slot-callback-source.json', 'eng/utel-callback-cases.json']:
            dest = self.repo / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, dest)
        for relative in ['FS.GG.Coordination.sln', 'global.json',
                         'tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj']:
            dest = self.repo / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text('{}')
        self.git('init', '-q')
        self.git('add', '.')
        self.git('-c', 'user.name=Controls', '-c', 'user.email=controls@example.invalid', 'commit', '-qm', 'inert controls')
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        for name, body in [('dotnet', DOTNET), ('python3', PYTHON)]:
            path = self.bin / name
            path.write_text(body.replace('#!/usr/bin/env python3', '#!' + sys.executable, 1))
            path.chmod(0o755)
        self.runner = self.root / 'runner'
        self.runner.mkdir()
        self.log = self.root / 'commands'
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                        RUNNER_TEMP=str(self.runner), REAL_PYTHON=sys.executable,
                        COMMAND_LOG=str(self.log), FSGG_CANDIDATE_SHA=self.git('rev-parse', 'HEAD').strip())

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.repo), *args], text=True)

    def run_gate(self, **environment):
        return subprocess.run(['bash', 'eng/bootstrap-gates/source-preflight.sh'], cwd=self.repo,
                              env=dict(self.env, **environment), text=True, capture_output=True, timeout=10)

    def launched(self):
        return self.log.read_text().splitlines() if self.log.exists() else []

    def cause(self):
        return (self.runner / 'source-preflight/first-cause.txt').read_text()

    def test_success_compiles_before_four_fresh_censuses_and_fixture(self):
        result = self.run_gate()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(['restore', 'build', 'portable-host-allocation', 'portable-host-executor',
                          'finite-custody-inert', 'fixture', 'finite-custody-transport'], self.launched())
        self.assertFalse((self.runner / 'source-preflight/first-cause.txt').exists())
        self.assertTrue((self.runner / 'source-preflight/fixtures/compiler-and-tests/callback-case-census.json').is_file())

    def test_restore_and_compile_failures_block_tests_and_fixture(self):
        for stage in ['restore', 'build']:
            with self.subTest(stage=stage):
                if (self.runner / 'source-preflight').exists(): shutil.rmtree(self.runner / 'source-preflight')
                if self.log.exists(): self.log.unlink()
                result = self.run_gate(FAIL_STAGE=stage)
                self.assertEqual(17, result.returncode)
                self.assertEqual(['restore'] if stage == 'restore' else ['restore', 'build'], self.launched())
                self.assertIn(('compile' if stage == 'build' else stage) + ' exit=17', self.cause())

    def test_invalid_fresh_census_and_exact_callback_cases_refuse(self):
        for mode in ['zero', 'missing', 'stale', 'malformed', 'failed', 'wrong-cases']:
            with self.subTest(mode=mode):
                if (self.runner / 'source-preflight').exists(): shutil.rmtree(self.runner / 'source-preflight')
                result = self.run_gate(CENSUS_MODE=mode, BAD_COHORT='finite-custody-transport')
                self.assertNotEqual(0, result.returncode, mode)
                self.assertIn('finite-custody-transport exit=', self.cause())

    def test_failed_test_process_blocks_later_launches(self):
        result = self.run_gate(FAIL_STAGE='portable-host-allocation')
        self.assertEqual(17, result.returncode)
        self.assertEqual(['restore', 'build', 'portable-host-allocation'], self.launched())
        self.assertIn('portable-host-allocation exit=17', self.cause())
        self.assertIn('prerequisite-blocked failed-or-unknown-test-outcome', (self.runner / 'source-preflight/stages.txt').read_text())

    def test_unknown_census_blocks_later_launches(self):
        result = self.run_gate(CENSUS_MODE='missing', BAD_COHORT='portable-host-allocation')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(['restore', 'build', 'portable-host-allocation'], self.launched())
        self.assertIn('portable-host-allocation exit=', self.cause())

    def test_fixture_failure_blocks_transport(self):
        result = self.run_gate(FAIL_FIXTURE='1')
        self.assertEqual(19, result.returncode)
        self.assertIn('finite-custody-inert', self.launched())
        self.assertNotIn('finite-custody-transport', self.launched())
        self.assertIn('callback-fixture exit=19', self.cause())
        self.assertIn('prerequisite-blocked callback-fixture', (self.runner / 'source-preflight/stages.txt').read_text())

    def test_retention_failure_is_separate_from_original_compile_failure(self):
        result = self.run_gate(FAIL_STAGE='build', FAIL_RETAIN='1')
        self.assertEqual(17, result.returncode)
        self.assertIn('compile exit=17', self.cause())
        self.assertIn('retention exit=23', (self.runner / 'source-preflight/stages.txt').read_text())

    def test_retention_failure_cannot_turn_success_into_qualification(self):
        result = self.run_gate(FAIL_RETAIN='1')
        self.assertEqual(23, result.returncode)
        self.assertIn('retention exit=23', self.cause())

    def test_candidate_mismatch_blocks_effects(self):
        result = self.run_gate(FSGG_CANDIDATE_SHA='a' * 40)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.launched())

    def test_dirty_tracked_candidate_blocks_effects(self):
        (self.repo / 'global.json').write_text('changed')
        result = self.run_gate()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.launched())

    def test_missing_input_blocks_effects(self):
        (self.repo / 'eng/utel-slot-callback-source.json').unlink()
        result = self.run_gate()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.launched())

    def test_unavailable_dotnet_blocks_effects(self):
        (self.bin / 'dotnet').unlink()
        # Exclude any installed CLR without executing it.
        for tool in ['bash', 'timeout', 'mkdir', 'git', 'date']:
            (self.bin / tool).symlink_to(shutil.which(tool))
        result = self.run_gate(PATH=str(self.bin))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.launched())
        self.assertIn('missing tool dotnet', result.stderr)

    def test_deadline_preserves_timeout_and_blocks_later_work(self):
        timeout = self.bin / 'timeout'
        timeout.write_text('#!/bin/bash\n[[ "$1 $2 $3" == "--signal=TERM --kill-after=10s 480s" ]] || exit 92\nshift 3\nexec /usr/bin/timeout --signal=TERM --kill-after=1s 0.2s "$@"\n')
        timeout.chmod(0o755)
        result = self.run_gate(SLEEP_STAGE='build')
        self.assertEqual(124, result.returncode, result.stderr)
        self.assertEqual(['restore', 'build'], self.launched())
        self.assertIn('deadline exit=124', self.cause())

    def test_stale_attempt_evidence_cannot_qualify(self):
        (self.runner / 'source-preflight').mkdir()
        result = self.run_gate()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.launched())


if __name__ == '__main__':
    unittest.main()
