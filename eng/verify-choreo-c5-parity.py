#!/usr/bin/env python3
"""Compare actual Quint legacy runs with the retained Choreo ITF milestones.

This is an observation projection, never a transition implementation. C2 first
regenerates all eight Choreo fixtures against the exact source and binary.
"""
import hashlib
import json
import os
import re
import sys
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / 'tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo'
QUINT_SHA = '939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f'


def require(condition, detail):
    if not condition:
        raise RuntimeError('CHOREO_PARITY_REFUSED ' + detail)


def export_legacy(directory):
    quint = os.environ.get('FSGG_QUINT_BIN') or shutil.which('quint')
    require(quint is not None, 'Quint executable missing')
    require(hashlib.sha256(Path(quint).read_bytes()).hexdigest() == QUINT_SHA,
            'Quint binary identity differs')
    source = (ROOT / 'src/FS.GG.Coordination.Protocol/Protocol.md').read_text()
    start = source.index('module O2HostedWriterModel {')
    end = source.index('// GS2-03.10 model 1:', start)
    model = directory / 'legacy.qnt'
    model.write_text(source[start:end])
    verification = subprocess.run([
        quint, 'verify', str(model), '--main=O2HostedWriterModel',
        '--init=init', '--step=step', '--invariant=safety',
        '--max-steps=20', '--backend=tlc', '--seed=0xC5F0', '--verbosity=3',
    ], cwd=directory, check=False, capture_output=True, text=True, timeout=150)
    diagnostic = verification.stdout + verification.stderr
    if verification.returncode != 0:
        print(diagnostic, file=sys.stderr)
        raise RuntimeError(f'CHOREO_PARITY_REFUSED legacy safety verifier exited {verification.returncode}')
    require('[ok] No violation found' in diagnostic, 'legacy safety did not pass')
    counts = re.findall(r'(\d+) states generated, (\d+) distinct states found, 0 states left on queue', diagnostic)
    require(bool(counts), 'legacy graph did not finish')
    generated, distinct = map(int, counts[-1])
    require(distinct <= 10000 and generated - 1 <= 10000, 'legacy graph exceeded its state/transition budget')
    print(f'CHOREO_LEGACY_ABSTRACTION_OK states={distinct} transitions={generated - 1}')
    subprocess.run([
        quint, 'test', str(model), '--main=O2HostedWriterLegacyScenarios',
        '--match=^(happy|lostApplied|claimAbsent|processRetry|restart|missingNative)$',
        '--max-samples=1', '--backend=rust', '--seed=0xC5F1', '--verbosity=1',
        '--out-itf=' + str(directory / '{test}.json'),
    ], cwd=directory, check=True)


KEYS = ['stage', 'operationId', 'operationStatus', 'paused', 'readbackCurrent',
        'claimCurrent', 'candidateDurable', 'branchPublished', 'pullRequestObserved',
        'mergeObserved', 'nativeReadbackObserved']


def compact(states):
    result = []
    for state in states:
        if not result or state != result[-1]:
            result.append(state)
    return result


def legacy(directory, name):
    trace = json.loads((directory / (name + '.json')).read_text())
    require(trace['vars'] == ['state'], 'legacy variable schema changed')
    # Choreo starts admitted. Legacy init precedes its separate manualStart.
    states = trace['states'][1:]
    return compact([
        {key: int(row['state'][key]['#bigint']) if key == 'stage'
         else row['state'][key] for key in KEYS} for row in states
    ])


def choreo(manifest, name):
    entry = next(row for row in manifest['scenarios'] if row['id'] == name)
    raw = (FIXTURES / entry['file']).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == entry['traceSha256'], name + ' digest differs')
    trace = json.loads(raw)
    variable = 'O2HostedWriterChoreoModel::choreo::s'
    require(trace['vars'] == [variable], 'Choreo variable schema changed')
    result = []
    for milestone in entry['milestones']:
        state = trace['states'][milestone['stateIndex']][variable]
        local = {key: value['local']['value'] for key, value in state['system']['#map']}
        host, journal = local['Host'], local['Journal']
        completed = {effect['tag'] for effect in host['completed']['#set']}
        status = {'JournalEmpty': 'none', 'Intent': 'intent', 'Dispatching': 'dispatching',
                  'Unknown': 'unknown', 'ProvenAbsent': 'absent', 'Applied': 'none'}[
                      journal['status']['tag']]
        result.append(dict(
            stage=len(completed),
            operationId='' if status == 'none' else journal['current']['value']['operation'],
            operationStatus=status, paused=host['paused'], readbackCurrent=host['authorityFresh'],
            claimCurrent='Claim' in completed, candidateDurable='Candidate' in completed,
            branchPublished='Branch' in completed, pullRequestObserved='PullRequest' in completed,
            mergeObserved='Merge' in completed, nativeReadbackObserved='NativeReadback' in completed,
        ))
    return compact(result)


def compare(name, old, new):
    require(len(old) == len(new), f'{name}: milestone counts {len(old)} != {len(new)}')
    for index, (left, right) in enumerate(zip(old, new)):
        require(left == right, f'{name}: first divergence at milestone {index}: {left} != {right}')
    print(f'CHOREO_PARITY_OK scenario={name} milestones={len(old)}')


def relative_retry(states, operation, initial_stage):
    require(all(state['operationId'] in ('', operation) for state in states),
            'retry changed operation identity')
    return [{key: state[key] - initial_stage if key == 'stage' else state[key]
             for key in ('stage', 'operationStatus', 'paused', 'readbackCurrent')}
            for state in states]


def main():
    manifest = json.loads((FIXTURES / 'manifest.json').read_text())
    source = ROOT / manifest['source']['path']
    require(hashlib.sha256(source.read_bytes()).hexdigest() == manifest['source']['sha256'],
            'protocol source identity differs')
    with tempfile.TemporaryDirectory(prefix='fsgg-choreo-parity-') as temporary:
        directory = Path(temporary)
        export_legacy(directory)
        for old, new in [('happy', 'happy-path'), ('lostApplied', 'lost-applied'),
                         ('restart', 'restart-gates'), ('missingNative', 'missing-native-readback')]:
            compare(old, legacy(directory, old), choreo(manifest, new))
        retry = choreo(manifest, 'proven-absent-retry')
        compare('claim-absence', legacy(directory, 'claimAbsent'), retry[:5])
        # Legacy retry requires a pre-existing claim. Choreo also permits retry of
        # the claim itself. Compare the admitted same-operation retry protocol
        # relative to the effect, explicitly retaining this scope difference.
        compare('relative-same-operation-retry',
                relative_retry(legacy(directory, 'processRetry')[3:], 'op-process', 1),
                relative_retry(retry, 'op-claim', 0))


if __name__ == '__main__':
    main()
