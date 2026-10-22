"""Canonical managed-group candidate owner: fsgg.managed-group-contract/1.

Linux only, standard library only, single file so a caller can pin one
(repository, commit, path, sha256) source identity.

The sibling ``shared_process_owner.py`` contains one direct child by numeric
process group and refuses when a descendant leaves that group or session. Stock
browsers and web servers deliberately create such groups. This owner keeps that
owner's receipt vocabulary (firstCause, additionalFailures, cleanup, custody) and
its "unknown birth never licenses a signal" rule, but contains by a delegated
cgroup v2 leaf instead of a process group:

* containment: every member is born inside a fresh leaf ``fsgg-mg-<id>`` under an
  exclusive delegated cgroup v2 root. setsid/double-fork does not leave a cgroup.
  A process anywhere else in the delegated root (except the owner) is an escape:
  the run fails, and retirement sweeps the whole root (cgroup.kill of member-made
  cgroups, quarantine-and-kill for the root and supervisor cgroups). The leader runs
  with no_new_privs, so members cannot gain the privilege needed to leave the root.
* birth: the leader is the owner's unreaped child, so its pidfd binds the exact
  birth; members are identified by (pid, start ticks, cgroup) and every signal is
  sent through a pidfd whose start ticks and cgroup were re-checked after opening.
* retirement: SIGTERM by pidfd, bounded grace, then the kernel's ``cgroup.kill``;
  emptiness is the kernel's ``populated 0`` plus a final census of the whole root;
  every stage is separately guarded, and a removal failure is reported, never hidden.

Containment that cannot be established refuses before launch. Members share the
owner's uid, so leaf limits are cooperative and supervisor-death cleanup belongs to
the enclosing delegated unit (see the contract document); this module claims neither.
"""
import ctypes, errno, hashlib, json, os, pathlib, re, select, signal, stat, subprocess, sys, threading, time, uuid

CONTRACT_SCHEMA = 'fsgg.managed-group-contract/1'
PROFILE_SCHEMA = 'fsgg.managed-group-profile/1'
RECEIPT_SCHEMA = 'fsgg.managed-group-receipt/1'
CONTAINMENT = 'cgroup2-delegated'
SUPERVISOR = 'fsgg-supervisor'
LEAF_PREFIX = 'fsgg-mg-'
QUARANTINE_PREFIX = 'fsgg-quarantine-'
POLL_SECONDS = 0.05
SAMPLE = 32
KNOWN_LIMIT = 65536
PR_SET_NO_NEW_PRIVS = 38
HEX64 = re.compile(r'[0-9a-f]{64}')
NAME = re.compile(r'[a-z0-9][a-z0-9.-]{0,63}')
ENV_KEY = re.compile(r'[A-Za-z_][A-Za-z0-9_]{0,127}')
PROFILE_KEYS = {'schema', 'name', 'root', 'executable', 'leaderImages', 'argv', 'cwd',
                'environmentKeys', 'containment', 'limits', 'retirement'}
LIMIT_KEYS = {'pidsMax', 'memoryMaxBytes', 'stdoutBytes', 'stderrBytes'}
RETIREMENT_KEYS = {'onLeaderExit', 'leaderExitSettleSeconds', 'termGraceSeconds', 'cleanupSeconds'}


class Refused(RuntimeError):
    """Raised before any launch. ``code`` is a closed refusal code; ``receipt`` may be None."""

    def __init__(self, code, detail='', receipt=None):
        super().__init__(code + (': ' + detail if detail else ''))
        self.code, self.detail, self.receipt = code, detail, receipt


class GroupFailed(RuntimeError):
    """Raised after a launch attempt failed in any way. ``receipt`` is the full receipt."""

    def __init__(self, receipt):
        super().__init__(receipt['firstCause'])
        self.receipt = receipt


def describe(error):
    try:
        return type(error).__name__ + ': ' + str(error)
    except BaseException:
        return type(error).__name__ + ': <message-unavailable>'


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode()


def sha256_file(path, until=None):
    """SHA-256 of a file; raise TimeoutError once ``until`` (monotonic) has passed."""
    digest = hashlib.sha256()
    with open(path, 'rb') as source:
        for block in iter(lambda: source.read(1048576), b''):
            if until is not None and time.monotonic() >= until:
                raise TimeoutError('original deadline exhausted while hashing ' + path)
            digest.update(block)
    return digest.hexdigest()


def sha256_fd(fd):
    digest = hashlib.sha256(); offset = 0
    while True:
        block = os.pread(fd, 1048576, offset)
        if not block:
            return digest.hexdigest()
        digest.update(block); offset += len(block)


# Hash of this file when it was imported (the receipt's owner identity).
OWNER_PATH = os.path.abspath(__file__)
OWNER_SHA256 = sha256_file(OWNER_PATH)


def _require(ok, code, detail=''):
    if not ok:
        raise Refused(code, detail)


def _number(value, low, high):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and low <= value <= high


def _integer(value, low, high):
    return isinstance(value, int) and not isinstance(value, bool) and low <= value <= high


def _plain_absolute(path):
    return isinstance(path, str) and path.startswith('/') and '\x00' not in path and os.path.normpath(path) == path


def _within(path, root):
    return path == root or path.startswith(root.rstrip('/') + '/')


def validate_profile(profile):
    """Return (profile, sha256) for a closed, self-consistent profile or raise Refused('profile-invalid')."""
    bad = lambda field: Refused('profile-invalid', field)
    if not isinstance(profile, dict) or set(profile) != PROFILE_KEYS: raise bad('closed keys')
    if profile['schema'] != PROFILE_SCHEMA: raise bad('schema')
    if not isinstance(profile['name'], str) or not NAME.fullmatch(profile['name']): raise bad('name')
    if profile['containment'] != CONTAINMENT: raise bad('containment')
    root = profile['root']
    if not _plain_absolute(root) or root == '/': raise bad('root')
    executable = profile['executable']
    if not isinstance(executable, dict) or set(executable) != {'path', 'sha256'}: raise bad('executable')
    if not _plain_absolute(executable['path']) or not isinstance(executable['sha256'], str) or not HEX64.fullmatch(executable['sha256']): raise bad('executable')
    images = profile['leaderImages']
    if not isinstance(images, list) or not 1 <= len(images) <= 8 or len(set(images)) != len(images) or not all(isinstance(v, str) and HEX64.fullmatch(v) for v in images): raise bad('leaderImages')
    argv = profile['argv']
    if not isinstance(argv, list) or not 1 <= len(argv) <= 256 or not all(isinstance(a, str) and len(a) <= 32768 and not any(ord(c) < 32 or ord(c) == 127 for c in a) for a in argv): raise bad('argv')
    if not _plain_absolute(profile['cwd']) or not _within(profile['cwd'], root): raise bad('cwd')
    keys = profile['environmentKeys']
    if not isinstance(keys, list) or len(keys) > 256 or keys != sorted(set(keys)) or not all(isinstance(k, str) and ENV_KEY.fullmatch(k) for k in keys): raise bad('environmentKeys')
    limits = profile['limits']
    if not isinstance(limits, dict) or set(limits) != LIMIT_KEYS: raise bad('limits')
    if limits['pidsMax'] is not None and not _integer(limits['pidsMax'], 1, 65536): raise bad('limits.pidsMax')
    if limits['memoryMaxBytes'] is not None and not _integer(limits['memoryMaxBytes'], 1048576, 1 << 40): raise bad('limits.memoryMaxBytes')
    if not _integer(limits['stdoutBytes'], 0, 1 << 30) or not _integer(limits['stderrBytes'], 0, 1 << 30): raise bad('limits.streams')
    retirement = profile['retirement']
    if not isinstance(retirement, dict) or set(retirement) != RETIREMENT_KEYS: raise bad('retirement')
    if retirement['onLeaderExit'] not in ('require-empty', 'retire-members'): raise bad('retirement.onLeaderExit')
    if not _number(retirement['cleanupSeconds'], 0.1, 120): raise bad('retirement.cleanupSeconds')
    if not _number(retirement['termGraceSeconds'], 0, 60) or retirement['termGraceSeconds'] >= retirement['cleanupSeconds']: raise bad('retirement.termGraceSeconds')
    if not _number(retirement['leaderExitSettleSeconds'], 0, 30): raise bad('retirement.leaderExitSettleSeconds')
    return profile, hashlib.sha256(canonical(profile)).hexdigest()



# ---------------------------------------------------------------- /proc and cgroupfs

def _stat(pid):
    try:
        text = pathlib.Path(f'/proc/{pid}/stat').read_text()
        a = text[text.rindex(')') + 2:].split()
        return {'pid': pid, 'state': a[0], 'ppid': int(a[1]), 'pgid': int(a[2]), 'sid': int(a[3]), 'start': int(a[19])}
    except (FileNotFoundError, ProcessLookupError, ValueError, IndexError):
        return None


def _read(path, limit=65536):
    with open(path, 'rb') as source:
        return source.read(limit).decode('utf-8', 'replace')


def _cgroup2_mount():
    """(mount point, mount root) of the single cgroup2 mount visible to this process, or None."""
    found = []
    for line in _read('/proc/self/mountinfo', 1 << 20).splitlines():
        left, _, right = line.partition(' - ')
        if right.split(' ', 1)[0] == 'cgroup2':
            fields = left.split(' ')
            found.append((fields[4], fields[3]))
    return found[0] if len(found) == 1 else None


def _cgroup_dir(pid, mount):
    """Absolute cgroup v2 directory of ``pid`` ('self' allowed), or None when unknown."""
    try:
        lines = [l for l in _read(f'/proc/{pid}/cgroup').splitlines() if l.startswith('0::')]
    except (FileNotFoundError, ProcessLookupError, PermissionError):
        return None
    if len(lines) != 1:
        return None
    relative = os.path.relpath(lines[0][3:], mount[1])
    if relative.startswith('..'):
        return None
    return os.path.normpath(os.path.join(mount[0], relative))


def _populated(directory):
    for line in _read(os.path.join(directory, 'cgroup.events')).splitlines():
        key, _, value = line.partition(' ')
        if key == 'populated':
            return value.strip() == '1'
    raise RuntimeError('cgroup.events lacks populated')


def _tree(top):
    """{pid: cgroup directory} for every process in the subtree rooted at top."""
    found = {}
    for directory, _, _ in os.walk(top):
        try:
            text = _read(os.path.join(directory, 'cgroup.procs'), 1 << 22)
        except FileNotFoundError:
            continue
        for token in text.split():
            found[int(token)] = directory
    return found


def _dirs(top):
    return {directory for directory, _, _ in os.walk(top)}


def _write(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CLOEXEC)
    try:
        os.write(fd, data)
    finally:
        os.close(fd)


def probe(delegated_root=None):
    """Report whether stable descendant containment is available. Never mutates."""
    result = {'schema': CONTRACT_SCHEMA, 'containment': CONTAINMENT, 'available': False, 'refusal': None,
              'delegatedRoot': None, 'ownerCgroup': None, 'cgroupMount': None, 'kernel': os.uname().release, 'controllers': []}

    def refuse(code):
        result['refusal'] = code
        return result
    if not sys.platform.startswith('linux'):
        return refuse('platform-unsupported')
    if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
        return refuse('pidfd-unavailable')
    try:
        os.close(os.pidfd_open(os.getpid()))
    except OSError:
        return refuse('pidfd-unavailable')
    try:
        mount = _cgroup2_mount()
    except OSError:
        mount = None
    if mount is None:
        return refuse('cgroup2-unavailable')
    result['cgroupMount'] = list(mount)
    own = _cgroup_dir('self', mount)
    if own is None:
        return refuse('owner-cgroup-unknown')
    result['ownerCgroup'] = own
    if delegated_root is None:
        delegated_root = os.path.dirname(own) if os.path.basename(own) == SUPERVISOR else own
    result['delegatedRoot'] = delegated_root
    if not _plain_absolute(delegated_root) or os.path.realpath(delegated_root) != delegated_root or not _within(delegated_root, mount[0]) or delegated_root == mount[0]:
        return refuse('delegated-root-invalid')
    if own not in (delegated_root, os.path.join(delegated_root, SUPERVISOR)):
        return refuse('owner-outside-delegated-root')
    for name in ('cgroup.procs', 'cgroup.subtree_control', 'cgroup.events', 'cgroup.kill', 'cgroup.type', 'cgroup.controllers'):
        if not os.path.isfile(os.path.join(delegated_root, name)):
            return refuse('cgroup-kill-unavailable' if name == 'cgroup.kill' else 'delegated-root-invalid')
    try:
        if _read(os.path.join(delegated_root, 'cgroup.type')).strip() != 'domain':
            return refuse('delegated-root-not-domain')
        result['controllers'] = _read(os.path.join(delegated_root, 'cgroup.controllers')).split()
    except OSError:
        return refuse('delegated-root-invalid')
    if not all(os.access(os.path.join(delegated_root, name), os.W_OK) for name in ('', 'cgroup.procs', 'cgroup.subtree_control')):
        return refuse('delegated-root-not-writable')
    result['available'] = True
    return result


# ---------------------------------------------------------------- identity and signals

def _identity(pid, inside, mount):
    """The stat row of a live pid whose start ticks are stable around a cgroup read accepted by ``inside``."""
    first = _stat(pid)
    if first is None or first['state'] == 'Z':
        return None
    directory = _cgroup_dir(pid, mount)
    second = _stat(pid)
    if second is None or second['start'] != first['start'] or directory is None or not inside(directory):
        return None
    return first


def _pidfd_signal(pid, start, sig, inside, mount):
    """Signal only through a pidfd whose process still has the recorded start ticks and an accepted cgroup."""
    try:
        fd = os.pidfd_open(pid)
    except (ProcessLookupError, OSError):
        return 'gone'
    try:
        row = _stat(pid)
        if row is None or row['start'] != start or row['state'] == 'Z':
            return 'identity-changed'
        directory = _cgroup_dir(pid, mount)
        if directory is None or not inside(directory):
            return 'scope-changed'
        signal.pidfd_send_signal(fd, sig)
        return 'sent'
    except ProcessLookupError:
        return 'gone'
    except OSError:
        return 'refused'
    finally:
        os.close(fd)


def _alive(pid, start):
    row = _stat(pid)
    return row is not None and row['start'] == start and row['state'] != 'Z'


def _remove_tree(top, until):
    """Remove an emptied cgroup subtree bottom-up, retrying EBUSY until ``until``; raise the last OSError."""
    while True:
        try:
            for directory, _, _ in sorted(os.walk(top), key=lambda item: -item[0].count('/')):
                os.rmdir(directory)
            return
        except OSError as error:
            if error.errno != errno.EBUSY or time.monotonic() >= until:
                raise
            time.sleep(0.02)


# ---------------------------------------------------------------- run

def run(profile, *, env, deadline, out_dir, delegated_root=None, control=None):
    """Run one managed group to bounded completion. Return the receipt or raise Refused/GroupFailed.

    ``deadline`` is an absolute time.monotonic() value owned by the caller. Work stops
    at the deadline; retirement may extend to ``min(stop, deadline) + cleanupSeconds``.
    Refused means nothing was launched. GroupFailed carries the complete receipt.
    """
    started = time.monotonic()
    hook = control if control is not None else (lambda event, context: None)
    profile, profile_sha = validate_profile(profile)
    root = profile['root']
    _require(_number(deadline, float('-inf'), float('inf')), 'deadline-invalid')
    _require(os.path.realpath(root) == root and os.path.isdir(root), 'root-invalid')
    _require(_plain_absolute(out_dir) and _within(out_dir, root) and out_dir != root, 'out-dir-invalid')
    parent = os.path.dirname(out_dir)
    _require(os.path.realpath(parent) == parent and os.path.isdir(parent) and not os.path.lexists(out_dir), 'out-dir-invalid')
    try:
        os.mkdir(out_dir, 0o700)
    except OSError as error:
        raise Refused('out-dir-invalid', describe(error))
    receipt = {'schema': RECEIPT_SCHEMA, 'contract': CONTRACT_SCHEMA,
               'owner': {'path': OWNER_PATH, 'sha256': OWNER_SHA256},
               'profile': profile, 'profileSha256': profile_sha, 'invocationId': uuid.uuid4().hex, 'bootId': None,
               'containment': {'mode': CONTAINMENT, 'delegatedRoot': None, 'leaf': None, 'limitsApplied': {},
                               'limitsBinding': 'cooperative-leaf-files', 'ownerDeathCleanup': 'delegated-unit-not-claimed-by-owner'},
               'launch': {'attempted': False, 'launched': False, 'error': None},
               'leader': None, 'exit': None, 'members': {'observed': 0, 'sample': []}, 'escaped': [],
               'residualAfterLeaderExit': [], 'residualInRoot': None, 'createdCgroups': [], 'streams': {},
               'retirement': None, 'cleanup': 'not-started', 'custody': 'no-child', 'outcome': None, 'refusal': None,
               'firstCause': None, 'additionalFailures': [], 'timing': {'deadlineInSeconds': round(deadline - started, 3)}}

    def write_receipt():
        temporary = os.path.join(out_dir, '.receipt.json.tmp')
        with open(temporary, 'w') as sink:
            sink.write(json.dumps(receipt, indent=2, sort_keys=True) + '\n')
        os.replace(temporary, os.path.join(out_dir, 'receipt.json'))

    def refuse(refusal):
        receipt['outcome'], receipt['refusal'], receipt['firstCause'] = 'refused', refusal.code, describe(refusal)
        try:
            write_receipt()
        except BaseException as error:
            receipt['additionalFailures'].append('receipt-report:' + describe(error))
        refusal.receipt = receipt
        return refusal

    exe_fd = None
    try:
        try:
            try:
                receipt['bootId'] = _read('/proc/sys/kernel/random/boot_id').strip()
            except OSError:
                pass
            _require(time.monotonic() < deadline, 'deadline-exhausted')
            _require(threading.active_count() == 1, 'owner-not-single-threaded')
            _require(signal.getitimer(signal.ITIMER_REAL) == (0.0, 0.0), 'owner-alarm-active')
            _require(isinstance(env, dict) and sorted(env) == profile['environmentKeys']
                     and all(isinstance(v, str) and '\x00' not in v for v in env.values()), 'environment-mismatch')
            _require(os.path.realpath(profile['cwd']) == profile['cwd'] and os.path.isdir(profile['cwd']), 'cwd-invalid')
            path = profile['executable']['path']
            _require(os.path.realpath(path) == path, 'executable-invalid', 'symlinked path')
            try:
                exe_fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
                info = os.fstat(exe_fd)
                _require(stat.S_ISREG(info.st_mode) and info.st_mode & 0o111, 'executable-invalid', 'not a regular executable file')
                digest = sha256_fd(exe_fd)
            except OSError as error:
                raise Refused('executable-invalid', describe(error))
            _require(digest == profile['executable']['sha256'], 'executable-identity-mismatch')
            available = probe(delegated_root)
            _require(available['available'], 'containment-unavailable', str(available['refusal']))
            droot, mount = available['delegatedRoot'], tuple(available['cgroupMount'])
            receipt['containment']['delegatedRoot'] = droot
            try:
                strays = sorted(pid for pid in _tree(droot) if pid != os.getpid())
                children = sorted(n for n in os.listdir(droot) if os.path.isdir(os.path.join(droot, n)) and n != SUPERVISOR)
                nested = sorted(os.path.relpath(d, droot) for d in _dirs(os.path.join(droot, SUPERVISOR)))[1:] if os.path.isdir(os.path.join(droot, SUPERVISOR)) else []
            except OSError as error:
                raise Refused('containment-unavailable', describe(error))
            _require(not strays and not children and not nested, 'delegated-root-not-exclusive',
                     json.dumps({'processes': strays[:SAMPLE], 'cgroups': (children + nested)[:SAMPLE]}))
            for controller, wanted in (('pids', profile['limits']['pidsMax']), ('memory', profile['limits']['memoryMaxBytes'])):
                _require(wanted is None or controller in available['controllers'], 'limit-unenforceable', controller)
            leaf = _prepare(profile, droot, available['ownerCgroup'], receipt)
        except Refused as refusal:
            raise refuse(refusal)
        return _contained(profile, env, deadline, out_dir, receipt, write_receipt, refuse, droot, mount, leaf, exe_fd, hook, started)
    finally:
        if exe_fd is not None:
            os.close(exe_fd)


def _prepare(profile, droot, own, receipt):
    """Move the owner to the supervisor cgroup, enable requested controllers and create the fresh leaf.

    Any failure here is a refusal: nothing has been launched and the fresh leaf is removed.
    """
    limits = profile['limits']
    supervisor = os.path.join(droot, SUPERVISOR)
    leaf = os.path.join(droot, LEAF_PREFIX + receipt['invocationId'])
    created = False
    try:
        if own != supervisor:
            os.makedirs(supervisor, exist_ok=True)
            _write(os.path.join(supervisor, 'cgroup.procs'), str(os.getpid()).encode())
        wanted = [c for c, v in (('pids', limits['pidsMax']), ('memory', limits['memoryMaxBytes'])) if v is not None]
        if wanted:
            _write(os.path.join(droot, 'cgroup.subtree_control'), ' '.join('+' + c for c in wanted).encode())
        os.mkdir(leaf, 0o755); created = True
        applied = receipt['containment']['limitsApplied']
        if limits['pidsMax'] is not None:
            _write(os.path.join(leaf, 'pids.max'), str(limits['pidsMax']).encode())
            applied['pids.max'] = _read(os.path.join(leaf, 'pids.max')).strip()
        if limits['memoryMaxBytes'] is not None:
            _write(os.path.join(leaf, 'memory.max'), str(limits['memoryMaxBytes']).encode())
            applied['memory.max'] = _read(os.path.join(leaf, 'memory.max')).strip()
            if os.path.exists(os.path.join(leaf, 'memory.swap.max')):
                _write(os.path.join(leaf, 'memory.swap.max'), b'0')
                applied['memory.swap.max'] = _read(os.path.join(leaf, 'memory.swap.max')).strip()
        receipt['containment']['leaf'] = os.path.relpath(leaf, droot)
        return leaf
    except OSError as error:
        if created:
            try:
                os.rmdir(leaf)
            except OSError as cleanup:
                receipt['additionalFailures'].append('leaf-remove:' + describe(cleanup))
        raise Refused('containment-unavailable', describe(error))


def _contained(profile, env, deadline, out_dir, receipt, write_receipt, refuse, droot, mount, leaf, exe_fd, hook, started):
    limits, retirement = profile['limits'], profile['retirement']
    owner, supervisor = os.getpid(), os.path.join(droot, SUPERVISOR)
    quarantine = os.path.join(droot, QUARANTINE_PREFIX + receipt['invocationId'])
    additional = receipt['additionalFailures']
    primary = None; child = None; pidfd = None; leader_exit_at = None
    known = {}; escaped = {}
    preexisting = {droot, supervisor}
    streams = {name: {'bytes': 0, 'capped': False, 'eof': False, 'sha256': hashlib.sha256(), 'fd': None,
                      'sink': None, 'sinkError': None} for name in ('stdout', 'stderr')}
    caps = {'stdout': limits['stdoutBytes'], 'stderr': limits['stderrBytes']}
    retire = {'termSignals': 0, 'cgroupKill': False, 'createdCgroupKills': 0, 'quarantined': 0, 'escapedKills': 0,
              'ownerRelocated': False, 'populatedAfter': None, 'leaderReaped': False, 'leafRemoved': False, 'removeError': None}
    in_leaf = lambda directory: _within(directory, leaf)
    in_root_outside_leaf = lambda directory: _within(directory, droot) and not _within(directory, leaf)

    # Pre-launch output setup: a failure here is still a refusal, and the fresh leaf is removed.
    opened = []
    try:
        libc = ctypes.CDLL(None, use_errno=True)
        for name in streams:
            streams[name]['sink'] = open(os.path.join(out_dir, name + '.log'), 'xb', buffering=0)
        out_r, out_w = os.pipe2(os.O_CLOEXEC); opened += [out_r, out_w]
        err_r, err_w = os.pipe2(os.O_CLOEXEC); opened += [err_r, err_w]
        os.set_blocking(out_r, False); os.set_blocking(err_r, False)
    except OSError as error:
        for fd in opened:
            os.close(fd)
        for s in streams.values():
            if s['sink'] is not None:
                s['sink'].close()
        try:
            os.rmdir(leaf)
        except OSError as cleanup:
            additional.append('leaf-remove:' + describe(cleanup))
        raise refuse(Refused('output-unavailable', describe(error)))
    streams['stdout']['fd'], streams['stderr']['fd'] = out_r, err_r

    def context():
        return {'leaf': leaf, 'delegatedRoot': droot, 'supervisor': supervisor, 'outDir': out_dir,
                'leaderPid': child.pid if child else None, 'known': dict(known), 'escaped': dict(escaped)}

    def leader_exited():
        nonlocal leader_exit_at
        if leader_exit_at is None and child is not None and child.poll() is not None:
            leader_exit_at = time.monotonic(); code = child.returncode
            receipt['exit'] = {'code': code if code >= 0 else None, 'signal': -code if code < 0 else None,
                               'atSeconds': round(leader_exit_at - started, 3)}
        return leader_exit_at is not None

    def drain(until):
        """Copy available output under the caps; wait at most until ``until`` or POLL_SECONDS."""
        timeout = max(0.0, min(POLL_SECONDS, until - time.monotonic()))
        readers = {s['fd']: name for name, s in streams.items() if s['fd'] is not None}
        watched = list(readers) + ([pidfd] if pidfd is not None and leader_exit_at is None else [])
        if not watched:
            time.sleep(timeout); return
        poller = select.poll()
        for fd in watched:
            poller.register(fd, select.POLLIN)
        for fd, _ in poller.poll(timeout * 1000):
            if fd == pidfd:
                leader_exited(); continue
            name = readers[fd]; s = streams[name]
            try:
                block = os.read(fd, 65536)
            except BlockingIOError:
                continue
            except OSError as error:
                os.close(fd); s['fd'] = None
                raise RuntimeError(name + ' read failed: ' + describe(error))
            if not block:
                os.close(fd); s['fd'] = None; s['eof'] = True
                continue
            kept = block[:max(0, caps[name] - s['bytes'])]
            if kept and s['sinkError'] is None:
                try:
                    view = memoryview(kept)
                    while view:
                        view = view[s['sink'].write(view):]
                except OSError as error:
                    s['sinkError'] = describe(error)  # keep reading so members never block on a full pipe
                    raise RuntimeError(name + ' sink failed: ' + s['sinkError'])
                s['sha256'].update(kept); s['bytes'] += len(kept)
            if len(kept) < len(block) and not s['capped']:
                s['capped'] = True
                raise RuntimeError(name + ' byte cap exceeded')

    def relocate_owner():
        if _cgroup_dir('self', mount) != supervisor:
            _write(os.path.join(supervisor, 'cgroup.procs'), str(owner).encode()); retire['ownerRelocated'] = True
            return True
        return False

    def census():
        """Record leaf members; return a failure reason when the delegated root holds anything else."""
        reason = None
        tree = _tree(droot)
        for pid, directory in tree.items():
            if pid == owner:
                if directory != supervisor:
                    relocate_owner(); reason = reason or 'owner cgroup tampered by a member'
                continue
            if _within(directory, leaf):
                row = _identity(pid, in_leaf, mount)
                if row is None or known.get(pid) == row['start']:
                    continue
                if len(known) >= KNOWN_LIMIT:
                    raise RuntimeError('member census bound exceeded')
                known[pid] = row['start']; receipt['members']['observed'] += 1
                if len(receipt['members']['sample']) < SAMPLE:
                    receipt['members']['sample'].append({k: row[k] for k in ('pid', 'start', 'ppid', 'pgid', 'sid')})
            else:
                row = _identity(pid, in_root_outside_leaf, mount)
                if row is not None:
                    escaped.setdefault((pid, row['start']), os.path.relpath(directory, droot))
                reason = reason or 'contained member escaped managed group'
        for pid, start in known.items():
            if pid not in tree and _alive(pid, start) and (pid, start) not in escaped:
                # Same birth, alive and absent from the whole delegated root: a privileged escape.
                escaped[(pid, start)] = 'outside-delegated-root'
                reason = reason or 'contained member escaped managed group'
        return reason

    def members():
        rows = []
        for pid in _tree(leaf):
            row = _stat(pid)
            if row is not None:
                rows.append({k: row[k] for k in ('pid', 'start', 'ppid', 'pgid', 'sid', 'state')})
        return rows

    def enter_group(procs=os.path.join(leaf, 'cgroup.procs').encode()):
        # Forked child, before exec: join the leaf, then forbid privilege gain for every member.
        fd = os.open(procs, os.O_WRONLY | os.O_CLOEXEC)
        try:
            os.write(fd, b'0')
        finally:
            os.close(fd)
        if libc.prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), 'no_new_privs refused')

    try:
        receipt['launch']['attempted'] = True
        try:
            child = subprocess.Popen(profile['argv'], executable=f'/proc/self/fd/{exe_fd}', pass_fds=(exe_fd,),
                                     cwd=profile['cwd'], env=env, stdin=subprocess.DEVNULL, stdout=out_w, stderr=err_w,
                                     start_new_session=True, preexec_fn=enter_group)
        except BaseException as error:
            receipt['launch']['error'] = describe(error)
            raise
        finally:
            os.close(out_w); os.close(err_w)
        receipt['launch']['launched'] = True
        # The child is unreaped, so its PID cannot be reused: this pidfd binds the exact birth.
        pidfd = os.pidfd_open(child.pid)
        birth = _stat(child.pid)
        leader = {'pid': child.pid, 'start': birth['start'] if birth else None, 'pidfdInode': os.fstat(pidfd).st_ino,
                  'cgroup': None, 'image': None, 'imageSha256': None, 'noNewPrivileges': None, 'cmdline': None}
        receipt['leader'], receipt['custody'] = leader, 'contained'
        if birth is not None:
            known[child.pid] = birth['start']; receipt['members']['observed'] += 1
        try:
            leader['cgroup'] = _read(f'/proc/{child.pid}/cgroup').strip()
            leader['noNewPrivileges'] = [l.split(':', 1)[1].strip() for l in _read(f'/proc/{child.pid}/status').splitlines()
                                         if l.startswith('NoNewPrivs:')][0]
            with open(f'/proc/{child.pid}/cmdline', 'rb') as source:
                leader['cmdline'] = [p.decode('utf-8', 'replace') for p in source.read(65536).split(b'\x00')[:-1]][:256]
            leader['imageSha256'] = sha256_file(f'/proc/{child.pid}/exe', until=deadline)
            leader['image'] = 'admitted' if leader['imageSha256'] in profile['leaderImages'] else 'not-admitted'
        except (FileNotFoundError, ProcessLookupError, IndexError):
            leader['image'] = 'unobserved-leader-exited'
        except PermissionError:
            leader['image'] = 'unobservable'
        hook('after-birth', context())
        alive = birth is not None and _alive(child.pid, birth['start'])
        if leader['image'] in ('not-admitted', 'unobservable') or (leader['image'] == 'unobserved-leader-exited' and alive):
            raise RuntimeError('leader image ' + leader['image'])
        if alive and leader['cgroup'] is not None and not leader['cgroup'].endswith('/' + os.path.relpath(leaf, droot)):
            raise RuntimeError('leader born outside managed group')
        if alive and leader['noNewPrivileges'] is not None and leader['noNewPrivileges'] != '1':
            raise RuntimeError('leader lacks no_new_privs')
        while True:
            hook('poll', context())
            reason = census()
            if reason:
                raise RuntimeError(reason)
            if leader_exited():
                break
            if time.monotonic() >= deadline:
                raise TimeoutError('original deadline exhausted')
            drain(deadline)
        hook('leader-exit-observed', context())
        settle = min(time.monotonic() + retirement['leaderExitSettleSeconds'], deadline)
        while _populated(leaf) and time.monotonic() < settle:
            reason = census()
            if reason:
                raise RuntimeError(reason)
            drain(settle)
        reason = census()
        if reason:
            raise RuntimeError(reason)
        residual = members()
        receipt['residualAfterLeaderExit'] = residual[:SAMPLE]
        if residual and retirement['onLeaderExit'] == 'require-empty':
            raise RuntimeError('leader exited with live contained members')
        code = receipt['exit']
        if code['code'] != 0:
            raise RuntimeError('command failed with ' + (f"exit {code['code']}" if code['code'] is not None else f"signal {code['signal']}"))
    except BaseException as error:
        primary = error
    finally:
        # Retirement runs for every outcome. Each stage is guarded on its own, so no hook,
        # census, output or cgroup failure can skip a later stage such as cgroup.kill.
        stop_at = time.monotonic()
        end = min(stop_at, deadline) + retirement['cleanupSeconds']
        receipt['timing']['stopSeconds'] = round(stop_at - started, 3)

        def note(label, error):
            nonlocal primary
            if primary is None:
                primary = error
            else:
                additional.append(label + ':' + describe(error))

        def stage(label, action):
            try:
                action()
                return True
            except BaseException as error:
                note(label, error)
                return False

        def wait(condition, until):
            while condition() and time.monotonic() < until:
                drain(until)

        def term():
            for pid in list(_tree(leaf)):
                start = known.get(pid)
                if pid != owner and start is not None and _pidfd_signal(pid, start, signal.SIGTERM, in_leaf, mount) == 'sent':
                    retire['termSignals'] += 1

        def kill_leaf():
            relocate_owner()  # the owner must never be inside the leaf it kills
            if _populated(leaf):
                _write(os.path.join(leaf, 'cgroup.kill'), b'1'); retire['cgroupKill'] = True

        def residue():
            return {pid: directory for pid, directory in _tree(droot).items() if pid != owner and not _within(directory, leaf)}

        def created_tops():
            tops = []
            for directory in sorted(_dirs(droot)):
                if directory in preexisting or _within(directory, leaf) or any(_within(directory, t) for t in tops):
                    continue
                tops.append(directory)
            return tops

        def sweep():
            """Retire everything in the delegated root outside the leaf until it is empty or the bound passes."""
            while time.monotonic() < end:
                relocate_owner()
                try:
                    census()
                except RuntimeError:
                    pass
                outside = [key for key, where in escaped.items() if where == 'outside-delegated-root' and _alive(*key)]
                for pid, start in outside:
                    if _pidfd_signal(pid, start, signal.SIGKILL, lambda directory: True, mount) == 'sent':
                        retire['escapedKills'] += 1
                stray = residue()
                if not stray and not outside and not _populated(leaf):
                    return
                if _populated(leaf):
                    kill_leaf()
                for top in created_tops():
                    if _populated(top):
                        _write(os.path.join(top, 'cgroup.kill'), b'1'); retire['createdCgroupKills'] += 1
                internal = [pid for pid, directory in stray.items() if directory in preexisting]
                if internal:
                    # The kernel cannot kill the root or supervisor cgroup without killing the owner:
                    # move those processes into a fresh quarantine cgroup and kill that instead.
                    os.makedirs(quarantine, exist_ok=True)
                    for pid in internal:
                        try:
                            _write(os.path.join(quarantine, 'cgroup.procs'), str(pid).encode()); retire['quarantined'] += 1
                        except OSError:
                            pass
                    _write(os.path.join(quarantine, 'cgroup.kill'), b'1')
                drain(min(end, time.monotonic() + POLL_SECONDS))

        def reap():
            try:
                child.wait(timeout=max(0.0, end - time.monotonic()))
                leader_exited(); retire['leaderReaped'] = True
            except subprocess.TimeoutExpired:
                pass

        stage('hook', lambda: hook('before-retire', context()))
        stage('census', census)
        stage('term', term)
        stage('grace', lambda: wait(lambda: _populated(leaf), min(time.monotonic() + retirement['termGraceSeconds'], end)))
        stage('kill', kill_leaf)
        stage('sweep', sweep)
        stage('settle', lambda: wait(lambda: _populated(leaf), end))
        stage('populated', lambda: retire.__setitem__('populatedAfter', _populated(leaf)))
        if child is not None:
            stage('reap', reap)
            stage('drain', lambda: wait(lambda: any(s['fd'] is not None for s in streams.values()), end))
        stage('hook', lambda: hook('before-remove', context()))
        stage('residue', lambda: receipt.__setitem__('residualInRoot', [
            {'pid': p, 'cgroup': os.path.relpath(d, droot)} for p, d in sorted(residue().items())][:SAMPLE + 1]))
        escaped_alive = [key for key in escaped if _alive(*key)]
        receipt['escaped'] = [{'pid': p, 'start': s, 'cgroup': where, 'aliveAfterRetirement': (p, s) in escaped_alive}
                              for (p, s), where in list(escaped.items())[:SAMPLE]]

        def remove():
            try:
                _remove_tree(leaf, end); retire['leafRemoved'] = True
            except OSError as error:
                retire['removeError'] = describe(error)
            for top in created_tops():
                entry = {'cgroup': os.path.relpath(top, droot), 'removed': False}
                try:
                    _remove_tree(top, end); entry['removed'] = True
                except OSError as error:
                    entry['error'] = describe(error)
                receipt['createdCgroups'].append(entry)
        stage('remove', remove)
        receipt['retirement'] = retire
        for name, s in streams.items():
            if s['fd'] is not None:
                os.close(s['fd']); s['fd'] = None
            if s['sink'] is not None:
                try:
                    s['sink'].close()
                except BaseException as error:
                    additional.append('output-close:' + describe(error))
            receipt['streams'][name] = {'bytes': s['bytes'], 'capped': s['capped'], 'eof': s['eof'],
                                        'sinkError': s['sinkError'], 'sha256': s['sha256'].hexdigest()}
        if pidfd is not None:
            os.close(pidfd)
        if (retire['populatedAfter'] is not False or receipt['residualInRoot'] is None or receipt['residualInRoot']
                or escaped_alive or (child is not None and not retire['leaderReaped'])):
            receipt['cleanup'] = 'unknown'
        elif not retire['leafRemoved'] or any(not c['removed'] for c in receipt['createdCgroups']):
            receipt['cleanup'] = 'observed-empty-remove-failed'
        else:
            receipt['cleanup'] = 'observed-empty-removed'
        if escaped:
            unretired = escaped_alive or receipt['residualInRoot'] is None or bool(receipt['residualInRoot'])
            receipt['custody'] = 'escaped-unretired' if unretired else 'escaped-retired'
        if primary is None and receipt['cleanup'] != 'observed-empty-removed':
            primary = RuntimeError('cleanup unqualified: ' + receipt['cleanup']
                                   + ('' if retire['removeError'] is None else ' (' + retire['removeError'] + ')'))
        if primary is None and not all(s['eof'] for s in receipt['streams'].values()):
            primary = RuntimeError('output streams did not reach EOF within the cleanup bound')
        stage('hook', lambda: hook('after-cleanup', context()))
        receipt['outcome'] = 'failed' if primary is not None else 'succeeded'
        receipt['firstCause'] = describe(primary) if primary is not None else None
        receipt['timing']['endSeconds'] = round(time.monotonic() - started, 3)
        try:
            write_receipt()
        except BaseException as error:
            if primary is None:
                primary = error; receipt['outcome'] = 'failed'; receipt['firstCause'] = describe(error)
            else:
                additional.append('receipt-report:' + describe(error))
    if isinstance(primary, (KeyboardInterrupt, SystemExit)):
        raise primary
    if primary is not None:
        raise GroupFailed(receipt) from primary
    return receipt


def _main(argv):
    import argparse
    parser = argparse.ArgumentParser(prog='managed_group_owner.py')
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('probe'); p.add_argument('--delegated-root')
    r = sub.add_parser('run'); r.add_argument('--profile', required=True); r.add_argument('--out-dir', required=True)
    r.add_argument('--deadline-seconds', type=float, required=True); r.add_argument('--delegated-root')
    args = parser.parse_args(argv)
    if args.command == 'probe':
        result = probe(args.delegated_root)
        print(json.dumps(result, indent=2, sort_keys=True))
        return 0 if result['available'] else 3
    with open(args.profile, 'rb') as source:
        profile = json.loads(source.read(1 << 20))
    env = {key: os.environ[key] for key in profile.get('environmentKeys', []) if key in os.environ}
    try:
        receipt = run(profile, env=env, deadline=time.monotonic() + args.deadline_seconds, out_dir=args.out_dir, delegated_root=args.delegated_root)
    except Refused as refusal:
        print(describe(refusal), file=sys.stderr); return 3
    except GroupFailed as failure:
        print(failure.receipt['firstCause'], file=sys.stderr); return 1
    print(json.dumps({'outcome': receipt['outcome'], 'cleanup': receipt['cleanup']}))
    return 0


if __name__ == '__main__':
    sys.exit(_main(sys.argv[1:]))
