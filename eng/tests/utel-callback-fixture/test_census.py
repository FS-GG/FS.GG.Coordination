#!/usr/bin/env python3
"""Pure preflight for the exact callback TRX census; launches no workload."""
import contextlib
import io
import hashlib
import json
import os
import pathlib
import runpy
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[3]
helper = runpy.run_path(str(ROOT / 'eng/utel-callback-fixture.py'), run_name='callback_census_test')
EXPECTED = json.loads((ROOT / 'eng/utel-callback-cases.json').read_bytes())['cases']


class CensusControls(unittest.TestCase):
    def check(self, names, outcomes=None):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / 'compiler-and-tests').mkdir()
            trx = ET.Element('TestRun')
            for name, outcome in zip(names, outcomes or ['Passed'] * len(names)):
                ET.SubElement(trx, 'UnitTestResult', testName=name, outcome=outcome)
            path = root / 'results.trx'
            ET.ElementTree(trx).write(path)
            with patch.dict(os.environ, {'RUNNER_TEMP': temporary}), contextlib.redirect_stdout(io.StringIO()):
                helper['census'](path)

    def test_exact_passing_cohort_is_accepted(self):
        self.check(EXPECTED)

    def test_empty_missing_duplicate_unknown_and_failed_results_refuse(self):
        for names, outcomes in [([], None), (EXPECTED[:-1], None), (EXPECTED + [EXPECTED[0]], None),
                                (EXPECTED[:-1] + ['different-case'], None),
                                (EXPECTED, ['Failed'] + ['Passed'] * (len(EXPECTED) - 1))]:
            with self.subTest(names=len(names), outcomes=outcomes):
                with self.assertRaises(RuntimeError): self.check(names, outcomes)


class PairControls(unittest.TestCase):
    @contextlib.contextmanager
    def pair(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            owner, producer = root / 'owner', root / 'producer'
            rows = []
            for repo, paths in helper['PAIR_ROLES'].items():
                base = owner if repo == 'FS-GG/.github' else producer
                for path in paths:
                    file = base / path
                    file.parent.mkdir(parents=True, exist_ok=True)
                    file.write_bytes((repo + '/' + path).encode('ascii'))
                    rows.append({'repository': repo, 'path': path,
                                 'sha256': hashlib.sha256(file.read_bytes()).hexdigest()})
            rows.sort(key=lambda row: (row['repository'] + '/' + row['path']).encode('ascii'))
            bundle = hashlib.sha256(''.join(row['sha256'] + '  ' + row['repository'] + '/' + row['path'] + '\n'
                                            for row in rows).encode('ascii')).hexdigest()
            manifest = {'schema': 'fsgg.authentication-callable-source-bundle/1',
                        'files': rows, 'bundleDigest': bundle}
            vectors = {'schema': 'fsgg.authentication-callback-vectors/1',
                       'binding': {'source': {'contract': bundle}}}
            manifest_path, vectors_path = owner / helper['MANIFEST_PATH'], owner / helper['VECTORS_PATH']
            manifest_path.write_text(json.dumps(manifest))
            vectors_path.write_text(json.dumps(vectors))
            pin = {'schema': 'fsgg.utel-callback-source/2', 'repository': 'FS-GG/.github',
                   'revision': 'a' * 40, 'manifestSha256': helper['digest'](manifest_path),
                   'vectorsSha256': helper['digest'](vectors_path), 'bundleDigest': bundle,
                   'qualification': 'inert fixture'}
            with patch.dict(helper['validate_pair'].__globals__, {'ROOT': producer}):
                yield pin, owner, producer, manifest, vectors

    def test_closed_pair_accepts_exact_twelve_roles_and_vectors(self):
        with self.pair() as (pin, owner, _, manifest, _):
            self.assertEqual(manifest, helper['validate_pair'](pin, owner))

    def test_legacy_schema_unknown_pin_fields_and_wrong_repository_refuse(self):
        with self.pair() as (pin, owner, _, _, _):
            for changed in [dict(pin, schema='fsgg.utel-callback-source/1'),
                            dict(pin, manifestPath='callable-source-manifest.json'),
                            dict(pin, repository='different/repository')]:
                with self.subTest(changed=changed):
                    with self.assertRaises(RuntimeError): helper['validate_pair'](changed, owner)

    def test_changed_source_manifest_and_vectors_refuse(self):
        with self.pair() as (pin, owner, producer, manifest, _):
            paths = [owner / helper['MANIFEST_PATH'], owner / helper['VECTORS_PATH'],
                     producer / next(row['path'] for row in manifest['files']
                                     if row['repository'] == 'FS-GG/FS.GG.Coordination')]
            for path in paths:
                original = path.read_bytes()
                path.write_bytes(original + b' ')
                with self.subTest(path=path.name):
                    with self.assertRaises(RuntimeError): helper['validate_pair'](pin, owner)
                path.write_bytes(original)

    def test_rehashed_wrong_role_and_crossed_vector_bundle_refuse(self):
        for mutation in ['duplicate-role', 'unknown-role', 'missing-role', 'wrong-order', 'crossed-vectors']:
            with self.pair() as (pin, owner, _, manifest, vectors):
                if mutation == 'duplicate-role': manifest['files'][0] = manifest['files'][1]
                elif mutation == 'unknown-role': manifest['files'][0]['path'] = '../elsewhere'
                elif mutation == 'missing-role': manifest['files'].pop()
                elif mutation == 'wrong-order': manifest['files'].reverse()
                else: vectors['binding']['source']['contract'] = 'f' * 64
                manifest_path, vectors_path = owner / helper['MANIFEST_PATH'], owner / helper['VECTORS_PATH']
                manifest_path.write_text(json.dumps(manifest))
                vectors_path.write_text(json.dumps(vectors))
                pin.update(manifestSha256=helper['digest'](manifest_path), vectorsSha256=helper['digest'](vectors_path))
                with self.subTest(mutation=mutation):
                    with self.assertRaises(RuntimeError): helper['validate_pair'](pin, owner)


if __name__ == '__main__':
    unittest.main()
