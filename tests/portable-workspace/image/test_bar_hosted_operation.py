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
        self.selection = {'helper': {'revision': 'a' * 40}, 'product': {'revision': WRAPPER.PRODUCT_REVISION}, 'clock': {'start': 1}}
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
                      'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1'}
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
        self.assertEqual(text.count('fetch-depth: 0'), 2)
        self.assertNotIn('pull_request:', text)
        self.assertIn('if: ${{ inputs.full_image }}', text)
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
        self.assertEqual(literal_block(text, 'PACMAN_SCRIPT'), literal_block(
            (ROOT / '.github/workflows/bar-hosted-domain-preflight.yml').read_text(), 'PACMAN_SCRIPT'))
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
               'OPERATION_ID': OPERATION, 'SOURCE_REVISION': 'b' * 40}
        with patch.dict(os.environ, env):
            runner = Entry()
            result, trace = runner.run_entry()
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('mount <-o> <remount,bind,ro>', trace)
            self.assertIn('/eng/bar_hosted_operation.py qualify --operation-id', trace)
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
