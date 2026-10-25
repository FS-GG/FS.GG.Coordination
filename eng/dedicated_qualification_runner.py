#!/usr/bin/env python3
"""Closed dedicated-image input/identity DATA checks and guest readbacks. No registration or native-policy setter.

The existing BAR domain and actual published catalog remain determining native gates.
A parsed JSON target or checksum does not supply provider/operator authorization.
The legacy writer `write-binding` seals one per-boot instance binding after registration
data validation. Named ATv2 operations also seal a closed factory import closure and
capture a fixed user pool for the separately admitted disposable guest. They never install
software, dispatch, register or execute downloaded/controller code.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import selectors
import signal
import stat
import subprocess
import time

AT_ENTRY_STARTED=time.monotonic()
BASELINE_ID = 'ubuntu2404-linux-x64-v1'
IMAGE_URL_PATTERN = r'https://cloud-images\.ubuntu\.com/releases/noble/release-[0-9]{8}/ubuntu-24\.04-server-cloudimg-amd64\.img'
# Free plan: a repository-level JIT runner in the Default group of the private caller repository only.
GROUP = 'Default'
LABEL = 'fsgg-native-linux-x64-v1'
CALLER_REPOSITORY = 'FS-GG/FS.GG.Native.Qualification'
FOURD_REPOSITORY = 'FS-GG/FS.GG.FourD'
# Exactly two private repositories may own a dedicated JIT runner (GitHub Free: one runner, one repository). Each
# is bound to one allowed workflow (on main) and one job id; a sealed binding names exactly one row.
TARGETS = {
    CALLER_REPOSITORY: {'workflow': CALLER_REPOSITORY + '/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main',
                        'job': 'full-image'},
    FOURD_REPOSITORY: {'workflow': FOURD_REPOSITORY + '/.github/workflows/fourd-native-image-qualification.yml@refs/heads/main',
                       'job': 'native-qualification'},
}
AT_WORKLOAD = 'current-public-at'
AT_CONTROLLER_ROOT = '/opt/fsgg/current-at-controller'
AT_POOL_ROOT = '/var/lib/fs-gg-current-at-pools'
AT_FACTORY_PREFIX = 'tests/composition/fable-game/'
AT_FACTORY_FILES = ('current-reference-noble-factory.py','current-reference-noble-transaction.py',
    'current-reference-noble-acquisition.py','current-reference-native-input.py','external-reference-orca.py',
    'fixtures/noble-c068-status.xz','fixtures/noble-source-prefix-request.json')
AT_LABEL = 'fsgg-native-at-x11-v1'
AT_TARGET = {'repository': CALLER_REPOSITORY,
             'workflow': CALLER_REPOSITORY + '/.github/workflows/current-public-reference-at-qualification.yml@refs/heads/main',
             'job': 'current-public-at'}
AT_ROOT_REQUEST_SHA256 = '47782f4389f3451e843cd9dadff584c51f840511a085f76b063235a56f8098c9'
AT_INVENTORY_SHA256 = '9fdf6c23ce3e223e4aaefacde405ece9a523957f24acbb9cc8465223a506958f'
AT_RESOURCES = {'vcpu': 2, 'memoryBytes': 4 * 1024**3, 'aggregateWritableBytes': 8 * 1024**3,
                'installSeconds': 600, 'keymapSeconds': 120, 'cleanupSeconds': 15}
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



def validate_at_software(value):
    """Closed selected software inputs, not installation/profile/runtime acceptance."""
    require(type(value) is dict and set(value)=={'schema','platform','stage','templatesSource','frozenPlan',
            'rootRequestSha256','expectedFinalInventory','resources'},'closed AT software contract required')
    require(value['schema']=='fsgg.current-reference-noble-software/1' and value['platform']=='ubuntu-24.04-x11',
            'selected Noble X11 software contract required')
    require(value['stage'] in ('facility-prefix','full-current-public-reference') and sha(value['templatesSource'],40),
            'exact selected AT stage/controller source required')
    require(value['rootRequestSha256']==AT_ROOT_REQUEST_SHA256 and
            value['expectedFinalInventory']=={'packages':993,'sha256':AT_INVENTORY_SHA256} and
            type(value['expectedFinalInventory'].get('packages')) is int and value['resources']==AT_RESOURCES and
            all(type(item) is int for item in value['resources'].values()),
            'frozen selected AT inputs/resources differ')
    plan=value['frozenPlan']
    require(type(plan) is dict and set(plan)=={'repository','repositoryId','workflowPath','source','runId','runAttempt',
            'artifactId','artifactSha256','artifactBytes','transactionSha256'},'complete frozen source artifact descriptor required')
    require(plan['repository']=='FS-GG/FS.GG.Templates' and type(plan['repositoryId']) is int and plan['repositoryId']==1281961814 and
            plan['workflowPath']=='.github/workflows/fable-external-reference-source.yml' and sha(plan['source'],40),
            'frozen source producer identity differs')
    require(all(type(plan[key]) is int and plan[key]>0 for key in ['runId','artifactId']) and
            type(plan['runAttempt']) is int and plan['runAttempt']==1 and
            type(plan['artifactBytes']) is int and 0<plan['artifactBytes']<=256*1024**2 and
            sha(plan['artifactSha256']) and sha(plan['transactionSha256']),'frozen source artifact tuple absent')
    require(len(json.dumps(value,sort_keys=True,separators=(',',':')).encode())<=65536,'AT software contract exceeds64KiB')
    return value


def _registration_target(instance):
    if instance.get('schema')=='fsgg.dedicated-qualification-instance/2':
        require(instance.get('runnerRepository')==AT_TARGET['repository'] and instance.get('runnerWorkload')==AT_WORKLOAD,
                'v2 is only the named private AT target')
        software=validate_at_software(instance.get('atSoftwareContract'))
        require(sha(instance.get('atSoftwareContractSha256')) and
                hashlib.sha256(json.dumps(software,sort_keys=True,separators=(',',':')).encode()).hexdigest()==instance['atSoftwareContractSha256'],
                'sealed AT software body/digest differs')
        return AT_TARGET,AT_LABEL
    require(instance.get('schema')=='fsgg.dedicated-qualification-instance/1','instance binding required')
    require(not any(key in instance for key in ['runnerWorkload','atSoftwareContract','atSoftwareContractSha256']),
            'v2 AT fields on legacy binding refused')
    repository=instance.get('runnerRepository')
    target=TARGETS.get(repository) if isinstance(repository,str) else None
    require(target is not None,'runner repository must be one of the two private qualification targets')
    return target,LABEL


def validate_target_data(target):
    at=target.get('schema')=='fsgg.dedicated-qualification-target/2'
    require(target.get('schema') in ('fsgg.dedicated-qualification-target/1','fsgg.dedicated-qualification-target/2'),'target schema')
    if at:
        require(set(target)=={'schema','baselineId','provider','providerInstanceId','providerImageId','systemUuid',
                'operatorAcceptanceSha256','dedicated','disposable','sharedCodexHost','architecture','os','resources',
                'runnerWorkload','atLayerSource'},'closed named AT target required')
        require(set(target.get('resources',{}))=={'vcpu','memoryBytes','diskBytes'},'closed AT target resources required')
        require(target.get('runnerWorkload')==AT_WORKLOAD and sha(target.get('atLayerSource'),40),'AT-capable baked layer source required')
    else:
        require('runnerWorkload' not in target and 'atLayerSource' not in target,'AT fields on legacy target refused')
    require(target.get('baselineId') == BASELINE_ID, 'target baseline differs')
    for key in ('provider', 'providerInstanceId', 'providerImageId', 'systemUuid', 'operatorAcceptanceSha256'):
        require(isinstance(target.get(key), str) and target[key].strip() == target[key] and target[key], 'target missing ' + key)
    require(sha(target['operatorAcceptanceSha256']), 'operator acceptance digest')
    require(target.get('dedicated') is True and target.get('disposable') is True, 'dedicated disposable target required')
    require(target.get('sharedCodexHost') is False, 'shared Codex host forbidden')
    require(target.get('architecture') == 'x86_64', 'x64 VM required')
    require(target.get('os') == {'id': 'ubuntu', 'version': '24.04'}, 'actual Ubuntu24.04 noble carrier required')
    resources = target.get('resources', {})
    require(type(resources.get('vcpu')) is int and resources['vcpu'] >= (2 if at else 4), 'selected CPU target floor')
    require(type(resources.get('memoryBytes')) is int and resources['memoryBytes'] >= (4 if at else 16) * 1024**3, 'selected candidate sizing floor; native available-memory gate remains separate')
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
    target,label=_registration_target(instance)
    require(instance.get('baselineId') == BASELINE_ID and instance.get('oneJobOnly') is True, 'single-use baseline instance required')
    require(runner_name_ok(runner_name) and instance.get('runnerName') == runner_name, 'exact ephemeral fsgg-q- runner name required')
    require(instance.get('runnerGroup') == GROUP and instance.get('runnerLabel') == label, 'fixed Default-group route differs')
    require(instance.get('allowedWorkflow') == target['workflow'] and instance.get('allowedJob') == target['job'],
            'allowed workflow/job differ from the repository binding')
    require(instance.get('repositoryScopeVerified') is True, 'repository-level single-repository runner scope must be independently verified')
    for key in ('qualifiedImageSha256', 'rootInstanceAcceptanceSha256', 'registrationAdmissionSha256'):
        require(sha(instance.get(key)), 'instance/admission missing ' + key)
    for key in ('providerInstanceId', 'providerImageId', 'systemUuid'):
        require(isinstance(instance.get(key), str) and instance[key], 'instance missing ' + key)
    require(instance.get('credentialKind') == 'jit-config', 'one-job JIT configuration role required; no PAT or registration token')
    return True


REQUEST_FIELDS = ('baselineId', 'providerInstanceId', 'providerImageId', 'systemUuid', 'runnerName', 'runnerRepository', 'qualifiedImageSha256',
                  'rootInstanceAcceptanceSha256', 'registrationAdmissionSha256', 'repositoryScopeVerified')


def binding_from_request(request, lock, system_uuid):
    """Closed per-boot instance binding built only from the launcher's exact request and the actual machine UUID."""
    at=request.get('schema')=='fsgg.dedicated-qualification-binding-request/2'
    require(request.get('schema') in ('fsgg.dedicated-qualification-binding-request/1','fsgg.dedicated-qualification-binding-request/2'),'binding request schema')
    fields=REQUEST_FIELDS+(('runnerWorkload','atSoftwareContractSha256','atSoftwareContract') if at else ())
    require(set(request)==set(fields)|{'schema'},'binding request fields differ')
    require(isinstance(system_uuid, str) and system_uuid and request['systemUuid'] == system_uuid, 'actual machine UUID differs from the request')
    repository = request['runnerRepository']
    require(isinstance(repository, str) and repository in TARGETS, 'runner repository must be one of the two private qualification targets')
    selected=AT_TARGET if at else TARGETS[repository]
    if at:require(repository==CALLER_REPOSITORY and request['runnerWorkload']==AT_WORKLOAD,'AT request target differs')
    instance={'schema':'fsgg.dedicated-qualification-instance/'+('2' if at else '1'),'oneJobOnly':True,'runnerGroup':GROUP,
              'runnerLabel':AT_LABEL if at else LABEL,'credentialKind':'jit-config',
              'allowedWorkflow':selected['workflow'],'allowedJob':selected['job'],
              **{key:request[key] for key in fields}}
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


def factory_git_closure(checkout,revision,deadline):
    """Read/hash exact commit/tree/blob objects; never execute checkout code or fetch."""
    require(sha(revision,40),'exact selected factory source required')
    checkout=Path(checkout).resolve(strict=True);database=checkout/'.git'
    require(database.is_dir() and not database.is_symlink() and
            not (database/'objects/info/alternates').exists(),'ordinary isolated non-alternate Git source required')
    cache={};aggregate=0
    def object_bytes(kind,identity):
        nonlocal aggregate
        require(sha(identity,40),'Git object identity malformed')
        key=(kind,identity)
        if key not in cache:
            left=deadline-time.monotonic();require(left>0,'original factory seal deadline exhausted')
            env={'PATH':'/usr/bin:/bin','LANG':'C','LC_ALL':'C','GIT_CONFIG_NOSYSTEM':'1',
                 'GIT_CONFIG_GLOBAL':'/dev/null','GIT_NO_LAZY_FETCH':'1','GIT_TERMINAL_PROMPT':'0',
                 'GIT_ALLOW_PROTOCOL':'','GIT_PROTOCOL_FROM_USER':'0'}
            command=['/usr/bin/git','--no-replace-objects','--git-dir='+str(database),
                '-c','core.fsmonitor=false','-c','protocol.allow=never','cat-file',kind,identity]
            child=subprocess.Popen(command,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True)
            output={'stdout':bytearray(),'stderr':bytearray()}
            try:
                with selectors.DefaultSelector() as selector:
                    for name in output:selector.register(getattr(child,name),selectors.EVENT_READ,name)
                    while selector.get_map():
                        left=deadline-time.monotonic();require(left>0,'original Git object deadline exhausted')
                        for key,_ in selector.select(min(.1,left)):
                            body=os.read(key.fileobj.fileno(),65536)
                            if not body:selector.unregister(key.fileobj);continue
                            output[key.data].extend(body)
                            require(len(output[key.data])<=(1024**2 if key.data=='stdout' else 65536),
                                    'Git object output exceeded closed bound')
                require(child.wait(timeout=max(.001,deadline-time.monotonic()))==0,'selected Git object unavailable')
            except BaseException as cause:
                try:
                    if child.poll() is None:os.killpg(child.pid,signal.SIGKILL)
                    child.wait(timeout=1)
                except BaseException as secondary:cause.add_note('secondary original Git reader retirement: '+str(secondary))
                raise
            finally:
                child.stdout.close();child.stderr.close()
            body=bytes(output['stdout']);aggregate+=len(body)
            require(aggregate<=8*1024**2 and hashlib.sha1((kind+' '+str(len(body))+'\0').encode()+body).hexdigest()==identity,
                    'Git object body identity differs')
            require(time.monotonic()<deadline,'original factory seal object read completed late')
            cache[key]=body
        return cache[key]
    commit=object_bytes('commit',revision)
    match=re.match(rb'tree ([0-9a-f]{40})\n',commit);require(match is not None,'selected commit root absent')
    root=match[1].decode();result={}
    for path in AT_FACTORY_FILES:
        tree=root;parts=(AT_FACTORY_PREFIX+path).split('/')
        for index,part in enumerate(parts):
            raw=object_bytes('tree',tree);position=0;rows={}
            while position<len(raw):
                end=raw.index(b'\0',position);mode,name=raw[position:end].split(b' ',1)
                identity=raw[end+1:end+21];require(len(identity)==20 and name not in rows,'Git tree entry malformed/duplicate')
                rows[name]=(mode,identity.hex());position=end+21
                require(len(rows)<=10000,'Git tree population exceeded')
            require(position==len(raw) and part.encode() in rows,'closed factory path absent')
            mode,identity=rows[part.encode()]
            require(mode==(b'100644' if index==len(parts)-1 else b'40000'),'factory source mode/path differs')
            if index==len(parts)-1:result[path]=object_bytes('blob',identity)
            else:tree=identity
    return result


def at_factory_binding():
    """Actual separate ATv2 guest/binding; JSON is not Root/provider authority."""
    require(os.geteuid()==0 and Path(__file__).resolve()==Path(LAYER_ROOT)/'eng/dedicated_qualification_runner.py',
            'only the baked root driver may seal the factory')
    request=load(Path(STATE_DIRECTORY)/'instance-binding.json',131072)
    lock=load(Path(STATE_DIRECTORY)/'acquired-input-lock.json')
    selected,label=_registration_target(request)
    require(label==AT_LABEL,'only separate ATv2 may seal a factory')
    validate_registration_data(request,lock,os.environ.get('RUNNER_NAME'))
    require(all(os.environ.get(key)==value for key,value in {'GITHUB_REPOSITORY':selected['repository'],
        'GITHUB_WORKFLOW_REF':selected['workflow'],'GITHUB_JOB':selected['job'],'GITHUB_RUN_ATTEMPT':'1'}.items()),
        'actual selected private AT context differs')
    observed=verify_guest('/',LAYER_ROOT,'boot',system_uuid=Path('/sys/class/dmi/id/product_uuid').read_text().strip(),
                          runner_name=request['runnerName'])
    require(not observed['refusals'],'actual AT guest/layer refused')
    software=request['atSoftwareContract']
    # This source slice prepares the installed-input prefix only. Full AT remains a separate held route.
    require(software['stage']=='facility-prefix','full AT controller admission is not supplied by factory sealing')
    return request


def at_handoff_deadline():
    overall=float(os.environ['FSGG_AT_DEADLINE_MONOTONIC'])
    require(math.isfinite(overall) and AT_ENTRY_STARTED<overall<=AT_ENTRY_STARTED+2100,
            'finite original AT overall clock required')
    deadline=min(AT_ENTRY_STARTED+60,overall-15)
    require(deadline>time.monotonic(),'original factory handoff aperture absent')
    return deadline


def seal_at_controller(checkout):
    request=at_factory_binding();software=request['atSoftwareContract']
    deadline=at_handoff_deadline()
    bodies=factory_git_closure(checkout,software['templatesSource'],deadline)
    destination=Path(AT_CONTROLLER_ROOT)/software['templatesSource']
    parent=destination.parent
    if not parent.exists():parent.mkdir(mode=0o755)
    info=parent.lstat();require(stat.S_ISDIR(info.st_mode) and info.st_uid==0 and stat.S_IMODE(info.st_mode)==0o755,
                               'factory seal parent is not root immutable')
    require(not destination.exists() and not destination.is_symlink(),'fresh factory seal required; no replay')
    destination.mkdir(mode=0o755)
    for path,body in bodies.items():
        target=destination/AT_FACTORY_PREFIX/path;target.parent.mkdir(parents=True,exist_ok=True,mode=0o755)
        with target.open('xb') as stream:stream.write(body)
        target.chmod(0o644)
        require(time.monotonic()<deadline,'original factory seal persistence completed late')
    receipt={'schema':'fsgg.current-reference-factory-seal/1','source':software['templatesSource'],
             'files':{path:hashlib.sha256(body).hexdigest() for path,body in bodies.items()},
             'rootBindingSha256':hashlib.sha256((Path(STATE_DIRECTORY)/'instance-binding.json').read_bytes()).hexdigest()}
    output=destination/'factory-seal.json'
    with output.open('x') as stream:stream.write(json.dumps(receipt,sort_keys=True)+'\n')
    output.chmod(0o644);require(time.monotonic()<deadline,'original factory seal receipt persistence completed late')
    return {'controller':str(destination/AT_FACTORY_PREFIX/'current-reference-noble-factory.py'),
            'source':software['templatesSource'],'installation':False,'actualAT':False}


def capture_at_pool():
    """Capture one fixed user preparation domain through directory fds before root use."""
    request=at_factory_binding();software=request['atSoftwareContract']
    software_sha=request['atSoftwareContractSha256'];deadline=at_handoff_deadline()
    base=Path(os.environ['RUNNER_TEMP']);require(base.is_absolute() and '..' not in base.parts,'fixed runner temp required')
    source=base/'native-at-pool';rootfd=os.open('/',os.O_RDONLY|os.O_DIRECTORY)
    try:
        for part in source.parts[1:]:
            child=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=rootfd)
            os.close(rootfd);rootfd=child
        require(os.fstat(rootfd).st_uid==RUNNER_UID,'prepared input domain must belong to runner')
        total=0
        def read(relative,cap,expected=None,size=None):
            nonlocal total
            require(time.monotonic()<deadline,'original pool capture deadline exhausted')
            require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9+._/@~-]*',relative) is not None and
                    all(part not in ('','.','..') for part in relative.split('/')),'closed captured member path required')
            fd=os.dup(rootfd)
            try:
                parts=relative.split('/')
                for part in parts[:-1]:
                    child=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=fd);os.close(fd);fd=child
                    require(os.fstat(fd).st_uid==RUNNER_UID,'captured member ancestor owner differs')
                leaf=os.open(parts[-1],os.O_RDONLY|os.O_NONBLOCK|os.O_NOFOLLOW,dir_fd=fd)
                with os.fdopen(leaf,'rb') as stream:
                    before=os.fstat(stream.fileno())
                    require(stat.S_ISREG(before.st_mode) and before.st_uid==RUNNER_UID and before.st_nlink==1 and
                            not before.st_mode&0o022 and 0<before.st_size<=cap,'bounded regular single-owner input required')
                    body=stream.read(cap+1);after=os.fstat(stream.fileno())
                require(len(body)<=cap and all(getattr(before,key)==getattr(after,key) for key in
                    ('st_dev','st_ino','st_size','st_mtime_ns','st_ctime_ns')) and len(body)==after.st_size,
                    'captured member changed during original read')
                total+=len(body);require(total<=768*1024**2,'aggregate captured pool exceeded')
                require(expected is None or (hashlib.sha256(body).hexdigest()==expected and len(body)==size),'captured archive body differs')
                require(time.monotonic()<deadline,'original pool capture read completed late')
                return body
            finally:os.close(fd)
        receipt_raw=read('frozen-inputs.json',1024**2)
        def unique(rows):
            value={}
            for key,item in rows:require(key not in value,'duplicate pool receipt property');value[key]=item
            return value
        receipt=json.loads(receipt_raw,object_pairs_hook=unique)
        require(receipt.get('schema')=='fsgg.current-reference-noble-frozen-inputs/1' and
                receipt.get('originalTransactionSha256')==software['frozenPlan']['transactionSha256'] and
                receipt.get('rootRequestSha256')==software['rootRequestSha256'],'selected pool receipt differs')
        rows=receipt.get('archives');require(type(rows) is list and 1<=len(rows)<=2048,'bounded archive population required')
        members={'frozen-inputs.json':(receipt_raw,None,None)}
        archive_total=0
        for row in rows:
            require(type(row) is dict and type(row.get('bytes')) is int and 0<row['bytes']<=128*1024**2 and
                    sha(row.get('sha256')) and type(row.get('path')) is str,'closed archive identity required')
            name='archives/'+row['path'];require(name not in members,'duplicate captured archive')
            members[name]=(None,row['sha256'],row['bytes']);archive_total+=row['bytes']
        require(archive_total<=512*1024**2,'unchanged archive population bound exceeded')
        for suite in ('noble','noble-updates','noble-security'):
            members['metadata/'+suite+'.InRelease']=(None,None,None)
            for component in ('main','universe'):
                for kind in ('binary-amd64-Packages.xz','source-Sources.xz'):
                    members['metadata/'+suite+'-'+component+'-'+kind]=(None,None,None)
        for name in ('transaction.json','apt-config.log','apt-update.log','apt-simulate.log','status'):
            members['originals/'+name]=(None,software['frozenPlan']['transactionSha256'] if name=='transaction.json' else None,None)
        parent=Path(AT_POOL_ROOT)
        if not parent.exists():parent.mkdir(mode=0o755)
        info=parent.lstat();require(stat.S_ISDIR(info.st_mode) and info.st_uid==0 and stat.S_IMODE(info.st_mode)==0o755,
                                   'root pool parent is not immutable')
        destination=parent/software_sha
        require(not destination.exists() and not destination.is_symlink(),'fresh root pool required; no replay')
        destination.mkdir(mode=0o700);hashes={}
        for name,(body,expected,size) in members.items():
            if body is None:
                body=read(name,128*1024**2,expected if size is not None else None,size)
                if expected is not None and size is None:require(hashlib.sha256(body).hexdigest()==expected,'selected original transaction changed')
            target=destination/name;target.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
            with target.open('xb') as stream:stream.write(body)
            target.chmod(0o600);hashes[name]=hashlib.sha256(body).hexdigest()
            require(time.monotonic()<deadline,'original pool capture persistence completed late')
        seal={'schema':'fsgg.current-reference-pool-seal/1','softwareContractSha256':software_sha,'files':hashes,
              'rootBindingSha256':hashlib.sha256((Path(STATE_DIRECTORY)/'instance-binding.json').read_bytes()).hexdigest()}
        target=destination/'pool-seal.json'
        with target.open('x') as stream:stream.write(json.dumps(seal,sort_keys=True)+'\n')
        target.chmod(0o600);require(time.monotonic()<deadline,'original pool seal persistence completed late')
        # Publish only the completed root-owned closure. Failed partial capture stays private.
        for path in destination.rglob('*'):path.chmod(0o755 if path.is_dir() else 0o644)
        destination.chmod(0o755);require(time.monotonic()<deadline,'original pool publication completed late')
        return {'pool':str(destination),'softwareContractSha256':software_sha,'installation':False,'actualAT':False}
    finally:os.close(rootfd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['validate-inputs', 'verify-image-target', 'verify-registration', 'write-binding', 'verify-guest','seal-at-controller','capture-at-pool'])
    parser.add_argument('--target')
    parser.add_argument('--checkout')
    parser.add_argument('--lock')
    parser.add_argument('--inputs')
    parser.add_argument('--approval')
    parser.add_argument('--system-uuid')
    parser.add_argument('--runner-name')
    parser.add_argument('--request')
    parser.add_argument('--phase', choices=['image', 'boot'])
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    if args.operation=='capture-at-pool':
        print(json.dumps(capture_at_pool(),sort_keys=True));return
    if args.operation=='seal-at-controller':
        require(args.checkout,'explicit selected Templates checkout required')
        print(json.dumps(seal_at_controller(args.checkout),sort_keys=True));return
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
