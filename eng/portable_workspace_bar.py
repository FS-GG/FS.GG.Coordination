"""Closed BAR full-image adapter for the existing portable-workspace controller.

Reuses the unchanged source/capture/lifetime implementation with a separate fixed
product contract. No engine runtime or arbitrary command interface is provided.
"""
from __future__ import annotations
import hashlib
import importlib.util
import os
import re
from pathlib import Path, PurePosixPath
import stat
import sys
import json

# Capture the existing internal module without a bytecode cache. Its exact bytes
# are independently bound in the three-leaf helper closure before any native IO.
_shared_path = Path(__file__).with_name('portable_workspace_sc2.py')
_descriptor = os.open(_shared_path, os.O_RDONLY | os.O_NOFOLLOW)
with os.fdopen(_descriptor, 'rb') as _stream:
    _info = os.fstat(_stream.fileno())
    if not stat.S_ISREG(_info.st_mode) or _info.st_size > 1048576:
        raise RuntimeError('BAR shared controller source is not bounded regular data')
    _raw = _stream.read(1048577)
    if len(_raw) != _info.st_size:
        raise RuntimeError('BAR shared controller source changed during capture')
_shared_spec = importlib.util.spec_from_file_location('bar_shared_lifetime', _shared_path)
shared = importlib.util.module_from_spec(_shared_spec)
exec(compile(_raw, str(_shared_path), 'exec'), shared.__dict__)
SHARED_SOURCE_SHA256 = hashlib.sha256(_raw).hexdigest()
del _raw

PROFILE = 'bar-headless'
PRODUCT_FILES = frozenset('containers/barc-stock-headless/' + name for name in
    ('Containerfile', 'resources.json', 'build-image.py', 'entrypoint.sh', 'test_source.py', 'README.md'))
HELPER_FILES = frozenset(('eng/portable-workspace-image.py', 'eng/portable_workspace_bar.py',
                          'eng/portable_workspace_sc2.py'))
BASE_IMAGE = 'docker.io/library/ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b'
WORK_SECONDS, RETIRE_SECONDS = shared.WORK_SECONDS, shared.RETIRE_SECONDS
PREREQUISITE_WORK_SECONDS = 300

def work_seconds(purpose):
    require(purpose in ('full-image','network-prerequisite'),'closed purpose required')
    return WORK_SECONDS if purpose=='full-image' else PREREQUISITE_WORK_SECONDS

MAX_JSON_BYTES, LOG_BYTES = shared.MAX_JSON_BYTES, shared.LOG_BYTES
SUPPORT_LIBRARIES, SUPPORT_EXECUTABLES = shared.SUPPORT_LIBRARIES, shared.SUPPORT_EXECUTABLES
fields, text, number = shared.fields, shared.text, shared.number
path, regular_bytes, strict_json = shared.path, shared.regular_bytes, shared.strict_json
pinned_token, file_token, identity = shared.pinned_token, shared.file_token, shared.identity
directory_pin, disjoint = shared.directory_pin, shared.disjoint

# Fixed aliases are checked separately; ordinary support/source paths still
# forbid links. Keep the original helper pathname (and its host LSM policy).
NETWORK_BINARIES = ('/usr/bin/passt', '/usr/bin/passt.avx2')
NETWORK_ALIASES = ('/usr/bin/pasta', '/usr/bin/pasta.avx2')
NETWORK_LIBRARY_ROOT = '/usr/lib'
NETWORK_OWNER = 0
NETWORK_VERSION = 'pasta 2026_10_02.cba3570'


def network_seed():
    files, aliases = {}, {}
    for name in NETWORK_BINARIES:
        value = path(name)
        payload, _ = regular_bytes(value, 16 * 1024**2, owned=False)
        require(value.stat().st_uid == NETWORK_OWNER and value.stat().st_mode & 0o111,
                'untrusted network helper')
        files[name] = {'sha256': hashlib.sha256(payload).hexdigest(), 'identity': file_token(value, owned=False)}
    for alias, target in zip(NETWORK_ALIASES, NETWORK_BINARIES):
        value = Path(alias); info = value.lstat()
        require(info.st_uid == NETWORK_OWNER and (value.resolve(strict=True) == Path(target) or
                (stat.S_ISREG(info.st_mode) and (info.st_dev, info.st_ino) ==
                 (Path(target).stat().st_dev, Path(target).stat().st_ino))),
                'network helper alias differs')
        require(stat.S_ISLNK(info.st_mode) or (info.st_dev, info.st_ino) ==
                (Path(target).stat().st_dev, Path(target).stat().st_ino), 'network alias is not the fixed target')
        aliases[alias] = {'target': target, 'link': os.readlink(value) if value.is_symlink() else None,
                          'device': info.st_dev, 'inode': info.st_ino, 'changedNs': info.st_ctime_ns}
    return {'files': files, 'aliases': aliases}


def network_probe_recipe():
    # A fixed shell/coreutils-only RUN, no BAR assets, packages or compiler.
    return ('FROM --platform=linux/amd64 ' + BASE_IMAGE + '\nUSER 0:0\n'
            'RUN set -eu; test "$(id -u)" = 0; '
            'grep -Eq "^NoNewPrivs:[[:space:]]+1$" /proc/self/status; '
            'test "$(stat -Lc %i /proc/$$/ns/pid)" = "$(stat -Lc %i /proc/self/ns/pid)"; '
            'printf \'FSGG_NET_PROBE {"uid":0,"noNewPrivs":1,"procPidCorrespondence":1,"pidNamespace":%s,"networkNamespace":%s}\\n\' '
            '"$(stat -Lc %i /proc/self/ns/pid)" "$(stat -Lc %i /proc/self/ns/net)"\n').encode()


def bounded_observation(name):
    try:
        with open(name, 'rb') as stream:
            value = stream.read(65537)
        if len(value) > 65536:
            return {'status': 'Unknown', 'reason': 'observation exceeds bound'}
        return {'status': 'observed', 'value': value.decode(errors='replace')}
    except OSError as error:
        return {'status': 'Unknown', 'reason': str(error)[:256]}
source_identity, context_directories = shared.source_identity, shared.context_directories
stream_identity = shared.stream_identity
integer = shared.integer
SHARED_HOST_RELATION = 'shared-host-reservations-outside-enclosing-cgroup'
# FS-GG/.github#4778 route (b): one fresh GitHub-hosted VM per operation. Its
# root-produced domain evidence is correspondence only, never authority.
HOSTED_RELATION = 'dedicated-ephemeral-hosted-vm'
DEDICATED_RELATION = shared.DEDICATED_RELATION
DOMAIN_RELATIONS = (HOSTED_RELATION, DEDICATED_RELATION)
HOSTED_DOMAIN_SCHEMA = 'fsgg.bar-hosted-domain/1'
HOSTED_RUNNER_IMAGE = 'ubuntu24'
# An explicitly selected hosted image version; image drift refuses until selected.
HOSTED_RUNNER_IMAGE_VERSION = '20261012.334.1'
HOSTED_STOPPED_SERVICES = ('containerd.service', 'docker.service', 'docker.socket', 'fstrim.timer')

def require(condition, message):
    if not condition:
        raise RuntimeError('bar-headless: ' + message)


def selected_input(args):
    require(args.operation_input and args.expected_operation_input_sha256, 'exact operation input required')
    expected = text(args.expected_operation_input_sha256, r'[0-9a-f]{64}')
    payload, object_id = regular_bytes(path(args.operation_input), MAX_JSON_BYTES, expected)
    require(object_id['mode'] == 0o600, 'private operation input mode0600 required')
    selection = strict_json(payload)
    fields(selection, 'schema operationId helper product allocation build load support clock admission resourceDomain licenseAccepted network')
    fields(selection['network'], 'purpose seed')
    require(selection['network']['purpose'] in ('full-image', 'network-prerequisite'), 'fixed network purpose required')
    require(selection['schema'] == 'fsgg.bar-headless-operation/1', 'BAR operation schema required')
    require(type(selection['resourceDomain']) is dict, 'resource-domain relation required')
    hosted = selection['resourceDomain'].get('relation') in DOMAIN_RELATIONS
    fields(selection['resourceDomain'], 'relation topologySha256' + (' domainEvidenceSha256' if hosted else ''))
    require(selection['resourceDomain']['relation'] in (SHARED_HOST_RELATION,) + DOMAIN_RELATIONS,
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
    require(product['baseImage'] == BASE_IMAGE, 'exact BAR Ubuntu base required')
    text(product['localTag'], r'localhost/[a-z0-9][a-z0-9._/-]{0,95}:[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}')
    require(all(part not in ('', '.', '..') for part in product['localTag'].split(':', 1)[0].split('/')),
            'noncanonical selected local tag')
    text(product['exportName'], r'[a-z][a-z0-9-]{0,63}')
    require(set(selection['product']['files']) == PRODUCT_FILES, 'complete closed product pin set required')
    require(set(selection['helper']['files']) == HELPER_FILES,
            'complete executing helper pin set required')
    fields(selection['clock'], 'bootId start workEnd totalEnd')
    text(selection['clock']['bootId'], r'[0-9a-f-]{36}')
    start, work, total = [number(selection['clock'][key]) for key in ('start', 'workEnd', 'totalEnd')]
    require(work == start + work_seconds(selection['network']['purpose']) and total == work + RETIRE_SECONDS, 'original purpose lifetime mismatch')
    # These references bind the root-selected determining evidence. They cannot
    # assert authority or replace fresh adjacent native admission observations.
    fields(selection['admission'], 'topology resourceDisposition security' + (' domain' if hosted else ''))
    require(selection['resourceDomain']['topologySha256'] == selection['admission']['topology']['sha256'],
            'resource-domain topology correspondence missing')
    require(not hosted or selection['resourceDomain']['domainEvidenceSha256'] == selection['admission']['domain']['sha256'],
            'hosted domain evidence correspondence missing')
    for reference in selection['admission'].values():
        fields(reference, 'path sha256')
        regular_bytes(path(reference['path']), MAX_JSON_BYTES, text(reference['sha256'], r'[0-9a-f]{64}'))
    return selection, object_id



def hosted_domain(selection, allocation, load_root):
    """Join root-produced hosted-VM domain evidence to the selected volume.

    Evidence is bounded correspondence only; the shared resource admission still
    observes memory, CPU and disk afresh at the operation boundary."""
    if selection['resourceDomain']['relation'] not in DOMAIN_RELATIONS:
        return None
    reference = selection['admission']['domain']
    evidence = strict_json(regular_bytes(path(reference['path']), MAX_JSON_BYTES, reference['sha256'])[0])
    fields(evidence, 'schema relation verdict refusals bootId runner services userns volume builder resources')
    require(evidence['schema'] == HOSTED_DOMAIN_SCHEMA and evidence['relation'] == selection['resourceDomain']['relation'],
            'hosted domain evidence schema differs')
    require(evidence['verdict'] == 'admitted' and evidence['refusals'] == [], 'hosted domain evidence refused')
    # Evidence belongs to this operation's own fresh VM boot; replay elsewhere refuses.
    require(text(evidence['bootId'], r'[0-9a-f-]{36}') == selection['clock']['bootId'],
            'hosted domain evidence is from another boot')
    runner = evidence['runner']
    if evidence['relation'] == DEDICATED_RELATION:
        require(shared.dedicated_runner_refusals(runner) == [], 'dedicated runner image/binding differs')
        require(runner['dedicated']['bootId'] == evidence['bootId'], 'dedicated binding boot differs')
    else:
        fields(runner, 'imageOS imageVersion githubHosted')
        require(runner['imageOS'] == HOSTED_RUNNER_IMAGE and runner['githubHosted'] is True
                and runner['imageVersion'] == HOSTED_RUNNER_IMAGE_VERSION, 'hosted runner image differs')
    services = evidence['services']; fields(services, 'stopped active daemonProcesses')
    require(services['stopped'] == list(HOSTED_STOPPED_SERVICES) and services['active'] == []
            and integer(services['daemonProcesses']) == 0, 'hosted container daemons not stopped')
    userns = evidence['userns']; fields(userns, 'apparmorRestrictUnprivileged maxUserNamespaces')
    require(integer(userns['apparmorRestrictUnprivileged']) == 0 and integer(userns['maxUserNamespaces']) > 0,
            'hosted rootless user namespaces unavailable')
    volume = evidence['volume']
    fields(volume, 'device filesystem bytes allocatedBytes freeBytes freeInodes discard backingDevice')
    require(volume['filesystem'] == 'ext4' and volume['discard'] == 'nodiscard', 'hosted volume format differs')
    require(integer(volume['allocatedBytes']) >= integer(volume['bytes']) > 0, 'hosted volume backing not preallocated')
    require(integer(volume['freeBytes']) >= shared.DISK_BYTES and integer(volume['freeInodes']) > 0,
            'hosted volume free floor not met')
    device = text(volume['device'], r'[0-9]+:[0-9]+')
    require(text(volume['backingDevice'], r'[0-9]+:[0-9]+') != device, 'hosted volume backing is not a separate file')
    for value in (allocation, load_root):
        actual = value.stat().st_dev
        require(f'{os.major(actual)}:{os.minor(actual)}' == device, 'hosted allocation/load store outside the domain volume')
    builder = evidence['builder']; fields(builder, 'version')
    require(re.fullmatch(r'buildah version ' + re.escape(shared.BUILDER_VERSION) + r'(?: \([^\r\n]{1,512}\))?',
                         text(builder['version'])) is not None, 'hosted builder version differs')
    resources = evidence['resources']; fields(resources, 'memAvailableBytes logicalCpus affinity')
    require(integer(resources['memAvailableBytes']) >= shared.RSS_BYTES + 2 * 1024**3
            and integer(resources['logicalCpus']) >= 4 and integer(resources['affinity']) >= 4,
            'hosted domain capacity floor not met')
    disposition = strict_json(regular_bytes(path(selection['admission']['resourceDisposition']['path']), MAX_JSON_BYTES,
                                            selection['admission']['resourceDisposition']['sha256'])[0])
    require(disposition == {'sharedHostReservedMemoryBytes': 0, 'sharedHostReservedCpu': 0,
                            'sharedHostReservedDiskBytes': 0, 'unquantifiedResourceConflicts': []},
            'hosted domain cannot carry shared-host reservations')
    return {'relation': evidence['relation'], 'evidenceSha256': reference['sha256'], 'volumeDevice': device,
            'runnerImageVersion': runner['imageVersion']}


def namespace(root, graphroot, runroot):
    paths, env, expected = shared.namespace(root, graphroot, runroot)
    expected['containers.conf']['network']['default_rootless_network_cmd'] = 'pasta'
    expected['containers.conf']['engine']['helper_binaries_dir'] = ['/usr/bin', '/usr/lib/podman']
    expected['registries.conf']['registry'] = [
        {'prefix': 'docker.io/library/ubuntu', 'location': 'docker.io/library/ubuntu',
         'insecure': False, 'blocked': False}]
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
            raise RuntimeError('bar-headless: invalid engine configuration') from error
        require(actual == content, 'unreviewed engine configuration key/value')
        config_pins[str(value)] = {'identity': object_id, 'sha256': hashlib.sha256(payload).hexdigest()}
    return paths, env, pins, config_pins



def check_inputs(args, api, *, command=None, skip_source_commands=False):
    selection, input_object = selected_input(args)
    captured_source = globals().get('_BOOTSTRAP_SOURCE_SHA256')
    require(captured_source is None or captured_source ==
            selection['helper']['files']['eng/portable_workspace_bar.py'],
            'internal source captured at bootstrap differs from selected source')
    require(SHARED_SOURCE_SHA256 == selection['helper']['files']['eng/portable_workspace_sc2.py'],
            'shared lifetime captured source differs from selected source')
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
    recipe = regular_bytes(product / 'containers/barc-stock-headless/Containerfile', MAX_JSON_BYTES)[0].decode()
    from_lines = [line for line in recipe.splitlines() if line.startswith('FROM ')]
    require(from_lines == ['FROM ' + BASE_IMAGE + ' AS toolchain',
                           'FROM toolchain AS development',
                           'FROM development AS headless-integration'],
            'exact three-stage BAR recipe required')
    resources = strict_json(regular_bytes(product / 'containers/barc-stock-headless/resources.json', MAX_JSON_BYTES)[0])
    require(resources['toolchain']['parentOsCandidate'] == BASE_IMAGE, 'BAR resource base differs')
    # Exact recipe hashes remain external source pins, not authority. These
    # consumed positive asset digests bind the final effective image as well.
    for digest in (resources['engine']['headlessSha256'], resources['map']['sha256'], resources['game']['sdpSha256']):
        text(digest, r'[0-9a-f]{64}')
    text(resources['map']['file'], r'[a-zA-Z0-9_.-]+')
    text(resources['game']['sdp'], r'[0-9a-f]{32}\.sdp')
    executing = Path(__file__).resolve()
    for name, actual in [('eng/portable_workspace_bar.py', executing),
                         ('eng/portable_workspace_sc2.py', _shared_path),
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
    require(load_root == Path.home() / '.local/share/fsgg/headless-images/bar' / selection['operationId'],
            'fresh operation-specific BAR load store required')
    require(all(not any(value.iterdir()) for value in (build_root, build_run, load_root, load_run)),
            'BAR stores and runroots must be empty before consuming this operation')
    disjoint([('allocation', allocation), ('load store', load_root)])
    hosted = hosted_domain(selection, allocation, load_root)
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
    require(selection['network']['seed'] == network_seed(), 'network helper seed differs')
    security = strict_json(regular_bytes(path(selection['admission']['security']['path']), MAX_JSON_BYTES)[0])
    require(security.get('schema') == 'fsgg.bar-network-security/1'
            and security.get('bootId') == selection['clock']['bootId']
            and type(security.get('observations')) is dict, 'security observation/boot correspondence differs')
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
            'storageReservation': None, 'resources': resources, 'hostedDomain': hosted,
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


class Lifetime(shared.Lifetime):
    def guard_objects(self, *, retirement=False):
        super().guard_objects(retirement=retirement)
        require(network_seed() == self.selection['network']['seed'], 'network helper alias/object changed')
        for alias, expected in getattr(self, 'network_library_aliases', {}).items():
            require(str(Path(alias).resolve(strict=True)) == expected, 'network library alias changed')
        require(not Path('/etc/ld.so.preload').exists(), 'network loader preload refused')

    def network_prerequisite(self):
        self.record({'event': 'network-security-observation',
                     'status': 'observations only; Unknown is not LSM absence',
                     'selfStatus': bounded_observation('/proc/self/status'),
                     'selfLabel': bounded_observation('/proc/self/attr/current'),
                     'mountinfo': bounded_observation('/proc/self/mountinfo')})
        files = dict(self.selection['network']['seed']['files'])
        self.network_library_aliases = {}
        loader = self.selection['support']['elf-loader']['path']
        for binary in NETWORK_BINARIES:
            output = self.execute('network-loader-list', [loader, '--list', binary], 30).stdout
            require('not found' not in output, 'network helper dependency unavailable')
            paths = {loader}
            for line in output.splitlines():
                match = re.search(r'(?:=>\s+|^\s*)(/[^\s]+)', line)
                if match:
                    resolved = Path(match[1]).resolve(strict=True)
                    require(resolved.is_relative_to(NETWORK_LIBRARY_ROOT), 'network library outside fixed root')
                    self.network_library_aliases[match[1]] = str(resolved)
                    paths.add(str(resolved))
            require(2 <= len(paths) <= 32, 'network loader closure malformed')
            for name in paths:
                payload, _ = regular_bytes(path(name), 256 * 1024**2, owned=False)
                files[name] = {'sha256': hashlib.sha256(payload).hexdigest(),
                               'identity': file_token(path(name), owned=False)}
        if Path('/etc/ld.so.cache').exists():
            payload, _ = regular_bytes(path('/etc/ld.so.cache'), 16 * 1024**2, owned=False)
            files['/etc/ld.so.cache'] = {'sha256': hashlib.sha256(payload).hexdigest(),
                                       'identity': file_token(path('/etc/ld.so.cache'), owned=False)}
        for name, row in files.items():
            require(Path(name).stat().st_uid in (0, NETWORK_OWNER), 'network closure owner differs')
            self.checked['supportPins'][name] = pinned_token(path(name), 256 * 1024**2,
                                                            row['sha256'], owned=False)
        self.record({'event': 'network-support-seal', 'inputSha256': self.checked['inputSha256'],
                     'seed': self.selection['network']['seed'], 'files': files,
                     'libraryAliases': self.network_library_aliases})
        version = self.execute('network-helper-version', [NETWORK_ALIASES[0], '--version'], 30).stdout
        require(bool(version.splitlines()) and version.splitlines()[0] == NETWORK_VERSION, 'network helper version differs')
        self.census()
        commands = proposed_commands(self.checked, self.directory)
        self.execute('base-pull', commands['pull'], 600)
        self.base_image_prepared = True
        context = self.directory / 'network-prerequisite'; context.mkdir(mode=0o700)
        recipe = context / 'Containerfile'
        descriptor = os.open(recipe, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(network_probe_recipe())
        iid = self.directory / 'network-prerequisite.id'
        require(not iid.exists() and not iid.is_symlink(), 'fresh network iidfile required')
        argv = [value for value in commands['build']
                if not value.startswith(('--target=', '--tag=', '--iidfile=', '--file='))]
        argv[-1:] = ['--tag=localhost/fsgg/bar-network:' + self.selection['operationId'],
                     '--iidfile=' + str(iid), '--file=' + str(recipe), str(context)]
        before = {key: Path('/proc/self/ns/' + key).stat().st_ino for key in ('pid', 'net')}
        # A base pull can take600s. Sampling ceilings do not refresh free
        # headroom/CPU admission; this new label needs its own adjacent gate.
        self.resource_admission()
        output = self.execute('network-prerequisite-build', argv, 120).stdout
        rows = [line.removeprefix('FSGG_NET_PROBE ') for line in output.splitlines()
                if line.startswith('FSGG_NET_PROBE ')]
        require(len(rows) == 1, 'network RUN result missing/ambiguous')
        observed = strict_json(rows[0].encode()); fields(observed, 'uid noNewPrivs procPidCorrespondence pidNamespace networkNamespace')
        require(type(observed['uid']) is int and type(observed['noNewPrivs']) is int
                and observed['uid'] == 0 and observed['noNewPrivs'] == 1
                and type(observed['procPidCorrespondence']) is int and observed['procPidCorrespondence'] == 1
                and type(observed['pidNamespace']) is int and type(observed['networkNamespace']) is int
                and observed['pidNamespace'] > 0 and observed['networkNamespace'] > 0
                and observed['pidNamespace'] != before['pid'] and observed['networkNamespace'] != before['net'],
                'network RUN isolation correspondence differs')
        image = text(regular_bytes(iid, 80)[0].decode().strip(), r'sha256:[0-9a-f]{64}')
        self.network_result = {'imageId': image, 'recipeSha256': hashlib.sha256(network_probe_recipe()).hexdigest(),
                               'observed': observed, 'parentNamespaces': before,
                               'scope': 'private network setup and fixed RUN only; no egress or BAR image acceptance'}
        self.record({'event': 'network-prerequisite-passed', **self.network_result})

    def record(self, value, primary=None):
        if value.get('event') == 'run-start':
            value = {**value, 'profile': PROFILE}
        return super().record(value, primary)

    def preflight(self):
        super().preflight()
        # The common platform preflight covers the original required mapping;
        # BAR additionally needs its exact final USER10001 before any build.
        script = ('import json,pathlib; print(json.dumps({"uidMap":'
                  'pathlib.Path("/proc/self/uid_map").read_text(),"gidMap":'
                  'pathlib.Path("/proc/self/gid_map").read_text()}))')
        observed = self.native_json('bar-user-mapping', self.checked['builder'] +
            ['unshare', self.selection['support']['python']['path'], '-I', '-c', script])
        fields(observed, 'uidMap gidMap')
        for value in observed.values():
            rows = value.splitlines()
            require(0 < len(rows) <= 16, 'bounded BAR namespace mapping required')
            mappings = [tuple(map(int, row.split())) for row in rows]
            require(all(len(row) == 3 and all(n >= 0 for n in row) for row in mappings)
                    and any(start <= 10001 < start + count for start, _, count in mappings),
                    'namespace does not map BAR UID/GID10001')
        require(not self.census(), 'fresh BAR load store already has images')
        self.record({'event': 'bar-preflight-passed', 'mappedImageUser': 10001})
        self.network_prerequisite()


def verify_image_config(value):
    require(type(value) is dict and value.get('Os') == 'linux' and value.get('Architecture') == 'amd64',
            'BAR image platform mismatch')
    config = value.get('Config', {})
    require(config.get('User') == '10001:10001' and config.get('WorkingDir') == '/work'
            and config.get('Entrypoint') == ['bash', '/opt/barc/entrypoint.sh']
            and config.get('Cmd') in (None, []), 'BAR image execution configuration mismatch')
    require(not config.get('Volumes') and not config.get('OnBuild'), 'unexpected BAR image effects')


def verify_static_elf(payload, population, resolve, observe, expected_engine):
    """Join produced metadata to the effective archive, without loading ELF."""
    require(type(payload) is bytes and 0 < len(payload) <= MAX_JSON_BYTES, 'bounded static ELF manifest required')
    value = strict_json(payload)
    fields(value, 'schema roots files aliases loadedAbiAccepted scope expectedRoots')
    require(value['schema'] == 'bar-static-elf-correspondence/1' and value['loadedAbiAccepted'] is False,
            'static ELF schema or acceptance scope differs')
    require(value['scope'] == 'ELF64 x86-64 image-local DT_NEEDED/interpreter candidate correspondence; not loader order, symbols, versions, dlopen or loaded ABI proof',
            'static ELF correspondence scope differs')
    fields(value['roots'], 'engine plugin'); fields(value['expectedRoots'], 'engine plugin')
    files, aliases = value['files'], value['aliases']
    require(type(files) is dict and 0 < len(files) <= 256 and type(aliases) is dict and 0 < len(aliases) <= 8192,
            'static ELF file/alias population bound')
    def member(name):
        text(name)
        require(name.startswith('/') and name != '/' and PurePosixPath(name).as_posix() == name
                and all(part not in ('', '.', '..') for part in name[1:].split('/')), 'noncanonical static ELF path')
        return name[1:]
    def library(name):
        text(name, r'[A-Za-z0-9_+.-]+')
        require(name not in ('.', '..'), 'invalid static ELF library name')
    total = 0
    graph = {}
    for name, record in files.items():
        observe(); relative = member(name)
        fields(record, 'sha256 bytes metadata dependencies')
        digest = text(record['sha256'], r'[0-9a-f]{64}')
        require(type(record['bytes']) is int and 64 <= record['bytes'] <= 256 * 1024**2,
                'static ELF file byte bound')
        total += record['bytes']; require(total <= 512 * 1024**2, 'static ELF cumulative byte bound')
        require(resolve(relative) == relative, 'static ELF canonical file became an alias')
        actual = population.get(relative, {})
        require(actual.get('kind') == 'file' and actual.get('sha256') == digest and actual.get('bytes') == record['bytes'],
                'static ELF effective file correspondence differs: ' + name)
        metadata = record['metadata']; fields(metadata, 'class machine type needed soname interpreter searchPath searchPathKind')
        require(metadata['class'] == 'ELF64' and metadata['machine'] == 'x86-64'
                and type(metadata['type']) is int and metadata['type'] in (2, 3), 'unsupported static ELF metadata')
        needed = metadata['needed']
        require(type(needed) is list and len(needed) <= 128, 'static ELF dependency population bound')
        for dependency in needed: library(dependency)
        require(len(set(needed)) == len(needed), 'duplicate static ELF dependency')
        if metadata['soname'] is not None: library(metadata['soname'])
        require(metadata['interpreter'] in (None, '/lib64/ld-linux-x86-64.so.2'), 'unsupported static ELF interpreter')
        if metadata['searchPath'] is None:
            require(metadata['searchPathKind'] is None, 'static ELF search kind without path')
        else:
            text(metadata['searchPath']); require(metadata['searchPathKind'] in ('RPATH', 'RUNPATH'), 'unsupported static ELF search kind')
            entries = metadata['searchPath'].split(':')
            require(len(entries) <= 16 and all(entry == '$ORIGIN' or entry.startswith('$ORIGIN/') for entry in entries),
                    'unsupported static ELF search path')
            require(all('$' not in entry[len('$ORIGIN'):] and '..' not in PurePosixPath(entry[len('$ORIGIN'):]).parts
                        for entry in entries), 'unsupported static ELF search expansion')
        edges = record['dependencies']; require(type(edges) is list and len(edges) <= 129, 'static ELF edge population bound')
        needed_edges = {}; interpreter_edges = []
        for edge in edges:
            observe(); require(type(edge) is dict, 'static ELF edge required')
            if set(edge) == {'needed', 'path'}:
                library(edge['needed']); require(edge['needed'] not in needed_edges, 'duplicate static ELF edge')
                needed_edges[edge['needed']] = edge['path']
            else:
                fields(edge, 'interpreter path'); interpreter_edges.append(edge)
            member(edge['path']); require(edge['path'] in files, 'static ELF edge target omitted')
        require(set(needed_edges) == set(needed), 'static ELF needed/edge population differs')
        expected_interpreters = [] if metadata['interpreter'] is None else [metadata['interpreter']]
        require([edge['interpreter'] for edge in interpreter_edges] == expected_interpreters, 'static ELF interpreter edge differs')
        for edge in interpreter_edges:
            require(aliases.get(edge['interpreter']) == edge['path'], 'static ELF interpreter alias omitted or changed')
        graph[name] = [edge['path'] for edge in edges]
    for selected, target in aliases.items():
        observe(); relative = member(selected); member(target)
        require(target in files and '/' + resolve(relative) == target, 'static ELF effective alias correspondence differs')
    required_aliases = set()
    for name, record in files.items():
        metadata = record['metadata']; origin = PurePosixPath(name).parent
        directories = [str(origin), '/lib/x86_64-linux-gnu', '/usr/lib/x86_64-linux-gnu', '/lib64', '/usr/lib64']
        if metadata['searchPath'] is not None:
            for entry in metadata['searchPath'].split(':'):
                directory = str(origin / entry[len('$ORIGIN'):].lstrip('/'))
                actual = '/' + resolve(directory.lstrip('/'))
                require(actual == '/' or population.get(actual[1:], {}).get('kind') == 'directory',
                        'static ELF search directory missing')
                directories.append(actual)
        for edge in record['dependencies']:
            observe()
            if 'needed' in edge:
                candidates = set()
                for directory in directories:
                    observe(); selected = str(PurePosixPath(directory) / edge['needed'])
                    target = '/' + resolve(member(selected))
                    actual = population.get(target[1:])
                    require(actual is not None or member(selected) not in population, 'static ELF dangling candidate alias')
                    if actual is not None:
                        require(actual.get('kind') == 'file' and target in files, 'static ELF lookup resolves outside declared files')
                        require(aliases.get(selected) == target, 'static ELF lookup alias omitted or changed')
                        candidates.add(target); required_aliases.add(selected)
                require(candidates == {edge['path']}, 'static ELF dependency candidate correspondence differs')
            else:
                required_aliases.add(edge['interpreter'])
    for role, selected in (('engine', '/opt/barc/engine/spring-headless'), ('plugin', '/opt/barc/plugin/libSkirmishAI.so')):
        target = value['roots'][role]; member(target)
        require(target in files and aliases.get(selected) == target and '/' + resolve(member(selected)) == target,
                'static ELF selected root correspondence differs')
        required_aliases.add(selected)
        digest = text(value['expectedRoots'][role], r'[0-9a-f]{64}')
        require(files[target]['sha256'] == digest and (role != 'engine' or digest == expected_engine),
                'static ELF expected root digest differs')
    require(required_aliases == set(aliases), 'static ELF lookup alias population differs')
    reached = set(); pending = list(value['roots'].values())
    while pending:
        observe(); name = pending.pop()
        if name in reached: continue
        reached.add(name); pending.extend(graph[name])
    require(reached == set(files), 'static ELF declared closure contains unreachable files')
    for record in files.values():
        for edge in record['dependencies']:
            observe()
            if 'needed' in edge:
                soname = files[edge['path']]['metadata']['soname']
                require(soname is None or soname == edge['needed'], 'static ELF dependency SONAME differs')
    return {'manifestSha256': hashlib.sha256(payload).hexdigest(), 'manifestBytes': len(payload),
            'files': len(files), 'fileBytes': total, 'aliases': len(aliases), 'roots': value['roots'],
            'scope': 'effective OCI file/hash/alias readback of produced static candidate correspondence',
            'loadedAbiAccepted': False}


def oci_policy(checked):
    resources = checked['resources']
    files = {'recipe/' + Path(name).name: digest for name, digest in checked['selection']['product']['files'].items()}
    files.update({'opt/barc/resources.json': checked['selection']['product']['files']['containers/barc-stock-headless/resources.json'],
        'opt/barc/entrypoint.sh': checked['selection']['product']['files']['containers/barc-stock-headless/entrypoint.sh'],
        'opt/barc/engine/spring-headless': resources['engine']['headlessSha256'],
        'opt/barc/data/maps/' + resources['map']['file']: resources['map']['sha256'],
        'opt/barc/data/packages/' + resources['game']['sdp']: resources['game']['sdpSha256']})
    return {'archiveBytes': shared.ARCHIVE_BYTES, 'expandedBytes': shared.EXPANDED_BYTES,
        'layerBytes': shared.LAYER_BYTES, 'headers': shared.MAX_HEADERS, 'hashFiles': True,
        'strictTypes': True, 'imageName': checked['selection']['product']['exportName'],
        'pathMetadataBytes': shared.PATH_METADATA_BYTES, 'jsonBytes': MAX_JSON_BYTES,
        'verifyConfig': verify_image_config, 'files': files,
        'verifyBarStaticElf': lambda payload, population, resolve, observe: verify_static_elf(
            payload, population, resolve, observe, resources['engine']['headlessSha256']),
        'requiredRegularFiles': ['opt/barc/plugin/libSkirmishAI.so', 'opt/barc/PLUGIN.SHA256',
            'opt/barc/BUILD-SOURCES.txt', 'opt/barc/toolchain/ubuntu-packages.txt',
            'opt/barc/toolchain/vcpkg-status.txt', 'opt/barc/toolchain/SHA256SUMS',
            'opt/barc/toolchain/MANIFEST.SHA256', 'opt/highbar/tests/headless/_launch.sh', 'opt/barc/STATIC-ELF.json']}


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
                                '--security-opt=no-new-privileges', '--http-proxy=false', '--target=headless-integration',
                                '--tag=' + product['localTag'],
                                '--iidfile=' + str(iid), '--file=' + str(checked['product'] / 'containers/barc-stock-headless/Containerfile'),
                                str(checked['product'])],
            'iidfile': str(iid), 'archive': str(archive)}



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
    require(not owner.census(), 'BAR load store must remain fresh before acquisition')
    require(owner.base_image_prepared, 'network prerequisite has not prepared base')
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
    # This generation owns a fresh separate load store. Recheck it before load;
    # no foreign-image reuse, removal or global prune is permitted.
    require(not owner.census(), 'BAR load store changed before load')
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
            'producedOutputs': verified['producedOutputs'],
            'staticElfCorrespondence': verified['staticElfCorrespondence'],
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
                'hostedDomain': checked['hostedDomain'],
                'nativeExecutionEnabled': False, 'authority': 'external selection required; correspondence only'}
    # Pure file/config/object checks precede the consumed-original fence. All
    # subsequent subprocesses, including Git, share its original capture owner.
    checked = check_inputs(args, api, skip_source_commands=True)
    require((args.mode == 'preflight') == (checked['selection']['network']['purpose'] == 'network-prerequisite'),
            'controller mode/purpose differs')
    owner = Lifetime(args, api, checked)
    result = None
    manifest = None
    try:
        checked_again = check_inputs(args, api, command=owner.execute)
        require(checked_again['inputIdentity'] == checked['inputIdentity'], 'input changed at native boundary')
        owner.checked = checked_again
        owner.preflight()
        require((args.mode == 'preflight') == (owner.selection['network']['purpose'] == 'network-prerequisite'),
                'controller mode/purpose differs')
        result = ({'profile': PROFILE, 'preflight': 'passed', 'networkPrerequisite': owner.network_result,
                   'candidateQualified': False} if args.mode == 'preflight' else qualify(owner))
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
            'scope': ('network prerequisite only; no product image acceptance' if args.mode == 'preflight' else
                      'ordinary sampled profile; native/product and whole descendant acceptance separate')}
