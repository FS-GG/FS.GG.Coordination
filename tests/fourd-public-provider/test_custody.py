#!/usr/bin/env python3
import importlib.util, json, os, pathlib, subprocess, sys, tempfile, time, unittest
from unittest import mock
ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('fourd_public_custody', ROOT / 'eng/fourd-public-provider/custody.py')
custody = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = custody
SPEC.loader.exec_module(custody)

def identity(binding=True):
    return {'schema': 'fsgg.fourd.custody-identity/1', 'runId': '123', 'runAttempt': '2', 'runNonce': 'fourd-run-12345678', 'placementSha': '1' * 40, 'fourdSourceSha': '2' * 40, 'fourdSourceTree': '3' * 40, 'fourdInventorySha256': '4' * 64, 'p2SourceSha': '5' * 40, 'p2SourceTree': '6' * 40, 'nativePolicySha256': '7' * 64, 'sealerSha256': custody.SEALER_SHA256, 'publicKeySha256': custody.PUBLIC_KEY_SHA256, 'nativeBindingSha256': '8' * 64 if binding else None, 'nativeEvidenceSha256': ''}

def owned(path, data):
    path.write_bytes(data)
    path.chmod(384)
    return path

def setup(root, archive=b'ab', evidence=b'ev'):
    a = owned(root / 'archive.oci', archive) if archive is not None else None
    e = owned(root / 'evidence.tar', evidence)
    i = identity(a is not None)
    i['nativeEvidenceSha256'] = custody.shab(evidence)
    return (a, e, i, root / 'scratch', root / 'output')

class PlanningTests(unittest.TestCase):

    def test_boundaries_and_one_byte_remainder_borrow(self):
        self.assertEqual((custody.ChunkPlan(0, 0, 2),), custody.plan_chunks(2))
        self.assertEqual((custody.ChunkPlan(0, 0, custody.CHUNK_BYTES),), custody.plan_chunks(custody.CHUNK_BYTES))
        self.assertEqual((custody.ChunkPlan(0, 0, custody.CHUNK_BYTES - 1), custody.ChunkPlan(1, custody.CHUNK_BYTES - 1, 2)), custody.plan_chunks(custody.CHUNK_BYTES + 1))
        p = custody.plan_chunks(custody.MAX_ARCHIVE_BYTES)
        self.assertEqual(512, len(p))
        self.assertEqual(custody.MAX_ARCHIVE_BYTES, sum((x.bytes for x in p)))
        for n in (1, custody.MAX_ARCHIVE_BYTES + 1):
            with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-size-refused'):
                custody.plan_chunks(n)

    def test_capacity_formula_is_exact_and_evidence_bounded(self):
        plans = custody.plan_chunks(custody.CHUNK_BYTES + 1)
        expected = sum((custody.cap_budget(p.bytes) for p in plans)) + custody.cap_budget(2) + custody.CHUNK_BYTES + custody.MAX_MANIFEST_BYTES + custody.STAGING_RESERVE
        self.assertEqual(expected, custody.required_staging_bytes(custody.CHUNK_BYTES + 1, 2))
        with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-size-refused'):
            custody.required_staging_bytes(None, 1)

class SealingTests(unittest.TestCase):

    def assert_sentinel_unchanged(self, directory, inode, payload=b'unowned'):
        self.assertTrue(directory.is_dir())
        self.assertEqual(inode, directory.stat().st_ino)
        self.assertEqual(payload, (directory / 'unowned').read_bytes())

    def test_preexisting_scratch_and_output_collisions_remain_untouched(self):
        for existing in ('scratch', 'output', 'both'):
            with self.subTest(existing=existing), tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                a, e, i, scratch, output = setup(root)
                selected = {'scratch': (scratch,), 'output': (output,), 'both': (scratch, output)}[existing]
                before = {}
                for directory in selected:
                    directory.mkdir(mode=0o700)
                    owned(directory / 'unowned', b'unowned')
                    before[directory] = directory.stat().st_ino
                with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-path-refused'):
                    custody.seal_series(archive=a, evidence=e, scratch=scratch, output=output,
                                        identity=i, deadline_monotonic=time.monotonic() + 20)
                for directory in selected:
                    self.assert_sentinel_unchanged(directory, before[directory])
                if existing == 'output':
                    self.assertFalse(scratch.exists())

    def test_failure_after_both_acquisitions_removes_only_acquired_directories(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, scratch, output = setup(root)
            with mock.patch.object(custody, 'open_owned', side_effect=custody.CustodyRefusal('custody-source-refused')):
                with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-source-refused'):
                    custody.seal_series(archive=a, evidence=e, scratch=scratch, output=output,
                                        identity=i, deadline_monotonic=time.monotonic() + 20)
            self.assertFalse(scratch.exists())
            self.assertFalse(output.exists())

    def test_replacement_paths_are_never_cleaned_as_owned(self):
        for replaced in ('scratch', 'output'):
            with self.subTest(replaced=replaced), tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                a, e, i, scratch, output = setup(root)

                def replace_then_refuse(*_args, **_kwargs):
                    target = scratch if replaced == 'scratch' else output
                    target.rename(root / f'acquired-{replaced}')
                    target.mkdir(mode=0o700)
                    owned(target / 'unowned', b'replacement')
                    raise custody.CustodyRefusal('custody-source-refused')

                with mock.patch.object(custody, 'open_owned', side_effect=replace_then_refuse):
                    with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-cleanup-refused'):
                        custody.seal_series(archive=a, evidence=e, scratch=scratch, output=output,
                                            identity=i, deadline_monotonic=time.monotonic() + 20)
                target = scratch if replaced == 'scratch' else output
                self.assertEqual(b'replacement', (target / 'unowned').read_bytes())
                other = output if replaced == 'scratch' else scratch
                self.assertFalse(other.exists())

    def test_actual_two_byte_archive_and_evidence_only_are_closed(self):
        for has_archive in (True, False):
            with self.subTest(has_archive=has_archive), tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                a, e, i, s, o = setup(root, b'ab' if has_archive else None)
                r = custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 30)
                self.assertEqual('sealed-complete' if has_archive else 'sealed-evidence-only', r.outcome)
                self.assertEqual(has_archive, r.archiveComplete)
                self.assertFalse(s.exists())
                self.assertEqual(r, custody.verify_staging(o, i))
                self.assertEqual({'manifest.json', 'evidence.capsule.json'} | ({'oci-0000.capsule.json'} if has_archive else set()), {p.name for p in o.iterdir()})
                manifest = json.loads((o / 'manifest.json').read_text())
                if has_archive:
                    self.assertEqual({'sha256': custody.shab(b'ab'), 'bytes': 2, 'chunkCount': 1},
                                     manifest['archive'])
                else:
                    self.assertIsNone(manifest['archive'])

    def test_directory_lease_file_descriptors_close_on_success_and_refusal(self):
        before = set(os.listdir('/proc/self/fd'))
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, scratch, output = setup(root)
            custody.seal_series(archive=a, evidence=e, scratch=scratch, output=output,
                                identity=i, deadline_monotonic=time.monotonic() + 20)
        self.assertEqual(before, set(os.listdir('/proc/self/fd')))
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, scratch, output = setup(root)
            output.mkdir(mode=0o700)
            owned(output / 'unowned', b'unowned')
            with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-path-refused'):
                custody.seal_series(archive=a, evidence=e, scratch=scratch, output=output,
                                    identity=i, deadline_monotonic=time.monotonic() + 20)
        self.assertEqual(before, set(os.listdir('/proc/self/fd')))

    def test_actual_boundary_and_remainder_descriptors_reconstruct_without_padding(self):
        for size, expected_sizes in ((custody.CHUNK_BYTES, [custody.CHUNK_BYTES]),
                                     (custody.CHUNK_BYTES + 1, [custody.CHUNK_BYTES - 1, 2])):
            with self.subTest(size=size), tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                data = b'a' * size
                a, e, i, s, o = setup(root, data)
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 40)
                m = json.loads((o / 'manifest.json').read_text())
                ds = [x['descriptor'] for x in m['files'][:-1]]
                self.assertEqual(expected_sizes, [x['bytes'] for x in ds])
                self.assertEqual(size, sum((x['bytes'] for x in ds)))
                self.assertEqual(list(range(len(ds))), [x['index'] for x in ds])

    def test_refuses_identity_extra_old_binding_and_nonowned_modes(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, s, o = setup(root)
            i['extra'] = 1
            with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-identity-refused'):
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 20)
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, s, o = setup(root)
            e.chmod(420)
            with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-source-refused'):
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 20)

    def test_child_failure_timeout_and_short_capacity_leave_no_partial_series(self):
        cases = [subprocess.CompletedProcess([], 9, b'', b'failure'), subprocess.TimeoutExpired([], 1)]
        for effect in cases:
            with self.subTest(effect=type(effect).__name__), tempfile.TemporaryDirectory() as d:
                root = pathlib.Path(d)
                a, e, i, s, o = setup(root)
                patch = mock.patch.object(custody.subprocess, 'run', side_effect=effect) if isinstance(effect, Exception) else mock.patch.object(custody.subprocess, 'run', return_value=effect)
                with patch, self.assertRaises(custody.CustodyRefusal):
                    custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 20)
                self.assertFalse(s.exists())
                self.assertFalse(o.exists())
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, s, o = setup(root)
            fake = os.statvfs(root)._replace(f_bavail=0) if hasattr(os.statvfs(root), '_replace') else None
            with mock.patch.object(custody, 'space', side_effect=custody.CustodyRefusal('custody-capacity-refused')), self.assertRaisesRegex(custody.CustodyRefusal, 'custody-capacity-refused'):
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 20)
            self.assertFalse(o.exists())

    def test_verify_refuses_reorder_gap_corruption_extra_and_link(self):

        def prepared(root):
            a, e, i, s, o = setup(root, b'abc')
            custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 30)
            return (i, o)
        for mode in ('reorder', 'gap', 'descriptor', 'aad', 'capsule', 'extra', 'link'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as d:
                i, o = prepared(pathlib.Path(d))
                mp = o / 'manifest.json'
                m = json.loads(mp.read_text())
                if mode == 'reorder':
                    m['files'].reverse()
                    mp.write_bytes(custody.canonical(m))
                elif mode == 'gap':
                    m['files'].pop(0)
                    mp.write_bytes(custody.canonical(m))
                elif mode == 'descriptor':
                    m['files'][0]['descriptor']['offset'] = 1
                    mp.write_bytes(custody.canonical(m))
                elif mode == 'aad':
                    item = m['files'][0]
                    capsule = o / item['capsuleName']
                    value = json.loads(capsule.read_text())
                    value['aadSha256'] = '0' * 64
                    raw = custody.canonical(value)
                    capsule.write_bytes(raw)
                    item['capsuleBytes'] = len(raw)
                    item['capsuleSha256'] = custody.shab(raw)
                    mp.write_bytes(custody.canonical(m))
                elif mode == 'capsule':
                    (o / m['files'][0]['capsuleName']).write_bytes(b'corrupt')
                elif mode == 'extra':
                    owned(o / 'extra', b'x')
                else:
                    (o / 'extra').symlink_to(o / 'manifest.json')
                with self.assertRaises(custody.CustodyRefusal):
                    custody.verify_staging(o, i)

    def test_source_change_after_seal_refuses_and_cleans(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, s, o = setup(root, b'abc')
            real = custody.seal_one

            def mutate(*args, **kwargs):
                result = real(*args, **kwargs)
                if args[2] == 'oci':
                    with a.open('r+b') as f:
                        f.seek(0)
                        f.write(b'z')
                        f.flush()
                        os.fsync(f.fileno())
                return result
            with mock.patch.object(custody, 'seal_one', side_effect=mutate), self.assertRaisesRegex(custody.CustodyRefusal, 'custody-file-changed'):
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() + 30)
            self.assertFalse(o.exists())

    def test_expired_deadline_refuses_before_directories(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            a, e, i, s, o = setup(root)
            with self.assertRaisesRegex(custody.CustodyRefusal, 'custody-deadline-refused'):
                custody.seal_series(archive=a, evidence=e, scratch=s, output=o, identity=i, deadline_monotonic=time.monotonic() - 1)
            self.assertFalse(s.exists())
            self.assertFalse(o.exists())

class SourceTests(unittest.TestCase):

    def test_public_tool_key_bytes_and_module_boundaries(self):
        self.assertEqual((custody.SEALER_SHA256, len((ROOT / 'eng/fourd-public-provider/seal_native_custody.mjs').read_bytes())), custody.shap(ROOT / 'eng/fourd-public-provider/seal_native_custody.mjs'))
        self.assertEqual(custody.PUBLIC_KEY_SHA256, custody.shap(ROOT / 'eng/fourd-public-provider/root-public.pem')[0])
        source = (ROOT / 'eng/fourd-public-provider/custody.py').read_text()
        for forbidden in ('requests', 'urllib', 'socket', 'podman', 'private key'):
            self.assertNotIn(forbidden, source.lower())
if __name__ == '__main__':
    unittest.main()
