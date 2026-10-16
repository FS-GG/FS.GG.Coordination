#!/usr/bin/env python3
"""Prepare pinned callback source, enforce exact TRX census, retain gate evidence."""
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST_PATH = 'tests/release-successor-authentication/callback-pair-manifest.json'
VECTORS_PATH = 'tests/release-successor-authentication/callback-pair-vectors.json'
PAIR_ROLES = {
    'FS-GG/.github': {
        'scripts/release-successor-publish.py',
        'scripts/release_successor_auth_consumer.py',
        'scripts/release_successor_provider.py',
        'tests/release-successor-authentication/callback-contract.json',
        'tests/release-successor-authentication/callback-fixture.py',
        'tests/release-successor-authentication/wire-contract.json'},
    'FS-GG/FS.GG.Coordination': {
        'src/FS.GG.Coordination.Orchestration.Execution/FiniteCustodyComposition.fs',
        'src/FS.GG.Coordination.Orchestration.Execution/FiniteCustodySessionAdapter.fs',
        'src/FS.GG.Coordination.Orchestration.Execution/PhaseCallbackProtocol.fs',
        'src/FS.GG.Coordination.Orchestration.Execution/PhaseResultProtocol.fs',
        'src/FS.GG.Coordination.Orchestration.Runner.Client/FiniteCustodyInertAdapter.fs',
        'src/FS.GG.Coordination.Orchestration.Runner.Client/Program.fs'}}

SLOT_PROFILE = 'inert-slot-callback-pair/1'
SLOT_MANIFEST_PATH = 'tests/release-successor-authentication/slot-callback-pair-manifest.json'
SLOT_VECTORS_PATH = 'tests/release-successor-authentication/slot-callback-pair-vectors.json'
SLOT_ENTRY_PATH = 'tests/release-successor-authentication/slot-sink-callback-entry.py'
SLOT_PAIR_ROLES = {
    'FS-GG/.github': {
        'scripts/fsgg_feed.py',
        'scripts/release_successor_auth_consumer.py',
        'scripts/release_successor_execution.py',
        'scripts/release_successor_provider.py',
        'tests/release-successor-authentication/callback-contract.json',
        'tests/release-successor-authentication/domain_contract.py',
        'tests/release-successor-authentication/inert_phase_slot_sink.py',
        'tests/release-successor-authentication/slot-sink-fixture.py',
        'tests/release-successor-authentication/wire-contract.json',
        SLOT_ENTRY_PATH},
    'FS-GG/FS.GG.Coordination': PAIR_ROLES['FS-GG/FS.GG.Coordination']}


def profile_paths(pin):
    if pin.get('schema') == 'fsgg.utel-callback-source/3':
        require(pin.get('profile') == SLOT_PROFILE, 'callback pin profile differs')
        return SLOT_MANIFEST_PATH, SLOT_VECTORS_PATH, SLOT_PAIR_ROLES
    return MANIFEST_PATH, VECTORS_PATH, PAIR_ROLES


def require(condition, reason):
    if not condition:
        raise RuntimeError(reason)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def validate_pin(pin):
    fields = {'schema', 'repository', 'revision', 'manifestSha256',
              'vectorsSha256', 'bundleDigest', 'qualification'}
    if pin.get('schema') == 'fsgg.utel-callback-source/3':
        fields.add('profile')
    require(set(pin) == fields, 'callback pin fields differ')
    require(pin['schema'] in {'fsgg.utel-callback-source/2', 'fsgg.utel-callback-source/3'}
            and pin['repository'] == 'FS-GG/.github', 'callback pin profile differs')
    profile_paths(pin)
    require(isinstance(pin['revision'], str) and re.fullmatch('[0-9a-f]{40}', pin['revision']),
            'callback pin revision differs')
    for key in ['manifestSha256', 'vectorsSha256', 'bundleDigest']:
        require(isinstance(pin[key], str) and re.fullmatch('[0-9a-f]{64}', pin[key]),
                'callback pin digest differs')


def validate_pair(pin, destination):
    validate_pin(pin)
    manifest_name, vectors_name, pair_roles = profile_paths(pin)
    manifest_path = destination / manifest_name
    vectors_path = destination / vectors_name
    for path, key in [(manifest_path, 'manifestSha256'), (vectors_path, 'vectorsSha256')]:
        require(path.is_file() and not path.is_symlink() and digest(path) == pin[key],
                'callback pair artifact differs')
    manifest = json.loads(manifest_path.read_bytes())
    require(manifest['schema'] == 'fsgg.authentication-callable-source-bundle/1',
            'callback pair manifest schema differs')
    rows = sorted(manifest['files'], key=lambda row: (row['repository'] + '/' + row['path']).encode('ascii'))
    roles = [(row['repository'], row['path']) for row in rows]
    expected = {(repo, path) for repo, paths in pair_roles.items() for path in paths}
    require(rows == manifest['files'] and len(roles) == len(expected) and set(roles) == expected,
            'callback pair role inventory differs')
    for row in rows:
        source = destination if row['repository'] == 'FS-GG/.github' else ROOT
        path = source / row['path']
        require(path.is_file() and not path.is_symlink() and digest(path) == row['sha256'],
                'callback source closure differs')
    canonical = ''.join(row['sha256'] + '  ' + row['repository'] + '/' + row['path'] + '\n' for row in rows).encode('ascii')
    require(hashlib.sha256(canonical).hexdigest() == manifest['bundleDigest'] == pin['bundleDigest'],
            'callback bundle digest differs')
    vectors = json.loads(vectors_path.read_bytes())
    require(vectors['schema'] == 'fsgg.authentication-callback-vectors/1'
            and vectors['binding']['source']['contract'] == manifest['bundleDigest'],
            'callback vectors bundle differs')
    return manifest



CLOSED_PIN_PATH = 'eng/utel-closed-callback-source.json'
CLOSED_MANIFEST_PATH = 'tests/release-successor-authentication/closed-source-manifest.json'
CLOSED_PROFILE_PATH = 'tests/release-successor-authentication/closed-source-profile.json'
CLOSED_ROLES = (
    'scripts/fsgg_feed.py',
    'scripts/release_successor_auth_consumer.py',
    'scripts/release_successor_closed_callback.py',
    'scripts/release_successor_execution.py',
    'scripts/release_successor_provider.py',
    'tests/release-successor-authentication/domain_contract.py',
    'tests/release-successor-authentication/inert_phase_slot_sink.py',
    'tests/release-successor-authentication/domain-max-vectors.json',
    CLOSED_PROFILE_PATH,
)


def validate_closed(pin, destination):
    fields = {'schema', 'profile', 'repository', 'revision', 'manifestSha256',
              'profileSha256', 'vectorsSha256', 'qualification'}
    require(type(pin) is dict and set(pin) == fields, 'closed pin fields differ')
    require(pin['schema'] == 'fsgg.utel-closed-callback-source/1'
            and pin['profile'] == 'fsgg.utel-closed-source/1'
            and pin['repository'] == 'FS-GG/.github', 'closed pin profile differs')
    require(type(pin['revision']) is str and re.fullmatch('[0-9a-f]{40}', pin['revision']), 'closed revision differs')
    for key in ('manifestSha256', 'profileSha256', 'vectorsSha256'):
        require(type(pin[key]) is str and re.fullmatch('[0-9a-f]{64}', pin[key]), 'closed digest differs')
    for name, key in ((CLOSED_MANIFEST_PATH, 'manifestSha256'),
                      (CLOSED_PROFILE_PATH, 'profileSha256'), (SLOT_VECTORS_PATH, 'vectorsSha256')):
        path = destination / name
        require(path.is_file() and not path.is_symlink() and digest(path) == pin[key], 'closed artifact differs')
    manifest = json.loads((destination / CLOSED_MANIFEST_PATH).read_bytes())
    require(set(manifest) == {'schema', 'qualification', 'files'}
            and manifest['schema'] == 'fsgg.utel-closed-source-manifest/1', 'closed manifest differs')
    rows = manifest['files']
    require(type(rows) is list and [row.get('path') for row in rows] == list(CLOSED_ROLES)
            and all(set(row) == {'path', 'sha256'} for row in rows), 'closed source roles differ')
    for row in rows:
        path = destination / row['path']
        require(path.is_file() and not path.is_symlink() and digest(path) == row['sha256'], 'closed source differs')
    profile = json.loads((destination / CLOSED_PROFILE_PATH).read_bytes())
    require(profile['schema'] == pin['profile'] and profile['admission'] is False
            and all(value is None for value in profile['production'].values()), 'closed unavailable profile differs')
    return manifest


def prepare_closed():
    # Existing maintained acquisition route, separate immutable opt-in pin.
    pin = json.loads((ROOT / CLOSED_PIN_PATH).read_bytes())
    destination = pathlib.Path(os.environ['FSGG_UTEL_CALLBACK_SOURCE'] + '-closed')
    require(destination.is_absolute() and not destination.exists(), 'fresh closed source directory required')
    destination.mkdir(mode=0o700, parents=True)
    def git(*arguments):
        return subprocess.check_output(['git', '-C', str(destination), *arguments], stderr=subprocess.DEVNULL, timeout=60).decode().strip()
    # Reject malformed identity before the authenticated public fetch.
    require(pin.get('repository') == 'FS-GG/.github'
            and type(pin.get('revision')) is str and re.fullmatch('[0-9a-f]{40}', pin['revision']), 'closed fetch identity differs')
    git('init', '--quiet')
    git('-c', 'credential.helper=', 'fetch', '--quiet', '--depth=1', 'https://github.com/FS-GG/.github.git', pin['revision'])
    git('checkout', '--quiet', '--detach', 'FETCH_HEAD')
    require(git('rev-parse', 'HEAD') == pin['revision'], 'closed source revision differs')
    validate_closed(pin, destination)
    evidence = pathlib.Path(os.environ['RUNNER_TEMP']) / 'compiler-and-tests'
    evidence.mkdir(parents=True, exist_ok=True)
    (evidence / 'closed-callback-source.json').write_text(json.dumps(dict(pin,
        productionAvailable=False, concurrentClosedProviderJourney=False), sort_keys=True) + '\n')
    print('Pinned closed SOURCE fixture prepared; provider remains unavailable')


def prepare(slot=False):
    pin = json.loads((ROOT / ('eng/utel-slot-callback-source.json' if slot else 'eng/utel-callback-source.json')).read_bytes())
    validate_pin(pin)
    require((pin['schema'] == 'fsgg.utel-callback-source/3') == slot, 'callback operation profile differs')
    destination = pathlib.Path(os.environ['FSGG_UTEL_CALLBACK_SOURCE'] + ('-slots' if slot else ''))
    require(destination.is_absolute() and not destination.exists(), 'fresh callback source directory required')
    destination.mkdir(mode=0o700, parents=True)
    def git(*arguments):
        return subprocess.check_output(['git', '-C', str(destination), *arguments], stderr=subprocess.DEVNULL, timeout=60).decode().strip()
    git('init', '--quiet')
    git('-c', 'credential.helper=', 'fetch', '--quiet', '--depth=1', 'https://github.com/FS-GG/.github.git', pin['revision'])
    git('checkout', '--quiet', '--detach', 'FETCH_HEAD')
    require(git('rev-parse', 'HEAD') == pin['revision'], 'callback source revision differs')
    manifest = validate_pair(pin, destination)
    evidence = pathlib.Path(os.environ['RUNNER_TEMP']) / 'compiler-and-tests'
    evidence.mkdir(parents=True, exist_ok=True)
    manifest_name, vectors_name, _ = profile_paths(pin)
    (evidence / ('slot-callback-source.json' if slot else 'callback-source.json')).write_text(json.dumps({
        'repository': pin['repository'], 'revision': pin['revision'],
        'manifestPath': manifest_name, 'manifestSha256': pin['manifestSha256'],
        'vectorsPath': vectors_name, 'vectorsSha256': pin['vectorsSha256'],
        'bundleDigest': manifest['bundleDigest'],
        'profile': SLOT_PROFILE if slot else 'in-memory managed child phase/control; separate Python callback process',
        'nativeAuthChild': False, 'credentialEffects': False}, sort_keys=True) + '\n')
    print('Pinned source-only callback fixture prepared')


def census(path):
    expected = json.loads((ROOT / 'eng/utel-callback-cases.json').read_bytes())['cases']
    results = [node for node in ET.parse(path).getroot().iter() if node.tag.rsplit('}', 1)[-1] == 'UnitTestResult']
    actual = [node.attrib.get('testName', '') for node in results]
    require(len(actual) == len(set(actual)) and sorted(actual) == sorted(expected), 'callback exact case census differs')
    require(all(node.attrib.get('outcome') == 'Passed' for node in results), 'callback case did not pass')
    evidence = pathlib.Path(os.environ['RUNNER_TEMP']) / 'compiler-and-tests'
    (evidence / 'callback-case-census.json').write_text(json.dumps({'profile': 'source-only callback pair', 'cases': actual, 'passed': len(actual)}, sort_keys=True) + '\n')
    print('Exact callback case census passed:', len(actual))


def retain(original):
    temporary = pathlib.Path(os.environ['RUNNER_TEMP'])
    destination = temporary / 'compiler-and-tests'
    destination.mkdir(parents=True, exist_ok=True)
    for cohort in ['portable-host-allocation', 'portable-host-executor', 'finite-custody-inert', 'process-sessions']:
        source = temporary / ('compiler-and-tests-' + cohort)
        if source.is_dir():
            shutil.copytree(source, destination / cohort, dirs_exist_ok=True)
    architecture = ROOT / 'artifacts/test-results/70-gs2-03-1-qualification-manifest/architecture-tests.trx'
    if architecture.is_file():
        shutil.copyfile(architecture, destination / 'architecture.trx')
    return original


if __name__ == '__main__':
    operation = sys.argv[1] if len(sys.argv) > 1 else ''
    try:
        if operation == 'prepare' and len(sys.argv) == 2:
            prepare()
            prepare(slot=True)
            prepare_closed()
        elif operation == 'prepare-slot' and len(sys.argv) == 2:
            prepare(slot=True)
        elif operation == 'census' and len(sys.argv) == 3:
            census(sys.argv[2])
        elif operation == 'retain' and len(sys.argv) == 3:
            raise SystemExit(retain(int(sys.argv[2])))
        else:
            raise RuntimeError('callback fixture operation refused')
    except Exception:
        # Keep transport/provider exception strings and arbitrary payloads private.
        print('UTEL callback fixture ' + operation + ' refused; source/case/evidence prerequisite failed', file=sys.stderr)
        raise SystemExit(int(sys.argv[2]) if operation == 'retain' and len(sys.argv) == 3 and sys.argv[2].isdigit() and int(sys.argv[2]) else 3) from None
