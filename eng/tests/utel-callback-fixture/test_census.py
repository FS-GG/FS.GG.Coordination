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
cut = runpy.run_path(str(ROOT / 'eng/tests/utel-callback-fixture/terminal_cut_owner.py'), run_name='terminal_cut_test')
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


class TerminalCutControls(unittest.TestCase):
    def test_closed_release_requires_current_execution_and_exact_cut(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / 'release.json'
            expected = {'nonce': 'a' * 32, 'binding': 'b' * 64, 'request': 'c' * 64,
                        'ordinal': 6, 'stage': 'readback'}
            path.write_text(json.dumps(expected))
            cut['closed_release'](path, expected['nonce'], expected['binding'], expected['request'])
            for changed in [dict(expected, nonce='d' * 32), dict(expected, ordinal=5),
                            dict(expected, request='d' * 64), dict(expected, stage='replace'),
                            dict(expected, extra=True)]:
                path.write_text(json.dumps(changed))
                with self.subTest(changed=changed):
                    with self.assertRaises(ValueError):
                        cut['closed_release'](path, expected['nonce'], expected['binding'], expected['request'])

    def test_digesting_input_hashes_bytes_actually_read(self):
        stream = cut['DigestingInput'](io.BytesIO(b'one-two'))
        self.assertEqual(b'one', stream.read(3))
        self.assertEqual(b'-two', stream.read(4))
        self.assertEqual(hashlib.sha256(b'one-two').hexdigest(), stream.digest.hexdigest())


class PairControls(unittest.TestCase):
    @contextlib.contextmanager
    def pair(self, slot=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            owner, producer = root / 'owner', root / 'producer'
            rows = []
            roles = helper['SLOT_PAIR_ROLES'] if slot else helper['PAIR_ROLES']
            for repo, paths in roles.items():
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
            if slot:
                vectors['slotEvidence'] = {'profile': helper['SLOT_PROFILE']}
            manifest_name = helper['SLOT_MANIFEST_PATH' if slot else 'MANIFEST_PATH']
            vectors_name = helper['SLOT_VECTORS_PATH' if slot else 'VECTORS_PATH']
            manifest_path, vectors_path = owner / manifest_name, owner / vectors_name
            manifest_path.write_text(json.dumps(manifest))
            vectors_path.write_text(json.dumps(vectors))
            pin = {'schema': 'fsgg.utel-callback-source/2', 'repository': 'FS-GG/.github',
                   'revision': 'a' * 40, 'manifestSha256': helper['digest'](manifest_path),
                   'vectorsSha256': helper['digest'](vectors_path), 'bundleDigest': bundle,
                   'qualification': 'inert fixture'}
            if slot:
                pin.update(schema='fsgg.utel-callback-source/3', profile=helper['SLOT_PROFILE'])
            with patch.dict(helper['validate_pair'].__globals__, {'ROOT': producer}):
                yield pin, owner, producer, manifest, vectors

    def test_closed_pair_accepts_exact_twelve_roles_and_vectors(self):
        with self.pair() as (pin, owner, _, manifest, _):
            self.assertEqual(manifest, helper['validate_pair'](pin, owner))

    def test_slot_profile_accepts_exact_sixteen_roles(self):
        self.assertEqual('inert-slot-callback-pair/2', helper['SLOT_PROFILE'])
        with self.pair(slot=True) as (pin, owner, _, manifest, _):
            self.assertEqual(16, len(manifest['files']))
            self.assertEqual(manifest, helper['validate_pair'](pin, owner))

    def test_slot_profile_unknown_and_crossed_profiles_and_paths_refuse(self):
        with self.pair(slot=True) as (pin, owner, _, _, _):
            for changed in [dict(pin, profile='unknown'), dict(pin, profile='inert-slot-callback-pair/1'), dict(pin, profile='inert-callback-pair/1'),
                            dict(pin, entryPath=helper['SLOT_ENTRY_PATH']),
                            dict(pin, manifestPath=helper['SLOT_MANIFEST_PATH']),
                            {key: value for key, value in pin.items() if key != 'profile'},
                            dict(pin, schema='fsgg.utel-callback-source/2')]:
                with self.subTest(changed=changed):
                    with self.assertRaises(RuntimeError): helper['validate_pair'](changed, owner)

    def test_slot_profile_rehashed_foreign_role_and_crossed_vectors_refuse(self):
        for mutation in ['foreign-role', 'crossed-vectors', 'crossed-profile']:
            with self.pair(slot=True) as (pin, owner, _, manifest, vectors):
                if mutation == 'foreign-role':
                    manifest['files'][0]['path'] = 'scripts/release-successor-publish.py'
                elif mutation == 'crossed-vectors':
                    vectors['binding']['source']['contract'] = 'f' * 64
                else:
                    vectors['slotEvidence']['profile'] = 'inert-slot-callback-pair/1'
                manifest_path = owner / helper['SLOT_MANIFEST_PATH']
                vectors_path = owner / helper['SLOT_VECTORS_PATH']
                manifest_path.write_text(json.dumps(manifest))
                vectors_path.write_text(json.dumps(vectors))
                pin.update(manifestSha256=helper['digest'](manifest_path), vectorsSha256=helper['digest'](vectors_path))
                with self.subTest(mutation=mutation):
                    with self.assertRaises(RuntimeError): helper['validate_pair'](pin, owner)

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


class ClosedSourcePinControls(unittest.TestCase):
    def test_selected_slot_and_closed_pins_require_same_revision_and_vectors(self):
        slot = json.loads((ROOT / 'eng/utel-slot-callback-source.json').read_bytes())
        closed = json.loads((ROOT / 'eng/utel-closed-callback-source.json').read_bytes())
        helper['validate_selected_sources'](slot, closed)
        for key in ('revision', 'vectorsSha256'):
            changed = dict(closed, **{key: '0' * len(closed[key])})
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                helper['validate_selected_sources'](slot, changed)

    def fixture(self, root):
        profile = {'schema': 'fsgg.utel-closed-source/1', 'admission': False,
                   'production': {'provider': None, 'enrollment': None, 'physicalUpperBound': None}}
        for name in helper['CLOSED_ROLES']:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(json.dumps(profile).encode() if name == helper['CLOSED_PROFILE_PATH'] else ('controlled-source:' + name).encode())
        manifest = {'schema': 'fsgg.utel-closed-source-manifest/1', 'qualification': 'synthetic control only',
                    'files': [{'path': name, 'sha256': helper['digest'](root / name)} for name in helper['CLOSED_ROLES']]}
        (root / helper['CLOSED_MANIFEST_PATH']).write_text(json.dumps(manifest))
        (root / helper['SLOT_VECTORS_PATH']).write_text('{}')
        pin = {'schema': 'fsgg.utel-closed-callback-source/1', 'profile': profile['schema'],
               'repository': 'FS-GG/.github', 'revision': 'a' * 40, 'qualification': 'synthetic control only',
               'manifestSha256': helper['digest'](root / helper['CLOSED_MANIFEST_PATH']),
               'profileSha256': helper['digest'](root / helper['CLOSED_PROFILE_PATH']),
               'vectorsSha256': helper['digest'](root / helper['SLOT_VECTORS_PATH'])}
        return pin, manifest

    def test_closed_source_pin_and_exact_ordered_closure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); pin, manifest = self.fixture(root)
            self.assertEqual(helper['validate_closed'](pin, root), manifest)
            for mutation in ('missing', 'duplicate', 'foreign', 'order'):
                changed = json.loads(json.dumps(manifest))
                if mutation == 'missing': changed['files'].pop()
                elif mutation == 'duplicate': changed['files'][0] = changed['files'][1]
                elif mutation == 'foreign': changed['files'][0]['path'] = '../foreign'
                else: changed['files'].reverse()
                (root / helper['CLOSED_MANIFEST_PATH']).write_text(json.dumps(changed))
                changed_pin = dict(pin, manifestSha256=helper['digest'](root / helper['CLOSED_MANIFEST_PATH']))
                with self.subTest(mutation=mutation), self.assertRaises(RuntimeError): helper['validate_closed'](changed_pin, root)

    def test_closed_source_pin_refuses_changed_source_profile_and_expanded_authority(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); pin, _ = self.fixture(root)
            for field, value in (('schema', 'fsgg.utel-callback-source/3'), ('profile', 'native'),
                                 ('repository', 'foreign/repo'), ('revision', 'not-a-sha'), ('manifestSha256', '0' * 64)):
                with self.subTest(field=field), self.assertRaises(RuntimeError): helper['validate_closed'](dict(pin, **{field: value}), root)
            extra = dict(pin, provider='caller-supplied')
            with self.assertRaises(RuntimeError): helper['validate_closed'](extra, root)
            path = root / helper['CLOSED_ROLES'][0]; original = path.read_bytes(); path.write_bytes(original + b'x')
            with self.assertRaises(RuntimeError): helper['validate_closed'](pin, root)
            path.write_bytes(original)
            profile_path = root / helper['CLOSED_PROFILE_PATH']; profile = json.loads(profile_path.read_bytes()); profile['admission'] = True
            profile_path.write_text(json.dumps(profile))
            with self.assertRaises(RuntimeError): helper['validate_closed'](pin, root)


if __name__ == '__main__':
    unittest.main()
