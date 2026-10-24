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
        network = self.root / 'network-tools'; network.mkdir(mode=0o700)
        binaries = tuple(str(network / name) for name in ('passt', 'passt.avx2'))
        aliases = tuple(str(network / name) for name in ('pasta', 'pasta.avx2'))
        for binary, alias in zip(binaries, aliases):
            Path(binary).write_bytes(b'authored inert network ELF port'); Path(binary).chmod(0o500)
            Path(alias).symlink_to(binary)
        self.network_library = network / 'libnetwork.so'
        self.network_library.write_bytes(b'authored inert loader dependency'); self.network_library.chmod(0o400)
        for key, value in [('NETWORK_BINARIES', binaries), ('NETWORK_ALIASES', aliases),
                           ('NETWORK_OWNER', os.getuid()), ('NETWORK_LIBRARY_ROOT', str(self.root))]:
            change = patch.object(BAR, key, value); change.start(); self.addCleanup(change.stop)
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
        payloads = {'engine': b'synthetic headless bytes'.ljust(64, b'\0'), 'map': b'synthetic map', 'game': b'synthetic sdp'}
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
        selection['network'] = {'purpose': 'full-image', 'seed': BAR.network_seed()}
        security = self.root / 'root-security.json'
        security.write_bytes(SHARED.canonical({'schema': 'fsgg.bar-network-security/1', 'bootId': selection['clock']['bootId'], 'observations': {}})); security.chmod(0o600)
        selection['admission']['security'] = {'path': str(security), 'sha256': sha(security.read_bytes())}
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

    def image(self, args, *, missing=None, changed=None, fifo=False, manifest_mutate=None, manifest_payload=None, tail=None, checked=None):
        if checked is None:
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
        plugin = 'opt/barc/plugin/libSkirmishAI.so'
        files[plugin] = files[plugin].ljust(64, b'\0')
        library = 'usr/lib/x86_64-linux-gnu/libfixture.so'; loader = 'lib64/ld-linux-x86-64.so.2'
        files[library] = b'synthetic library'.ljust(64, b'\0'); files[loader] = b'synthetic interpreter'.ljust(64, b'\0')
        roots = {'engine': '/opt/barc/engine/spring-headless', 'plugin': '/' + plugin}
        records = {}
        for name in [roots['engine'][1:], plugin, library, loader]:
            is_root = '/' + name in roots.values()
            metadata = {'class': 'ELF64', 'machine': 'x86-64', 'type': 3,
                        'needed': ['libfixture.so'] if is_root else [],
                        'soname': 'libfixture.so' if name == library else None,
                        'interpreter': '/' + loader if name == roots['engine'][1:] else None,
                        'searchPath': None, 'searchPathKind': None}
            edges = [{'needed': 'libfixture.so', 'path': '/' + library}] if is_root else []
            if metadata['interpreter']: edges.append({'interpreter': '/' + loader, 'path': '/' + loader})
            records['/' + name] = {'sha256': sha(files[name]), 'bytes': len(files[name]), 'metadata': metadata, 'dependencies': edges}
        manifest = {'schema': 'bar-static-elf-correspondence/1', 'roots': roots, 'files': records,
                    'aliases': {name: name for name in records}, 'loadedAbiAccepted': False,
                    'scope': 'ELF64 x86-64 image-local DT_NEEDED/interpreter candidate correspondence; not loader order, symbols, versions, dlopen or loaded ABI proof',
                    'expectedRoots': {role: records[name]['sha256'] for role, name in roots.items()}}
        if manifest_mutate: manifest_mutate(manifest)
        files['opt/barc/STATIC-ELF.json'] = SHARED.canonical(manifest) if manifest_payload is None else manifest_payload
        if missing: files.pop(missing)
        if changed: files[changed[0]] = changed[1]
        rows = [(name, 'file', value) for name, value in files.items()]
        if fifo: rows.append(('tmp/clr-debug-pipe-7-123-in', 'fifo', b''))
        archive = self.root / 'synthetic-built.oci.tar'
        with patch.object(COMMON.IMAGE, 'COMMON_IMAGE_NAME', checked['selection']['product']['exportName']):
            _, image_id, config = COMMON.synthetic_common_archive(archive, b'synthetic 1\n',
                base=rows if tail is not None else (), final=tail if tail is not None else rows,
                config_update={'User': '10001:10001', 'WorkingDir': '/work',
                               'Entrypoint': ['bash', '/opt/barc/entrypoint.sh'], 'Cmd': []})
        with tarfile.open(archive, 'r:') as data:
            native_config = json.load(data.extractfile('blobs/sha256/' + image_id[7:]))
        return {'archive': archive, 'id': image_id, 'policy': policy, 'checked': checked,
                'inspect': {'Id': image_id, 'Os': 'linux', 'Architecture': 'amd64', 'Config': config,
                            'RootFS': {'Layers': native_config['rootfs']['diff_ids']}}}

    def ports(self, args, selection, image=None, fail=None, bad_mapping=False, existing_image=False, network_fault=None):
        spawn, calls, resources = self.base.native_ports(args, selection, image=image, fail=fail)
        real_popen = subprocess.Popen
        def invoke(argv, **kwargs):
            if 'pull' in argv:
                calls.append(argv.copy())
                if fail == 'pull':
                    return real_popen([sys.executable, '-I', '-c', 'raise SystemExit(23)'], **kwargs)
                return real_popen([sys.executable, '-I', '-c', 'print("synthetic selected base pull")'], **kwargs)
            if '--list' in argv and argv[-1] in BAR.NETWORK_BINARIES:
                calls.append(argv.copy())
                output = 'libnetwork.so => ' + str(self.network_library) + ' (0x0)'
                return real_popen([sys.executable, '-I', '-c', 'print(' + repr(output) + ')'], **kwargs)
            if argv[0] == BAR.NETWORK_ALIASES[0]:
                calls.append(argv.copy())
                return real_popen([sys.executable, '-I', '-c', 'print(' + repr(BAR.NETWORK_VERSION) + ')'], **kwargs)
            if 'build' in argv and any('network-prerequisite/Containerfile' in value for value in argv):
                calls.append(argv.copy())
                if network_fault == 'pasta':
                    return real_popen([sys.executable, '-I', '-c',
                        "import sys;sys.stderr.write(\"Couldn't open network namespace /proc/401/ns/net: Permission denied\\n\");raise SystemExit(125)"], **kwargs)
                if fail == 'build':
                    return real_popen([sys.executable, '-I', '-c', 'raise SystemExit(23)'], **kwargs)
                target = next(value.split('=', 1)[1] for value in argv if value.startswith('--iidfile='))
                Path(target).write_text('sha256:' + 'e' * 64 + '\n')
                value = {'uid': 0, 'noNewPrivs': 1, 'procPidCorrespondence': 1,
                         'pidNamespace': Path('/proc/self/ns/pid').stat().st_ino + 1,
                         'networkNamespace': Path('/proc/self/ns/net').stat().st_ino + 1}
                if network_fault == 'nnp': value['noNewPrivs'] = 0
                if network_fault == 'namespace': value['networkNamespace'] -= 1
                return real_popen([sys.executable, '-I', '-c', 'print(' + repr('FSGG_NET_PROBE ' + json.dumps(value)) + ')'], **kwargs)
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

    def test_actual_bar_preflight_consumes_role_specific_info_protocol(self):
        args, selection, home = self.fixture(); args.mode = 'preflight'
        selection['network']['purpose'] = 'network-prerequisite'
        selection['clock']['workEnd']=selection['clock']['start']+300
        selection['clock']['totalEnd']=selection['clock']['workEnd']+120
        self.base.rebind_selection(args, selection)
        spawn, calls, resources = self.ports(args, selection)
        with self.native_context(home, spawn, resources):
            result = BAR.main(args, IMAGE.__dict__)
        self.assertEqual(result['preflight'], 'passed')
        infos = [argv for argv in calls if 'info' in argv]
        self.assertEqual([argv[argv.index('info'):] for argv in infos],
                         [['info'], ['info', '--format=json']])
        self.assertEqual([argv[0] for argv in infos],
                         [selection['support']['builder']['path'], selection['support']['podman']['path']])
        self.assertEqual(sum('pull' in argv for argv in calls), 1)
        self.assertEqual(sum('build' in argv for argv in calls), 1)
        self.assertFalse(any('load' in argv or 'push' in argv for argv in calls))
        self.assertFalse(result['candidateQualified'])
        probe = next(argv for argv in calls if 'build' in argv)
        for flag in ('--network=private', '--pid=private', '--ipc=private',
                     '--runtime=/usr/bin/crun', '--security-opt=no-new-privileges', '--retry=0'):
            self.assertIn(flag, probe)
        self.assertFalse(any(value.startswith('--target=') for value in probe))
        recipe = BAR.network_probe_recipe().decode()
        self.assertEqual(recipe.count('RUN '), 1)
        self.assertIn('FROM --platform=linux/amd64 ' + BAR.BASE_IMAGE, recipe)
        self.assertNotIn('apt-get', recipe)
        self.assertEqual(subprocess.run(['/bin/sh', '-n'], input=recipe.split('RUN ', 1)[1],
                                       text=True, capture_output=True, timeout=5).returncode, 0)

    def test_network_failure_or_weakened_result_blocks_full_generation(self):
        for fault, expected in [('pasta', 'command failed with exit 125'),
                                ('nnp', 'isolation correspondence'), ('namespace', 'isolation correspondence')]:
            with self.subTest(fault=fault), FIXTURE.tempfile.TemporaryDirectory(dir=self.root) as temporary:
                original = self.root; self.base.root = self.root = Path(temporary); self.root.chmod(0o700)
                try:
                    args, selection, home = self.fixture(); args.mode = 'qualify'
                    spawn, calls, resources = self.ports(args, selection, network_fault=fault)
                    with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError, expected):
                        BAR.main(args, IMAGE.__dict__)
                    self.assertFalse(any('--target=headless-integration' in argv for argv in calls))
                    self.assertFalse(any('push' in argv or 'load' in argv or 'tag' in argv for argv in calls))
                    journal = Path(args.state_dir) / selection['operationId'] / 'journal.jsonl'
                    records = [json.loads(line) for line in journal.read_bytes().splitlines()]
                    self.assertEqual(records[-1]['descendantSettlement'], 'unproved')
                    if fault == 'pasta':
                        self.assertEqual(records[-1]['firstCause'], expected)
                        failed = next(row for row in records if row.get('event') == 'command-result' and row.get('exitCode') == 125)
                        self.assertTrue(failed['bothStreamsEof']); self.assertTrue(failed['directChildReaped'])
                finally:
                    self.base.root = self.root = original

    def test_post_pull_headroom_loss_refuses_before_network_build(self):
        args, selection, home = self.fixture(); args.mode = 'preflight'
        selection['network']['purpose'] = 'network-prerequisite'
        selection['clock']['workEnd']=selection['clock']['start']+300
        selection['clock']['totalEnd']=selection['clock']['workEnd']+120
        self.base.rebind_selection(args, selection)
        spawn, calls, resources = self.ports(args, selection)
        def lose_headroom(argv, **kwargs):
            child = spawn(argv, **kwargs)
            if 'pull' in argv:
                resources['hostMemoryAvailableBytes'] = 0
            return child
        with self.native_context(home, lose_headroom, resources), self.assertRaisesRegex(RuntimeError, 'host memory'):
            BAR.main(args, IMAGE.__dict__)
        self.assertTrue(any('pull' in argv for argv in calls))
        self.assertFalse(any('build' in argv for argv in calls))

    def test_network_alias_or_avx2_change_refuses_before_probe(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        # These were captured in the operation input, not invented fixture hashes.
        Path(BAR.NETWORK_BINARIES[1]).chmod(0o700)
        Path(BAR.NETWORK_BINARIES[1]).write_bytes(b'changed after capture')
        Path(BAR.NETWORK_BINARIES[1]).chmod(0o500)
        with patch.object(Path, 'home', return_value=home), \
             self.assertRaisesRegex(RuntimeError, 'network helper seed differs'), \
             patch.object(IMAGE.subprocess, 'Popen') as launched:
            BAR.main(args, IMAGE.__dict__)
        launched.assert_not_called()
        Path(BAR.NETWORK_ALIASES[0]).unlink()
        Path(BAR.NETWORK_ALIASES[0]).symlink_to(BAR.NETWORK_BINARIES[1])
        with self.assertRaisesRegex(RuntimeError, 'network helper alias differs'):
            BAR.network_seed()

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

    def hosted(self, args, selection, mutate=None, disposition=None):
        """Synthetic hosted-VM domain evidence on the fixture's own volume.

        No hosted runner, service stop or volume is involved; this exercises only
        the closed relation join in the actual BAR selection reader."""
        device = Path(args.engine_allocation).stat().st_dev
        evidence = {'schema': BAR.HOSTED_DOMAIN_SCHEMA, 'relation': BAR.HOSTED_RELATION,
                    'verdict': 'admitted', 'refusals': [], 'bootId': selection['clock']['bootId'],
                    'runner': {'imageOS': 'ubuntu24', 'imageVersion': BAR.HOSTED_RUNNER_IMAGE_VERSION, 'githubHosted': True},
                    'services': {'stopped': list(BAR.HOSTED_STOPPED_SERVICES), 'active': [], 'daemonProcesses': 0},
                    'userns': {'apparmorRestrictUnprivileged': 0, 'maxUserNamespaces': 63838},
                    'volume': {'device': f'{os.major(device)}:{os.minor(device)}', 'filesystem': 'ext4',
                               'bytes': 127 * 1024**3, 'allocatedBytes': 127 * 1024**3,
                               'freeBytes': 116 * 1024**3, 'freeInodes': 7_806_964,
                               'discard': 'nodiscard',
                               'backingDevice': '8:2' if os.major(device) == 8 and os.minor(device) == 1 else '8:1'},
                    'builder': {'version': 'buildah version 1.43.1 (image-spec 1.1.1, runtime-spec 1.2.1)'},
                    'resources': {'memAvailableBytes': 15 * 1024**3, 'logicalCpus': 4, 'affinity': 4}}
        if mutate: mutate(evidence)
        target = self.root / 'hosted-domain.json'
        target.write_bytes(SHARED.canonical(evidence)); target.chmod(0o600)
        reference = Path(selection['admission']['resourceDisposition']['path'])
        reference.write_bytes(SHARED.canonical(disposition or {
            'sharedHostReservedMemoryBytes': 0, 'sharedHostReservedCpu': 0,
            'sharedHostReservedDiskBytes': 0, 'unquantifiedResourceConflicts': []}))
        selection['admission']['resourceDisposition']['sha256'] = sha(reference.read_bytes())
        selection['admission']['domain'] = {'path': str(target), 'sha256': sha(target.read_bytes())}
        selection['resourceDomain'] = {'relation': BAR.HOSTED_RELATION,
                                       'topologySha256': selection['resourceDomain']['topologySha256'],
                                       'domainEvidenceSha256': sha(target.read_bytes())}
        self.base.rebind_selection(args, selection)

    def test_hosted_vm_relation_joins_closed_domain_evidence(self):
        args, selection, home = self.fixture()
        self.hosted(args, selection)
        with patch.object(Path, 'home', return_value=home):
            result = BAR.main(args, IMAGE.__dict__)
        self.assertFalse(result['nativeExecutionEnabled'])
        self.assertEqual(result['hostedDomain']['relation'], BAR.HOSTED_RELATION)
        self.assertEqual(result['hostedDomain']['runnerImageVersion'], '20261012.334.1')
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_hosted_vm_relation_refuses_unproved_or_weakened_domains(self):
        cases = {
            'refused': (lambda e: e.update(verdict='refused', refusals=['synthetic']), None, 'evidence refused'),
            'runner': (lambda e: e['runner'].update(imageOS='ubuntu22'), None, 'runner image differs'),
            'image-version': (lambda e: e['runner'].update(imageVersion='20261019.340.1'), None, 'runner image differs'),
            'replayed-boot': (lambda e: e.update(bootId='00000000-0000-0000-0000-000000000000'), None, 'another boot'),
            'backing-not-separate': (lambda e: e['volume'].update(backingDevice=e['volume']['device']), None,
                                     'backing is not a separate file'),
            'daemon-bool': (lambda e: e['services'].update(daemonProcesses=False), None, 'bounded integer required'),
            'userns-float': (lambda e: e['userns'].update(apparmorRestrictUnprivileged=0.0), None, 'bounded integer required'),
            'fstrim': (lambda e: e['services'].update(stopped=e['services']['stopped'][:3]), None, 'daemons not stopped'),
            'services': (lambda e: e['services'].update(active=['docker.service']), None, 'daemons not stopped'),
            'userns': (lambda e: e['userns'].update(apparmorRestrictUnprivileged=1), None, 'user namespaces'),
            'sparse': (lambda e: e['volume'].update(allocatedBytes=1024**3), None, 'not preallocated'),
            'discard': (lambda e: e['volume'].update(discard='discard'), None, 'volume format differs'),
            'floor': (lambda e: e['volume'].update(freeBytes=79 * 1024**3), None, 'free floor not met'),
            'device': (lambda e: e['volume'].update(device='4095:4095'), None, 'outside the domain volume'),
            'builder': (lambda e: e['builder'].update(version='buildah version 1.33.7 (image-spec 1.1.0-rc.5, runtime-spec 1.1.0)'),
                        None, 'builder version differs'),
            'memory': (lambda e: e['resources'].update(memAvailableBytes=14 * 1024**3 - 1), None, 'capacity floor'),
            'cpu': (lambda e: e['resources'].update(affinity=2), None, 'capacity floor'),
            'extension': (lambda e: e.update(command=['bash']), None, 'input fields mismatch'),
            'reservation': (None, {'sharedHostReservedMemoryBytes': 1, 'sharedHostReservedCpu': 0,
                                   'sharedHostReservedDiskBytes': 0, 'unquantifiedResourceConflicts': []},
                            'cannot carry shared-host reservations'),
        }
        args, base, home = self.fixture()
        for name, (mutate, disposition, message) in cases.items():
            selection = copy.deepcopy(base)
            self.hosted(args, selection, mutate, disposition)
            with self.subTest(case=name), patch.object(Path, 'home', return_value=home), \
                    self.assertRaisesRegex(RuntimeError, message):
                BAR.main(args, IMAGE.__dict__)
            self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_hosted_vm_relation_requires_its_bound_evidence_reference(self):
        args, selection, home = self.fixture()
        self.hosted(args, selection)
        for mutate, message in ((lambda s: s['resourceDomain'].pop('domainEvidenceSha256'), 'input fields mismatch'),
                                (lambda s: s['admission'].pop('domain'), 'input fields mismatch'),
                                (lambda s: s['resourceDomain'].update(domainEvidenceSha256='0' * 64), 'correspondence missing'),
                                (lambda s: s['resourceDomain'].update(relation='hosted-vm'), 'input fields mismatch'),
                                (lambda s: s['admission']['domain'].update(sha256='0' * 64), 'correspondence missing')):
            wrong = copy.deepcopy(selection); mutate(wrong); self.base.rebind_selection(args, wrong)
            with patch.object(Path, 'home', return_value=home), self.assertRaisesRegex(RuntimeError, message):
                BAR.main(args, IMAGE.__dict__)
        shared_host = copy.deepcopy(selection)
        shared_host['resourceDomain'] = {'relation': BAR.SHARED_HOST_RELATION,
                                         'topologySha256': selection['resourceDomain']['topologySha256']}
        self.base.rebind_selection(args, shared_host)
        with patch.object(Path, 'home', return_value=home), self.assertRaisesRegex(RuntimeError, 'input fields mismatch'):
            BAR.main(args, IMAGE.__dict__)

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
        self.assertEqual(len(result['producedOutputs']), 9)
        self.assertEqual(result['staticElfCorrespondence']['files'], 4)
        self.assertFalse(result['staticElfCorrespondence']['loadedAbiAccepted'])
        self.assertFalse(result['productRuntimeAcceptance'])
        records = [json.loads(row) for row in Path(result['journal']).read_bytes().splitlines()]
        self.assertEqual(records[0]['profile'], BAR.PROFILE)
        verified = next(i for i, row in enumerate(records) if row['event'] == 'archive-verified')
        loaded = next(i for i, row in enumerate(records) if row['event'] == 'command-intent' and row['name'] == 'verified-load')
        self.assertLess(verified, loaded)
        self.assertEqual(sum('build' in argv for argv in calls), 2)
        self.assertEqual(sum('pull' in argv for argv in calls), 1)
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

    def test_static_manifest_failure_refuses_before_actual_load_port(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        image = self.image(args, manifest_mutate=lambda m: m['aliases'].pop('/usr/lib/x86_64-linux-gnu/libfixture.so'))
        spawn, calls, resources = self.ports(args, selection, image)
        with self.native_context(home, spawn, resources), self.assertRaisesRegex(RuntimeError, 'lookup alias'):
            BAR.main(args, IMAGE.__dict__)
        self.assertFalse(any('load' in argv or 'tag' in argv for argv in calls))

    def test_static_manifest_refusal_variants_use_actual_archive_verifier(self):
        args, _, _ = self.fixture(); checked = self.image(args)['checked']
        cases = [
            {'missing': 'opt/barc/STATIC-ELF.json'},
            {'manifest_payload': b'{broken'},
            {'manifest_payload': b' ' * (BAR.MAX_JSON_BYTES + 1)},
            {'manifest_mutate': lambda m: m.update(loadedAbiAccepted=True)},
            {'manifest_mutate': lambda m: m['files'].pop('/usr/lib/x86_64-linux-gnu/libfixture.so')},
            {'manifest_mutate': lambda m: m['files']['/usr/lib/x86_64-linux-gnu/libfixture.so'].update(sha256='0'*64)},
            {'manifest_mutate': lambda m: m['aliases'].pop('/usr/lib/x86_64-linux-gnu/libfixture.so')},
            {'manifest_mutate': lambda m: m['expectedRoots'].update(engine='0'*64)},
        ]
        for index, options in enumerate(cases):
            with self.subTest(case=index):
                image = self.image(args, checked=checked, **options)
                with self.assertRaises(RuntimeError):
                    IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                        built_image_id=image['id'], product_policy=image['policy'])

    def test_effective_layers_cannot_delete_replace_or_retarget_manifest_members(self):
        args, _, _ = self.fixture(); checked = self.image(args)['checked']
        library = 'usr/lib/x86_64-linux-gnu/libfixture.so'
        cases = [
            [('opt/barc/.wh.STATIC-ELF.json', 'file', b'')],
            [('usr/lib/x86_64-linux-gnu/.wh.libfixture.so', 'file', b'')],
            [(library, 'file', b'different library'.ljust(64, b'\0'))],
            [(library, 'symlink', '/lib64/ld-linux-x86-64.so.2')],
            [('lib64/libfixture.so', 'file', b'another candidate'.ljust(64, b'\0'))],
            [('opt/barc/STATIC-ELF.json', 'file', b'{}')],
        ]
        for rows in cases:
            with self.subTest(rows=rows):
                image = self.image(args, tail=rows, checked=checked)
                with self.assertRaises(RuntimeError):
                    IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                        built_image_id=image['id'], product_policy=image['policy'])

    def test_aliases_bind_effective_directory_resolution(self):
        args, _, _ = self.fixture(); checked = self.image(args)['checked']
        def alias(m):
            m['aliases']['/lib/x86_64-linux-gnu/libfixture.so'] = '/usr/lib/x86_64-linux-gnu/libfixture.so'
        image = self.image(args, manifest_mutate=alias, tail=[('lib', 'symlink', 'usr/lib')], checked=checked)
        result = IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                    built_image_id=image['id'], product_policy=image['policy'])
        self.assertEqual(result['staticElfCorrespondence']['aliases'], 5)
        image = self.image(args, manifest_mutate=alias, tail=[('lib', 'symlink', 'missing')], checked=checked)
        with self.assertRaises(RuntimeError):
            IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                built_image_id=image['id'], product_policy=image['policy'])

    def test_observation_failure_remains_original_before_load(self):
        args, _, _ = self.fixture(); image = self.image(args)
        sentinel = RuntimeError('original verification clock exhausted')
        def expired(): raise sentinel
        image['policy']['verifyBarStaticElf'] = lambda payload, population, resolve, observe: BAR.verify_static_elf(
            payload, population, resolve, expired, sha(self.payloads['engine']))
        with self.assertRaisesRegex(RuntimeError, 'original verification clock exhausted'):
            IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                built_image_id=image['id'], product_policy=image['policy'])

    def test_manifest_population_and_metadata_refusals(self):
        args, _, _ = self.fixture(); checked = self.image(args)['checked']
        mutations = [
            lambda m: m.update(schema='unselected'),
            lambda m: m.update(extra='unknown'),
            lambda m: m['roots'].update(engine='/not-selected'),
            lambda m: m['files']['/opt/barc/engine/spring-headless'].update(bytes=True),
            lambda m: m['files']['/opt/barc/engine/spring-headless']['metadata'].update(machine='aarch64'),
            lambda m: m['files']['/opt/barc/engine/spring-headless']['metadata'].update(needed=['libfixture.so', 'libfixture.so']),
            lambda m: m['files']['/opt/barc/engine/spring-headless']['metadata'].update(searchPath='/host', searchPathKind='RUNPATH'),
            lambda m: m['files']['/opt/barc/engine/spring-headless']['dependencies'].clear(),
            lambda m: m['files'].update({f'/extra{i}': {} for i in range(257)}),
            lambda m: m['aliases'].update({f'/extra{i}': '/lib64/ld-linux-x86-64.so.2' for i in range(8193)}),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(case=index):
                image = self.image(args, manifest_mutate=mutate, checked=checked)
                with self.assertRaises(RuntimeError):
                    IMAGE.inspect_oci_archive(image['archive'], profile=BAR.PROFILE,
                        built_image_id=image['id'], product_policy=image['policy'])

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
        # A recently booted runner cannot subtract 4000 seconds from its monotonic
        # epoch without making the input invalid before the deadline guard.
        # Bind a positive original and advance only the owner's injected clock.
        selection['clock'].update(start=10.0, workEnd=3610.0, totalEnd=3730.0)
        self.base.rebind_selection(args, selection)
        spawn, calls, resources = self.ports(args, selection)
        with self.native_context(home, spawn, resources), \
             patch.object(BAR.Lifetime, 'now', return_value=4010.0), \
             self.assertRaisesRegex(RuntimeError, 'original|deadline'):
            BAR.main(args, IMAGE.__dict__)
        self.assertEqual(calls, [])
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

    def test_low_uptime_subtraction_remains_invalid_input_not_deadline_evidence(self):
        args, selection, home = self.fixture(); args.mode = 'qualify'
        # Reproduce the former fixture on a ten-second-old host. Its negative
        # start must retain the production input refusal, never pass as expiry.
        selection['clock'].update(start=10.0 - 4000, workEnd=3610.0 - 4000,
                                  totalEnd=3730.0 - 4000)
        self.base.rebind_selection(args, selection)
        spawn, calls, resources = self.ports(args, selection)
        with self.native_context(home, spawn, resources), \
             self.assertRaisesRegex(RuntimeError, 'finite clock required'):
            BAR.main(args, IMAGE.__dict__)
        self.assertEqual(calls, [])
        self.assertFalse((Path(args.state_dir) / selection['operationId']).exists())

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
