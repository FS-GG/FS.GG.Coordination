"""Closed product-image profile used only by portable-workspace-image.py.

Product revisions, private pins and admission references are externally selected
data. Neither this document nor its digest grants permission to launch engines.
There is deliberately no second command-line entrypoint or generic command port.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import re
import signal
import stat
import sys
import time
import shutil


PROFILE = 'sc2-headless'
WORK_SECONDS = 3600
RETIRE_SECONDS = 120
LOG_BYTES = 8 * 1024**2
METADATA_BYTES = 32 * 1024**2
RECORD_BYTES = 128 * 1024
MAX_EVENTS = 2048
MAX_COMMANDS = 512
MAX_PROCESSES = 512
SAMPLE_SECONDS = 15
MAX_SAMPLE_GAP_SECONDS = 60
RSS_BYTES = 12 * 1024**3
DISK_BYTES = 80 * 1024**3
ARCHIVE_BYTES = 16 * 1024**3
EXPANDED_BYTES = 32 * 1024**3
LAYER_BYTES = 16 * 1024**3
MAX_HEADERS = 200_000
MAX_LAYERS = 128
MAX_PATH_BYTES = 4096
MAX_JSON_BYTES = 1024**2
PATH_METADATA_BYTES = 64 * 1024**2
BUILDER_VERSION = '1.43.1'
PRODUCT_FILES = frozenset((
    'containers/Containerfile', 'containers/development-tool-inputs.json',
    'containers/sc2-headless-inputs.json', 'containers/ubuntu-support.lock.json',
    'containers/ubuntu-support.sources', 'scripts/prepare-container-inputs.py',
    'scripts/sc2-container-preflight.sh', 'docs/SC2C-01.6f-headless-container.md',
    '.config/dotnet-tools.json', 'global.json', 'scripts/verify.sh',
    '.github/workflows/verify.yml',
))
SUPPORT_LIBRARIES = {
    'elf-loader': '/usr/lib/ld-linux-x86-64.so.2', 'libc': '/usr/lib/libc.so.6',
    'libm': '/usr/lib/libm.so.6', 'libgpgme': '/usr/lib/libgpgme.so.45',
    'libassuan': '/usr/lib/libassuan.so.9', 'libsqlite3': '/usr/lib/libsqlite3.so.0',
    'libseccomp': '/usr/lib/libseccomp.so.2', 'libgpg-error': '/usr/lib/libgpg-error.so.0',
}
SUPPORT_EXECUTABLES = {name: '/usr/bin/' + name for name in ('podman', 'crun', 'conmon', 'git', 'du')}


def require(condition, message):
    if not condition:
        raise RuntimeError('sc2-headless: ' + message)


def fields(value, names):
    require(type(value) is dict and set(value) == set(names.split()), 'input fields mismatch')


def text(value, pattern=None):
    require(type(value) is str and 0 < len(value.encode()) <= MAX_PATH_BYTES
            and not any(ord(c) < 32 or ord(c) == 127 for c in value), 'bounded text required')
    require(pattern is None or re.fullmatch(pattern, value) is not None, 'text identity mismatch')
    return value


def integer(value, maximum=2**63 - 1):
    require(type(value) is int and 0 <= value <= maximum, 'bounded integer required')
    return value


def number(value):
    require(type(value) in (float, int) and math.isfinite(value) and value >= 0, 'finite clock required')
    return value


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False) + '\n').encode()


def strict_json(payload):
    def pairs(rows):
        result = {}
        for key, value in rows:
            require(key not in result, 'duplicate JSON field')
            result[key] = value
        return result
    require(len(payload) <= MAX_JSON_BYTES, 'JSON byte bound exceeded')
    try:
        return json.loads(payload, object_pairs_hook=pairs,
                          parse_constant=lambda value: require(False, 'nonfinite JSON number'))
    except (ValueError, RecursionError) as error:
        raise RuntimeError('sc2-headless: malformed JSON') from error


def path(value):
    value = Path(text(value))
    require(value.is_absolute() and '..' not in value.parts, 'absolute nontraversing path required')
    require(not any(p.is_symlink() for p in (value, *value.parents)), 'symlink path component refused')
    return value


def identity(value, *, directory=False, owned=True):
    value = path(str(value))
    info = value.lstat()
    require((info.st_uid == os.getuid() if owned else info.st_uid in (0, os.getuid()))
            and not stat.S_ISLNK(info.st_mode), 'unowned path refused')
    require(owned or not info.st_mode & 0o022, 'writable native support refused')
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode), 'path type refused')
    require(directory or info.st_nlink == 1 or (not owned and info.st_uid == 0), 'linked file refused')
    return {'device': info.st_dev, 'inode': info.st_ino, 'mode': stat.S_IMODE(info.st_mode),
            'uid': info.st_uid, 'bytes': info.st_size}


def regular_bytes(value, maximum, expected=None, *, owned=True):
    value = path(str(value))
    before = identity(value, owned=owned)
    before_stat = value.stat()
    require(before['bytes'] <= maximum, 'file byte bound exceeded')
    fd = os.open(value, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        require((info.st_dev, info.st_ino) == (before['device'], before['inode']), 'file object replaced')
        payload = stream.read(maximum + 1)
    after_stat = value.stat()
    require(len(payload) == before['bytes'] and identity(value, owned=owned) == before
            and (before_stat.st_mtime_ns, before_stat.st_ctime_ns, before_stat.st_nlink)
            == (after_stat.st_mtime_ns, after_stat.st_ctime_ns, after_stat.st_nlink), 'file changed while reading')
    require(expected is None or hashlib.sha256(payload).hexdigest() == expected, 'file digest mismatch')
    return payload, before


def file_token(value, *, owned=True):
    result = identity(value, owned=owned)
    info = value.stat()
    return {**result, 'modifiedNs': info.st_mtime_ns, 'changedNs': info.st_ctime_ns, 'links': info.st_nlink}


def pinned_token(value, maximum, expected, *, owned=True):
    before = file_token(value, owned=owned)
    regular_bytes(value, maximum, expected, owned=owned)
    require(file_token(value, owned=owned) == before, 'hashed input object changed before token binding')
    return before


def directory_token(value):
    result = identity(value, directory=True)
    result.pop('bytes')
    require(not result['mode'] & 0o022, 'writable source directory refused')
    info = value.stat()
    return {**result, 'modifiedNs': info.st_mtime_ns, 'changedNs': info.st_ctime_ns}


def context_directories(root, observe=None):
    """Bound the physical context directory population without following links.

    Directory timestamps fence new/removal/rename entries after clean Git/source
    validation. This is adjacent observation, not immutable filesystem isolation.
    """
    pending, result, entries = [root], {}, 0
    while pending:
        directory = pending.pop()
        result[str(directory)] = directory_token(directory)
        require(len(result) <= 8192, 'context directory population bound exceeded')
        with os.scandir(directory) as children:
            for child in children:
                if observe is not None:
                    observe()
                entries += 1
                require(entries <= 16384, 'context entry population bound exceeded')
                value = Path(child.path)
                info = child.stat(follow_symlinks=False)
                require(not stat.S_ISLNK(info.st_mode), 'context symlink refused')
                if value == root / '.git':
                    require(stat.S_ISDIR(info.st_mode), 'context external Git pointer refused')
                    continue
                if stat.S_ISDIR(info.st_mode):
                    pending.append(value)
                else:
                    require(stat.S_ISREG(info.st_mode), 'context special file refused')
    return result


def disjoint(paths):
    for index, (name, left) in enumerate(paths):
        for other, right in paths[index + 1:]:
            require(left != right and left not in right.parents and right not in left.parents,
                    'overlapping paths: ' + name + '/' + other)


def birth(pid, *, include_namespaces=True):
    root = Path('/proc') / str(pid)
    raw = (root / 'stat').read_text().rsplit(')', 1)[1].split()
    value = {'pid': pid, 'startTicks': int(raw[19]), 'parent': int(raw[1]), 'state': raw[0],
            'group': int(raw[2]), 'session': int(raw[3]),
            'cgroup': (root / 'cgroup').read_text().strip(),
            'rssBytes': max(0, int(raw[21])) * os.sysconf('SC_PAGE_SIZE')}
    if include_namespaces:
        value['namespaces'] = {name: (root / 'ns' / name).stat().st_ino for name in ('user', 'mnt', 'pid', 'net')}
    return value


def same_birth(left, right):
    # Namespace observations are separate: the selected rootless unshare path
    # deliberately changes them. They are never substituted for kernel birth.
    return all(left.get(key) == right.get(key) for key in ('pid', 'startTicks', 'group', 'session', 'cgroup'))


def host_resources(allocation, load_store):
    """Fixed local observations; root-owned domain disposition remains external."""
    memory = {}
    for row in Path('/proc/meminfo').read_text().splitlines():
        name, value = row.split(':', 1)
        if name in ('MemAvailable', 'MemTotal'):
            memory[name] = int(value.strip().split()[0]) * 1024
    cgroup = Path('/sys/fs/cgroup')
    def limit(name):
        value = (cgroup / name).read_text().strip()
        return None if value == 'max' else int(value)
    quota, period = (cgroup / 'cpu.max').read_text().split()
    return {'hostMemoryAvailableBytes': memory['MemAvailable'],
            'hostLogicalCpus': os.cpu_count(), 'affinity': sorted(os.sched_getaffinity(0)),
            'enclosingMemoryCurrentBytes': limit('memory.current'),
            'enclosingMemoryMaxBytes': limit('memory.max'),
            'enclosingCpuCapacity': None if quota == 'max' else int(quota) / int(period),
            'enclosingTasksCurrent': limit('pids.current'), 'enclosingTasksMax': limit('pids.max'),
            'diskFreeBytes': shutil.disk_usage(allocation).free,
            'loadStoreDiskFreeBytes': shutil.disk_usage(load_store).free,
            'cgroup': Path('/proc/self/cgroup').read_text().strip()}


def disk_backing(value):
    info = value.stat()
    matches = []
    for line in Path('/proc/self/mountinfo').read_text().splitlines():
        row = line.split()
        require(len(row) >= 10 and '-' in row, 'invalid mount observation')
        mount = Path(re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1], 8)), row[4]))
        if (mount == value or mount in value.parents) and row[2] == f'{os.major(info.st_dev)}:{os.minor(info.st_dev)}':
            matches.append((len(mount.parts), row[row.index('-') + 1]))
    require(matches, 'backing mount observation unavailable')
    filesystem = max(matches)[1]
    require(filesystem not in ('tmpfs', 'ramfs'), 'SC2 context/TMPDIR require disk-backed storage')
    return filesystem


def source_identity(api, root, revision, tree, command=None, *, complete=False, observe=None):
    require((root / '.git').is_dir() and not (root / '.git').is_symlink(),
            'source requires internal Git directory, not a worktree pointer')
    require(not (root / '.git/commondir').exists() and not (root / '.git/objects/info/alternates').exists(),
            'external Git object/reference storage refused')
    require(not (root / '.git/refs/replace').exists() and not (root / '.git/info/grafts').exists(),
            'replacement Git history refused')
    require(not (root / '.git/shallow').exists(), 'complete source history required')
    hooks = root / '.git/hooks'
    for name in ('objects', 'refs', 'info', 'hooks'):
        path(str(root / '.git' / name))
    if hooks.exists():
        count = 0
        with os.scandir(hooks) as entries:
            for item in entries:
                count += 1
                require(count <= 32 and item.name.endswith('.sample')
                        and stat.S_ISREG(item.stat(follow_symlinks=False).st_mode),
                        'active/nonregular Git hook refused')
    import configparser
    config = configparser.ConfigParser(interpolation=None)
    payload, _ = regular_bytes(root / '.git/config', MAX_JSON_BYTES)
    config.read_string(payload.decode())
    for section in config.sections():
        keys = set(config[section])
        allowed = ({'repositoryformatversion', 'filemode', 'bare', 'logallrefupdates', 'symlinks',
                    'ignorecase', 'precomposeunicode'} if section == 'core' else
                   {'url', 'fetch'} if section == 'remote "origin"' else
                   {'remote', 'merge'} if section.startswith('branch "') else set())
        require(keys <= allowed and allowed, 'Git configuration executable/external override refused')
        if section == 'remote "origin"' and 'url' in config[section]:
            remote = config[section]['url']
            require(re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?', remote)
                    is not None or re.fullmatch(r'git@github\.com:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?', remote)
                    is not None, 'credential-bearing or unsupported origin URL refused')
    def git(*args):
        argv = ['/usr/bin/git', '--no-optional-locks', '--no-replace-objects', '-c', 'core.fsmonitor=false',
                '-c', 'core.hooksPath=/dev/null', '-C', str(root), *args]
        if command is not None:
            return command('git-source', argv, 30).stdout.strip()
        return api['run'](argv, capture_limit_bytes=LOG_BYTES, timeout_seconds=30,
                          environment={'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
                                       'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'}).stdout.strip()
    require(git('rev-parse', 'HEAD') == revision and git('rev-parse', 'HEAD^{tree}') == tree,
            'source revision/tree mismatch')
    require(git('rev-parse', '--show-toplevel') == str(root), 'Git working tree redirected')
    require(git('status', '--porcelain', '--untracked-files=all', '--ignored') == '',
            'source is dirty or contains ignored output')
    tokens = {}
    if complete:
        total_bytes = 0
        # Git status can hide assume-unchanged/skip-worktree entries. Bind every
        # product leaf independently to the immutable native tree, without
        # executing filters or copying private source into the helper.
        listing = git('ls-tree', '-rz', 'HEAD')
        rows = listing.split('\0')
        require(rows[-1] == '' and len(rows) <= 8193, 'bounded complete product tree required')
        seen = set()
        for row in rows[:-1]:
            if observe is not None:
                observe()
            metadata, name = row.split('\t', 1)
            mode, kind, digest = metadata.split(' ')
            require(mode in ('100644', '100755') and kind == 'blob', 'product nonregular Git leaf refused')
            name = text(name)
            require(not Path(name).is_absolute() and '..' not in Path(name).parts and name not in seen,
                    'product tree path/duplicate refused')
            seen.add(name)
            token = file_token(root / name)
            payload, info = regular_bytes(root / name, 256 * 1024**2)
            total_bytes += len(payload)
            require(total_bytes <= 512 * 1024**2, 'tracked source population byte bound exceeded')
            actual = hashlib.sha1(b'blob ' + str(len(payload)).encode() + b'\0' + payload).hexdigest()
            require(actual == digest and bool(info['mode'] & 0o111) == (mode == '100755'),
                    'product working bytes/mode differ from immutable tree')
            require(file_token(root / name) == token, 'tracked source changed before token binding')
            tokens[str(root / name)] = token
    return tokens


def selected_input(args):
    require(args.operation_input and args.expected_operation_input_sha256, 'exact operation input required')
    expected = text(args.expected_operation_input_sha256, r'[0-9a-f]{64}')
    payload, object_id = regular_bytes(path(args.operation_input), MAX_JSON_BYTES, expected)
    require(object_id['mode'] == 0o600, 'private operation input mode0600 required')
    selection = strict_json(payload)
    fields(selection, 'operationId helper product allocation build load support clock admission resourceDomain licenseAccepted')
    fields(selection['resourceDomain'], 'relation topologySha256')
    require(selection['resourceDomain']['relation'] == 'shared-host-reservations-outside-enclosing-cgroup',
            'unsupported resource-domain relation')
    text(selection['operationId'], r'[a-z0-9][a-z0-9-]{0,63}')
    require(selection['licenseAccepted'] is True, 'explicit licensed private build selection required')
    for key in ('helper', 'product'):
        fields(selection[key], 'revision tree files' + (' baseImage localTag exportName' if key == 'product' else ''))
        text(selection[key]['revision'], r'[0-9a-f]{40}')
        text(selection[key]['tree'], r'[0-9a-f]{40}')
        require(type(selection[key]['files']) is dict, 'source pins required')
        for name, digest in selection[key]['files'].items():
            text(name); text(digest, r'[0-9a-f]{64}')
    product = selection['product']
    text(product['baseImage'], r'mcr\.microsoft\.com/dotnet/sdk@sha256:[0-9a-f]{64}')
    text(product['localTag'], r'localhost/[a-z0-9][a-z0-9._/-]{0,95}:[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}')
    require(all(part not in ('', '.', '..') for part in product['localTag'].split(':', 1)[0].split('/')),
            'noncanonical selected local tag')
    text(product['exportName'], r'[a-z][a-z0-9-]{0,63}')
    require(set(selection['product']['files']) == PRODUCT_FILES, 'complete closed product pin set required')
    require(set(selection['helper']['files']) == {'eng/portable-workspace-image.py', 'eng/portable_workspace_sc2.py'},
            'complete executing helper pin set required')
    fields(selection['clock'], 'bootId start workEnd totalEnd')
    text(selection['clock']['bootId'], r'[0-9a-f-]{36}')
    start, work, total = [number(selection['clock'][key]) for key in ('start', 'workEnd', 'totalEnd')]
    require(work == start + WORK_SECONDS and total == work + RETIRE_SECONDS, 'original lifetime mismatch')
    # These references bind the root-selected determining evidence. They cannot
    # assert authority or replace fresh adjacent native admission observations.
    fields(selection['admission'], 'topology resourceDisposition')
    require(selection['resourceDomain']['topologySha256'] == selection['admission']['topology']['sha256'],
            'resource-domain topology correspondence missing')
    for reference in selection['admission'].values():
        fields(reference, 'path sha256')
        regular_bytes(path(reference['path']), MAX_JSON_BYTES, text(reference['sha256'], r'[0-9a-f]{64}'))
    return selection, object_id


def directory_pin(value):
    result = identity(value, directory=True)
    result.pop('bytes')
    require(result['mode'] == 0o700, 'private directory mode0700 required')
    return result


def sync_directory(value, expected):
    descriptor = os.open(value, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        info = os.fstat(descriptor)
        require((info.st_dev, info.st_ino) == (expected['device'], expected['inode']),
                'directory replaced before durable entry publication')
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def namespace(root, graphroot, runroot):
    """Pure closed child environment; never inherit the caller's credentials."""
    import pwd
    paths = {key: root / key for key in ('home', 'config', 'data', 'xdg-runtime',
                                        'download-tmp', 'libpod-tmp', 'hooks', 'network', 'cdi')}
    config = paths['config'] / 'containers'
    user = pwd.getpwuid(os.getuid()).pw_name
    env = {'PATH': '/usr/bin:/bin', 'HOME': str(paths['home']), 'USER': user,
           'LOGNAME': user, 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
           'XDG_RUNTIME_DIR': str(paths['xdg-runtime']), 'XDG_CONFIG_HOME': str(paths['config']),
           'XDG_DATA_HOME': str(paths['data']), 'TMPDIR': str(paths['download-tmp']),
           'GOMAXPROCS': '4', 'GOMEMLIMIT': '1GiB'}
    env.update({'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'})
    config_names = {'CONTAINERS_CONF': 'containers.conf', 'CONTAINERS_STORAGE_CONF': 'storage.conf',
                    'CONTAINERS_REGISTRIES_CONF': 'registries.conf', 'REGISTRY_AUTH_FILE': 'auth.json',
                    'PODMAN_CONNECTIONS_CONF': 'podman-connections.json'}
    env.update({key: str(config / name) for key, name in config_names.items()})
    expected = {
        'auth.json': {'auths': {}},
        'podman-connections.json': {'Connection': {}, 'Farm': {'List': {}}},
        'storage.conf': {'storage': {'driver': 'vfs', 'graphroot': str(graphroot), 'runroot': str(runroot),
                                    'rootless_storage_path': str(graphroot),
                                    'options': {'additionalimagestores': []}}},
        'registries.conf': {'unqualified-search-registries': [], 'short-name-mode': 'enforcing',
                            'registry': [{'prefix': 'mcr.microsoft.com/dotnet/sdk',
                                          'location': 'mcr.microsoft.com/dotnet/sdk',
                                          'insecure': False, 'blocked': False}]},
        'containers.conf': {
            'containers': {'env': [], 'env_host': False, 'http_proxy': False, 'mounts': [],
                           'cgroups': 'disabled', 'log_driver': 'k8s-file', 'log_size_max': LOG_BYTES},
            'engine': {'remote': False, 'cgroup_manager': 'cgroupfs', 'conmon_path': ['/usr/bin/conmon'],
                       'conmon_env_vars': ['PATH=/usr/bin:/bin'], 'runtime': '/usr/bin/crun',
                       'lock_type': 'file', 'num_locks': 64, 'hooks_dir': [str(paths['hooks'])],
                       'cdi_spec_dirs': [str(paths['cdi'])], 'static_dir': str(graphroot / 'libpod'),
                       'tmp_dir': str(paths['libpod-tmp']), 'volume_path': str(graphroot / 'volumes'),
                       'image_copy_tmp_dir': str(paths['download-tmp']), 'events_logger': 'file', 'env': []},
            'network': {'network_config_dir': str(paths['network'])}},
    }
    return paths, env, expected


def check_namespace(root, graphroot, runroot):
    import tomllib
    paths, env, expected = namespace(root, graphroot, runroot)
    pins = {str(value): directory_pin(value) for value in paths.values()}
    for key in ('hooks', 'cdi'):
        require(not any(paths[key].iterdir()), 'hooks/CDI must be empty')
    config_pins = {}
    for name, content in expected.items():
        value = paths['config'] / 'containers' / name
        payload, object_id = regular_bytes(value, MAX_JSON_BYTES)
        require(object_id['mode'] == 0o600, 'configuration must be private mode0600')
        try:
            actual = strict_json(payload) if name.endswith('.json') else tomllib.loads(payload.decode())
        except (ValueError, UnicodeError) as error:
            raise RuntimeError('sc2-headless: invalid engine configuration') from error
        require(actual == content, 'unreviewed engine configuration key/value')
        config_pins[str(value)] = {'identity': object_id, 'sha256': hashlib.sha256(payload).hexdigest()}
    return paths, env, pins, config_pins


def check_inputs(args, api, *, command=None, skip_source_commands=False):
    selection, input_object = selected_input(args)
    captured_source = globals().get('_BOOTSTRAP_SOURCE_SHA256')
    require(captured_source is None or captured_source ==
            selection['helper']['files']['eng/portable_workspace_sc2.py'],
            'internal source captured at bootstrap differs from selected source')
    require(args.expected_source_revision == selection['helper']['revision']
            and args.expected_image_source_revision == selection['product']['revision'],
            'CLI/source selection mismatch')
    require(args.acquisition_dir is None and args.expected_acquired_inputs_sha256 is None,
            'other profile acquisition flags refused')
    helper, product = path(args.source), path(args.image_source)
    complete_tokens = {}
    declared_tokens = {}
    for key, root in (('helper', helper), ('product', product)):
        if not skip_source_commands:
            complete_tokens.update(source_identity(api, root, selection[key]['revision'], selection[key]['tree'], command,
                                                   complete=key == 'product',
                                                   observe=getattr(getattr(command, '__self__', None), 'verification_tick', None)))
        for name, digest in selection[key]['files'].items():
            declared_tokens[str(root / name)] = pinned_token(root / name, MAX_JSON_BYTES, digest)
    recipe = regular_bytes(product / 'containers/Containerfile', MAX_JSON_BYTES)[0].decode()
    from_lines = [line for line in recipe.splitlines() if line.startswith('FROM ')]
    require(from_lines == ['FROM --platform=linux/amd64 ' + selection['product']['baseImage'] + ' AS product',
                           'FROM product AS headless'], 'selected immutable base differs from bounded product recipe')
    executing = Path(__file__).resolve()
    for name, actual in [('eng/portable_workspace_sc2.py', executing),
                         ('eng/portable-workspace-image.py', executing.with_name('portable-workspace-image.py'))]:
        declared_tokens[str(actual)] = pinned_token(actual, MAX_JSON_BYTES, selection['helper']['files'][name])
    fields(selection['allocation'], 'path identity')
    allocation = path(selection['allocation']['path'])
    require(directory_pin(allocation) == selection['allocation']['identity'], 'allocation identity mismatch')
    require(path(args.engine_allocation) == allocation, 'allocation CLI mismatch')
    build, load = selection['build'], selection['load']
    for row in (build, load):
        fields(row, 'root runroot')
    build_root, build_run = path(build['root']), path(build['runroot'])
    load_root, load_run = path(load['root']), path(load['runroot'])
    require(build_root == allocation / 'build' / 'graphroot' and build_run == allocation / 'build' / 'runroot'
            and load_run == allocation / 'load' / 'runroot', 'closed store layout mismatch')
    require(load_root == Path.home() / '.local/share/fsgg/headless-images/sc2', 'designated load store mismatch')
    disjoint([('allocation', allocation), ('load store', load_root)])
    require(path(args.root) == build_root and path(args.runroot) == build_run, 'store CLI mismatch')
    state = path(args.state_dir)
    require(state == allocation / 'state' and product == allocation / 'context', 'closed context/state layout mismatch')
    require(path(args.engine_scratch_dir) == allocation / 'build' / 'download-tmp', 'disk scratch CLI mismatch')
    disjoint([('helper', helper), ('context', product), ('state', state), ('buildroot', build_root),
              ('buildrun', build_run), ('loadroot', load_root), ('loadrun', load_run)])
    pins = {str(value): directory_pin(value) for value in (product, state, build_root, build_run, load_root, load_run)}
    configs, envs = {}, {}
    for name, graph, runtime in (('build', build_root, build_run), ('load', load_root, load_run)):
        paths, env, directories, config_pins = check_namespace(allocation / name, graph, runtime)
        pins.update(directories); configs.update(config_pins); envs[name] = env
    support = selection['support']
    require(type(support) is dict and set(support) ==
            {'builder', 'podman', 'crun', 'conmon', 'git', 'du', 'python'} | set(SUPPORT_LIBRARIES),
            'complete closed native support pins required')
    support_tokens = {}
    for role, row in support.items():
        text(role, r'[a-z][a-z0-9-]{0,31}'); fields(row, 'path sha256')
        info = pinned_token(path(row['path']), 256 * 1024**2, text(row['sha256'], r'[0-9a-f]{64}'), owned=False)
        support_tokens[row['path']] = info
        require(role in SUPPORT_LIBRARIES or info['mode'] & 0o111, 'native support executable bit missing')
    for role, expected in SUPPORT_EXECUTABLES.items():
        require(support[role]['path'] == expected, 'native executable alias refused')
    for role, alias in SUPPORT_LIBRARIES.items():
        require(Path(alias).resolve(strict=True) == path(support[role]['path']), 'native library linkage alias mismatch')
    require(support['python']['path'] == str(Path(sys.executable).resolve()), 'executing Python identity mismatch')
    require(args.podman == support['podman']['path'], 'selected load engine mismatch')
    builder = [support['builder']['path'], '--storage-driver=vfs', '--root', str(build_root),
               '--runroot', str(build_run), '--cgroup-manager=cgroupfs', '--storage-opt=']
    current_paths, _, _ = namespace(allocation / 'load', load_root, load_run)
    loader = [support['podman']['path'], '--storage-driver=vfs', '--root', str(load_root),
              '--runroot', str(load_run), '--remote=false', '--runtime=/usr/bin/crun', '--conmon=/usr/bin/conmon',
              '--cgroup-manager=cgroupfs', '--tmpdir=' + str(current_paths['libpod-tmp']),
              '--hooks-dir=' + str(current_paths['hooks']), '--network-config-dir=' + str(current_paths['network']),
              '--events-backend=file', '--storage-opt=']
    return {'profile': PROFILE, 'selection': selection, 'inputIdentity': input_object,
            'inputSha256': args.expected_operation_input_sha256, 'helper': helper, 'product': product,
            'allocation': allocation, 'state': state, 'directoryPins': pins, 'configPins': configs,
            'supportPins': support_tokens,
            'sourcePins': {**complete_tokens, **declared_tokens, **{str(root / name): file_token(root / name)
                           for key, root in (('helper', helper), ('product', product))
                           for name in ('.git/config', '.git/HEAD', '.git/index')}},
            'contextDirectoryPins': context_directories(product,
                getattr(getattr(command, '__self__', None), 'verification_tick', None)),
            'operationInputPath': path(args.operation_input),
            'admissionPins': {reference['path']: file_token(path(reference['path']))
                              for reference in selection['admission'].values()},
            'environments': envs, 'builder': builder, 'loader': loader,
            'nativeExecutionEnabled': False}


class Lifetime:
    """One original operation owner for the existing command capture primitive.

    Observed direct-child and engine retirement remain separate from unknown
    descendants. This userspace owner does not provide worker-loss containment.
    """

    def __init__(self, args, api, checked):
        self.args, self.api, self.checked = args, api, checked
        self.selection = checked['selection']
        self.clock = self.selection['clock']
        self.commands = []
        self.events = self.metadata_bytes = self.log_bytes = 0
        self.first_cause = None
        self.sampling = False
        self.native_sampling = False
        self.engine_observation_started = False
        self.last_sample = None
        self.last_clock = None
        self.last_domain_check = None
        self.known_births = {}
        self.sampled_births = {}
        self.direct_rss_peak = 0
        self.retirement_gaps = []
        self.work_guard()
        self.directory = checked['state'] / self.selection['operationId']
        state_identity = directory_pin(checked['state'])
        # Exclusive creation is the consumed-original fence, not an enrollment
        # or grant. Never reopen a prior operation after interruption.
        self.directory.mkdir(mode=0o700)
        self.directory_identity = directory_pin(self.directory)
        sync_directory(checked['state'], state_identity)
        self.journal = self.directory / 'journal.jsonl'
        with self.journal.open('xb') as stream:
            os.fchmod(stream.fileno(), 0o600)
            initial = os.fstat(stream.fileno())
            self.journal_identity = (initial.st_dev, initial.st_ino)
            stream.flush()
            os.fsync(stream.fileno())
        sync_directory(self.directory, self.directory_identity)
        self.record({'event': 'run-start', 'profile': PROFILE,
                     'operationId': self.selection['operationId'],
                     'inputSha256': checked['inputSha256'],
                     'helperSourceRevision': self.selection['helper']['revision'],
                     'imageSourceRevision': self.selection['product']['revision'],
                     'clock': self.clock, 'scope': 'ordinary sampled image qualification; descendants unproved'})

    def now(self):
        return time.monotonic()

    def work_guard(self, retirement=False):
        now = self.now()
        require(self.last_clock is None or now >= self.last_clock, 'monotonic clock moved backwards')
        self.last_clock = now
        if self.last_domain_check is None or now - self.last_domain_check >= 1:
            require(Path('/proc/sys/kernel/random/boot_id').read_text().strip() == self.clock['bootId'],
                    'monotonic boot domain changed')
            self.last_domain_check = now
        require(self.clock['start'] <= now < self.clock['totalEnd' if retirement else 'workEnd'],
                'original retirement deadline exhausted' if retirement else 'original work deadline exhausted')
        if not retirement and self.first_cause is not None:
            raise self.first_cause
        return now

    def preserve(self, error):
        if self.first_cause is None:
            self.first_cause = error
        return self.first_cause

    def record(self, value, primary=None):
        try:
            payload = canonical(value)
            require(len(payload) <= RECORD_BYTES and self.events < MAX_EVENTS
                    and self.metadata_bytes + len(payload) <= METADATA_BYTES, 'journal bound exceeded')
            require(directory_pin(self.directory) == self.directory_identity, 'operation evidence directory replaced')
            self.api['append_journal'](self.journal, value, expected_identity=self.journal_identity)
            self.events += 1
            self.metadata_bytes += len(payload)
        except BaseException as error:
            if primary is None:
                self.preserve(error)
                raise
            primary.add_note('retained journal/reporting uncertainty: ' + str(error)[:512])

    def execute(self, label, argv, seconds, *, kind='build', retirement=False, check=True, capture_bytes=LOG_BYTES):
        if 'operationInputPath' in self.checked:
            self.guard_objects(retirement=retirement)
            if label in ('base-pull', 'image-build', 'image-export', 'verified-load', 'selected-tag'):
                require(not retirement, 'new image effect cannot use retirement allowance')
                self.resource_admission()
        return self.api['run'](argv, check=check, timeout_seconds=seconds,
                               capture_limit_bytes=capture_bytes, execution=self, label=label,
                               environment=self.checked['environments'][kind], retirement=retirement)

    def command_intent(self, argv, cwd, environment, label, retirement):
        self.work_guard(retirement)
        require(type(argv) is list and 0 < len(argv) <= 128 and all(type(x) is str for x in argv),
                'bounded argument list required')
        require(sum(len(text(x).encode()) for x in argv) <= RECORD_BYTES // 2, 'command byte bound exceeded')
        text(label, r'[a-z][a-z0-9-]{0,47}')
        require(environment in self.checked['environments'].values() and cwd is None, 'unselected command environment')
        require(len(self.commands) < MAX_COMMANDS, 'command census bound exceeded')
        slot = {'ordinal': len(self.commands), 'label': label, 'argv': argv.copy(),
                'retirement': retirement, 'state': 'intent', 'pid': None, 'birth': None,
                'process': None, 'streams': {}, 'bytes': {'stdout': 0, 'stderr': 0}}
        # Retain the owner slot before any partial file/Popen construction.
        self.commands.append(slot)
        self.record({'event': 'command-intent', 'ordinal': slot['ordinal'], 'name': label,
                     'argv': argv, 'environment': environment, 'retirement': retirement})
        try:
            for name in ('stdout', 'stderr'):
                slot['streams'][name] = (self.directory / f"{slot['ordinal']:04d}-{label}.{name}").open('xb')
                os.fchmod(slot['streams'][name].fileno(), 0o600)
        except BaseException as error:
            slot['state'] = 'not-launched'
            self.preserve(error)
            self.close_streams(slot, error)
            self.record({'event': 'command-construction-failed', 'ordinal': slot['ordinal'],
                         'nativeLaunchAttempted': False, 'reason': str(error)[:512]}, error)
            raise
        return slot

    def command_deadline(self, seconds, retirement):
        now = self.work_guard(retirement)
        return min(now + seconds, self.clock['totalEnd' if retirement else 'workEnd'])

    def close_streams(self, slot, primary=None):
        for stream in slot['streams'].values():
            try:
                if not stream.closed:
                    stream.flush(); os.fsync(stream.fileno()); stream.close()
            except BaseException as error:
                if primary is None:
                    raise
                primary.add_note('partial stream retention failed: ' + str(error)[:512])

    def command_launch_unknown(self, slot, error):
        slot['state'] = 'launch-unknown'
        self.preserve(error)
        self.retirement_gaps.append({'ordinal': slot['ordinal'], 'reason': 'Popen return/identity unavailable'})
        self.close_streams(slot, error)
        self.record({'event': 'command-launch-unknown', 'ordinal': slot['ordinal'],
                     'reason': str(error)[:512]}, error)

    def command_abandoned(self, slot, error):
        slot['state'] = 'not-launched'
        self.preserve(error)
        self.close_streams(slot, error)
        self.record({'event': 'command-not-launched', 'ordinal': slot['ordinal'],
                     'nativeLaunchAttempted': False, 'reason': str(error)[:512]}, error)

    def command_started(self, slot, process):
        slot['process'] = process
        slot['pid'] = process.pid
        actual = birth(process.pid, include_namespaces=False)
        slot['birth'] = actual
        try:
            observed = birth(process.pid)
            require(same_birth(actual, observed), 'command birth changed during namespace observation')
            actual = observed
            slot['birth'] = actual
        except FileNotFoundError:
            # Kernel teardown can remove ns/mnt before waitpid reports exit.
            # The already-read PID/start/group/session/cgroup remains a real
            # birth, not a PID-only guess. Preserve the missing namespace
            # observation and let the shared pipe/wait path observe settlement.
            # ns/mnt can disappear while the stat state is still R and before
            # waitpid is ready. It is not a usable admission/containment fact.
            # Keep the genuine core birth and the explicit observation gap;
            # pipe EOF plus wait remain mandatory before this command settles.
            slot['namespaceObservation'] = 'unavailable at observation; core kernel birth retained; no namespace claim'
        require(actual['group'] == process.pid and actual['session'] == process.pid,
                'owned command session identity mismatch')
        require(actual['cgroup'] == Path('/proc/self/cgroup').read_text().strip(),
                'command outside selected enclosing cgroup')
        self.known_births[(process.pid, actual['startTicks'])] = actual
        slot['state'] = 'running'
        self.record({'event': 'command-started', 'ordinal': slot['ordinal'], 'birth': actual,
                     'namespaceObservation': slot.get('namespaceObservation', 'observed')})

    def command_tick(self, slot, process, retirement):
        self.work_guard(retirement)
        if self.native_sampling and not self.sampling:
            self.sample_if_due(retirement=retirement)
        else:
            self.direct_rss_check()

    def command_output(self, slot, name, chunk):
        remaining = max(0, LOG_BYTES - self.log_bytes)
        slot['streams'][name].write(chunk[:remaining])
        slot['bytes'][name] += len(chunk)
        self.log_bytes += len(chunk)
        require(self.log_bytes <= LOG_BYTES, 'operation combined log bound exceeded')

    def command_settled(self, slot, process, status):
        self.work_guard(slot['retirement'])
        slot['state'] = 'reaped-and-eof'
        self.close_streams(slot)
        self.record({'event': 'command-result', 'ordinal': slot['ordinal'], 'exitCode': status,
                     'directChildReaped': True, 'bothStreamsEof': True, 'streamBytes': slot['bytes'],
                     'descendantSettlement': 'unproved'})

    def command_stop(self, slot, process, error):
        self.preserve(error)
        eof_observed = slot['state'] == 'reaped-and-eof'
        confirmed = False
        try:
            self.work_guard(True)
            if process.poll() is None:
                require(slot['birth'] is not None and same_birth(
                        {k: v for k, v in slot['birth'].items() if k != 'namespaces'},
                        birth(process.pid, include_namespaces=False)),
                        'stop birth identity unavailable or replaced')
                # Stop only the retained direct child. Group/escaped descendants
                # require separately observed identity and remain obligations.
                process.terminate()
                try:
                    process.wait(timeout=min(1, self.clock['totalEnd'] - self.now()))
                except TimeoutError:
                    raise
                except Exception as wait_error:
                    import subprocess
                    if not isinstance(wait_error, subprocess.TimeoutExpired):
                        raise
                    self.work_guard(True)
                    require(same_birth({k: v for k, v in slot['birth'].items() if k != 'namespaces'},
                                       birth(process.pid, include_namespaces=False)), 'kill birth identity replaced')
                    process.kill()
                    process.wait(timeout=min(1, self.clock['totalEnd'] - self.now()))
            else:
                process.wait(timeout=min(1, self.clock['totalEnd'] - self.now()))
            confirmed = True
        except BaseException as cleanup_error:
            error.add_note('direct child retirement unconfirmed: ' + str(cleanup_error)[:512])
        slot['state'] = 'failed'
        self.retirement_gaps.append({'ordinal': slot['ordinal'],
                                    'reason': 'failed command EOF/descendant or engine effects unconfirmed'})
        self.close_streams(slot, error)
        self.record({'event': 'command-failed', 'ordinal': slot['ordinal'], 'firstCause': str(error)[:512],
                     'directChildReaped': confirmed, 'bothStreamsEof': eof_observed,
                     'descendantSettlement': 'unproved', 'streamBytes': slot['bytes']}, error)

    def command_close_failed(self, slot, error):
        self.preserve(error)
        self.retirement_gaps.append({'ordinal': slot['ordinal'], 'reason': 'capture handle close failed'})
        self.record({'event': 'command-close-failed', 'ordinal': slot['ordinal'], 'reason': str(error)[:512]}, error)

    def retire_direct_children(self):
        for slot in self.commands:
            process = slot['process']
            if process is not None and process.poll() is None:
                cause = self.first_cause or RuntimeError('sc2-headless: retained direct child remains live at retirement')
                # Same original handle/birth and total deadline; never a new
                # command, group signal or synthetic replacement identity.
                self.command_stop(slot, process, cause)

    def resource_admission(self):
        self.work_guard()
        reference = self.selection['admission']['resourceDisposition']
        disposition = strict_json(regular_bytes(path(reference['path']), MAX_JSON_BYTES, reference['sha256'])[0])
        fields(disposition, 'sharedHostReservedMemoryBytes sharedHostReservedCpu sharedHostReservedDiskBytes unquantifiedResourceConflicts')
        for key in ('sharedHostReservedMemoryBytes', 'sharedHostReservedCpu', 'sharedHostReservedDiskBytes'):
            integer(disposition[key])
        require(disposition['unquantifiedResourceConflicts'] == [], 'unquantified relevant resource conflicts remain')
        observed = host_resources(self.checked['allocation'], path(self.selection['load']['root']))
        # Home envelopes share the physical host but are outside this operation's
        # enforcing inner cgroup. Do not debit them twice or infer settlement.
        require(observed['hostMemoryAvailableBytes'] >= RSS_BYTES + 2 * 1024**3 + disposition['sharedHostReservedMemoryBytes'],
                'shared host memory reservation/headroom floor not met')
        require(observed['hostLogicalCpus'] >= 4 + disposition['sharedHostReservedCpu'], 'shared host CPU reservation floor not met')
        maximum = observed['enclosingMemoryMaxBytes']
        require(maximum is None or maximum - observed['enclosingMemoryCurrentBytes'] >= RSS_BYTES + 2 * 1024**3,
                'independent enclosing memory headroom not met')
        require(len(observed['affinity']) >= 4 and
                (observed['enclosingCpuCapacity'] is None or observed['enclosingCpuCapacity'] >= 4),
                'independent enclosing CPU capacity not met')
        tasks = observed['enclosingTasksMax']
        require(tasks is None or tasks - observed['enclosingTasksCurrent'] >= MAX_PROCESSES,
                'enclosing task observation headroom not met')
        require(observed['diskFreeBytes'] >= DISK_BYTES + disposition['sharedHostReservedDiskBytes'],
                'whole population disk capacity not met')
        require(observed['loadStoreDiskFreeBytes'] >= DISK_BYTES + disposition['sharedHostReservedDiskBytes'],
                'designated load-store disk capacity not met')
        for value in (self.checked['product'], self.checked['allocation'] / 'build' / 'download-tmp'):
            disk_backing(value)
        self.record({'event': 'resource-admission', 'observed': observed,
                     'sharedHostReservation': disposition, 'hardPerOperationCgroups': False})
        return observed

    def process_sample(self):
        rows, vanished = {}, []
        roots = {(os.getpid(), birth(os.getpid())['startTicks'])}
        roots.update(self.known_births)
        entries = []
        with os.scandir('/proc') as scan:
            for item in scan:
                require(len(entries) < 8192, 'visible process directory scan bound exceeded')
                entries.append(Path(item.path))
        for item in entries:
            if not item.name.isdigit():
                continue
            try:
                value = birth(int(item.name), include_namespaces=False)
            except FileNotFoundError:
                vanished.append(int(item.name))
                continue
            except (OSError, ValueError, IndexError) as error:
                raise RuntimeError('sc2-headless: visible process coverage read failed') from error
            rows[value['pid']] = value
        selected = {pid: row for pid, row in rows.items() if (pid, row['startTicks']) in roots}
        sessions = set()
        for previous in self.known_births.values():
            leader = rows.get(previous['pid'])
            if leader is None or same_birth({k: v for k, v in previous.items() if k != 'namespaces'}, leader):
                sessions.add(previous['session'])
        for pid, row in rows.items():
            if row['session'] in sessions:
                selected[pid] = row
        changed = True
        while changed:
            changed = False
            for pid, row in rows.items():
                if row['parent'] in selected and pid not in selected:
                    selected[pid] = row
                    changed = True
        # A process outside the observed owned session/parent closure is not
        # attributed by a command-line substring. In particular, unrelated model
        # workers' command lines are neither payload nor cleanup authority here.
        # A daemon which escaped that closure remains unproved, never zero.
        require(len(selected) <= MAX_PROCESSES, 'sampled process population bound exceeded')
        for pid, previous in list(selected.items()):
            try:
                observed = birth(pid)
            except FileNotFoundError:
                vanished.append(pid)
                del selected[pid]
                continue
            require(same_birth(previous, observed), 'sampled process birth changed during namespace observation')
            known = self.known_births.get((pid, observed['startTicks']))
            require(known is None or same_birth(known, observed), 'owned process birth identity changed')
            selected[pid] = observed
        expected = Path('/proc/self/cgroup').read_text().strip()
        require(all(row['cgroup'] == expected for row in selected.values()), 'selected process moved outside enclosing cgroup')
        self.sampled_births = {(row['pid'], row['startTicks']): row for row in selected.values()}
        rss = sum(row['rssBytes'] for row in selected.values())
        require(rss <= RSS_BYTES, 'sampled scoped RSS ceiling exceeded')
        return {'selectedProcesses': list(selected.values()), 'selectedRssBytes': rss,
                'racedVisibleExits': vanished, 'escapedDescendantCoverage': 'unproved'}

    def direct_rss_check(self):
        total = 0
        candidates = {**self.sampled_births, **self.known_births}
        own = birth(os.getpid()); candidates[(own['pid'], own['startTicks'])] = own
        for previous in candidates.values():
            try:
                actual = birth(previous['pid'], include_namespaces=False)
            except FileNotFoundError:
                continue
            require(same_birth(previous, actual), 'sampled birth replaced during observation helper')
            total += actual['rssBytes']
        self.direct_rss_peak = max(self.direct_rss_peak, total)
        require(total <= RSS_BYTES, 'observed scoped RSS ceiling exceeded during helper')

    def sample_if_due(self, *, retirement=False, force=False):
        now = self.work_guard(retirement)
        if self.last_sample is not None:
            require(now - self.last_sample <= MAX_SAMPLE_GAP_SECONDS, 'resource observation gap exceeded')
            if not force and now - self.last_sample < SAMPLE_SECONDS:
                return
        self.sampling = True
        try:
            snapshot = self.process_sample()
            roots = [self.checked['allocation'], path(self.selection['load']['root'])]
            disjoint([('allocation', roots[0]), ('load store', roots[1])])
            census = self.execute('disk-sample', self.checked['builder'] +
                                  ['unshare', '/usr/bin/du', '--summarize', '--bytes', *map(str, roots)],
                                  10, retirement=retirement)
            sizes = {}
            for row in census.stdout.splitlines():
                amount, name = row.split('\t', 1)
                require(name in map(str, roots) and name not in sizes, 'disk sample path census mismatch')
                sizes[name] = integer(int(amount))
            require(set(sizes) == set(map(str, roots)), 'incomplete disk population sample')
            require(sum(sizes.values()) <= DISK_BYTES, 'sampled whole disk population ceiling exceeded')
            finished = self.work_guard(retirement)
            require(finished - now <= 10.5 and
                    (self.last_sample is None or finished - self.last_sample <= MAX_SAMPLE_GAP_SECONDS),
                    'resource observation helper/gap bound exceeded')
            self.last_sample = finished
            self.record({'event': 'sample', 'startedMonotonic': now, 'completedMonotonic': finished,
                         **snapshot, 'logicalRoots': sizes, 'logicalBytes': sum(sizes.values()),
                         'helperAndKnownProcessRssPeakBytes': self.direct_rss_peak,
                         'attribution': 'allocation plus designated load store; nested paths counted once'})
        except BaseException as error:
            self.preserve(error)
            raise
        finally:
            self.sampling = False

    def verification_tick(self):
        self.work_guard()
        if self.native_sampling:
            self.sample_if_due()
        else:
            self.direct_rss_check()

    def guard_objects(self, *, retirement=False):
        """Adjacent correspondence, never an authorization from selected data."""
        self.work_guard(retirement)
        payload, actual = regular_bytes(self.checked['operationInputPath'], MAX_JSON_BYTES,
                                        self.checked['inputSha256'])
        require(actual == self.checked['inputIdentity'], 'operation input object replaced')
        require(strict_json(payload) == self.selection, 'operation selection changed')
        for role, alias in SUPPORT_LIBRARIES.items():
            require(Path(alias).resolve(strict=True) == path(self.selection['support'][role]['path']),
                    'native library linkage alias changed')
        for name, expected in self.checked['directoryPins'].items():
            require(directory_pin(path(name)) == expected, 'owned directory identity changed')
        for name, expected in self.checked['contextDirectoryPins'].items():
            require(directory_token(path(name)) == expected, 'context directory entries changed')
        for name, expected in self.checked['admissionPins'].items():
            require(file_token(path(name)) == expected, 'determining admission reference object changed')
        for name, expected in self.checked['configPins'].items():
            _, actual = regular_bytes(path(name), MAX_JSON_BYTES, expected['sha256'])
            require(actual == expected['identity'], 'engine configuration object changed')
        for key, owned in (('supportPins', False), ('sourcePins', True)):
            for index, (name, expected) in enumerate(self.checked[key].items()):
                require(file_token(path(name), owned=owned) == expected, 'source/support file object changed')
                if index % 128 == 0:
                    self.work_guard(retirement)

    def native_json(self, label, argv, *, kind='build', retirement=False):
        result = self.execute(label, argv, 30, kind=kind, retirement=retirement, capture_bytes=MAX_JSON_BYTES)
        payload = result.stdout.encode()
        require(len(payload) <= MAX_JSON_BYTES, 'native JSON response bound exceeded')
        return strict_json(payload)

    def retain_manifest(self, value):
        self.work_guard(True)
        payload = canonical(value)
        require(len(payload) <= RECORD_BYTES and self.metadata_bytes + len(payload) <= METADATA_BYTES,
                'result manifest metadata bound exceeded')
        require(directory_pin(self.directory) == self.directory_identity, 'result directory object changed')
        require(not (self.directory / 'manifests').exists() and not (self.directory / 'manifests').is_symlink(),
                'fresh result manifest directory required')
        # Reserve finite retention before partial construction; use the existing
        # canonical/durable manifest writer rather than a second result format.
        self.metadata_bytes += len(payload)
        target = self.api['persist_manifest'](self.directory, value)
        regular_bytes(target, RECORD_BYTES, hashlib.sha256(payload).hexdigest())
        self.work_guard(True)
        self.record({'event': 'result-manifest', 'path': str(target),
                     'sha256': hashlib.sha256(payload).hexdigest()})
        return target

    def census(self, *, retirement=False):
        builder, loader = self.checked['builder'], self.checked['loader']
        build = self.native_json('build-containers', builder + ['containers', '--json'], retirement=retirement)
        load = self.native_json('load-containers', loader + ['ps', '--all', '--external', '--no-trunc', '--format=json'],
                                kind='load', retirement=retirement)
        images = self.native_json('load-images', loader + ['images', '--all', '--no-trunc', '--format=json'],
                                  kind='load', retirement=retirement)
        require(build is None or type(build) is list, 'build container census malformed')
        require(type(load) is list and type(images) is list and len(images) <= 128,
                'load container/image census malformed or excessive')
        require((build is None or len(build) <= 128) and len(load) <= 128,
                'container census population bound exceeded')
        self.record({'event': 'native-store-observation', 'buildContainers': build or [],
                     'loadContainers': load, 'loadImages': images, 'retirement': retirement})
        require(not build and not load, 'selected store has live/retained containers; ownership unresolved')
        seen = set()
        for image in images:
            require(type(image) is dict, 'image census member malformed')
            digest = text(image.get('Id'), r'(sha256:)?[0-9a-f]{64}')
            require(digest not in seen, 'duplicate native image identity')
            seen.add(digest)
        self.record({'event': 'store-census', 'buildContainers': [], 'loadContainers': [],
                     'loadImages': images, 'retirement': retirement})
        return images

    def preflight(self):
        self.guard_objects()
        self.resource_admission()
        os.sched_setaffinity(0, set(sorted(os.sched_getaffinity(0))[:4]))
        require(len(os.sched_getaffinity(0)) == 4, 'four-CPU affinity not established')
        self.engine_observation_started = True
        version = self.execute('builder-version', self.checked['builder'] + ['--version'], 30).stdout.strip()
        require(re.fullmatch(r'buildah version ' + re.escape(BUILDER_VERSION) + r'(?: \([^\r\n]{1,512}\))?', version)
                is not None, 'selected builder version family unsupported')
        for kind, prefix in (('build', self.checked['builder']), ('load', self.checked['loader'])):
            info = self.native_json(kind + '-info', prefix + ['info', '--format=json'], kind=kind)
            store, host = info.get('store', {}), info.get('host', {})
            if kind == 'build':
                root, runtime, driver = store.get('GraphRoot'), store.get('RunRoot'), store.get('GraphDriverName')
                rootless = host.get('rootless')
            else:
                root, runtime, driver = store.get('graphRoot'), store.get('runRoot'), store.get('graphDriverName')
                rootless = host.get('security', {}).get('rootless')
            expected = self.selection[kind]
            require((root, runtime, driver) == (expected['root'], expected['runroot'], 'vfs'),
                    'effective native storage differs from selection')
            require(rootless is True and host.get('os') == 'linux' and host.get('arch') == 'amd64',
                    'effective rootless/platform native support missing')
        script = ('import json,os,pathlib; print(json.dumps({"euid":os.geteuid(),'
                  '"uidMap":pathlib.Path("/proc/self/uid_map").read_text(),'
                  '"gidMap":pathlib.Path("/proc/self/gid_map").read_text(),'
                  '"userNamespace":pathlib.Path("/proc/self/ns/user").stat().st_ino,'
                  '"mountNamespace":pathlib.Path("/proc/self/ns/mnt").stat().st_ino}))')
        namespace_state = self.native_json('namespace-support', self.checked['builder'] +
                                           ['unshare', self.selection['support']['python']['path'], '-I', '-c', script])
        fields(namespace_state, 'euid uidMap gidMap userNamespace mountNamespace')
        require(namespace_state['euid'] == 0, 'builder unshare root identity unavailable')
        for key in ('uidMap', 'gidMap'):
            rows = namespace_state[key].splitlines()
            require(0 < len(rows) <= 16, 'bounded namespace mapping required')
            mappings = [tuple(map(int, row.split())) for row in rows]
            require(all(len(row) == 3 and all(n >= 0 for n in row) for row in mappings)
                    and any(start <= 1654 < start + count for start, _, count in mappings),
                    'namespace map does not cover selected image user/group')
        for key in ('userNamespace', 'mountNamespace'):
            require(integer(namespace_state[key]) > 0, 'native namespace object identity missing')
        self.record({'event': 'namespace-support', **namespace_state})
        self.census()
        self.native_sampling = True
        self.sample_if_due(force=True)
        self.record({'event': 'preflight-passed', 'supportScope': 'current namespaces and separate selected stores',
                     'hardPerOperationContainment': False, 'workerLossTeardown': 'unproved'})


def verify_image_config(value):
    require(type(value) is dict and value.get('Os') == 'linux' and value.get('Architecture') == 'amd64',
            'image platform mismatch')
    config = value.get('Config', {})
    require(config.get('User') == '1654:1654' and config.get('WorkingDir') == '/workspace'
            and config.get('Entrypoint') == ['bash', 'scripts/sc2-container-preflight.sh']
            and config.get('Cmd') == ['preflight'], 'image execution configuration mismatch')
    require(not config.get('Volumes') and not config.get('OnBuild'), 'unexpected image effects')


def oci_policy(checked):
    # Strict finite product policy; does not modify either earlier profile.
    return {'archiveBytes': ARCHIVE_BYTES, 'expandedBytes': EXPANDED_BYTES,
            'layerBytes': LAYER_BYTES, 'headers': MAX_HEADERS, 'hashFiles': True,
            'strictTypes': True, 'imageName': checked['selection']['product']['exportName'], 'pathMetadataBytes': PATH_METADATA_BYTES,
            'jsonBytes': MAX_JSON_BYTES,
            'clrFifoMetadata': True,
            'verifyConfig': verify_image_config,
            'files': {'workspace/' + name: digest for name, digest in checked['selection']['product']['files'].items()}}


def proposed_commands(checked, directory):
    builder = checked['builder']
    product = checked['selection']['product']
    iid, archive = directory / 'built-image.id', directory / 'candidate.oci.tar'
    return {'pull': builder + ['pull', '--authfile=' + checked['environments']['build']['REGISTRY_AUTH_FILE'],
                               '--platform=linux/amd64', '--retry=0', product['baseImage']],
            'build': builder + ['build', '--authfile=' + checked['environments']['build']['REGISTRY_AUTH_FILE'],
                                '--platform=linux/amd64', '--pull=never', '--retry=0',
                                '--layers=false', '--jobs=1', '--rm=true', '--force-rm=true',
                                '--network=private', '--pid=private', '--ipc=private', '--runtime=/usr/bin/crun',
                                '--security-opt=no-new-privileges', '--http-proxy=false', '--target=headless',
                                '--build-arg=SC2_LICENSE_ACCEPTED=true', '--tag=' + product['localTag'],
                                '--iidfile=' + str(iid), '--file=' + str(checked['product'] / 'containers/Containerfile'),
                                str(checked['product'])],
            'iidfile': str(iid), 'archive': str(archive)}


def stream_identity(value, observe):
    before = file_token(value)
    require(before['bytes'] <= ARCHIVE_BYTES, 'archive byte bound exceeded')
    digest = hashlib.sha256()
    with value.open('rb') as stream:
        require((os.fstat(stream.fileno()).st_dev, os.fstat(stream.fileno()).st_ino) ==
                (before['device'], before['inode']), 'archive opened object changed')
        while True:
            observe()
            chunk = stream.read(1024**2)
            if not chunk:
                break
            digest.update(chunk)
    require(file_token(value) == before, 'archive object changed during hashing')
    return {'object': before, 'sha256': digest.hexdigest()}


def qualify(owner):
    checked = owner.checked
    commands = proposed_commands(checked, owner.directory)
    require(not Path(commands['iidfile']).exists() and not Path(commands['iidfile']).is_symlink()
            and not Path(commands['archive']).exists() and not Path(commands['archive']).is_symlink(),
            'fresh output paths required')
    # Refresh native source identity with the SAME command/lifetime/capture
    # owner immediately before the first acquisition/build effects.
    for key, root in (('helper', checked['helper']), ('product', checked['product'])):
        source_identity(owner.api, root, owner.selection[key]['revision'], owner.selection[key]['tree'],
                        owner.execute, complete=key == 'product', observe=owner.verification_tick)
    owner.resource_admission()
    owner.census()
    owner.execute('base-pull', commands['pull'], 600)
    owner.execute('image-build', commands['build'], WORK_SECONDS)
    iid = Path(commands['iidfile'])
    payload, _ = regular_bytes(iid, 80)
    image_id = text(payload.decode().strip(), r'sha256:[0-9a-f]{64}')
    owner.record({'event': 'built-image', 'imageId': image_id, 'iidfile': str(iid),
                  'iidfileSha256': hashlib.sha256(payload).hexdigest()})
    archive = Path(commands['archive'])
    # Export reference is an actual validated image ID, never a mutable tag or
    # shell substitution. A partial archive cannot become a load input.
    require(not archive.exists() and not archive.is_symlink(), 'fresh archive output required at export boundary')
    owner.execute('image-export', checked['builder'] +
                  ['push', '--retry=0', image_id, 'oci-archive:' + str(archive) + ':' + owner.selection['product']['exportName']], 600)
    exported_object = identity(archive)
    descriptor = os.open(archive, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        observed = os.fstat(descriptor)
        require((observed.st_dev, observed.st_ino) == (exported_object['device'], exported_object['inode']),
                'exported archive replaced before mode custody')
        os.fchmod(descriptor, 0o600)
    finally:
        os.close(descriptor)
    exported = stream_identity(archive, owner.verification_tick)
    verified = owner.api['inspect_oci_archive'](archive, profile=PROFILE, built_image_id=image_id,
                                                product_policy=oci_policy(checked), observe=owner.verification_tick)
    require(stream_identity(archive, owner.verification_tick) == exported,
            'verified archive changed before load')
    owner.record({'event': 'archive-verified', 'archive': str(archive), **exported,
                  'imageId': image_id, 'oci': verified})
    # Complete current load census permits earlier accepted images but no
    # foreign live containers. No removal/prune or global empty-store demand.
    owner.census()
    require(stream_identity(archive, owner.verification_tick) == exported,
            'archive changed at load boundary')
    owner.execute('verified-load', checked['loader'] + ['load', '--input=' + str(archive)], 600, kind='load')
    loaded = owner.native_json('loaded-inspect', checked['loader'] + ['image', 'inspect', image_id], kind='load')
    require(type(loaded) is list and len(loaded) == 1, 'loaded image census mismatch')
    actual = loaded[0]
    verify_image_config(actual)
    require(actual.get('Id', '').removeprefix('sha256:') == image_id[7:]
            and actual.get('RootFS', {}).get('Layers') == verified['rootfsDiffIds']
            and actual.get('Config') == verified['config'], 'loaded config/rootfs identity differs from verified archive')
    local_tag = owner.selection['product']['localTag']
    owner.execute('selected-tag', checked['loader'] + ['tag', image_id, local_tag], 30, kind='load')
    tagged = owner.native_json('tagged-inspect', checked['loader'] + ['image', 'inspect', local_tag], kind='load')
    require(type(tagged) is list and len(tagged) == 1 and
            tagged[0].get('Id', '').removeprefix('sha256:') == image_id[7:], 'selected local tag readback differs')
    owner.census()
    owner.sample_if_due(force=True)
    return {'imageId': image_id, 'localTag': local_tag, 'archive': str(archive),
            'archiveSha256': exported['sha256'], 'archiveObject': exported['object'],
            'imageAssertions': 'verified OCI and exact separate-store loaded config/rootfs/source correspondence',
            'productRuntimeAcceptance': False, 'hardContainment': False,
            'descendantSettlement': 'unproved', 'workerLossTeardown': 'unproved'}


def main(args, api):
    require(args.image_source and args.expected_image_source_revision, 'product source/revision required')
    if args.mode == 'check-inputs':
        checked = check_inputs(args, api)
        directory = checked['state'] / checked['selection']['operationId']
        return {'profile': PROFILE, 'inputSha256': checked['inputSha256'],
                'operationInputIdentity': checked['inputIdentity'],
                'commands': proposed_commands(checked, directory),
                'nativeExecutionEnabled': False, 'authority': 'external selection required; correspondence only'}
    # Pure file/config/object checks precede the consumed-original fence. All
    # subsequent subprocesses, including Git, share its original capture owner.
    checked = check_inputs(args, api, skip_source_commands=True)
    owner = Lifetime(args, api, checked)
    result = None
    manifest = None
    try:
        checked_again = check_inputs(args, api, command=owner.execute)
        require(checked_again['inputIdentity'] == checked['inputIdentity'], 'input changed at native boundary')
        owner.checked = checked_again
        owner.preflight()
        result = {'profile': PROFILE, 'preflight': 'passed'} if args.mode == 'preflight' else qualify(owner)
    except BaseException as error:
        owner.preserve(error)
    finally:
        # Observation and first-cause retention only: no guessed container or
        # process name grants deletion/signalling authority after a failed call.
        try:
            owner.work_guard(True)
            owner.retire_direct_children()
            if owner.engine_observation_started:
                owner.census(retirement=True)
            owner.record({'event': 'retirement-observed', 'directCommands':
                          [{'ordinal': slot['ordinal'], 'state': slot['state'], 'birth': slot['birth']}
                           for slot in owner.commands],
                          'descendantSettlement': 'unproved', 'workerLossTeardown': 'unproved',
                          'retirementGaps': owner.retirement_gaps})
        except BaseException as error:
            owner.preserve(error)
            owner.retirement_gaps.append({'reason': 'final native census/retention unconfirmed'})
        try:
            manifest = owner.retain_manifest({'profile': PROFILE, 'operationId': owner.selection['operationId'],
                'inputSha256': checked['inputSha256'], 'helper': owner.selection['helper'],
                'product': owner.selection['product'], 'clock': owner.clock, 'result': result,
                'firstCause': str(owner.first_cause)[:512] if owner.first_cause else None,
                'retirementGaps': owner.retirement_gaps, 'descendantSettlement': 'unproved',
                'directCommands': [{'ordinal': slot['ordinal'], 'state': slot['state'],
                                    'pid': slot['pid'], 'startTicks': (slot['birth'] or {}).get('startTicks'),
                                    'namespaceObservation': slot.get('namespaceObservation',
                                        'observed' if (slot['birth'] or {}).get('namespaces') else 'not observed')}
                                   for slot in owner.commands]})
        except BaseException as error:
            owner.preserve(error)
        owner.record({'event': 'run-complete', 'outcome': 'failed' if owner.first_cause else 'passed-scoped',
                      'firstCause': str(owner.first_cause)[:512] if owner.first_cause else None,
                      'retirementGaps': owner.retirement_gaps,
                      'descendantSettlement': 'unproved'}, owner.first_cause)
    if owner.first_cause is not None:
        raise owner.first_cause
    return {**result, 'operationId': owner.selection['operationId'], 'journal': str(owner.journal),
            'manifest': str(manifest),
            'combinedLogBytes': owner.log_bytes, 'metadataBytes': owner.metadata_bytes,
            'commandNamespaceObservationGaps': [slot['ordinal'] for slot in owner.commands
                                               if 'namespaceObservation' in slot],
            'scope': 'ordinary sampled profile; native/product and whole descendant acceptance separate'}
