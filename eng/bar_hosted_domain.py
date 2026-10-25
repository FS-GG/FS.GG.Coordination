#!/usr/bin/env python3
"""Hosted-VM resource-domain evidence for the closed BAR image profile.

FS-GG/.github#4778 route (b) gives each BAR image operation one fresh
GitHub-hosted VM. This standard-library helper has two fixed modes:

``admit``
    Runs as root on the VM after provisioning. It observes the runner image,
    stopped container daemons, the rootless user-namespace setting, the single
    preallocated operation volume, the userland builder version and host
    capacity, then writes ``fsgg.bar-hosted-domain/1`` evidence. It refuses
    (exit 2) when any decision refusal control fails.

``userland-check``
    Runs inside the pinned Arch userland as the unprivileged runner, within the
    delegated systemd unit and its own cgroup namespace. It uses the maintained
    controller's own ``host_resources`` observation and exercises rootless
    Buildah/Podman with no image pull or product content.

Neither mode reads BAR content, builds an image, or grants an operation.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import stat
import sys

SCHEMA = 'fsgg.bar-hosted-domain/1'
CHECK_SCHEMA = 'fsgg.bar-hosted-userland-check/1'
RELATION = 'dedicated-ephemeral-hosted-vm'
RUNNER_IMAGE = 'ubuntu24'
DEDICATED_RELATION = 'dedicated-preconfigured-ephemeral-vm'
# Explicitly selected image version (Atlas learn/hosted-image-candidate-
# qualification-boundary): image drift refuses until source selects it.
RUNNER_IMAGE_VERSION = '20261012.334.1'
# fstrim.timer would punch holes through the loop device into the backing file.
STOPPED_SERVICES = ('containerd.service', 'docker.service', 'docker.socket', 'fstrim.timer')
DAEMONS = ('dockerd', 'containerd', 'containerd-shim')
DISK_BYTES = 80 * 1024**3
MEMORY_BYTES = 14 * 1024**3
CPUS = 4
BUILDER_VERSION = '1.43.1'
IMAGE_USER = 10001
COMMAND_SECONDS = 120
CAPTURE_BYTES = 64 * 1024


def canonical(value) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False) + '\n').encode()


def command(argv, *, check=False, timeout_seconds=COMMAND_SECONDS):
    """Bounded fixed command; returns (exit, stdout, stderr) without a shell."""
    try:
        done = subprocess.run(argv, capture_output=True, timeout=timeout_seconds, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        if check:
            raise
        return 127, '', str(error)[-CAPTURE_BYTES:]
    result = (done.returncode, done.stdout[-CAPTURE_BYTES:].decode(errors='replace'),
              done.stderr[-CAPTURE_BYTES:].decode(errors='replace'))
    if check and done.returncode != 0:
        raise RuntimeError(f'{argv[0]} failed: {result[2].strip()[-512:]}')
    return result


def device_text(value: Path) -> str:
    dev = value.stat().st_dev
    return f'{os.major(dev)}:{os.minor(dev)}'


def mount_row(value: Path, mountinfo=None):
    """Deepest mountinfo row for the value's device: (source, fstype, options)."""
    device = device_text(value)
    best = None
    if mountinfo is None:
        mountinfo = Path('/proc/self/mountinfo').read_text()
    for line in mountinfo.splitlines():
        row = line.split()
        separator = row.index('-')
        mount = Path(re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1], 8)), row[4]))
        if row[2] == device and (mount == value or mount in value.parents):
            candidate = (len(mount.parts), row[separator + 2], row[separator + 1],
                         set(row[5].split(',')) | set(row[separator + 3].split(',')))
            best = candidate if best is None or candidate[0] > best[0] else best
    if best is None:
        raise RuntimeError('operation volume mount observation unavailable')
    return best[1], best[2], best[3]


def loop_backing(device: str, sysfs=Path('/sys')):
    """Backing file, offset and size limit of a loop block device ``major:minor``."""
    loop = sysfs / 'dev/block' / device / 'loop'
    try:
        backing = Path((loop / 'backing_file').read_text().strip())
        return backing, int((loop / 'offset').read_text()), int((loop / 'sizelimit').read_text())
    except (OSError, ValueError):
        return None, None, None


def builder_version_ok(version: str) -> bool:
    return re.fullmatch(r'buildah version ' + re.escape(BUILDER_VERSION) + r'(?: \([^\r\n]{1,512}\))?', version) is not None


def evaluate(evidence) -> list[str]:
    """Pure decision refusal controls (FS-GG/.github#4778) over observed facts."""
    refusals = []
    runner, services, userns = evidence['runner'], evidence['services'], evidence['userns']
    volume, resources = evidence['volume'], evidence['resources']
    if evidence['relation'] == DEDICATED_RELATION:
        shared = shared_controller()
        refusals.extend(shared.dedicated_runner_refusals(runner))
        if runner.get('dedicated', {}).get('bootId') != evidence['bootId']:
            refusals.append('dedicated-boot')
    elif evidence['relation'] != RELATION or runner['imageOS'] != RUNNER_IMAGE or runner['githubHosted'] is not True \
            or runner['imageVersion'] != RUNNER_IMAGE_VERSION:
        refusals.append('runner-image')
    if re.fullmatch(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', evidence['bootId'] or '') is None:
        refusals.append('boot-identity')
    if services['stopped'] != list(STOPPED_SERVICES) or services['active'] or services['daemonProcesses']:
        refusals.append('service-stop')
    if type(userns['apparmorRestrictUnprivileged']) is not int or userns['apparmorRestrictUnprivileged'] != 0 \
            or type(userns['maxUserNamespaces']) is not int or userns['maxUserNamespaces'] <= 0:
        refusals.append('rootless-userns')
    if volume['filesystem'] != 'ext4' or volume['discard'] != 'nodiscard':
        refusals.append('volume-format')
    if not 0 < volume['bytes'] <= volume['allocatedBytes'] or not volume['loopBacked'] \
            or volume['backingDevice'] == volume['device']:
        refusals.append('volume-preallocation')
    if volume['freeBytes'] < DISK_BYTES or volume['loadStoreDevice'] != volume['device'] or volume['freeInodes'] <= 0:
        refusals.append('volume-capacity')
    if not builder_version_ok(evidence['builder']['version']):
        refusals.append('builder-version')
    if resources['memAvailableBytes'] < MEMORY_BYTES:
        refusals.append('memory')
    if resources['logicalCpus'] < CPUS or resources['affinity'] < CPUS:
        refusals.append('cpu')
    return refusals


def finish(observed):
    """Pure: apply the refusal controls and emit the controller's closed shape."""
    evidence = json.loads(json.dumps(observed))
    refusals = evaluate(evidence)
    evidence['refusals'] = refusals
    evidence['verdict'] = 'refused' if refusals else 'admitted'
    # The controller's closed reader accepts exactly its declared volume fields;
    # the load-store device and loop correspondence are rechecked there or above.
    evidence['volume'].pop('loadStoreDevice')
    evidence['volume'].pop('loopBacked')
    return evidence


def read_int(value: Path):
    try:
        return int(value.read_text().strip())
    except (OSError, ValueError):
        return None


def security_observation():
    """Read-only observations; missing/unreadable evidence is explicitly Unknown."""
    result = {'schema': 'fsgg.bar-network-security/1',
              'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
              'uid': os.getuid(), 'gid': os.getgid(), 'pid': os.getpid(), 'observations': {}}
    for name in ('/sys/kernel/security/lsm', '/sys/module/apparmor/parameters/enabled',
                 '/sys/kernel/security/apparmor/profiles', '/proc/self/attr/current',
                 '/proc/self/status', '/proc/self/mountinfo', '/proc/self/uid_map', '/proc/self/gid_map'):
        try:
            with open(name, 'rb') as stream:
                payload = stream.read(65537)
            result['observations'][name] = ({'status': 'observed', 'value': payload.decode(errors='replace')}
                if len(payload) <= 65536 else {'status': 'Unknown', 'reason': 'byte bound exceeded'})
        except OSError as error:
            result['observations'][name] = {'status': 'Unknown', 'reason': str(error)[:256]}
    result['namespaces'] = {}
    for name in ('pid', 'user', 'mnt', 'net'):
        try:
            result['namespaces'][name] = {'status': 'observed', 'inode': Path('/proc/self/ns/' + name).stat().st_ino}
        except OSError as error:
            result['namespaces'][name] = {'status': 'Unknown', 'reason': str(error)[:256]}
    result['scope'] = 'observed host state; rootless Podman AppArmor=false is not host LSM absence'
    return result


def security_readback(args):
    # Fixed bounded kernel read, no policy/sysctl/profile mutation. A missing or
    # empty audit log cannot prove that no LSM denied the short-lived helper.
    started = os.environ.get('HOST_JOB_STARTED', '')
    if not re.fullmatch(r'[0-9]{1,12}', started):
        raise RuntimeError('original host job start required')
    code, output, error = command(['/usr/bin/journalctl', '--kernel', '--boot', '--no-pager',
        '--output=json', '-n', '128', '--since=@' + started, '--grep=apparmor|AVC|DENIED'], timeout_seconds=30)
    value = {'security': security_observation(), 'audit': {'exit': code, 'stdout': output,
             'stderr': error, 'completeness': 'unproved', 'absenceConclusion': 'Unknown'},
             'scope': 'original operation kernel readback; no process or provider disposal proof'}
    target = Path(args.evidence)
    descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'wb') as stream:
        stream.write(canonical(value))
    os.chown(target, args.owner_uid, args.owner_gid)
    return 0



def shared_controller():
    spec = importlib.util.spec_from_file_location('bar_domain_shared_profile', Path(__file__).with_name('portable_workspace_sc2.py'))
    value = importlib.util.module_from_spec(spec)
    source = Path(__file__).with_name('portable_workspace_sc2.py')
    raw = source.read_bytes()
    if len(raw) > 1048576:
        raise RuntimeError('bounded shared profile source required')
    exec(compile(raw, str(source), 'exec'), value.__dict__)
    return value


# The BAR domain serves only the Native.Qualification caller. A sealed binding that names the other private
# target (FS.GG.FourD) or any other workflow/job is a different runner's binding and must not admit BAR work.
BAR_BINDING_TARGET = {'runnerRepository': 'FS-GG/FS.GG.Native.Qualification',
                      'allowedWorkflow': 'FS-GG/FS.GG.Native.Qualification/.github/workflows/bar-hosted-image-qualification.yml@refs/heads/main',
                      'allowedJob': 'full-image'}


def binding_target_refusals(value):
    return [key for key, expected in BAR_BINDING_TARGET.items() if value.get(key) != expected]


def dedicated_runner_observation(binding_path=Path('/etc/fsgg-qualification-runner/instance-binding.json')):
    """Observe a root-sealed binding, real OS/UUID/boot; never synthesize hosted facts."""
    fd = os.open(binding_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o644 or not 0 < info.st_size <= 65536:
            raise RuntimeError('dedicated root-sealed instance binding unavailable')
        raw = stream.read(65537)
    if len(raw) != info.st_size:
        raise RuntimeError('dedicated binding changed while reading')
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise RuntimeError('duplicate dedicated binding field')
            value[key] = item
        return value
    value = json.loads(raw, object_pairs_hook=unique)
    if value.get('schema') != 'fsgg.dedicated-qualification-instance/1':
        raise RuntimeError('dedicated binding schema differs')
    if binding_target_refusals(value):
        raise RuntimeError('dedicated binding targets another repository, workflow or job')
    os_data = {}
    for line in Path('/etc/os-release').read_text().splitlines():
        if '=' in line:
            key, item = line.split('=', 1); os_data[key] = item.strip('"')
    uuid = Path('/sys/class/dmi/id/product_uuid').read_text().strip()
    if value.get('systemUuid') != uuid or os.environ.get('RUNNER_ENVIRONMENT') != 'self-hosted' or os.uname().machine != 'x86_64':
        raise RuntimeError('dedicated binding actual machine/environment differs')
    return {'imageOS': os_data.get('ID', '') + os_data.get('VERSION_ID', ''),
            'imageVersion': value.get('qualifiedImageSha256'), 'githubHosted': False,
            'dedicated': {key: value.get(key) for key in ('baselineId', 'providerInstanceId', 'providerImageId',
                'systemUuid', 'oneJobOnly', 'runnerGroup', 'runnerLabel')} | {
                'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                'bindingSha256': hashlib.sha256(raw).hexdigest()}}


def admit(args) -> int:
    if os.geteuid() != 0:
        raise RuntimeError('admit observes root-owned domain state and must run as root')
    dedicated = getattr(args, 'profile', 'github-hosted') == 'dedicated-preconfigured'
    runner = dedicated_runner_observation() if dedicated else None
    if dedicated and shared_controller().dedicated_runner_refusals(runner):
        raise RuntimeError('dedicated image/binding source selection unavailable or differs')
    allocation, load_store = Path(args.allocation).resolve(strict=True), Path(args.load_store).resolve(strict=True)
    backing = Path(args.backing_image).resolve(strict=True)
    userland = Path(args.userland).resolve(strict=True)
    active = []
    for unit in STOPPED_SERVICES:
        state = command(['systemctl', 'is-active', unit])[1].strip()
        if state not in ('inactive', 'failed', 'unknown'):
            active.append(unit)
    daemons = 0
    for entry in Path('/proc').iterdir():
        if entry.name.isdigit():
            try:
                name = (entry / 'comm').read_text().strip()
            except OSError:
                continue
            daemons += any(name == daemon or name.startswith(daemon + '-') for daemon in DAEMONS)
    source, filesystem, options = mount_row(allocation)
    stats = os.statvfs(allocation)
    info = backing.stat()
    # The preallocation must be the actual backing file of the mounted loop device.
    loop_file, offset, size_limit = loop_backing(device_text(allocation))
    loop_backed = (loop_file is not None and offset == 0 and size_limit == 0
                   and source.startswith('/dev/loop')
                   and (loop_file.stat().st_dev, loop_file.stat().st_ino) == (info.st_dev, info.st_ino))
    memory = {}
    for row in Path('/proc/meminfo').read_text().splitlines():
        key, value = row.split(':', 1)
        memory[key] = int(value.split()[0]) * 1024
    exit_code, out, err = command(['unshare', '--mount', '--pid', '--fork', '/bin/sh', '-c',
                                   'mount --make-rprivate / && mount -t proc proc "$1/proc" && '
                                   'exec chroot "$1" /usr/bin/buildah --version', 'sh', str(userland)])
    evidence = {
        'schema': SCHEMA, 'relation': DEDICATED_RELATION if dedicated else RELATION, 'verdict': None, 'refusals': None,
        'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
        'runner': runner if dedicated else {'imageOS': os.environ.get('ImageOS', ''), 'imageVersion': os.environ.get('ImageVersion', ''),
                   'githubHosted': os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'},
        'services': {'stopped': list(STOPPED_SERVICES), 'active': active, 'daemonProcesses': daemons},
        'userns': {'apparmorRestrictUnprivileged': read_int(Path('/proc/sys/kernel/apparmor_restrict_unprivileged_userns')),
                   'maxUserNamespaces': read_int(Path('/proc/sys/user/max_user_namespaces'))},
        'volume': {'device': device_text(allocation), 'loadStoreDevice': device_text(load_store),
                   'filesystem': filesystem, 'bytes': info.st_size, 'allocatedBytes': info.st_blocks * 512,
                   'freeBytes': stats.f_bavail * stats.f_frsize, 'freeInodes': stats.f_favail,
                   'discard': 'discard' if 'discard' in options else 'nodiscard', 'loopBacked': loop_backed,
                   'backingDevice': f'{os.major(info.st_dev)}:{os.minor(info.st_dev)}'},
        'builder': {'version': out.strip() if exit_code == 0 else 'unavailable: ' + err.strip()[-256:]},
        'resources': {'memAvailableBytes': memory['MemAvailable'], 'logicalCpus': os.cpu_count(),
                      'affinity': len(os.sched_getaffinity(0))}}
    evidence = finish(evidence)
    refusals = evidence['refusals']
    payload = canonical(evidence)
    target = Path(args.evidence)
    descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'wb') as stream:
        stream.write(payload)
    os.chown(target, args.owner_uid, args.owner_gid)
    security = target.with_name('root-security.json')
    descriptor = os.open(security, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'wb') as stream:
        stream.write(canonical(security_observation()))
    os.chown(security, args.owner_uid, args.owner_gid)
    print(json.dumps({'evidence': str(target), 'sha256': hashlib.sha256(payload).hexdigest(),
                      'verdict': evidence['verdict'], 'refusals': refusals}, sort_keys=True))
    return 2 if refusals else 0


def uid_map_covers(text: str, uid: int) -> bool:
    for row in text.splitlines():
        parts = row.split()
        if len(parts) == 3 and all(part.isdigit() for part in parts):
            start, _, count = map(int, parts)
            if start <= uid < start + count:
                return True
    return False


def userland_check(args) -> int:
    allocation, load_store = Path(args.allocation), Path(args.load_store)
    spec = importlib.util.spec_from_file_location('bar_hosted_shared', args.controller_module)
    shared = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(shared)
    builder = ['/usr/bin/buildah', '--storage-driver=vfs', '--root', str(allocation / 'build/graphroot'),
               '--runroot', str(allocation / 'build/runroot'), '--cgroup-manager=cgroupfs']
    loader = ['/usr/bin/podman', '--storage-driver=vfs', '--root', str(load_store),
              '--runroot', str(allocation / 'load/runroot'), '--cgroup-manager=cgroupfs', '--events-backend=file']
    for value in (allocation / 'build/graphroot', allocation / 'build/runroot', allocation / 'load/runroot', load_store):
        value.mkdir(parents=True, exist_ok=True, mode=0o700)
    checks, refusals = {}, []
    version = command(['/usr/bin/buildah', '--version'])
    checks['builderVersion'] = version[1].strip()
    if version[0] != 0 or not builder_version_ok(checks['builderVersion']):
        refusals.append('builder-version')
    for kind in ('uid', 'gid'):
        mapping = command(builder + ['unshare', 'cat', f'/proc/self/{kind}_map'])
        checks[f'rootless{kind.title()}Map'] = mapping[1].strip() if mapping[0] == 0 else 'failed: ' + mapping[2].strip()[-512:]
        if mapping[0] != 0 or not uid_map_covers(mapping[1], IMAGE_USER):
            refusals.append(f'rootless-{kind}-mapping')
    scratch = command(builder + ['from', '--pull=never', 'scratch'])
    checks['scratchContainer'] = scratch[1].strip() if scratch[0] == 0 else 'failed: ' + scratch[2].strip()[-512:]
    if scratch[0] == 0:
        removed = command(builder + ['rm', scratch[1].strip()])
        checks['scratchRemoved'] = removed[0] == 0
        if removed[0] != 0:
            refusals.append('scratch-retirement')
    else:
        refusals.append('rootless-build-store')
    census = command(loader + ['images', '--quiet'])
    checks['loadStoreCensus'] = census[1].split() if census[0] == 0 else 'failed: ' + census[2].strip()[-512:]
    if census[0] != 0 or census[1].split():
        refusals.append('load-store-census')
    resources = shared.host_resources(allocation, load_store)
    checks['hostResources'] = resources
    if resources['hostMemoryAvailableBytes'] < MEMORY_BYTES or resources['hostLogicalCpus'] < CPUS \
            or len(resources['affinity']) < CPUS:
        refusals.append('capacity')
    if resources['diskFreeBytes'] < DISK_BYTES or resources['loadStoreDiskFreeBytes'] < DISK_BYTES:
        refusals.append('disk')
    maximum, tasks = resources['enclosingMemoryMaxBytes'], resources['enclosingTasksMax']
    if maximum is not None and maximum - resources['enclosingMemoryCurrentBytes'] < 12 * 1024**3 + 2 * 1024**3:
        refusals.append('enclosing-memory')
    if tasks is not None and tasks - resources['enclosingTasksCurrent'] < 512:
        refusals.append('enclosing-tasks')
    if resources['enclosingCpuCapacity'] is not None and resources['enclosingCpuCapacity'] < CPUS:
        refusals.append('enclosing-cpu')
    # Delegated unit with its own cgroup namespace: the controller sees its root.
    if resources['cgroup'] != '0::/':
        refusals.append('cgroup-namespace')
    checks['sameVolume'] = device_text(allocation) == device_text(load_store)
    if not checks['sameVolume']:
        refusals.append('volume')
    pins = {name: Path(name).exists() for name in sorted(set(shared.SUPPORT_LIBRARIES.values()) |
                                                        set(shared.SUPPORT_EXECUTABLES.values()))}
    checks['supportPins'] = pins
    if not all(pins.values()):
        refusals.append('support-pins')
    checks['uid'] = os.getuid()
    if checks['uid'] != args.expected_uid:
        refusals.append('unprivileged-identity')
    result = {'schema': CHECK_SCHEMA, 'verdict': 'refused' if refusals else 'passed', 'refusals': refusals,
              'checks': checks, 'scope': 'hosted Arch-userland domain preflight; no image pull, product source or BAR content'}
    print(json.dumps(result, sort_keys=True, default=str))
    return 2 if refusals else 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    modes = parser.add_subparsers(dest='mode', required=True)
    modes.add_parser('dedicated-profile')
    admit_parser = modes.add_parser('admit')
    for name in ('--allocation', '--load-store', '--backing-image', '--userland', '--evidence'):
        admit_parser.add_argument(name, required=True)
    admit_parser.add_argument('--profile', choices=('github-hosted', 'dedicated-preconfigured'), default='github-hosted')
    admit_parser.add_argument('--owner-uid', type=int, required=True)
    admit_parser.add_argument('--owner-gid', type=int, required=True)
    check_parser = modes.add_parser('userland-check')
    for name in ('--allocation', '--load-store', '--controller-module'):
        check_parser.add_argument(name, required=True)
    check_parser.add_argument('--expected-uid', type=int, required=True)
    security_parser = modes.add_parser('security-readback')
    security_parser.add_argument('--evidence', required=True)
    security_parser.add_argument('--owner-uid', type=int, required=True)
    security_parser.add_argument('--owner-gid', type=int, required=True)
    args = parser.parse_args(argv)
    if args.mode == 'dedicated-profile':
        if os.geteuid() != 0:
            raise RuntimeError('root-sealed dedicated profile must be checked as root')
        runner = dedicated_runner_observation()
        refusals = shared_controller().dedicated_runner_refusals(runner)
        print(json.dumps({'profile': DEDICATED_RELATION, 'refusals': refusals,
                          'scope': 'image/binding correspondence only; actual catalog and resource gates still required'}))
        return 2 if refusals else 0
    if args.mode == 'security-readback':
        return security_readback(args)
    return admit(args) if args.mode == 'admit' else userland_check(args)


if __name__ == '__main__':
    sys.exit(main())
