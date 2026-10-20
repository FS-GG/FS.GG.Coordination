#!/usr/bin/env python3
"""Public synthetic BAR source controls. No private input or native engine runs.

The existing fixture supplies temporary Git repositories and inert native ports;
only genuine small Python capture children and Git execute. Actual BAR production
selection, controller, OCI reader, source guards and original clock are exercised.
"""
import copy
from contextlib import ExitStack
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]

def load(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

FIXTURE = load('bar_existing_source_fixtures', Path(__file__).with_name('test_sc2_profile.py'))
BAR = load('bar_source_controls', ROOT / 'eng/portable_workspace_bar.py')
IMAGE, COMMON = FIXTURE.IMAGE, FIXTURE.COMMON
SHARED = BAR.shared
sha = FIXTURE.digest

@unittest.skipUnless(sys.platform == 'linux', 'closed profile requires Linux')
class BarProfile(unittest.TestCase):
    def setUp(self):
        self.base = FIXTURE.Sc2Profile()
        self.base.setUp()
        self.addCleanup(self.base.doCleanups)
        self.addCleanup(self.base.tearDown)
        self.root = self.base.root
        for module in (BAR, SHARED):
            for key, value in (('SUPPORT_LIBRARIES', self.base.library_aliases),
                               ('SUPPORT_EXECUTABLES', self.base.executable_paths)):
                change = patch.object(module, key, value)
                change.start(); self.addCleanup(change.stop)

    def fixture(self):
        args, selection, home = self.base.fixture()
        helper, product = Path(args.source), Path(args.image_source)
        (helper / 'eng/portable_workspace_bar.py').write_bytes((ROOT / 'eng/portable_workspace_bar.py').read_bytes())
        (helper / 'eng/portable_workspace_bar.py').chmod(0o600)
        for child in product.iterdir():
            if child.name != '.git':
                shutil.rmtree(child) if child.is_dir() else child.unlink()
        payloads = {'engine': b'synthetic headless bytes', 'map': b'synthetic map', 'game': b'synthetic sdp'}
        resources = {'toolchain': {'parentOsCandidate': BAR.BASE_IMAGE},
                     'engine': {'headlessSha256': sha(payloads['engine'])},
                     'map': {'file': 'synthetic.sd7', 'sha256': sha(payloads['map'])},
                     'game': {'sdp': 'a' * 32 + '.sdp', 'sdpSha256': sha(payloads['game'])}}
        for name in BAR.PRODUCT_FILES:
            target = product / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(('synthetic recipe ' + name + '\n').encode()); target.chmod(0o600)
        recipe = product / 'containers/barc-stock-headless/Containerfile'
        recipe.write_text('FROM ' + BAR.BASE_IMAGE + ' AS toolchain\nFROM toolchain AS development\nFROM development AS headless-integration\n')
        (recipe.parent / 'resources.json').write_bytes(SHARED.canonical(resources))
        def git(root, *argv):
            return IMAGE.run(['/usr/bin/git', '-C', str(root), *argv], timeout_seconds=10,
                capture_limit_bytes=1024**2, environment={'PATH': '/usr/bin:/bin', 'HOME': str(home),
                'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}).stdout.strip()
        for key, root, names in (('helper', helper, BAR.HELPER_FILES), ('product', product, BAR.PRODUCT_FILES)):
            git(root, 'add', '-A')
            git(root, '-c', 'user.name=Synthetic fixture', '-c', 'user.email=fixture@example.invalid',
                '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Synthetic BAR source selection')
            selection[key].update(revision=git(root, 'rev-parse', 'HEAD'), tree=git(root, 'rev-parse', 'HEAD^{tree}'),
                                  files={name: sha((root / name).read_bytes()) for name in names})
        selection['schema'] = 'fsgg.bar-headless-operation/1'
        selection['product']['baseImage'] = BAR.BASE_IMAGE
        selection['load']['root'] = str(home / '.local/share/fsgg/headless-images/bar' / selection['operationId'])
        Path(selection['load']['root']).mkdir(parents=True, mode=0o700)
        for key in ('build', 'load'):
            store = selection[key]
            paths, _, configs = BAR.namespace(Path(args.engine_allocation) / key, Path(store['root']), Path(store['runroot']))
            for name, content in configs.items():
                target = paths['config'] / 'containers' / name
                target.write_bytes(SHARED.canonical(content) if name.endswith('.json') else FIXTURE.toml_document(content))
        args.expected_source_revision = selection['helper']['revision']
        args.expected_image_source_revision = selection['product']['revision']
        self.base.rebind_selection(args, selection)
        self.payloads = payloads
        return args, selection, home

    def image(self, args, *, missing=None, changed=None, fifo=False):
        with patch.object(Path, 'home', return_value=self.root / 'home'):
            checked = BAR.check_inputs(args, IMAGE.__dict__)
        policy = BAR.oci_policy(checked)
        files = {'recipe/' + Path(name).name: (Path(args.image_source) / name).read_bytes()
                 for name in BAR.PRODUCT_FILES}
        files.update({'opt/barc/resources.json': (Path(args.image_source) / 'containers/barc-stock-headless/resources.json').read_bytes(),
            'opt/barc/entrypoint.sh': (Path(args.image_source) / 'containers/barc-stock-headless/entrypoint.sh').read_bytes(),
            'opt/barc/engine/spring-headless': self.payloads['engine'],
            'opt/barc/data/maps/synthetic.sd7': self.payloads['map'],
            'opt/barc/data/packages/' + 'a' * 32 + '.sdp': self.payloads['game']})
        files.update({name: ('synthetic output ' + name).encode() for name in policy['requiredRegularFiles']})
        if missing: files.pop(missing)
        if changed: files[changed[0]] = changed[1]
        rows = [(name, 'file', value) for name, value in files.items()]
        if fifo: rows.append(('tmp/clr-debug-pipe-7-123-in', 'fifo', b''))
        archive = self.root / 'synthetic-built.oci.tar'
        with patch.object(COMMON.IMAGE, 'COMMON_IMAGE_NAME', checked['selection']['product']['exportName']):
            _, image_id, config = COMMON.synthetic_common_archive(archive, b'synthetic 1\n', final=rows,
                config_update={'User': '10001:10001', 'WorkingDir': '/work',
                               'Entrypoint': ['bash', '/opt/barc/entrypoint.sh'], 'Cmd': []})
        with tarfile.open(archive, 'r:') as data:
            native_config = json.load(data.extractfile('blobs/sha256/' + image_id[7:]))
        return {'archive': archive, 'id': image_id, 'policy': policy,
                'inspect': {'Id': image_id, 'Os': 'linux', 'Architecture': 'amd64', 'Config': config,
                            'RootFS': {'Layers': native_config['rootfs']['diff_ids']}}}

    def ports(self, args, selection, image=None, fail=None, bad_mapping=False, existing_image=False):
        spawn, calls, resources = self.base.native_ports(args, selection, image=image, fail=fail)
        real_popen = subprocess.Popen
        def invoke(argv, **kwargs):
            if 'unshare' in argv and '-c' in argv and '"euid"' not in argv[-1]:
                calls.append(argv.copy())
                rows = '0 1000 1\n1 100000 ' + ('2000' if bad_mapping else '65536') + '\n'
                value = json.dumps({'uidMap': rows, 'gidMap': rows})
                return real_popen([sys.executable, '-I', '-c', 'print(' + repr(value) + ')'], **kwargs)
            if existing_image and 'images' in argv:
                calls.append(argv.copy())
                value = json.dumps([{'Id': 'sha256:' + 'b' * 64, 'Names': ['localhost/foreign:old']}])
                return real_popen([sys.executable, '-I', '-c', 'print(' + repr(value) + ')'], **kwargs)
            return spawn(argv, **kwargs)
        return invoke, calls, resources

    def native_context(self, home, spawn, resources):
        stack = ExitStack()
        for value in (patch.object(Path, 'home', return_value=home),
                      patch.object(SHARED, 'host_resources', return_value=resources),
                      patch.object(SHARED, 'disk_backing', return_value='synthetic disk'),
                      patch.object(os, 'sched_setaffinity'),
                      patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}),
                      patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn)):
            stack.enter_context(value)
        return stack

    def test_actual_check_inputs_is_inert_and_full_helper_closure_is_bound(self):
        args, selection, home = self.fixture()
        with patch.object(Path, 'home', return_value=home):
            result = BAR.main(args, IMAGE.__dict__)
            self.assertFalse(result['nativeExecutionEnabled'])
            self.assertEqual(result['commands']['pull'][-1], BAR.BASE_IMAGE)
            build = result['commands']['build']
            self.assertIn('--target=headless-integration', build)
            self.assertIn('--jobs=1', build)
            self.assertIn('--retry=0', build)
            self.assertFalse(any('build-arg' in value for value in build))
            self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())
            selection['helper']['files'].pop('eng/portable_workspace_sc2.py')
            self.base.rebind_selection(args, selection)
            with self.assertRaisesRegex(RuntimeError, 'helper pin set'):
                BAR.main(args, IMAGE.__dict__)

    def test_wrong_product_and_arbitrary_selection_extensions_refuse(self):
        args, selection, home = self.fixture()
        for mutate in (lambda s: s.update(schema='fsgg.sc2-headless-operation/1'),
                       lambda s: s.update(command=['bash']),
                       lambda s: s.update(storageReservation={}),
                       lambda s: s['product'].update(baseImage=FIXTURE.TEST_BASE_IMAGE),
                       lambda s: s['clock'].update(workEnd=s['clock']['workEnd'] + 1)):
            wrong = copy.deepcopy(selection); mutate(wrong); self.base.rebind_selection(args, wrong)
            with patch.object(Path, 'home', return_value=home), self.assertRaises(RuntimeError):
                BAR.main(args, IMAGE.__dict__)
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_dirty_source_and_nonfresh_store_refuse_before_consumption(self):
        args, selection, home = self.fixture()
        dirty = Path(args.image_source) / 'unselected'; dirty.write_bytes(b'x')
        with patch.object(Path, 'home', return_value=home):
            with self.assertRaisesRegex(RuntimeError, 'dirty'):
                BAR.main(args, IMAGE.__dict__)
            dirty.unlink()
            for kind in ('build', 'load'):
                for key in ('root', 'runroot'):
                    occupied = Path(selection[kind][key]) / 'old-generation'; occupied.write_bytes(b'x')
                    with self.subTest(kind=kind, path=key), self.assertRaisesRegex(RuntimeError, 'stores and runroots must be empty'):
                        BAR.main(args, IMAGE.__dict__)
                    occupied.unlink()
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_selected_bar_input_cannot_be_used_by_sc2(self):
        args, selection, home = self.fixture()
        with patch.object(Path, 'home', return_value=home), self.assertRaises(RuntimeError):
            SHARED.main(args, IMAGE.__dict__)

    def test_actual_qualify_records_outputs_then_loads_exact_image_and_cannot_replay(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        image = self.image(args)
        spawn, calls, resources = self.ports(args, selection, image)
        with self.native_context(home, spawn, resources):
            result = BAR.main(args, IMAGE.__dict__)
            count = len(calls)
            with self.assertRaises(FileExistsError):
                BAR.main(args, IMAGE.__dict__)
            self.assertEqual(count, len(calls))
        self.assertEqual(result['imageId'], image['id'])
        self.assertEqual(len(result['producedOutputs']), 8)
        self.assertFalse(result['productRuntimeAcceptance'])
        records = [json.loads(row) for row in Path(result['journal']).read_bytes().splitlines()]
        self.assertEqual(records[0]['profile'], BAR.PROFILE)
        verified = next(i for i, row in enumerate(records) if row['event'] == 'archive-verified')
        loaded = next(i for i, row in enumerate(records) if row['event'] == 'command-intent' and row['name'] == 'verified-load')
        self.assertLess(verified, loaded)
        self.assertEqual(sum('build' in argv for argv in calls), 1)
        self.assertEqual(sum('load' in argv for argv in calls), 1)
        self.assertFalse(any('run' in argv or 'create' in argv or 'prune' in argv for argv in calls))

    def test_effective_image_asset_and_output_failures_block_load(self):
        for mode in ('missing', 'empty', 'asset', 'fifo'):
            with self.subTest(mode=mode), ExitStack() as stack:
                temporary = stack.enter_context(FIXTURE.tempfile.TemporaryDirectory(dir=self.root))
                oldroot = self.base.root; self.base.root = self.root = Path(temporary)
                try:
                    args, selection, home = self.fixture(); args.mode = 'qualify'
                    plugin = 'opt/barc/plugin/libSkirmishAI.so'
                    options = {'missing': {'missing': plugin}, 'empty': {'changed': (plugin, b'')},
                               'asset': {'changed': ('opt/barc/engine/spring-headless', b'wrong')}, 'fifo': {'fifo': True}}[mode]
                    image = self.image(args, **options)
                    spawn, calls, resources = self.ports(args, selection, image)
                    with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError,
                        {'missing': 'output missing', 'empty': 'output missing',
                         'asset': 'source/input bytes mismatch', 'fifo': 'special/sparse'}[mode]):
                        BAR.main(args, IMAGE.__dict__)
                    self.assertFalse(any('load' in argv or 'tag' in argv for argv in calls))
                finally:
                    self.base.root = self.root = oldroot

    def test_bar_mapping_and_existing_load_image_block_build(self):
        for failure in ('bad_mapping', 'existing_image'):
            with self.subTest(failure=failure), FIXTURE.tempfile.TemporaryDirectory(dir=self.root) as temporary:
                oldroot = self.base.root; self.base.root = self.root = Path(temporary)
                try:
                    args, selection, home = self.fixture(); args.mode = 'qualify'
                    spawn, calls, resources = self.ports(args, selection, **{failure: True})
                    with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError,
                        'UID/GID10001' if failure == 'bad_mapping' else 'already has images'):
                        BAR.main(args, IMAGE.__dict__)
                    self.assertFalse(any('pull' in argv or 'build' in argv for argv in calls))
                finally:
                    self.base.root = self.root = oldroot

    def test_resource_conflict_keeps_original_controller_floor(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        spawn, calls, resources = self.ports(args, selection)
        resources['hostMemoryAvailableBytes'] -= 1
        with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError, 'memory'):
            BAR.main(args, IMAGE.__dict__)
        self.assertFalse(any('info' in argv or 'pull' in argv or 'build' in argv for argv in calls))

    def test_each_native_failure_retains_first_cause_without_retry_or_dependents(self):
        for stage, dependent in (('pull', 'build'), ('build', 'push'), ('push', 'load'),
                                 ('load', 'tag'), ('inspect', 'tag'), ('tag', None)):
            with self.subTest(stage=stage), FIXTURE.tempfile.TemporaryDirectory(dir=self.root) as temporary:
                oldroot = self.base.root; self.base.root = self.root = Path(temporary)
                try:
                    args, selection, home = self.fixture(); args.mode = 'qualify'
                    image = self.image(args)
                    spawn, calls, resources = self.ports(args, selection, image, fail=stage)
                    with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError, 'exit 23'):
                        BAR.main(args, IMAGE.__dict__)
                    self.assertEqual(sum(stage in argv for argv in calls), 1)
                    if dependent:
                        self.assertFalse(any(dependent in argv for argv in calls))
                    journal = Path(args.state_dir) / selection['operationId'] / 'journal.jsonl'
                    records = [json.loads(row) for row in journal.read_bytes().splitlines()]
                    self.assertEqual(records[-1]['outcome'], 'failed')
                    self.assertIn('exit 23', records[-1]['firstCause'])
                    self.assertFalse(any('prune' in argv or 'rm' in argv for argv in calls))
                finally:
                    self.base.root = self.root = oldroot

    def test_original_expired_clock_cannot_be_reset_by_qualify(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        for key in ('start', 'workEnd', 'totalEnd'):
            selection['clock'][key] -= 4000
        self.base.rebind_selection(args, selection)
        spawn, calls, resources = self.ports(args, selection)
        with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError, 'original|deadline'):
            BAR.main(args, IMAGE.__dict__)
        self.assertEqual(calls, [])

    def test_bar_config_accepts_empty_cmd_and_refuses_inherited_shell_and_sc2(self):
        config = {'Os': 'linux', 'Architecture': 'amd64', 'Config': {'User': '10001:10001',
            'WorkingDir': '/work', 'Entrypoint': ['bash', '/opt/barc/entrypoint.sh'], 'Cmd': []}}
        BAR.verify_image_config(config)
        config['Config']['Cmd'] = None; BAR.verify_image_config(config)
        for mutate in (lambda c: c.update(Cmd=['bash']), lambda c: c.update(User='1654:1654'),
                       lambda c: c.update(Volumes={'/work': {}})):
            wrong = copy.deepcopy(config); mutate(wrong['Config'])
            with self.assertRaises(RuntimeError):
                BAR.verify_image_config(wrong)

    def test_both_captured_internal_sources_must_match_before_git_or_native_reads(self):
        args, selection, home = self.fixture()
        for key in ('_BOOTSTRAP_SOURCE_SHA256', 'SHARED_SOURCE_SHA256'):
            with self.subTest(source=key), patch.object(Path, 'home', return_value=home), \
                 patch.dict(BAR.__dict__, {key: 'f' * 64}), \
                 patch.object(IMAGE.subprocess, 'Popen') as launched:
                with self.assertRaisesRegex(RuntimeError, 'captured.*differs'):
                    BAR.main(args, IMAGE.__dict__)
                launched.assert_not_called()

    def test_cli_bypasses_valid_caches_for_both_internal_source_modules(self):
        helper = self.root / 'cache-helper'; helper.mkdir()
        retained = {}
        for name in ('portable-workspace-image.py', 'portable_workspace_bar.py', 'portable_workspace_sc2.py'):
            target = helper / name; shutil.copyfile(ROOT / 'eng' / name, target)
            if name == 'portable-workspace-image.py':
                continue
            cache = Path(importlib.util.cache_from_source(str(target))); cache.parent.mkdir(exist_ok=True)
            info = target.stat()
            poisoned = (importlib.util.MAGIC_NUMBER + FIXTURE.struct.pack('<III', 0, int(info.st_mtime), info.st_size)
                        + FIXTURE.marshal.dumps(compile('raise RuntimeError("cached code executed")', str(target), 'exec')))
            cache.write_bytes(poisoned); retained[cache] = poisoned
            cached = importlib.machinery.SourceFileLoader('cached_profile', str(target))
            with self.assertRaisesRegex(RuntimeError, 'cached code executed'):
                exec(cached.get_code('cached_profile'), {})
        result = subprocess.run([sys.executable, '-I', str(helper / 'portable-workspace-image.py'),
            'check-inputs', '--profile=bar-headless', '--source=' + str(helper),
            '--expected-source-revision=' + 'a' * 40, '--state-dir=' + str(self.root),
            '--root=' + str(self.root), '--runroot=' + str(self.root)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(b'product source/revision required', result.stderr)
        self.assertNotIn(b'cached code executed', result.stderr)
        self.assertTrue(all(cache.read_bytes() == content for cache, content in retained.items()))

    def test_actual_cli_wrong_product_refuses_without_creating_state_or_bytecode(self):
        args, selection, home = self.fixture()
        selection['schema'] = 'wrong-product'; self.base.rebind_selection(args, selection)
        result = subprocess.run([sys.executable, '-I', '-B', str(Path(args.source) / 'eng/portable-workspace-image.py'),
            'check-inputs', '--profile', BAR.PROFILE, '--source', args.source,
            '--expected-source-revision', args.expected_source_revision, '--image-source', args.image_source,
            '--expected-image-source-revision', args.expected_image_source_revision,
            '--operation-input', args.operation_input, '--expected-operation-input-sha256', args.expected_operation_input_sha256,
            '--state-dir', args.state_dir, '--root', args.root, '--runroot', args.runroot],
            capture_output=True, timeout=10, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('BAR operation schema', result.stderr)
        self.assertFalse(list(Path(args.source).rglob('__pycache__')))
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

if __name__ == '__main__':
    unittest.main()
