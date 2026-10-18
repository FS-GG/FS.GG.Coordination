#!/usr/bin/env python3
"""Synthetic closed-profile controls; no engine, image or private product fixture.

The capture controls use genuine small Python children under the source-test
envelope. They establish only the declared process/capture contract, not native
Buildah/Podman qualification or product acceptance.
"""

import copy
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
import shutil
import subprocess
import tarfile
import selectors
import io
from unittest.mock import patch, Mock


ROOT = Path(__file__).resolve().parents[3]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


IMAGE = load('sc2_image_entry', ROOT / 'eng/portable-workspace-image.py')
SC2 = load('sc2_profile_controls', ROOT / 'eng/portable_workspace_sc2.py')
COMMON = load('sc2_shared_archive_fixtures', ROOT / 'tests/portable-workspace/image/test_common_arch_inputs.py')
TEST_EXPORT_NAME = 'synthetic-sc2'
TEST_LOCAL_TAG = 'localhost/synthetic-sc2:source'
TEST_BASE_IMAGE = 'mcr.microsoft.com/dotnet/sdk@sha256:' + 'a' * 64


def digest(value):
    return hashlib.sha256(value).hexdigest()


def toml_document(value):
    """Only the finite scalar/table shapes used by the actual profile fixture."""
    lines = []
    def table(row, prefix=''):
        for name, content in row.items():
            if not isinstance(content, dict) and not (isinstance(content, list) and content and isinstance(content[0], dict)):
                lines.append(name + '=' + json.dumps(content))
        for name, content in row.items():
            target = prefix + '.' + name if prefix else name
            if isinstance(content, dict):
                lines.append('[' + target + ']'); table(content, target)
            elif isinstance(content, list) and content and isinstance(content[0], dict):
                for item in content:
                    lines.append('[[' + target + ']]'); table(item, target)
    table(value)
    return ('\n'.join(lines) + '\n').encode()


@unittest.skipUnless(sys.platform == 'linux', 'closed profile requires Linux')
class Sc2Profile(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.root.chmod(0o700)
        # Public source tests need no installed engine/native library. These
        # injected fixed support paths belong ONLY to the declared synthetic
        # native-port contract, never to a production selection or native proof.
        self.library_roles = tuple(SC2.SUPPORT_LIBRARIES)
        self.library_aliases = {}
        self.executable_paths = dict(SC2.SUPPORT_EXECUTABLES)
        libraries = patch.object(SC2, 'SUPPORT_LIBRARIES', self.library_aliases)
        executables = patch.object(SC2, 'SUPPORT_EXECUTABLES', self.executable_paths)
        libraries.start(); executables.start()
        self.addCleanup(libraries.stop); self.addCleanup(executables.stop)

    def tearDown(self):
        self.temporary.cleanup()

    def fixture(self):
        """Actual check-inputs contract with synthetic product and local Git only.

        No source fixture from the private product, no native engine execution.
        Support pins bind ONLY the declared inert native-port fixture.
        """
        home = self.root / 'home'; home.mkdir(mode=0o700)
        allocation = self.root / 'allocation'; allocation.mkdir(mode=0o700)
        helper, product = self.root / 'helper', allocation / 'context'
        helper.mkdir(mode=0o700); product.mkdir(mode=0o700)
        def write(target, data):
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data); target.chmod(0o600)
        for name in ('eng/portable-workspace-image.py', 'eng/portable_workspace_sc2.py'):
            write(helper / name, (ROOT / name).read_bytes())
        for name in SC2.PRODUCT_FILES:
            write(product / name, ('synthetic closed product fixture: ' + name + '\n').encode())
        write(product / 'containers/Containerfile',
              ('FROM --platform=linux/amd64 ' + TEST_BASE_IMAGE + ' AS product\nFROM product AS headless\n').encode())
        def git(root, *argv):
            return IMAGE.run(['/usr/bin/git', '-C', str(root), *argv], timeout_seconds=10,
                             capture_limit_bytes=1024**2, environment={'PATH': '/usr/bin:/bin',
                             'HOME': str(home), 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}).stdout.strip()
        def commit(root, names):
            git(root, 'init', '-q'); git(root, 'add', '.')
            git(root, '-c', 'user.name=Synthetic fixture', '-c', 'user.email=fixture@example.invalid',
                '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Synthetic source input')
            return {'revision': git(root, 'rev-parse', 'HEAD'), 'tree': git(root, 'rev-parse', 'HEAD^{tree}'),
                    'files': {name: digest((root / name).read_bytes()) for name in names}}
        sources = {'helper': commit(helper, ('eng/portable-workspace-image.py', 'eng/portable_workspace_sc2.py')),
                   'product': commit(product, SC2.PRODUCT_FILES)}
        sources['product'].update(baseImage=TEST_BASE_IMAGE, localTag=TEST_LOCAL_TAG, exportName=TEST_EXPORT_NAME)
        state = allocation / 'state'; state.mkdir(mode=0o700)
        stores = {'build': {'root': str(allocation / 'build/graphroot'), 'runroot': str(allocation / 'build/runroot')},
                  'load': {'root': str(home / '.local/share/fsgg/headless-images/sc2'),
                           'runroot': str(allocation / 'load/runroot')}}
        for key, store in stores.items():
            root, runtime = Path(store['root']), Path(store['runroot'])
            root.mkdir(parents=True, mode=0o700); runtime.mkdir(parents=True, mode=0o700)
            paths, _, configs = SC2.namespace(allocation / key, root, runtime)
            for target in paths.values():
                target.mkdir(parents=True, exist_ok=True, mode=0o700); target.chmod(0o700)
            for name, content in configs.items():
                write(paths['config'] / 'containers' / name,
                      SC2.canonical(content) if name.endswith('.json') else toml_document(content))
        builder = self.root / 'synthetic-builder'; write(builder, b'inert synthetic support; never execute\n')
        builder.chmod(0o700)
        for role in ('podman', 'crun', 'conmon'):
            target = self.root / ('synthetic-' + role)
            write(target, ('inert synthetic ' + role + '\n').encode())
            target.chmod(0o700)
            self.executable_paths[role] = str(target)
        for role in self.library_roles:
            target = self.root / ('synthetic-' + role)
            write(target, ('inert synthetic library ' + role + '\n').encode())
            self.library_aliases[role] = str(target)
        support = {name: {'path': value, 'sha256': digest(Path(value).read_bytes())}
                   for name, value in self.executable_paths.items()}
        support.update({'builder': {'path': str(builder), 'sha256': digest(builder.read_bytes())},
                        'python': {'path': str(Path(sys.executable).resolve()),
                                   'sha256': digest(Path(sys.executable).resolve().read_bytes())}})
        support.update({role: {'path': str(Path(alias).resolve(strict=True)),
                               'sha256': digest(Path(alias).resolve(strict=True).read_bytes())}
                        for role, alias in SC2.SUPPORT_LIBRARIES.items()})
        topology, disposition = self.root / 'topology.json', self.root / 'disposition.json'
        write(topology, SC2.canonical({'synthetic': 'declared shared-host relation; no native admission'}))
        write(disposition, SC2.canonical({'sharedHostReservedMemoryBytes': 12 * 1024**3,
                                        'sharedHostReservedCpu': 6, 'sharedHostReservedDiskBytes': 0,
                                        'unquantifiedResourceConflicts': []}))
        now = time.monotonic()
        selection = {'operationId': 'synthetic-operation', **sources,
                     'allocation': {'path': str(allocation), 'identity': SC2.directory_pin(allocation)},
                     **stores, 'support': support, 'licenseAccepted': True,
                     'clock': {'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                               'start': now, 'workEnd': now + 3600, 'totalEnd': now + 3720},
                     'admission': {name: {'path': str(target), 'sha256': digest(target.read_bytes())}
                                   for name, target in (('topology', topology), ('resourceDisposition', disposition))},
                     'resourceDomain': {'relation': 'shared-host-reservations-outside-enclosing-cgroup',
                                        'topologySha256': digest(topology.read_bytes())}}
        selected = self.root / 'selection.json'; write(selected, SC2.canonical(selection))
        args = argparse.Namespace(mode='check-inputs', source=str(helper), image_source=str(product),
                                  expected_source_revision=sources['helper']['revision'],
                                  expected_image_source_revision=sources['product']['revision'],
                                  operation_input=str(selected), expected_operation_input_sha256=digest(selected.read_bytes()),
                                  state_dir=str(state), root=stores['build']['root'], runroot=stores['build']['runroot'],
                                  engine_allocation=str(allocation), engine_scratch_dir=str(allocation / 'build/download-tmp'),
                                  podman=self.executable_paths['podman'], acquisition_dir=None, expected_acquired_inputs_sha256=None)
        return args, selection, home

    def test_actual_check_inputs_is_inert_and_binds_complete_synthetic_sources(self):
        args, selection, home = self.fixture()
        with patch.object(Path, 'home', return_value=home):
            result = SC2.main(args, IMAGE.__dict__)
            self.assertFalse(result['nativeExecutionEnabled'])
            self.assertEqual(result['commands']['pull'][-1], TEST_BASE_IMAGE)
            self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())
            (Path(args.image_source) / 'hidden-untracked').write_bytes(b'must refuse')
            with self.assertRaisesRegex(RuntimeError, 'dirty'):
                SC2.main(args, IMAGE.__dict__)

    def test_current_source_rejects_embedded_origin_credentials_shallow_and_active_hooks(self):
        args, selection, home = self.fixture()
        config = Path(args.image_source) / '.git/config'
        original = config.read_bytes()
        with patch.object(Path, 'home', return_value=home):
            config.write_bytes(original + b'\n[remote "origin"]\nurl = https://github.com/Example/Synthetic.git\n')
            self.assertFalse(SC2.main(args, IMAGE.__dict__)['nativeExecutionEnabled'])
            config.write_bytes(original + b'\n[remote "origin"]\nurl = https://synthetic-not-a-secret@github.com/Example/Synthetic.git\n')
            with self.assertRaisesRegex(RuntimeError, 'credential-bearing'):
                SC2.main(args, IMAGE.__dict__)
            config.write_bytes(original)
            shallow = Path(args.image_source) / '.git/shallow'; shallow.write_text(selection['product']['revision'] + '\n')
            with self.assertRaisesRegex(RuntimeError, 'complete source history'):
                SC2.main(args, IMAGE.__dict__)
            shallow.unlink()
            hook = Path(args.image_source) / '.git/hooks/post-index-change'; hook.write_bytes(b'never execute\n')
            with self.assertRaisesRegex(RuntimeError, 'Git hook'):
                SC2.main(args, IMAGE.__dict__)
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_external_product_selection_cannot_override_recipe_base_or_remote_tag(self):
        args, selection, home = self.fixture()
        selected = Path(args.operation_input)
        with patch.object(Path, 'home', return_value=home):
            wrong = copy.deepcopy(selection)
            wrong['product']['baseImage'] = 'mcr.microsoft.com/dotnet/sdk@sha256:' + 'b' * 64
            selected.write_bytes(SC2.canonical(wrong)); args.expected_operation_input_sha256 = digest(selected.read_bytes())
            with self.assertRaisesRegex(RuntimeError, 'immutable base differs'):
                SC2.main(args, IMAGE.__dict__)
            wrong = copy.deepcopy(selection); wrong['product']['localTag'] = 'registry.example.invalid/not-local:tag'
            selected.write_bytes(SC2.canonical(wrong)); args.expected_operation_input_sha256 = digest(selected.read_bytes())
            with self.assertRaisesRegex(RuntimeError, 'text identity'):
                SC2.main(args, IMAGE.__dict__)
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def native_ports(self, args, selection, *, fail=None, image=None):
        """Controlled Popen boundary; engine argv are observed, never executed.

        Shared selector capture still drains genuine tiny Python child streams.
        Git source checks continue through the real Git executable.
        """
        real_popen = subprocess.Popen
        calls = []
        loaded = False
        def spawn(argv, **kwargs):
            nonlocal loaded
            if argv[0] == '/usr/bin/git':
                return real_popen(argv, **kwargs)
            calls.append(argv.copy())
            if fail and fail in argv:
                return real_popen([sys.executable, '-I', '-c', 'raise SystemExit(23)'], **kwargs)
            kind = 'load' if argv[0] == selection['support']['podman']['path'] else 'build'
            if '--version' in argv:
                output = 'buildah version 1.43.1'
            elif 'info' in argv:
                store = selection[kind]
                value = ({'store': {'GraphRoot': store['root'], 'RunRoot': store['runroot'], 'GraphDriverName': 'vfs'},
                          'host': {'os': 'linux', 'arch': 'amd64', 'rootless': True}} if kind == 'build' else
                         {'store': {'graphRoot': store['root'], 'runRoot': store['runroot'], 'graphDriverName': 'vfs'},
                          'host': {'os': 'linux', 'arch': 'amd64', 'security': {'rootless': True}}})
                output = json.dumps(value)
            elif 'du' in [Path(value).name for value in argv]:
                output = '\n'.join('1\t' + root for root in (args.engine_allocation, selection['load']['root']))
            elif 'unshare' in argv:
                output = json.dumps({'euid': 0, 'uidMap': '0 1000 1\n1 100000 65536\n',
                                     'gidMap': '0 1000 1\n1 100000 65536\n',
                                     'userNamespace': 100, 'mountNamespace': 101})
            elif 'containers' in argv:
                output = 'null'
            elif 'images' in argv:
                output = json.dumps([{'Id': image['id'], 'Names': [TEST_LOCAL_TAG]}] if loaded else [])
            elif 'ps' in argv:
                output = '[]'
            elif image is not None and 'build' in argv:
                target = next(value.split('=', 1)[1] for value in argv if value.startswith('--iidfile='))
                Path(target).write_text(image['id'] + '\n')
                output = 'synthetic build result'
            elif image is not None and 'pull' in argv:
                output = 'synthetic base result'
            elif image is not None and 'push' in argv:
                target = argv[-1].removeprefix('oci-archive:').removesuffix(':' + TEST_EXPORT_NAME)
                shutil.copyfile(image['archive'], target)
                output = 'synthetic exported archive'
            elif image is not None and 'load' in argv:
                loaded = True
                output = 'synthetic loaded image'
            elif image is not None and 'inspect' in argv:
                output = json.dumps([image['inspect']])
            elif image is not None and 'tag' in argv:
                output = ''
            else:
                raise AssertionError('Unexpected synthetic native invocation: ' + repr(argv))
            return real_popen([sys.executable, '-I', '-c', 'import os;os.write(1,' + repr(output.encode()) + ')'], **kwargs)
        resources = {'hostMemoryAvailableBytes': 26 * 1024**3, 'hostLogicalCpus': 12,
                     'affinity': [0, 1, 2, 3], 'enclosingMemoryCurrentBytes': 2 * 1024**3,
                     'enclosingMemoryMaxBytes': 32 * 1024**3, 'enclosingCpuCapacity': 12,
                     'enclosingTasksCurrent': 4, 'enclosingTasksMax': 2048,
                     'diskFreeBytes': 100 * 1024**3, 'loadStoreDiskFreeBytes': 100 * 1024**3,
                     'cgroup': 'synthetic declared test resource port'}
        return spawn, calls, resources

    def image_fixture(self, args):
        archive = self.root / 'synthetic-built.oci.tar'
        files = [('workspace/' + name, 'file', (Path(args.image_source) / name).read_bytes())
                 for name in SC2.PRODUCT_FILES]
        with patch.object(COMMON.IMAGE, 'COMMON_IMAGE_NAME', TEST_EXPORT_NAME):
            _, image_id, config = COMMON.synthetic_common_archive(archive, b'synthetic 1\n', final=files,
                config_update={'User': '1654:1654', 'Entrypoint': ['bash', 'scripts/sc2-container-preflight.sh'],
                               'Cmd': ['preflight']})
        with tarfile.open(archive, 'r:') as data:
            actual_config = json.load(data.extractfile('blobs/sha256/' + image_id[7:]))
        return {'archive': archive, 'id': image_id,
                'inspect': {'Id': image_id, 'Os': 'linux', 'Architecture': 'amd64', 'Config': config,
                            'RootFS': {'Layers': actual_config['rootfs']['diff_ids']}}}

    def test_actual_qualify_verifies_export_before_separate_load_and_tag(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        image = self.image_fixture(args)
        spawn, calls, resources = self.native_ports(args, selection, image=image)
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
             patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
            result = SC2.main(args, IMAGE.__dict__)
        self.assertEqual(result['imageId'], image['id'])
        self.assertEqual(result['archiveSha256'], digest(image['archive'].read_bytes()))
        self.assertFalse(result['productRuntimeAcceptance'])
        records = [json.loads(row) for row in Path(result['journal']).read_bytes().splitlines()]
        verified = next(i for i, row in enumerate(records) if row['event'] == 'archive-verified')
        load = next(i for i, row in enumerate(records) if row['event'] == 'command-intent' and row['name'] == 'verified-load')
        self.assertLess(verified, load)
        self.assertEqual(sum('build' in argv for argv in calls), 1)
        self.assertEqual(sum('load' in argv for argv in calls), 1)

    def test_bad_export_stops_load_and_retains_original_failed_attempt(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        image = self.image_fixture(args)
        image['archive'].write_bytes(b'not an OCI archive')
        spawn, calls, resources = self.native_ports(args, selection, image=image)
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
             patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
            with self.assertRaises(tarfile.ReadError):
                SC2.main(args, IMAGE.__dict__)
        self.assertFalse(any('load' in argv or 'tag' in argv for argv in calls))

    def test_each_native_stage_failure_blocks_dependents_without_retry(self):
        # Distinct original allocation per independent history. These are
        # synthetic transport failures, not native engine qualification.
        for stage, dependent in (('pull', 'build'), ('build', 'push'), ('push', 'load'),
                                 ('load', 'tag'), ('inspect', 'tag'), ('tag', None)):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory(dir=self.root) as temporary:
                original = self.root; self.root = Path(temporary); self.root.chmod(0o700)
                try:
                    args, selection, home = self.fixture(); args.mode = 'qualify'
                    image = self.image_fixture(args)
                    spawn, calls, resources = self.native_ports(args, selection, fail=stage, image=image)
                    with patch.object(Path, 'home', return_value=home), \
                         patch.object(SC2, 'host_resources', return_value=resources), \
                         patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
                         patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
                         patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
                        with self.assertRaisesRegex(RuntimeError, 'command failed with exit 23'):
                            SC2.main(args, IMAGE.__dict__)
                    self.assertEqual(sum(stage in argv for argv in calls), 1)
                    if dependent:
                        self.assertFalse(any(dependent in argv for argv in calls))
                    journal = Path(args.state_dir) / selection['operationId'] / 'journal.jsonl'
                    records = [json.loads(row) for row in journal.read_bytes().splitlines()]
                    self.assertEqual(records[-1]['outcome'], 'failed')
                    self.assertIn('exit 23', records[-1]['firstCause'])
                finally:
                    self.root = original

    def test_archive_replacement_after_real_verification_blocks_load(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        image = self.image_fixture(args)
        spawn, calls, resources = self.native_ports(args, selection, image=image)
        actual_inspect = IMAGE.inspect_oci_archive
        def replace(candidate, **kwargs):
            result = actual_inspect(candidate, **kwargs)
            candidate.write_bytes(candidate.read_bytes() + b'changed after actual validation')
            return result
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
             patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn), \
             patch.object(IMAGE, 'inspect_oci_archive', side_effect=replace):
            with self.assertRaisesRegex(RuntimeError, 'archive changed before load'):
                SC2.main(args, IMAGE.__dict__)
        self.assertFalse(any('load' in argv for argv in calls))

    def test_actual_preflight_uses_one_owner_and_consumed_original(self):
        args, selection, home = self.fixture(); args.mode = 'preflight'
        spawn, calls, resources = self.native_ports(args, selection)
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
             patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
            result = SC2.main(args, IMAGE.__dict__)
            self.assertEqual(result['preflight'], 'passed')
            self.assertFalse(any('pull' in argv or 'build' in argv or 'load' in argv for argv in calls))
            records = [json.loads(row) for row in Path(result['journal']).read_bytes().splitlines()]
            self.assertEqual(records[-1]['outcome'], 'passed-scoped')
            self.assertEqual(records[-1]['descendantSettlement'], 'unproved')
            count = len(calls)
            with self.assertRaises(FileExistsError):
                SC2.main(args, IMAGE.__dict__)
            self.assertEqual(len(calls), count)

    def test_failed_resource_admission_never_contacts_engine(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        spawn, calls, resources = self.native_ports(args, selection)
        resources['hostMemoryAvailableBytes'] -= 1
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
            with self.assertRaisesRegex(RuntimeError, 'shared host memory'):
                SC2.main(args, IMAGE.__dict__)
        self.assertEqual(calls, [])

    def test_full_resource_floor_family_blocks_dependent_engine_observations(self):
        cases = (('hostMemoryAvailableBytes', 26 * 1024**3 - 1, 'shared host memory'),
                 ('hostLogicalCpus', 9, 'shared host CPU'),
                 ('enclosingMemoryMaxBytes', 13 * 1024**3, 'enclosing memory'),
                 ('enclosingCpuCapacity', 3, 'enclosing CPU'),
                 ('affinity', [0, 1, 2], 'enclosing CPU'),
                 ('enclosingTasksMax', 515, 'task observation'),
                 ('diskFreeBytes', 80 * 1024**3 - 1, 'whole population disk'),
                 ('loadStoreDiskFreeBytes', 80 * 1024**3 - 1, 'load-store disk'))
        for key, value, expected in cases:
            with self.subTest(field=key), tempfile.TemporaryDirectory(dir=self.root) as temporary:
                original = self.root; self.root = Path(temporary); self.root.chmod(0o700)
                try:
                    args, selection, home = self.fixture(); args.mode = 'preflight'
                    spawn, calls, resources = self.native_ports(args, selection)
                    resources[key] = value
                    with patch.object(Path, 'home', return_value=home), \
                         patch.object(SC2, 'host_resources', return_value=resources), \
                         patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
                        with self.assertRaisesRegex(RuntimeError, expected):
                            SC2.main(args, IMAGE.__dict__)
                    self.assertEqual(calls, [])
                finally:
                    self.root = original

    def test_native_preflight_failure_preserves_first_cause_and_blocks_build(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        spawn, calls, resources = self.native_ports(args, selection, fail='info')
        with patch.object(Path, 'home', return_value=home), \
             patch.object(SC2, 'host_resources', return_value=resources), \
             patch.object(SC2, 'disk_backing', return_value='synthetic disk'), \
             patch.object(os, 'sched_setaffinity'), patch.object(os, 'sched_getaffinity', return_value={0, 1, 2, 3}), \
             patch.object(IMAGE.subprocess, 'Popen', side_effect=spawn):
            with self.assertRaisesRegex(RuntimeError, 'command failed with exit 23'):
                SC2.main(args, IMAGE.__dict__)
        self.assertFalse(any('pull' in argv or 'build' in argv for argv in calls))

    def owner(self, name='capture', *, work=3600):
        state = self.root / name
        state.mkdir(mode=0o700)
        now = time.monotonic()
        selection = {'operationId': name, 'helper': {'revision': 'a' * 40},
                     'product': {'revision': 'b' * 40},
                     'clock': {'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                               'start': now, 'workEnd': now + work, 'totalEnd': now + work + 120}}
        # This fixture directly supplies the explicitly injected owner contract;
        # it does not bypass or claim actual selected-input/native admission.
        checked = {'state': state, 'selection': selection, 'inputSha256': 'c' * 64,
                   'environments': {'build': {'PATH': '/usr/bin:/bin'}, 'load': {'PATH': '/usr/bin:/bin'}}}
        return SC2.Lifetime(None, IMAGE.__dict__, checked)

    def test_duplicate_nonfinite_and_unknown_selection_fields_refuse(self):
        for payload in (b'{"a":1,"a":2}', b'{"a":NaN}'):
            with self.subTest(payload=payload), self.assertRaises(RuntimeError):
                SC2.strict_json(payload)
        with self.assertRaises(RuntimeError):
            SC2.fields({'selected': 1, 'override': 2}, 'selected')

    def test_symlink_and_hardlink_inputs_refuse_before_read(self):
        source = self.root / 'source'
        source.write_bytes(b'declared synthetic bytes')
        linked = self.root / 'link'
        linked.symlink_to(source)
        with self.assertRaisesRegex(RuntimeError, 'symlink'):
            SC2.regular_bytes(linked, 100)
        linked.unlink()
        os.link(source, linked)
        with self.assertRaisesRegex(RuntimeError, 'linked'):
            SC2.regular_bytes(source, 100)

    def test_hash_read_cannot_bind_a_later_mutated_input_object(self):
        source = self.root / 'selected-input'
        source.write_bytes(b'original')
        original_read = SC2.regular_bytes
        def mutate_after_verified_read(*args, **kwargs):
            result = original_read(*args, **kwargs)
            source.write_bytes(b'mutated!')
            return result
        with patch.object(SC2, 'regular_bytes', side_effect=mutate_after_verified_read):
            with self.assertRaisesRegex(RuntimeError, 'changed before token binding'):
                SC2.pinned_token(source, 100, digest(b'original'))
        source.write_bytes(b'original')
        self.assertEqual(SC2.pinned_token(source, 100, digest(b'original')),
                         SC2.file_token(source))

    def test_streams_use_genuine_bytes_and_retained_command_identity(self):
        owner = self.owner()
        result = owner.execute('two-streams', [sys.executable, '-I', '-c',
                               'import os; os.write(1,b"out"); os.write(2,b"err")'], 5)
        self.assertEqual((result.returncode, result.stdout, result.stderr), (0, 'out', 'err'))
        self.assertEqual(owner.log_bytes, 6)
        slot = owner.commands[0]
        self.assertEqual(slot['state'], 'reaped-and-eof')
        self.assertGreater(slot['birth']['startTicks'], 0)
        self.assertEqual((owner.directory / '0000-two-streams.stdout').read_bytes(), b'out')
        records = [json.loads(row) for row in owner.journal.read_bytes().splitlines()]
        self.assertEqual([r['event'] for r in records],
                         ['run-start', 'command-intent', 'command-started', 'command-result'])
        self.assertEqual(records[-1]['descendantSettlement'], 'unproved')

    def test_combined_budget_is_shared_across_commands_not_renewed(self):
        with patch.object(SC2, 'LOG_BYTES', 64):
            owner = self.owner('flood')
            argv = [sys.executable, '-I', '-c', 'import os; os.write(1,b"x"*40)']
            owner.execute('first', argv, 5)
            with self.assertRaisesRegex(RuntimeError, 'operation combined log'):
                owner.execute('second', argv, 5)
            self.assertEqual(sum(p.stat().st_size for p in owner.directory.glob('*.stdout')), 64)
            self.assertIsNotNone(owner.first_cause)
            count = len(owner.commands)
            with self.assertRaises(RuntimeError):
                owner.execute('not-a-retry', argv, 5)
            self.assertEqual(len(owner.commands), count)

    def test_journal_substitution_refuses_without_overwriting_foreign_file(self):
        owner = self.owner('journal')
        foreign = self.root / 'foreign'
        foreign.write_bytes(b'preserve')
        owner.journal.unlink()
        owner.journal.symlink_to(foreign)
        with self.assertRaises(OSError):
            owner.record({'event': 'must-refuse'})
        self.assertEqual(foreign.read_bytes(), b'preserve')

    def test_expired_original_stops_before_any_command_intent(self):
        owner = self.owner('expired')
        with patch.object(owner, 'now', return_value=owner.clock['workEnd']):
            with self.assertRaisesRegex(RuntimeError, 'original work deadline'):
                owner.execute('never-launched', [sys.executable, '-I', '-c', 'raise SystemExit(91)'], 5)
        self.assertEqual(owner.commands, [])

    def test_ambiguous_spawn_preserves_consumed_intent_and_cannot_retry(self):
        owner = self.owner('ambiguous')
        with patch.object(IMAGE.subprocess, 'Popen', side_effect=OSError('declared injected spawn gap')):
            with self.assertRaisesRegex(OSError, 'spawn gap'):
                owner.execute('one-attempt', [sys.executable, '-I', '-c', 'pass'], 5)
        self.assertEqual(owner.commands[0]['state'], 'launch-unknown')
        self.assertIsNone(owner.commands[0]['birth'])
        with self.assertRaises(OSError):
            owner.execute('not-recovery', [sys.executable, '-I', '-c', 'pass'], 5)
        self.assertEqual(len(owner.commands), 1)

    def test_actual_capture_close_error_does_not_replace_command_failure(self):
        owner = self.owner('close-error')
        actual_selector = selectors.DefaultSelector
        class CloseFailure(actual_selector):
            def close(self):
                super().close()
                raise OSError('declared injected selector close failure')
        with patch.object(selectors, 'DefaultSelector', CloseFailure):
            with self.assertRaisesRegex(RuntimeError, 'command failed with exit 23') as caught:
                owner.execute('first-cause', [sys.executable, '-I', '-c', 'raise SystemExit(23)'], 5)
        self.assertIn('close', ' '.join(getattr(caught.exception, '__notes__', [])))
        self.assertIs(owner.first_cause, caught.exception)

    def test_missing_namespace_observation_keeps_actual_birth_eof_and_wait_separate(self):
        owner = self.owner('namespace-read-gap')
        actual_birth = SC2.birth
        def missing_namespace(pid, *, include_namespaces=True):
            if pid != os.getpid() and include_namespaces:
                raise FileNotFoundError('declared injected namespace read gap')
            return actual_birth(pid, include_namespaces=include_namespaces)
        with patch.object(SC2, 'birth', side_effect=missing_namespace):
            result = owner.execute('short-child', [sys.executable, '-I', '-c',
                                   'import os,time;os.write(1,b"actual bytes");time.sleep(0.02)'], 5)
        self.assertEqual(result.stdout, 'actual bytes')
        slot = owner.commands[0]
        self.assertGreater(slot['birth']['startTicks'], 0)
        self.assertNotIn('namespaces', slot['birth'])
        self.assertIn('no namespace claim', slot['namespaceObservation'])
        records = [json.loads(row) for row in owner.journal.read_bytes().splitlines()]
        self.assertTrue(records[-1]['directChildReaped'])
        self.assertTrue(records[-1]['bothStreamsEof'])
        self.assertEqual(records[-1]['descendantSettlement'], 'unproved')

    def test_replaced_kernel_birth_never_grants_signalling_authority(self):
        owner = self.owner('replaced-birth')
        slot = owner.command_intent([sys.executable, '-I', '-c', 'pass'], None,
                                    owner.checked['environments']['build'], 'declared-owned', False)
        previous = SC2.birth(os.getpid(), include_namespaces=False)
        slot['birth'] = previous; slot['pid'] = previous['pid']
        process = Mock(pid=previous['pid']); process.poll.return_value = None
        first = RuntimeError('original declared failure')
        with patch.object(SC2, 'birth', return_value={**previous, 'startTicks': previous['startTicks'] + 1}):
            owner.command_stop(slot, process, first)
        process.terminate.assert_not_called(); process.kill.assert_not_called()
        self.assertIs(owner.first_cause, first)
        self.assertIn('retirement unconfirmed', ' '.join(first.__notes__))

    def test_sample_gap_refuses_before_new_monitoring_command(self):
        owner = self.owner('sample-gap')
        owner.last_sample = owner.clock['start'] - 61
        with self.assertRaisesRegex(RuntimeError, 'observation gap'):
            owner.sample_if_due()
        self.assertEqual(owner.commands, [])

    def test_original_phase_exhausted_after_intent_cannot_launch(self):
        owner = self.owner('phase-intent')
        now = [owner.now()]
        actual_intent = owner.command_intent
        def intent(*args):
            value = actual_intent(*args); now[0] += 5
            return value
        with patch.object(owner, 'now', side_effect=lambda: now[0]), \
             patch.object(owner, 'command_intent', side_effect=intent), \
             patch.object(IMAGE.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(RuntimeError, 'before launch'):
                owner.execute('no-effect', [sys.executable, '-I', '-c', 'pass'], 5)
        launch.assert_not_called()
        self.assertEqual(owner.commands[0]['state'], 'not-launched')
        self.assertTrue(all(stream.closed for stream in owner.commands[0]['streams'].values()))

    def test_leader_exit_does_not_count_as_inherited_pipe_eof(self):
        owner = self.owner('held-pipe')
        actual_popen = subprocess.Popen
        read_fd, write_fd = os.pipe()
        delegated = False
        writer = actual_popen([sys.executable, '-I', '-c', 'import time;time.sleep(1)'],
                              stdout=write_fd, stderr=subprocess.DEVNULL, start_new_session=True)
        writer_birth = SC2.birth(writer.pid, include_namespaces=False)
        def launch(argv, **kwargs):
            nonlocal delegated
            kwargs['stdout'] = write_fd
            process = actual_popen(argv, **kwargs)
            process.stdout = os.fdopen(read_fd, 'rb', buffering=0)
            os.close(write_fd); delegated = True
            return process
        try:
            with patch.object(IMAGE.subprocess, 'Popen', side_effect=launch):
                with self.assertRaisesRegex(RuntimeError, 'timed out'):
                    owner.execute('leader-exits', [sys.executable, '-I', '-c', 'pass'], 0.15)
            records = [json.loads(row) for row in owner.journal.read_bytes().splitlines()]
            failed = next(row for row in records if row['event'] == 'command-failed')
            self.assertTrue(failed['directChildReaped'])
            self.assertFalse(failed['bothStreamsEof'])
            self.assertEqual(failed['descendantSettlement'], 'unproved')
        finally:
            # The external inherited-writer port is an actual direct child of
            # THIS fixture, with retained birth; it is not cleanup authority for
            # any production descendant or another operation.
            if writer.poll() is None:
                self.assertTrue(SC2.same_birth(writer_birth, SC2.birth(writer.pid, include_namespaces=False)))
                writer.terminate()
            writer.wait(timeout=2)
            if not delegated:
                os.close(read_fd); os.close(write_fd)

    def test_actual_process_sample_refuses_rss_excess_and_unreadable_population(self):
        owner = self.owner('process-sample')
        current = os.getpid(); other = current + 10_000_000
        cgroup = Path('/proc/self/cgroup').read_text().strip()
        row = {'pid': current, 'startTicks': 1, 'parent': 0, 'group': current, 'session': current,
               'state': 'R', 'cgroup': cgroup, 'rssBytes': 12 * 1024**3 + 1, 'namespaces': {}}
        def scan(entries):
            value = Mock(); value.__enter__ = Mock(return_value=iter(entries)); value.__exit__ = Mock(return_value=False)
            return value
        entries = [argparse.Namespace(name=str(current), path='/proc/' + str(current))]
        with patch.object(os, 'scandir', return_value=scan(entries)), patch.object(SC2, 'birth', return_value=row):
            with self.assertRaisesRegex(RuntimeError, 'RSS ceiling'):
                owner.process_sample()
        entries.append(argparse.Namespace(name=str(other), path='/proc/' + str(other)))
        def unreadable(pid, **kwargs):
            if pid == other:
                raise PermissionError('declared injected visible-population gap')
            return {**row, 'rssBytes': 1}
        with patch.object(os, 'scandir', return_value=scan(entries)), patch.object(SC2, 'birth', side_effect=unreadable):
            with self.assertRaisesRegex(RuntimeError, 'coverage read failed'):
                owner.process_sample()

    def test_product_oci_actual_streaming_reader_and_source_policy(self):
        candidate = self.root / 'synthetic.oci.tar'
        source = b'public synthetic product source\n'
        def archive(**kwargs):
            with patch.object(COMMON.IMAGE, 'COMMON_IMAGE_NAME', TEST_EXPORT_NAME):
                return COMMON.synthetic_common_archive(candidate, b'synthetic 1\n',
                    final=[('workspace/source.txt', 'file', source)],
                    config_update={'User': '1654:1654', 'Entrypoint': ['bash', 'scripts/sc2-container-preflight.sh'],
                                   'Cmd': ['preflight']}, **kwargs)
        _, image_id, _ = archive()
        checked = {'selection': {'product': {'files': {'source.txt': digest(source)}, 'exportName': TEST_EXPORT_NAME}}}
        policy = SC2.oci_policy(checked)
        ticks = []
        result = IMAGE.inspect_oci_archive(candidate, profile=SC2.PROFILE, built_image_id=image_id,
                                          product_policy=policy, observe=lambda: ticks.append(True))
        self.assertGreater(result['headers'], 0)
        self.assertGreater(len(ticks), 5)
        self.assertEqual(result['config']['User'], '1654:1654')
        for key, value in (('headers', 1), ('pathMetadataBytes', 2), ('expandedBytes', 1), ('archiveBytes', 1)):
            changed = {**policy, key: value}
            with self.subTest(bound=key), self.assertRaises(RuntimeError):
                IMAGE.inspect_oci_archive(candidate, profile=SC2.PROFILE, built_image_id=image_id, product_policy=changed)
        changed = {**policy, 'files': {'workspace/source.txt': '0' * 64}}
        with self.assertRaisesRegex(RuntimeError, 'source/input bytes'):
            IMAGE.inspect_oci_archive(candidate, profile=SC2.PROFILE, built_image_id=image_id, product_policy=changed)
        _, image_id, _ = archive(wrong_diff=True)
        with self.assertRaisesRegex(RuntimeError, 'diffID'):
            IMAGE.inspect_oci_archive(candidate, profile=SC2.PROFILE, built_image_id=image_id, product_policy=policy)

    def product_archive(self, *, base=(), final=(), files=None):
        target = self.root / 'synthetic-fifo.oci.tar'
        with patch.object(COMMON.IMAGE, 'COMMON_IMAGE_NAME', TEST_EXPORT_NAME):
            _, image_id, _ = COMMON.synthetic_common_archive(target, b'synthetic 1\n', base=base, final=final,
                config_update={'User': '1654:1654', 'Entrypoint': ['bash', 'scripts/sc2-container-preflight.sh'],
                               'Cmd': ['preflight']})
        policy = SC2.oci_policy({'selection': {'product': {'files': {}, 'exportName': TEST_EXPORT_NAME}}})
        if files is not None:
            policy['files'] = files
        return target, image_id, policy

    def inspect_product(self, *, base=(), final=(), files=None):
        target, image_id, policy = self.product_archive(base=base, final=final, files=files)
        return IMAGE.inspect_oci_archive(target, profile=SC2.PROFILE, built_image_id=image_id, product_policy=policy)

    def test_exact_fifo_metadata_has_no_content_read_or_regular_hash(self):
        name = 'tmp/clr-debug-pipe-7-123-in'
        target, image_id, policy = self.product_archive(final=[(name, 'fifo', {})])
        actual_extract = tarfile.TarFile.extractfile
        def extract(archive, member, *args, **kwargs):
            if isinstance(member, tarfile.TarInfo):
                self.assertFalse(member.isfifo(), 'FIFO must never become an extractfile/content request')
            return actual_extract(archive, member, *args, **kwargs)
        with patch.object(tarfile.TarFile, 'extractfile', extract):
            IMAGE.inspect_oci_archive(target, profile=SC2.PROFILE, built_image_id=image_id, product_policy=policy)
        member = tarfile.TarInfo(name); member.type = tarfile.FIFOTYPE; member.mode = 0o700
        entry = IMAGE.product_fifo_entry(member, name)
        self.assertEqual(entry['kind'], 'fifo'); self.assertNotIn('sha256', entry)
        for key, value in (('uid', True), ('gid', 0.0), ('mode', False), ('size', False)):
            mutated = copy.copy(member); setattr(mutated, key, value)
            with self.subTest(field=key), self.assertRaises(RuntimeError):
                IMAGE.product_fifo_entry(mutated, name)

    def test_fifo_effective_metadata_and_path_mutants_refuse(self):
        name = 'tmp/clr-debug-pipe-7-123-in'
        for change in ({'size': 1}, {'mode': 0o600}, {'mode': 0o4700}, {'uid': 1654}, {'gid': 1},
                       {'devmajor': 1}, {'devminor': 1}, {'linkname': 'elsewhere'},
                       {'type': tarfile.CHRTYPE}, {'type': tarfile.BLKTYPE}, {'type': b'Z'},
                       {'pax_headers': {'path': 'elsewhere'}}, {'pax_headers': {'size': '1'}}):
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.inspect_product(final=[(name, 'fifo', change)])
        for wrong in ('elsewhere/clr-debug-pipe-7-123-in', 'tmp/clr-debug-pipe-0-1-in',
                      'tmp/clr-debug-pipe-' + '1' * 21 + '-1-in', name + '-extra',
                      name + '/child', 'tmp/.wh.clr-debug-pipe-7-123-in'):
            with self.subTest(path=wrong), self.assertRaises(RuntimeError):
                self.inspect_product(final=[(wrong, 'fifo', {})])

    def test_fifo_replacement_whiteout_and_regular_evidence_semantics(self):
        name = 'tmp/clr-debug-pipe-7-123-in'
        # Every history goes through the actual complete OCI validator/diffIDs.
        self.inspect_product(base=[(name, 'file', b'old'), ('retained-link', 'hardlink', name)],
                             final=[(name, 'fifo', {})], files={'retained-link': digest(b'old')})
        self.inspect_product(base=[(name, 'fifo', {})], final=[(name, 'file', b'new')], files={name: digest(b'new')})
        self.inspect_product(base=[(name, 'directory', None), (name + '/old', 'file', b'old')],
                             final=[(name, 'fifo', {})])
        self.inspect_product(base=[(name, 'fifo', {})],
                             final=[(name, 'directory', None), (name + '/new', 'file', b'new')],
                             files={name + '/new': digest(b'new')})
        for deletion in ('tmp/.wh.clr-debug-pipe-7-123-in', 'tmp/.wh..wh..opq'):
            self.inspect_product(base=[(name, 'fifo', {})], final=[(deletion, 'file', b'')])
        for final in ([(name, 'fifo', {}), (name, 'file', b'duplicate')],
                      [(name + '/child', 'file', b'bad')],
                      [(name + '/.wh.child', 'file', b'')],
                      [(name + '/.wh..wh..opq', 'file', b'')]):
            with self.subTest(final=final), self.assertRaises(RuntimeError):
                self.inspect_product(base=[(name, 'fifo', {})], final=final)
        with self.assertRaisesRegex(RuntimeError, 'source/input bytes'):
            self.inspect_product(base=[(name, 'file', b'')], final=[(name, 'fifo', {})], files={name: digest(b'')})

    def test_fifo_aliases_and_top_level_fifo_are_refused(self):
        name = 'tmp/clr-debug-pipe-7-123-in'
        for final in ([('alias', 'symlink', '/' + name)],
                      [('hop', 'symlink', name), ('alias', 'symlink', 'hop')],
                      [('alias', 'symlink', name + '/child')],
                      [('alias', 'hardlink', name)],
                      [('hop', 'symlink', name), ('alias', 'hardlink', 'hop')]):
            with self.subTest(final=final), self.assertRaises(RuntimeError):
                self.inspect_product(base=[(name, 'fifo', {})], final=final)
        with self.assertRaises(RuntimeError):
            self.inspect_product(final=[('forward', 'hardlink', name), (name, 'fifo', {})])
        with self.assertRaises(RuntimeError):
            self.inspect_product(base=[('alias', 'symlink', name)], final=[(name, 'fifo', {})])
        source, image_id, policy = self.product_archive(final=[(name, 'fifo', {})])
        changed = self.root / 'outer-fifo.oci.tar'
        with tarfile.open(source, 'r:') as original, tarfile.open(changed, 'w') as output:
            for member in original:
                if member.name == 'index.json':
                    member.type = tarfile.FIFOTYPE; member.size = 0; member.mode = 0o700
                    output.addfile(member)
                else:
                    output.addfile(member, original.extractfile(member) if member.isfile() else None)
        with self.assertRaisesRegex(RuntimeError, 'not regular'):
            IMAGE.inspect_oci_archive(changed, profile=SC2.PROFILE, built_image_id=image_id, product_policy=policy)

    def test_common_profile_keeps_original_other_type_policy_and_result_shape(self):
        target = self.root / 'synthetic-common-fifo.oci.tar'; inventory = b'synthetic 1\n'
        _, image_id, _ = COMMON.synthetic_common_archive(target, inventory,
            final=[('elsewhere/not-a-sc2-fifo', 'fifo', {'mode': 0o600})])
        result = IMAGE.inspect_oci_archive(target, profile='common-arch', expected_inventory=inventory,
                                          built_image_id=image_id)
        self.assertEqual(set(result), {'digest', 'configDigest', 'manifestBytes'})

    def test_verification_observes_original_deadline_and_no_fresh_aperture(self):
        owner = self.owner('verify-expiry')
        with patch.object(owner, 'now', return_value=owner.clock['workEnd']):
            with self.assertRaisesRegex(RuntimeError, 'original work deadline'):
                owner.verification_tick()
        with patch.object(owner, 'now', return_value=owner.clock['totalEnd']):
            with self.assertRaisesRegex(RuntimeError, 'original retirement deadline'):
                owner.work_guard(True)

    def test_streaming_product_metadata_guards_precede_sparse_or_global_expansion(self):
        policy = SC2.oci_policy({'selection': {'product': {'files': {}, 'exportName': TEST_EXPORT_NAME}}})
        def layer(*, global_pax=False, sparse=False, extensions=0):
            out = io.BytesIO()
            with tarfile.open(fileobj=out, mode='w', format=tarfile.PAX_FORMAT,
                              pax_headers={'public-test-key': 'value'} if global_pax else None) as archive:
                for _ in range(extensions):
                    value = b'12 path=x/y\n'
                    # Compute valid POSIX record framing, no malformed-size
                    # shortcut around the actual nested metadata reader.
                    body = b'path=x/y\n'; size = len(body) + 2
                    while len(str(size).encode()) + 1 + len(body) != size:
                        size = len(str(size).encode()) + 1 + len(body)
                    value = str(size).encode() + b' ' + body
                    header = tarfile.TarInfo('public-extension'); header.type = tarfile.XHDTYPE; header.size = len(value)
                    archive.addfile(header, io.BytesIO(value))
                member = tarfile.TarInfo('x/y'); member.size = 1
                if sparse:
                    member.pax_headers = {'GNU.sparse.map': '0,1'}
                archive.addfile(member, io.BytesIO(b'x'))
            return out.getvalue()
        for name, value in (('global', layer(global_pax=True)), ('sparse', layer(sparse=True)),
                            ('nested', layer(extensions=17))):
            with self.subTest(kind=name), self.assertRaises(RuntimeError):
                IMAGE.common_layer_entries(io.BytesIO(value), 'application/vnd.oci.image.layer.v1.tar', policy=policy)
        value = layer(extensions=2)
        result = IMAGE.common_layer_entries(io.BytesIO(value), 'application/vnd.oci.image.layer.v1.tar', policy=policy)
        self.assertEqual(result[4], 3)  # Two extension headers plus real file.
        with self.assertRaisesRegex(RuntimeError, 'header bound'):
            IMAGE.common_layer_entries(io.BytesIO(value), 'application/vnd.oci.image.layer.v1.tar',
                                       policy={**policy, 'headers': 2})


if __name__ == '__main__':
    unittest.main()
