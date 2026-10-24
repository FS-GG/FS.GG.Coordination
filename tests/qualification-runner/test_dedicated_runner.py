"""Tiny DATA/refusal controls only; no VM, native SDK/compiler, policy, key or registration operation."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
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
        'resources': {'vcpu': 4, 'memoryBytes': 16 * 1024**3, 'diskBytes': 256 * 1024**3}}


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
        'policy': {'apparmorRestrictUnprivilegedUserns': 0, 'nativeWorkloadNoNewPrivileges': True, 'noJobPolicySetter': True}}


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

    def test_home16_candidate_is_data_and_smaller_guest_refuses(self):
        self.assertEqual(owner.validate_target_data(target())['resources']['memoryBytes'], 16 * 1024**3)
        selected = target(); selected['resources']['memoryBytes'] = 16 * 1024**3 - 1
        with self.assertRaises(ValueError): owner.validate_target_data(selected)
        baseline = json.loads((ROOT / 'deployment/qualification-runner/baseline.json').read_text())
        self.assertIsNone(baseline['target']); self.assertIsNone(baseline['nativeAdmission'])

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
        selected = lock(); selected['policy']['nativeWorkloadNoNewPrivileges'] = False
        with self.assertRaises(ValueError): owner.validate_lock_data(selected)

    def test_sdk401_archive_identity_is_exact(self):
        selected = lock(); selected['sdk']['archiveSha512'] = 'a' * 128
        with self.assertRaises(ValueError): owner.validate_lock_data(selected)

    def test_matching_source_lock_is_data_not_execution(self):
        self.assertEqual(owner.validate_lock_data(lock()), lock())
        self.assertEqual(owner.validate_target_data(target()), target())

    def instance(self, **changes):
        value = {'schema': 'fsgg.dedicated-qualification-instance/1', 'baselineId': owner.BASELINE_ID,
            'oneJobOnly': True, 'runnerName': 'fsgg-q-20261024t2300z-0123abcd', 'runnerGroup': owner.GROUP,
            'runnerLabel': owner.LABEL, 'runnerRepository': owner.CALLER_REPOSITORY, 'repositoryScopeVerified': True,
            'credentialKind': 'jit-config', 'qualifiedImageSha256': 'a' * 64, 'rootInstanceAcceptanceSha256': 'b' * 64,
            'registrationAdmissionSha256': 'c' * 64, 'providerInstanceId': 'authored-vm',
            'providerImageId': 'authored-image', 'systemUuid': 'authored-uuid'}
        return {**value, **changes}

    def test_registration_requires_exact_single_use_default_group_private_repository_route(self):
        instance = self.instance()
        self.assertEqual((owner.GROUP, owner.LABEL, owner.CALLER_REPOSITORY),
                         ('Default', 'fsgg-native-linux-x64-v1', 'FS-GG/FS.GG.Native.Qualification'))
        self.assertTrue(owner.validate_registration_data(instance, lock(), instance['runnerName']))
        for key, value in [('runnerGroup', 'fsgg-native-qualification'), ('oneJobOnly', False), ('credentialKind', 'PAT'),
                            ('credentialKind', 'short-lived-registration-token'), ('repositoryScopeVerified', False),
                            ('runnerRepository', 'FS-GG/FS.GG.Coordination'), ('registrationAdmissionSha256', None),
                            ('runnerName', 'fsgg-qualification-authored')]:
            selected = {**instance, key: value}
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                owner.validate_registration_data(selected, lock(), selected['runnerName'])
        with self.assertRaises(ValueError): owner.validate_registration_data(instance, lock(), 'fsgg-q-another')
        for name in ('fsgg-q-', 'fsgg-q-a b', 'x-fsgg-q-a', 'fsgg-q-' + 'a' * 65, 'fsgg-q-;id'):
            self.assertFalse(owner.runner_name_ok(name), name)
        self.assertTrue(owner.runner_name_ok('fsgg-q-20261024t2300z-0123abcd'))

    def request(self, **changes):
        instance = self.instance()
        value = {'schema': 'fsgg.dedicated-qualification-binding-request/1',
                 **{key: instance[key] for key in owner.REQUEST_FIELDS}}
        return {**value, **changes}

    def test_binding_is_built_only_from_the_exact_request_and_actual_machine_uuid(self):
        built = owner.binding_from_request(self.request(), lock(), 'authored-uuid')
        self.assertEqual(built, self.instance())
        with self.assertRaises(ValueError): owner.binding_from_request(self.request(), lock(), 'another-vm')
        with self.assertRaises(ValueError): owner.binding_from_request({**self.request(), 'extra': 1}, lock(), 'authored-uuid')
        request = self.request(); del request['runnerName']
        with self.assertRaises(ValueError): owner.binding_from_request(request, lock(), 'authored-uuid')
        with self.assertRaises(ValueError): owner.binding_from_request(self.request(repositoryScopeVerified=False), lock(), 'authored-uuid')
        with self.assertRaises(ValueError): owner.binding_from_request(self.request(runnerName='fsgg-qualification-x'), lock(), 'authored-uuid')

    def test_binding_is_written_once_sealed_and_never_replaced(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); root.chmod(0o755)
            digest = owner.write_binding(self.instance(), root, owner_uid=os.getuid())
            path = root / 'instance-binding.json'
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), digest)
            self.assertEqual(oct(path.stat().st_mode & 0o777), '0o644')
            self.assertEqual(json.loads(path.read_text()), self.instance())
            with self.assertRaises(FileExistsError): owner.write_binding(self.instance(runnerName='fsgg-q-second'), root, owner_uid=os.getuid())
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), digest)
            root.chmod(0o777)
            (path).unlink()
            with self.assertRaises(ValueError): owner.write_binding(self.instance(), root, owner_uid=os.getuid())

    def guest(self, root, *, openssl='4.0.3-1ubuntu1', binding=True):
        """A complete passing fake guest root; the real recipe's file names and bytes."""
        deployment = ROOT / 'deployment/qualification-runner'
        def put(name, data, mode):
            path = root / name.lstrip('/'); path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data); path.chmod(mode); return path
        put('/etc/os-release', b'ID=ubuntu\nVERSION_ID="26.10"\n', 0o644)
        put('/etc/passwd', b'root:x:0:0::/root:/bin/bash\nrunner:x:1001:1001::/home/runner:/bin/bash\n', 0o644)
        put('/etc/group', b'root:x:0:\nrunner:x:1001:\n', 0o644)
        put(owner.SYSCTL_DROPIN, (deployment / '90-fsgg-qualification.conf').read_bytes(), 0o644)
        put(owner.SUDOERS_FILE, (deployment / 'runner.sudoers').read_bytes(), 0o440)
        put(owner.USERNS_KNOB, b'0\n', 0o644)
        put(owner.STATE_DIRECTORY + '/acquired-input-lock.json', json.dumps(lock()).encode(), 0o644)
        for name in ('run.sh', 'config.sh', 'bin/Runner.Listener'):
            put(owner.RUNNER_ROOT + '/' + name, b'#!/bin/sh\n', 0o755)
        put(owner.RUNNER_ROOT + '/bin/runnerversion', b'1.2.3\n', 0o644)
        put(owner.DOTNET_ROOT + '/dotnet', b'x', 0o755)
        (root / owner.DOTNET_ROOT.lstrip('/') / 'sdk/10.0.401').mkdir(parents=True)
        (root / owner.DOTNET_ROOT.lstrip('/') / 'shared/Microsoft.NETCore.App/10.0.12').mkdir(parents=True)
        put(owner.GO_ROOT + '/VERSION', b'go1.27.1\ntime 2026-10-01T00:00:00Z\n', 0o644)
        put(owner.GO_ROOT + '/bin/go', b'x', 0o755)
        for tool in ('docker', 'systemd-run', 'unshare', 'losetup', 'mkfs.ext4', 'fallocate', 'setpriv', 'openssl', 'newuidmap', 'newgidmap'):
            put('/usr/bin/' + tool, b'x', 0o755)
        put('/var/lib/dpkg/status', ('Package: openssl\nStatus: install ok installed\nVersion: ' + openssl + '\n\n'
                                     'Package: docker.io\nStatus: install ok installed\nVersion: 1\n').encode(), 0o644)
        listing = []
        for name in owner.LAYER_FILES:
            data = (ROOT / name).read_bytes()
            put(owner.LAYER_ROOT + '/' + name, data, 0o755)
            listing.append(hashlib.sha256(data).hexdigest() + '  ./' + name)
        put(owner.LAYER_ROOT + '/MANIFEST.sha256', ('\n'.join(sorted(listing, key=lambda row: row.split('  ')[1])) + '\n').encode(), 0o644)
        if binding:
            put(owner.STATE_DIRECTORY + '/instance-binding.json', json.dumps(self.instance()).encode(), 0o644)
        return root

    def verify(self, root, **changes):
        arguments = dict(system_uuid='authored-uuid', runner_name=self.instance()['runnerName'], owner_uid=os.getuid(), runner_uid=os.getuid(), machine='x86_64')
        return owner.verify_guest(root, ROOT, 'boot', **{**arguments, **changes})['refusals']

    def test_guest_readback_accepts_only_the_complete_baked_layer_on_the_openssl3_family(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.5.4-1ubuntu1')
            self.assertEqual(self.verify(root), [])
            result = owner.verify_guest(root, ROOT, 'boot', system_uuid='authored-uuid', runner_name=self.instance()['runnerName'], owner_uid=os.getuid(), runner_uid=os.getuid(), machine='x86_64')
            self.assertEqual(result['facts']['goVersion'], 'go1.27.1')
            self.assertEqual(result['facts']['runnerVersion'], '1.2.3')
            self.assertIn('Arch userland', result['facts']['buildah'])

    def test_guest_readback_names_the_openssl4_blocker_of_ubuntu_26_10(self):
        # Ubuntu 26.10 ships OpenSSL 4.0.x; bar_hosted_operation.crypto_identity accepts only the 3.x family.
        source = (ROOT / 'eng/bar_hosted_operation.py').read_text()
        self.assertIn("version.startswith('" + owner.OPENSSL_FAMILY_PREFIX + "')", source)
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.verify(self.guest(Path(directory))), ['openssl-family-not-3'])

    def test_guest_readback_refuses_each_missing_or_altered_requirement(self):
        cases = {
            'os-not-ubuntu-26.10': lambda r: (r / 'etc/os-release').write_bytes(b'ID=ubuntu\nVERSION_ID="24.04"\n'),
            'apparmor-userns-not-zero': lambda r: (r / owner.USERNS_KNOB.lstrip('/')).write_bytes(b'1\n'),
            'sysctl-dropin-bytes': lambda r: (r / owner.SYSCTL_DROPIN.lstrip('/')).write_bytes(b'kernel.apparmor_restrict_unprivileged_userns = 1\n'),
            'sudoers-bytes': lambda r: ((r / owner.SUDOERS_FILE.lstrip('/')).chmod(0o640), (r / owner.SUDOERS_FILE.lstrip('/')).write_bytes(b'runner ALL=(ALL) NOPASSWD: ALL\n'), (r / owner.SUDOERS_FILE.lstrip('/')).chmod(0o440)),
            'runner-uid-1001-account': lambda r: (r / 'etc/passwd').write_bytes(b'runner:x:1002:1002::/home/runner:/bin/bash\n'),
            'runner-gid-1001-group': lambda r: (r / 'etc/group').write_bytes(b'runner:x:1002:\n'),
            'runner-already-configured:.runner': lambda r: (r / owner.RUNNER_ROOT.lstrip('/') / '.runner').write_bytes(b'{}'),
            'runner-version-differs-from-lock': lambda r: (r / owner.RUNNER_ROOT.lstrip('/') / 'bin/runnerversion').write_bytes(b'9.9.9\n'),
            'runner-file-missing:run.sh': lambda r: (r / owner.RUNNER_ROOT.lstrip('/') / 'run.sh').unlink(),
            'dotnet-sdk-10.0.401': lambda r: (r / owner.DOTNET_ROOT.lstrip('/') / 'sdk/10.0.100').mkdir(),
            'dotnet-runtime-10.0.12': lambda r: (r / owner.DOTNET_ROOT.lstrip('/') / 'shared/Microsoft.NETCore.App/10.0.12').rmdir(),
            'go-1.27.1': lambda r: (r / owner.GO_ROOT.lstrip('/') / 'VERSION').write_bytes(b'go1.24.1\n'),
            'tool-missing:docker': lambda r: (r / 'usr/bin/docker').unlink(),
            'instance-binding-missing': lambda r: (r / owner.STATE_DIRECTORY.lstrip('/') / 'instance-binding.json').unlink(),
            'layer-file-differs:eng/dedicated_qualification_runner.py': lambda r: (r / owner.LAYER_ROOT.lstrip('/') / 'eng/dedicated_qualification_runner.py').write_bytes(b'tampered'),
            'layer-file-unlisted': lambda r: (r / owner.LAYER_ROOT.lstrip('/') / 'extra.sh').write_bytes(b'x'),
            'layer-manifest-missing': lambda r: (r / owner.LAYER_ROOT.lstrip('/') / 'MANIFEST.sha256').unlink(),
            'layer-file-not-listed': lambda r: (r / owner.LAYER_ROOT.lstrip('/') / 'MANIFEST.sha256').write_bytes(b''),
        }
        for expected, damage in cases.items():
            with self.subTest(expected), tempfile.TemporaryDirectory() as directory:
                root = self.guest(Path(directory), openssl='3.5.4-1ubuntu1')
                damage(root)
                refusals = self.verify(root)
                self.assertIn(expected, refusals)
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.5.4-1ubuntu1')
            self.assertEqual(self.verify(root, machine='aarch64'), ['architecture-not-x86_64'])
            self.assertIn('binding-uuid-differs', self.verify(root, system_uuid='another-vm'))
            self.assertIn('instance-binding-data: ' + 'dedicated-runner: exact ephemeral fsgg-q- runner name required',
                          self.verify(root, runner_name='fsgg-q-other'))
            self.assertTrue(any(row.startswith('sudoers-owner-or-mode') for row in self.verify(root, owner_uid=os.getuid() + 1)))

    def test_image_phase_refuses_a_baked_per_boot_binding(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.5.4-1ubuntu1')
            self.assertEqual(owner.verify_guest(root, ROOT, 'image', owner_uid=os.getuid(), runner_uid=os.getuid(), machine='x86_64')['refusals'], ['image-contains-instance-binding'])
            (root / owner.STATE_DIRECTORY.lstrip('/') / 'instance-binding.json').unlink()
            self.assertEqual(owner.verify_guest(root, ROOT, 'image', owner_uid=os.getuid(), runner_uid=os.getuid(), machine='x86_64')['refusals'], [])
            with self.assertRaises(ValueError): owner.verify_guest(root, ROOT, 'boot', owner_uid=os.getuid())

    def test_guest_scripts_refuse_without_exact_arguments_privilege_and_name(self):
        directory = ROOT / 'deployment/qualification-runner'
        environment = {'PATH': '/usr/bin:/bin'}
        cases = [(['guest-boot.sh'], 2), (['guest-boot.sh', '--apply', '/nonexistent', 'fsgg-q-a'], 2),
                 (['guest-boot.sh', '--apply', '/nonexistent', 'fsgg-qualification-a'], 2),
                 (['run-jit-runner.sh'], 2), (['provision-image.sh'], 2)]
        for argv, code in cases:
            with self.subTest(argv=argv):
                result = subprocess.run(['/bin/bash', str(directory / argv[0]), *argv[1:]], env=environment, capture_output=True, text=True, timeout=20)
                self.assertEqual(result.returncode, code, result.stderr)
        # Without the JIT configuration (and, off-guest, without root) the runner never starts.
        result = subprocess.run(['/bin/bash', str(directory / 'run-jit-runner.sh'), '--run', 'fsgg-q-a'], env=environment, capture_output=True, text=True, timeout=20)
        self.assertNotEqual(result.returncode, 0)
        for name in ('guest-boot.sh', 'run-jit-runner.sh', 'provision-image.sh'):
            self.assertEqual(subprocess.run(['/bin/bash', '-n', str(directory / name)], capture_output=True).returncode, 0, name)
        self.assertFalse((directory / 'register-ephemeral-runner.sh').exists(), 'org registration-token route is retired')
        text = (directory / 'run-jit-runner.sh').read_text()
        self.assertIn('ACTIONS_RUNNER_INPUT_JITCONFIG', text)
        self.assertNotIn('--url', text)
        self.assertNotIn('config.sh', text)

    def test_layer_install_list_matches_what_the_guest_scripts_and_readback_use(self):
        recipe = (ROOT / 'deployment/qualification-runner/provision-image.sh').read_text()
        for name in ('guest-boot.sh', 'run-jit-runner.sh', '90-fsgg-qualification.conf', 'runner.sudoers', 'dedicated_qualification_runner.py'):
            self.assertIn(name, recipe)
        for constant in (owner.RUNNER_ROOT, owner.DOTNET_ROOT, owner.GO_ROOT):
            self.assertIn(constant, recipe)
        self.assertIn('verify-guest --phase image', recipe)
        self.assertIn('libicu78', recipe)

    def test_public_coordination_never_targets_the_private_runner_label(self):
        for path in sorted((ROOT / '.github').rglob('*')):
            if path.is_file():
                text = path.read_text(errors='replace')
                self.assertNotIn(owner.LABEL, text, str(path.relative_to(ROOT)))
                self.assertNotIn('group: fsgg-native', text, str(path.relative_to(ROOT)))
        template = (ROOT / 'deployment/qualification-runner/native-qualification/bar-hosted-image-qualification.yml').read_text()
        self.assertIn('runs-on: [self-hosted, linux, x64, ' + owner.LABEL + ']', template)
        baseline = json.loads((ROOT / 'deployment/qualification-runner/baseline.json').read_text())['github']
        self.assertEqual((baseline['runnerGroup'], baseline['runnerRepository'], baseline['fixedLabel'], baseline['runnerNamePrefix']),
                         (owner.GROUP, owner.CALLER_REPOSITORY, owner.LABEL, 'fsgg-q-'))
        self.assertEqual(baseline['allowedWorkflows'], [owner.CALLER_REPOSITORY + '/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main'])

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
