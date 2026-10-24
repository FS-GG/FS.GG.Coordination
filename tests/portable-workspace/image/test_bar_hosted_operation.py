#!/usr/bin/env python3
"""Inert controls for the actual fixed hosted BAR wrapper; no native engine,
namespace, source acquisition, build, compiler or product bytes are required."""
import argparse
import hashlib
import inspect
import shlex
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import textwrap
import time
import tomllib
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


WRAPPER = load('bar_hosted_operation_controls', ROOT / 'eng/bar_hosted_operation.py')
BAR = WRAPPER.load_bar(ROOT)
ENTRY = load('bar_hosted_entry_controls', ROOT / 'tests/portable-workspace/image/test_bar_hosted_domain.py')
WORKFLOW = ROOT / 'deployment/qualification-runner/native-qualification/bar-hosted-image-qualification.yml'
OPERATION = 'bar-image-123-1'
CALLER = 'c' * 40
RUNNER = 'fsgg-q-20261024t0100z-0123abcd'
GIB = 1024**3


def install_clock(evidence,purpose='full-image',revision='b'*40):
    import datetime
    now=time.monotonic();first=now-1
    utc=datetime.datetime.now(datetime.timezone.utc)-datetime.timedelta(seconds=1)
    text=lambda date:date.isoformat(timespec='microseconds').replace('+00:00','Z')
    clock={'schema':'fsgg.bar-hosted-clock/1','verdict':'accepted','firstRefusal':None,'operationId':OPERATION,
           'sourceRevision':revision,'callerRevision':CALLER,'callerRepository':WRAPPER.CALLER_REPOSITORY,'workflowRef':WRAPPER.WORKFLOW_REF,'purpose':purpose,
           'bootId':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'firstUTC':text(utc),'firstMonotonic':first,
           'rootSelection':{'selected_utc':text(utc),'latest_start_utc':text(utc+datetime.timedelta(seconds=300)),
                            'observer_deadline_utc':text(utc+datetime.timedelta(seconds=5700))},
           'providerJob':{'id':7,'run_id':123,'head_sha':CALLER,'name':'full-image','runner_name':RUNNER,'runner_group_name':'Default','status':'in_progress','started_at':text(utc)},
           'overallDeadlineMonotonic':first+5400}
    def write(name,value):
        path=evidence/name;path.write_bytes(WRAPPER.canonical(value));path.chmod(0o600)
    write('original-clock.json',clock);pin=WRAPPER.digest_file(evidence/'original-clock.json',16384)
    request={'operationId':OPERATION,'clockSha256':pin['sha256'],'bootId':clock['bootId'],'purpose':purpose,
             'requestedMonotonic':now-0.2,'runtimeMaxSeconds':3900,'runtimeLimitMonotonic':first+5400-1350}
    write('unit-request.json',request)
    write('unit-admission.json',{'operationId':OPERATION,'clockSha256':pin['sha256'],
          'requestSha256':WRAPPER.digest_file(evidence/'unit-request.json',1048576)['sha256'],'bootId':clock['bootId'],
          'purpose':purpose,'activeMonotonic':now-0.1,'runtimeDeadlineMonotonic':now-0.1+3900,'properties':{}})
    seal=evidence/'transport-seal.json'
    if seal.exists():
        value=json.loads(seal.read_text());value.update(outerStartMonotonic=first,outerDeadlineMonotonic=first+5400,clockSha256=pin['sha256']);write('transport-seal.json',value)
    return clock


def literal_block(text, key):
    rows = text.splitlines(keepends=True)
    start = next(i for i, row in enumerate(rows) if row.strip() == key + ': |')
    indent = len(rows[start]) - len(rows[start].lstrip())
    following = []
    for row in rows[start + 1:]:
        if row.strip() and len(row) - len(row.lstrip()) <= indent:
            break
        following.append(row)
    return textwrap.dedent(''.join(following))


class CheckoutBootstrap(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='bar-gc-causal-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.environment = {'PATH': '/usr/bin:/bin', 'HOME': str(self.root), 'GIT_CONFIG_NOSYSTEM': '1',
                            'GIT_CONFIG_GLOBAL': '/dev/null', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8'}

    def git(self, root, *args):
        result = subprocess.run(['/usr/bin/git', '-C', str(root), *args], env=self.environment,
                                capture_output=True, check=True, timeout=10)
        return result.stdout.decode().strip()

    def fixture(self, name, origin='https://github.com/FS-GG/FS.GG.Coordination'):
        root = self.root / name; root.mkdir(mode=0o700)
        self.git(root, 'init', '-q', '-b', 'main')
        self.git(root, 'remote', 'add', 'origin', origin)
        (root / 'authored.txt').write_text('inert real-Git checkout control\n')
        self.git(root, 'add', 'authored.txt')
        self.git(root, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Authored fixture')
        return root, self.git(root, 'rev-parse', 'HEAD'), self.git(root, 'rev-parse', 'HEAD^{tree}')

    def strict(self, root, revision, tree):
        def command(label, argv, seconds):
            result = subprocess.run(argv, env=self.environment, capture_output=True, check=True, timeout=10)
            return argparse.Namespace(stdout=result.stdout.decode())
        return BAR.source_identity({}, root, revision, tree, command=command, complete=True)

    def test_real_git_causal_default_checkout_refusal_then_exact_repair(self):
        root, revision, tree = self.fixture('source')
        canonical = (root / '.git/config').read_bytes()
        self.strict(root, revision, tree)
        self.git(root, 'config', '--local', 'gc.auto', '0')
        # Reproduce the pinned action's separate temporary include cleanup,
        # using an inert placeholder rather than any real credential data.
        include = 'includeIf.gitdir:/fixture/.git.path'
        self.git(root, 'config', '--local', include, '/fixture/not-a-credential.config')
        self.git(root, 'config', '--local', '--unset', include)
        with self.assertRaisesRegex(RuntimeError, 'Git configuration executable/external override'):
            self.strict(root, revision, tree)
        global_config, system_config = self.root / 'global', self.root / 'system'
        for path in (global_config, system_config):
            path.write_text('[sentinel]\n\tvalue = unchanged\n')
        with patch.dict(os.environ, {'GIT_CONFIG_GLOBAL': str(global_config), 'GIT_CONFIG_SYSTEM': str(system_config)}):
            result = WRAPPER.canonicalize_checkout_gc(root, revision, tree,
                'https://github.com/FS-GG/FS.GG.Coordination', BAR, complete=True)
        self.assertEqual((root / '.git/config').read_bytes(), canonical)
        self.assertTrue(result['otherConfigBytesUnchanged'])
        self.assertEqual(result['strictSourceIdentity'], 'passed')
        self.assertEqual(global_config.read_bytes(), system_config.read_bytes())
        self.assertEqual(global_config.read_text(), '[sentinel]\n\tvalue = unchanged\n')
        self.strict(root, revision, tree)

    def test_unexpected_missing_duplicate_or_nonzero_config_is_never_normalized(self):
        for case in ('missing', 'nonzero', 'duplicate', 'extra-gc', 'ssh-command', 'include', 'quoted-zero'):
            with self.subTest(case=case):
                root, revision, tree = self.fixture(case)
                if case != 'missing': self.git(root, 'config', '--local', 'gc.auto', '0')
                if case == 'nonzero': self.git(root, 'config', '--local', 'gc.auto', '1')
                elif case == 'duplicate': self.git(root, 'config', '--local', '--add', 'gc.auto', '0')
                elif case == 'extra-gc': self.git(root, 'config', '--local', 'gc.pruneExpire', 'never')
                elif case == 'ssh-command': self.git(root, 'config', '--local', 'core.sshCommand', 'must-not-execute')
                elif case == 'include': self.git(root, 'config', '--local', 'include.path', '/must-not-read')
                elif case == 'quoted-zero':
                    path = root / '.git/config'; path.write_bytes(path.read_bytes().replace(b'auto = 0', b'auto = "0"'))
                before = (root / '.git/config').read_bytes()
                with self.assertRaises(RuntimeError):
                    WRAPPER.canonicalize_checkout_gc(root, revision, tree,
                        'https://github.com/FS-GG/FS.GG.Coordination', BAR, complete=True)
                self.assertEqual((root / '.git/config').read_bytes(), before)

    def test_dirty_incomplete_or_wrong_revision_checkout_is_not_modified(self):
        for case in ('dirty', 'shallow', 'revision', 'tree', 'hook', 'alternates'):
            with self.subTest(case=case):
                root, revision, tree = self.fixture(case)
                self.git(root, 'config', '--local', 'gc.auto', '0')
                before = (root / '.git/config').read_bytes()
                if case == 'dirty': (root / 'authored.txt').write_text('changed')
                elif case == 'shallow': (root / '.git/shallow').write_text(revision + '\n')
                elif case == 'revision': revision = '0' * 40
                elif case == 'tree': tree = '0' * 40
                elif case == 'hook': (root / '.git/hooks/pre-commit').write_text('must not execute')
                elif case == 'alternates': (root / '.git/objects/info/alternates').write_text('/must-not-read\n')
                with self.assertRaises(RuntimeError):
                    WRAPPER.canonicalize_checkout_gc(root, revision, tree,
                        'https://github.com/FS-GG/FS.GG.Coordination', BAR, complete=True)
                self.assertEqual((root / '.git/config').read_bytes(), before)

    def test_actual_two_checkout_bootstrap_and_first_refusal_receipt(self):
        source, revision, tree = self.fixture('source')
        product, product_revision, product_tree = self.fixture('product', 'https://github.com/FS-GG/FSBarV2')
        (self.root / 'evidence').mkdir(mode=0o700)
        for path in (source, product): self.git(path, 'config', '--local', 'gc.auto', '0')
        env = {'GITHUB_WORKSPACE': str(self.root), 'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Native.Qualification',
               'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1', 'QUALIFICATION_MODE': 'qualify'}
        with patch.dict(os.environ, env), patch.object(WRAPPER, 'load_bar', return_value=BAR), \
             patch.object(WRAPPER, 'PRODUCT_REVISION', product_revision), patch.object(WRAPPER, 'PRODUCT_TREE', product_tree):
            result = WRAPPER.canonicalize_checkouts(revision)
            self.assertIsNone(result['firstRefusal'])
            self.assertEqual(result['source']['strictSourceIdentity'], 'passed')
            self.assertEqual(result['product']['strictSourceIdentity'], 'passed')
            # A second call cannot silently reinterpret a canonical checkout as
            # new checkout-created gc metadata; the first refusal is retained.
            with self.assertRaises(RuntimeError): WRAPPER.canonicalize_checkouts(revision)
        refusal = json.loads((self.root / 'evidence/checkout-config-refusal.json').read_text())
        self.assertIn('expected singleton', refusal['firstRefusal'])


class Preparation(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.allocation, self.evidence, self.home = [self.root / name for name in ('allocation', 'evidence', 'home')]
        for path in (self.allocation, self.evidence, self.home, self.allocation / 'context'):
            path.mkdir(mode=0o700)
        self.product_pins = {}
        for name in WRAPPER.PRODUCT_FILES:
            path = self.allocation / 'context' / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('authored synthetic product control ' + name)
            self.product_pins[name] = hashlib.sha256(path.read_bytes()).hexdigest()
        native = self.root / 'native'
        native.mkdir()
        roles = set(BAR.SUPPORT_EXECUTABLES) | set(BAR.SUPPORT_LIBRARIES) | {'builder'}
        self.native = {}
        for role in roles:
            path = native / role
            path.write_text('authored inert support ' + role)
            path.chmod(0o500)
            self.native[role] = str(path)
        for target, name, value in [
            (BAR, 'network_seed', lambda: {'files': {}, 'aliases': {}}),
            (WRAPPER, 'RUNNER_UID', os.getuid()), (WRAPPER, 'BUILDER', self.native['builder']),
            (WRAPPER, 'PRODUCT_FILES', self.product_pins),
            (BAR, 'SUPPORT_EXECUTABLES', {key: self.native[key] for key in BAR.SUPPORT_EXECUTABLES}),
            (BAR, 'SUPPORT_LIBRARIES', {key: self.native[key] for key in BAR.SUPPORT_LIBRARIES}),
        ]:
            change = patch.object(target, name, value)
            change.start(); self.addCleanup(change.stop)
        change = patch.dict(os.environ, {'HELPER_TREE': 'a' * 40, 'HOST_JOB_STARTED': str(int(time.time())),
                                        'HOST_JOB_MONOTONIC': str(time.monotonic()),'FSGG_SOURCE_REVISION':'b'*40})
        change.start(); self.addCleanup(change.stop)
        device = BAR.shared.device_text(self.allocation) if hasattr(BAR.shared, 'device_text') else None
        dev = self.allocation.stat().st_dev
        self.device = device or f'{os.major(dev)}:{os.minor(dev)}'
        self.domain = {
            'schema': BAR.HOSTED_DOMAIN_SCHEMA, 'relation': BAR.HOSTED_RELATION, 'verdict': 'admitted', 'refusals': [],
            'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
            'runner': {'imageOS': 'ubuntu24', 'imageVersion': BAR.HOSTED_RUNNER_IMAGE_VERSION, 'githubHosted': True},
            'services': {'stopped': list(BAR.HOSTED_STOPPED_SERVICES), 'active': [], 'daemonProcesses': 0},
            'userns': {'apparmorRestrictUnprivileged': 0, 'maxUserNamespaces': 60000},
            'volume': {'device': self.device, 'backingDevice': '8:2' if self.device == '8:1' else '8:1',
                       'filesystem': 'ext4', 'bytes': 120 * GIB, 'allocatedBytes': 120 * GIB,
                       'freeBytes': 100 * GIB, 'freeInodes': 1024, 'discard': 'nodiscard'},
            'builder': {'version': 'buildah version 1.43.1'},
            'resources': {'memAvailableBytes': 15 * GIB, 'logicalCpus': 4, 'affinity': 4}}
        self.write_domain()
        (self.evidence / 'facts.env').write_text('userland.pastaPackage=passt 2026_10_02.cba3570-1\n')
        WRAPPER.write_new(self.evidence / 'root-security.json', {'schema': 'fsgg.bar-network-security/1', 'bootId': self.domain['bootId'], 'observations': {}})
        start = float(os.environ['HOST_JOB_MONOTONIC'])
        WRAPPER.write_new(self.evidence / 'transport-seal.json', {
            'operationId': OPERATION, 'bootId': self.domain['bootId'], 'outerStartMonotonic': start,
            'outerDeadlineMonotonic': start + WRAPPER.OUTER_SECONDS,
            'recipientDerSha256': WRAPPER.RECIPIENT_DER_SHA256})
        install_clock(self.evidence)

    def write_domain(self):
        path = self.evidence / 'domain.json'
        path.write_bytes(WRAPPER.canonical(self.domain)); path.chmod(0o600)

    def prepare(self, operation=OPERATION, revision='b' * 40):
        return WRAPPER.prepare(operation, revision, source=ROOT, allocation=self.allocation,
                               evidence=self.evidence, home=self.home, bar=BAR)

    def test_real_preparation_matches_actual_closed_reader_and_namespaces(self):
        selection, path, digest = self.prepare()
        actual, _ = BAR.selected_input(argparse.Namespace(operation_input=str(path), expected_operation_input_sha256=digest))
        self.assertEqual(actual, selection)
        observed = BAR.hosted_domain(actual, self.allocation, Path(actual['load']['root']))
        self.assertEqual(observed['volumeDevice'], self.device)
        for kind in ('build', 'load'):
            store = selection[kind]
            BAR.check_namespace(self.allocation / kind, Path(store['root']), Path(store['runroot']))
        self.assertEqual(selection['clock']['workEnd'], selection['clock']['start'] + 3600)
        self.assertEqual(selection['clock']['totalEnd'], selection['clock']['workEnd'] + 120)
        self.assertEqual(selection['product']['revision'], WRAPPER.PRODUCT_REVISION)
        self.assertFalse((self.allocation / 'state' / OPERATION).exists(), 'preparation must not consume controller original')
        with self.assertRaises(FileExistsError):
            self.prepare()

    def test_fixed_prerequisite_purpose_uses_the_same_managed_preflight(self):
        install_clock(self.evidence,'network-prerequisite')
        selection, target, digest = WRAPPER.prepare(OPERATION, 'b' * 40, source=ROOT,
            allocation=self.allocation, evidence=self.evidence, home=self.home, bar=BAR,
            purpose='network-prerequisite')
        argv = WRAPPER.controller_argv(selection, target, digest, source=ROOT, allocation=self.allocation)
        self.assertEqual(argv[4], 'preflight')
        self.assertEqual(selection['network']['purpose'], 'network-prerequisite')
        self.assertEqual(selection['clock']['workEnd'], selection['clock']['start'] + 300)
        self.assertEqual(selection['clock']['totalEnd'], selection['clock']['workEnd'] + 120)
        self.assertNotIn('qualify', argv)

    def test_bad_source_domain_stores_and_original_clock_refuse_before_exec(self):
        cases = ['wrong-product', 'stale-boot', 'root-refusal', 'occupied-store', 'consumed', 'expired-outer', 'bad-tree', 'missing-support']
        for case in cases:
            # A fresh fixture per negative keeps independent state valid.
            with self.subTest(case=case):
                fixture = Preparation('test_real_preparation_matches_actual_closed_reader_and_namespaces')
                fixture.setUp()
                try:
                    if case == 'wrong-product':
                        (fixture.allocation / 'context' / next(iter(fixture.product_pins))).write_text('changed')
                    elif case == 'stale-boot': fixture.domain['bootId'] = '00000000-0000-0000-0000-000000000000'
                    elif case == 'root-refusal': fixture.domain.update(verdict='refused', refusals=['memory'])
                    elif case == 'occupied-store':
                        path = fixture.allocation / 'build/graphroot'
                        path.mkdir(parents=True, mode=0o700); (path / 'occupied').write_text('probe metadata')
                    elif case == 'consumed':
                        (fixture.allocation / 'state' / OPERATION).mkdir(parents=True, mode=0o700)
                    elif case == 'expired-outer': (fixture.evidence/'original-clock.json').unlink()
                    elif case == 'bad-tree': os.environ['HELPER_TREE'] = 'missing'
                    elif case == 'missing-support': Path(fixture.native['builder']).unlink()
                    fixture.write_domain()
                    with patch.object(os, 'execve') as execute, self.assertRaises((RuntimeError, FileExistsError, FileNotFoundError)):
                        fixture.prepare()
                    execute.assert_not_called()
                finally:
                    fixture.doCleanups()

    def test_fixed_controller_entrypoint_and_no_arbitrary_command_port(self):
        selection, target, digest = self.prepare()
        argv = WRAPPER.controller_argv(selection, target, digest, source=ROOT, allocation=self.allocation)
        self.assertEqual(argv[1:5], ['-I', '-B', str(ROOT / 'eng/portable-workspace-image.py'), 'qualify'])
        self.assertEqual(argv[argv.index('--profile') + 1], 'bar-headless')
        self.assertEqual(argv[argv.index('--operation-input') + 1], str(target))
        self.assertNotIn('preflight', argv)
        with patch.object(WRAPPER, 'prepare', return_value=(selection, target, digest)), patch.object(os, 'execve') as execute:
            WRAPPER.main(['qualify', '--operation-id', OPERATION, '--source-revision', 'b' * 40])
        self.assertEqual(execute.call_count, 1)
        called = execute.call_args.args
        self.assertIn('--profile', called[1])
        self.assertNotIn('GITHUB_TOKEN', called[2])
        self.assertEqual(called[2]['HOME'], '/home/runner')
        with patch.object(os, 'execve') as execute, self.assertRaises(RuntimeError):
            WRAPPER.main(['qualify', '--operation-id', '../resume', '--source-revision', 'b' * 40])
        execute.assert_not_called()
        for error in (FileNotFoundError('Python disappeared'), TimeoutError('outer observation expired')):
            with patch.object(WRAPPER, 'prepare', return_value=(selection, target, digest)), \
                 patch.object(os, 'execve', side_effect=error), self.assertRaises(type(error)):
                WRAPPER.main(['qualify', '--operation-id', OPERATION, '--source-revision', 'b' * 40])

    def test_actual_controller_rejects_shallow_and_external_git_before_subprocess(self):
        source = self.root / 'git-fixture'
        source.mkdir(); (source / '.git').mkdir()
        (source / '.git/shallow').write_text('a' * 40)
        with self.assertRaisesRegex(RuntimeError, 'complete source history'):
            BAR.shared.source_identity({}, source, 'a' * 40, 'b' * 40)
        (source / '.git/shallow').unlink()
        (source / '.git/commondir').write_text('/external')
        with self.assertRaisesRegex(RuntimeError, 'external Git'):
            BAR.shared.source_identity({}, source, 'a' * 40, 'b' * 40)


class Transport(unittest.TestCase):
    def setUp(self):
        change = patch.object(WRAPPER, 'CRYPTO_OBSERVATIONS', [])
        change.start(); self.addCleanup(change.stop)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.allocation, self.evidence, self.transport = [self.root / name for name in ('allocation', 'evidence', 'transport')]
        self.state = self.allocation / 'state' / OPERATION
        self.state.mkdir(parents=True, mode=0o700); self.evidence.mkdir(mode=0o700)
        self.candidate = self.state / 'candidate.oci.tar'
        self.candidate.write_bytes(b'authored inert archive bytes; no OCI image or product')
        self.selection = {'network': {'purpose': 'full-image'}, 'helper': {'revision': 'a' * 40}, 'product': {'revision': WRAPPER.PRODUCT_REVISION}, 'clock': {'start': 1}}
        WRAPPER.write_new(self.evidence / 'transport-seal.json', {'synthetic': True, 'outerDeadlineMonotonic': time.monotonic() + 3600})
        install_clock(self.evidence)
        WRAPPER.write_new(self.evidence / 'topology.json', {
            'transportSealSha256': WRAPPER.digest_file(self.evidence / 'transport-seal.json', 1024**2)['sha256']})
        self.selection['admission'] = {'topology': {'sha256': WRAPPER.digest_file(self.evidence / 'topology.json', 1024**2)['sha256']}}
        selection_path = self.evidence / 'operation-input.json'
        selection_path.write_bytes(WRAPPER.canonical(self.selection))
        result = {'imageId': 'sha256:' + 'c' * 64, 'archive': str(self.candidate),
                  'archiveSha256': hashlib.sha256(self.candidate.read_bytes()).hexdigest()}
        self.manifest = {'profile': 'bar-headless', 'operationId': OPERATION, 'firstCause': None,
                         **self.selection, 'inputSha256': hashlib.sha256(selection_path.read_bytes()).hexdigest(), 'result': result}
        self.manifest_dir = self.state / 'manifests'; self.manifest_dir.mkdir(mode=0o700)
        self.result = {**result, 'operationId': OPERATION,
                      'scope': 'ordinary sampled profile; native/product and whole descendant acceptance separate'}
        self.write_result()
        (self.state / '0001.stdout').write_bytes(b'bounded original command stdout')
        (self.evidence / 'qualification.stderr.log').write_bytes(b'')
        def encrypt(operation_id, candidate, archive, **kwargs):
            path = self.root / 'candidate.cms'
            path.write_bytes(b'authored synthetic ciphertext port; not a product image')
            return {'path': str(path), **WRAPPER.digest_file(path, 1024**2)}
        change = patch.object(WRAPPER, 'encrypt_candidate', side_effect=encrypt)
        change.start(); self.addCleanup(change.stop)

    def write_result(self):
        payload = WRAPPER.canonical(self.manifest)
        path = self.manifest_dir / ('sha256-' + hashlib.sha256(payload).hexdigest() + '.json')
        path.write_bytes(payload)
        self.result['manifest'] = str(path)
        (self.evidence / 'qualification-result.json').write_bytes(WRAPPER.canonical(self.result))

    def receipt(self):
        with patch.object(WRAPPER.os, 'statvfs', return_value=argparse.Namespace(f_bavail=20 * GIB, f_frsize=1)):
            return WRAPPER.receipt(OPERATION, allocation=self.allocation, evidence=self.evidence, transport=self.transport)

    def test_malformed_optional_json_cannot_discard_primary_metadata(self):
        self.candidate.unlink()
        (self.evidence / 'qualification-result.json').write_bytes(b'')
        stage = self.root / ('fsgg-' + OPERATION + '-security'); stage.mkdir(mode=0o700)
        (stage / 'status').write_text('0\n'); (stage / 'stderr').write_bytes(b'')
        (stage / 'readback.json').write_bytes(b'{"security":{"bootId":"invalid"},"number":NaN}')
        with patch.dict(os.environ, {'RUNNER_TEMP': str(self.root)}), patch.object(WRAPPER, 'encrypt_candidate') as encrypt:
            value = WRAPPER.receipt(OPERATION, allocation=self.allocation, evidence=self.evidence,
                                    transport=self.transport, security_stage=stage)
        encrypt.assert_not_called()
        self.assertEqual(value['securityReadback']['status'], 'Unknown')
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertTrue((self.transport / OPERATION / 'state/0001.stdout').exists())

    def test_prerequisite_success_is_metadata_only_and_never_encrypts(self):
        self.candidate.unlink()
        self.selection['network']['purpose'] = 'network-prerequisite'
        install_clock(self.evidence,'network-prerequisite')
        selected = self.evidence / 'operation-input.json'; selected.write_bytes(WRAPPER.canonical(self.selection))
        result = {'profile': 'bar-headless', 'preflight': 'passed', 'candidateQualified': False,
                  'networkPrerequisite': {'imageId': 'sha256:' + 'e' * 64, 'scope': 'authored prerequisite control'}}
        self.manifest.update(result=result, inputSha256=hashlib.sha256(selected.read_bytes()).hexdigest())
        self.result = {**result, 'operationId': OPERATION}
        self.write_result()
        with patch.object(WRAPPER, 'encrypt_candidate') as encrypt:
            value = WRAPPER.receipt(OPERATION, allocation=self.allocation, evidence=self.evidence, transport=self.transport)
        encrypt.assert_not_called()
        self.assertTrue(value['networkPrerequisitePassed'])
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertIsNone(value['candidate']); self.assertIsNone(value['encryptedCandidate'])
        self.assertEqual(value['cryptoCommands'], [])
        self.assertTrue((self.transport / OPERATION / 'state/0001.stdout').exists())

    def test_valid_exact_manifest_archive_and_original_streams_are_staged(self):
        value = self.receipt()
        self.assertTrue(value['candidateTransportPermitted'])
        stage = self.transport / OPERATION
        self.assertEqual((stage / 'state/0001.stdout').read_bytes(), b'bounded original command stdout')
        self.assertFalse(any(path.name == 'candidate.oci.tar' for path in stage.rglob('*')))
        self.assertEqual(value['candidate']['sha256'], hashlib.sha256(self.candidate.read_bytes()).hexdigest())
        self.assertEqual(value['providerPhysicalDisposal'], 'Unknown')
        with self.assertRaisesRegex(RuntimeError, 'already consumed'):
            self.receipt()

    def test_original_failed_or_malformed_result_retains_metadata_without_candidate_permission(self):
        (self.evidence / 'qualification-result.json').write_bytes(b'')
        (self.evidence / 'qualification.stderr.log').write_bytes(b'original first cause')
        value = self.receipt()
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertIn('malformed', value['transportRefusals'][0])
        self.assertEqual((self.transport / OPERATION / 'evidence/qualification.stderr.log').read_bytes(), b'original first cause')

    def assert_refused_with_original_stream(self, value, message):
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertIn(message, value['transportRefusals'][0])
        self.assertEqual((self.transport / OPERATION / 'state/0001.stdout').read_bytes(), b'bounded original command stdout')

    def test_changed_archive_retains_original_stream_without_candidate_permission(self):
        self.candidate.write_bytes(b'changed archive')
        self.assert_refused_with_original_stream(self.receipt(), 'archive identity mismatch')

    def test_failed_manifest_retains_original_stream_without_candidate_permission(self):
        self.manifest['firstCause'] = 'original failed command'
        self.write_result()
        self.assert_refused_with_original_stream(self.receipt(), 'manifest correspondence')

    def test_duplicate_result_fields_cannot_authorize_transport(self):
        path = self.evidence / 'qualification-result.json'
        payload = path.read_text().strip()
        path.write_text('{"operationId":"wrong",' + payload[1:])
        value = self.receipt()
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertIn('duplicate JSON', value['transportRefusals'][0])

    def test_encryption_failure_retains_original_controller_evidence_without_candidate_permission(self):
        with patch.object(WRAPPER, 'encrypt_candidate', side_effect=RuntimeError('original crypto first cause')):
            value = self.receipt()
        self.assertFalse(value['candidateTransportPermitted'])
        self.assertIn('original crypto first cause', value['transportRefusals'][0])
        self.assertEqual((self.transport / OPERATION / 'state/0001.stdout').read_bytes(), b'bounded original command stdout')

    def test_oversized_candidate_retains_original_stream_without_candidate_permission(self):
        with patch.object(WRAPPER, 'ARCHIVE_BYTES', 1):
            value = self.receipt()
        self.assert_refused_with_original_stream(value, 'oversized')

    def test_candidate_staging_headroom_refusal_preserves_original_streams(self):
        with patch.object(WRAPPER.os, 'statvfs', return_value=argparse.Namespace(f_bavail=1, f_frsize=1)):
            value = WRAPPER.receipt(OPERATION, allocation=self.allocation, evidence=self.evidence, transport=self.transport)
        self.assert_refused_with_original_stream(value, 'staging headroom')

    def test_unsafe_metadata_bounds_or_links_still_refuse(self):
        with patch.object(WRAPPER, 'METADATA_BYTES', 1), self.assertRaisesRegex(RuntimeError, 'metadata transport bound'):
            self.receipt()
        (self.state / 'linked').symlink_to(self.root)
        with self.assertRaisesRegex(RuntimeError, 'linked artifact'):
            self.receipt()


class Workflow(unittest.TestCase):
    def test_optional_privileged_failure_preserves_actual_receipt_and_primary_upload_path(self):
        self.optional_workflow_cases('privileged-failure')

    def test_current_boot_overflow_preserves_actual_workflow_receipt(self):
        self.optional_workflow_cases('overflow')

    def optional_workflow_cases(self, secondary):
        text = WORKFLOW.read_text()
        receipt_script = literal_block(text.split('      - name: Retain the bounded generation receipt', 1)[1], 'run')
        summary_script = literal_block(text.split('      - name: Summarize the complete generation', 1)[1], 'run')
        for primary in ('failed', 'prerequisite'):
            with self.subTest(primary=primary, secondary=secondary):
                fixture = Transport('test_valid_exact_manifest_archive_and_original_streams_are_staged')
                fixture.setUp()
                try:
                    fixture.candidate.unlink()
                    (fixture.evidence / 'domain.json').write_text('{}')
                    (fixture.evidence / 'qualification.stderr.log').write_bytes(b'original native first-cause125')
                    if primary == 'failed':
                        fixture.manifest.update(firstCause='command failed with exit125', result=None)
                        fixture.write_result()
                        (fixture.evidence / 'qualification-result.json').write_bytes(b'')
                    else:
                        fixture.selection['network']['purpose'] = 'network-prerequisite'
                        install_clock(fixture.evidence,'network-prerequisite')
                        selected = fixture.evidence / 'operation-input.json'
                        selected.write_bytes(WRAPPER.canonical(fixture.selection))
                        result = {'profile': 'bar-headless', 'preflight': 'passed', 'candidateQualified': False,
                                  'networkPrerequisite': {'scope': 'authored prerequisite control'}}
                        fixture.manifest.update(result=result,
                            inputSha256=hashlib.sha256(selected.read_bytes()).hexdigest())
                        fixture.result = {**result, 'operationId': OPERATION}
                        fixture.write_result()
                    tools = fixture.root / 'tools'; tools.mkdir()
                    runner_temp = fixture.root / 'runner-temp'; runner_temp.mkdir(mode=0o700)
                    # The inert privileged port either fails with partial output
                    # or returns valid JSON with current boot and float overflow.
                    # Actual canonical receipt runs in a genuine Python child.
                    (tools / 'sudo').write_text("""#!/bin/sh
previous=
for argument in "$@"; do
  if [ "$previous" = --evidence ]; then
    if [ "$FIXTURE_SECONDARY" = overflow ]; then
      printf '{"security":{"bootId":"%s"},"number":1e999}' "$FIXTURE_CURRENT_BOOT" > "$argument"
    else
      printf '%s' '{partial privileged output' > "$argument"
      chmod 000 "$argument"
    fi
  fi
  previous="$argument"
done
if [ "$FIXTURE_SECONDARY" = overflow ]; then exit 0; fi
printf '%s\n' 'authored secondary readback refusal' >&2
exit 41
""")
                    (tools / 'python3').write_text(
                        '#!/usr/bin/python3\nimport importlib.util,json,os,pathlib,sys\n'
                        'spec=importlib.util.spec_from_file_location("actual_receipt",'
                        + repr(str(ROOT / 'eng/bar_hosted_operation.py')) + ')\n'
                        'module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)\n'
                        'assert "receipt" in sys.argv\n'
                        'root=pathlib.Path(os.environ["FIXTURE_ROOT"])\n'
                        'stage=sys.argv[sys.argv.index("--security-stage")+1] if "--security-stage" in sys.argv else None\n'
                        'value=module.receipt(' + repr(OPERATION) + ',allocation=root/"allocation",'
                        'evidence=root/"evidence",transport=root/"transport",security_stage=stage)\n'
                        'print(json.dumps({"candidate":value["candidateTransportPermitted"]}))\n')
                    for child in tools.iterdir(): child.chmod(0o700)
                    outputs = fixture.root / 'outputs'
                    env = {**os.environ, 'PATH': str(tools) + ':/usr/bin:/bin', 'FIXTURE_ROOT': str(fixture.root),
                           'EVIDENCE': str(fixture.evidence), 'RUNNER_TEMP': str(runner_temp),
                           'FIXTURE_SECONDARY': secondary,
                           'FIXTURE_CURRENT_BOOT': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                           'OPERATION_ID': OPERATION, 'HOST_JOB_STARTED': '1700000000', 'GITHUB_OUTPUT': str(outputs),
                           'GUARD_OUTCOME': 'success'}
                    done = subprocess.run(['/bin/bash', '-c', receipt_script], env=env,
                                          capture_output=True, text=True, timeout=15)
                    self.assertEqual(done.returncode, 0, done.stderr)
                    value = json.loads((fixture.evidence / 'transport-receipt.json').read_text())
                    self.assertEqual(value['securityReadback']['status'], 'Unknown')
                    self.assertEqual(value['securityReadback']['exit'], 0 if secondary == 'overflow' else 41)
                    if secondary == 'overflow':
                        self.assertIn('Out of range float', value['securityReadback']['reason'])
                    self.assertFalse(value['candidateTransportPermitted'])
                    self.assertEqual(value['networkPrerequisitePassed'], primary == 'prerequisite')
                    stage = fixture.transport / OPERATION
                    self.assertEqual((stage / 'evidence/qualification.stderr.log').read_bytes(),
                                     b'original native first-cause125')
                    self.assertTrue((stage / 'state/0001.stdout').exists())
                    self.assertFalse((stage / 'evidence/host-security-readback.json').exists())
                    self.assertIn('candidate-permitted=false', outputs.read_text())
                    # This is exactly the maintained metadata uploader's path.
                    self.assertIn("format('/fsgg-bar/transport-metadata/{0}'", text)
                    summary_env = {**env, 'FSGG_SOURCE_REVISION': 'b' * 40, 'GITHUB_SHA': 'c' * 40, 'GITHUB_STEP_SUMMARY': str(fixture.root / 'summary'),
                        'QUALIFICATION_MODE': 'network-prerequisite', 'ADMIT_OUTCOME': 'success',
                        'QUALIFY_OUTCOME': 'failure' if primary == 'failed' else 'success',
                        'RECEIPT_OUTCOME': 'success', 'METADATA_OUTCOME': 'success', 'CANDIDATE_OUTCOME': 'skipped',
                        'CANDIDATE_PERMITTED': 'false', 'PREREQUISITE_PASSED': str(primary == 'prerequisite').lower()}
                    summary = subprocess.run(['/bin/bash', '-c', summary_script], env=summary_env,
                                             capture_output=True, text=True, timeout=10)
                    self.assertEqual(summary.returncode == 0, primary == 'prerequisite')
                finally:
                    fixture.doCleanups()

    def test_actual_unit_command_keeps_capture_files_owned_by_the_invoking_runner(self):
        text = WORKFLOW.read_text().split('      - name: Qualify one complete BAR image', 1)[1]
        script = literal_block(text, 'run')
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tools = root / 'tools'; tools.mkdir()
            (tools / 'python3').write_text('#!/bin/sh\nprintf \"3900\\n\"\n')
            (tools / 'git').write_text('#!/bin/sh\nprintf "%s\\n" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n')
            (tools / 'sudo').write_text('#!/bin/sh\nprintf \'{"fixture":"result"}\\n\'\nprintf \'fixture diagnostic\\n\' >&2\nexit "$FIXTURE_EXIT"\n')
            for path in tools.iterdir():
                path.chmod(0o700)
            for code in (0, 41):
                evidence = root / str(code); evidence.mkdir(mode=0o700)
                env = {**os.environ, 'PATH': str(tools) + ':/usr/bin:/bin', 'EVIDENCE': str(evidence),
                       'ENTER_SCRIPT': 'authored inert entry fixture', 'UNIT_ENTRY_SCRIPT':'authored inert unit fixture', 'OPERATION_ID': OPERATION, 'QUALIFICATION_MODE': 'qualify',
                       'USERLAND': '/fixture-userland', 'VOLUME': '/fixture-volume', 'GITHUB_WORKSPACE': str(root),
                       'GITHUB_SHA': 'c' * 40, 'FSGG_SOURCE_REVISION': 'b' * 40, 'HOST_JOB_STARTED': str(int(time.time())),
                       'HOST_JOB_MONOTONIC': str(time.monotonic()), 'FIXTURE_EXIT': str(code)}
                result = subprocess.run(['/bin/bash', '-c', script], env=env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, code, result.stderr)
                captured, diagnostic = evidence / 'qualification-result.json', evidence / 'qualification.stderr.log'
                self.assertEqual(captured.read_text(), '{"fixture":"result"}\n')
                self.assertEqual(diagnostic.read_text(), 'fixture diagnostic\n')
                self.assertEqual((captured.stat().st_uid, diagnostic.stat().st_uid), (os.getuid(), os.getuid()))
                if code:
                    self.assertEqual(result.stderr, 'fixture diagnostic\n')

    def test_actual_transport_defaults_are_provisioned_private_for_the_runner(self):
        text = WORKFLOW.read_text()
        command = next(row.strip() for row in text.splitlines() if row.strip().startswith('sudo install -d -m 0700 -o 1001 -g 1001'))
        def validate(command):
            argv = shlex.split(command)
            self.assertEqual(argv[:10], ['sudo', 'install', '-d', '-m', '0700', '-o', '1001', '-g', '1001', '$VOLUME/allocation'])
            paths = {path.replace('$VOLUME', '/fsgg-bar') for path in argv[9:]}
            for method in (WRAPPER.receipt, WRAPPER.encrypt_candidate):
                actual = str(inspect.signature(method).parameters['transport'].default)
                self.assertIn(actual, paths, 'actual helper transport parent must be uid1001/private before receipt')
        validate(command)
        for missing in ('transport-candidate', 'transport-metadata'):
            broken = command.replace(' "$VOLUME/' + missing + '"', '')
            with self.assertRaises(AssertionError):
                validate(broken)

    def test_first_actual_shell_guard_blocks_rerun_wrong_event_or_ref_before_checkout(self):
        fixture=ClockBudget();fixture.setUp()
        try:
            for index,update in enumerate(({'GITHUB_RUN_ATTEMPT':'2'},{'GITHUB_EVENT_NAME':'push'},{'GITHUB_REF':'refs/heads/topic'})):
                fixture.env['STARTUP_EVIDENCE']=str(fixture.root/('guard-'+str(index)))
                with self.assertRaisesRegex(ValueError,'first dispatch'):fixture.produce(update)
                self.assertEqual(json.loads((Path(fixture.env['STARTUP_EVIDENCE'])/'clock.json').read_text())['verdict'],'refused')
        finally:fixture.doCleanups()

    def test_owned_userland_does_not_change_host_namespace_policy(self):
        body,environment=WRAPPER.fixed_preparation('userland')
        commands='\n'.join(line for line in body.splitlines() if not line.lstrip().startswith('#'))
        self.assertNotRegex(commands,r'(?m)^\s*(?:sudo\s+)?(?:/[^\s]+/)?sysctl\b')
        self.assertNotRegex(commands,r'(?:>|tee[^\n]*)\s*/proc/sys/')
        # Package helper capability restoration is inside the new owned Arch
        # userland PACMAN_SCRIPT, not a live-host namespace policy repair.
        self.assertIn('exec chroot "$USERLAND"',body)
        self.assertIn('/bin/bash -c "$PACMAN_SCRIPT"',body)
        self.assertIn('for helper in newuidmap newgidmap; do',environment['PACMAN_SCRIPT'])
        self.assertIn('setcap "$capability=ep" "/usr/bin/$helper"',environment['PACMAN_SCRIPT'])
        self.assertNotIn('setcap ',commands)

    def test_preprovisioned_namespace_capability_refuses_restricted_or_unknown(self):
        value=ENTRY.observed()
        self.assertEqual(ENTRY.DOMAIN.finish(value)['verdict'],'admitted')
        for restriction,maximum in ((1,63838),(None,63838),(0,0),(0,None)):
            with self.subTest(restriction=restriction,maximum=maximum):
                observed=ENTRY.observed()
                observed['userns'].update(apparmorRestrictUnprivileged=restriction,maxUserNamespaces=maximum)
                before=json.loads(json.dumps(observed))
                self.assertEqual(ENTRY.DOMAIN.finish(observed)['refusals'],['rootless-userns'])
                self.assertEqual(observed,before,'read-only admission must not normalize host facts')

    def test_source_pins_and_registration_match_actual_workflow(self):
        WRAPPER.load_bar(ROOT)
        self.assertEqual(hashlib.sha256(WRAPPER.RECIPIENT.read_bytes()).hexdigest(), WRAPPER.RECIPIENT_PEM_SHA256)
        self.assertNotIn(b'PRIVATE KEY', WRAPPER.RECIPIENT.read_bytes())
        # The template is outside .github/workflows by design: Coordination can never queue it.
        # Its bytes and the private-repository README are pinned by one manifest, and every
        # external action it uses is an immutable full-length commit.
        template = WORKFLOW.parent
        pins = {line.split('  ', 1)[1]: line.split('  ', 1)[0] for line in (template / 'MANIFEST.sha256').read_text().splitlines()}
        self.assertEqual(sorted(pins), ['README.md', 'actionlint.yaml', 'bar-hosted-image-qualification.yml'])
        for name, digest in pins.items():
            self.assertEqual(hashlib.sha256((template / name).read_bytes()).hexdigest(), digest, name)
        uses = set(re.findall(r'^\s*(?:-\s*)?uses:\s*([^\s#]+)', WORKFLOW.read_text(), re.MULTILINE))
        self.assertEqual(uses, {'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'})
        self.assertFalse((ROOT / '.github/workflows/bar-hosted-image-qualification.yml').exists())
        registry = (ROOT / 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json').read_text()
        self.assertNotIn('bar-hosted-image-qualification', registry)

    def test_guard_step_accepts_only_main_ancestor_commit_and_identical_caller(self):
        text = WORKFLOW.read_text()
        script = literal_block(text.split('- name: Refuse an unreviewed Coordination commit or a changed caller', 1)[1], 'run')
        template_directory = 'deployment/qualification-runner/native-qualification'
        def git(path, *arguments):
            return subprocess.run(['git', '-C', str(path), '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', *arguments],
                                  capture_output=True, text=True, check=True).stdout.strip()
        def run(root, revision, caller):
            env = {'PATH': '/usr/bin:/bin', 'FSGG_SOURCE_REVISION': revision, 'GITHUB_SHA': caller}
            return subprocess.run(['/bin/bash', '-c', script], cwd=root, env=env, capture_output=True, text=True, timeout=20)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, caller = root / 'source', root / 'caller'
            for path in (source, caller):
                path.mkdir(); git(path, 'init', '-q', '-b', 'main')
            names = ('bar-hosted-image-qualification.yml', 'README.md', 'actionlint.yaml')
            (source / template_directory).mkdir(parents=True)
            for name in names:
                (source / template_directory / name).write_bytes((ROOT / template_directory / name).read_bytes())
            git(source, 'add', '-A'); git(source, 'commit', '-qm', 'main commit')
            reviewed = git(source, 'rev-parse', 'HEAD')
            git(source, 'update-ref', 'refs/remotes/origin/main', reviewed)
            git(source, 'checkout', '-q', '-b', 'side'); (source / 'extra').write_text('x'); git(source, 'add', '-A'); git(source, 'commit', '-qm', 'side')
            unreviewed = git(source, 'rev-parse', 'HEAD')
            (caller / '.github/workflows').mkdir(parents=True)
            copies = {'.github/workflows/bar-hosted-image-qualification.yml': names[0], 'README.md': names[1], '.github/actionlint.yaml': names[2]}
            for target, name in copies.items():
                (caller / target).write_bytes((ROOT / template_directory / name).read_bytes())
            git(caller, 'add', '-A'); git(caller, 'commit', '-qm', 'caller')
            head = git(caller, 'rev-parse', 'HEAD')
            git(source, 'checkout', '-q', reviewed)
            self.assertEqual(run(root, reviewed, head).returncode, 0, run(root, reviewed, head).stderr)
            self.assertNotEqual(run(root, reviewed, 'a' * 40).returncode, 0, 'caller head differs from GITHUB_SHA')
            git(source, 'checkout', '-q', unreviewed)
            self.assertNotEqual(run(root, unreviewed, head).returncode, 0, 'a commit outside Coordination main is refused')
            git(source, 'checkout', '-q', reviewed)
            (caller / '.github/workflows/stray.yml').write_text('on: workflow_dispatch\n'); git(caller, 'add', '-A'); git(caller, 'commit', '-qm', 'stray')
            stray = git(caller, 'rev-parse', 'HEAD')
            self.assertNotEqual(run(root, reviewed, stray).returncode, 0, 'an extra tracked file in the caller is refused')
            git(caller, 'rm', '-q', '.github/workflows/stray.yml'); git(caller, 'commit', '-qm', 'remove stray')
            self.assertEqual(run(root, reviewed, git(caller, 'rev-parse', 'HEAD')).returncode, 0)
            for target in copies:
                original = (caller / target).read_bytes()
                (caller / target).write_bytes(original + b'# drift\n')
                git(caller, 'commit', '-qam', 'drift'); drifted = git(caller, 'rev-parse', 'HEAD')
                with self.subTest(changed=target):
                    self.assertNotEqual(run(root, reviewed, drifted).returncode, 0)
                (caller / target).write_bytes(original); git(caller, 'commit', '-qam', 'restore')

    def test_template_is_a_dispatch_only_private_caller_that_public_coordination_cannot_queue(self):
        text = WORKFLOW.read_text()
        self.assertEqual(re.findall(r'^on:\n((?:  .*\n|\n)+)', text, re.MULTILINE)[0].split(':', 1)[0].strip(), 'workflow_dispatch')
        for trigger in ('pull_request', 'pull_request_target', 'push:', 'schedule', 'workflow_run', 'workflow_call', 'issue_comment'):
            self.assertNotIn('\n  ' + trigger, text, trigger)
        self.assertIn('runs-on: [self-hosted, linux, x64, fsgg-native-linux-x64-v1]', text)
        self.assertIn("github.ref == 'refs/heads/main' && github.run_attempt == 1", text)
        self.assertIn('permissions:\n  contents: read\n  actions: read\n', text)
        self.assertNotIn('secrets.', text)
        self.assertNotIn('fsgg-native-qualification', text)
        self.assertIn("job.get('runner_group_name')=='Default' and job['runner_name'].startswith('fsgg-q-')", text)
        self.assertIn("FS-GG/FS.GG.Native.Qualification/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main", text)
        self.assertEqual(WRAPPER.WORKFLOW_REF, 'FS-GG/FS.GG.Native.Qualification/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main')
        self.assertIn('repository: FS-GG/FS.GG.Coordination\n          ref: ${{ inputs.coordination_sha }}', text)
        self.assertIn('coordination_sha:', text)
        # The first step stays first and carries no dependency on either checkout.
        self.assertLess(text.index('Retain the original outer job clock'), text.index('Check out the exact public Coordination source'))
        self.assertLess(text.index('Check out this exact private caller'), text.index('Refuse an unreviewed Coordination commit'))
        guard = literal_block(text.split('- name: Refuse an unreviewed Coordination commit or a changed caller', 1)[1], 'run')
        for line in ('merge-base --is-ancestor "$FSGG_SOURCE_REVISION" refs/remotes/origin/main',
                     'caller/.github/workflows/bar-hosted-image-qualification.yml',
                     'caller/README.md', 'caller/.github/actionlint.yaml'):
            self.assertIn(line, guard)

    def test_fixed_workflow_preserves_domain_profile_and_complete_generation_guards(self):
        text = WORKFLOW.read_text()
        self.assertEqual(text.count('canonicalize-checkouts --operation-id'), 1)
        self.assertLess(text.index('canonicalize-checkouts --operation-id'), text.index('Build the pinned daemonless Arch userland'))
        self.assertEqual(text.count('fetch-depth: 0'), 2)
        self.assertNotIn('pull_request:', text)
        self.assertIn("inputs.purpose == 'network-prerequisite' && !inputs.full_image", text)
        self.assertIn("inputs.purpose == 'full-image' && inputs.full_image", text)
        self.assertIn('timeout-minutes: 90', text)
        self.assertIn('chmod 0700 "$VOLUME/allocation/context"', text)
        self.assertIn('test "$GITHUB_REF" = refs/heads/main', text)
        self.assertIn(WRAPPER.PRODUCT_REVISION, text)
        self.assertIn(WRAPPER.PRODUCT_TREE, text)
        self.assertIn('--mount --cgroup --pid --fork', text)
        self.assertIn('RuntimeMaxSec="$runtime"', text)
        self.assertIn('KillMode=control-group', text)
        self.assertIn("steps.qualify.outcome == 'success' && steps.receipt.outcome == 'success'", text)
        self.assertIn("steps.receipt.outputs.candidate-permitted == 'true'", text)
        self.assertIn("format('/fsgg-bar/transport-metadata/{0}'", text)
        self.assertIn('path: /fsgg-bar/transport-candidate/', text)
        self.assertNotIn('path: /fsgg-bar/allocation/state/', text)
        self.assertNotIn('continue-on-error', text)
        self.assertIn('pacman -Qkk passt', literal_block(text, 'PACMAN_SCRIPT'))
        rows = text.splitlines(keepends=True)
        scripts = [literal_block(text, key) for key in ('PACMAN_SCRIPT','ENTER_SCRIPT','UNIT_ENTRY_SCRIPT','PREP_CONTROLS_SCRIPT','PREP_USERLAND_SCRIPT','PREP_ADMIT_SCRIPT')]
        scripts.extend(literal_block(''.join(rows[index:]), 'run') for index, row in enumerate(rows) if row.strip() == 'run: |')
        for script in scripts:
            result = subprocess.run(['/bin/bash', '-n'], input=script, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_real_root_transition_reaches_only_fixed_qualifier_and_failures_block_it(self):
        class Entry(ENTRY.UserlandEntry):
            @staticmethod
            def script():
                return literal_block(WORKFLOW.read_text(), 'ENTER_SCRIPT')
        env = {'SOURCE': '/fixture-source', 'EVIDENCE': '/fixture-evidence', 'HELPER_TREE': 'a' * 40,
               'HOST_JOB_STARTED': str(int(time.time())), 'HOST_JOB_MONOTONIC': str(time.monotonic()),
               'OPERATION_ID': OPERATION, 'SOURCE_REVISION': 'b' * 40, 'QUALIFICATION_MODE': 'qualify'}
        with patch.dict(os.environ, env):
            runner = Entry()
            result, trace = runner.run_entry()
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('mount <-o> <remount,bind,ro>', trace)
            self.assertIn('/eng/bar_hosted_operation.py "$QUALIFICATION_MODE" --operation-id', trace)
            self.assertNotIn('userland-check', trace)
            for failure in ('pivot_root', 'umount', 'rmdir'):
                result, trace = runner.run_entry(failure)
                self.assertEqual(result.returncode, 41)
                self.assertFalse(any(row.startswith('setpriv ') for row in trace.splitlines()))


class ClockBudget(unittest.TestCase):
    """Actual producer/helper with inert HTTP/systemd ports; no native operations."""
    def setUp(self):
        import datetime
        self.temp=tempfile.TemporaryDirectory(prefix='bar-clock-pure-');self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.evidence=self.root/'evidence';self.evidence.mkdir(mode=0o700)
        self.clock=install_clock(self.evidence)
        change=patch.dict(os.environ,{'FSGG_SOURCE_REVISION':'b'*40,'GITHUB_SHA':'c'*40,'SOURCE_REVISION':'b'*40})
        change.start();self.addCleanup(change.stop)
        workflow=WORKFLOW.read_text();script=literal_block(workflow.split('- name: Retain the original outer job clock',1)[1],'run')
        self.producer=textwrap.dedent(script.split("<<'PY_CLOCK'\n",1)[1].split('\nPY_CLOCK',1)[0])
        self.utc=datetime.datetime(2026,10,24,2,0,0,tzinfo=datetime.timezone.utc)
        self.env={'STARTUP_EVIDENCE':str(self.root/'startup'),'OPERATION_ID':OPERATION,'QUALIFICATION_MODE':'network-prerequisite',
          'GITHUB_SHA':CALLER,'FSGG_SOURCE_REVISION':'b'*40,'GITHUB_WORKFLOW_REF':WRAPPER.WORKFLOW_REF,'GITHUB_EVENT_NAME':'workflow_dispatch','GITHUB_REF':'refs/heads/main',
          'GITHUB_RUN_ATTEMPT':'1','GITHUB_REPOSITORY':'FS-GG/FS.GG.Native.Qualification','GITHUB_RUN_ID':'123','RUNNER_NAME':RUNNER,
          'GH_TOKEN':'inert fixture not a credential','GITHUB_ENV':str(self.root/'github-env'),'GITHUB_OUTPUT':str(self.root/'github-output'),
          'SELECTED_UTC':'2026-10-24T02:00:00Z','LATEST_START_UTC':'2026-10-24T02:05:00Z','OBSERVER_DEADLINE_UTC':'2026-10-24T03:35:00Z'}
        self.job={'id':7,'run_id':123,'head_sha':CALLER,'name':'full-image','runner_name':RUNNER,'runner_group_name':'Default','status':'in_progress','started_at':'2026-10-24T01:59:59.500000Z'}

    def produce(self,updates=None,job=None,error=None,elapsed=0):
        import datetime,urllib.request,io
        actual=self.utc
        class NativeUTC(datetime.datetime):
            @classmethod
            def now(cls,tz=None):return actual
        data=json.dumps({'jobs':[self.job if job is None else job]}).encode()
        port=patch.object(urllib.request,'urlopen',side_effect=error) if error else patch.object(urllib.request,'urlopen',return_value=io.BytesIO(data))
        with patch.dict(os.environ,{**self.env,**(updates or {})},clear=True),patch.object(datetime,'datetime',NativeUTC),patch.object(time,'monotonic',side_effect=[100,100+elapsed]),port:
            exec(compile(self.producer,'actual-first-clock','exec'),{})
        return json.loads((Path(self.env['STARTUP_EVIDENCE'])/'clock.json').read_text())

    def test_actual_first_producer_minimum_provider_root_deadline_and_both_purposes(self):
        value=self.produce();WRAPPER.validate_clock(value,OPERATION,'b'*40)
        self.assertEqual(value['overallDeadlineMonotonic'],5499.5)
        self.assertEqual(value['purpose'],'network-prerequisite')
        (Path(self.env['STARTUP_EVIDENCE'])/'clock.json').unlink();Path(self.env['STARTUP_EVIDENCE']).rmdir()
        value=self.produce({'QUALIFICATION_MODE':'qualify'},job={**self.job,'started_at':'2026-10-24T02:00:00Z'})
        WRAPPER.validate_clock(value,OPERATION,'b'*40);self.assertEqual(value['overallDeadlineMonotonic'],5500)
        bad={**value,'overallDeadlineMonotonic':5501}
        with self.assertRaisesRegex(RuntimeError,'deadline differs'):WRAPPER.validate_clock(bad,OPERATION)

    def test_actual_first_producer_refusal_retained_before_checkout_no_retry(self):
        cases=[({'SELECTED_UTC':'1e999'},None,None),({'LATEST_START_UTC':'2026-10-24T01:59:59Z'},None,None),
          ({'OBSERVER_DEADLINE_UTC':'2026-10-24T03:35:01Z'},None,None),({}, {**self.job,'head_sha':'d'*40},None),
          ({}, {**self.job,'runner_group_name':'other-group'},None),({'GITHUB_RUN_ATTEMPT':'2'},None,None),({},None,OSError('native API Unknown')),({}, {**self.job,'started_at':'2026-10-24T01:59:58Z'},None)]
        for n,(updates,job,error) in enumerate(cases):
            self.env['STARTUP_EVIDENCE']=str(self.root/('startup-'+str(n)))
            with self.subTest(case=n),self.assertRaises(Exception):self.produce(updates,job,error)
            value=json.loads((Path(self.env['STARTUP_EVIDENCE'])/'clock.json').read_text())
            self.assertEqual(value['verdict'],'refused');self.assertTrue(value['firstRefusal'])
        text=WORKFLOW.read_text();self.assertLess(text.index('Retain the original outer job clock'),text.index('Check out the exact public Coordination source'))
        self.assertNotIn('retry',self.producer)

    def test_ephemeral_runner_name_group_and_main_attempt_are_refused_in_the_first_step(self):
        cases = (({'RUNNER_NAME': 'fsgg-qualification-x'}, {**self.job, 'runner_name': 'fsgg-qualification-x'}),
                              ({'RUNNER_NAME': 'ubuntu-latest'}, {**self.job, 'runner_name': 'ubuntu-latest'}),
                              ({'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Coordination'}, None),
                              ({'FSGG_SOURCE_REVISION': 'main'}, None),
                              ({'GITHUB_REF': 'refs/heads/other'}, None),
                              ({'GITHUB_RUN_ATTEMPT': '2'}, None),
                              ({'GITHUB_WORKFLOW_REF': 'FS-GG/FS.GG.Native.Qualification/.github/workflows/other.yml@refs/heads/main'}, None),
                              ({}, {**self.job, 'runner_group_name': 'fsgg-native-qualification'}))
        for number, (updates, job) in enumerate(cases):
            self.env['STARTUP_EVIDENCE'] = str(self.root / ('refusal-' + str(number)))
            with self.subTest(updates=updates, group=(job or {}).get('runner_group_name')), self.assertRaises(Exception):
                self.produce(updates, job)
        self.env['STARTUP_EVIDENCE'] = str(self.root / 'accepted')
        accepted = self.produce({'FSGG_SOURCE_REVISION': 'a' * 40})
        self.assertEqual((accepted['sourceRevision'], accepted['callerRevision']), ('a' * 40, CALLER))
        self.assertEqual(accepted['providerJob']['runner_group_name'], 'Default')


    def test_consumer_side_clock_validation_refuses_caller_identity_group_and_name_drift(self):
        good = json.loads(json.dumps(install_clock(self.evidence)))
        WRAPPER.validate_clock(good, OPERATION, 'b' * 40)
        mutations = {
            'callerRepository': lambda v: v.update(callerRepository='FS-GG/FS.GG.Coordination'),
            'callerRevision-not-hex': lambda v: v.update(callerRevision='main'),
            'callerRevision-missing': lambda v: v.pop('callerRevision'),
            'head-differs-from-caller': lambda v: v['providerJob'].update(head_sha='b' * 40),
            'workflowRef': lambda v: v.update(workflowRef='FS-GG/FS.GG.Coordination/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main'),
            'runner-prefix': lambda v: v['providerJob'].update(runner_name='fsgg-qualification-x'),
            'runner-group': lambda v: v['providerJob'].update(runner_group_name='fsgg-native-qualification'),
            'runner-group-missing': lambda v: v['providerJob'].pop('runner_group_name'),
        }
        for name, mutate in mutations.items():
            value = json.loads(json.dumps(good)); mutate(value)
            with self.subTest(name), self.assertRaisesRegex(RuntimeError, 'original clock source differs|native job/source join differs'):
                WRAPPER.validate_clock(value, OPERATION, 'b' * 40)
        with self.assertRaisesRegex(RuntimeError, 'original clock source differs'):
            WRAPPER.validate_clock(good, OPERATION, 'a' * 40)

    def test_fixed_preparation_is_pinned_to_the_manifest_of_the_template(self):
        template = ROOT / 'deployment/qualification-runner/native-qualification'
        body, env = WRAPPER.fixed_preparation('profile')
        self.assertIn('dedicated-profile', body)
        with tempfile.TemporaryDirectory() as directory:
            copy = Path(directory) / 'tree'
            shutil.copytree(ROOT / 'eng', copy / 'eng', ignore=shutil.ignore_patterns('__pycache__'))
            shutil.copytree(template, copy / 'deployment/qualification-runner/native-qualification')
            module = load('bar_hosted_operation_copy', copy / 'eng/bar_hosted_operation.py')
            self.assertEqual(module.fixed_preparation('controls')[0], WRAPPER.fixed_preparation('controls')[0])
            workflow = copy / 'deployment/qualification-runner/native-qualification/bar-hosted-image-qualification.yml'
            manifest = copy / 'deployment/qualification-runner/native-qualification/MANIFEST.sha256'
            original = manifest.read_text()
            workflow.write_text(workflow.read_text() + '# drift\n')
            with self.assertRaisesRegex(RuntimeError, 'workflow pin differs'): module.fixed_preparation('profile')
            workflow.write_bytes((template / 'bar-hosted-image-qualification.yml').read_bytes())
            for damaged in ('', original + original.splitlines()[0] + '\n', original.replace('bar-hosted-image-qualification.yml', 'other.yml')):
                manifest.write_text(damaged)
                with self.subTest(damaged=damaged[:20]), self.assertRaisesRegex(RuntimeError, 'workflow pin differs'):
                    module.fixed_preparation('profile')

    def test_checkout_bootstrap_refuses_the_public_coordination_repository(self):
        env = {'GITHUB_WORKSPACE': str(self.root), 'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Coordination', 'GITHUB_EVENT_NAME': 'workflow_dispatch',
               'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1'}
        with patch.dict(os.environ, env), self.assertRaisesRegex(RuntimeError, 'private-caller checkout bootstrap'):
            WRAPPER.canonicalize_checkouts('b' * 40)

    def test_runner_temp_context_is_step_scoped_and_propagates_only_accepted_path(self):
        text=WORKFLOW.read_text();job_header=text.split('    steps:',1)[0]
        self.assertNotIn('${{ runner.temp }}',job_header)
        clock_step=text.split('      - name: Retain the original outer job clock',1)[1].split('      - name: Check out the exact candidate',1)[0]
        self.assertIn('          STARTUP_EVIDENCE: ${{ runner.temp }}',clock_step)
        value=self.produce()
        self.assertEqual(value['verdict'],'accepted')
        self.assertIn('STARTUP_EVIDENCE='+self.env['STARTUP_EVIDENCE']+'\n',Path(self.env['GITHUB_ENV']).read_text())
        self.assertIn('startup-path='+self.env['STARTUP_EVIDENCE'],Path(self.env['GITHUB_OUTPUT']).read_text())

    def test_refused_clock_retains_safe_output_without_success_environment_export(self):
        with self.assertRaises(OSError):self.produce(error=OSError('original API refusal'))
        self.assertFalse(Path(self.env['GITHUB_ENV']).exists())
        self.assertIn('startup-path='+self.env['STARTUP_EVIDENCE'],Path(self.env['GITHUB_OUTPUT']).read_text())
        self.assertIn('original API refusal',(Path(self.env['STARTUP_EVIDENCE'])/'clock.json').read_text())

    def test_whole_api_timeout_and_remaining_checkout_budget_refuse_before_effects(self):
        import signal
        def timer_port(*args,**kwargs):signal.getsignal(signal.SIGALRM)(signal.SIGALRM,None)
        with self.assertRaisesRegex(TimeoutError,'whole15s'):self.produce(error=timer_port)
        value=json.loads((Path(self.env['STARTUP_EVIDENCE'])/'clock.json').read_text())
        self.assertEqual(value['verdict'],'refused');self.assertIn('whole15s',value['firstRefusal'])
        self.env['STARTUP_EVIDENCE']=str(self.root/'late-api')
        with self.assertRaisesRegex(ValueError,'checkout/transport aperture'):self.produce(elapsed=3700)
        self.assertEqual(json.loads((Path(self.env['STARTUP_EVIDENCE'])/'clock.json').read_text())['verdict'],'refused')

    def reset(self,purpose='full-image'):
        self.clock=install_clock(self.evidence,purpose)
        for name in ('unit-request.json','unit-admission.json'):(self.evidence/name).unlink()

    def test_dynamic_creation_budget_old_fallback_red_and_prerequisite_late_preparation_green(self):
        self.assertEqual(480+3900+120+1200,5700)
        self.reset('full-image')
        with patch.object(time,'monotonic',return_value=self.clock['firstMonotonic']+313),self.assertRaisesRegex(RuntimeError,'remaining original budget'):
            WRAPPER.unit_budget(OPERATION,evidence=self.evidence)
        self.reset('network-prerequisite');end=self.clock['overallDeadlineMonotonic']
        with patch.object(time,'monotonic',return_value=end-1000),self.assertRaisesRegex(RuntimeError,'remaining original budget'):
            WRAPPER.unit_budget(OPERATION,evidence=self.evidence)
        with patch.object(time,'monotonic',return_value=self.clock['firstMonotonic']+313):
            request=WRAPPER.unit_budget(OPERATION,evidence=self.evidence)
        self.assertGreaterEqual(request['runtimeMaxSeconds'],450)
        self.assertEqual(request['purpose'],'network-prerequisite')

    def test_purpose_work_both_clocks_and_stale_final_gate(self):
        for purpose,work in [('full-image',3600),('network-prerequisite',300)]:
            self.clock=install_clock(self.evidence,purpose)
            self.assertEqual(WRAPPER.work_seconds(purpose),work);self.assertEqual(BAR.work_seconds(purpose),work)
            with patch.object(time,'monotonic',return_value=self.clock['firstMonotonic']+313):
                if purpose=='full-image':
                    with self.assertRaisesRegex(RuntimeError,'remaining original unit budget'):WRAPPER.controller_start(OPERATION,purpose,evidence=self.evidence,revision='b'*40)
                else:WRAPPER.controller_start(OPERATION,purpose,evidence=self.evidence,revision='b'*40)
        with self.assertRaises(RuntimeError):WRAPPER.work_seconds('custom-duration')

    def test_systemctl_duration_protocol_is_not_raw_microsecond_assumption(self):
        self.assertEqual(WRAPPER.unit_microseconds('1h 5min'),3900000000)
        self.assertEqual(WRAPPER.unit_microseconds('2min'),120000000)
        self.assertEqual(WRAPPER.unit_microseconds('0'),0)
        for value in ('infinity','NaN','1e999s','2min garbage'):
            with self.assertRaises(RuntimeError):WRAPPER.unit_microseconds(value)

    def test_actual_unit_entry_birth_properties_and_cgroup_guard_before_namespace(self):
        import types
        self.reset('network-prerequisite')
        request=WRAPPER.unit_budget(OPERATION,evidence=self.evidence)
        active=request['requestedMonotonic']+0.1
        props={'ActiveEnterTimestampMonotonic':str(round(active*1000000)),'RuntimeMaxUSec':str(request['runtimeMaxSeconds']*1000000),
            'TimeoutStopUSec':'2min','InvocationID':'a'*32,'MainPID':str(os.getppid()),'ControlGroup':'/fixture.service',
            'Type':'exec','ActiveState':'active','RuntimeRandomizedExtraUSec':'0'}
        actual_read=Path.read_text
        def read(path,*args,**kwargs):
            return '0::/fixture.service' if str(path)=='/proc/self/cgroup' else actual_read(path,*args,**kwargs)
        for case in ('wrong-runtime','late-activation','wrong-main','wrong-cgroup','duplicate','normal'):
            changed=dict(props)
            if case=='wrong-runtime':changed['RuntimeMaxUSec']='1'
            if case=='late-activation':changed['ActiveEnterTimestampMonotonic']=str(round((active+31)*1000000))
            if case=='wrong-main':changed['MainPID']='1'
            if case=='wrong-cgroup':changed['ControlGroup']='/foreign.service'
            output='\n'.join(k+'='+v for k,v in changed.items()).encode()
            if case=='duplicate':output+=b'\nMainPID=1'
            with patch.object(WRAPPER.subprocess,'run',return_value=types.SimpleNamespace(stdout=output)) as port,patch.object(Path,'read_text',read),patch.object(WRAPPER.os,'chown') as chown:
                if case=='normal':
                    value=WRAPPER.unit_enter(OPERATION,evidence=self.evidence)
                    self.assertLessEqual(value['runtimeDeadlineMonotonic'],request['runtimeLimitMonotonic']);chown.assert_called_once()
                else:
                    with self.assertRaises(RuntimeError):WRAPPER.unit_enter(OPERATION,evidence=self.evidence)
                    self.assertFalse((self.evidence/'unit-admission.json').exists())
            self.assertEqual(port.call_args.args[0][:3],['/usr/bin/systemctl','show','fsgg-'+OPERATION+'.service'])
        script=literal_block(WORKFLOW.read_text(),'UNIT_ENTRY_SCRIPT')
        self.assertLess(script.index('unit-enter'),script.index('exec /usr/bin/unshare'))
        self.assertNotIn('set-property',WORKFLOW.read_text())

    def test_receipt_never_runs_pinned_source_unless_the_guard_passed(self):
        script=literal_block(WORKFLOW.read_text().split('- name: Retain the bounded generation receipt',1)[1],'run')
        tools=self.root/'guard-tools';tools.mkdir()
        trace=self.root/'guard-trace'
        # Pinned-source invocations are recorded and inert; the source-independent outcome capture is the real python.
        (tools/'python3').write_text('#!/bin/sh\ncase "$*" in *bar_hosted*|*source/eng*) printf "%s %s\\n" "$0" "$*" >> '+shlex.quote(str(trace))+'; exit 0;; esac\nexec /usr/bin/python3 "$@"\n')
        (tools/'sudo').write_text('#!/bin/sh\nprintf "%s %s\\n" "$0" "$*" >> '+shlex.quote(str(trace))+'\nexit 0\n')
        for name in ('python3','sudo'):(tools/name).chmod(0o700)
        for outcome in ('failure','skipped','cancelled',''):
            startup=self.root/('guard-startup-'+(outcome or 'unset'));startup.mkdir(mode=0o700)
            env={'PATH':str(tools)+':/usr/bin:/bin','STARTUP_CAPTURE_PATH':str(startup),'EVIDENCE':str(self.evidence),'RUNNER_TEMP':str(self.root),
                 'OPERATION_ID':OPERATION,'CLOCK_OUTCOME':'success','ADMIT_OUTCOME':'skipped','QUALIFY_OUTCOME':'skipped','HOST_JOB_STARTED':'1'}
            if outcome:env['GUARD_OUTCOME']=outcome
            with self.subTest(outcome=outcome):
                done=subprocess.run(['/bin/bash','-c',script],cwd=self.root,env=env,capture_output=True,text=True,timeout=10)
                self.assertNotEqual(done.returncode,0);self.assertIn('guard did not pass',done.stderr)
                self.assertIn('outcomes.json',[p.name for p in startup.iterdir()],'source-independent outcome is still retained')
                recorded=trace.read_text() if trace.exists() else ''
                self.assertNotIn('bar_hosted',recorded);self.assertNotIn('source/eng',recorded)

    def test_early_refusal_and_absent_helpers_retain_startup_in_existing_single_upload(self):
        startup=self.root/'no-helper-startup';startup.mkdir(mode=0o700)
        (startup/'clock.json').write_text('{"verdict":"refused","firstRefusal":"original Root queue refusal"}')
        script=literal_block(WORKFLOW.read_text().split('- name: Retain the bounded generation receipt',1)[1],'run')
        env={**os.environ,'STARTUP_EVIDENCE':str(startup),'STARTUP_CAPTURE_PATH':str(startup),'EVIDENCE':str(self.evidence),'RUNNER_TEMP':str(self.root),
             'OPERATION_ID':OPERATION,'CLOCK_OUTCOME':'failure','GUARD_OUTCOME':'success','ADMIT_OUTCOME':'skipped','QUALIFY_OUTCOME':'skipped'}
        done=subprocess.run(['/bin/bash','-c',script],cwd=self.root,env=env,capture_output=True,text=True,timeout=10)
        self.assertNotEqual(done.returncode,0)
        self.assertIn('original Root queue refusal',(startup/'clock.json').read_text())
        self.assertEqual(json.loads((startup/'outcomes.json').read_text())['CLOCK_OUTCOME'],'failure')
        text=WORKFLOW.read_text();metadata=text.split('- name: Retain metadata and original command streams',1)[1].split('- name: Retain only the encrypted',1)[0]
        self.assertIn('if: always()',metadata);self.assertIn('|| steps.clock.outputs.startup-path',metadata)
        self.assertEqual(metadata.count('uses:'),1);self.assertIn('timeout-minutes: 1',metadata)


class PreparationDeadline(ClockBudget):
    def test_fixed_phase_closure_and_no_command_or_duration_port(self):
        for phase in ('profile','controls','userland','admit'):
            body,env=WRAPPER.fixed_preparation(phase)
            self.assertIn('set -euo pipefail',body);self.assertIn('ARCH_IMAGE',env);self.assertIn('BUILDAH_SHA256',env)
        with self.assertRaises(RuntimeError):WRAPPER.fixed_preparation('arbitrary')
        text=WORKFLOW.read_text()
        for phase in ('profile','controls','userland','admit'):
            self.assertIn('--phase '+phase+' --operation-id',text)
        self.assertNotIn('--command',text)

    def test_all_preunit_run_blocks_are_fixed_owned_phases(self):
        text=WORKFLOW.read_text();prefix=text.split('      - name: Qualify one complete BAR image',1)[0]
        chunks=prefix.split('      - name:')[1:]
        for chunk in chunks:
            if 'run: |' not in chunk:continue
            body=literal_block(chunk,'run')
            if 'Retain the original outer job clock' in chunk:continue
            if 'Refuse an unreviewed Coordination commit or a changed caller' in chunk:
                # The only plain step: it must precede every sudo/pinned-source execution.
                self.assertNotIn('sudo',body);self.assertNotIn('python',body);self.assertNotIn('source/eng',body)
                self.assertLess(text.index('Refuse an unreviewed Coordination commit'),text.index('Refuse unselected dedicated image'))
                continue
            self.assertIn('prep-run',body)
        body,_=WRAPPER.fixed_preparation('userland')
        self.assertIn('sudo swapoff -a',body);self.assertIn('sudo fallocate',body);self.assertIn('sudo systemctl stop',body)
        self.assertLess(body.index('sudo docker'),body.index('sudo swapoff'));self.assertLess(body.index('sudo swapoff'),body.index('sudo fallocate'))

    def test_actual_late_phase_cannot_start480s_past_original_transport_reserve(self):
        end=self.clock['overallDeadlineMonotonic']
        with patch.object(time,'monotonic',return_value=end-20),self.assertRaisesRegex(RuntimeError,'aperture exhausted'):
            WRAPPER.preparation_intent(OPERATION,'userland',evidence=self.evidence)
        with patch.object(time,'monotonic',return_value=end-1230-20):
            intent=WRAPPER.preparation_intent(OPERATION,'userland',evidence=self.evidence)
        self.assertEqual(intent['hardEnd'],end-1230);self.assertEqual(intent['termAt']-(end-1230-20),19)
        self.assertEqual(end-intent['hardEnd'],1200+30)

    def test_actual_child_rechecks_before_fixed_body_and_gnu_timeout(self):
        import sys
        intent=WRAPPER.preparation_intent(OPERATION,'controls',evidence=self.evidence)
        slot=self.evidence/'prep-controls';slot.mkdir();WRAPPER.write_new(slot/'intent.json',intent)
        with patch.object(WRAPPER.os,'getppid',return_value=os.getpid()),patch.object(WRAPPER.os,'getpgrp',return_value=os.getpid()),patch.object(WRAPPER.os,'execve') as execute:
            with patch.object(time,'monotonic',return_value=intent['termAt']+.1),self.assertRaisesRegex(RuntimeError,'actual preparation start expired'):
                WRAPPER.preparation_child(OPERATION,'controls',evidence=self.evidence)
            execute.assert_not_called()
            with patch.object(time,'monotonic',return_value=intent['startedMonotonic']+.1):WRAPPER.preparation_child(OPERATION,'controls',evidence=self.evidence)
            argv=execute.call_args.args[1];self.assertEqual(argv[:4],['/usr/bin/timeout','--foreground','--signal=TERM','--kill-after=1'])
            self.assertLessEqual(float(argv[4][:-1]),intent['termAt']-intent['startedMonotonic'])
            self.assertIn('canonicalize-checkouts',argv[-1])

    def test_group_birth_liveness_distinguishes_zombies_reuse_and_escape(self):
        for state,start,pgrp,expected in [('S',44,4321,[123]),('Z',44,4321,[]),('S',45,4321,[]),('S',44,999,[123])]:
            fields=['0']*20;fields[0]=state;fields[2]=str(pgrp);fields[3]='4321';fields[19]=str(start)
            with patch.object(Path,'read_text',return_value='123 (fixture) '+' '.join(fields)):
                self.assertEqual(WRAPPER.live_group_births(4321,{123:44}),expected)

    def test_unknown_and_moved_known_birth_never_count_as_retired(self):
        for error in (PermissionError('fixture proc denied'),ValueError('fixture malformed proc')):
            with patch.object(Path,'read_text',side_effect=error):
                self.assertEqual(WRAPPER.live_group_births(4321,{123:44}),[123])
                self.assertEqual(WRAPPER.group_birth_observations(4321,{123:44})[0]['status'],'Unknown')
        with patch.object(Path,'read_text',side_effect=FileNotFoundError('confirmed absent')):
            self.assertEqual(WRAPPER.live_group_births(4321,{123:44}),[])
            self.assertEqual(WRAPPER.group_birth_observations(4321,{123:44})[0]['status'],'gone')
        with patch.object(Path,'read_text',return_value='malformed proc row'):
            self.assertEqual(WRAPPER.live_group_births(4321,{123:44}),[123])

    def test_final_unknown_observation_cannot_diverge_from_parent_acceptance(self):
        real_popen=subprocess.Popen
        def launch(argv,**kwargs):return real_popen(['/bin/bash','-c','printf original; exit 0'],**kwargs)
        with patch.object(WRAPPER.os,'geteuid',return_value=0),patch.object(WRAPPER.os,'chown'),patch.dict(os.environ,{'GITHUB_WORKSPACE':str(self.root)}),patch.object(WRAPPER,'live_group_births',return_value=[]),patch.object(WRAPPER,'group_birth_observations',return_value=[{'pid':123,'expectedStartTicks':44,'status':'Unknown','reason':'fixture denied final readback'}]):
            with self.assertRaisesRegex(RuntimeError,'custody Unknown'):WRAPPER.run_preparation(OPERATION,'controls',evidence=self.evidence,launch=launch)
        value=json.loads((self.evidence/'prep-controls/receipt.json').read_text())
        self.assertTrue(value['leaderReaped']);self.assertTrue(value['bothStreamsEof']);self.assertEqual(value['finalGroupObservations'][0]['status'],'Unknown')

    def test_known_live_group_birth_blocks_success_after_leader_and_eof(self):
        real_popen=subprocess.Popen;state={'calls':0}
        def launch(argv,**kwargs):return real_popen(['/bin/bash','-c','printf original; exit 0'],**kwargs)
        def live(group,members):
            state['calls']+=1
            return [group] if state['calls']<4 else []
        with patch.object(WRAPPER.os,'geteuid',return_value=0),patch.object(WRAPPER.os,'chown'),patch.dict(os.environ,{'GITHUB_WORKSPACE':str(self.root)}),patch.object(WRAPPER,'live_group_births',side_effect=live),patch.object(WRAPPER.os,'killpg'):
            with self.assertRaisesRegex(RuntimeError,'known original'):WRAPPER.run_preparation(OPERATION,'controls',evidence=self.evidence,launch=launch)
        value=json.loads((self.evidence/'prep-controls/receipt.json').read_text())
        self.assertTrue(value['leaderReaped']);self.assertTrue(value['bothStreamsEof']);self.assertIn('known original',value['firstCause'])

    def test_actual_owned_parent_timeout_wait_eof_and_failure_streams(self):
        import sys
        real_popen=subprocess.Popen
        for name,command in [('normal',['/bin/bash','-c','printf owned-stdout; printf original-error >&2; exit 0']),
              ('gnu-timeout',['/usr/bin/timeout','--foreground','--kill-after=1','.1s',sys.executable,'-I','-B','-c','import time;time.sleep(5)']),
              ('parent-timeout',[sys.executable,'-I','-B','-c','import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);print("owned-before-timeout",flush=True);time.sleep(5)'])]:
            with self.subTest(case=name):
                evidence=self.root/name;evidence.mkdir();install_clock(evidence)
                def launch(argv,**kwargs):
                    self.assertIn('prep-child',argv);self.assertTrue(kwargs['start_new_session'])
                    return real_popen(command,**kwargs)
                with patch.object(WRAPPER.os,'geteuid',return_value=0),patch.object(WRAPPER.os,'chown'),patch.dict(os.environ,{'GITHUB_WORKSPACE':str(self.root)}),patch.dict(WRAPPER.PREPARATION_PHASES,{'controls':2}):
                    if name=='normal':value=WRAPPER.run_preparation(OPERATION,'controls',evidence=evidence,launch=launch)
                    else:
                        with self.assertRaises(RuntimeError):WRAPPER.run_preparation(OPERATION,'controls',evidence=evidence,launch=launch)
                        value=json.loads((evidence/'prep-controls/receipt.json').read_text())
                self.assertTrue(value['leaderReaped']);self.assertTrue(value['bothStreamsEof']);self.assertEqual(value['descendantSettlement'],'unproved')
                self.assertTrue((evidence/'prep-controls/stdout.log').exists())
                if name=='parent-timeout':self.assertEqual([v['signal'] for v in value['signals']][:2],[15,9])
                if name=='normal':self.assertEqual((evidence/'prep-controls/stderr.log').read_bytes(),b'original-error')
                with self.assertRaises(RuntimeError),patch.object(WRAPPER.os,'geteuid',return_value=0):WRAPPER.run_preparation(OPERATION,'controls',evidence=evidence,launch=launch)


class SyntheticCrypto(unittest.TestCase):
    """Only tiny authored bytes and temporary fixture keys; never root's key."""
    def setUp(self):
        change = patch.object(WRAPPER, 'CRYPTO_OBSERVATIONS', [])
        change.start(); self.addCleanup(change.stop)
        temporary = tempfile.TemporaryDirectory(prefix='bar-cms-fixture-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name); self.root.chmod(0o700)
        self.evidence = self.root / 'evidence'; self.evidence.mkdir(mode=0o700)
        for name in ('recipient', 'wrong'):
            result = subprocess.run(['/usr/bin/openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes',
                '-keyout', str(self.root / (name + '.key')), '-out', str(self.root / (name + '.pem')),
                '-subj', '/CN=BAR authored synthetic fixture only', '-days', '1'],
                stdin=subprocess.DEVNULL, capture_output=True, timeout=15, env=WRAPPER.CRYPTO_ENV)
            self.assertEqual(result.returncode, 0, result.stderr)
            (self.root / (name + '.key')).chmod(0o600)
        certificate = self.root / 'recipient.pem'
        der = subprocess.check_output(['/usr/bin/openssl', 'x509', '-in', str(certificate), '-outform', 'DER'],
                                      timeout=10, env=WRAPPER.CRYPTO_ENV)
        for name, value in [('RECIPIENT', certificate), ('RECIPIENT_PEM_SHA256', hashlib.sha256(certificate.read_bytes()).hexdigest()),
                            ('RECIPIENT_DER_SHA256', hashlib.sha256(der).hexdigest())]:
            change = patch.object(WRAPPER, name, value); change.start(); self.addCleanup(change.stop)
        change = patch.dict(os.environ, {'HOST_JOB_MONOTONIC': str(time.monotonic()),'FSGG_SOURCE_REVISION':'b'*40})
        change.start(); self.addCleanup(change.stop)
        install_clock(self.evidence)
        self.plain = self.root / 'candidate.oci.tar'
        self.plain.write_bytes(b'authored tiny synthetic transport bytes\x00\xff' * 32)
        self.archive = WRAPPER.digest_file(self.plain, 1024**2)

    def encrypt(self):
        return WRAPPER.encrypt_candidate(OPERATION, self.plain, self.archive, evidence=self.evidence,
                                         transport=self.root / 'ciphertext')

    def test_actual_streaming_aead_roundtrip_wrong_key_and_tamper(self):
        seal = WRAPPER.seal_crypto(OPERATION, evidence=self.evidence)
        encrypted = self.encrypt()
        self.assertFalse(seal['privateKeyOnRunner'])
        self.assertIn('/usr/bin/openssl', seal['identity']['files'])
        self.assertEqual(WRAPPER.digest_file(self.plain, 1024**2), self.archive)
        argv = WRAPPER.encryption_argv(self.plain, Path(encrypted['path']))
        self.assertIn('-stream', argv); self.assertIn('-aes-256-gcm', argv)
        self.assertNotIn('-inkey', argv)
        def decrypt(key, target):
            return subprocess.run(['/usr/bin/openssl', 'cms', '-decrypt', '-binary', '-inform', 'DER',
                '-in', encrypted['path'], '-recip', str(self.root / 'recipient.pem'),
                '-inkey', str(self.root / key), '-out', str(self.root / target)],
                stdin=subprocess.DEVNULL, capture_output=True, timeout=10, env=WRAPPER.CRYPTO_ENV)
        result = decrypt('recipient.key', 'quarantine')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.root / 'quarantine').read_bytes(), self.plain.read_bytes())
        self.assertNotEqual(decrypt('wrong.key', 'wrong-quarantine').returncode, 0)
        path = Path(encrypted['path']); payload = bytearray(path.read_bytes()); payload[-10] ^= 1; path.write_bytes(payload)
        self.assertNotEqual(decrypt('recipient.key', 'tampered-quarantine').returncode, 0)

    def test_changed_identity_or_expired_original_deadline_blocks_encryption(self):
        WRAPPER.seal_crypto(OPERATION, evidence=self.evidence)
        with patch.object(WRAPPER, 'crypto_identity', return_value={'changed': True}), self.assertRaisesRegex(RuntimeError, 'identity changed'):
            self.encrypt()
        self.assertFalse((self.root / 'ciphertext' / OPERATION).exists())
        path = self.evidence / 'transport-seal.json'; value = json.loads(path.read_text())
        value['outerStartMonotonic'] = time.monotonic() - WRAPPER.OUTER_SECONDS - 1
        value['outerDeadlineMonotonic'] = value['outerStartMonotonic'] + WRAPPER.OUTER_SECONDS
        path.write_bytes(WRAPPER.canonical(value))
        with self.assertRaisesRegex(RuntimeError, 'deadline expired'):
            self.encrypt()

    def test_wrong_recipient_and_ciphertext_bound_cannot_authorize_transport(self):
        with patch.object(WRAPPER, 'RECIPIENT_PEM_SHA256', '0' * 64), self.assertRaisesRegex(RuntimeError, 'PEM differs'):
            WRAPPER.seal_crypto(OPERATION, evidence=self.evidence)
        WRAPPER.seal_crypto(OPERATION, evidence=self.evidence)
        with patch.object(WRAPPER, 'CIPHER_BYTES', 1), self.assertRaises(RuntimeError):
            self.encrypt()
        # Failed ciphertext generations cannot be reopened with a fresh clock.
        with self.assertRaisesRegex(RuntimeError, 'already consumed'):
            self.encrypt()


if __name__ == '__main__':
    unittest.main()
