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
WORKFLOW = ROOT / '.github/workflows/bar-hosted-image-qualification.yml'
OPERATION = 'bar-image-123-1'
GIB = 1024**3


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
        env = {'GITHUB_WORKSPACE': str(self.root), 'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Coordination',
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
                                        'HOST_JOB_MONOTONIC': str(time.monotonic())})
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
        self.assertEqual(selection['clock']['workEnd'] - selection['clock']['start'], 3600)
        self.assertEqual(selection['clock']['totalEnd'] - selection['clock']['workEnd'], 120)
        self.assertEqual(selection['product']['revision'], WRAPPER.PRODUCT_REVISION)
        self.assertFalse((self.allocation / 'state' / OPERATION).exists(), 'preparation must not consume controller original')
        with self.assertRaises(FileExistsError):
            self.prepare()

    def test_fixed_prerequisite_purpose_uses_the_same_managed_preflight(self):
        selection, target, digest = WRAPPER.prepare(OPERATION, 'b' * 40, source=ROOT,
            allocation=self.allocation, evidence=self.evidence, home=self.home, bar=BAR,
            purpose='network-prerequisite')
        argv = WRAPPER.controller_argv(selection, target, digest, source=ROOT, allocation=self.allocation)
        self.assertEqual(argv[4], 'preflight')
        self.assertEqual(selection['network']['purpose'], 'network-prerequisite')
        self.assertEqual(selection['clock']['totalEnd'] - selection['clock']['start'], 3720)
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
                    elif case == 'expired-outer': os.environ['HOST_JOB_MONOTONIC'] = str(time.monotonic() - 500)
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
        text = WORKFLOW.read_text()
        receipt_script = literal_block(text.split('      - name: Retain the bounded generation receipt', 1)[1], 'run')
        summary_script = literal_block(text.split('      - name: Summarize the complete generation', 1)[1], 'run')
        for primary in ('failed', 'prerequisite'):
            with self.subTest(primary=primary):
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
                    # The sudo port fails after an unreadable partial file.
                    # Actual canonical receipt runs in a genuine Python child.
                    (tools / 'sudo').write_text("""#!/bin/sh
previous=
for argument in "$@"; do
  if [ "$previous" = --evidence ]; then
    printf '%s' '{partial privileged output' > "$argument"
    chmod 000 "$argument"
  fi
  previous="$argument"
done
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
                           'OPERATION_ID': OPERATION, 'HOST_JOB_STARTED': '1700000000', 'GITHUB_OUTPUT': str(outputs)}
                    done = subprocess.run(['/bin/bash', '-c', receipt_script], env=env,
                                          capture_output=True, text=True, timeout=15)
                    self.assertEqual(done.returncode, 0, done.stderr)
                    value = json.loads((fixture.evidence / 'transport-receipt.json').read_text())
                    self.assertEqual(value['securityReadback']['status'], 'Unknown')
                    self.assertEqual(value['securityReadback']['exit'], 41)
                    self.assertFalse(value['candidateTransportPermitted'])
                    self.assertEqual(value['networkPrerequisitePassed'], primary == 'prerequisite')
                    stage = fixture.transport / OPERATION
                    self.assertEqual((stage / 'evidence/qualification.stderr.log').read_bytes(),
                                     b'original native first-cause125')
                    self.assertTrue((stage / 'state/0001.stdout').exists())
                    self.assertFalse((stage / 'evidence/host-security-readback.json').exists())
                    self.assertIn('candidate-permitted=false', outputs.read_text())
                    # This is exactly the maintained metadata uploader's path.
                    self.assertIn('path: /fsgg-bar/transport-metadata/', text)
                    summary_env = {**env, 'GITHUB_SHA': 'b' * 40, 'GITHUB_STEP_SUMMARY': str(fixture.root / 'summary'),
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
            (tools / 'git').write_text('#!/bin/sh\nprintf "%s\\n" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n')
            (tools / 'sudo').write_text('#!/bin/sh\nprintf \'{"fixture":"result"}\\n\'\nprintf \'fixture diagnostic\\n\' >&2\nexit "$FIXTURE_EXIT"\n')
            for path in tools.iterdir():
                path.chmod(0o700)
            for code in (0, 41):
                evidence = root / str(code); evidence.mkdir(mode=0o700)
                env = {**os.environ, 'PATH': str(tools) + ':/usr/bin:/bin', 'EVIDENCE': str(evidence),
                       'ENTER_SCRIPT': 'authored inert entry fixture', 'OPERATION_ID': OPERATION, 'QUALIFICATION_MODE': 'qualify',
                       'USERLAND': '/fixture-userland', 'VOLUME': '/fixture-volume', 'GITHUB_WORKSPACE': str(root),
                       'GITHUB_SHA': 'b' * 40, 'HOST_JOB_STARTED': str(int(time.time())),
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
        guard = literal_block(WORKFLOW.read_text(), 'run')
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'environment'
            normal = {'GITHUB_ENV': str(output), 'GITHUB_EVENT_NAME': 'workflow_dispatch',
                      'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1', 'QUALIFICATION_MODE': 'qualify'}
            result = subprocess.run(['/bin/bash', '-c', guard], env={**os.environ, **normal}, capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('HOST_JOB_STARTED=', output.read_text())
            output.unlink()
            for key, value in [('GITHUB_EVENT_NAME', 'push'), ('GITHUB_REF', 'refs/heads/other'), ('GITHUB_RUN_ATTEMPT', '2')]:
                result = subprocess.run(['/bin/bash', '-c', guard], env={**os.environ, **normal, key: value}, capture_output=True, timeout=10)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(output.exists())

    def test_source_pins_and_registration_match_actual_workflow(self):
        WRAPPER.load_bar(ROOT)
        self.assertEqual(hashlib.sha256(WRAPPER.RECIPIENT.read_bytes()).hexdigest(), WRAPPER.RECIPIENT_PEM_SHA256)
        self.assertNotIn(b'PRIVATE KEY', WRAPPER.RECIPIENT.read_bytes())
        inventory = json.loads((ROOT / 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json').read_text())
        entries = [entry for entry in inventory['documents'] if entry['path'] == str(WORKFLOW.relative_to(ROOT))]
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]['sha256'], hashlib.sha256(WORKFLOW.read_bytes()).hexdigest())
        uses = set(re.findall(r'^\s*(?:-\s*)?uses:\s*([^\s#]+)', WORKFLOW.read_text(), re.MULTILINE))
        self.assertEqual(set(entries[0]['externalReferences']), uses)
        self.assertEqual(entries[0]['localReferences'], [])

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
        self.assertIn('RuntimeMaxSec=3900', text)
        self.assertIn('KillMode=control-group', text)
        self.assertIn("steps.qualify.outcome == 'success' && steps.receipt.outcome == 'success'", text)
        self.assertIn("steps.receipt.outputs.candidate-permitted == 'true'", text)
        self.assertIn('path: /fsgg-bar/transport-metadata/', text)
        self.assertIn('path: /fsgg-bar/transport-candidate/', text)
        self.assertNotIn('path: /fsgg-bar/allocation/state/', text)
        self.assertNotIn('continue-on-error', text)
        self.assertIn('pacman -Qkk passt', literal_block(text, 'PACMAN_SCRIPT'))
        rows = text.splitlines(keepends=True)
        scripts = [literal_block(text, 'PACMAN_SCRIPT'), literal_block(text, 'ENTER_SCRIPT')]
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
        change = patch.dict(os.environ, {'HOST_JOB_MONOTONIC': str(time.monotonic())})
        change.start(); self.addCleanup(change.stop)
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
