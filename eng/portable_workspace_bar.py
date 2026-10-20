"""Closed BAR full-image adapter for the existing portable-workspace controller.

Reuses the unchanged source/capture/lifetime implementation with a separate fixed
product contract. No engine runtime or arbitrary command interface is provided.
"""
from __future__ import annotations
import hashlib
import importlib.util
import os
from pathlib import Path
import stat
import sys

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
MAX_JSON_BYTES, LOG_BYTES = shared.MAX_JSON_BYTES, shared.LOG_BYTES
SUPPORT_LIBRARIES, SUPPORT_EXECUTABLES = shared.SUPPORT_LIBRARIES, shared.SUPPORT_EXECUTABLES
fields, text, number = shared.fields, shared.text, shared.number
path, regular_bytes, strict_json = shared.path, shared.regular_bytes, shared.strict_json
pinned_token, file_token, identity = shared.pinned_token, shared.file_token, shared.identity
directory_pin, disjoint = shared.directory_pin, shared.disjoint
source_identity, context_directories = shared.source_identity, shared.context_directories
stream_identity = shared.stream_identity

def require(condition, message):
    if not condition:
        raise RuntimeError('bar-headless: ' + message)


def selected_input(args):
    require(args.operation_input and args.expected_operation_input_sha256, 'exact operation input required')
    expected = text(args.expected_operation_input_sha256, r'[0-9a-f]{64}')
    payload, object_id = regular_bytes(path(args.operation_input), MAX_JSON_BYTES, expected)
    require(object_id['mode'] == 0o600, 'private operation input mode0600 required')
    selection = strict_json(payload)
    fields(selection, 'schema operationId helper product allocation build load support clock admission resourceDomain licenseAccepted')
    require(selection['schema'] == 'fsgg.bar-headless-operation/1', 'BAR operation schema required')
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



def namespace(root, graphroot, runroot):
    paths, env, expected = shared.namespace(root, graphroot, runroot)
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
            'storageReservation': None, 'resources': resources,
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


def verify_image_config(value):
    require(type(value) is dict and value.get('Os') == 'linux' and value.get('Architecture') == 'amd64',
            'BAR image platform mismatch')
    config = value.get('Config', {})
    require(config.get('User') == '10001:10001' and config.get('WorkingDir') == '/work'
            and config.get('Entrypoint') == ['bash', '/opt/barc/entrypoint.sh']
            and config.get('Cmd') in (None, []), 'BAR image execution configuration mismatch')
    require(not config.get('Volumes') and not config.get('OnBuild'), 'unexpected BAR image effects')


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
        'requiredRegularFiles': ['opt/barc/plugin/libSkirmishAI.so', 'opt/barc/PLUGIN.SHA256',
            'opt/barc/BUILD-SOURCES.txt', 'opt/barc/toolchain/ubuntu-packages.txt',
            'opt/barc/toolchain/vcpkg-status.txt', 'opt/barc/toolchain/SHA256SUMS',
            'opt/barc/toolchain/MANIFEST.SHA256', 'opt/highbar/tests/headless/_launch.sh']}


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
