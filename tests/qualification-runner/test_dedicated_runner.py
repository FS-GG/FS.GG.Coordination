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
        'sharedCodexHost': False, 'architecture': 'x86_64', 'os': {'id': 'ubuntu', 'version': '24.04'},
        'resources': {'vcpu': 4, 'memoryBytes': 16 * 1024**3, 'diskBytes': 256 * 1024**3}}


def lock():
    return {'schema': 'fsgg.dedicated-qualification-acquired-inputs/1', 'baselineId': owner.BASELINE_ID,
        'image': {'url': 'https://cloud-images.ubuntu.com/releases/noble/release-20261017/ubuntu-24.04-server-cloudimg-amd64.img', 'sha256': 'a' * 64,
            'signedChecksumSha256': 'b' * 64, 'qualifiedAcquisitionReceiptSha256': 'c' * 64, 'release': 'release-20261017'},
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
        for os_value in [{'id': 'ubuntu', 'version': '26.10'}, {'id': 'ubuntu', 'version': '22.04'}, {'id': 'fedora', 'version': '45'}]:
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
            'allowedWorkflow': owner.TARGETS[owner.CALLER_REPOSITORY]['workflow'], 'allowedJob': owner.TARGETS[owner.CALLER_REPOSITORY]['job'],
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

    def test_exactly_two_private_targets_each_bound_to_one_workflow_and_job(self):
        self.assertEqual(set(owner.TARGETS), {'FS-GG/FS.GG.Native.Qualification', 'FS-GG/FS.GG.FourD'})
        self.assertEqual(owner.TARGETS[owner.FOURD_REPOSITORY], {
            'workflow': 'FS-GG/FS.GG.FourD/.github/workflows/fourd-native-image-qualification.yml@refs/heads/main', 'job': 'native-qualification'})
        self.assertEqual(owner.TARGETS[owner.CALLER_REPOSITORY]['job'], 'full-image')
        # The BAR template's job id and the wrapper's workflow ref are the same row, not a second copy that can drift.
        template = (ROOT / 'deployment/qualification-runner/native-qualification/bar-hosted-image-qualification.yml').read_text()
        self.assertIn('\n  full-image:\n', template)
        job_block = template.split('\n  full-image:\n', 1)[1].split('\n    steps:', 1)[0]
        self.assertNotIn('\n    name:', '\n' + job_block, 'job id and job-record name must stay the same')
        self.assertEqual(owner.TARGETS[owner.CALLER_REPOSITORY]['workflow'], BAR_OPERATION_WORKFLOW_REF)
        for name, row in owner.TARGETS.items():
            self.assertTrue(row['workflow'].startswith(name + '/.github/workflows/') and row['workflow'].endswith('@refs/heads/main'))

    def test_both_targets_are_sealed_with_their_own_workflow_and_job(self):
        for repository, row in owner.TARGETS.items():
            with self.subTest(repository=repository):
                built = owner.binding_from_request(self.request(runnerRepository=repository), lock(), 'authored-uuid')
                self.assertEqual((built['runnerRepository'], built['allowedWorkflow'], built['allowedJob']), (repository, row['workflow'], row['job']))
                self.assertTrue(owner.validate_registration_data(built, lock(), built['runnerName']))

    def test_runner_repository_is_required_and_restricted_to_the_two_targets(self):
        request = self.request(); del request['runnerRepository']
        with self.assertRaises(ValueError): owner.binding_from_request(request, lock(), 'authored-uuid')
        for repository in ('FS-GG/FS.GG.Coordination', 'FS-GG/FS.GG.Templates', 'FS-GG/FS.GG.Native.Qualification ', 'fs-gg/fs.gg.fourd',
                           'FS-GG/FS.GG.FourD/x', '', None, ['FS-GG/FS.GG.FourD'], {'a': 1}):
            with self.subTest(repository=repository), self.assertRaises(ValueError):
                owner.binding_from_request(self.request(runnerRepository=repository), lock(), 'authored-uuid')

    def test_workflow_or_job_of_the_other_repository_refuses(self):
        bar, fourd = owner.TARGETS[owner.CALLER_REPOSITORY], owner.TARGETS[owner.FOURD_REPOSITORY]
        cases = [dict(runnerRepository=owner.CALLER_REPOSITORY, allowedWorkflow=fourd['workflow']),
                 dict(runnerRepository=owner.CALLER_REPOSITORY, allowedJob=fourd['job']),
                 dict(runnerRepository=owner.FOURD_REPOSITORY, allowedWorkflow=bar['workflow'], allowedJob=fourd['job']),
                 dict(runnerRepository=owner.FOURD_REPOSITORY, allowedWorkflow=fourd['workflow'], allowedJob=bar['job']),
                 dict(runnerRepository=owner.FOURD_REPOSITORY),  # repository switched but the BAR workflow/job left in place
                 dict(allowedWorkflow=bar['workflow'].replace('refs/heads/main', 'refs/heads/other')),
                 dict(allowedWorkflow=None), dict(allowedJob=None), dict(allowedJob='other-job')]
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                owner.validate_registration_data(self.instance(**changes), lock(), self.instance()['runnerName'])
        for key in ('allowedWorkflow', 'allowedJob', 'runnerRepository'):
            value = self.instance(); del value[key]
            with self.subTest(missing=key), self.assertRaises(ValueError):
                owner.validate_registration_data(value, lock(), value['runnerName'])

    def test_request_cannot_carry_its_own_workflow_or_job(self):
        for extra in ('allowedWorkflow', 'allowedJob'):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                owner.binding_from_request({**self.request(), extra: 'anything'}, lock(), 'authored-uuid')

    def test_guest_readback_refuses_a_binding_that_names_the_wrong_workflow_for_its_repository(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.0.13-0ubuntu3.16')
            self.assertEqual(self.verify(root), [])
            binding = root / owner.STATE_DIRECTORY.lstrip('/') / 'instance-binding.json'
            fourd = self.instance(runnerRepository=owner.FOURD_REPOSITORY, allowedWorkflow=owner.TARGETS[owner.FOURD_REPOSITORY]['workflow'],
                                  allowedJob=owner.TARGETS[owner.FOURD_REPOSITORY]['job'])
            binding.write_text(json.dumps(fourd))
            self.assertEqual(self.verify(root), [], 'a coherent FourD binding is a valid binding')
            binding.write_text(json.dumps({**fourd, 'allowedJob': 'full-image'}))
            self.assertTrue(any(row.startswith('instance-binding-data') for row in self.verify(root)))

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

    def guest(self, root, *, openssl='3.0.13-0ubuntu3.16', binding=True):
        """A complete passing fake guest root; the real recipe's file names and bytes."""
        deployment = ROOT / 'deployment/qualification-runner'
        def put(name, data, mode):
            path = root / name.lstrip('/'); path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data); path.chmod(mode); return path
        put('/etc/os-release', b'ID=ubuntu\nVERSION_ID="24.04"\n', 0o644)
        put('/etc/passwd', b'root:x:0:0::/root:/bin/bash\nrunner:x:1001:1001::/home/runner:/bin/bash\n', 0o644)
        put('/etc/group', b'root:x:0:\nrunner:x:1001:\n', 0o644)
        put(owner.SYSCTL_DROPIN, (deployment / '90-fsgg-qualification.conf').read_bytes(), 0o644)
        put(owner.SUDOERS_FILE, (deployment / 'runner.sudoers').read_bytes(), 0o440)
        put(owner.USERNS_KNOB, b'0\n', 0o644)
        put(owner.STATE_DIRECTORY + '/acquired-input-lock.json', json.dumps(lock()).encode(), 0o644)
        for name in ('run.sh', 'config.sh', 'bin/Runner.Listener'):
            put(owner.RUNNER_ROOT + '/' + name, b'#!/bin/sh\n', 0o755)
        put(owner.RUNNER_ROOT + '/bin/Runner.Listener.deps.json', b'{"libraries":{"Runner.Listener/1.2.3":{},"Runner.Common/1.0.0":{}}}', 0o644)
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
            root = self.guest(Path(directory), openssl='3.0.13-0ubuntu3.16')
            self.assertEqual(self.verify(root), [])
            result = owner.verify_guest(root, ROOT, 'boot', system_uuid='authored-uuid', runner_name=self.instance()['runnerName'], owner_uid=os.getuid(), runner_uid=os.getuid(), machine='x86_64')
            self.assertEqual(result['facts']['goVersion'], 'go1.27.1')
            self.assertEqual(result['facts']['runnerVersion'], '1.2.3')
            self.assertIn('Arch userland', result['facts']['buildah'])

    def test_guest_readback_keeps_refusing_the_openssl4_family_without_relaxing_the_gate(self):
        # Root chose Ubuntu 24.04 (OpenSSL 3.0.x) instead of relaxing bar_hosted_operation.crypto_identity,
        # which accepts only the 3.x family. A newer carrier with OpenSSL 4 must still be refused.
        source = (ROOT / 'eng/bar_hosted_operation.py').read_text()
        self.assertIn("version.startswith('" + owner.OPENSSL_FAMILY_PREFIX + "')", source)
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.verify(self.guest(Path(directory), openssl='4.0.3-1ubuntu1')), ['openssl-family-not-3'])

    def test_lock_pins_only_an_official_immutable_noble_release_image(self):
        good = lock()
        self.assertEqual(owner.BASELINE_ID, 'ubuntu2404-linux-x64-v1')
        for url in ('https://cloud-images.ubuntu.com/stonking/current/stonking-server-cloudimg-amd64.img',
                    'https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img',
                    'https://cloud-images.ubuntu.com/releases/noble/release-20261017/ubuntu-24.04-server-cloudimg-amd64-root.tar.xz',
                    'https://example.invalid/releases/noble/release-20261017/ubuntu-24.04-server-cloudimg-amd64.img'):
            selected = lock(); selected['image']['url'] = url
            with self.subTest(url=url), self.assertRaises(ValueError): owner.validate_lock_data(selected)
        for release in ('release-20260926', 'current', 'release-1'):
            selected = lock(); selected['image']['release'] = release
            with self.subTest(release=release), self.assertRaises(ValueError): owner.validate_lock_data(selected)
        self.assertEqual(owner.validate_lock_data(good), good)

    def test_guest_readback_refuses_each_missing_or_altered_requirement(self):
        cases = {
            'os-not-ubuntu-24.04': lambda r: (r / 'etc/os-release').write_bytes(b'ID=ubuntu\nVERSION_ID="26.10"\n'),
            'apparmor-userns-not-zero': lambda r: (r / owner.USERNS_KNOB.lstrip('/')).write_bytes(b'1\n'),
            'sysctl-dropin-bytes': lambda r: (r / owner.SYSCTL_DROPIN.lstrip('/')).write_bytes(b'kernel.apparmor_restrict_unprivileged_userns = 1\n'),
            'sudoers-bytes': lambda r: ((r / owner.SUDOERS_FILE.lstrip('/')).chmod(0o640), (r / owner.SUDOERS_FILE.lstrip('/')).write_bytes(b'runner ALL=(ALL) NOPASSWD: ALL\n'), (r / owner.SUDOERS_FILE.lstrip('/')).chmod(0o440)),
            'runner-uid-1001-account': lambda r: (r / 'etc/passwd').write_bytes(b'runner:x:1002:1002::/home/runner:/bin/bash\n'),
            'runner-gid-1001-group': lambda r: (r / 'etc/group').write_bytes(b'runner:x:1002:\n'),
            'runner-already-configured:.runner': lambda r: (r / owner.RUNNER_ROOT.lstrip('/') / '.runner').write_bytes(b'{}'),
            'runner-version-differs-from-lock': lambda r: (r / owner.RUNNER_ROOT.lstrip('/') / 'bin/Runner.Listener.deps.json').write_bytes(b'{"libraries":{"Runner.Listener/9.9.9":{}}}'),
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
                root = self.guest(Path(directory), openssl='3.0.13-0ubuntu3.16')
                damage(root)
                refusals = self.verify(root)
                self.assertIn(expected, refusals)
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.0.13-0ubuntu3.16')
            self.assertEqual(self.verify(root, machine='aarch64'), ['architecture-not-x86_64'])
            self.assertIn('binding-uuid-differs', self.verify(root, system_uuid='another-vm'))
            self.assertIn('instance-binding-data: ' + 'dedicated-runner: exact ephemeral fsgg-q- runner name required',
                          self.verify(root, runner_name='fsgg-q-other'))
            self.assertTrue(any(row.startswith('sudoers-owner-or-mode') for row in self.verify(root, owner_uid=os.getuid() + 1)))

    def test_image_phase_refuses_a_baked_per_boot_binding(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self.guest(Path(directory), openssl='3.0.13-0ubuntu3.16')
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
        self.assertIn('noble-apt.sh', recipe)
        self.assertIn('libicu74', (ROOT / 'deployment/qualification-runner/noble-apt.sh').read_text())

    def test_public_coordination_never_targets_the_private_runner_label(self):
        for path in sorted((ROOT / '.github').rglob('*')):
            if path.is_file():
                text = path.read_text(errors='replace')
                self.assertNotIn(owner.LABEL, text, str(path.relative_to(ROOT)))
                self.assertNotIn('group: fsgg-native', text, str(path.relative_to(ROOT)))
        template = (ROOT / 'deployment/qualification-runner/native-qualification/bar-hosted-image-qualification.yml').read_text()
        self.assertIn('runs-on: [self-hosted, linux, x64, ' + owner.LABEL + ']', template)
        baseline = json.loads((ROOT / 'deployment/qualification-runner/baseline.json').read_text())['github']
        self.assertEqual((baseline['runnerGroup'], baseline['fixedLabel'], baseline['runnerNamePrefix']), (owner.GROUP, owner.LABEL, 'fsgg-q-'))
        self.assertEqual(baseline['runnerTargets'], [{'repository': name, 'workflow': row['workflow'], 'job': row['job'],
                                                      **({'decision': baseline['runnerTargets'][1]['decision']} if name == owner.FOURD_REPOSITORY else {})}
                                                     for name, row in owner.TARGETS.items()])
        self.assertEqual(baseline['allowedWorkflows'], [row['workflow'] for row in owner.TARGETS.values()])

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

    def test_baseline_is_not_ready_without_actual_target_or_admission(self):
        value = json.loads((ROOT / 'deployment/qualification-runner/baseline.json').read_text())
        self.assertIsNone(value['target']); self.assertIsNone(value['nativeAdmission'])
        self.assertEqual(value['id'], owner.BASELINE_ID)
        self.assertEqual(value['acquiredInputLock'], 'noble/acquired-input-lock.json')  # source record, not admission
        self.assertEqual((value['carrier']['version'], value['carrier']['codename']), ('24.04', 'noble'))


spec_operation = importlib.util.spec_from_file_location('bar_operation_source', ROOT / 'eng/bar_hosted_operation.py')
operation = importlib.util.module_from_spec(spec_operation); spec_operation.loader.exec_module(operation)
BAR_OPERATION_WORKFLOW_REF = operation.WORKFLOW_REF

NOBLE = ROOT / 'deployment/qualification-runner/noble'
spec_inputs = importlib.util.spec_from_file_location('noble_inputs_source', ROOT / 'deployment/qualification-runner/noble-inputs.py')
inputs = importlib.util.module_from_spec(spec_inputs); spec_inputs.loader.exec_module(inputs)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class NobleInputRecords(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lock = json.loads((NOBLE / 'acquired-input-lock.json').read_text())
        cls.transaction = json.loads((NOBLE / 'apt-transaction.json').read_text())
        cls.inventory = [row.split('\t') for row in (NOBLE / 'package-inventory.expected').read_text().splitlines()]

    def test_lock_is_valid_and_binds_every_committed_record(self):
        lock = self.lock
        self.assertEqual(owner.validate_lock_data(lock), lock)
        self.assertEqual(lock['baselineId'], owner.BASELINE_ID)
        self.assertEqual(lock['image']['signedChecksumSha256'], digest(NOBLE / 'image/SHA256SUMS'))
        self.assertEqual(lock['image']['qualifiedAcquisitionReceiptSha256'], digest(NOBLE / 'image-acquisition-receipt.json'))
        self.assertEqual(lock['runner']['releaseMetadataSha256'], digest(NOBLE / 'runner-release.json'))
        self.assertEqual(lock['aptTransaction']['expectedFinalInventorySha256'], digest(NOBLE / 'package-inventory.expected'))
        self.assertEqual(lock['aptTransaction']['qualifiedSignedTransactionSha256'], digest(NOBLE / 'apt-transaction.json'))
        runner = json.loads((NOBLE / 'runner-release.json').read_text())
        self.assertEqual(runner['digest'], 'sha256:' + lock['runner']['sha256'])
        self.assertEqual(runner['tag'], 'v' + lock['runner']['version'])
        self.assertEqual(lock['goRace']['version'], '1.27.1')

    def test_population_rows_are_complete_ordered_and_origin_pinned(self):
        rows = self.lock['files']
        paths = [row['path'] for row in rows]
        self.assertEqual(paths, sorted(set(paths)))
        derived = {'apt/package-inventory.expected': NOBLE / 'package-inventory.expected', 'apt/apt-transaction.json': NOBLE / 'apt-transaction.json'}
        for row in rows:
            self.assertEqual(sorted(row), ['bytes', 'path', 'sha256'] + ([] if row['path'] in derived else ['url']), row['path'])
            if row['path'] in derived:
                self.assertEqual((row['sha256'], row['bytes']), (digest(derived[row['path']]), derived[row['path']].stat().st_size))
            else:
                self.assertTrue(row['url'].startswith('https://'), row['path'])
        for required in ('sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz', 'go/go1.27.1.linux-amd64.tar.gz', 'runner/actions-runner-linux-x64-2.338.1.tar.gz'):
            self.assertIn(required, paths)
        pool = sorted(row['path'][len('apt/'):] for row in rows if row['path'].startswith('apt/pool/'))
        self.assertEqual(pool, sorted(row['filename'] for row in self.transaction['debs']))
        for row in rows:
            if row['path'].startswith(('apt/pool/', 'apt/dists/')):
                self.assertEqual(row['url'], inputs.SNAPSHOT + '/' + row['path'][len('apt/'):])
        self.assertEqual({'apt/dists/%s/InRelease' % suite for suite in inputs.SUITES}, {p for p in paths if p.endswith('InRelease')})
        by_path = {row['path']: row for row in rows}
        for suite in inputs.SUITES:
            self.assertEqual(self.transaction['suites'][suite]['inReleaseSha256'], by_path['apt/dists/%s/InRelease' % suite]['sha256'])

    def test_inventory_is_the_signed_image_set_plus_the_noble_transaction(self):
        names = [row[0] for row in self.inventory]
        self.assertEqual(names, sorted(names, key=lambda n: n.encode()))
        self.assertEqual(self.transaction['finalInventory'], {'packages': len(names), 'sha256': digest(NOBLE / 'package-inventory.expected')})
        packages = dict(self.inventory)
        self.assertTrue(packages['openssl'].startswith(owner.OPENSSL_PACKAGE_PREFIX), 'noble keeps the OpenSSL-3 gate satisfied')
        self.assertIn('libicu74:amd64', packages)
        self.assertIn('docker.io', packages)
        self.assertEqual(packages['libc6:amd64'].split('-')[0], '2.39')
        for forbidden in ('libicu78:amd64', 'libssl4:amd64'):
            self.assertNotIn(forbidden, packages)
        requested = set(self.transaction['installRequest'])
        recipe = (ROOT / 'deployment/qualification-runner/noble-apt.sh').read_text()
        self.assertEqual(requested, set(recipe.split('runner_packages=(', 1)[1].split(')', 1)[0].split()))
        for deb in self.transaction['debs']:
            installed = packages.get(deb['package']) or packages.get(deb['package'] + ':amd64')
            self.assertEqual(installed, deb['version'], deb['package'])

    def test_signed_checksums_verify_and_pin_the_image_when_gpg_is_available(self):
        keyring = Path(os.environ.get('UBUNTU_KEYRING_DIR', '/usr/share/keyrings')) / 'ubuntu-cloudimage-keyring.gpg'
        if shutil.which('gpgv') is None or not keyring.exists():
            self.skipTest('gpgv or the Ubuntu cloud image keyring is unavailable')
        fingerprint, _ = inputs.verify_image_checksums(NOBLE / 'image/SHA256SUMS', NOBLE / 'image/SHA256SUMS.gpg', keyring, self.lock)
        self.assertEqual(fingerprint, inputs.CLOUDIMAGE_FINGERPRINT)
        tampered = dict(self.lock, image=dict(self.lock['image'], sha256='0' * 64))
        with self.assertRaises(SystemExit): inputs.verify_image_checksums(NOBLE / 'image/SHA256SUMS', NOBLE / 'image/SHA256SUMS.gpg', keyring, tampered)

    def test_archive_signature_check_refuses_an_altered_inrelease_when_gpg_is_available(self):
        keyring = Path(os.environ.get('UBUNTU_KEYRING_DIR', '/usr/share/keyrings')) / 'ubuntu-archive-keyring.gpg'
        if shutil.which('gpgv') is None or not keyring.exists():
            self.skipTest('gpgv or the Ubuntu archive keyring is unavailable')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'InRelease'
            path.write_text('Origin: Ubuntu\nNot signed\n')
            with self.assertRaises(SystemExit): inputs.gpgv(keyring, path)

    def test_compiler_identities_recompute_from_the_recorded_roles(self):
        for key, rows in self.transaction['compilerIdentity']['roles'].items():
            self.assertEqual(rows, sorted(rows), key)
            self.assertEqual(hashlib.sha256(('\n'.join(rows) + '\n').encode()).hexdigest(), self.lock['compiler'][key], key)
        for key, roles in inputs.COMPILER_ROLES.items():
            self.assertEqual(len(self.transaction['compilerIdentity']['roles'][key]), len(roles), key)

    def test_wrong_signer_is_refused_even_when_the_signature_is_valid(self):
        keyring = Path(os.environ.get('UBUNTU_KEYRING_DIR', '/usr/share/keyrings')) / 'ubuntu-archive-keyring.gpg'
        if shutil.which('gpgv') is None or not keyring.exists():
            self.skipTest('gpgv or the Ubuntu archive keyring is unavailable')
        # The image checksums are signed by the cloud-image key; the archive keyring cannot vouch for them.
        with self.assertRaises(SystemExit):
            inputs.verify_image_checksums(NOBLE / 'image/SHA256SUMS', NOBLE / 'image/SHA256SUMS.gpg', keyring, self.lock)

    def test_only_the_signed_text_of_an_inrelease_is_trusted(self):
        if shutil.which('gpg') is None or shutil.which('gpgv') is None:
            self.skipTest('gpg is unavailable')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); home = root / 'gnupg'; home.mkdir(mode=0o700)
            env = {**os.environ, 'GNUPGHOME': str(home)}
            def gpg(*arguments, **kw):
                return subprocess.run(['gpg', '--batch', '--no-tty', *arguments], env=env, capture_output=True, check=True, **kw)
            self.addCleanup(lambda: subprocess.run(['gpgconf', '--kill', 'all'], env=env, capture_output=True))
            gpg('--pinentry-mode', 'loopback', '--passphrase', '', '--quick-generate-key', 'test <test@example.invalid>', 'rsa2048', 'sign', '1d')
            ring = root / 'ring.gpg'
            ring.write_bytes(gpg('--export').stdout)
            body = root / 'body'
            body.write_text('Origin: Ubuntu\nSuite: noble\nSHA256:\n ' + 'a' * 64 + ' 10 main/binary-amd64/Packages.xz\n')
            signed = root / 'InRelease'
            gpg('--pinentry-mode', 'loopback', '--passphrase', '', '--clearsign', '--output', str(signed), str(body))
            fingerprint, _, rows = inputs.verified_release(ring, signed, 'noble')
            self.assertEqual(rows, {'main/binary-amd64/Packages.xz': ('a' * 64, 10)})
            # Unsigned bytes in front of the signed block still pass gpgv but must never be parsed as hashes.
            forged = root / 'Forged'
            forged.write_text('SHA256:\n ' + '0' * 64 + ' 10 main/binary-amd64/Packages.xz\n' + signed.read_text())
            _, _, rows = inputs.verified_release(ring, forged, 'noble')
            self.assertEqual(rows, {'main/binary-amd64/Packages.xz': ('a' * 64, 10)})
            with self.assertRaises(SystemExit): inputs.verified_release(ring, signed, 'noble-updates')
            tampered = root / 'Tampered'
            tampered.write_text(signed.read_text().replace('a' * 64, 'b' * 64))
            with self.assertRaises(SystemExit): inputs.verified_release(ring, tampered, 'noble')

    def test_acquire_validates_the_whole_lock_before_writing_anything(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for bad in ('../apt-transaction.json', '/etc/x', 'a/../../b', 'a\\b', ''):
                lock = json.loads(json.dumps(self.lock)); lock['files'][0] = {'path': bad, 'sha256': 'a' * 64, 'bytes': 1}
                (root / 'lock.json').write_text(json.dumps(lock))
                out = root / 'inner'
                with self.subTest(path=bad), self.assertRaises(SystemExit):
                    inputs.cmd_acquire(type('Args', (), {'lock': str(root / 'lock.json'), 'out': str(out), 'archive_keyring': None,
                                                         'cloudimage_keyring': None, 'image_out': None})())
                self.assertFalse(out.exists(), 'nothing is created before the lock validates')
                self.assertEqual(sorted(p.name for p in root.iterdir()), ['lock.json'])

    def test_simulation_refuses_outside_its_disposable_container(self):
        done = subprocess.run(['/bin/bash', str(ROOT / 'deployment/qualification-runner/simulate-noble-bake.sh')],
                              env={'PATH': '/usr/bin:/bin'}, capture_output=True, text=True, timeout=20)
        self.assertEqual(done.returncode, 2, done.stderr)
        self.assertIn('HELD', done.stderr)

    def test_bake_and_simulation_run_apt_with_the_one_locked_option_set(self):
        directory = ROOT / 'deployment/qualification-runner'
        digest = subprocess.run(['bash', '-c', 'runner_apt_sources=/dev/null; . "$1"; printf %s "$runner_apt_digest"; printf "\\n"; printf "%s\\n" "${runner_apt_fixed[@]}"',
                                 'x', str(directory / 'noble-apt.sh')], capture_output=True, text=True, check=True).stdout.split('\n', 1)
        locked = self.transaction['aptOptionsSha256']
        self.assertEqual(digest[0], locked, 'the locked transaction names the options the scripts really use')
        self.assertEqual(hashlib.sha256(digest[1].encode()).hexdigest(), locked)
        options = digest[1]
        # First bake failure: the _apt sandbox plus by-hash probing made apt fall back to index names that are not locked.
        for required in ('APT::Sandbox::User=root', 'Acquire::By-Hash=no', 'Acquire::CompressionTypes::Order::=xz', 'Acquire::Languages=none'):
            self.assertIn(required, options.splitlines())
        for name in ('provision-image.sh', 'simulate-noble-bake.sh'):
            text = (directory / name).read_text()
            self.assertIn('runner_apt_digest', text, name)
            self.assertIn('aptOptionsSha256', text, name)
            for mutation in ('runner_apt+=', 'runner_apt=(', 'runner_apt_fixed', 'APT::Sandbox', 'By-Hash', 'CompressionTypes'):
                self.assertNotIn(mutation, text, name + ' must not alter the shared apt options: ' + mutation)
        # The lock tool computes the same digest from the same file and verify refuses a transaction that names another.
        self.assertEqual(inputs.apt_options_digest(), locked)

    def test_bake_and_simulation_share_one_apt_definition_and_stay_offline_and_signed(self):
        recipe = (ROOT / 'deployment/qualification-runner/provision-image.sh').read_text()
        simulation = (ROOT / 'deployment/qualification-runner/simulate-noble-bake.sh').read_text()
        for text in (recipe, simulation):
            self.assertIn('noble-apt.sh', text)
            self.assertIn('${runner_packages[@]}', text)
        sources = (ROOT / 'deployment/qualification-runner/runner.sources.list').read_text()
        lines = [row for row in sources.splitlines() if row.startswith('deb ')]
        self.assertEqual([row.split()[3] for row in lines], list(inputs.SUITES))
        for row in lines:
            self.assertIn('signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg', row)
            self.assertIn('file:/mnt/fsgg-qualification-inputs/apt', row)
        self.assertNotIn('http', ''.join(lines).replace('file:', ''))
        self.assertIn('VERSION_ID == 24.04', recipe)
        self.assertNotIn('stonking', sources + recipe)


if __name__ == '__main__': unittest.main()
