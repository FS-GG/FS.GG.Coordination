"""Tiny DATA/refusal controls only; no VM, native SDK/compiler, policy, key or registration operation."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('dedicated_runner_source', ROOT / 'eng/dedicated_qualification_runner.py')
owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)


def target():
    return {'schema': 'fsgg.dedicated-qualification-target/1', 'baselineId': owner.BASELINE_ID,
        'provider': 'authored-test-provider', 'providerInstanceId': 'test-vm-only', 'providerImageId': 'test-image',
        'systemUuid': 'test-uuid', 'operatorAcceptanceSha256': 'a' * 64, 'dedicated': True, 'disposable': True,
        'sharedCodexHost': False, 'architecture': 'x86_64', 'os': {'id': 'ubuntu', 'version': '26.10'},
        'resources': {'vcpu': 4, 'memoryBytes': 32 * 1024**3, 'diskBytes': 256 * 1024**3}}


def lock():
    return {'schema': 'fsgg.dedicated-qualification-acquired-inputs/1', 'baselineId': owner.BASELINE_ID,
        'image': {'url': 'https://cloud-images.ubuntu.com/authored-test.img', 'sha256': 'a' * 64,
            'signedChecksumSha256': 'b' * 64, 'qualifiedAcquisitionReceiptSha256': 'c' * 64, 'release': 'test-only'},
        'sdk': {'version': '10.0.401', 'runtime': '10.0.12', 'archiveSha512': owner.SDK_SHA512},
        'runner': {'version': '1.2.3', 'sha256': 'd' * 64, 'releaseMetadataSha256': 'e' * 64,
            'url': 'https://github.com/actions/runner/releases/download/v1.2.3/actions-runner-linux-x64-1.2.3.tar.gz'},
        'goRace': {'version': '1.27.1', 'archiveSha256': 'f' * 64, 'url': 'https://go.dev/dl/go1.27.1.linux-amd64.tar.gz'},
        'compiler': {'raceRequired': True, 'cgoEnabled': True, **{key: 'a' * 64 for key in
            ['compilerSha256', 'headersManifestSha256', 'linkerSha256', 'runtimeClosureSha256']}},
        'aptTransaction': {'qualifiedSignedTransactionSha256': 'a' * 64, 'expectedFinalInventorySha256': 'b' * 64},
        'policy': {'apparmorRestrictUnprivilegedUserns': 0, 'nativeUnitNoNewPrivileges': True, 'noJobPolicySetter': True}}


class DedicatedRunnerDataControls(unittest.TestCase):
    def test_missing_target_stays_unknown(self):
        with self.assertRaises(ValueError): owner.validate_target_data({})

    def test_shared_host_refuses_even_with_other_matching_facts(self):
        value = target(); value['sharedCodexHost'] = True
        with self.assertRaises(ValueError): owner.validate_target_data(value)

    def test_prerelease_or_old_libc_carrier_not_relabelled(self):
        for os_value in [{'id': 'ubuntu', 'version': '24.04'}, {'id': 'fedora', 'version': '45'}]:
            value = target(); value['os'] = os_value
            with self.assertRaises(ValueError): owner.validate_target_data(value)

    def test_target_identity_does_not_accept_another_vm(self):
        value = target()
        approval = {'schema': 'fsgg.dedicated-image-build-selection/1', 'providerInstanceId': value['providerInstanceId'],
            'systemUuid': value['systemUuid'], 'baselineId': owner.BASELINE_ID,
            'operation': 'build-dedicated-image', 'targetSha256': 'a' * 64}
        self.assertTrue(owner.validate_instance_match(value, approval, 'test-uuid'))
        with self.assertRaises(ValueError): owner.validate_instance_match(value, approval, 'another-vm')

    def test_cgo_race_cannot_be_skipped(self):
        for field in ['cgoEnabled', 'raceRequired']:
            value = lock(); value['compiler'][field] = False
            with self.assertRaises(ValueError): owner.validate_lock_data(value)

    def test_absent_apparmor_is_not_zero(self):
        for value in [None, -1, '0', False]:
            selected = lock(); selected['policy']['apparmorRestrictUnprivilegedUserns'] = value
            with self.assertRaises(ValueError): owner.validate_lock_data(selected)

    def test_native_nnp_cannot_be_weakened(self):
        selected = lock(); selected['policy']['nativeUnitNoNewPrivileges'] = False
        with self.assertRaises(ValueError): owner.validate_lock_data(selected)

    def test_sdk401_archive_identity_is_exact(self):
        selected = lock(); selected['sdk']['archiveSha512'] = 'a' * 128
        with self.assertRaises(ValueError): owner.validate_lock_data(selected)

    def test_matching_source_lock_is_data_not_execution(self):
        self.assertEqual(owner.validate_lock_data(lock()), lock())
        self.assertEqual(owner.validate_target_data(target()), target())

    def test_complete_population_detects_addition_and_substitution(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); payload = b'authored test input only'; (root / 'a').write_bytes(payload)
            selected = {'files': [{'path': 'a', 'bytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()}]}
            self.assertEqual(owner.validate_population(selected, root)['files'], 1)
            (root / 'extra').write_bytes(b'x')
            with self.assertRaises(ValueError): owner.validate_population(selected, root)
            (root / 'extra').unlink(); (root / 'a').write_bytes(b'changed')
            with self.assertRaises(ValueError): owner.validate_population(selected, root)

    def test_linked_and_traversal_input_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); (root / 'a').symlink_to('/nonexistent')
            selected = {'files': [{'path': 'a', 'bytes': 0, 'sha256': 'a' * 64}]}
            with self.assertRaises(ValueError): owner.validate_population(selected, root)
            selected['files'][0]['path'] = '../a'
            with self.assertRaises(ValueError): owner.validate_population(selected, root)

    def test_baseline_is_not_ready_without_actual_inputs(self):
        value = json.loads((ROOT / 'deployment/qualification-runner/baseline.json').read_text())
        self.assertIsNone(value['target']); self.assertIsNone(value['acquiredInputLock']); self.assertIsNone(value['nativeAdmission'])


if __name__ == '__main__': unittest.main()
