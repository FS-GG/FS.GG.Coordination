#!/usr/bin/env python3
"""Closed dedicated-image input/identity DATA checks. No provision, registration or native-policy setter.

The existing BAR domain and actual published catalog remain determining native gates.
A parsed JSON target or checksum does not supply provider/operator authorization.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat

BASELINE_ID = 'ubuntu2610-linux-x64-v1'
GROUP = 'fsgg-native-qualification'
LABEL = 'fsgg-native-linux-x64-v1'
SDK_SHA512 = '51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b'


def require(condition, reason):
    if not condition:
        raise ValueError('dedicated-runner: ' + reason)


def sha(value, length=64):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{' + str(length) + '}', value) is not None


def load(path, maximum=4 * 1024**2):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= maximum, 'bounded regular JSON required')
        raw = stream.read(maximum + 1)
    require(len(raw) <= maximum, 'JSON cap exceeded')
    def unique(rows):
        result = {}
        for key, value in rows:
            require(key not in result, 'duplicate JSON property')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def validate_target_data(target):
    require(target.get('schema') == 'fsgg.dedicated-qualification-target/1', 'target schema')
    require(target.get('baselineId') == BASELINE_ID, 'target baseline differs')
    for key in ('provider', 'providerInstanceId', 'providerImageId', 'systemUuid', 'operatorAcceptanceSha256'):
        require(isinstance(target.get(key), str) and target[key].strip() == target[key] and target[key], 'target missing ' + key)
    require(sha(target['operatorAcceptanceSha256']), 'operator acceptance digest')
    require(target.get('dedicated') is True and target.get('disposable') is True, 'dedicated disposable target required')
    require(target.get('sharedCodexHost') is False, 'shared Codex host forbidden')
    require(target.get('architecture') == 'x86_64', 'x64 VM required')
    require(target.get('os') == {'id': 'ubuntu', 'version': '26.10'}, 'actual Ubuntu26.10 carrier required')
    resources = target.get('resources', {})
    require(type(resources.get('vcpu')) is int and resources['vcpu'] >= 4, 'four CPU target floor')
    require(type(resources.get('memoryBytes')) is int and resources['memoryBytes'] >= 32 * 1024**3, '32GiB target floor')
    require(type(resources.get('diskBytes')) is int and resources['diskBytes'] >= 256 * 1024**3, '256GiB target floor')
    return target


def validate_lock_data(lock):
    require(lock.get('schema') == 'fsgg.dedicated-qualification-acquired-inputs/1', 'acquired input lock required')
    require(lock.get('baselineId') == BASELINE_ID, 'input baseline differs')
    image = lock.get('image', {})
    require(isinstance(image.get('url'), str) and image['url'].startswith('https://cloud-images.ubuntu.com/'), 'official Ubuntu image origin')
    require(sha(image.get('sha256')) and sha(image.get('signedChecksumSha256')), 'exact image/checksum pins required')
    require(sha(image.get('qualifiedAcquisitionReceiptSha256')), 'qualified signed image acquisition receipt required')
    require(isinstance(image.get('release'), str) and image['release'], 'immutable image release identity required')
    require(lock.get('sdk', {}).get('archiveSha512') == SDK_SHA512, 'SDK401 input bytes differ')
    require(lock.get('sdk', {}).get('version') == '10.0.401' and lock['sdk'].get('runtime') == '10.0.12', 'SDK/runtime tuple differs')
    runner = lock.get('runner', {})
    require(re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', runner.get('version', '')) is not None, 'exact runner version required')
    require(sha(runner.get('sha256')) and sha(runner.get('releaseMetadataSha256')), 'runner archive/release pins required')
    require(runner.get('url') == 'https://github.com/actions/runner/releases/download/v' + runner['version'] + '/actions-runner-linux-x64-' + runner['version'] + '.tar.gz', 'official fixed runner archive origin')
    require(lock.get('goRace', {}).get('version') == '1.27.1' and sha(lock['goRace'].get('archiveSha256')), 'Go1.27.1 race toolchain required')
    require(lock['goRace'].get('url') == 'https://go.dev/dl/go1.27.1.linux-amd64.tar.gz', 'official fixed Go archive origin')
    compiler = lock.get('compiler', {})
    require(compiler.get('raceRequired') is True and compiler.get('cgoEnabled') is True, 'real CGO/race required; no skip')
    for key in ('compilerSha256', 'headersManifestSha256', 'linkerSha256', 'runtimeClosureSha256'):
        require(sha(compiler.get(key)), 'compiler closure missing ' + key)
    require(sha(lock.get('aptTransaction', {}).get('qualifiedSignedTransactionSha256')), 'complete signed APT transaction required')
    require(sha(lock['aptTransaction'].get('expectedFinalInventorySha256')), 'complete final package inventory required')
    policy = lock.get('policy', {})
    require(type(policy.get('apparmorRestrictUnprivilegedUserns')) is int and policy['apparmorRestrictUnprivilegedUserns'] == 0, 'actual AppArmor policy0 required')
    require(policy.get('nativeWorkloadNoNewPrivileges') is True and policy.get('noJobPolicySetter') is True, 'native NNP/job-policy contract differs')
    return lock


def validate_population(lock, directory):
    root = Path(directory)
    require(root.is_absolute() and root.is_dir() and not root.is_symlink(), 'absolute regular input directory')
    rows = lock.get('files')
    require(isinstance(rows, list) and 1 <= len(rows) <= 20000, 'complete bounded input file inventory required')
    expected = []
    for row in rows:
        name = row.get('path')
        require(isinstance(name, str) and name and '\\' not in name, 'unsafe input path')
        path = PurePosixPath(name)
        require(not path.is_absolute() and all(part not in ('', '.', '..') for part in name.split('/')), 'unsafe input path')
        require(sha(row.get('sha256')) and type(row.get('bytes')) is int and 0 <= row['bytes'] <= 4 * 1024**3, 'file pin differs')
        expected.append(name)
    require(expected == sorted(set(expected)), 'ordered unique input population required')
    actual = []
    for path in root.rglob('*'):
        require(not path.is_symlink(), 'linked acquired input refused')
        if path.is_dir():
            continue
        require(stat.S_ISREG(path.stat().st_mode), 'special acquired input refused')
        actual.append(str(path.relative_to(root)))
    require(sorted(actual) == expected, 'acquired input population missing/extra')
    for row in rows:
        h = hashlib.sha256(); count = 0
        with (root / row['path']).open('rb') as stream:
            while True:
                data = stream.read(1024**2)
                if not data:
                    break
                count += len(data); require(count <= row['bytes'], 'input grew while hashing'); h.update(data)
        require(count == row['bytes'] and h.hexdigest() == row['sha256'], 'input hash/size differs: ' + row['path'])
    return {'files': len(rows), 'bytes': sum(row['bytes'] for row in rows), 'classification': 'input DATA checked; no invocation grant'}



def validate_tool_archives(lock, directory):
    root = Path(directory)
    selected = [
        ('sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz', 'sha512', SDK_SHA512),
        ('runner/actions-runner-linux-x64-' + lock['runner']['version'] + '.tar.gz', 'sha256', lock['runner']['sha256']),
        ('go/go1.27.1.linux-amd64.tar.gz', 'sha256', lock['goRace']['archiveSha256']),
        ('apt/package-inventory.expected', 'sha256', lock['aptTransaction']['expectedFinalInventorySha256'])]
    for name, algorithm, expected in selected:
        h = hashlib.new(algorithm)
        with (root / name).open('rb') as stream:
            while True:
                block = stream.read(1024**2)
                if not block:
                    break
                h.update(block)
        require(h.hexdigest() == expected, 'selected tool/inventory bytes differ: ' + name)
    return True


def validate_instance_match(target, approval, system_uuid):
    validate_target_data(target)
    require(approval.get('schema') == 'fsgg.dedicated-image-build-selection/1', 'external operator build selection required')
    require(approval.get('providerInstanceId') == target['providerInstanceId'], 'provider instance mismatch')
    require(approval.get('systemUuid') == system_uuid == target['systemUuid'], 'actual machine UUID mismatch')
    require(approval.get('baselineId') == BASELINE_ID and approval.get('operation') == 'build-dedicated-image', 'fixed image operation only')
    require(sha(approval.get('targetSha256')), 'exact selected target bytes required')
    return True



def validate_registration_data(instance, lock, runner_name):
    validate_lock_data(lock)
    require(instance.get('schema') == 'fsgg.dedicated-qualification-instance/1', 'instance binding required')
    require(instance.get('baselineId') == BASELINE_ID and instance.get('oneJobOnly') is True, 'single-use baseline instance required')
    require(instance.get('runnerName') == runner_name and re.fullmatch(r'fsgg-qualification-[a-zA-Z0-9-]{1,64}', runner_name), 'exact runner name required')
    require(instance.get('runnerGroup') == GROUP and instance.get('runnerLabel') == LABEL, 'fixed protected route differs')
    require(instance.get('workflowRestrictionVerified') is True, 'protected-main workflow restriction must be independently verified')
    for key in ('qualifiedImageSha256', 'rootInstanceAcceptanceSha256', 'registrationAdmissionSha256'):
        require(sha(instance.get(key)), 'instance/admission missing ' + key)
    for key in ('providerInstanceId', 'providerImageId', 'systemUuid'):
        require(isinstance(instance.get(key), str) and instance[key], 'instance missing ' + key)
    require(instance.get('credentialKind') == 'short-lived-registration-token', 'registration token role required; no PAT')
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['validate-inputs', 'verify-image-target', 'verify-registration'])
    parser.add_argument('--target', required=True)
    parser.add_argument('--lock')
    parser.add_argument('--inputs')
    parser.add_argument('--approval')
    parser.add_argument('--system-uuid')
    parser.add_argument('--runner-name')
    args = parser.parse_args()
    target = load(args.target)
    if args.operation == 'verify-registration':
        require(args.lock and args.runner_name, 'registration lock/name missing')
        validate_registration_data(target, load(args.lock), args.runner_name)
        require(args.system_uuid and target['systemUuid'] == args.system_uuid, 'registration actual VM UUID differs')
        print(json.dumps({'registrationDataChecked': True, 'authorization': 'external Root/provider/group owner only'}))
        return
    validate_target_data(target)
    if args.operation == 'validate-inputs':
        require(args.lock and args.inputs, 'lock/inputs missing')
        lock = load(args.lock); validate_lock_data(lock)
        population = validate_population(lock, args.inputs)
        validate_tool_archives(lock, args.inputs)
        print(json.dumps(population, sort_keys=True))
    else:
        require(args.approval and args.system_uuid, 'operator selection/actual UUID missing')
        approval = load(args.approval)
        require(hashlib.sha256(Path(args.target).read_bytes()).hexdigest() == approval.get('targetSha256'), 'selected target body differs')
        validate_instance_match(target, approval, args.system_uuid)
        print(json.dumps({'targetDataMatches': True, 'providerAttestation': 'external operator responsibility; not authenticated by JSON'}))


if __name__ == '__main__':
    main()
