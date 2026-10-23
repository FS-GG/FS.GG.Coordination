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
import sys

SCHEMA = 'fsgg.bar-hosted-domain/1'
CHECK_SCHEMA = 'fsgg.bar-hosted-userland-check/1'
RELATION = 'dedicated-ephemeral-hosted-vm'
RUNNER_IMAGE = 'ubuntu24'
STOPPED_SERVICES = ('containerd.service', 'docker.service', 'docker.socket')
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


def command(argv, *, check=False):
    """Bounded fixed command; returns (exit, stdout, stderr) without a shell."""
    try:
        done = subprocess.run(argv, capture_output=True, timeout=COMMAND_SECONDS, check=False)
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


def mount_row(value: Path):
    """Deepest mountinfo row for the value's device: (source, fstype, options)."""
    device = device_text(value)
    best = None
    for line in Path('/proc/self/mountinfo').read_text().splitlines():
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


def builder_version_ok(version: str) -> bool:
    return re.fullmatch(r'buildah version ' + re.escape(BUILDER_VERSION) + r'(?: \([^\r\n]{1,512}\))?', version) is not None


def evaluate(evidence) -> list[str]:
    """Pure decision refusal controls (FS-GG/.github#4778) over observed facts."""
    refusals = []
    runner, services, userns = evidence['runner'], evidence['services'], evidence['userns']
    volume, resources = evidence['volume'], evidence['resources']
    if runner['imageOS'] != RUNNER_IMAGE or not runner['githubHosted'] \
            or re.fullmatch(r'[0-9]{8}\.[0-9]{1,6}(?:\.[0-9]{1,6})?', runner['imageVersion'] or '') is None:
        refusals.append('runner-image')
    if services['stopped'] != list(STOPPED_SERVICES) or services['active'] or services['daemonProcesses']:
        refusals.append('service-stop')
    if userns['apparmorRestrictUnprivileged'] != 0 or not userns['maxUserNamespaces']:
        refusals.append('rootless-userns')
    if volume['filesystem'] != 'ext4' or volume['discard'] != 'nodiscard':
        refusals.append('volume-format')
    if not 0 < volume['bytes'] <= volume['allocatedBytes']:
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
    # the load-store device correspondence is rechecked there from the paths.
    evidence['volume'].pop('loadStoreDevice')
    return evidence


def read_int(value: Path):
    try:
        return int(value.read_text().strip())
    except (OSError, ValueError):
        return None


def admit(args) -> int:
    if os.geteuid() != 0:
        raise RuntimeError('admit observes root-owned domain state and must run as root')
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
    backing_rdev = Path(source).stat().st_rdev if source.startswith('/dev/') else 0
    memory = {}
    for row in Path('/proc/meminfo').read_text().splitlines():
        key, value = row.split(':', 1)
        memory[key] = int(value.split()[0]) * 1024
    exit_code, out, err = command(['chroot', str(userland), '/usr/bin/buildah', '--version'])
    evidence = {
        'schema': SCHEMA, 'relation': RELATION, 'verdict': None, 'refusals': None,
        'runner': {'imageOS': os.environ.get('ImageOS', ''), 'imageVersion': os.environ.get('ImageVersion', ''),
                   'githubHosted': os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'},
        'services': {'stopped': list(STOPPED_SERVICES), 'active': active, 'daemonProcesses': daemons},
        'userns': {'apparmorRestrictUnprivileged': read_int(Path('/proc/sys/kernel/apparmor_restrict_unprivileged_userns')),
                   'maxUserNamespaces': read_int(Path('/proc/sys/user/max_user_namespaces'))},
        'volume': {'device': device_text(allocation), 'loadStoreDevice': device_text(load_store),
                   'filesystem': filesystem, 'bytes': info.st_size, 'allocatedBytes': info.st_blocks * 512,
                   'freeBytes': stats.f_bavail * stats.f_frsize, 'freeInodes': stats.f_favail,
                   'discard': 'discard' if 'discard' in options else 'nodiscard',
                   'backingDevice': f'{os.major(backing_rdev)}:{os.minor(backing_rdev)}'},
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
    mapping = command(builder + ['unshare', 'cat', '/proc/self/uid_map'])
    checks['rootlessUidMap'] = mapping[1].strip() if mapping[0] == 0 else 'failed: ' + mapping[2].strip()[-512:]
    if mapping[0] != 0 or not uid_map_covers(mapping[1], IMAGE_USER):
        refusals.append('rootless-mapping')
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
    checks['sameVolume'] = device_text(allocation) == device_text(load_store)
    if not checks['sameVolume']:
        refusals.append('volume')
    pins = {name: Path(name).exists() for name in sorted(set(shared.SUPPORT_LIBRARIES.values()) |
                                                        set(shared.SUPPORT_EXECUTABLES.values()))}
    checks['supportPins'] = pins
    if not all(pins.values()):
        refusals.append('support-pins')
    checks['uid'] = os.getuid()
    result = {'schema': CHECK_SCHEMA, 'verdict': 'refused' if refusals else 'passed', 'refusals': refusals,
              'checks': checks, 'scope': 'hosted Arch-userland domain preflight; no image pull, product source or BAR content'}
    print(json.dumps(result, sort_keys=True, default=str))
    return 2 if refusals else 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    modes = parser.add_subparsers(dest='mode', required=True)
    admit_parser = modes.add_parser('admit')
    for name in ('--allocation', '--load-store', '--backing-image', '--userland', '--evidence'):
        admit_parser.add_argument(name, required=True)
    admit_parser.add_argument('--owner-uid', type=int, required=True)
    admit_parser.add_argument('--owner-gid', type=int, required=True)
    check_parser = modes.add_parser('userland-check')
    for name in ('--allocation', '--load-store', '--controller-module'):
        check_parser.add_argument(name, required=True)
    args = parser.parse_args(argv)
    return admit(args) if args.mode == 'admit' else userland_check(args)


if __name__ == '__main__':
    sys.exit(main())
