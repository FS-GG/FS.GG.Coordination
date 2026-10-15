#!/usr/bin/env python3
"""Prepare pinned callback source, enforce exact TRX census, retain gate evidence."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]


def require(condition, reason):
    if not condition:
        raise RuntimeError(reason)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare():
    pin = json.loads((ROOT / 'eng/utel-callback-source.json').read_bytes())
    destination = pathlib.Path(os.environ['FSGG_UTEL_CALLBACK_SOURCE'])
    require(destination.is_absolute() and not destination.exists(), 'fresh callback source directory required')
    destination.mkdir(mode=0o700, parents=True)
    def git(*arguments):
        return subprocess.check_output(['git', '-C', str(destination), *arguments], stderr=subprocess.DEVNULL, timeout=60).decode().strip()
    git('init', '--quiet')
    git('-c', 'credential.helper=', 'fetch', '--quiet', '--depth=1', 'https://github.com/FS-GG/.github.git', pin['revision'])
    git('checkout', '--quiet', '--detach', 'FETCH_HEAD')
    require(git('rev-parse', 'HEAD') == pin['revision'], 'callback source revision differs')
    manifest_path = destination / 'tests/release-successor-authentication/callable-source-manifest.json'
    require(digest(manifest_path) == pin['manifestSha256'], 'callback source manifest differs')
    manifest = json.loads(manifest_path.read_bytes())
    rows = sorted(manifest['files'], key=lambda row: (row['repository'] + '/' + row['path']).encode('ascii'))
    require(rows == manifest['files'], 'callback manifest order differs')
    for row in rows:
        source = destination if row['repository'] == 'FS-GG/.github' else ROOT
        path = source / row['path']
        require(path.is_file() and not path.is_symlink() and digest(path) == row['sha256'], 'callback source closure differs')
    canonical = ''.join(row['sha256'] + '  ' + row['repository'] + '/' + row['path'] + '\n' for row in rows).encode('ascii')
    require(hashlib.sha256(canonical).hexdigest() == manifest['bundleDigest'], 'callback bundle digest differs')
    evidence = pathlib.Path(os.environ['RUNNER_TEMP']) / 'compiler-and-tests'
    evidence.mkdir(parents=True, exist_ok=True)
    (evidence / 'callback-source.json').write_text(json.dumps({
        'repository': pin['repository'], 'revision': pin['revision'],
        'manifestSha256': pin['manifestSha256'], 'bundleDigest': manifest['bundleDigest'],
        'profile': 'in-memory managed child phase/control; separate Python callback process',
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
