#!/usr/bin/env python3
"""Closed dedicated-image input/identity DATA checks and guest readbacks. No registration or native-policy setter.

The existing BAR domain and actual published catalog remain determining native gates.
A parsed JSON target or checksum does not supply provider/operator authorization.
The only writer is `write-binding`, which seals one per-boot instance binding after the
same validation that registration uses; it never starts, registers or provisions anything.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat

BASELINE_ID = 'ubuntu2404-linux-x64-v1'
IMAGE_URL_PATTERN = r'https://cloud-images\.ubuntu\.com/releases/noble/release-[0-9]{8}/ubuntu-24\.04-server-cloudimg-amd64\.img'
# Free plan: a repository-level JIT runner in the Default group of the private caller repository only.
GROUP = 'Default'
LABEL = 'fsgg-native-linux-x64-v1'
CALLER_REPOSITORY = 'FS-GG/FS.GG.Native.Qualification'
RUNNER_NAME_PATTERN = r'fsgg-q-[A-Za-z0-9][A-Za-z0-9-]{0,63}'
RUNNER_UID = 1001
STATE_DIRECTORY = '/etc/fsgg-qualification-runner'
RUNNER_ROOT = '/opt/fsgg/actions-runner'
DOTNET_ROOT = '/opt/fsgg/toolchains/dotnet-10.0.401'
GO_ROOT = '/opt/fsgg/toolchains/go-1.27.1'
SYSCTL_DROPIN = '/etc/sysctl.d/90-fsgg-qualification.conf'
SUDOERS_FILE = '/etc/sudoers.d/fsgg-qualification-runner'
LAYER_ROOT = '/opt/fsgg/qualification-layer'
LAYER_FILES = ('eng/dedicated_qualification_runner.py', 'deployment/qualification-runner/90-fsgg-qualification.conf',
               'deployment/qualification-runner/runner.sudoers', 'deployment/qualification-runner/guest-boot.sh',
               'deployment/qualification-runner/run-jit-runner.sh')
USERNS_KNOB = '/proc/sys/kernel/apparmor_restrict_unprivileged_userns'
# bar_hosted_operation.crypto_identity accepts only this OpenSSL family for the encrypted candidate.
OPENSSL_FAMILY_PREFIX = 'OpenSSL 3.'
OPENSSL_PACKAGE_PREFIX = '3.'
SDK_SHA512 = '51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b'


def require(condition, reason):
    if not condition:
        raise ValueError('dedicated-runner: ' + reason)


def sha(value, length=64):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{' + str(length) + '}', value) is not None


def load(path, maximum=4 * 1024**2):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= maximum, 'bounded regular JSON required')
        raw = stream.read(maximum + 1)
    require(len(raw) <= maximum, 'JSON cap exceeded')
    def unique(rows):
        result = {}
        for key, value in rows:
            require(key not in result, 'duplicate JSON property')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def validate_target_data(target):
    require(target.get('schema') == 'fsgg.dedicated-qualification-target/1', 'target schema')
    require(target.get('baselineId') == BASELINE_ID, 'target baseline differs')
    for key in ('provider', 'providerInstanceId', 'providerImageId', 'systemUuid', 'operatorAcceptanceSha256'):
        require(isinstance(target.get(key), str) and target[key].strip() == target[key] and target[key], 'target missing ' + key)
    require(sha(target['operatorAcceptanceSha256']), 'operator acceptance digest')
    require(target.get('dedicated') is True and target.get('disposable') is True, 'dedicated disposable target required')
    require(target.get('sharedCodexHost') is False, 'shared Codex host forbidden')
    require(target.get('architecture') == 'x86_64', 'x64 VM required')
    require(target.get('os') == {'id': 'ubuntu', 'version': '24.04'}, 'actual Ubuntu24.04 noble carrier required')
    resources = target.get('resources', {})
    require(type(resources.get('vcpu')) is int and resources['vcpu'] >= 4, 'four CPU target floor')
    require(type(resources.get('memoryBytes')) is int and resources['memoryBytes'] >= 16 * 1024**3, '16GiB candidate sizing floor; native available-memory gate remains separate')
    require(type(resources.get('diskBytes')) is int and resources['diskBytes'] >= 256 * 1024**3, '256GiB target floor')
    return target


def validate_lock_data(lock):
    require(lock.get('schema') == 'fsgg.dedicated-qualification-acquired-inputs/1', 'acquired input lock required')
    require(lock.get('baselineId') == BASELINE_ID, 'input baseline differs')
    image = lock.get('image', {})
    require(isinstance(image.get('url'), str) and re.fullmatch(IMAGE_URL_PATTERN, image['url']) is not None, 'official immutable Ubuntu 24.04 release image origin')
    require(sha(image.get('sha256')) and sha(image.get('signedChecksumSha256')), 'exact image/checksum pins required')
    require(sha(image.get('qualifiedAcquisitionReceiptSha256')), 'qualified signed image acquisition receipt required')
    require(isinstance(image.get('release'), str) and re.fullmatch(r'release-[0-9]{8}', image['release']) is not None
            and '/' + image['release'] + '/' in image['url'], 'immutable image release identity required')
    require(lock.get('sdk', {}).get('archiveSha512') == SDK_SHA512, 'SDK401 input bytes differ')
    require(lock.get('sdk', {}).get('version') == '10.0.401' and lock['sdk'].get('runtime') == '10.0.12', 'SDK/runtime tuple differs')
    runner = lock.get('runner', {})
    require(re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', runner.get('version', '')) is not None, 'exact runner version required')
    require(sha(runner.get('sha256')) and sha(runner.get('releaseMetadataSha256')), 'runner archive/release pins required')
    require(runner.get('url') == 'https://github.com/actions/runner/releases/download/v' + runner['version'] + '/actions-runner-linux-x64-' + runner['version'] + '.tar.gz', 'official fixed runner archive origin')
    require(lock.get('goRace', {}).get('version') == '1.27.1' and sha(lock['goRace'].get('archiveSha256')), 'Go1.27.1 race toolchain required')
    require(lock['goRace'].get('url') == 'https://go.dev/dl/go1.27.1.linux-amd64.tar.gz', 'official fixed Go archive origin')
    compiler = lock.get('compiler', {})
    require(compiler.get('raceRequired') is True and compiler.get('cgoEnabled') is True, 'real CGO/race required; no skip')
    for key in ('compilerSha256', 'headersManifestSha256', 'linkerSha256', 'runtimeClosureSha256'):
        require(sha(compiler.get(key)), 'compiler closure missing ' + key)
    require(sha(lock.get('aptTransaction', {}).get('qualifiedSignedTransactionSha256')), 'complete signed APT transaction required')
    require(sha(lock['aptTransaction'].get('expectedFinalInventorySha256')), 'complete final package inventory required')
    policy = lock.get('policy', {})
    require(type(policy.get('apparmorRestrictUnprivilegedUserns')) is int and policy['apparmorRestrictUnprivilegedUserns'] == 0, 'actual AppArmor policy0 required')
    require(policy.get('nativeWorkloadNoNewPrivileges') is True and policy.get('noJobPolicySetter') is True, 'native NNP/job-policy contract differs')
    return lock


def validate_population(lock, directory):
    root = Path(directory)
    require(root.is_absolute() and root.is_dir() and not root.is_symlink(), 'absolute regular input directory')
    rows = lock.get('files')
    require(isinstance(rows, list) and 1 <= len(rows) <= 20000, 'complete bounded input file inventory required')
    expected = []
    for row in rows:
        name = row.get('path')
        require(isinstance(name, str) and name and '\\' not in name, 'unsafe input path')
        path = PurePosixPath(name)
        require(not path.is_absolute() and all(part not in ('', '.', '..') for part in name.split('/')), 'unsafe input path')
        require(sha(row.get('sha256')) and type(row.get('bytes')) is int and 0 <= row['bytes'] <= 4 * 1024**3, 'file pin differs')
        expected.append(name)
    require(expected == sorted(set(expected)), 'ordered unique input population required')
    actual = []
    for path in root.rglob('*'):
        require(not path.is_symlink(), 'linked acquired input refused')
        if path.is_dir():
            continue
        require(stat.S_ISREG(path.stat().st_mode), 'special acquired input refused')
        actual.append(str(path.relative_to(root)))
    require(sorted(actual) == expected, 'acquired input population missing/extra')
    for row in rows:
        h = hashlib.sha256(); count = 0
        with (root / row['path']).open('rb') as stream:
            while True:
                data = stream.read(1024**2)
                if not data:
                    break
                count += len(data); require(count <= row['bytes'], 'input grew while hashing'); h.update(data)
        require(count == row['bytes'] and h.hexdigest() == row['sha256'], 'input hash/size differs: ' + row['path'])
    return {'files': len(rows), 'bytes': sum(row['bytes'] for row in rows), 'classification': 'input DATA checked; no invocation grant'}



def validate_tool_archives(lock, directory):
    root = Path(directory)
    selected = [
        ('sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz', 'sha512', SDK_SHA512),
        ('runner/actions-runner-linux-x64-' + lock['runner']['version'] + '.tar.gz', 'sha256', lock['runner']['sha256']),
        ('go/go1.27.1.linux-amd64.tar.gz', 'sha256', lock['goRace']['archiveSha256']),
        ('apt/package-inventory.expected', 'sha256', lock['aptTransaction']['expectedFinalInventorySha256'])]
    for name, algorithm, expected in selected:
        h = hashlib.new(algorithm)
        with (root / name).open('rb') as stream:
            while True:
                block = stream.read(1024**2)
                if not block:
                    break
                h.update(block)
        require(h.hexdigest() == expected, 'selected tool/inventory bytes differ: ' + name)
    return True


def validate_instance_match(target, approval, system_uuid):
    validate_target_data(target)
    require(approval.get('schema') == 'fsgg.dedicated-image-build-selection/1', 'external operator build selection required')
    require(approval.get('providerInstanceId') == target['providerInstanceId'], 'provider instance mismatch')
    require(approval.get('systemUuid') == system_uuid == target['systemUuid'], 'actual machine UUID mismatch')
    require(approval.get('baselineId') == BASELINE_ID and approval.get('operation') == 'build-dedicated-image', 'fixed image operation only')
    require(sha(approval.get('targetSha256')), 'exact selected target bytes required')
    return True



def runner_name_ok(name):
    return isinstance(name, str) and re.fullmatch(RUNNER_NAME_PATTERN, name) is not None


def validate_registration_data(instance, lock, runner_name):
    validate_lock_data(lock)
    require(instance.get('schema') == 'fsgg.dedicated-qualification-instance/1', 'instance binding required')
    require(instance.get('baselineId') == BASELINE_ID and instance.get('oneJobOnly') is True, 'single-use baseline instance required')
    require(runner_name_ok(runner_name) and instance.get('runnerName') == runner_name, 'exact ephemeral fsgg-q- runner name required')
    require(instance.get('runnerGroup') == GROUP and instance.get('runnerLabel') == LABEL, 'fixed Default-group route differs')
    require(instance.get('runnerRepository') == CALLER_REPOSITORY, 'private caller repository binding differs')
    require(instance.get('repositoryScopeVerified') is True, 'repository-level single-repository runner scope must be independently verified')
    for key in ('qualifiedImageSha256', 'rootInstanceAcceptanceSha256', 'registrationAdmissionSha256'):
        require(sha(instance.get(key)), 'instance/admission missing ' + key)
    for key in ('providerInstanceId', 'providerImageId', 'systemUuid'):
        require(isinstance(instance.get(key), str) and instance[key], 'instance missing ' + key)
    require(instance.get('credentialKind') == 'jit-config', 'one-job JIT configuration role required; no PAT or registration token')
    return True


REQUEST_FIELDS = ('baselineId', 'providerInstanceId', 'providerImageId', 'systemUuid', 'runnerName', 'qualifiedImageSha256',
                  'rootInstanceAcceptanceSha256', 'registrationAdmissionSha256', 'repositoryScopeVerified')


def binding_from_request(request, lock, system_uuid):
    """Closed per-boot instance binding built only from the launcher's exact request and the actual machine UUID."""
    require(request.get('schema') == 'fsgg.dedicated-qualification-binding-request/1', 'binding request schema')
    require(set(request) == set(REQUEST_FIELDS) | {'schema'}, 'binding request fields differ')
    require(isinstance(system_uuid, str) and system_uuid and request['systemUuid'] == system_uuid, 'actual machine UUID differs from the request')
    instance = {'schema': 'fsgg.dedicated-qualification-instance/1', 'oneJobOnly': True, 'runnerGroup': GROUP,
                'runnerLabel': LABEL, 'runnerRepository': CALLER_REPOSITORY, 'credentialKind': 'jit-config',
                **{key: request[key] for key in REQUEST_FIELDS}}
    validate_registration_data(instance, lock, instance['runnerName'])
    return instance


def write_binding(instance, directory, *, owner_uid=0):
    """Create the sealed binding once, root-owned 0644. An existing binding is never replaced."""
    root = Path(directory)
    require(root.is_dir() and not root.is_symlink(), 'binding directory required')
    info = root.lstat()
    require(info.st_uid == owner_uid and not info.st_mode & 0o022, 'binding directory must be owner-only writable')
    path = root / 'instance-binding.json'
    data = (json.dumps(instance, sort_keys=True, indent=2) + '\n').encode()
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o644)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(path, 0o644)
    return hashlib.sha256(data).hexdigest()


def _lstat(path):
    try:
        return os.lstat(path)
    except OSError:
        return None


def _sealed(path, uid, modes, refusals, label):
    info = _lstat(path)
    if info is None or not stat.S_ISREG(info.st_mode):
        refusals.append(label + '-missing')
        return None
    if info.st_uid != uid or stat.S_IMODE(info.st_mode) not in modes:
        refusals.append(label + '-owner-or-mode')
    return path.read_bytes()


def _package_versions(root, names):
    found = {}
    try:
        text = (root / 'var/lib/dpkg/status').read_text(errors='replace')
    except OSError:
        return found
    for block in text.split('\n\n'):
        fields = dict(line.split(': ', 1) for line in block.splitlines() if ': ' in line and not line.startswith(' '))
        if fields.get('Package') in names and fields.get('Status', '').endswith(' installed'):
            found[fields['Package']] = fields.get('Version', '')
    return found


def verify_guest(root, source, phase, *, system_uuid=None, runner_name=None, owner_uid=0, runner_uid=RUNNER_UID, machine=None):
    """Read-only readback of the qualified-image layer on a guest rooted at `root`.

    `phase` is `image` (baked, no per-boot binding may exist) or `boot` (one valid binding is required).
    Nothing is executed from the guest and no policy is written. Returns refusals and observed facts.
    """
    root, source = Path(root), Path(source)
    refusals, facts = [], {}
    def under(name):
        return root / name.lstrip('/')
    require(phase in ('image', 'boot'), 'closed phase required')
    require(phase == 'image' or (runner_name_ok(runner_name) and system_uuid), 'boot phase needs the runner name and actual UUID')
    release = {}
    try:
        for line in under('/etc/os-release').read_text().splitlines():
            if '=' in line:
                key, value = line.split('=', 1)
                release[key] = value.strip('"')
    except OSError:
        pass
    if (release.get('ID'), release.get('VERSION_ID')) != ('ubuntu', '24.04'):
        refusals.append('os-not-ubuntu-24.04')
    if (machine or os.uname().machine) != 'x86_64':
        refusals.append('architecture-not-x86_64')
    # AppArmor userns policy: the image drop-in is the only writer, the live value must read zero.
    dropin = _sealed(under(SYSCTL_DROPIN), owner_uid, (0o644,), refusals, 'sysctl-dropin')
    if dropin is not None and dropin != (source / 'deployment/qualification-runner/90-fsgg-qualification.conf').read_bytes():
        refusals.append('sysctl-dropin-bytes')
    try:
        facts['apparmorRestrictUnprivilegedUserns'] = under(USERNS_KNOB).read_text().strip()
    except OSError:
        facts['apparmorRestrictUnprivilegedUserns'] = None
    if facts['apparmorRestrictUnprivilegedUserns'] != '0':
        refusals.append('apparmor-userns-not-zero')
    # Runner identity: fixed UID/GID 1001, home and path.
    passwd = [line.split(':') for line in (under('/etc/passwd').read_text().splitlines() if _lstat(under('/etc/passwd')) else [])]
    account = [row for row in passwd if row and row[0] == 'runner']
    if len(account) != 1 or account[0][2:4] != [str(RUNNER_UID), str(RUNNER_UID)] or account[0][5] != '/home/runner':
        refusals.append('runner-uid-1001-account')
    group = [line.split(':') for line in (under('/etc/group').read_text().splitlines() if _lstat(under('/etc/group')) else [])]
    if [row[2] for row in group if row and row[0] == 'runner'] != [str(RUNNER_UID)]:
        refusals.append('runner-gid-1001-group')
    sudoers = _sealed(under(SUDOERS_FILE), owner_uid, (0o440,), refusals, 'sudoers')
    if sudoers is not None and sudoers != (source / 'deployment/qualification-runner/runner.sudoers').read_bytes():
        refusals.append('sudoers-bytes')
    lock_bytes = _sealed(under(STATE_DIRECTORY + '/acquired-input-lock.json'), owner_uid, (0o644,), refusals, 'acquired-input-lock')
    lock = None
    if lock_bytes is not None:
        try:
            lock = json.loads(lock_bytes)
            validate_lock_data(lock)
        except (ValueError, TypeError, AttributeError) as error:
            refusals.append('acquired-input-lock-data: ' + str(error)[:120])
            lock = None
    # Baked runner: fixed path, owned by 1001, fresh (never configured), version equals the lock.
    runner = under(RUNNER_ROOT)
    info = _lstat(runner)
    if info is None or not stat.S_ISDIR(info.st_mode) or info.st_uid != runner_uid:
        refusals.append('runner-root-missing-or-owner')
    for name in ('run.sh', 'config.sh', 'bin/Runner.Listener'):
        if _lstat(runner / name) is None:
            refusals.append('runner-file-missing:' + name)
    for name in ('.runner', '.credentials', '.credentials_rsaparams'):
        if _lstat(runner / name) is not None:
            refusals.append('runner-already-configured:' + name)
    # The release tarball has no version file; the listener's deps manifest names its own assembly version.
    try:
        libraries = json.loads((runner / 'bin/Runner.Listener.deps.json').read_text()).get('libraries', {})
        versions = sorted(name.split('/', 1)[1] for name in libraries if name.startswith('Runner.Listener/'))
        facts['runnerVersion'] = versions[0] if len(versions) == 1 else None
    except (OSError, ValueError, AttributeError):
        facts['runnerVersion'] = None
    if lock is not None and facts['runnerVersion'] != lock['runner']['version']:
        refusals.append('runner-version-differs-from-lock')
    # Toolchains: exact fixed SDK and Go identities; nothing is executed here.
    dotnet = under(DOTNET_ROOT)
    sdk_versions = sorted(entry.name for entry in (dotnet / 'sdk').iterdir()) if (dotnet / 'sdk').is_dir() else []
    if _lstat(dotnet / 'dotnet') is None or sdk_versions != ['10.0.401']:
        refusals.append('dotnet-sdk-10.0.401')
    if not (dotnet / 'shared/Microsoft.NETCore.App/10.0.12').is_dir():
        refusals.append('dotnet-runtime-10.0.12')
    try:
        go_version = (under(GO_ROOT) / 'VERSION').read_text().splitlines()[0]
    except (OSError, IndexError):
        go_version = None
    facts['goVersion'] = go_version
    if go_version != 'go1.27.1' or _lstat(under(GO_ROOT) / 'bin/go') is None:
        refusals.append('go-1.27.1')
    # Buildah 1.43.1 is deliberately not a guest-host package: the job builds the pinned Arch userland.
    facts['buildah'] = 'per-job pinned Arch userland (1.43.1, sha256-pinned in the workflow); not a guest-host package'
    for tool in ('docker', 'systemd-run', 'unshare', 'losetup', 'mkfs.ext4', 'fallocate', 'setpriv', 'openssl', 'newuidmap', 'newgidmap'):
        if not any(_lstat(under(directory + '/' + tool)) is not None for directory in ('/usr/bin', '/usr/sbin', '/bin', '/sbin')):
            refusals.append('tool-missing:' + tool)
    packages = _package_versions(root, {'openssl', 'docker.io', 'apparmor'})
    facts['packages'] = packages
    if not packages.get('openssl', '').startswith(OPENSSL_PACKAGE_PREFIX):
        refusals.append('openssl-family-not-3')
    # The installed layer is exactly its reviewed manifest: required files present, hashes equal, nothing extra.
    layer = under(LAYER_ROOT)
    manifest = _sealed(layer / 'MANIFEST.sha256', owner_uid, (0o644,), refusals, 'layer-manifest')
    if manifest is not None:
        listed = {}
        for line in manifest.decode(errors='replace').splitlines():
            digest, _, name = line.partition('  ')
            listed[name[2:] if name.startswith('./') else name] = digest
        if set(LAYER_FILES) - set(listed):
            refusals.append('layer-file-not-listed')
        for name, digest in listed.items():
            path = layer / name
            info = _lstat(path)
            if info is None or not stat.S_ISREG(info.st_mode) or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                refusals.append('layer-file-differs:' + name)
        present = {str(path.relative_to(layer)) for path in layer.rglob('*') if path.is_file() or path.is_symlink()} - {'MANIFEST.sha256'}
        if present - set(listed):
            refusals.append('layer-file-unlisted')
    # Per-boot binding.
    binding = under(STATE_DIRECTORY + '/instance-binding.json')
    if phase == 'image':
        if _lstat(binding) is not None:
            refusals.append('image-contains-instance-binding')
    else:
        data = _sealed(binding, owner_uid, (0o644,), refusals, 'instance-binding')
        if data is not None and lock is not None:
            try:
                value = json.loads(data)
                validate_registration_data(value, lock, runner_name)
                if value['systemUuid'] != system_uuid:
                    refusals.append('binding-uuid-differs')
            except (ValueError, TypeError, AttributeError) as error:
                refusals.append('instance-binding-data: ' + str(error)[:120])
    return {'phase': phase, 'refusals': refusals, 'facts': facts, 'authorization': 'readback only; not provider or Root authority'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['validate-inputs', 'verify-image-target', 'verify-registration', 'write-binding', 'verify-guest'])
    parser.add_argument('--target')
    parser.add_argument('--lock')
    parser.add_argument('--inputs')
    parser.add_argument('--approval')
    parser.add_argument('--system-uuid')
    parser.add_argument('--runner-name')
    parser.add_argument('--request')
    parser.add_argument('--phase', choices=['image', 'boot'])
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    if args.operation == 'write-binding':
        require(args.request and args.lock and args.system_uuid, 'binding request/lock/actual UUID missing')
        instance = binding_from_request(load(args.request), load(args.lock), args.system_uuid)
        digest = write_binding(instance, STATE_DIRECTORY)
        print(json.dumps({'bindingWritten': True, 'bindingSha256': digest, 'authorization': 'external Root/provider/group owner only'}))
        return
    if args.operation == 'verify-guest':
        require(args.phase, 'phase missing')
        result = verify_guest('/', source, args.phase, system_uuid=args.system_uuid, runner_name=args.runner_name)
        print(json.dumps(result, sort_keys=True))
        raise SystemExit(1 if result['refusals'] else 0)
    require(args.target, 'target missing')
    target = load(args.target)
    if args.operation == 'verify-registration':
        require(args.lock and args.runner_name, 'registration lock/name missing')
        validate_registration_data(target, load(args.lock), args.runner_name)
        require(args.system_uuid and target['systemUuid'] == args.system_uuid, 'registration actual VM UUID differs')
        print(json.dumps({'registrationDataChecked': True, 'authorization': 'external Root/provider/group owner only'}))
        return
    validate_target_data(target)
    if args.operation == 'validate-inputs':
        require(args.lock and args.inputs, 'lock/inputs missing')
        lock = load(args.lock); validate_lock_data(lock)
        population = validate_population(lock, args.inputs)
        validate_tool_archives(lock, args.inputs)
        print(json.dumps(population, sort_keys=True))
    else:
        require(args.approval and args.system_uuid, 'operator selection/actual UUID missing')
        approval = load(args.approval)
        require(hashlib.sha256(Path(args.target).read_bytes()).hexdigest() == approval.get('targetSha256'), 'selected target body differs')
        validate_instance_match(target, approval, args.system_uuid)
        print(json.dumps({'targetDataMatches': True, 'providerAttestation': 'external operator responsibility; not authenticated by JSON'}))


if __name__ == '__main__':
    main()
