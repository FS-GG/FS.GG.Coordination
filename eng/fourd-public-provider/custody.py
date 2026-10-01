#!/usr/bin/env python3
"""Bounded ciphertext custody for the FourD public provider."""
from __future__ import annotations
import base64, hashlib, json, math, os, re, stat, subprocess, time
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping
CHUNK_BYTES = 8 * 1024 * 1024
MAX_ARCHIVE_BYTES = 4 * 1024 * 1024 * 1024
MAX_CHUNKS = 512
MAX_EVIDENCE_BYTES = 16 * 1024 * 1024
MAX_DESCRIPTOR_BYTES = 4096
MAX_MANIFEST_BYTES = 4 * 1024 * 1024
STAGING_RESERVE = 256 * 1024 * 1024
SEALER_SHA256 = '4f46d5a1762eaee9ba800e5fb58933a9c312e22e4d416b9489e632fd9b2ce85d'
PUBLIC_KEY_SHA256 = 'de40516580a5e6e97c154a8889c3ac6edf047b695ff5d4187386aeaccd61d3e7'
HERE = Path(__file__).resolve().parent
SEALER = HERE / 'seal_native_custody.mjs'
PUBLIC_KEY = HERE / 'root-public.pem'
NODE = Path('/usr/bin/node')
SHA40 = re.compile('[0-9a-f]{40}')
SHA64 = re.compile('[0-9a-f]{64}')
DEC = re.compile('[1-9][0-9]*')
NONCE = re.compile('[a-z0-9][a-z0-9-]{7,47}')
IDENTITY_KEYS = {'schema', 'runId', 'runAttempt', 'runNonce', 'placementSha', 'fourdSourceSha', 'fourdSourceTree', 'fourdInventorySha256', 'p2SourceSha', 'p2SourceTree', 'nativePolicySha256', 'sealerSha256', 'publicKeySha256', 'nativeBindingSha256', 'nativeEvidenceSha256'}
DESC_KEYS = {'schema', 'role', 'seriesId', 'runId', 'runAttempt', 'fourdSourceSha', 'fourdSourceTree', 'nativePolicySha256', 'fullSha256', 'fullBytes', 'index', 'count', 'offset', 'bytes', 'plaintextSha256'}
CAPSULE_KEYS = {'schema', 'algorithm', 'runNonce', 'sourceSha', 'profileSha256', 'aadSha256', 'nonce', 'tag', 'wrappedKey', 'ciphertext'}
CODES = {'custody-argument-refused', 'custody-identity-refused', 'custody-size-refused', 'custody-path-refused', 'custody-source-refused', 'custody-capacity-refused', 'custody-deadline-refused', 'custody-child-refused', 'custody-file-changed', 'custody-descriptor-refused', 'custody-capsule-refused', 'custody-manifest-refused', 'custody-staging-refused', 'custody-cleanup-refused', 'custody-internal-refused'}

class CustodyRefusal(RuntimeError):

    def __init__(self, code):
        super().__init__(code if code in CODES else 'custody-internal-refused')

def refuse(code):
    raise CustodyRefusal(code)

@dataclass(frozen=True)
class ChunkPlan:
    index: int
    offset: int
    bytes: int

@dataclass(frozen=True)
class CustodyResult:
    outcome: str
    archiveComplete: bool
    manifestName: str
    manifestSha256: str
    fileCount: int
    totalBytes: int

@dataclass(frozen=True)
class DirectoryLease:
    path: Path
    fd: int
    device: int
    inode: int

def canonical(v):
    try:
        return (json.dumps(v, sort_keys=True, separators=(',', ':'), ensure_ascii=True) + '\n').encode('ascii')
    except Exception:
        refuse('custody-manifest-refused')

def shab(v):
    return hashlib.sha256(v).hexdigest()

def shap(path, limit=None):
    h = hashlib.sha256()
    n = 0
    try:
        with path.open('rb', buffering=0) as f:
            while (b := f.read(1024 * 1024)):
                n += len(b)
                if limit is not None and n > limit:
                    refuse('custody-size-refused')
                h.update(b)
    except CustodyRefusal:
        raise
    except OSError:
        refuse('custody-source-refused')
    return (h.hexdigest(), n)

def plan_chunks(total_bytes):
    if type(total_bytes) is not int or not 2 <= total_bytes <= MAX_ARCHIVE_BYTES:
        refuse('custody-size-refused')
    count = (total_bytes + CHUNK_BYTES - 1) // CHUNK_BYTES
    if not 1 <= count <= MAX_CHUNKS:
        refuse('custody-size-refused')
    sizes = [CHUNK_BYTES] * (count - 1) + [total_bytes - CHUNK_BYTES * (count - 1)]
    if sizes[-1] == 1:
        if count == 1:
            refuse('custody-size-refused')
        sizes[-2] -= 1
        sizes[-1] = 2
    out = []
    offset = 0
    for i, n in enumerate(sizes):
        if not 2 <= n <= CHUNK_BYTES:
            refuse('custody-size-refused')
        out.append(ChunkPlan(i, offset, n))
        offset += n
    if offset != total_bytes:
        refuse('custody-internal-refused')
    return tuple(out)

def cap_budget(n):
    return 4 * math.ceil(n / 3) + 4096

def required_staging_bytes(archive_bytes, evidence_bytes):
    if type(evidence_bytes) is not int or not 2 <= evidence_bytes <= MAX_EVIDENCE_BYTES:
        refuse('custody-size-refused')
    archive_budget = 0 if archive_bytes is None else sum((cap_budget(p.bytes) for p in plan_chunks(archive_bytes)))
    return archive_budget + cap_budget(evidence_bytes) + CHUNK_BYTES + MAX_MANIFEST_BYTES + STAGING_RESERVE

def _validate_identity(v, has_archive):
    if not isinstance(v, Mapping) or set(v) != IDENTITY_KEYS:
        refuse('custody-identity-refused')
    v = dict(v)
    if v['schema'] != 'fsgg.fourd.custody-identity/1':
        refuse('custody-identity-refused')
    if any((not isinstance(v[k], str) or DEC.fullmatch(v[k]) is None for k in ('runId', 'runAttempt'))):
        refuse('custody-identity-refused')
    if not isinstance(v['runNonce'], str) or NONCE.fullmatch(v['runNonce']) is None:
        refuse('custody-identity-refused')
    if any((not isinstance(v[k], str) or SHA40.fullmatch(v[k]) is None for k in ('placementSha', 'fourdSourceSha', 'fourdSourceTree', 'p2SourceSha', 'p2SourceTree'))):
        refuse('custody-identity-refused')
    if any((not isinstance(v[k], str) or SHA64.fullmatch(v[k]) is None for k in ('fourdInventorySha256', 'nativePolicySha256', 'sealerSha256', 'publicKeySha256', 'nativeEvidenceSha256'))):
        refuse('custody-identity-refused')
    b = v['nativeBindingSha256']
    if has_archive and (not isinstance(b, str) or SHA64.fullmatch(b) is None) or (not has_archive and b is not None):
        refuse('custody-identity-refused')
    if v['sealerSha256'] != SEALER_SHA256 or v['publicKeySha256'] != PUBLIC_KEY_SHA256:
        refuse('custody-identity-refused')
    return v

def own_dir(p, create=False):
    try:
        if create:
            p.mkdir(mode=448, parents=False, exist_ok=False)
        s = p.stat(follow_symlinks=False)
    except OSError:
        refuse('custody-path-refused')
    if not stat.S_ISDIR(s.st_mode) or s.st_uid != os.getuid() or stat.S_IMODE(s.st_mode) != 448:
        refuse('custody-path-refused')

def acquire_dir(p):
    fd = None
    try:
        p.mkdir(mode=448, parents=False, exist_ok=False)
        fd = os.open(p, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_DIRECTORY)
        s = os.fstat(fd)
    except OSError:
        if fd is not None:
            os.close(fd)
        refuse('custody-path-refused')
    if not stat.S_ISDIR(s.st_mode) or s.st_uid != os.getuid() or stat.S_IMODE(s.st_mode) != 448:
        os.close(fd)
        refuse('custody-path-refused')
    return DirectoryLease(p, fd, s.st_dev, s.st_ino)

def open_owned(p, lo, hi):
    try:
        fd = os.open(p, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        s = os.fstat(fd)
    except OSError:
        refuse('custody-source-refused')
    if not stat.S_ISREG(s.st_mode) or s.st_uid != os.getuid() or s.st_nlink != 1 or (stat.S_IMODE(s.st_mode) != 384) or (not lo <= s.st_size <= hi):
        os.close(fd)
        refuse('custody-source-refused')
    return (fd, s)

def fdhash(fd, limit, deadline=None):
    h = hashlib.sha256()
    n = off = 0
    while True:
        if deadline is not None and time.monotonic() >= deadline:
            refuse('custody-deadline-refused')
        b = os.pread(fd, min(1024 * 1024, limit + 1 - n), off)
        if not b:
            break
        n += len(b)
        off += len(b)
        if n > limit:
            refuse('custody-size-refused')
        h.update(b)
    return (h.hexdigest(), n)

def stable(fd, s):
    c = os.fstat(fd)
    if (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns) != (c.st_dev, c.st_ino, c.st_size, c.st_mtime_ns, c.st_ctime_ns):
        refuse('custody-file-changed')

def space(p, n, i):
    try:
        s = os.statvfs(p)
    except OSError:
        refuse('custody-capacity-refused')
    if s.f_bavail * s.f_frsize < n or s.f_favail < i:
        refuse('custody-capacity-refused')

def write_new(p, b):
    try:
        fd = os.open(p, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 384)
        try:
            v = memoryview(b)
            while v:
                w = os.write(fd, v)
                v = v[w:]
            os.fsync(fd)
        finally:
            os.close(fd)
    except OSError:
        refuse('custody-path-refused')

def desc(ident, role, fullsha, fullbytes, p, count, plainsha):
    v = {'schema': 'fsgg.fourd.sealed-chunk/1', 'role': role, 'seriesId': ident['runNonce'], 'runId': ident['runId'], 'runAttempt': ident['runAttempt'], 'fourdSourceSha': ident['fourdSourceSha'], 'fourdSourceTree': ident['fourdSourceTree'], 'nativePolicySha256': ident['nativePolicySha256'], 'fullSha256': fullsha, 'fullBytes': fullbytes, 'index': p.index, 'count': count, 'offset': p.offset, 'bytes': p.bytes, 'plaintextSha256': plainsha}
    raw = canonical(v)
    if set(v) != DESC_KEYS or len(raw) > MAX_DESCRIPTOR_BYTES:
        refuse('custody-descriptor-refused')
    return (v, raw)

def nonce(ident, role, index):
    v = str(ident['runNonce']) + (f'-oci-{index:04d}' if role == 'oci' else '-evidence')
    if len(v) > 64 or re.fullmatch('[a-z0-9][a-z0-9-]{7,63}', v) is None:
        refuse('custody-identity-refused')
    return v

def aad(n, s, p):
    return shab(f'fsgg-native-custody/1\x00{n}\x00{s}\x00{p}'.encode())

def parse_capsule(path, draw, d, ident):
    try:
        raw = path.read_bytes()
        v = json.loads(raw)
    except Exception:
        refuse('custody-capsule-refused')
    profile = shab(draw)
    n = nonce(ident, d['role'], d['index'])
    if not isinstance(v, dict) or set(v) != CAPSULE_KEYS or raw != canonical(v) or (v['schema'] != 'fsgg.telemetry.native-custody-capsule/1') or (v['algorithm'] != 'AES-256-GCM+RSA-OAEP-SHA256') or (v['runNonce'] != n) or (v['sourceSha'] != ident['fourdSourceSha']) or (v['profileSha256'] != profile) or (v['aadSha256'] != aad(n, ident['fourdSourceSha'], profile)):
        refuse('custody-capsule-refused')
    try:
        x = {k: base64.b64decode(v[k], validate=True) for k in ('nonce', 'tag', 'wrappedKey', 'ciphertext')}
    except Exception:
        refuse('custody-capsule-refused')
    if (len(x['nonce']), len(x['tag']), len(x['wrappedKey']), len(x['ciphertext'])) != (12, 16, 384, d['bytes']):
        refuse('custody-capsule-refused')

def wipe(p):
    try:
        if p.exists() and p.is_file() and (not p.is_symlink()):
            n = p.stat().st_size
            with p.open('r+b', buffering=0) as f:
                z = b'\x00' * min(1024 * 1024, max(1, n))
                while n:
                    w = min(n, len(z))
                    f.write(z[:w])
                    n -= w
                f.flush()
                os.fsync(f.fileno())
            p.unlink()
    except OSError:
        refuse('custody-cleanup-refused')

def clean_dir(lease):
    p = lease.path
    try:
        held = os.fstat(lease.fd)
        before = p.stat(follow_symlinks=False)
        if ((held.st_dev, held.st_ino) != (lease.device, lease.inode)
                or not stat.S_ISDIR(before.st_mode) or before.st_uid != os.getuid()
                or stat.S_IMODE(before.st_mode) != 448
                or (before.st_dev, before.st_ino) != (lease.device, lease.inode)):
            refuse('custody-cleanup-refused')
        for name in os.listdir(lease.fd):
            if not isinstance(name, str) or name in ('', '.', '..') or '/' in name:
                refuse('custody-cleanup-refused')
            s = os.stat(name, dir_fd=lease.fd, follow_symlinks=False)
            if not stat.S_ISREG(s.st_mode) or s.st_uid != os.getuid() or s.st_nlink != 1:
                refuse('custody-cleanup-refused')
            os.unlink(name, dir_fd=lease.fd)
        held_after = os.fstat(lease.fd)
        after = p.stat(follow_symlinks=False)
        if ((held_after.st_dev, held_after.st_ino) != (lease.device, lease.inode)
                or (after.st_dev, after.st_ino) != (lease.device, lease.inode)):
            refuse('custody-cleanup-refused')
        p.rmdir()
    except CustodyRefusal:
        raise
    except OSError:
        refuse('custody-cleanup-refused')

def seal_one(fd, p, role, fullsha, fullbytes, count, ident, scratch, output, deadline):
    plain = os.pread(fd, p.bytes, p.offset)
    if len(plain) != p.bytes:
        refuse('custody-file-changed')
    d, draw = desc(ident, role, fullsha, fullbytes, p, count, shab(plain))
    name = f'oci-{p.index:04d}.capsule.json' if role == 'oci' else 'evidence.capsule.json'
    chunk = scratch / 'plaintext.chunk'
    capsule = output / name
    write_new(chunk, plain)
    try:
        left = deadline - time.monotonic()
        if left <= 0:
            refuse('custody-deadline-refused')
        try:
            r = subprocess.run([str(NODE), str(SEALER), 'seal', str(chunk), str(PUBLIC_KEY), str(capsule), nonce(ident, role, p.index), ident['fourdSourceSha'], shab(draw)], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=min(60, left), env={'PATH': '/usr/bin:/bin'})
        except subprocess.TimeoutExpired:
            refuse('custody-deadline-refused')
        except OSError:
            refuse('custody-child-refused')
        if r.returncode or len(r.stdout) > 65536 or len(r.stderr) > 65536 or (not capsule.is_file()):
            refuse('custody-child-refused')
        parse_capsule(capsule, draw, d, ident)
    finally:
        plain = b''
        wipe(chunk)
    h, n = shap(capsule, cap_budget(p.bytes))
    return {'descriptor': d, 'capsuleName': name, 'capsuleBytes': n, 'capsuleSha256': h}

def seal_series(*, archive, evidence, scratch, output, identity: Mapping[str, object], deadline_monotonic):
    ident = _validate_identity(identity, archive is not None)
    if type(deadline_monotonic) not in (int, float) or not math.isfinite(deadline_monotonic) or deadline_monotonic <= time.monotonic():
        refuse('custody-deadline-refused')
    if (not scratch.is_absolute() or not output.is_absolute() or scratch.parent != output.parent
            or scratch == output or scratch.parent.resolve() != scratch.parent
            or scratch.is_symlink() or output.is_symlink()):
        refuse('custody-path-refused')
    own_dir(scratch.parent)
    if shap(SEALER)[0] != SEALER_SHA256 or shap(PUBLIC_KEY)[0] != PUBLIC_KEY_SHA256 or (not NODE.is_file()):
        refuse('custody-source-refused')
    afd = efd = None
    scratch_lease = output_lease = None
    success = False
    try:
        scratch_lease = acquire_dir(scratch)
        output_lease = acquire_dir(output)
        efd, es = open_owned(evidence, 2, MAX_EVIDENCE_BYTES)
        eh, eb = fdhash(efd, MAX_EVIDENCE_BYTES, deadline_monotonic)
        if eh != ident['nativeEvidenceSha256']:
            refuse('custody-source-refused')
        plans = ()
        ah = ab = ast = None
        if archive is not None:
            afd, ast = open_owned(archive, 2, MAX_ARCHIVE_BYTES)
            ah, ab = fdhash(afd, MAX_ARCHIVE_BYTES, deadline_monotonic)
            plans = plan_chunks(ab)
        space(output, required_staging_bytes(ab, eb), len(plans) + 4)
        records = []
        sizes = [p.bytes for p in plans] + [eb]
        for i, p in enumerate(plans):
            space(output, sum((cap_budget(x) for x in sizes[i:])) + CHUNK_BYTES + MAX_MANIFEST_BYTES + STAGING_RESERVE, len(sizes) - i + 3)
            records.append(seal_one(afd, p, 'oci', ah, ab, len(plans), ident, scratch, output, deadline_monotonic))
        space(output, cap_budget(eb) + CHUNK_BYTES + MAX_MANIFEST_BYTES + STAGING_RESERVE, 4)
        records.append(seal_one(efd, ChunkPlan(0, 0, eb), 'evidence', eh, eb, 1, ident, scratch, output, deadline_monotonic))
        stable(efd, es)
        if fdhash(efd, MAX_EVIDENCE_BYTES, deadline_monotonic) != (eh, eb):
            refuse('custody-file-changed')
        if afd is not None:
            stable(afd, ast)
        if afd is not None and fdhash(afd, MAX_ARCHIVE_BYTES, deadline_monotonic) != (ah, ab):
            refuse('custody-file-changed')
        manifest = {'schema': 'fsgg.fourd.sealed-archive-series/1', 'identity': ident, 'archive': None if archive is None else {'sha256': ah, 'bytes': ab, 'chunkCount': len(plans)}, 'evidence': {'sha256': eh, 'bytes': eb}, 'files': records}
        raw = canonical(manifest)
        if len(raw) > MAX_MANIFEST_BYTES:
            refuse('custody-manifest-refused')
        write_new(output / 'manifest.json', raw)
        result = verify_staging(output, ident)
        success = True
        return result
    except CustodyRefusal:
        raise
    except Exception:
        refuse('custody-internal-refused')
    finally:
        if afd is not None:
            os.close(afd)
        if efd is not None:
            os.close(efd)
        cleanup_failed = False
        if scratch_lease is not None:
            try:
                clean_dir(scratch_lease)
            except CustodyRefusal:
                cleanup_failed = True
        if output_lease is not None and (not success or cleanup_failed):
            try:
                clean_dir(output_lease)
            except CustodyRefusal:
                cleanup_failed = True
        for lease in (scratch_lease, output_lease):
            if lease is not None:
                try:
                    os.close(lease.fd)
                except OSError:
                    cleanup_failed = True
        if cleanup_failed:
            refuse('custody-cleanup-refused')

def verify_staging(output, expected_identity):
    try:
        own_dir(output)
        mp = output / 'manifest.json'
        raw = mp.read_bytes()
        if not 1 <= len(raw) <= MAX_MANIFEST_BYTES:
            refuse('custody-manifest-refused')
        m = json.loads(raw)
        present = m.get('archive') is not None
        ident = _validate_identity(expected_identity, present)
        if not isinstance(m, dict) or set(m) != {'schema', 'identity', 'archive', 'evidence', 'files'} or m['schema'] != 'fsgg.fourd.sealed-archive-series/1' or (raw != canonical(m)) or (m['identity'] != ident):
            refuse('custody-manifest-refused')
        ev = m['evidence']
        if not isinstance(ev, dict) or set(ev) != {'sha256', 'bytes'} or type(ev['bytes']) is not int or (not 2 <= ev['bytes'] <= MAX_EVIDENCE_BYTES) or (not isinstance(ev['sha256'], str)) or (SHA64.fullmatch(ev['sha256']) is None):
            refuse('custody-manifest-refused')
        plans = ()
        if present:
            ar = m['archive']
            if not isinstance(ar, dict) or set(ar) != {'sha256', 'bytes', 'chunkCount'}:
                refuse('custody-manifest-refused')
            plans = plan_chunks(ar['bytes'])
            if ar['chunkCount'] != len(plans) or not isinstance(ar['sha256'], str) or SHA64.fullmatch(ar['sha256']) is None:
                refuse('custody-manifest-refused')
        expected = [('oci', p, len(plans), m['archive']) for p in plans] + [('evidence', ChunkPlan(0, 0, ev['bytes']), 1, ev)]
        if not isinstance(m['files'], list) or len(m['files']) != len(expected):
            refuse('custody-manifest-refused')
        names = {'manifest.json'}
        total = len(raw)
        for item, (role, p, count, full) in zip(m['files'], expected, strict=True):
            if not isinstance(item, dict) or set(item) != {'descriptor', 'capsuleName', 'capsuleBytes', 'capsuleSha256'}:
                refuse('custody-manifest-refused')
            d = item['descriptor']
            if not isinstance(d, dict) or set(d) != DESC_KEYS:
                refuse('custody-descriptor-refused')
            want = {'schema': 'fsgg.fourd.sealed-chunk/1', 'role': role, 'seriesId': ident['runNonce'], 'runId': ident['runId'], 'runAttempt': ident['runAttempt'], 'fourdSourceSha': ident['fourdSourceSha'], 'fourdSourceTree': ident['fourdSourceTree'], 'nativePolicySha256': ident['nativePolicySha256'], 'fullSha256': full['sha256'], 'fullBytes': full['bytes'], 'index': p.index, 'count': count, 'offset': p.offset, 'bytes': p.bytes}
            if any((d.get(k) != v for k, v in want.items())) or not isinstance(d['plaintextSha256'], str) or SHA64.fullmatch(d['plaintextSha256']) is None:
                refuse('custody-descriptor-refused')
            name = f'oci-{p.index:04d}.capsule.json' if role == 'oci' else 'evidence.capsule.json'
            if item['capsuleName'] != name or name in names:
                refuse('custody-manifest-refused')
            names.add(name)
            cap = output / name
            s = cap.stat(follow_symlinks=False)
            if not stat.S_ISREG(s.st_mode) or s.st_uid != os.getuid() or s.st_nlink != 1 or (stat.S_IMODE(s.st_mode) != 384) or (s.st_size != item['capsuleBytes']):
                refuse('custody-staging-refused')
            h, n = shap(cap, cap_budget(p.bytes))
            if (h, n) != (item['capsuleSha256'], item['capsuleBytes']):
                refuse('custody-capsule-refused')
            parse_capsule(cap, canonical(d), d, ident)
            total += n
        actual = set()
        for p in output.iterdir():
            s = p.stat(follow_symlinks=False)
            if not stat.S_ISREG(s.st_mode) or s.st_uid != os.getuid() or s.st_nlink != 1 or (stat.S_IMODE(s.st_mode) != 384):
                refuse('custody-staging-refused')
            actual.add(p.name)
        if actual != names:
            refuse('custody-staging-refused')
        return CustodyResult('sealed-complete' if present else 'sealed-evidence-only', present, 'manifest.json', shab(raw), len(actual), total)
    except CustodyRefusal:
        raise
    except Exception:
        refuse('custody-staging-refused')
