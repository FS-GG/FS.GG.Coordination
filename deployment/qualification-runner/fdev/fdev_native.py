#!/usr/bin/env python3
"""fdev native-chain window launcher: M0 source for .github#4778 / #4650.

Owning plan: .github docs/coordination/2026-10-30-native-chain-rehost-fdev.md (M0).
Root rulings: #4778 comments 6522829725 (decisions 1-6), 6526677331 (storage I/O),
6527516917 (io.max scope and shared-host ruling), 6530390005 (Home capacity fact).

Custody is cooperative (fdev is the only security zone, #4778 comment 6519522997):
claims, hashes, clocks and readbacks detect accidents and keep the record coherent.
Nothing here is tamper-proof, and evidence never claims independence from fdev or Home.

Subcommands
  preflight          read-only; every precondition as Supported / Unsupported / Unknown
  launch --dry-run   preflight plus the exact effect plan; executes and writes nothing
  launch             the window (M1+ only, under a Root selection): bounded-scope check,
                     claim, start witness, io.max caps, disks, guest unit, observation,
                     collector, cap removal, evidence. Every check fails closed.
  window-exit        idempotent removal of recorded io.max caps (recovery after a crash)
  attach-home-io     re-evaluate a window's evidence with Home-observed I/O evidence
  validate-evidence  validate an evidence document against the schema rules
  census             read-only open-file census of one mount (run under sudo -n)
  qmp-powerdown      guest unit ExecStop: QMP system_powerdown, then wait for MAINPID
  collect            collector unit body: observe guest exit, hash outputs, closed packet

No subcommand selects an operation, clock, claim or credential. M0 merges source only.
"""
from __future__ import annotations

import argparse
import datetime as dt
import email.utils
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import stat
import subprocess
import sys
import time
import uuid as uuidlib

SUPPORTED, UNSUPPORTED, UNKNOWN = 'Supported', 'Unsupported', 'Unknown'
GIB = 1024 ** 3
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]

# ---------------------------------------------------------------------------
# Policy. Values come from the merged plan and Root's rulings; Root binds the
# per-window values (clocks, caps, exempt sessions) in the selection document.
# ---------------------------------------------------------------------------
POLICY = {
    'hostKind': 'fdev-agent-vm-drained-window',
    'ioSharedWithHostCoTenants': True,
    'provider': 'fdev-kvm-qualification-v1',
    'owningIssue': 'FS-GG/.github#4778',
    'rootCommentAuthors': ('EHotwagner',),
    'retiredOperations': ('83a08ef2b158471bbec61133da3b21ed', '69db1fff629c4048981886544c7f8aa6',
                          '2ae4d697a81344a697c0d6448945168f', 'd2f2171d7c5d55aacdc560a09e19b46a'),
    'retiredShortIds': ('83a08', '69db', '2ae4', 'd2f2'),
    'poolMount': '/srv/v3-pool', 'poolLabel': 'systemadmin-v3',
    'claimsDir': '/srv/v3-pool/native/claims', 'runsDir': '/srv/v3-pool/native/runs',
    'commitRoot': '/srv/bulk/native-chain',
    # HDD-backed fdev filesystems (Home evidence, #4778 comment 6526677331); resolved to
    # whole-disk major:minor at runtime, never assumed from a device name.
    'hddMounts': ('/srv/v3-pool', '/srv/bulk', '/srv/sc2-reserved',
                  '/mnt/storage/containers/learn-qualification/filesystem'),
    'laneUsers': ('developer', 'fsgg-store-dev', 'fsgg-learn-qual'),
    # Service units capped (not drained) during the window. SC2 service units are
    # discovered by name; any found is capped too.
    'serviceUnits': (('fsgg-store', 'fsgg-store-dev', 'fsgg-store.service'),
                     ('learn', 'fsgg-learn-qual', 'learn-qualification.service')),
    'sc2NamePattern': r'sc2',
    # A drained lane holds only idle shells and pause processes. Anything else refuses.
    'idleComms': ('nu', 'bash', 'sh', 'dash', 'zsh', 'fish', 'tmux', 'tmux: server', 'catatonit', 'sleep',
                  'sshd-session', 'tmux: client', '(sd-pam)', 'systemd'),
    # In an exempt (Root / readback / launcher) unit, agent front ends may stay; build
    # servers and compilers may not, since in-process subagents would show up there.
    'exemptAllowedComms': ('claude', 'claude.exe', 'codex', 'node', 'gh', 'git', 'python3', 'ssh'),
    'memAvailableMinBytes': 26 * GIB,
    'poolHeadroomBytes': 64 * GIB,
    'defaultPoolNeedBytes': 280 * GIB,
    'guest': {'vcpu': 4, 'memoryBytes': 16 * GIB, 'rootDiskBytes': 256 * GIB},
    'slice': {'name': 'fsgg-native.slice', 'AllowedCPUs': '8-11', 'MemoryMin': '18G', 'MemoryMax': '19G',
              'TasksMax': '512'},
    'collectSlice': {'name': 'fsgg-native-collect.slice', 'MemoryMax': '1G', 'TasksMax': '64'},
    'launcherScope': {'MemoryMax': '1G', 'TasksMax': '128'},
    'systemdMinVersion': 250,
    'dotnetRuntime': '10.0.12',
    'tools': ('qemu-system-x86_64', 'qemu-img', 'systemd-run', 'systemctl', 'fallocate', 'sudo', 'gh'),
    'postingAllowanceSeconds': 300,
    'maxCapTargets': 64,
}
EFFECT_TIMEOUT = 30  # every systemctl/systemd-run effect is bounded
SCOPE_MARGIN_SECONDS = 120
NOBLE_LOCK = 'deployment/qualification-runner/noble/acquired-input-lock.json'
OP_PATTERN = re.compile(r'[0-9a-f]{32}')
STEP_PATTERN = re.compile(r'[a-z][a-z0-9-]{0,23}')
SHA_PATTERN = re.compile(r'[0-9a-f]{64}')


class Refusal(Exception):
    """A fail-closed refusal: the named check did not pass; nothing further runs."""

    def __init__(self, check, reason):
        super().__init__(f'{check}: {reason}')
        self.check, self.reason = check, reason


class Interrupted(Exception):
    """A termination signal turned into an exception, so the window's exit path runs."""

    def __init__(self, signum):
        super().__init__(f'signal {signal.Signals(signum).name}')
        self.signum = signum


SIGNALS = {'raise': True, 'pending': []}


def _on_signal(signum, _frame):
    if SIGNALS['raise']:
        SIGNALS['raise'] = False  # one-shot: disarm before raising, so no second exception can land in exit
        raise Interrupted(signum)
    SIGNALS['pending'].append(int(signum))  # disarmed (claim or window exit): record, do not abort


def recorded_signals(snapshot, pending):
    """Signals recorded during window exit plus any recorded after launch returned, counted once.
    The window record holds a snapshot of the pending list taken at exit; later arrivals extend it."""
    snapshot, pending = list(snapshot), list(pending)
    extra = pending[len(snapshot):] if pending[:len(snapshot)] == snapshot else pending
    return snapshot + extra


def install_signal_exceptions():
    """SIGTERM (scope RuntimeMaxSec or stop), SIGINT and SIGHUP raise Interrupted, once per arming."""
    SIGNALS['raise'], SIGNALS['pending'] = True, []
    return {s: signal.signal(s, _on_signal) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}


def require(condition, check, reason):
    if not condition:
        raise Refusal(check, reason)


def sha256_file(path, chunk=8 * 1024 ** 2):
    digest, count = hashlib.sha256(), 0
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, 'rb') as stream:
        require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), 'file.regular', f'{path} is not a regular file')
        while block := stream.read(chunk):
            digest.update(block); count += len(block)
    return digest.hexdigest(), count


def load_json(path, maximum=4 * 1024 ** 2):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, 'rb') as stream:
        raw = stream.read(maximum + 1)
    require(len(raw) <= maximum, 'json.bounded', f'{path} exceeds {maximum} bytes')

    def unique(rows):
        result = {}
        for key, value in rows:
            require(key not in result, 'json.unique', f'duplicate property {key} in {path}')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def parse_utc(text, check='time.utc'):
    require(isinstance(text, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ', text) is not None,
            check, f'UTC timestamp YYYY-MM-DDTHH:MM:SSZ required, got {text!r}')
    return dt.datetime.strptime(text, '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=dt.timezone.utc)


def utc_text(moment):
    return moment.astimezone(dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


# ---------------------------------------------------------------------------
# Selection: the per-window document Root binds. Validation is pure.
# ---------------------------------------------------------------------------
def validate_operation_id(op):
    require(isinstance(op, str) and OP_PATTERN.fullmatch(op) is not None, 'selection.operation-id',
            '32 lowercase hex operation id required')
    require(op not in POLICY['retiredOperations'], 'selection.operation-not-retired',
            f'operation {op} is a retired identity and is never reused or replayed')
    require(not op.startswith(POLICY['retiredShortIds']), 'selection.operation-not-retired',
            f'operation {op} shares a retired short id prefix; it would be ambiguous in records')
    return op


def validate_clocks(clocks):
    keys = ('preStartSeconds', 'usefulSeconds', 'retireSeconds', 'collectorSeconds', 'collectorRetireSeconds',
            'enclosingSeconds')
    require(isinstance(clocks, dict) and set(clocks) == set(keys), 'selection.clocks', f'exactly {keys} required')
    for key in keys:
        require(type(clocks[key]) is int and 0 < clocks[key] <= 86400, 'selection.clocks', f'{key} positive int')
    total = sum(clocks[key] for key in keys[:-1])
    require(total <= clocks['enclosingSeconds'], 'selection.clocks',
            f'step clocks {total}s exceed the enclosing bound {clocks["enclosingSeconds"]}s')
    return clocks


def validate_io_caps(caps):
    keys = ('rbps', 'wbps', 'riops', 'wiops')
    require(isinstance(caps, dict) and set(caps) == set(keys), 'io.caps-bound',
            'Root must bind rbps, wbps, riops and wiops for the window; no default is guessed')
    for key in keys:
        require(type(caps[key]) is int and caps[key] > 0, 'io.caps-bound', f'{key} must be a positive int')
    return caps


def validate_selection(sel, caps=True, operation=True):
    require(isinstance(sel, dict) and sel.get('schema') == 'fsgg.fdev-native-window-selection/1',
            'selection.valid', 'fsgg.fdev-native-window-selection/1 required')
    if operation:  # the preflight reports identity in its own selection.operation-not-retired row
        validate_operation_id(sel.get('operationId'))
    require(isinstance(sel.get('step'), str) and STEP_PATTERN.fullmatch(sel['step']) is not None,
            'selection.valid', 'step name [a-z][a-z0-9-]{0,23} required')
    root = sel.get('rootSelection', {})
    require(root.get('issue') == POLICY['owningIssue'] and type(root.get('commentId')) is int,
            'selection.valid', f'rootSelection must name {POLICY["owningIssue"]} and an integer commentId')
    validate_clocks(sel.get('clocks'))
    if caps:  # the preflight reports caps in their own io.caps-bound row
        validate_io_caps(sel.get('ioCaps'))
    exempt = sel.get('exemptCgroups')
    require(isinstance(exempt, list) and 1 <= len(exempt) <= 8 and all(
        isinstance(p, str) and p.startswith('/user.slice/') and '..' not in p for p in exempt),
        'selection.valid', 'exemptCgroups: 1-8 absolute cgroup paths under /user.slice (Root session, readback)')
    require(isinstance(sel.get('rootDrainAttestation'), str) and sel['rootDrainAttestation'].startswith(
        'https://github.com/FS-GG/'), 'selection.valid',
        'rootDrainAttestation URL required: in-process agent work in exempt sessions is not cgroup-observable')
    image = sel.get('image', {})
    require(isinstance(image.get('path'), str) and image['path'].startswith('/') and image.get('lock') == NOBLE_LOCK,
            'selection.valid', f'image.path (absolute) and image.lock == {NOBLE_LOCK} (the stock pin) required')
    disks = sel.get('readOnlyDisks', [])
    require(isinstance(disks, list) and len(disks) <= 4, 'selection.valid', 'at most four read-only disks')
    for disk in disks:
        require(isinstance(disk.get('path'), str) and disk['path'].startswith('/') and isinstance(
            disk.get('sha256'), str) and SHA_PATTERN.fullmatch(disk['sha256']) is not None,
            'selection.valid', 'each read-only disk needs an absolute path and sha256')
    seed = sel.get('seedDir')
    require(seed is None or (isinstance(seed, str) and seed.startswith('/')), 'selection.valid',
            'seedDir must be absolute or absent')
    validate_host_partition(sel.get('hostPartition'), None)
    require(type(sel.get('hostPartitionMaxAgeSeconds')) is int and 0 < sel['hostPartitionMaxAgeSeconds'] <= 86400,
            'selection.valid', 'hostPartitionMaxAgeSeconds bound required')
    if sel.get('hostMemory') is not None:  # optional; when present Root must bind the floor and age
        validate_host_memory(sel['hostMemory'], None, None)
        require(type(sel.get('hostMemoryMinAvailableBytes')) is int and sel['hostMemoryMinAvailableBytes'] > 0
                and type(sel.get('hostMemoryMaxAgeSeconds')) is int and 0 < sel['hostMemoryMaxAgeSeconds'] <= 86400,
                'selection.valid', 'hostMemory needs a Root-set hostMemoryMinAvailableBytes and hostMemoryMaxAgeSeconds')
    return sel


# ---------------------------------------------------------------------------
# Home host-memory input (Root decision). Optional; absent means Unknown, never Supported.
# ---------------------------------------------------------------------------
def validate_host_memory(doc, now, max_age, floor=None):
    check = 'capacity.host-memory'
    require(isinstance(doc, dict) and doc.get('schema') == 'fsgg.fdev-native-home-memory/1', check,
            'Home host-memory input fsgg.fdev-native-home-memory/1 required')
    measured = parse_utc(doc.get('measuredAt'), check)
    require(isinstance(doc.get('source'), str) and doc['source'].startswith('https://github.com/FS-GG/'), check,
            'source must cite the Home post')
    total, avail = doc.get('memTotalBytes'), doc.get('memAvailableBytes')
    require(type(total) is int and type(avail) is int and 0 <= avail <= total, check,
            'memTotalBytes and memAvailableBytes integers with available <= total required')
    if now is not None:
        age = (now - measured).total_seconds()
        require(age >= -60, check, f'measuredAt is {-age:.0f}s in the future')
        require(max_age is not None and age <= max_age, check,
                f'Home host-memory reading is {age:.0f}s old; the bound is {max_age}s; re-read before the window')
    if floor is not None:
        require(avail >= floor, check, f'host MemAvailable {avail} is below the Root floor {floor}')
    return {'measuredAt': doc['measuredAt'], 'source': doc['source'], 'memTotalBytes': total,
            'memAvailableBytes': avail}


# ---------------------------------------------------------------------------
# Home df input (capacity re-read hook). It arrives as an input; never a guess.
# ---------------------------------------------------------------------------
DF_FIELDS = ('size', 'used', 'avail', 'itotal', 'iused', 'iavail')


def validate_host_partition(doc, now, max_age=None):
    require(isinstance(doc, dict) and doc.get('schema') == 'fsgg.fdev-native-home-df/1',
            'capacity.host-partition', 'Home df input fsgg.fdev-native-home-df/1 required')
    measured = parse_utc(doc.get('measuredAt'), 'capacity.host-partition')
    require(isinstance(doc.get('source'), str) and doc['source'].startswith('https://github.com/FS-GG/'),
            'capacity.host-partition', 'source must cite the Home post')
    values = parse_df_output(doc.get('dfOutput'))
    require(values['used'] + values['avail'] <= values['size'] and values['iused'] + values['iavail'] <= values[
        'itotal'], 'capacity.host-partition', 'df values are inconsistent')
    if now is not None:
        age = (now - measured).total_seconds()
        require(age >= -60, 'capacity.host-partition', f'measuredAt is {-age:.0f}s in the future')
        require(max_age is not None and age <= max_age, 'capacity.host-partition',
                f'Home df reading is {age:.0f}s old; the bound is {max_age}s; re-read capacity before the window')
    return {'measuredAt': doc['measuredAt'], 'source': doc['source'], **values}


def parse_df_output(text):
    """Parse `df -B1 --output=size,used,avail,itotal,iused,iavail` (header plus one row)."""
    require(isinstance(text, str), 'capacity.host-partition', 'dfOutput text required')
    lines = [line.split() for line in text.strip().splitlines() if line.strip()]
    require(len(lines) == 2 and len(lines[1]) == 6, 'capacity.host-partition', 'header plus exactly one df row')
    require(len(lines[0]) == 6 and lines[0][0].endswith('blocks') and lines[0][3:] == ['Inodes', 'IUsed', 'IFree'],
            'capacity.host-partition', 'df header must be size,used,avail,itotal,iused,iavail with -B1')
    require(all(v.isdigit() for v in lines[1]), 'capacity.host-partition', 'df values must be integers')
    return dict(zip(DF_FIELDS, map(int, lines[1])))


# ---------------------------------------------------------------------------
# Live, read-only observation of fdev. Every read is bounded; failure is Unknown.
# ---------------------------------------------------------------------------
class LiveHost:
    cgroot = Path('/sys/fs/cgroup')

    def read(self, path, maximum=1024 ** 2):
        with open(path, 'rb') as stream:
            return stream.read(maximum).decode('utf-8', 'replace')

    def run(self, argv, timeout=30):
        try:
            done = subprocess.run(argv, capture_output=True, timeout=timeout, check=False, stdin=subprocess.DEVNULL)
            return done.returncode, done.stdout.decode('utf-8', 'replace')[:4 * 1024 ** 2]
        except (OSError, subprocess.TimeoutExpired) as error:
            return None, str(error)

    def now(self):
        return dt.datetime.now(dt.timezone.utc).replace(microsecond=0)

    def monotonic(self):
        return time.monotonic()


def _try(fn, default=None):
    try:
        return fn()
    except (OSError, ValueError, KeyError, IndexError):
        return default


def whole_disk(host, mount):
    """Resolve a mount to its whole-disk major:minor (io.max refuses partitions)."""
    if not os.path.ismount(mount):
        return None
    st = os.stat(mount)
    node = f'{os.major(st.st_dev)}:{os.minor(st.st_dev)}'
    sys_path = Path('/sys/dev/block') / node
    if (sys_path / 'partition').exists():
        node = host.read(sys_path.resolve().parent / 'dev').strip()
    return node


def cgroup_procs(host, cgdir):
    pids = []
    for root, _dirs, files in os.walk(cgdir):
        if 'cgroup.procs' in files:
            pids += [int(p) for p in _try(lambda: host.read(Path(root) / 'cgroup.procs').split(), [])]
    return pids


def comm_of(host, pid):
    return _try(lambda: host.read(f'/proc/{pid}/comm').strip(), '?')


def ancestors(host, pid):
    chain, seen = [], set()
    while pid and pid not in seen and pid > 1:
        seen.add(pid); chain.append(pid)
        stat_line = _try(lambda: host.read(f'/proc/{pid}/stat'), '')
        pid = int(stat_line.rsplit(')', 1)[1].split()[1]) if ')' in stat_line else 0
    return chain


def lane_units(host, user_ids):
    """Every unit cgroup that can hold an agent lane for the named uids."""
    units = []
    for uid in user_ids:
        base = host.cgroot / f'user.slice/user-{uid}.slice'
        for child in sorted(_try(lambda: list(base.iterdir()), [])):
            if child.is_dir() and child.name.startswith('session-') and child.name.endswith('.scope'):
                units.append(child)
        manager = base / f'user@{uid}.service'
        for slice_name in ('app.slice', 'user.slice'):
            for child in sorted(_try(lambda: list((manager / slice_name).iterdir()), [])):
                if child.is_dir() and child.name.endswith(('.scope', '.service')):
                    units.append(child)
    return units


def cg_rel(host, path):
    return '/' + str(Path(path).relative_to(host.cgroot))


def observe(host, selection=None, privileged_census=False):
    """Collect every fact the checks need. Read-only; failures become None (Unknown)."""
    facts = {'observedAt': utc_text(host.now())}
    kvm = _try(lambda: os.stat('/dev/kvm'))
    nested = None
    for module in ('kvm_amd', 'kvm_intel'):
        nested = nested or _try(lambda: host.read(f'/sys/module/{module}/parameters/nested').strip())
    facts['kvm'] = {'present': kvm is not None, 'charDevice': bool(kvm and stat.S_ISCHR(kvm.st_mode)),
                    'rw': os.access('/dev/kvm', os.R_OK | os.W_OK), 'nested': nested}
    facts['cgroup'] = {
        'v2': _try(lambda: os.path.exists('/sys/fs/cgroup/cgroup.controllers'), False),
        'rootSubtree': _try(lambda: host.read('/sys/fs/cgroup/cgroup.subtree_control').split()),
        'cpusEffective': _try(lambda: host.read('/sys/fs/cgroup/cpuset.cpus.effective').strip()),
        'userManagerControllers': _try(lambda: host.read(
            f'/sys/fs/cgroup/user.slice/user-{os.getuid()}.slice/user@{os.getuid()}.service/cgroup.controllers').split()),
    }
    rc, out = host.run(['systemctl', '--version'])
    facts['systemdVersion'] = _try(lambda: int(out.split()[1])) if rc == 0 else None
    facts['tools'] = {tool: shutil.which(tool) for tool in POLICY['tools']}
    rc, _ = host.run(['sudo', '-n', 'true'])
    facts['sudo'] = None if rc is None else rc == 0
    meminfo = _try(lambda: host.read('/proc/meminfo'), '')
    match = re.search(r'^MemAvailable:\s+(\d+) kB', meminfo, re.MULTILINE)
    facts['memAvailableBytes'] = int(match.group(1)) * 1024 if match else None

    pool = POLICY['poolMount']
    rc, out = host.run(['findmnt', '-J', '-o', 'TARGET,SOURCE,LABEL,UUID,FSTYPE', '--mountpoint', pool])
    mount = _try(lambda: json.loads(out)['filesystems'][0]) if rc == 0 else None
    vfs = _try(lambda: os.statvfs(pool))
    facts['pool'] = {'mounted': mount is not None, 'label': (mount or {}).get('label'),
                     'uuid': (mount or {}).get('uuid'), 'source': (mount or {}).get('source'),
                     'device': _try(lambda: whole_disk(host, pool)),
                     'freeBytes': vfs.f_bavail * vfs.f_frsize if vfs else None,
                     'freeInodes': vfs.f_favail if vfs else None}
    commit = Path(POLICY['commitRoot'])
    cst = _try(lambda: os.lstat(commit))
    facts['commitRoot'] = {'exists': cst is not None, 'dir': bool(cst and stat.S_ISDIR(cst.st_mode)),
                           'mode': stat.S_IMODE(cst.st_mode) if cst else None, 'uid': cst.st_uid if cst else None,
                           'parentWritable': os.access(commit.parent, os.W_OK)}
    facts['hddDevices'] = {m: _try(lambda m=m: whole_disk(host, m)) for m in POLICY['hddMounts']}

    # Units: anything named fsgg-native-* in any manager, retained or active, and v3 pilots.
    own_scope = None
    self_cg = _try(lambda: host.read('/proc/self/cgroup').strip().split('::', 1)[1])
    if self_cg:
        own_scope = self_cg.rstrip('/').rsplit('/', 1)[-1]
    native = set()
    for root, dirs, _files in os.walk(host.cgroot):
        for name in dirs:
            if name.startswith('fsgg-native-') and name != own_scope and not name.endswith('.slice'):
                native.add(name)
    listings_ok = True
    for argv in (['systemctl', 'list-units', '--all', '--plain', '--no-legend', 'fsgg-native-*'],
                 ['systemctl', '--user', 'list-units', '--all', '--plain', '--no-legend', 'fsgg-native-*']):
        rc, out = host.run(argv)
        listings_ok &= rc == 0
        native |= {line.split()[0] for line in (out.splitlines() if rc == 0 else [])
                   if line.split() and not line.split()[0].endswith('.slice') and line.split()[0] != own_scope}
    rc_pilot, out = host.run(['systemctl', 'list-units', '--all', '--plain', '--no-legend', 'v3-storage@*'])
    pilots = [line.split()[0] for line in out.splitlines() if rc_pilot == 0 and len(line.split()) >= 3
              and line.split()[2] in ('active', 'activating', 'reloading', 'deactivating')]
    sc2 = []
    for root, dirs, _files in os.walk(host.cgroot):
        for name in dirs:
            if re.search(POLICY['sc2NamePattern'], name) and name.endswith(('.service', '.scope')):
                sc2.append(cg_rel(host, Path(root) / name))
    facts['units'] = {'native': sorted(native), 'nativeListingOk': listings_ok,
                      'v3PilotActive': pilots if rc_pilot == 0 else None, 'sc2': sorted(sc2), 'ownScope': own_scope,
                      'selfCgroup': self_cg}

    # Lanes and exempt sessions.
    uids = {}
    for name in POLICY['laneUsers']:
        rc, out = host.run(['id', '-u', name])
        if rc == 0 and out.strip().isdigit():
            uids[name] = int(out.strip())
    exempt = set((selection or {}).get('exemptCgroups', []))
    if self_cg:
        exempt.add(self_cg.rstrip('/'))
    service_cgroups = set()
    services = []
    for role, user, unit in POLICY['serviceUnits']:
        uid = uids.get(user)
        cg = f'/user.slice/user-{uid}.slice/user@{uid}.service/app.slice/{unit}' if uid is not None else None
        present = bool(cg and (host.cgroot / cg.lstrip('/')).is_dir())
        services.append({'role': role, 'user': user, 'uid': uid, 'unit': unit, 'cgroup': cg, 'present': present})
        if cg:
            service_cgroups.add(cg)
    for path in sc2:
        m = re.match(r'/user\.slice/user-(\d+)\.slice/user@\d+\.service/', path)
        services.append({'role': 'sc2', 'user': None, 'uid': int(m.group(1)) if m else None,
                         'unit': path.rsplit('/', 1)[-1], 'cgroup': path, 'present': True,
                         'manager': 'user' if m else 'system'})
        service_cgroups.add(path)
    facts['services'] = services
    launcher_tree = set(ancestors(host, os.getpid()))
    lanes, exempt_units = [], []
    for unit in lane_units(host, sorted(set(uids.values()))):
        rel = cg_rel(host, unit)
        if rel in service_cgroups:
            continue
        pids = cgroup_procs(host, unit)
        comms = sorted({comm_of(host, p) for p in pids})
        # logind session scopes belong to the system manager; the rest to the user's manager
        manager = 'system' if '.service/' not in rel else 'user'
        row = {'cgroup': rel, 'unit': unit.name, 'uid': _uid_of(rel), 'manager': manager, 'procs': len(pids),
               'comms': comms}
        if rel in exempt:
            row['workload'] = sorted({comm_of(host, p) for p in pids if p not in launcher_tree
                                      and comm_of(host, p) not in POLICY['exemptAllowedComms'] + POLICY['idleComms']})
            exempt_units.append(row)
        else:
            row['busy'] = sorted(c for c in comms if c not in POLICY['idleComms'])
            lanes.append(row)
    facts['lanes'] = {'users': uids, 'lanes': lanes, 'exempt': exempt_units, 'exemptDeclared': sorted(exempt)}

    # Current io.max on every cap target (stale caps from an earlier window refuse).
    io_now = {}
    for row in lanes + [s for s in services if s['cgroup']]:
        path = host.cgroot / row['cgroup'].lstrip('/') / 'io.max'
        io_now[row['cgroup']] = _try(lambda: host.read(path).strip().splitlines(), []) if path.exists() else []
    facts['ioMax'] = io_now
    facts['ioStat'] = _try(lambda: host.read('/sys/fs/cgroup/io.stat').strip().splitlines())
    facts['ioPressure'] = _try(lambda: host.read('/proc/pressure/io').strip().splitlines())

    # Writers on the pool (needs root to see other users' descriptors).
    if privileged_census:
        rc, out = host.run(['sudo', '-n', sys.executable, '-I', '-B', str(Path(__file__).resolve()), 'census',
                            '--mount', pool], timeout=120)
        facts['poolWriters'] = _try(lambda: json.loads(out)) if rc == 0 else {'status': 'unknown',
                                                                               'reason': 'privileged census failed'}
    else:
        facts['poolWriters'] = open_file_census(pool)
    rc, out = host.run(['dotnet', '--list-runtimes'])
    facts['dotnetRuntimes'] = sorted(set(re.findall(r'Microsoft\.NETCore\.App (\S+)', out))) if rc == 0 else None
    if selection:
        facts['claimExists'] = os.path.lexists(Path(POLICY['claimsDir']) / selection['operationId'])
    return facts


def _uid_of(cgroup):
    m = re.match(r'/user\.slice/user-(\d+)\.slice', cgroup)
    return int(m.group(1)) if m else None


def open_file_census(mount):
    """Processes holding files under `mount`. Unreadable processes make the result Unknown."""
    mount = os.path.realpath(mount)
    holders, unreadable = [], 0
    for entry in os.listdir('/proc'):
        if not entry.isdigit() or int(entry) == os.getpid():
            continue
        try:
            targets = [os.readlink(f'/proc/{entry}/cwd')]
            for fd in os.listdir(f'/proc/{entry}/fd'):
                try:
                    targets.append(os.readlink(f'/proc/{entry}/fd/{fd}'))
                except OSError:
                    pass
        except PermissionError:
            unreadable += 1; continue
        except (FileNotFoundError, ProcessLookupError):
            continue
        if any(t == mount or t.startswith(mount + '/') for t in targets):
            holders.append({'pid': int(entry), 'comm': _try(lambda: Path(f'/proc/{entry}/comm').read_text().strip())})
    status = 'found' if holders else ('unknown' if unreadable else 'none')
    return {'status': status, 'holders': holders, 'unreadableProcesses': unreadable}


# ---------------------------------------------------------------------------
# Checks: pure functions over facts (+ selection, + supplied inputs).
# ---------------------------------------------------------------------------
def row(name, status, observed, reason=''):
    return {'check': name, 'status': status, 'observed': observed, 'reason': reason}


def _pool_need(selection):
    if not selection:
        return POLICY['defaultPoolNeedBytes']
    return POLICY['guest']['rootDiskBytes'] + 24 * GIB


def selection_marker(op, step):
    return f'<!-- fsgg:fdev-native-selection/v1 operation={op} step={step} -->'


def check_selection(selection, comment):
    if selection is None:
        return [row('selection.valid', UNKNOWN, None, 'no Root selection supplied; launch requires one'),
                row('selection.operation-not-retired', UNKNOWN, None, 'no selection'),
                row('selection.root-comment-binds-operation', UNKNOWN, None, 'no selection')]
    rows = []
    try:
        validate_selection(selection, caps=False, operation=False)
        rows.append(row('selection.valid', SUPPORTED, selection.get('step')))
    except Refusal as refusal:
        rows.append(row('selection.valid', UNSUPPORTED, None, f'{refusal.check}: {refusal.reason}'))
    try:
        validate_operation_id(selection.get('operationId') if isinstance(selection, dict) else None)
        rows.append(row('selection.operation-not-retired', SUPPORTED, selection['operationId']))
    except Refusal as refusal:
        rows.append(row('selection.operation-not-retired', UNSUPPORTED, selection.get('operationId')
                        if isinstance(selection, dict) else None, refusal.reason))
    if rows[0]['status'] != SUPPORTED or rows[1]['status'] != SUPPORTED:
        return rows + [row('selection.root-comment-binds-operation', UNKNOWN, None, 'selection invalid')]
    if comment is None:
        rows.append(row('selection.root-comment-binds-operation', UNKNOWN, None, 'Root selection comment not read'))
    else:
        # Cooperative binding: an allowed Root account's comment on #4778 carrying the selection
        # marker. Posts made with the same account are indistinguishable, so the marker (which the
        # launcher's own start-witness comment never contains) is what separates a selection.
        marker = selection_marker(selection['operationId'], selection['step'])
        ok = (comment.get('author') in POLICY['rootCommentAuthors']
              and comment.get('issueUrl', '').endswith('/FS-GG/.github/issues/4778')
              and marker in comment.get('body', ''))
        rows.append(row('selection.root-comment-binds-operation', SUPPORTED if ok else UNSUPPORTED,
                        {'author': comment.get('author'), 'issueUrl': comment.get('issueUrl')},
                        '' if ok else f'no comment by an allowed Root account on #4778 carries {marker}'))
    return rows


def evaluate(facts, selection=None, comment=None, host_df=None, now=None, image=None, host_mem=None, mem_floor=None,
             mem_max_age=None):
    """Every precondition, in order. Returns rows; nothing is changed."""
    rows = check_selection(selection, comment)
    if selection is not None and facts.get('claimExists') is not None:
        rows.append(row('claim.absent', UNSUPPORTED if facts['claimExists'] else SUPPORTED, facts['claimExists'],
                        'an existing claim refuses forever' if facts['claimExists'] else ''))
    else:
        rows.append(row('claim.absent', UNKNOWN, None, 'no selection'))
    kvm = facts.get('kvm') or {}
    rows.append(row('kvm.device', SUPPORTED if kvm.get('present') and kvm.get('charDevice') and kvm.get('rw')
                    else UNSUPPORTED, kvm))
    rows.append(row('kvm.nested', SUPPORTED if kvm.get('nested') in ('1', 'Y') else
                    (UNKNOWN if kvm.get('nested') is None else UNSUPPORTED), kvm.get('nested')))
    cg = facts.get('cgroup') or {}
    sub = cg.get('rootSubtree')
    rows.append(row('cgroup.v2-controllers', UNKNOWN if sub is None else (
        SUPPORTED if cg.get('v2') and {'cpuset', 'cpu', 'io', 'memory', 'pids'} <= set(sub) else UNSUPPORTED), sub))
    cpus = _cpu_set(cg.get('cpusEffective'))
    allowed = _cpu_set(POLICY['slice']['AllowedCPUs'])
    rows.append(row('cgroup.cpuset-cpus', UNKNOWN if cpus is None else (SUPPORTED if allowed <= cpus else UNSUPPORTED),
                    cg.get('cpusEffective'), f'slice AllowedCPUs={POLICY["slice"]["AllowedCPUs"]}'))
    um = cg.get('userManagerControllers')
    rows.append(row('cgroup.user-io-delegated', UNKNOWN if um is None else (SUPPORTED if 'io' in um else UNSUPPORTED),
                    um, 'lane units are capped through the user manager'))
    version = facts.get('systemdVersion')
    rows.append(row('systemd.version', UNKNOWN if version is None else (
        SUPPORTED if version >= POLICY['systemdMinVersion'] else UNSUPPORTED), version, 'ExitType=cgroup needs 250+'))
    tools = facts.get('tools') or {}
    missing = sorted(t for t in POLICY['tools'] if not tools.get(t))
    rows.append(row('tools.present', SUPPORTED if not missing else UNSUPPORTED, tools, f'missing {missing}' if missing else ''))
    sudo = facts.get('sudo')
    rows.append(row('privilege.sudo-noninteractive', UNKNOWN if sudo is None else (SUPPORTED if sudo else UNSUPPORTED),
                    sudo, 'cooperative hygiene only; used for system units and other users\' managers'))
    rows += check_exclusivity(facts)
    mem = facts.get('memAvailableBytes')
    rows.append(row('memory.available', UNKNOWN if mem is None else (
        SUPPORTED if mem >= POLICY['memAvailableMinBytes'] else UNSUPPORTED), mem,
        f'MemAvailable >= {POLICY["memAvailableMinBytes"]} (26 GiB) at pre-start'))
    pool = facts.get('pool') or {}
    rows.append(row('pool.mounted-identity', SUPPORTED if pool.get('mounted') and pool.get('label') == POLICY[
        'poolLabel'] else UNSUPPORTED, {k: pool.get(k) for k in ('label', 'uuid', 'source', 'device')}))
    need = _pool_need(selection) + POLICY['poolHeadroomBytes']
    free = pool.get('freeBytes')
    rows.append(row('pool.free-space', UNKNOWN if free is None else (SUPPORTED if free >= need else UNSUPPORTED),
                    {'freeBytes': free, 'needBytes': need}, 'run need plus 64 GiB headroom'))
    writers = facts.get('poolWriters') or {'status': 'unknown'}
    rows.append(row('pool.no-data-writers', {'none': SUPPORTED, 'found': UNSUPPORTED}.get(writers.get('status'), UNKNOWN),
                    writers, 'any writer found at admission refuses the run'))
    commit = facts.get('commitRoot') or {}
    if commit.get('exists'):
        ok = commit.get('dir') and commit.get('mode') == 0o700 and commit.get('uid') == os.getuid()
        rows.append(row('commit-root.ready', SUPPORTED if ok else UNSUPPORTED, commit, 'mode 0700, owner developer'))
    else:
        rows.append(row('commit-root.ready', SUPPORTED if commit.get('parentWritable') else UNSUPPORTED, commit,
                        'absent; launch creates it 0700' if commit.get('parentWritable') else 'absent and parent not writable'))
    hdd = facts.get('hddDevices') or {}
    unresolved = sorted(m for m in POLICY['hddMounts'] if not hdd.get(m))
    rows.append(row('io.hdd-devices-resolved', SUPPORTED if not unresolved else UNSUPPORTED, hdd,
                    f'unresolved {unresolved}' if unresolved else 'whole-disk major:minor per HDD-backed mount'))
    if selection is None:
        rows.append(row('io.caps-bound', UNKNOWN, None, 'Root binds rbps/wbps/riops/wiops per window'))
    else:
        try:
            rows.append(row('io.caps-bound', SUPPORTED, validate_io_caps(selection.get('ioCaps'))))
        except Refusal as refusal:
            rows.append(row('io.caps-bound', UNSUPPORTED, selection.get('ioCaps'), refusal.reason))
    devices = {d for d in hdd.values() if d}
    stale = {cg: lines for cg, lines in (facts.get('ioMax') or {}).items()
             for line in lines if line.split()[0] in devices and any(not f.endswith('=max') for f in line.split()[1:])}
    rows.append(row('io.no-stale-caps', SUPPORTED if not stale else UNSUPPORTED, stale or None,
                    'a cap left by an earlier window must be removed (window-exit) first' if stale else ''))
    services = facts.get('services') or []
    unresolved_services = [s['unit'] for s in services if s.get('uid') is None and s.get('manager') != 'system']
    rows.append(row('services.cap-targets-resolved', SUPPORTED if not unresolved_services else UNSUPPORTED,
                    [{k: s.get(k) for k in ('role', 'unit', 'present')} for s in services],
                    f'unresolved {unresolved_services}' if unresolved_services else
                    'present units are capped; absent ones get a runtime drop-in that applies at start'))
    if host_df is None:
        rows.append(row('capacity.host-partition', UNKNOWN, None, 'Home df reading not supplied (input, never guessed)'))
    else:
        try:
            max_age = (selection or {}).get('hostPartitionMaxAgeSeconds', host_df.get('_maxAge'))
            reading = validate_host_partition(host_df, now, max_age)
            enough = reading['avail'] >= _pool_need(selection) + 64 * GIB and reading['iavail'] >= 1_000_000
            rows.append(row('capacity.host-partition', SUPPORTED if enough else UNSUPPORTED, reading))
        except Refusal as refusal:
            rows.append(row('capacity.host-partition', UNSUPPORTED, None, refusal.reason))
    floor = (selection or {}).get('hostMemoryMinAvailableBytes', mem_floor)
    max_age = (selection or {}).get('hostMemoryMaxAgeSeconds', mem_max_age)
    if host_mem is None:
        rows.append(row('capacity.host-memory', UNKNOWN, None, 'Home host-memory reading not supplied; never inferred'))
    elif floor is None:
        rows.append(row('capacity.host-memory', UNKNOWN, None, 'no Root-set host MemAvailable floor bound'))
    else:
        try:
            rows.append(row('capacity.host-memory', SUPPORTED, validate_host_memory(host_mem, now, max_age, floor),
                            f'host MemAvailable >= Root floor {floor}'))
        except Refusal as refusal:
            rows.append(row('capacity.host-memory', UNSUPPORTED, None, refusal.reason))
    if image is None:
        rows.append(row('image.stock-digest', UNKNOWN, None, 'stock image not hashed in this run'))
    else:
        rows.append(row('image.stock-digest', SUPPORTED if image.get('match') else UNSUPPORTED, image))
    runtimes = facts.get('dotnetRuntimes')
    rows.append(row('dotnet.runtime', UNKNOWN if runtimes is None else (
        SUPPORTED if POLICY['dotnetRuntime'] in runtimes else UNSUPPORTED), runtimes, 'M1 prerequisite: 10.0.12'))
    return rows


def check_exclusivity(facts):
    rows = []
    lanes = (facts.get('lanes') or {})
    busy = [{'cgroup': l['cgroup'], 'busy': l['busy']} for l in lanes.get('lanes', []) if l.get('busy')]
    declared = lanes.get('exemptDeclared') or []
    rows.append(row('exclusive.lanes-drained', UNKNOWN if 'lanes' not in facts else (
        SUPPORTED if not busy else UNSUPPORTED), busy or len(lanes.get('lanes', [])),
        'non-exempt lane units may hold only idle shells' if busy else ''))
    noisy = [{'cgroup': e['cgroup'], 'workload': e['workload']} for e in lanes.get('exempt', []) if e.get('workload')]
    rows.append(row('exclusive.exempt-sessions-quiet', UNKNOWN if 'lanes' not in facts else (
        SUPPORTED if not noisy else UNSUPPORTED), noisy or declared,
        'build servers or compilers in an exempt session are lane work' if noisy else
        'in-process subagent work is not cgroup-observable; Root attests the drain'))
    units = facts.get('units') or {}
    native = units.get('native')
    rows.append(row('exclusive.no-other-native-unit', UNKNOWN if native is None or not units.get('nativeListingOk')
                    else (SUPPORTED if not native else UNSUPPORTED), native))
    pilots = units.get('v3PilotActive')
    rows.append(row('exclusive.no-v3-pilot', UNKNOWN if pilots is None else (SUPPORTED if not pilots else UNSUPPORTED),
                    pilots))
    return rows


EXCLUSIVITY_CHECKS = ('exclusive.lanes-drained', 'exclusive.exempt-sessions-quiet', 'exclusive.no-other-native-unit',
                      'exclusive.no-v3-pilot')


def shared_codex_host(rows):
    """Root ruling: false only when every exclusivity check passes."""
    by = {r['check']: r['status'] for r in rows}
    return not all(by.get(name) == SUPPORTED for name in EXCLUSIVITY_CHECKS)


def _cpu_set(text):
    if not isinstance(text, str) or not text.strip():
        return None
    result = set()
    for part in text.strip().split(','):
        lo, _, hi = part.partition('-')
        if not lo.isdigit() or (hi and not hi.isdigit()):
            return None
        result |= set(range(int(lo), int(hi or lo) + 1))
    return result


def refuse_unless_all_supported(rows):
    for r in rows:
        require(r['status'] == SUPPORTED, r['check'], f'{r["status"]}: {r["reason"] or r["observed"]}')


# ---------------------------------------------------------------------------
# Clock arithmetic and GitHub real-time witnesses.
# ---------------------------------------------------------------------------
def parse_http_date(value):
    require(isinstance(value, str), 'clock.github-date', 'Date header required')
    moment = email.utils.parsedate_to_datetime(value)
    require(moment.tzinfo is not None and moment.utcoffset() == dt.timedelta(0), 'clock.github-date',
            'Date header must be GMT')
    return moment


def check_pre_start(claim_monotonic, now_monotonic, clocks):
    elapsed = now_monotonic - claim_monotonic
    require(0 <= elapsed <= clocks['preStartSeconds'], 'clock.pre-start',
            f'{elapsed:.1f}s since the claim exceeds the {clocks["preStartSeconds"]}s pre-start cut')
    return elapsed


def check_enclosing(start_witness, github_date, clocks, reserve_seconds=0):
    """Refuse the next step if the enclosing bound (from the start witness) has passed
    or cannot hold the next step's reserved clocks."""
    elapsed = (github_date - start_witness).total_seconds()
    require(elapsed >= 0, 'clock.enclosing', 'GitHub Date precedes the start witness')
    require(elapsed + reserve_seconds <= clocks['enclosingSeconds'], 'clock.enclosing',
            f'{elapsed:.0f}s elapsed + {reserve_seconds}s reserved exceeds the enclosing {clocks["enclosingSeconds"]}s')
    return elapsed


def realtime_verdict(start_witness, end_witness, clocks):
    elapsed = (end_witness - start_witness).total_seconds()
    limit = clocks['enclosingSeconds'] + POLICY['postingAllowanceSeconds']
    return {'elapsedSeconds': elapsed, 'limitSeconds': limit, 'exceeded': elapsed > limit,
            'acceptableAsQualification': elapsed <= limit}


# ---------------------------------------------------------------------------
# Builders (pure): unit names, io.max targets, systemd-run argv, QEMU argv, target.
# ---------------------------------------------------------------------------
def unit_names(op, step):
    base = f'fsgg-native-{op}-{step}'
    return {'launcher': f'{base}-launcher.scope', 'guest': f'{base}.service', 'collector': f'{base}-collect.service'}


def cap_targets(facts):
    """Drained lane units plus the store, LEARN and SC2 service units. Never a whole slice,
    never an exempt session, never the launcher."""
    targets = []
    user_names = {uid: name for name, uid in (facts.get('lanes') or {}).get('users', {}).items()}
    for lane in (facts.get('lanes') or {}).get('lanes', []):
        system = lane.get('manager') == 'system' or '.service/' not in lane['cgroup']
        targets.append({'role': 'lane', 'uid': None if system else lane['uid'],
                        'user': None if system else user_names.get(lane['uid']),
                        'unit': lane['unit'], 'cgroup': lane['cgroup']})
    for service in facts.get('services') or []:
        if service.get('manager') == 'system':
            targets.append({'role': service['role'], 'uid': None, 'user': None, 'unit': service['unit'],
                            'cgroup': service['cgroup']})
        elif service.get('uid') is not None:
            targets.append({'role': service['role'], 'uid': service['uid'], 'user': service.get('user') or
                            user_names.get(service['uid']), 'unit': service['unit'], 'cgroup': service['cgroup']})
    exempt = set((facts.get('lanes') or {}).get('exemptDeclared', []))
    for target in targets:
        require(target['cgroup'] not in exempt, 'io.cap-scope', f'{target["cgroup"]} is exempt and stays uncapped')
        require(not target['unit'].endswith('.slice'), 'io.cap-scope', 'a whole slice is never capped')
    return targets


IO_PROPS = (('IOReadBandwidthMax', 'rbps'), ('IOWriteBandwidthMax', 'wbps'), ('IOReadIOPSMax', 'riops'),
            ('IOWriteIOPSMax', 'wiops'))


def set_property_argv(target, assignments, my_uid):
    if target['uid'] is None:
        prefix = ['sudo', '-n', 'systemctl']
    elif target['uid'] == my_uid:
        prefix = ['systemctl', '--user']
    else:
        require(target.get('user'), 'io.cap-scope', f'no user name for uid {target["uid"]}')
        prefix = ['sudo', '-n', 'systemctl', '--user', f'--machine={target["user"]}@.host']
    return prefix + ['set-property', '--runtime', target['unit']] + assignments


def cap_apply_argv(target, devices, caps, my_uid):
    assignments = [f'{prop}=/dev/block/{dev} {caps[key]}' for prop, key in IO_PROPS for dev in sorted(devices)]
    return set_property_argv(target, assignments, my_uid)


def cap_remove_argv(target, my_uid):
    return set_property_argv(target, [f'{prop}=' for prop, _ in IO_PROPS], my_uid)


def expected_io_max(devices, caps):
    return {dev: f'{dev} rbps={caps["rbps"]} wbps={caps["wbps"]} riops={caps["riops"]} wiops={caps["wiops"]}'
            for dev in devices}


def slice_argv(remove=False):
    s, c = POLICY['slice'], POLICY['collectSlice']
    if remove:
        return [['sudo', '-n', 'systemctl', 'revert', s['name']], ['sudo', '-n', 'systemctl', 'revert', c['name']]]
    return [['sudo', '-n', 'systemctl', 'set-property', '--runtime', s['name'], f'AllowedCPUs={s["AllowedCPUs"]}',
             f'MemoryMin={s["MemoryMin"]}', f'MemoryMax={s["MemoryMax"]}', f'TasksMax={s["TasksMax"]}'],
            ['sudo', '-n', 'systemctl', 'set-property', '--runtime', c['name'], f'MemoryMax={c["MemoryMax"]}',
             f'TasksMax={c["TasksMax"]}']]


def launcher_scope_argv(selection, argv_tail):
    """The wrapper Root runs: the launcher front end bounded in its own user scope."""
    runtime = scope_runtime_seconds(selection['clocks'])
    name = unit_names(selection['operationId'], selection['step'])['launcher']
    return ['systemd-run', '--user', '--scope', f'--unit={name}', '-p', f'RuntimeMaxSec={runtime}',
            '-p', f'TimeoutStopSec={scope_stop_seconds(selection["clocks"])}',
            '-p', f'MemoryMax={POLICY["launcherScope"]["MemoryMax"]}', '-p',
            f'TasksMax={POLICY["launcherScope"]["TasksMax"]}', '--'] + argv_tail


def blockdev(node, path, read_only, driver='raw'):
    return json.dumps({'driver': driver, 'node-name': node, 'read-only': read_only, 'discard': 'ignore',
                       'detect-zeroes': 'off', 'file': {'driver': 'file', 'filename': path, 'aio': 'native',
                                                         'cache': {'direct': True, 'no-flush': False},
                                                         'read-only': read_only}}, sort_keys=True, separators=(',', ':'))


def qemu_argv(run_dir, guest_uuid, read_only_disks, seed_dir, name):
    g = POLICY['guest']
    argv = ['/usr/bin/qemu-system-x86_64', '-name', f'{name},debug-threads=on', '-nodefaults', '-no-user-config',
            '-machine', 'q35,accel=kvm', '-cpu', 'host', '-smp', str(g['vcpu']),
            '-m', f'{g["memoryBytes"] // 1024 ** 2}M', '-mem-prealloc', '-uuid', guest_uuid,
            '-display', 'none', '-nic', 'none', '-serial', f'file:{run_dir}/serial.log',
            '-qmp', f'unix:{run_dir}/qmp.sock,server=on,wait=off',
            '-blockdev', blockdev('root', f'{run_dir}/root.raw', False),
            '-device', 'virtio-blk-pci,drive=root,bootindex=0']
    for index, disk in enumerate(read_only_disks):
        argv += ['-blockdev', blockdev(f'ro{index}', disk['path'], True),
                 '-device', f'virtio-blk-pci,drive=ro{index}']
    if seed_dir:
        argv += ['-blockdev', json.dumps({'driver': 'vvfat', 'node-name': 'seed', 'dir': seed_dir, 'label': 'CIDATA',
                                          'read-only': True}, sort_keys=True, separators=(',', ':')),
                 '-device', 'virtio-blk-pci,drive=seed']
    return argv


def guest_unit_argv(selection, run_dir, guest_uuid):
    op, step, clocks = selection['operationId'], selection['step'], selection['clocks']
    name = unit_names(op, step)['guest']
    stop = f'{sys.executable} -I -B {Path(__file__).resolve()} qmp-powerdown --socket {run_dir}/qmp.sock --wait-pid $MAINPID'
    props = ['Type=exec', 'ExitType=cgroup', 'RemainAfterExit=yes', 'Restart=no', 'CollectMode=inactive',
             'OOMPolicy=kill', 'KillMode=control-group', f'RuntimeMaxSec={clocks["usefulSeconds"]}',
             f'TimeoutStopSec={clocks["retireSeconds"]}', f'MemoryMax={POLICY["slice"]["MemoryMax"]}', 'TasksMax=256',
             f'WorkingDirectory={run_dir}', f'ExecStop={stop}',
             f'Description=fsgg native {step} guest for operation {op} (cooperative custody)']
    argv = ['sudo', '-n', 'systemd-run', '--system', f'--unit={name}', f'--slice={POLICY["slice"]["name"]}',
            f'--uid={os.getuid()}', f'--gid={os.getgid()}', '--no-block']
    for prop in props:
        argv += ['-p', prop]
    return argv + ['--'] + qemu_argv(run_dir, guest_uuid, selection.get('readOnlyDisks', []),
                                     selection.get('seedDir'), name.removesuffix('.service'))


def collector_unit_argv(selection, run_dir):
    op, step, clocks = selection['operationId'], selection['step'], selection['clocks']
    names = unit_names(op, step)
    props = ['Type=exec', 'RemainAfterExit=yes', 'Restart=no', 'CollectMode=inactive', 'OOMPolicy=kill',
             f'RuntimeMaxSec={clocks["collectorSeconds"]}', f'TimeoutStopSec={clocks["collectorRetireSeconds"]}',
             f'MemoryMax={POLICY["collectSlice"]["MemoryMax"]}', 'TasksMax=64', f'WorkingDirectory={run_dir}',
             f'Description=fsgg native {step} collector for operation {op}']
    argv = ['sudo', '-n', 'systemd-run', '--system', f'--unit={names["collector"]}',
            f'--slice={POLICY["collectSlice"]["name"]}', f'--uid={os.getuid()}', f'--gid={os.getgid()}', '--no-block']
    for prop in props:
        argv += ['-p', prop]
    return argv + ['--', sys.executable, '-I', '-B', str(Path(__file__).resolve()), 'collect', '--run-dir', run_dir,
                   '--guest-unit', names['guest'], '--operation', op]


def disk_prep_argv(run_dir, image_path):
    root = f'{run_dir}/root.raw'
    return [['fallocate', '-l', str(POLICY['guest']['rootDiskBytes']), root],
            ['qemu-img', 'convert', '-n', '--target-is-zero', '-f', 'qcow2', '-O', 'raw', image_path, root]]


def lock_image_sha(lock_relpath):
    lock = load_json(REPO / lock_relpath)
    digest = lock.get('image', {}).get('sha256')
    require(isinstance(digest, str) and SHA_PATTERN.fullmatch(digest) is not None, 'image.stock-digest',
            f'{lock_relpath} carries no image sha256 pin')
    return digest


def build_target(selection, rows, guest_uuid, operator_acceptance_sha256):
    """TARGET.json for the guest (fsgg.dedicated-qualification-target/1), stated truthfully."""
    op = selection['operationId']
    return {'schema': 'fsgg.dedicated-qualification-target/1', 'baselineId': 'ubuntu2404-linux-x64-v1',
            'provider': POLICY['provider'], 'providerInstanceId': f'{POLICY["provider"]}/{op}/{selection["step"]}',
            'providerImageId': f'{POLICY["provider"]}/{op}', 'systemUuid': guest_uuid,
            'operatorAcceptanceSha256': operator_acceptance_sha256, 'dedicated': True, 'disposable': True,
            'sharedCodexHost': shared_codex_host(rows), 'hostKind': POLICY['hostKind'],
            'ioSharedWithHostCoTenants': True, 'architecture': 'x86_64', 'os': {'id': 'ubuntu', 'version': '24.04'},
            'resources': {'vcpu': POLICY['guest']['vcpu'], 'memoryBytes': POLICY['guest']['memoryBytes'],
                          'diskBytes': POLICY['guest']['rootDiskBytes']}}


# ---------------------------------------------------------------------------
# Evidence schema. Never claims independence; I/O correspondence is never asserted.
# ---------------------------------------------------------------------------
EVIDENCE_SCHEMA = 'fsgg.fdev-native-window-evidence/1'
IO_NOT_ESTABLISHED = 'not-established'
IO_CAPTURED = 'window-evidence-captured-awaiting-root'


def validate_home_io(doc, window):
    require(isinstance(doc, dict) and doc.get('schema') == 'fsgg.fdev-native-home-io-evidence/1', 'io.home-evidence',
            'fsgg.fdev-native-home-io-evidence/1 required')
    require(doc.get('observedBy') == 'Home', 'io.home-evidence', 'must be observed by Home, not by fdev')
    require(isinstance(doc.get('source'), str) and doc['source'].startswith('https://github.com/FS-GG/'),
            'io.home-evidence', 'source must cite the Home post')
    start, end = parse_utc(doc.get('observedFrom'), 'io.home-evidence'), parse_utc(doc.get('observedTo'), 'io.home-evidence')
    require(window.get('enteredAt') and window.get('exitedAt'), 'io.home-evidence',
            'the window has no recorded entry and exit, so no observation can cover it')
    require(start <= parse_utc(window['enteredAt'], 'io.home-evidence')
            and end >= parse_utc(window['exitedAt'], 'io.home-evidence'), 'io.home-evidence',
            'Home observation must cover the whole window (entry to exit)')
    iostat = doc.get('iostat', {})
    require(iostat.get('command', '').startswith('iostat -dx') and type(iostat.get('samples')) is int and
            iostat['samples'] > 0 and SHA_PATTERN.fullmatch(str(iostat.get('rawSha256'))) is not None,
            'io.home-evidence', 'iostat -dx samples with a raw-capture sha256 required')
    controls = doc.get('controls', {})
    require(controls.get('scheduledWriterStopped') is True and controls.get('interactiveWorkloadAbsent') is True,
            'io.home-evidence', 'Home must state the scheduled writer stopped and no interactive workload')
    return doc


def io_correspondence(window, home_io):
    reasons = []
    if not window.get('capsApplied', {}).get('readbackOk'):
        reasons.append('caps not read back at entry')
    if not window.get('capsRemoved', {}).get('readbackOk') or window.get('capsRemoved', {}).get('errors'):
        reasons.append('caps not read back as removed at exit')
    if not (window.get('ioStatBefore') and window.get('ioStatAfter') and window.get('psiBefore') and window.get('psiAfter')):
        reasons.append('fdev io.stat/PSI not captured at both ends')
    if home_io is None:
        reasons.append('no Home-observed window evidence supplied')
    else:
        try:
            validate_home_io(home_io, window)
        except Refusal as refusal:
            reasons.append(refusal.reason)
    status = IO_CAPTURED if not reasons else IO_NOT_ESTABLISHED
    return {'status': status, 'reasons': reasons, 'acceptedByRoot': False,
            'homeEvidence': (home_io or {}).get('source') if status == IO_CAPTURED else None,
            'note': 'guest PSI or timing alone is never accepted; Root decides satisfaction from captured evidence'}


def build_evidence(selection, rows, window, home_io=None):
    return {'schema': EVIDENCE_SCHEMA, 'operationId': selection['operationId'], 'step': selection['step'],
            'owningIssue': POLICY['owningIssue'], 'hostKind': POLICY['hostKind'],
            'sharedCodexHost': window_shared(rows, window), 'ioSharedWithHostCoTenants': True,
            'independence': {'fromFdev': False, 'fromHome': False},
            'custody': 'cooperative', 'tamperProof': False,
            'preflight': rows, 'window': window, 'ioCorrespondence': io_correspondence(window, home_io)}


def validate_evidence(doc):
    require(doc.get('schema') == EVIDENCE_SCHEMA, 'evidence.schema', EVIDENCE_SCHEMA + ' required')
    validate_operation_id(doc.get('operationId'))
    require(doc.get('hostKind') == POLICY['hostKind'], 'evidence.host-kind', 'hostKind fdev-agent-vm-drained-window')
    require(doc.get('ioSharedWithHostCoTenants') is True, 'evidence.io-shared', 'the HDD is shared with Home co-tenants')
    require(doc.get('independence') == {'fromFdev': False, 'fromHome': False}, 'evidence.independence',
            'evidence never claims independence from fdev or Home')
    require(doc.get('custody') == 'cooperative' and doc.get('tamperProof') is False, 'evidence.custody',
            'custody is cooperative and never tamper-proof')
    rows = doc.get('preflight')
    require(isinstance(rows, list) and rows, 'evidence.preflight', 'preflight rows required')
    require(isinstance(doc.get('window'), dict), 'evidence.window', 'window record required')
    require(doc.get('sharedCodexHost') is window_shared(rows, doc['window']), 'evidence.shared-codex-host',
            'sharedCodexHost must follow from the entry and exit exclusivity checks and new lane units')
    io = doc.get('ioCorrespondence', {})
    require(io.get('status') in (IO_NOT_ESTABLISHED, IO_CAPTURED), 'evidence.io-correspondence',
            'status is not-established or window-evidence-captured-awaiting-root; never established by fdev')
    require(io.get('acceptedByRoot') is False, 'evidence.io-correspondence', 'acceptance is Root\'s, not the launcher\'s')
    recomputed = io_correspondence(doc.get('window', {}), _home_from(doc))
    require(io['status'] == recomputed['status'], 'evidence.io-correspondence',
            'status does not follow from the captured window evidence')
    return doc


def _home_from(doc):
    return doc.get('_homeIoEvidence')


def attach_home_io(evidence, home_io):
    updated = dict(evidence)
    updated['_homeIoEvidence'] = home_io
    updated['ioCorrespondence'] = io_correspondence(evidence.get('window', {}), home_io)
    return validate_evidence(updated)


# ---------------------------------------------------------------------------
# Effects. Every write goes through an Executor, so dry-run provably writes nothing.
# ---------------------------------------------------------------------------
class Executor:
    def __init__(self, timeout=600):
        self.timeout = timeout

    def run(self, argv, effect, timeout=None):
        try:
            done = subprocess.run(argv, capture_output=True, timeout=timeout or self.timeout, check=False,
                                  stdin=subprocess.DEVNULL)
        except subprocess.TimeoutExpired:
            raise Refusal(f'effect.{effect}', f'timed out after {timeout or self.timeout}s') from None
        except OSError as error:
            raise Refusal(f'effect.{effect}', repr(error)) from None
        require(done.returncode == 0, f'effect.{effect}',
                f'exit {done.returncode}: {done.stderr.decode("utf-8", "replace")[-2000:]}')
        return done.stdout.decode('utf-8', 'replace')

    def create_claim(self, path, payload):
        os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
        try:
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        except FileExistsError:
            raise Refusal('claim.absent', f'{path} exists; an existing claim refuses forever') from None
        try:
            with os.fdopen(fd, 'w') as stream:
                json.dump(payload, stream, sort_keys=True); stream.write('\n')
                stream.flush(); os.fsync(stream.fileno())
        except Exception as error:
            # The claim file exists, so it refuses forever; the caller must leave a durable record of it.
            partial = Refusal('claim.partial', f'{path} was created but its payload write failed: {error!r}; '
                                               'the claim stands and refuses forever')
            partial.claim_created = True
            raise partial from error

    def mkdir(self, path, mode):
        os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
        os.mkdir(path, mode)

    def write(self, path, data, mode=0o600):
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
        with os.fdopen(fd, 'w') as stream:
            stream.write(data)


class PlanOnly(Executor):
    """Dry-run executor: records the plan and refuses to perform any effect."""

    def __init__(self):
        super().__init__()
        self.plan = []

    def run(self, argv, effect, timeout=None):
        self.plan.append({'effect': effect, 'argv': argv}); return ''

    def create_claim(self, path, payload):
        self.plan.append({'effect': 'claim', 'path': path})

    def mkdir(self, path, mode):
        self.plan.append({'effect': 'mkdir', 'path': path, 'mode': oct(mode)})

    def write(self, path, data, mode=0o600):
        self.plan.append({'effect': 'write', 'path': path, 'bytes': len(data)})


def read_io_max(host, cgroup):
    path = host.cgroot / cgroup.lstrip('/') / 'io.max'
    return host.read(path).strip().splitlines() if path.exists() else []


def readback_caps(host, targets, devices, caps):
    expected = expected_io_max(devices, caps)
    failures = []
    for target in targets:
        if not (host.cgroot / target['cgroup'].lstrip('/')).is_dir():
            continue  # not running: its runtime drop-in applies when it starts
        lines = {line.split()[0]: line for line in read_io_max(host, target['cgroup'])}
        for dev, line in expected.items():
            if lines.get(dev) != line:
                failures.append({'cgroup': target['cgroup'], 'device': dev, 'observed': lines.get(dev)})
    return failures


def readback_removed(host, targets, devices):
    failures = []
    for target in targets:
        if not (host.cgroot / target['cgroup'].lstrip('/')).is_dir():
            continue
        for line in read_io_max(host, target['cgroup']):
            fields = line.split()
            if fields[0] in devices and any(not f.endswith('=max') for f in fields[1:]):
                failures.append({'cgroup': target['cgroup'], 'line': line})
    return failures


def unit_show(host, unit, user=False):
    argv = ['systemctl'] + (['--user'] if user else []) + ['show', unit, '-p', 'ActiveState', '-p', 'SubState',
                                                           '-p', 'Result', '-p', 'ExecMainStatus', '-p', 'MainPID',
                                                           '-p', 'ActiveEnterTimestampMonotonic', '-p', 'ControlGroup',
                                                           '-p', 'RuntimeMaxUSec', '-p', 'MemoryMax', '-p', 'TasksMax']
    rc, out = host.run(argv)
    require(rc == 0, 'unit.show', f'systemctl show {unit} failed')
    return dict(line.split('=', 1) for line in out.splitlines() if '=' in line)


def terminal(props):
    return props.get('ActiveState') in ('inactive', 'failed') or props.get('SubState') == 'exited'


def parse_timespan_seconds(text):
    """Parse a systemd timespan as `systemctl show` prints it ('2h 31min 40s', '1d', or raw usec)."""
    require(isinstance(text, str) and text.strip() and text.strip() != 'infinity', 'scope.bounded',
            f'finite timespan required, got {text!r}')
    text = text.strip()
    if text.isdigit():
        return int(text) / 1e6
    units = {'us': 1e-6, 'usec': 1e-6, 'ms': 1e-3, 'msec': 1e-3, 's': 1, 'sec': 1, 'min': 60, 'm': 60,
             'h': 3600, 'hr': 3600, 'd': 86400, 'w': 604800}
    parts = re.findall(r'(\d+(?:\.\d+)?)\s*([a-z]+)', text)
    require(parts and re.fullmatch(r'(\s*\d+(?:\.\d+)?\s*[a-z]+)+\s*', text) is not None
            and all(unit in units for _, unit in parts), 'scope.bounded', f'unparsed timespan {text!r}')
    return sum(float(value) * units[unit] for value, unit in parts)


def internal_bound(clocks, n_targets):
    """Longest the front end can run on its abnormal path: entry effects within the pre-start cut,
    guest observation + 60 s then a bounded stop, collector observation + 60 s then a bounded stop,
    and window exit. The launcher scope's RuntimeMaxSec must be strictly larger, so the internal
    bounds always expire first and the window still exits."""
    entry = clocks['preStartSeconds'] + (2 * n_targets + 10) * EFFECT_TIMEOUT
    guest = clocks['usefulSeconds'] + clocks['retireSeconds'] + 60 + clocks['retireSeconds'] + EFFECT_TIMEOUT
    collector = (clocks['collectorSeconds'] + clocks['collectorRetireSeconds'] + 60 + clocks['collectorRetireSeconds']
                 + EFFECT_TIMEOUT)
    return entry + guest + collector + exit_bound(clocks, n_targets)


def exit_bound(clocks, n_targets):
    """Window exit: a bounded stop (and readback) of a guest and a collector not yet observed
    terminal, then every cap removal, both slice reverts and the record writes."""
    stops = clocks['retireSeconds'] + clocks['collectorRetireSeconds'] + 4 * EFFECT_TIMEOUT
    return stops + (n_targets + 6) * EFFECT_TIMEOUT


def scope_runtime_seconds(clocks):
    return internal_bound(clocks, POLICY['maxCapTargets']) + SCOPE_MARGIN_SECONDS


def scope_stop_seconds(clocks):
    """TimeoutStopSec for the launcher scope: after SIGTERM the window exit still completes."""
    return exit_bound(clocks, POLICY['maxCapTargets']) + SCOPE_MARGIN_SECONDS


def check_launcher_scope(host, selection, n_targets):
    """The front end must itself be bounded: its own user scope with finite limits whose
    RuntimeMaxSec strictly exceeds every internal bound on the abnormal path."""
    name = unit_names(selection['operationId'], selection['step'])['launcher']
    props = unit_show(host, name, user=True)
    require(props.get('ActiveState') == 'active', 'scope.bounded',
            f'run the launcher inside {name}: ' + ' '.join(launcher_scope_argv(selection, ['<launcher argv>'])))
    self_cg = host.read('/proc/self/cgroup').strip().split('::', 1)[1]
    require(self_cg.rstrip('/').endswith('/' + name), 'scope.bounded', f'launcher runs in {self_cg}, not {name}')
    for key in ('RuntimeMaxUSec', 'MemoryMax', 'TasksMax'):
        require(props.get(key) not in (None, '', 'infinity'), 'scope.bounded', f'{name} has no finite {key}')
    runtime = parse_timespan_seconds(props['RuntimeMaxUSec'])
    needed = scope_runtime_seconds(selection['clocks'])
    require(runtime >= needed and runtime > internal_bound(selection['clocks'], n_targets), 'scope.bounded',
            f'{name} RuntimeMaxSec {runtime:.0f}s is below the required {needed}s')
    return props


GONE_MARKERS = ('not found', 'not loaded', 'no such unit', 'does not exist')


def _gone(reason):
    return any(marker in reason.lower() for marker in GONE_MARKERS)


def remove_caps_all(host, executor, targets, devices, uid):
    """Attempt every removal. 'Unit not found' means already gone; any other error is kept per
    target. The kernel readback decides whether a cap is still present."""
    results = []
    for target in targets:
        entry = {'unit': target['unit'], 'cgroup': target['cgroup']}
        try:
            executor.run(cap_remove_argv(target, uid), 'io.cap-remove', timeout=EFFECT_TIMEOUT)
            entry['result'] = 'removed'
        except Exception as error:  # every target is attempted whatever happened to the previous one
            reason = error.reason if isinstance(error, Refusal) else repr(error)
            entry.update({'result': 'already-gone'} if _gone(reason) else {'result': 'error', 'error': reason})
        results.append(entry)
    failures = [] if isinstance(executor, PlanOnly) else readback_removed(host, targets, devices)
    errors = [r for r in results if r['result'] == 'error']
    # A removal error on a target whose cgroup is absent (a stopped unit) may leave its runtime
    # drop-in, which would cap it at its next start; the kernel cannot read that back.
    unconfirmed = [] if isinstance(executor, PlanOnly) else [
        r for r in errors if not (host.cgroot / r['cgroup'].lstrip('/')).is_dir()]
    return {'targets': results, 'errors': errors, 'failures': failures, 'unconfirmed': unconfirmed,
            'readbackOk': not failures, 'pending': bool(failures or unconfirmed), 'at': utc_text(host.now())}


def revert_slices(executor):
    errors = []
    for argv in slice_argv(remove=True):
        try:
            executor.run(argv, 'slice.revert', timeout=EFFECT_TIMEOUT)
        except Exception as error:
            reason = error.reason if isinstance(error, Refusal) else repr(error)
            if not _gone(reason):
                errors.append({'argv': argv, 'error': reason})
    return errors


def window_shared(rows, window):
    """Root ruling: sharedCodexHost is false only when the exclusivity checks pass at window entry
    AND at window exit, and no new lane unit appeared uncapped during the window."""
    if shared_codex_host(rows):
        return True
    exit_rows = window.get('exitExclusivity')
    if not exit_rows or shared_codex_host(exit_rows):
        return True
    return window.get('newUncappedUnitsDuringWindow') != []


def record_path(op):
    """Durable window record, outside the run directory (which may never be created)."""
    return f'{POLICY["claimsDir"]}/{op}.window.json'


def window_exit_directive(run_dir):
    return (f'{sys.executable} -I -B {Path(__file__).resolve()} window-exit --manifest {run_dir}/caps-manifest.json')


class Window:
    """One launch. Holds what must be undone, so every exit path removes the caps."""

    def __init__(self, host, executor, selection, facts, rows, targets, run_root=None, poll=5.0):
        self.host, self.ex, self.sel, self.facts, self.rows = host, executor, selection, facts, rows
        self.op, self.step = selection['operationId'], selection['step']
        self.names = unit_names(self.op, self.step)
        self.run_dir = f'{run_root or POLICY["runsDir"]}/{self.op}'
        self.devices = sorted({d for d in facts['hddDevices'].values() if d})
        self.targets = targets
        self.uid = os.getuid()
        self.poll = poll
        self.record = {'units': self.names, 'runDir': self.run_dir, 'devices': self.devices,
                       'capTargets': self.targets, 'caps': selection['ioCaps']}

    def apply_caps(self):
        self.ex.write(f'{self.run_dir}/caps-manifest.json', json.dumps(
            {'operationId': self.op, 'devices': self.devices, 'targets': self.targets, 'caps': self.sel['ioCaps']},
            sort_keys=True))
        self.record['capsPending'] = True
        for target in self.targets:
            self.ex.run(cap_apply_argv(target, self.devices, self.sel['ioCaps'], self.uid), 'io.cap-apply',
                        timeout=EFFECT_TIMEOUT)
        failures = [] if isinstance(self.ex, PlanOnly) else readback_caps(self.host, self.targets, self.devices,
                                                                         self.sel['ioCaps'])
        self.record['capsApplied'] = {'readbackOk': not failures, 'failures': failures,
                                      'at': utc_text(self.host.now())}
        require(not failures, 'io.cap-readback', f'io.max readback differs: {failures}')

    def remove_caps(self):
        removal = remove_caps_all(self.host, self.ex, self.targets, self.devices, self.uid)
        self.record['capsRemoved'] = removal
        self.record['capsPending'] = removal['pending']

    def observe_terminal(self, unit, bound_seconds, stop_seconds):
        if isinstance(self.ex, PlanOnly):
            return {'planned': True}
        deadline = self.host.monotonic() + bound_seconds
        while True:
            props = unit_show(self.host, unit)
            if terminal(props):
                cg = props.get('ControlGroup')
                events = _try(lambda: self.host.read(self.host.cgroot / cg.lstrip('/') / 'cgroup.events'), '') if cg else ''
                props['populated'] = 'populated 0' not in events if events else False
                return props
            if self.host.monotonic() >= deadline:
                self.ex.run(['sudo', '-n', 'systemctl', 'stop', unit], 'unit.stop-on-bound',
                            timeout=stop_seconds + EFFECT_TIMEOUT)
                props = unit_show(self.host, unit)
                props['boundExceeded'] = True
                return props
            time.sleep(self.poll)


def guest_outcome(guest):
    ok = (guest.get('Result') == 'success' and guest.get('ExecMainStatus') == '0'
          and not guest.get('boundExceeded'))
    return 'completed-awaiting-readback' if ok else 'guest-failed-awaiting-readback'


ENTRY_RECHECKS = ('memory.available', 'pool.free-space', 'capacity.host-partition', 'capacity.host-memory',
                  *EXCLUSIVITY_CHECKS)


def launch(host, executor, selection, facts, rows, run_root=None, github=None, poll=5.0, plan_ungated=False,
           reobserve=None):
    """The window. Refuses before any effect unless every precondition is Supported.

    `github` supplies the start witness and Date reads (injected in tests).
    `plan_ungated` lets a dry run list the full plan despite refusing rows; it is
    accepted only with the PlanOnly executor, which performs nothing.
    After the claim, every path (refusal, error, signal) runs window exit, writes the durable
    window record, and raises io.cap-remove-pending with a window-exit directive if a cap may remain."""
    require(not plan_ungated or isinstance(executor, PlanOnly), 'launch.gate', 'only a dry run may plan ungated')
    if not plan_ungated:
        refuse_unless_all_supported(rows)
        require(not shared_codex_host(rows), 'exclusive.window',
                'sharedCodexHost would be true; the window is not exclusive')
    targets = cap_targets(facts)
    require(len(targets) <= POLICY['maxCapTargets'], 'io.cap-scope',
            f'{len(targets)} cap targets exceed the bounded maximum {POLICY["maxCapTargets"]}')
    if not isinstance(executor, PlanOnly):
        check_launcher_scope(host, selection, len(targets))
    win = Window(host, executor, selection, facts, rows, targets, run_root, poll)
    clocks = selection['clocks']
    observe_now = reobserve or (lambda: observe(host, selection))
    # Disarmed from the claim until inside the try: a signal here is recorded, then raised in the try.
    SIGNALS['raise'], SIGNALS['pending'] = False, []
    try:
        executor.create_claim(f'{POLICY["claimsDir"]}/{win.op}', {
            'operationId': win.op, 'step': win.step, 'claimedAt': utc_text(host.now()),
            'bootId': _try(lambda: host.read('/proc/sys/kernel/random/boot_id').strip()), 'launcherPid': os.getpid()})
    except BaseException as error:
        # Stay disarmed through the record write: a signal here is recorded, never raised into the write.
        if getattr(error, 'claim_created', False):  # a partial claim exists: record it durably, nothing else
            win.record.update(outcome='refused-claim-partial', exitedAt=utc_text(host.now()),
                              firstFailure={'check': getattr(error, 'check', 'launch.unexpected'),
                                            'reason': getattr(error, 'reason', repr(error))})
            win.evidence = build_evidence(selection, rows, win.record)
            try:
                executor.write(record_path(win.op), json.dumps(win.evidence, indent=1, sort_keys=True))
                win.record_path = record_path(win.op)
            except Exception as write_error:
                win.record_path = None
                win.record['recordWriteError'] = repr(write_error)
            win.record['signalsDuringRecord'] = list(SIGNALS['pending'])
            error.window = win
        SIGNALS['raise'] = True
        raise  # a clean refusal (existing claim) changes nothing
    claim_mono = host.monotonic()
    win.record['outcome'] = 'refused'
    first = None
    try:
        try:
            SIGNALS['raise'] = True
            if SIGNALS['pending']:
                SIGNALS['raise'] = False
                raise Interrupted(SIGNALS['pending'].pop(0))
            _window_body(win, host, executor, selection, facts, github, clocks, claim_mono, observe_now)
        finally:
            SIGNALS['raise'] = False  # exit shield: no signal can raise from here until launch returns
    except Refusal as refusal:
        first = refusal
        win.record['firstFailure'] = {'check': refusal.check, 'reason': refusal.reason}
    except BaseException as error:  # signals arrive as Interrupted; the window still exits
        first = error
        check = 'launch.signal' if isinstance(error, Interrupted) else 'launch.unexpected'
        win.record['firstFailure'] = {'check': check, 'reason': repr(error)}
        if isinstance(error.__context__, Refusal):  # a signal that replaced a refusal keeps the refusal
            win.record['firstFailure']['replaced'] = {'check': error.__context__.check,
                                                      'reason': error.__context__.reason}
    return _window_exit_path(win, host, executor, selection, rows, clocks, observe_now, first)


def _window_body(win, host, executor, selection, facts, github, clocks, claim_mono, observe_now):
    start_witness = github.post_start_witness(win.op, win.step) if github else None
    win.record['startWitness'] = utc_text(start_witness) if start_witness else None
    entry_facts = facts
    if not isinstance(executor, PlanOnly):
        # Capacity and exclusivity re-read hook at window entry, after the claim.
        entry_facts = observe_now()
        again = evaluate(entry_facts, selection, None, selection['hostPartition'], host.now(),
                         host_mem=selection.get('hostMemory'))
        refuse_unless_all_supported([r for r in again if r['check'] in ENTRY_RECHECKS])
    executor.mkdir(win.run_dir, 0o700)
    win.record['enteredAt'] = utc_text(host.now())
    win.record['ioStatBefore'] = entry_facts.get('ioStat')
    win.record['psiBefore'] = entry_facts.get('ioPressure')
    win.apply_caps()
    win.record['slicesSet'] = True
    for argv in slice_argv():
        executor.run(argv, 'slice.properties', timeout=EFFECT_TIMEOUT)
    if not (facts.get('commitRoot') or {}).get('exists'):
        executor.mkdir(POLICY['commitRoot'], 0o700)
    image_path = selection['image']['path']
    expected = lock_image_sha(NOBLE_LOCK)
    if not isinstance(executor, PlanOnly):
        actual, _size = sha256_file(image_path)
        require(actual == expected, 'image.stock-digest', f'{image_path} sha256 {actual} != lock pin {expected}')
        for disk in selection.get('readOnlyDisks', []):
            got, _ = sha256_file(disk['path'])
            require(got == disk['sha256'], 'disk.digest', f'{disk["path"]} sha256 differs from the selection')
    win.record['image'] = {'path': image_path, 'sha256': expected}
    for argv in disk_prep_argv(win.run_dir, image_path):
        remaining = clocks['preStartSeconds'] - (host.monotonic() - claim_mono)
        require(remaining > 0, 'clock.pre-start', 'pre-start cut exhausted during disk preparation')
        executor.run(argv, 'disk.prepare', timeout=remaining)
    guest_uuid = str(uuidlib.uuid4())
    win.record['guestUuid'] = guest_uuid
    if not isinstance(executor, PlanOnly):
        check_pre_start(claim_mono, host.monotonic(), clocks)
        if github:
            reserve = clocks['usefulSeconds'] + clocks['retireSeconds'] + clocks['collectorSeconds'] + clocks[
                'collectorRetireSeconds']
            check_enclosing(start_witness, github.date(), clocks, reserve)
    win.record['guestStartAttempted'] = True  # before the call: a timeout may leave it running
    executor.run(guest_unit_argv(selection, win.run_dir, guest_uuid), 'unit.start-guest', timeout=EFFECT_TIMEOUT)
    win.record['guest'] = win.observe_terminal(win.names['guest'], clocks['usefulSeconds'] + clocks[
        'retireSeconds'] + 60, clocks['retireSeconds'])
    if github and not isinstance(executor, PlanOnly):
        check_enclosing(start_witness, github.date(), clocks,
                        clocks['collectorSeconds'] + clocks['collectorRetireSeconds'])
    win.record['collectorStartAttempted'] = True
    executor.run(collector_unit_argv(selection, win.run_dir), 'unit.start-collector', timeout=EFFECT_TIMEOUT)
    win.record['collector'] = win.observe_terminal(win.names['collector'], clocks['collectorSeconds'] + clocks[
        'collectorRetireSeconds'] + 60, clocks['collectorRetireSeconds'])
    win.record['outcome'] = guest_outcome(win.record['guest']) if not isinstance(executor, PlanOnly) else 'planned'


def stop_unobserved_units(win, executor, clocks):
    """A guest or collector whose start was attempted but whose terminal state was never observed
    may still run: stop it (bounded) before the caps and slices are removed underneath it."""
    if isinstance(executor, PlanOnly):
        return True
    all_terminal = True
    for kind, retire in (('collector', clocks['collectorRetireSeconds']), ('guest', clocks['retireSeconds'])):
        if not win.record.get(f'{kind}StartAttempted') or kind in win.record:
            continue
        unit, entry = win.names[kind], {}
        try:
            executor.run(['sudo', '-n', 'systemctl', 'stop', unit], 'unit.stop-on-exit', timeout=retire + EFFECT_TIMEOUT)
            entry['stopped'] = True
        except Exception as error:
            entry.update(stopped=False, error=error.reason if isinstance(error, Refusal) else repr(error))
        try:
            props = unit_show(win.host, unit)
            entry.update(state={k: props.get(k) for k in ('ActiveState', 'SubState', 'Result', 'ExecMainStatus')},
                         terminal=terminal(props))
        except Exception as error:
            entry.update(terminal=False, showError=error.reason if isinstance(error, Refusal) else repr(error))
        win.record[f'{kind}StopOnExit'] = entry
        all_terminal &= entry['terminal']
    return all_terminal


def _window_exit_path(win, host, executor, selection, rows, clocks, observe_now, first):
    try:
        if win.record.get('guestStartAttempted') or win.record.get('collectorStartAttempted'):
            stopped = stop_unobserved_units(win, executor, clocks)
            if first is not None:
                win.record['outcome'] = 'failed-awaiting-readback' if stopped else 'unknown-awaiting-readback'
                if not stopped:
                    win.record['unitMayStillRun'] = True
        if win.record.get('capsPending') is not None:
            win.remove_caps()
        if win.record.get('slicesSet'):
            win.record['sliceRevertErrors'] = revert_slices(executor)
        win.record['exitedAt'] = utc_text(host.now())
        if not isinstance(executor, PlanOnly):
            try:
                post = observe_now()
                own = set(win.names.values())
                post = dict(post, units=dict(post.get('units') or {}, native=[
                    n for n in (post.get('units') or {}).get('native') or [] if n not in own]))
                win.record['ioStatAfter'], win.record['psiAfter'] = post.get('ioStat'), post.get('ioPressure')
                win.record['exitExclusivity'] = check_exclusivity(post)
                capped = {t['cgroup'] for t in win.targets}
                win.record['newUncappedUnitsDuringWindow'] = sorted(
                    l['cgroup'] for l in post['lanes']['lanes'] if l['cgroup'] not in capped)
            except Exception as error:
                win.record['exitObservationError'] = repr(error)
        if win.record.get('capsPending'):
            win.record['windowExitDirective'] = window_exit_directive(win.run_dir)
        win.record['signalsDuringExit'] = list(SIGNALS['pending'])
        evidence = build_evidence(selection, rows, win.record)
        win.evidence = evidence
        body = json.dumps(evidence, indent=1, sort_keys=True)
        try:
            executor.write(record_path(win.op), body)
            win.record_path = record_path(win.op)
        except Exception as error:
            win.record_path = None
            win.record['recordWriteError'] = repr(error)
        if os.path.isdir(win.run_dir) and not isinstance(executor, PlanOnly):
            try:
                executor.write(f'{win.run_dir}/window-evidence.json', body)
            except Exception as error:
                win.record['runDirWriteError'] = repr(error)
    finally:
        SIGNALS['raise'] = False  # stays disarmed until re-armed; later signals are recorded for main
    if win.record.get('capsPending'):
        pending = Refusal('io.cap-remove-pending',
                          f'caps may remain ({len(win.record["capsRemoved"]["errors"])} errors, '
                          f'{len(win.record["capsRemoved"]["failures"])} still present); run: '
                          + win.record['windowExitDirective'])
        pending.window = win
        raise pending from first
    if first is not None:
        first.window = win
        raise first
    return win


def dry_run(host, selection, facts, rows):
    """Preflight verdict plus the exact effect plan. Performs and writes nothing."""
    validate_selection(selection)
    refusing = [{'check': r['check'], 'status': r['status'], 'reason': r['reason']}
                for r in rows if r['status'] != SUPPORTED]
    plan = PlanOnly()
    win = launch(host, plan, selection, facts, rows, plan_ungated=True)
    return {'schema': 'fsgg.fdev-native-dry-run/1', 'changesMade': False, 'wouldRefuse': refusing,
            'sharedCodexHost': shared_codex_host(rows), 'plan': plan.plan, 'evidencePreview': win.evidence}


def window_exit(host, executor, manifest_path):
    """Recovery after a crashed window. Attempts every removal (an absent unit is already gone),
    reverts both native slices, reads back, and refuses under the same pending rule as launch: a
    cap still present, or a removal error on a stopped unit whose drop-in may remain."""
    manifest = load_json(manifest_path)
    validate_operation_id(manifest.get('operationId'))
    removal = remove_caps_all(host, executor, manifest['targets'], manifest['devices'], os.getuid())
    result = {'operationId': manifest['operationId'], 'targets': removal['targets'], 'errors': removal['errors'],
              'sliceRevertErrors': revert_slices(executor), 'failures': removal['failures'],
              'readbackOk': removal['readbackOk']}
    result['unconfirmed'] = removal['unconfirmed']
    if removal['pending']:
        refusal = Refusal('io.cap-remove-readback', f'caps still present: {removal["failures"]}; removal '
                          f'unconfirmed on stopped units: {[r["unit"] for r in removal["unconfirmed"]]}')
        refusal.result = result
        raise refusal
    return result


# ---------------------------------------------------------------------------
# Unit bodies: QMP power-down (ExecStop) and the collector.
# ---------------------------------------------------------------------------
def qmp_powerdown(sock_path, wait_pid, timeout=None):
    deadline = time.monotonic() + (timeout or 3600)
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as conn:
            conn.settimeout(10)
            conn.connect(sock_path)
            stream = conn.makefile('rwb')
            stream.readline()  # greeting
            for command in ({'execute': 'qmp_capabilities'}, {'execute': 'system_powerdown'}):
                stream.write(json.dumps(command).encode() + b'\n'); stream.flush()
                while (reply := json.loads(stream.readline() or b'{}')) and 'event' in reply:
                    pass
    except OSError as error:
        print(f'qmp-powerdown: {error}; systemd escalates after TimeoutStopSec', file=sys.stderr)
    while wait_pid and os.path.exists(f'/proc/{wait_pid}') and time.monotonic() < deadline:
        time.sleep(1)
    return 0


def collect(host, run_dir, guest_unit, op, export_dir=None):
    props = unit_show(host, guest_unit)
    require(terminal(props), 'collect.guest-exit', f'{guest_unit} is {props.get("ActiveState")}/{props.get("SubState")}')
    cg = props.get('ControlGroup')
    events = _try(lambda: host.read(host.cgroot / cg.lstrip('/') / 'cgroup.events'), '') if cg else ''
    require('populated 1' not in events, 'collect.guest-exit', 'guest cgroup still populated')
    root = f'{run_dir}/root.raw'
    digest, size = sha256_file(root)
    st = os.stat(root)
    packet = {'schema': 'fsgg.fdev-native-result-packet/1', 'operationId': op, 'guestUnit': guest_unit,
              'unit': {k: props.get(k) for k in ('ActiveState', 'SubState', 'Result', 'ExecMainStatus')},
              'root': {'sha256': digest, 'bytes': size, 'device': st.st_dev, 'inode': st.st_ino,
                       'allocatedBytes': st.st_blocks * 512},
              'custody': 'cooperative', 'independence': {'fromFdev': False, 'fromHome': False}}
    if export_dir:
        target = f'{export_dir}/{op}/qualified-root.raw'
        os.makedirs(os.path.dirname(target), mode=0o700, exist_ok=False)
        subprocess.run(['cp', '--sparse=always', root, target], check=True, timeout=3600)
        copy_digest, _ = sha256_file(target)
        require(copy_digest == digest, 'collect.export', 'export copy digest differs from the source')
        packet['export'] = {'path': target, 'sha256': copy_digest}
    body = json.dumps(packet, indent=1, sort_keys=True)
    fd = os.open(f'{run_dir}/result-packet.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(body)
    return packet


# ---------------------------------------------------------------------------
# GitHub witnesses (real implementation; injected fakes in tests).
# ---------------------------------------------------------------------------
class GitHub:
    def __init__(self, executor, host):
        self.ex, self.host = executor, host

    def post_start_witness(self, op, step):
        out = self.ex.run(['gh', 'api', '-X', 'POST', 'repos/FS-GG/.github/issues/4778/comments', '-f',
                           f'body=fdev native claim: operation `{op}` step `{step}` claimed. Start witness. '
                           'Cooperative custody; not independent of fdev or Home.'], 'github.start-witness', timeout=60)
        return parse_utc(json.loads(out)['created_at'], 'clock.start-witness')

    def date(self):
        rc, out = self.host.run(['gh', 'api', '-i', 'rate_limit'], timeout=60)
        require(rc == 0, 'clock.github-date', 'GitHub Date read failed')
        match = re.search(r'^date:\s*(.+?)\r?$', out, re.MULTILINE | re.IGNORECASE)
        require(match is not None, 'clock.github-date', 'no Date header')
        return parse_http_date(match.group(1))

    def comment(self, comment_id):
        rc, out = self.host.run(['gh', 'api', f'repos/FS-GG/.github/issues/comments/{comment_id}'], timeout=60)
        if rc != 0:
            return None
        doc = json.loads(out)
        return {'author': doc['user']['login'], 'issueUrl': doc['issue_url'].replace(
            'https://api.github.com/repos/', 'https://github.com/'), 'body': doc['body']}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def _summary(rows):
    counts = {s: sum(1 for r in rows if r['status'] == s) for s in (SUPPORTED, UNSUPPORTED, UNKNOWN)}
    return {'counts': counts, 'allSupported': counts[UNSUPPORTED] == counts[UNKNOWN] == 0,
            'sharedCodexHost': shared_codex_host(rows)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('preflight', 'launch'):
        p = sub.add_parser(name)
        p.add_argument('--selection')
        p.add_argument('--host-partition-df', help='Home df input (fsgg.fdev-native-home-df/1)')
        p.add_argument('--host-partition-max-age', type=int, default=None)
        p.add_argument('--host-memory', help='Home host-memory input (fsgg.fdev-native-home-memory/1)')
        p.add_argument('--host-memory-floor', type=int, default=None, help='Root-set host MemAvailable floor (bytes)')
        p.add_argument('--host-memory-max-age', type=int, default=None)
        p.add_argument('--image', help='hash this stock image against the noble lock pin')
        p.add_argument('--privileged-census', action='store_true', help='read pool writers via sudo -n (read-only)')
        p.add_argument('--no-github', action='store_true', help='skip reading the Root selection comment')
        if name == 'launch':
            p.add_argument('--dry-run', action='store_true')
    p = sub.add_parser('window-exit'); p.add_argument('--manifest', required=True)
    p = sub.add_parser('attach-home-io'); p.add_argument('--evidence', required=True); p.add_argument('--home-io', required=True)
    p = sub.add_parser('validate-evidence'); p.add_argument('evidence')
    p = sub.add_parser('census'); p.add_argument('--mount', required=True)
    p = sub.add_parser('qmp-powerdown'); p.add_argument('--socket', required=True); p.add_argument('--wait-pid', type=int)
    p = sub.add_parser('collect'); p.add_argument('--run-dir', required=True); p.add_argument('--guest-unit', required=True)
    p.add_argument('--operation', required=True); p.add_argument('--export-dir')
    args = parser.parse_args(argv)
    host = LiveHost()
    try:
        if args.command in ('preflight', 'launch'):
            selection = load_json(args.selection) if args.selection else None
            host_df = load_json(args.host_partition_df) if args.host_partition_df else (
                selection or {}).get('hostPartition')
            if host_df is not None and selection is None:
                host_df = dict(host_df, _maxAge=args.host_partition_max_age)
            image = None
            if args.image or selection:
                path = args.image or selection['image']['path']
                expected = lock_image_sha(NOBLE_LOCK)
                got = _try(lambda: sha256_file(path))
                image = {'path': path, 'expected': expected, 'sha256': got[0] if got else None,
                         'match': bool(got and got[0] == expected)}
            comment = None
            if selection and not args.no_github:
                comment = _try(lambda: GitHub(None, host).comment(selection['rootSelection']['commentId']))
            facts = observe(host, selection, args.privileged_census)
            host_mem = load_json(args.host_memory) if args.host_memory else (selection or {}).get('hostMemory')
            rows = evaluate(facts, selection, comment, host_df, host.now(), image, host_mem,
                            args.host_memory_floor, args.host_memory_max_age)
            if args.command == 'preflight':
                print(json.dumps({'schema': 'fsgg.fdev-native-preflight/1', 'observedAt': facts['observedAt'],
                                  'changesMade': False, 'summary': _summary(rows), 'rows': rows}, indent=1))
                return 0 if _summary(rows)['allSupported'] else 2
            if args.dry_run:
                result = dry_run(host, selection, facts, rows)
                print(json.dumps({'summary': _summary(rows), **result}, indent=1))
                return 0 if not result['wouldRefuse'] else 2
            validate_selection(selection)
            executor = Executor()
            install_signal_exceptions()
            win = launch(host, executor, selection, facts, rows, github=GitHub(executor, host))
            print(json.dumps(win.evidence, indent=1, sort_keys=True))
            late = recorded_signals(win.record.get('signalsDuringExit') or [], SIGNALS['pending'])
            if late:  # a stop request arrived during window exit: the window ran, but not undisturbed
                print(json.dumps({'signalsDuringExit': [signal.Signals(s).name for s in late],
                                  'windowRecord': getattr(win, 'record_path', None),
                                  'outcome': win.record.get('outcome')}, indent=1), file=sys.stderr)
                return 128 + int(late[0])
            return 0
        if args.command == 'window-exit':
            print(json.dumps(window_exit(host, Executor(), args.manifest), indent=1)); return 0
        if args.command == 'attach-home-io':
            print(json.dumps(attach_home_io(load_json(args.evidence), load_json(args.home_io)), indent=1)); return 0
        if args.command == 'validate-evidence':
            validate_evidence(load_json(args.evidence)); print('evidence valid'); return 0
        if args.command == 'census':
            print(json.dumps(open_file_census(args.mount))); return 0
        if args.command == 'qmp-powerdown':
            return qmp_powerdown(args.socket, args.wait_pid)
        if args.command == 'collect':
            print(json.dumps(collect(host, args.run_dir, args.guest_unit, args.operation, args.export_dir), indent=1))
            return 0
    except Interrupted as interrupted:
        window = getattr(interrupted, 'window', None)
        report = {'interrupted': signal.Signals(interrupted.signum).name}
        if window is not None:
            report.update(windowRecord=getattr(window, 'record_path', None), outcome=window.record.get('outcome'),
                          firstFailure=window.record.get('firstFailure'))
        print(json.dumps(report, indent=1), file=sys.stderr)
        return 128 + int(interrupted.signum)
    except Refusal as refusal:
        report = {'refused': {'check': refusal.check, 'reason': refusal.reason}}
        window = getattr(refusal, 'window', None)
        if window is not None:
            report['windowRecord'] = getattr(window, 'record_path', None)
            report['windowExitDirective'] = window.record.get('windowExitDirective')
            report['firstFailure'] = window.record.get('firstFailure')
            if window.record.get('recordWriteError'):
                report['recordWriteError'] = window.record['recordWriteError']
        if getattr(refusal, 'result', None) is not None:
            report['windowExit'] = refusal.result
        print(json.dumps(report, indent=1), file=sys.stderr)
        return 3
    return 1


if __name__ == '__main__':
    sys.exit(main())
