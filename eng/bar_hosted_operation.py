#!/usr/bin/env python3
"""Fixed hosted BAR generation preparation and bounded transport receipts.

This wrapper invokes only the existing closed BAR controller. A workflow dispatch
and these files are not operation authority; Programme/root selects each fresh
generation separately. There is no command, retry, resume, or runtime port.
"""
from __future__ import annotations

import argparse
import base64
import configparser
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import math
import resource
import stat
import subprocess
import sys
import time

PRODUCT_REVISION = '4ddd070650e3131916a13bb2c5e75f99376e1c3a'
PRODUCT_TREE = 'f6e4dc108bf065cd2fd44d48e0faf8807e1b8e3c'
HELPER_FILES = {
    'eng/portable-workspace-image.py': '14f519038fd24684b458d917ddb33ab6bada86a084b39c35ce5ae7e8e9fa7aa5',
    'eng/portable_workspace_bar.py': '8cccac4562371b8aa04cf68708769a9fdb89d59ebb35558e707e64dd3b4d24bf',
    'eng/portable_workspace_sc2.py': '19f4b126ace622349e5bff9ae9462e079e13ebf41d5706bb8cc1551a33ee262e',
}
PRODUCT_FILES = {
    'containers/barc-stock-headless/Containerfile': '72ab4ae75d040a2a4f94fdf6a57d910a0a51a07024238cb09d831c8f94c89392',
    'containers/barc-stock-headless/resources.json': 'fdb77eb7887766f14cc8756d7d093bda7a541a9b8b18a00f68d8260b907bb90f',
    'containers/barc-stock-headless/build-image.py': '6996bc4827ee90ce6be77367e8eecdcd8e7b1c063bed22619ed1a22ab280e1da',
    'containers/barc-stock-headless/entrypoint.sh': 'c61f955978bf8f774c9347fa6f91dbaedbacec68daa310adef97bb6d81f07b2a',
    'containers/barc-stock-headless/test_source.py': '7aab4cea6505269f30e27dfee0a7874237c380344ac375e6a0a49fbbe12ebc5b',
    'containers/barc-stock-headless/README.md': '5bf195f4e2cb7ddb7846789b03509d3dfb1f3bfbcba20a81cad2d87301c9d648',
}
SOURCE = Path('/opt/fsgg/source')
ALLOCATION = Path('/fsgg-bar/allocation')
EVIDENCE = Path('/fsgg-bar/evidence')
HOME = Path('/home/runner')
RUNNER_UID = 1001
BUILDER = '/usr/bin/buildah'
ARCHIVE_BYTES = 16 * 1024**3
METADATA_BYTES = 64 * 1024**2
RECEIPT_BYTES = 4 * 1024**2
TRANSPORT_FREE_BYTES = ARCHIVE_BYTES + 4 * METADATA_BYTES
MAX_FILES = 4096
OUTER_SECONDS = 90 * 60
TRANSPORT_SECONDS = 20 * 60
RECIPIENT_PEM_SHA256 = '1be09fdd361e7a531e170eb0a8585bcd3e473937aa260080bce2ad04035d7124'
RECIPIENT_DER_SHA256 = '664cec5127f485ec28c19877cbe260010df99f1f2917068fc2f39dd47eb2184e'
RECIPIENT = Path(__file__).with_name('bar-hosted-recipient.pem')
OPENSSL = Path('/usr/bin/openssl')
CIPHER_BYTES = ARCHIVE_BYTES + METADATA_BYTES
CRYPTO_ENV = {'PATH': '/usr/bin:/bin', 'HOME': '/nonexistent', 'LANG': 'C', 'LC_ALL': 'C',
              'OPENSSL_CONF': '/dev/null', 'OPENSSL_MODULES': '/nonexistent'}
CRYPTO_OBSERVATIONS = []


def require(condition, message):
    if not condition:
        raise RuntimeError('BAR hosted generation: ' + message)


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False) + '\n').encode()


def digest_file(path, maximum, *, deadline=None):
    """Bounded regular-file hash with descriptor/path identity correspondence."""
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, 'rb') as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_size <= maximum, 'nonregular/oversized file: ' + str(path))
        digest, count = hashlib.sha256(), 0
        while count < before.st_size:
            require(deadline is None or time.monotonic() < deadline, 'original outer deadline expired during hash')
            block = stream.read(min(1024**2, before.st_size - count))
            require(block, 'file shortened: ' + str(path))
            count += len(block); digest.update(block)
        after = os.fstat(stream.fileno())
    identity = lambda info: (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
    require(identity(before) == identity(after) == identity(path.stat(follow_symlinks=False)), 'file changed: ' + str(path))
    return {'bytes': count, 'sha256': digest.hexdigest(), 'device': before.st_dev, 'inode': before.st_ino}


def write_new(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'wb') as stream:
        stream.write(value if isinstance(value, bytes) else canonical(value))
        stream.flush(); os.fsync(stream.fileno())


def read_json(path, maximum=1024**2):
    pin = digest_file(path, maximum)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, 'rb') as stream:
        payload = stream.read(maximum + 1)
    require(len(payload) == pin['bytes'] and hashlib.sha256(payload).hexdigest() == pin['sha256'],
            'JSON changed during capture')
    def pairs(rows):
        value = {}
        for key, item in rows:
            require(key not in value, 'duplicate JSON field')
            value[key] = item
        return value
    def constant(value):
        raise ValueError('nonfinite JSON constant: ' + value)
    return json.loads(payload, object_pairs_hook=pairs, parse_constant=constant), pin


def native(argv, deadline, *, file_limit=METADATA_BYTES):
    """Only fixed crypto/loader calls use this private function, never CLI data."""
    remaining = deadline - time.monotonic()
    require(remaining > 0, 'original outer deadline expired')
    def limits():
        def limit(kind, maximum):
            _, inherited = resource.getrlimit(kind)
            value = maximum if inherited == resource.RLIM_INFINITY else min(maximum, inherited)
            resource.setrlimit(kind, (value, value))
        limit(resource.RLIMIT_AS, 1024**3)
        cpu = max(1, min(300, math.ceil(remaining)))
        limit(resource.RLIMIT_CPU, cpu)
        limit(resource.RLIMIT_FSIZE, file_limit)
    require(len(CRYPTO_OBSERVATIONS) < 32, 'crypto command-count bound exceeded')
    started = time.monotonic()
    try:
        done = subprocess.run(argv, stdin=subprocess.DEVNULL, capture_output=True, env=CRYPTO_ENV,
                              timeout=min(300, remaining), preexec_fn=limits)
        output, error, exit_code = done.stdout, done.stderr, done.returncode
    except subprocess.TimeoutExpired as failure:
        output, error, exit_code = failure.stdout or b'', failure.stderr or b'', None
    require(len(output) + len(error) <= 65536, 'crypto diagnostic capture bound exceeded')
    CRYPTO_OBSERVATIONS.append({'argv': argv, 'startedMonotonic': started, 'completedMonotonic': time.monotonic(),
        'originalDeadlineMonotonic': deadline, 'exit': exit_code, 'timeout': exit_code is None,
        'stdoutBase64': base64.b64encode(output).decode(), 'stderrBase64': base64.b64encode(error).decode()})
    require(exit_code is not None, 'crypto command timed out within original deadline')
    require(done.returncode == 0, 'crypto command failed: ' + done.stderr.decode(errors='replace')[-512:])
    return done.stdout


def crypto_identity(deadline):
    require(not Path('/etc/ld.so.preload').exists(), 'unexpected loader preload configuration')
    loader = Path('/lib64/ld-linux-x86-64.so.2').resolve(strict=True)
    listed = native([str(loader), '--list', str(OPENSSL)], deadline).decode()
    require('not found' not in listed, 'OpenSSL dependency unavailable')
    paths = {loader, OPENSSL.resolve(strict=True)}
    for line in listed.splitlines():
        match = re.search(r'(?:=>\s+|^\s*)(/[^\s]+)', line)
        if match:
            paths.add(Path(match[1]).resolve(strict=True))
    require(3 <= len(paths) <= 32, 'OpenSSL library census differs')
    if Path('/etc/ld.so.cache').exists():
        paths.add(Path('/etc/ld.so.cache'))
    files = {}
    for path in sorted(paths):
        info = path.stat()
        require(info.st_uid == 0 and not info.st_mode & 0o022, 'untrusted crypto executable/library')
        files[str(path)] = digest_file(path, 256 * 1024**2, deadline=deadline)
    version = native([str(OPENSSL), 'version'], deadline).decode().strip()
    require(version.startswith('OpenSSL 3.'), 'unsupported OpenSSL family')
    return {'files': files, 'version': version, 'environment': CRYPTO_ENV}


def seal_crypto(operation_id, *, evidence=EVIDENCE):
    require(re.fullmatch(r'bar-image-[0-9]{1,20}-[0-9]{1,5}', operation_id) is not None, 'fixed run/attempt identity required')
    started = float(os.environ.get('HOST_JOB_MONOTONIC', 'nan'))
    require(math.isfinite(started) and 0 <= started <= time.monotonic(), 'original monotonic job clock required')
    deadline = started + OUTER_SECONDS
    identity = crypto_identity(deadline)
    require(digest_file(RECIPIENT, 8192)['sha256'] == RECIPIENT_PEM_SHA256, 'public recipient PEM differs')
    der = native([str(OPENSSL), 'x509', '-in', str(RECIPIENT), '-outform', 'DER'], deadline)
    require(hashlib.sha256(der).hexdigest() == RECIPIENT_DER_SHA256, 'public recipient DER fingerprint differs')
    description = native([str(OPENSSL), 'x509', '-in', str(RECIPIENT), '-noout', '-text'], deadline).decode()
    require('Public Key Algorithm: rsaEncryption' in description and 'Public-Key: (3072 bit)' in description,
            'selected RSA3072 recipient required')
    value = {'operationId': operation_id, 'bootId': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
             'outerStartMonotonic': started, 'outerDeadlineMonotonic': deadline,
             'recipientPemSha256': RECIPIENT_PEM_SHA256, 'recipientDerSha256': RECIPIENT_DER_SHA256,
             'identity': identity, 'scheme': 'CMS AuthEnvelopedData AES-256-GCM; RSA-OAEP SHA256/MGF1-SHA256',
             'encoding': 'binary BER streaming, OpenSSL outform DER CLI', 'privateKeyOnRunner': False,
             'commands': list(CRYPTO_OBSERVATIONS)}
    write_new(evidence / 'transport-seal.json', value)
    return value


def encryption_argv(candidate, ciphertext):
    return [str(OPENSSL), 'cms', '-provider', 'default', '-encrypt', '-aes-256-gcm', '-binary', '-stream', '-in', str(candidate),
            '-out', str(ciphertext), '-outform', 'DER', '-recip', str(RECIPIENT),
            '-keyopt', 'rsa_padding_mode:oaep', '-keyopt', 'rsa_oaep_md:sha256', '-keyopt', 'rsa_mgf1_md:sha256']


def encrypt_candidate(operation_id, candidate, archive, *, evidence=EVIDENCE, transport=Path('/fsgg-bar/transport-candidate')):
    seal, pin = read_json(evidence / 'transport-seal.json')
    require(seal['operationId'] == operation_id and seal['bootId'] == Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
            'crypto seal operation/boot differs')
    require(seal['outerDeadlineMonotonic'] == seal['outerStartMonotonic'] + OUTER_SECONDS, 'original crypto deadline differs')
    deadline = seal['outerDeadlineMonotonic']
    require(crypto_identity(deadline) == seal['identity'], 'OpenSSL executable/library identity changed')
    require(digest_file(RECIPIENT, 8192)['sha256'] == seal['recipientPemSha256'] == RECIPIENT_PEM_SHA256
            and seal['recipientDerSha256'] == RECIPIENT_DER_SHA256, 'recipient changed after seal')
    require(digest_file(candidate, ARCHIVE_BYTES, deadline=deadline) == archive, 'original archive changed before encryption')
    stage = transport / operation_id
    require(not stage.exists(), 'ciphertext generation already consumed')
    private_dir(stage)
    ciphertext = stage / 'candidate.cms'
    write_new(ciphertext, b'')
    native(encryption_argv(candidate, ciphertext), deadline, file_limit=CIPHER_BYTES)
    require(digest_file(candidate, ARCHIVE_BYTES, deadline=deadline) == archive, 'original archive changed during encryption')
    require(crypto_identity(deadline) == seal['identity'], 'OpenSSL identity changed during encryption')
    require(digest_file(evidence / 'transport-seal.json', 1024**2) == pin, 'original crypto seal changed')
    encrypted = digest_file(ciphertext, CIPHER_BYTES, deadline=deadline)
    require(0 < encrypted['bytes'] <= archive['bytes'] + METADATA_BYTES, 'ciphertext overhead bound exceeded')
    require(time.monotonic() < deadline, 'original outer deadline expired after encryption')
    return {'path': str(ciphertext), **encrypted, 'recipientDerSha256': RECIPIENT_DER_SHA256,
            'transportSealSha256': pin['sha256'], 'scheme': seal['scheme'], 'encoding': seal['encoding']}


def copy_pinned(source, target, expected):
    """Copy only the already bounded metadata bytes; refuse replacement/growth."""
    descriptor = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, 'rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and (info.st_dev, info.st_ino, info.st_size) ==
                (expected['device'], expected['inode'], expected['bytes']), 'metadata object changed before staging')
        digest, remaining = hashlib.sha256(), expected['bytes']
        descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'wb') as output:
            while remaining:
                block = stream.read(min(1024**2, remaining))
                require(block, 'metadata shortened during staging')
                remaining -= len(block); digest.update(block); output.write(block)
            output.flush(); os.fsync(output.fileno())
    require(digest.hexdigest() == expected['sha256'] and digest_file(source, expected['bytes']) == expected,
            'metadata changed during staging')


def private_dir(path):
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    info = path.stat(follow_symlinks=False)
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700,
            'directory must be owned, private and nonlinked: ' + str(path))


def toml(value, prefix=''):
    """Serialize only the fixed namespace's primitive tables, with quoted keys."""
    def scalar(item):
        if isinstance(item, dict):
            return '{' + ', '.join(json.dumps(key) + ' = ' + scalar(value) for key, value in item.items()) + '}'
        if isinstance(item, list):
            return '[' + ', '.join(map(scalar, item)) + ']'
        require(type(item) in (str, int, bool), 'unsupported namespace configuration value')
        return json.dumps(item)
    lines = []
    for key, item in value.items():
        if not isinstance(item, dict):
            lines.append(json.dumps(key) + ' = ' + scalar(item))
    for key, item in value.items():
        if isinstance(item, dict):
            name = (prefix + '.' if prefix else '') + json.dumps(key)
            lines.extend(['[' + name + ']', toml(item, name)])
    return '\n'.join(lines) + '\n'


def load_bar(source):
    for name, expected in HELPER_FILES.items():
        require(digest_file(source / name, 1024**2)['sha256'] == expected, 'closed controller source pin differs: ' + name)
    spec = importlib.util.spec_from_file_location('hosted_bar_profile', source / 'eng/portable_workspace_bar.py')
    bar = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bar)
    return bar


def canonicalize_checkout_gc(root, revision, tree, origin, bar, *, complete=False):
    """Remove only the pinned checkout action's exact local gc.auto=0.

    This is a one-time bootstrap for two fresh internal checkouts, not a config
    cleaner. Any other unexpected config, ambiguity or changed source refuses.
    Global/system config and credentials are neither read nor edited by Git.
    """
    require(re.fullmatch(r'[0-9a-f]{40}', revision) is not None, 'exact checkout revision required')
    git_dir = root / '.git'
    require(git_dir.is_dir() and not git_dir.is_symlink(), 'fresh internal Git directory required')
    for name in ('shallow', 'commondir', 'objects/info/alternates', 'refs/replace', 'info/grafts'):
        require(not (git_dir / name).exists(), 'checkout is incomplete or externally redirected')
    for name in ('objects', 'refs', 'info', 'hooks'):
        bar.shared.path(str(git_dir / name))
    if (git_dir / 'hooks').exists():
        with os.scandir(git_dir / 'hooks') as hooks:
            for count, hook in enumerate(hooks, 1):
                require(count <= 32 and hook.name.endswith('.sample')
                        and stat.S_ISREG(hook.stat(follow_symlinks=False).st_mode),
                        'fresh checkout has an active/nonregular Git hook')
    config_path = git_dir / 'config'
    token = bar.file_token(config_path)
    raw, _ = bar.regular_bytes(config_path, 1024**2)
    require(bar.file_token(config_path) == token, 'local checkout config changed during capture')
    parser = configparser.ConfigParser(interpolation=None, strict=True)
    try:
        parser.read_string(raw.decode())
    except (configparser.Error, UnicodeError):
        raise RuntimeError('BAR hosted generation: ambiguous or malformed local checkout config') from None
    require(parser.has_section('gc') and dict(parser['gc']) == {'auto': '0'}, 'expected singleton local gc.auto=0 required')
    for section in parser.sections():
        if section == 'gc':
            continue
        allowed = ({'repositoryformatversion', 'filemode', 'bare', 'logallrefupdates', 'symlinks',
                    'ignorecase', 'precomposeunicode'} if section == 'core' else
                   {'url', 'fetch'} if section == 'remote "origin"' else
                   {'remote', 'merge'} if section.startswith('branch "') else set())
        require(allowed and set(parser[section]) <= allowed, 'unexpected checkout configuration remains')
    require(parser.get('remote "origin"', 'url', fallback='') in (origin, origin + '.git'), 'checkout origin differs')
    # git config writes this exact block on both pinned fresh action checkouts.
    # An alternative representation is not silently normalized.
    block = b'[gc]\n\tauto = 0\n'
    require(raw.count(block) == 1, 'checkout-created gc.auto representation differs')
    expected = raw.replace(block, b'', 1)
    environment = {'PATH': '/usr/bin:/bin', 'HOME': '/nonexistent', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
                   'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}
    prefix = ['/usr/bin/git', '--no-optional-locks', '--no-replace-objects', '-c', 'core.fsmonitor=false',
              '-c', 'core.hooksPath=/dev/null', '-C', str(root)]
    def command(label, argv, seconds):
        result = subprocess.run(argv, capture_output=True, stdin=subprocess.DEVNULL, env=environment,
                                timeout=min(seconds, 30), check=False)
        require(len(result.stdout) + len(result.stderr) <= 8 * 1024**2, 'checkout diagnostic bound exceeded')
        require(result.returncode == 0, 'checkout bootstrap Git command refused: ' + label)
        return argparse.Namespace(stdout=result.stdout.decode(), stderr=result.stderr.decode())
    def git(*args):
        return command('checkout-config', prefix + list(args), 30).stdout.strip()
    require(git('config', '--local', '--get-all', 'gc.auto') == '0', 'ambiguous local gc.auto provenance')
    require(git('rev-parse', 'HEAD') == revision, 'fresh checkout revision differs')
    actual_tree = git('rev-parse', revision + '^{tree}')
    require(tree is None or actual_tree == tree, 'fresh checkout tree differs')
    require(git('status', '--porcelain', '--untracked-files=all', '--ignored') == '', 'fresh checkout is dirty')
    require(bar.file_token(config_path) == token, 'local checkout config changed before targeted removal')
    git('config', '--local', '--unset-all', 'gc.auto', '^0$')
    after, _ = bar.regular_bytes(config_path, 1024**2)
    require(after == expected, 'non-target checkout configuration bytes changed')
    bar.source_identity({}, root, revision, actual_tree, command=command, complete=complete)
    return {'revision': revision, 'tree': actual_tree, 'removedLocalKey': 'gc.auto', 'expectedValue': '0',
            'beforeSha256': hashlib.sha256(raw).hexdigest(), 'afterSha256': hashlib.sha256(after).hexdigest(),
            'otherConfigBytesUnchanged': True, 'strictSourceIdentity': 'passed'}


def canonicalize_checkouts(revision):
    require(os.environ.get('GITHUB_EVENT_NAME') == 'workflow_dispatch'
            and os.environ.get('GITHUB_REF') == 'refs/heads/main'
            and os.environ.get('GITHUB_RUN_ATTEMPT') == '1'
            and os.environ.get('GITHUB_REPOSITORY') == 'FS-GG/FS.GG.Coordination',
            'only the fixed first-attempt hosted checkout bootstrap is supported')
    workspace = Path(os.environ['GITHUB_WORKSPACE'])
    source = workspace / 'source'
    bar = load_bar(source)
    receipt = {'source': None, 'product': None, 'firstRefusal': None}
    try:
        receipt['source'] = canonicalize_checkout_gc(source, revision, None, 'https://github.com/FS-GG/FS.GG.Coordination', bar)
        receipt['product'] = canonicalize_checkout_gc(workspace / 'product', PRODUCT_REVISION, PRODUCT_TREE,
                                                     'https://github.com/FS-GG/FSBarV2', bar, complete=True)
    except Exception as error:
        receipt['firstRefusal'] = type(error).__name__ + ': ' + str(error)[:512]
        write_new(workspace / 'evidence/checkout-config-refusal.json', receipt)
        print(json.dumps(receipt, sort_keys=True), file=sys.stderr)
        raise
    return receipt


def prepare(operation_id, revision, *, source=SOURCE, allocation=ALLOCATION, evidence=EVIDENCE, home=HOME, bar=None, purpose='full-image'):
    require(re.fullmatch(r'bar-image-[0-9]{1,20}-[0-9]{1,5}', operation_id) is not None, 'fixed run/attempt identity required')
    require(re.fullmatch(r'[0-9a-f]{40}', revision) is not None, 'exact helper revision required')
    require(re.fullmatch(r'[0-9a-f]{40}', os.environ.get('HELPER_TREE', '')) is not None, 'exact helper tree required')
    require(os.getuid() == RUNNER_UID, 'unprivileged uid1001 required')
    bar = load_bar(source) if bar is None else bar
    require(purpose in ('full-image', 'network-prerequisite'), 'fixed purpose required')
    facts_pin = digest_file(evidence / 'facts.env', 65536)
    facts_bytes = (evidence / 'facts.env').read_bytes()
    require(len(facts_bytes) == facts_pin['bytes'] and hashlib.sha256(facts_bytes).hexdigest() == facts_pin['sha256'],
            'installed package facts changed')
    facts = facts_bytes.decode()
    require(facts.splitlines().count('userland.pastaPackage=passt 2026_10_02.cba3570-1') == 1,
            'signed installed pasta package identity differs')
    security, security_pin = read_json(evidence / 'root-security.json')
    require(security.get('bootId') == Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
            'fresh root security observation required')
    started = float(os.environ.get('HOST_JOB_MONOTONIC', 'nan'))
    require(math.isfinite(started), 'original outer job clock required')
    elapsed = time.monotonic() - started
    require(0 <= elapsed and elapsed + bar.WORK_SECONDS + bar.RETIRE_SECONDS + TRANSPORT_SECONDS <= OUTER_SECONDS,
            'remaining outer job budget cannot contain full controller and transport apertures')
    product = allocation / 'context'
    for name, expected in PRODUCT_FILES.items():
        require(digest_file(product / name, 1024**2)['sha256'] == expected, 'product source pin differs: ' + name)
    domain_path = evidence / 'domain.json'
    domain_pin = digest_file(domain_path, 1024**2)
    domain, _ = read_json(domain_path)
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    require(domain.get('verdict') == 'admitted' and domain.get('refusals') == [] and domain.get('bootId') == boot,
            'fresh root domain admission required')
    require(domain.get('runner', {}).get('imageVersion') == bar.HOSTED_RUNNER_IMAGE_VERSION, 'runner image drift')
    crypto_seal, crypto_pin = read_json(evidence / 'transport-seal.json')
    require(crypto_seal.get('operationId') == operation_id and crypto_seal.get('bootId') == boot
            and crypto_seal.get('outerStartMonotonic') == started
            and crypto_seal.get('outerDeadlineMonotonic') == started + OUTER_SECONDS
            and crypto_seal.get('recipientDerSha256') == RECIPIENT_DER_SHA256,
            'original recipient/crypto deadline seal required')
    require(not (allocation / 'state' / operation_id).exists(), 'consumed operation cannot be prepared again')
    build_root, build_run = allocation / 'build/graphroot', allocation / 'build/runroot'
    load_root, load_run = home / '.local/share/fsgg/headless-images/bar' / operation_id, allocation / 'load/runroot'
    for directory in (allocation / 'state', build_root, build_run, load_root, load_run):
        private_dir(directory)
    require(all(not any(value.iterdir()) for value in (build_root, build_run, load_root, load_run)), 'generation stores must be empty')
    for name, graph, runtime in [('build', build_root, build_run), ('load', load_root, load_run)]:
        paths, _, configs = bar.namespace(allocation / name, graph, runtime)
        for directory in paths.values():
            private_dir(directory)
        private_dir(paths['config'] / 'containers')
        for name, content in configs.items():
            write_new(paths['config'] / 'containers' / name, canonical(content) if name.endswith('.json') else toml(content).encode())
    support_paths = {'builder': BUILDER, 'python': str(Path(sys.executable).resolve()),
                     **bar.SUPPORT_EXECUTABLES, **bar.SUPPORT_LIBRARIES}
    support = {}
    for role, name in support_paths.items():
        actual = Path(name).resolve(strict=True)
        support[role] = {'path': str(actual), 'sha256': digest_file(actual, 256 * 1024**2)['sha256']}
    topology = evidence / 'topology.json'
    disposition = evidence / 'resource-disposition.json'
    write_new(topology, {'relation': bar.HOSTED_RELATION, 'bootId': boot, 'volumeDevice': domain['volume']['device'],
                         'allocation': str(allocation), 'loadRoot': str(load_root), 'operationId': operation_id,
                         'transportSealSha256': crypto_pin['sha256']})
    write_new(disposition, {'sharedHostReservedMemoryBytes': 0, 'sharedHostReservedCpu': 0,
                            'sharedHostReservedDiskBytes': 0, 'unquantifiedResourceConflicts': []})
    reference = lambda path: {'path': str(path), 'sha256': digest_file(path, 1024**2)['sha256']}
    topology_ref, disposition_ref = reference(topology), reference(disposition)
    start = time.monotonic()
    selection = {'schema': 'fsgg.bar-headless-operation/1', 'operationId': operation_id,
        'helper': {'revision': revision, 'tree': os.environ['HELPER_TREE'], 'files': HELPER_FILES},
        'product': {'revision': PRODUCT_REVISION, 'tree': PRODUCT_TREE, 'files': PRODUCT_FILES,
                    'baseImage': bar.BASE_IMAGE, 'localTag': 'localhost/fsgg/bar:' + operation_id, 'exportName': operation_id},
        'allocation': {'path': str(allocation), 'identity': bar.directory_pin(allocation)},
        'build': {'root': str(build_root), 'runroot': str(build_run)}, 'load': {'root': str(load_root), 'runroot': str(load_run)},
        'network': {'purpose': purpose, 'seed': bar.network_seed()},
        'support': support, 'clock': {'bootId': boot, 'start': start, 'workEnd': start + bar.WORK_SECONDS,
                                    'totalEnd': start + bar.WORK_SECONDS + bar.RETIRE_SECONDS},
        'admission': {'topology': topology_ref, 'resourceDisposition': disposition_ref,
                      'security': {'path': str(evidence / 'root-security.json'), 'sha256': security_pin['sha256']},
                      'domain': {'path': str(domain_path), 'sha256': domain_pin['sha256']}},
        'resourceDomain': {'relation': bar.HOSTED_RELATION, 'topologySha256': topology_ref['sha256'],
                           'domainEvidenceSha256': domain_pin['sha256']}, 'licenseAccepted': True}
    target = evidence / 'operation-input.json'
    write_new(target, selection)
    return selection, target, digest_file(target, 1024**2)['sha256']


def controller_argv(selection, target, digest, *, source=SOURCE, allocation=ALLOCATION):
    return [str(Path(sys.executable).resolve()), '-I', '-B', str(source / 'eng/portable-workspace-image.py'),
        ('preflight' if selection['network']['purpose'] == 'network-prerequisite' else 'qualify'), '--profile', 'bar-headless', '--source', str(source), '--expected-source-revision', selection['helper']['revision'],
        '--image-source', str(allocation / 'context'), '--expected-image-source-revision', PRODUCT_REVISION,
        '--engine-allocation', str(allocation), '--engine-scratch-dir', str(allocation / 'build/download-tmp'),
        '--state-dir', str(allocation / 'state'), '--root', selection['build']['root'], '--runroot', selection['build']['runroot'],
        '--operation-input', str(target), '--expected-operation-input-sha256', digest, '--podman', '/usr/bin/podman']


def optional_security_readback(stage, operation_id):
    """Optional diagnostics never determine primary capture/transport success.

    The privileged producer writes outside canonical evidence. Only bounded,
    readable ordinary data is embedded; partial/unreadable output stays Unknown.
    """
    result = {'status': 'Unknown', 'absenceConclusion': 'Unknown', 'exit': None}
    if stage is None:
        return {**result, 'reason': 'optional diagnostic stage unavailable'}
    try:
        stage = Path(stage)
        require(stage.parent == Path(os.environ['RUNNER_TEMP'])
                and stage.name == 'fsgg-' + operation_id + '-security', 'fixed security stage required')
        info = stage.lstat()
        require(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
                and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700,
                'private owned security stage required')
        def ordinary(name, maximum):
            target = stage / name
            require(target.lstat().st_nlink == 1, 'linked optional diagnostic refused')
            pin = digest_file(target, maximum)
            descriptor = os.open(target, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(descriptor, 'rb') as stream:
                opened = os.fstat(stream.fileno())
                require(stat.S_ISREG(opened.st_mode) and opened.st_nlink == 1
                        and (opened.st_dev, opened.st_ino) == (pin['device'], pin['inode']),
                        'optional diagnostic object changed')
                payload = stream.read(maximum + 1)
            require(len(payload) == pin['bytes'] and hashlib.sha256(payload).hexdigest() == pin['sha256'],
                    'optional diagnostic changed')
            return payload
        code = ordinary('status', 16).decode().strip()
        require(re.fullmatch(r'[0-9]{1,3}', code) is not None and int(code) <= 255,
                'optional diagnostic status malformed')
        result['exit'] = int(code)
        result['stderr'] = ordinary('stderr', 65536).decode(errors='replace')[-4096:]
        if result['exit'] != 0:
            return {**result, 'reason': 'optional privileged readback failed'}
        ordinary('readback.json', 256 * 1024)
        data, _ = read_json(stage / 'readback.json', 256 * 1024)
        require(type(data) is dict and data.get('security', {}).get('bootId') ==
                Path('/proc/sys/kernel/random/boot_id').read_text().strip(), 'optional readback boot differs')
        return {**result, 'status': 'observed', 'data': data}
    except Exception as error:
        return {**result, 'reason': type(error).__name__ + ': ' + str(error)[:512]}


def receipt(operation_id, *, allocation=ALLOCATION, evidence=EVIDENCE, transport=Path('/fsgg-bar/transport-metadata'),
            security_stage=None):
    """Retain bounded metadata and authorize transport only for verified success.

    No store is modified or pruned. A failed/oversized candidate is described but
    is never uploaded as a qualified image. Original failure bytes stay intact.
    """
    require(re.fullmatch(r'bar-image-[0-9]{1,20}-[0-9]{1,5}', operation_id) is not None, 'fixed run/attempt identity required')
    state = allocation / 'state' / operation_id
    candidate = state / 'candidate.oci.tar'
    files, total, entries, copies = {}, 0, 0, []
    for label, root in [('evidence', evidence), ('state', state)]:
        if not root.exists():
            continue
        require(stat.S_ISDIR(root.stat(follow_symlinks=False).st_mode), 'linked/non-directory artifact root')
        pending, paths = [root], []
        while pending:
            with os.scandir(pending.pop()) as children:
                for child in children:
                    entries += 1
                    require(entries < MAX_FILES, 'metadata entry-count bound exceeded')
                    path = Path(child.path)
                    if path == candidate:
                        continue
                    require(not child.is_symlink(), 'linked artifact refused')
                    paths.append(path)
                    if child.is_dir(follow_symlinks=False):
                        pending.append(path)
        for path in sorted(paths):
            info = path.stat(follow_symlinks=False)
            require(not stat.S_ISLNK(info.st_mode), 'linked artifact refused')
            if stat.S_ISDIR(info.st_mode) or path == candidate or path.name in ('receipt-result.json', 'receipt.stderr.log'):
                continue
            require(total + info.st_size <= METADATA_BYTES - RECEIPT_BYTES, 'metadata transport bound exceeded')
            value = digest_file(path, METADATA_BYTES - RECEIPT_BYTES - total)
            total += value['bytes']; files[str(path)] = value
            copies.append((path, Path(label) / path.relative_to(root), value))
    result_path = evidence / 'qualification-result.json'
    refusals = []
    try:
        result = read_json(result_path)[0] if result_path.exists() else None
    except (ValueError, OSError, RuntimeError) as error:
        result = None
        refusals.append('qualification result unavailable/malformed: ' + str(error)[:256])
    success = (isinstance(result, dict)
               and result.get('operationId') == operation_id
               and result.get('scope') == 'ordinary sampled profile; native/product and whole descendant acceptance separate'
               and re.fullmatch(r'sha256:[0-9a-f]{64}', result.get('imageId', '')) is not None
               and result.get('archive') == str(candidate))
    archive, encrypted, free = None, None, None
    if success:
        try:
            manifest_path = Path(result.get('manifest', ''))
            require(manifest_path.parent == state / 'manifests', 'result manifest outside original generation')
            manifest, manifest_pin = read_json(manifest_path)
            require(manifest_path.name == 'sha256-' + manifest_pin['sha256'] + '.json', 'manifest content address differs')
            selection_path = evidence / 'operation-input.json'
            selection, selection_pin = read_json(selection_path)
            require(selection.get('network', {}).get('purpose') == 'full-image', 'prerequisite cannot authorize candidate transport')
            require(manifest.get('profile') == 'bar-headless' and manifest.get('operationId') == operation_id
                    and manifest.get('firstCause') is None and manifest.get('result', {}).get('imageId') == result['imageId']
                    and manifest.get('result', {}).get('archiveSha256') == result.get('archiveSha256')
                    and manifest.get('inputSha256') == selection_pin['sha256']
                    and manifest.get('helper') == selection.get('helper') and manifest.get('product') == selection.get('product')
                    and manifest.get('clock') == selection.get('clock'),
                    'successful controller manifest correspondence differs')
            topology, topology_pin = read_json(evidence / 'topology.json')
            seal, seal_pin = read_json(evidence / 'transport-seal.json')
            require(topology_pin['sha256'] == selection['admission']['topology']['sha256']
                    and topology['transportSealSha256'] == seal_pin['sha256'], 'original crypto seal/topology join differs')
            archive = digest_file(candidate, ARCHIVE_BYTES, deadline=seal['outerDeadlineMonotonic'])
            require(archive['bytes'] > 0 and archive['sha256'] == result.get('archiveSha256'),
                    'successful archive identity mismatch')
            free = os.statvfs(allocation).f_bavail * os.statvfs(allocation).f_frsize
            # The original archive is read in place; additional space covers
            # ciphertext, bounded metadata copies and streaming ZIP overhead.
            require(free >= TRANSPORT_FREE_BYTES, 'full-candidate transport staging headroom unavailable')
            encrypted = encrypt_candidate(operation_id, candidate, archive, evidence=evidence)
        except Exception as error:
            # Candidate-only refusals do not discard independently safe original
            # controller evidence. Metadata safety checks above remain strict.
            success = False
            refusals.append(type(error).__name__ + ': ' + str(error)[:512])
    prerequisite_passed = False
    purpose = 'Unknown'
    try:
        selected, selected_pin = read_json(evidence / 'operation-input.json')
        purpose = selected.get('network', {}).get('purpose', 'Unknown')
        if purpose == 'network-prerequisite':
            require(not candidate.exists(), 'prerequisite produced a candidate')
            require(not success and not encrypted, 'prerequisite candidate permission refused')
            if isinstance(result, dict) and result.get('preflight') == 'passed':
                manifest_path = Path(result.get('manifest', ''))
                require(manifest_path.parent == state / 'manifests', 'prerequisite manifest outside original')
                manifest, manifest_pin = read_json(manifest_path)
                require(manifest_path.name == 'sha256-' + manifest_pin['sha256'] + '.json'
                        and manifest.get('inputSha256') == selected_pin['sha256']
                        and manifest.get('operationId') == result.get('operationId') == operation_id
                        and manifest.get('profile') == result.get('profile') == 'bar-headless'
                        and manifest.get('firstCause') is None
                        and manifest.get('result', {}).get('preflight') == 'passed'
                        and manifest.get('result', {}).get('candidateQualified') is False
                        and result.get('candidateQualified') is False
                        and manifest['result'].get('networkPrerequisite') == result.get('networkPrerequisite')
                        and manifest.get('helper') == selected.get('helper')
                        and manifest.get('product') == selected.get('product')
                        and manifest.get('clock') == selected.get('clock'),
                        'prerequisite manifest/input/result differs')
                prerequisite_passed = True
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        refusals.append('prerequisite correspondence: ' + str(error)[:256])
    observation = {'exists': False, 'bytes': None, 'regular': None}
    try:
        info = candidate.stat(follow_symlinks=False)
        observation.update(exists=True, bytes=info.st_size, regular=stat.S_ISREG(info.st_mode))
    except OSError as error:
        observation['unavailable'] = str(error)[:256]
    observation['hashStatus'] = 'bounded hash retained' if archive is not None else 'not retained: absent, unqualified or refused'
    value = {'operationId': operation_id, 'purpose': purpose, 'networkPrerequisitePassed': prerequisite_passed, 'metadataBytes': total, 'metadata': files, 'candidate': archive,
             'metadataTransportCeilingBytes': total + RECEIPT_BYTES,
             'candidateObservation': observation,
             'candidateTransportPermitted': success, 'candidatePath': str(candidate), 'transportFreeBytes': free,
             'encryptedCandidate': encrypted,
             'cryptoCommands': list(CRYPTO_OBSERVATIONS),
             'transportRefusals': refusals,
             'transportStagingAllowanceBytes': TRANSPORT_FREE_BYTES,
             'scope': 'exported image bytes and controller evidence; no gameplay or loaded ABI acceptance',
             'providerPhysicalDisposal': 'Unknown'}
    secondary = optional_security_readback(security_stage, operation_id)
    # Primary evidence has precedence even if optional observations exceed the
    # remaining receipt aperture. Absence never asserts an LSM absence.
    if len(canonical({**value, 'securityReadback': secondary})) <= RECEIPT_BYTES:
        value['securityReadback'] = secondary
    elif len(canonical({**value, 'securityReadback': {'status': 'Unknown', 'reason': 'optional receipt budget'}})) <= RECEIPT_BYTES:
        value['securityReadback'] = {'status': 'Unknown', 'reason': 'optional receipt budget'}
    require(len(canonical(value)) <= RECEIPT_BYTES, 'transport receipt byte bound exceeded')
    # Transport contains only verified, copied metadata. The uploader never
    # globs an unchecked generation directory after a receipt failure.
    stage = transport / operation_id
    require(not stage.exists(), 'transport generation already consumed')
    private_dir(stage)
    for original, relative, pin in copies:
        target = stage / relative
        parent = stage
        for part in relative.parts[:-1]:
            parent /= part
            private_dir(parent)
        copy_pinned(original, target, pin)
    require(not success or time.monotonic() < seal['outerDeadlineMonotonic'], 'original outer deadline expired during metadata staging')
    write_new(evidence / 'transport-receipt.json', value)
    write_new(stage / 'transport-receipt.json', value)
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as stream:
            stream.write('candidate-permitted=' + str(success).lower() + '\n')
            stream.write('prerequisite-passed=' + str(prerequisite_passed).lower() + '\n')
    return value


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('mode', choices=['canonicalize-checkouts', 'seal-transport', 'qualify', 'network-prerequisite', 'receipt'])
    parser.add_argument('--operation-id', required=True)
    parser.add_argument('--source-revision')
    parser.add_argument('--security-stage')
    args = parser.parse_args(argv)
    require(args.security_stage is None or args.mode == 'receipt', 'security stage is receipt-only')
    if args.mode == 'canonicalize-checkouts':
        print(json.dumps(canonicalize_checkouts(args.source_revision or ''), sort_keys=True))
    elif args.mode == 'seal-transport':
        print(json.dumps(seal_crypto(args.operation_id, evidence=Path(os.environ['GITHUB_WORKSPACE']) / 'evidence'), sort_keys=True))
    elif args.mode == 'receipt':
        value = receipt(args.operation_id, evidence=Path(os.environ['GITHUB_WORKSPACE']) / 'evidence',
                        security_stage=args.security_stage)
        print(json.dumps({'candidateTransportPermitted': value['candidateTransportPermitted'],
                          'metadataBytes': value['metadataBytes'], 'candidate': value['candidate']}, sort_keys=True))
    else:
        selection, target, digest = prepare(args.operation_id, args.source_revision or '',
            purpose='network-prerequisite' if args.mode == 'network-prerequisite' else 'full-image')
        argv = controller_argv(selection, target, digest)
        os.execve(argv[0], argv, {'PATH': '/usr/bin:/bin', 'HOME': str(HOME), 'USER': 'runner', 'LOGNAME': 'runner',
                                'XDG_RUNTIME_DIR': '/tmp/runtime-1001', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8'})


if __name__ == '__main__':
    main()
