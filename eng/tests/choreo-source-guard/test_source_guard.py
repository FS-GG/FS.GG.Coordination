#!/usr/bin/env python3
"""Pure provenance controls; never invoke Quint or execute a model."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
GUARD = ROOT / 'eng/validate-choreo-trace-source.fsx'
CURRENT = 'src/FS.GG.Coordination.Protocol/Protocol.md'
CONFIG = 'eng/quint-qualification.json'
COMPILED = 'src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/manifest.json'
FIXTURES = 'tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo'


class ProvenanceControls(unittest.TestCase):
    def run_guard(self, mutate=None):
        with tempfile.TemporaryDirectory(prefix='fsgg-choreo-source-control-') as directory:
            scratch = Path(directory)
            for relative in [CURRENT, CONFIG, COMPILED]:
                destination = scratch / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / relative, destination)
            shutil.copytree(ROOT / FIXTURES, scratch / FIXTURES)
            if mutate:
                mutate(scratch)
            result = subprocess.run([os.environ.get('DOTNET_EXE', 'dotnet'), 'fsi', str(GUARD),
                                     '--', '--root', str(scratch)], cwd=ROOT,
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode == 0, mutate is None, result.stdout + result.stderr)
            if mutate:
                self.assertTrue(any(marker in result.stderr for marker in
                                    ['CHOREO_SOURCE_REFUSED', 'CANONICAL-PROTOCOL-SOURCE',
                                     'FileNotFoundException']), result.stderr)
            else:
                self.assertIn('CHOREO_SOURCE_OK', result.stdout)

    def test_positive_and_independent_input_drift(self):
        self.run_guard()
        for relative in [CURRENT, CONFIG, COMPILED,
                         FIXTURES + '/HistoricalProtocol.md', FIXTURES + '/manifest.json',
                         FIXTURES + '/happy-path.itf.json']:
            with self.subTest(relative=relative):
                def mutate(scratch):
                    path = scratch / relative
                    if relative in [CONFIG, COMPILED]:
                        document = json.loads(path.read_text())
                        document['sourceSha256'] = '0' * 64
                        path.write_text(json.dumps(document))
                    else:
                        path.write_bytes(path.read_bytes() + b'\ndrift\n')
                self.run_guard(mutate)

    def test_candidate_declarations_cannot_bless_changed_source(self):
        def mutate(scratch):
            source = scratch / CURRENT
            source.write_bytes(source.read_bytes() + b'\ndrift\n')
            import hashlib
            candidate = hashlib.sha256(source.read_bytes()).hexdigest()
            for relative in [CONFIG, COMPILED]:
                path = scratch / relative
                document = json.loads(path.read_text())
                document['sourceSha256'] = candidate
                path.write_text(json.dumps(document))
        self.run_guard(mutate)

    def test_missing_historical_fixture_refuses(self):
        self.run_guard(lambda scratch: (scratch / FIXTURES / 'HistoricalProtocol.md').unlink())


class ExecutorBoundaries(unittest.TestCase):
    def test_c3_stops_at_failed_guard_before_quint_lookup(self):
        with tempfile.TemporaryDirectory(prefix='fsgg-choreo-executor-control-') as directory:
            binary = Path(directory) / 'dotnet'
            binary.write_text('#!/usr/bin/env bash\nexit 37\n')
            binary.chmod(0o755)
            environment = dict(os.environ, PATH=directory + ':' + os.environ['PATH'],
                               FSGG_QUINT_BIN=directory + '/absent-quint')
            result = subprocess.run(['bash', str(ROOT / 'eng/verify-choreo-c3-traces.sh')],
                                    cwd=ROOT, env=environment, capture_output=True, text=True)
            self.assertEqual(result.returncode, 37, result.stdout + result.stderr)

    def test_c5_stops_at_failed_guard_before_model_export(self):
        import importlib.util
        from unittest.mock import patch
        spec = importlib.util.spec_from_file_location('choreo_parity_control',
                                                     ROOT / 'eng/verify-choreo-c5-parity.py')
        module = importlib.util.module_from_spec(spec)
        import sys
        prior_bytecode = sys.dont_write_bytecode
        try:
            sys.dont_write_bytecode = True
            spec.loader.exec_module(module)
        finally:
            sys.dont_write_bytecode = prior_bytecode
        refusal = subprocess.CalledProcessError(37, ['dotnet', 'fsi'])
        with patch.object(module.subprocess, 'run', side_effect=refusal) as invoked:
            with self.assertRaises(subprocess.CalledProcessError):
                module.export_legacy(Path('/uncreated-model-control'))
            self.assertEqual(invoked.call_count, 1)
            self.assertEqual(invoked.call_args.args[0][1], 'fsi')
            self.assertIn(str(GUARD), invoked.call_args.args[0])


if __name__ == '__main__':
    unittest.main()
