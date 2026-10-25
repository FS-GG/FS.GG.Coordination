#!/usr/bin/env python3
"""Bounded source controls only; no bake, registration, dispatch or native qualification.

The public producer qualifies the real maintained bundle and inert driver controls.
Its byte-identical private mirror records the exact first-install candidate. The canonical
delivery reader independently authenticates protected source, native run/job/check/App15368,
artifact digest, required checks and event/checkout Git ancestry; this receipt grants none
of that authority. Missing real bundle/source is a refusal, never placeholder qualification.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import resource
import stat
import subprocess
import sys
import unittest

PUBLIC = 'FS-GG/FS.GG.Coordination'
PRIVATE = 'FS-GG/FS.GG.Native.Qualification'
REPOSITORIES = {'public': (PUBLIC, 1346720714), 'private': (PRIVATE, 1432517271)}
PREFIX = 'deployment/qualification-runner/native-qualification/'
VERIFIER = 'eng/current-public-at/verify-current-public-at-source.py'
SOURCE_WORKFLOW = '.github/workflows/current-public-at-source-controls.yml'
HELD_WORKFLOW = '.github/workflows/current-public-reference-at-qualification.yml'
PAYLOAD = {SOURCE_WORKFLOW: PREFIX+'current-public-at-source-controls.yml',
           HELD_WORKFLOW: PREFIX+'current-public-reference-at-qualification.yml',
           'tests/current-public-at/verify-source.py': PREFIX+'verify-current-public-at-source.py'}
PRODUCER = ('.fsgg/routine-development.json', '.github/workflows/routine-eligibility.yml',
            'scripts/check-claim-generation.py', 'scripts/check-routine-eligibility-envelope.py', 'scripts/lib/gate.py')
RETAINED = ('.github/workflows/bar-hosted-image-qualification.yml', 'README.md')
LINT = '.github/actionlint.yaml'
LINT_BASE = (b'# Declares the one self-hosted label of this private caller repository so actionlint accepts it.\n'
             b'self-hosted-runner:\n  labels:\n    - fsgg-native-linux-x64-v1\n')
LINT_DERIVED = (b'# Declares the exact BAR and AT self-hosted labels of this private caller repository so actionlint accepts them.\n'
                b'self-hosted-runner:\n  labels:\n    - fsgg-native-linux-x64-v1\n    - fsgg-native-at-x11-v1\n')
CHECKOUT = 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'
UPLOAD = 'actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'
DATA_CONTROLS = (
    'test_at_v2_software_is_closed_exact_and_never_an_arbitrary_command_profile',
    'test_at_v2_target_has_separate_floor_and_does_not_lower_legacy_target',
    'test_at_v2_seals_distinct_private_job_label_and_software_without_bar_crosstalk',
)
MAX_FILE = 131072


def private_workflow():
    """One source-owned receiver workflow; no caller-supplied rendering inputs."""
    return b'''name: Current public AT source controls

on:
  pull_request:

permissions:
  contents: read

concurrency:
  group: native-at-source-${{ github.ref }}
  cancel-in-progress: true

jobs:
  source-controls:
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    env:
      PYTHONDONTWRITEBYTECODE: '1'
    steps:
      - name: Check out exact candidate source
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          fetch-depth: 0
          persist-credentials: false
      - name: Validate AT source and held native route
        shell: bash
        run: |
          set -euo pipefail
          python3 -I -B tests/current-public-at/verify-source.py private
      - name: Retain source qualification
        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a
        with:
          name: native-at-source-${{ github.event.pull_request.head.sha }}-${{ github.run_id }}-${{ github.run_attempt }}
          path: ${{ runner.temp }}/native-at-source/source-qualification.json
          if-no-files-found: error
          retention-days: 7
          compression-level: 0
'''


def require(value, message):
    if not value:
        raise ValueError('native-at-source: '+message)


def sha(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{40}', value) is not None


def unique(rows):
    value = {}
    for key, item in rows:
        require(key not in value, 'duplicate JSON property')
        value[key] = item
    return value


def read(path, limit=MAX_FILE):
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as handle:
        info = os.fstat(handle.fileno())
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= limit, 'bounded regular file required: '+str(path))
        body = handle.read(limit+1)
    require(len(body) <= limit, 'file read exceeded bound')
    return body


def digest(body):
    return hashlib.sha256(body).hexdigest()


def git(root, *args, limit=MAX_FILE):
    # No fetching, hooks, credential configuration or native workload execution.
    command = ['git', '--no-replace-objects', '-c', 'core.hooksPath=/dev/null',
               '-c', 'core.fsmonitor=false', '-c', 'core.untrackedCache=false', *args]
    result = subprocess.run(command, cwd=root, capture_output=True, timeout=15, check=False)
    require(result.returncode == 0 and len(result.stdout) <= limit and len(result.stderr) <= MAX_FILE,
            'bounded local Git metadata unavailable: '+args[0])
    return result.stdout


def blob(root, revision, path):
    require(sha(revision), 'exact Git revision required')
    return git(root, 'cat-file', 'blob', revision+':'+path)


def tree(root, revision):
    require(sha(revision), 'exact Git tree revision required')
    rows = {}
    for raw in git(root, 'ls-tree', '-rz', '--full-tree', revision, limit=262144).split(b'\0'):
        if not raw:
            continue
        metadata, path = raw.decode('utf-8').split('\t', 1)
        mode, kind, identity = metadata.split(' ')
        expected_mode = '100755' if path == 'scripts/check-claim-generation.py' else '100644'
        require(path not in rows and kind == 'blob' and mode == expected_mode and sha(identity),
                'receiver tree must contain unique regular source files')
        rows[path] = identity
    return rows


def structural_lines(body):
    """Closed authored YAML shape, not a general YAML parser or native admission."""
    text = body.decode('utf-8')
    require('\t' not in text and '\r' not in text, 'workflow must use LF and spaces')
    result = []
    block = None
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        depth = len(line)-len(line.lstrip())
        if block is not None and depth > block:
            continue
        block = None
        require(not re.search(r'(^|\s)[&*!][A-Za-z_]|<<:', line), 'workflow aliases/tags/merges unsupported')
        result.append(line)
        if re.search(r':\s*[|>][-+]?\s*$', line):
            block = depth
    return result


def workflow_controls(body, held):
    lines = structural_lines(body)
    roots = [line for line in lines if not line.startswith(' ')]
    keys = [line.split(':',1)[0] for line in roots]
    require(len(keys) == len(set(keys)) and set(keys) <= {'name','on','permissions','concurrency','jobs'}
            and {'name','on','permissions','jobs'} <= set(keys), 'closed workflow root fields required')
    require('on:' in roots and 'jobs:' in roots and 'permissions:' in roots, 'workflow sections must be mappings')
    def section(name):
        start = lines.index(name+':')+1
        end = next((i for i in range(start,len(lines)) if not lines[i].startswith(' ')),len(lines))
        return lines[start:end]
    events = [line.strip().split(':',1)[0] for line in section('on') if line.startswith('  ') and not line.startswith('   ')]
    require(events == ['workflow_dispatch'] if held else events == ['pull_request'], 'unexpected native/source trigger')
    jobs = section('jobs')
    names = [line.strip() for line in jobs if line.startswith('  ') and not line.startswith('   ')]
    require(names == ['current-public-at:'] if held else names == ['source-controls:'], 'unexpected job population')
    runners = [line for line in jobs if line.startswith('    runs-on:')]
    require(runners == (['    runs-on: [self-hosted, linux, x64, fsgg-native-at-x11-v1]'] if held
                        else ['    runs-on: ubuntu-24.04']), 'wrong native/hosted runner')
    uses = [line.split('uses:',1)[1].strip() for line in jobs if re.match(r'\s+(?:- )?uses:',line)]
    require(all(re.fullmatch(r'actions/[a-z0-9-]+@[0-9a-f]{40}', use) for use in uses), 'only SHA-pinned GitHub-owned actions allowed')
    if not held:
        require(body == private_workflow(), 'private source workflow differs from closed source-owned bytes')
        require(uses == [CHECKOUT,UPLOAD], 'source workflow action identity differs')
        names = [line.split('- name:',1)[1].strip() for line in jobs if line.startswith('      - name:')]
        require(names == ['Check out exact candidate source','Validate AT source and held native route','Retain source qualification'],
                'source workflow step population/order differs')
        require('          ref: ${{ github.event.pull_request.head.sha }}' in jobs
                and '          fetch-depth: 0' in jobs and '          persist-credentials: false' in jobs,
                'source checkout must bind the exact PR head without persisted credentials')
        require('python3 -I -B tests/current-public-at/verify-source.py private' in body.decode(), 'private verifier invocation missing')
        require('native-at-source-${{ github.event.pull_request.head.sha }}-${{ github.run_id }}-${{ github.run_attempt }}' in body.decode(),
                'private source artifact name differs')
        require('if-no-files-found: error' in body.decode(), 'missing receipt must fail')
        require(section('permissions') == ['  contents: read'], 'source token must be contents-read only')
    else:
        require(any('github.ref' in line and 'refs/heads/main' in line for line in jobs)
                and any('github.run_attempt' in line for line in jobs), 'held route must retain main/attempt guards')
    return True


def private_delta(base, candidate, bodies, base_lint):
    require(set(base) == set(RETAINED) | {LINT}, 'fresh receiver base must contain only the BAR three')
    require(set(candidate) == set(base) | set(PRODUCER) | set(PAYLOAD), 'receiver leaf population differs')
    require(all(candidate[path] == base[path] for path in RETAINED), 'retained BAR source changed')
    require(base_lint == LINT_BASE and bodies[LINT] == LINT_DERIVED, 'actionlint closed derivation differs')
    require(set(bodies) == set(PRODUCER) | set(PAYLOAD) | {LINT}, 'nine source bodies required')
    workflow_controls(bodies[HELD_WORKFLOW], True)
    workflow_controls(bodies[SOURCE_WORKFLOW], False)
    return {path:digest(body) for path,body in bodies.items()}


def context(mode, environment, event):
    repository, identity = REPOSITORIES[mode]
    require(environment.get('GITHUB_REPOSITORY') == repository
            and environment.get('GITHUB_REPOSITORY_ID') == str(identity), 'wrong native repository identity')
    require(event.get('repository',{}).get('full_name') == repository
            and type(event['repository'].get('id')) is int and event['repository']['id'] == identity, 'event repository differs')
    event_name = environment.get('GITHUB_EVENT_NAME')
    require(event_name in (('pull_request','push') if mode == 'public' else ('pull_request',)), 'wrong source event')
    event_sha = environment.get('GITHUB_SHA')
    require(sha(event_sha), 'event SHA unavailable')
    if event_name == 'pull_request':
        pr = event.get('pull_request',{})
        for side in ('base','head'):
            value = pr.get(side,{})
            require(sha(value.get('sha')) and value.get('repo',{}).get('full_name') == repository
                    and type(value['repo'].get('id')) is int and value['repo']['id'] == identity, 'fork/malformed PR source refused')
        require(pr['base'].get('ref') == 'main', 'source PR base must be main')
        candidate, base = pr['head']['sha'], pr['base']['sha']
    else:
        require(environment.get('GITHUB_REF') == 'refs/heads/main', 'public push must be main')
        candidate, base = event_sha, event.get('before')
        require(sha(base), 'push base unavailable')
    run, attempt = environment.get('GITHUB_RUN_ID',''), environment.get('GITHUB_RUN_ATTEMPT','')
    require(re.fullmatch('[1-9][0-9]*',run) and attempt == '1', 'exact native run/attempt required')
    return dict(repository=repository,candidateSha=candidate,baseSha=base,testedCheckoutSha=candidate,
                eventSha=event_sha,runId=int(run),runAttempt=1)


def driver_controls(root):
    # Invoke only actual pure AT data controls. Other classes include GPG and guest-shell controls.
    spec = importlib.util.spec_from_file_location('at_driver_controls',root/'tests/qualification-runner/test_dedicated_runner.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    suite = unittest.TestSuite(module.DedicatedRunnerDataControls(name) for name in DATA_CONTROLS)
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    require(result.wasSuccessful() and result.testsRun == len(DATA_CONTROLS) and not result.skipped,
            'actual AT driver controls failed/skipped')


def public_bundle(root):
    bodies = {path:read(root/path) for path in PAYLOAD.values()}
    require(bodies[PAYLOAD['tests/current-public-at/verify-source.py']] == read(root/VERIFIER), 'private verifier mirror differs')
    workflow_controls(bodies[PAYLOAD[SOURCE_WORKFLOW]],False)
    workflow_controls(bodies[PAYLOAD[HELD_WORKFLOW]],True)
    # BAR source/template stays outside the AT derivation and is not rewritten here.
    require(read(root/(PREFIX+'actionlint.yaml')) == LINT_BASE, 'BAR template baseline changed')
    driver_controls(root)
    for path in (SOURCE_WORKFLOW,VERIFIER,'eng/dedicated_qualification_runner.py',
                 'tests/qualification-runner/test_dedicated_runner.py',PREFIX+'actionlint.yaml'):
        bodies[path]=read(root/path)
    return {path:digest(body) for path,body in bodies.items()}


def produce(mode, root, environment):
    event = json.loads(read(Path(environment['GITHUB_EVENT_PATH']),1048576),object_pairs_hook=unique)
    facts = context(mode,environment,event)
    require(git(root,'rev-parse','HEAD').decode().strip() == facts['candidateSha'], 'tested checkout is not exact candidate')
    require(git(root,'status','--porcelain=v1','--untracked-files=all') == b'', 'source checkout is dirty')
    if mode == 'public':
        files = public_bundle(root)
        receipt = dict(schema='fsgg.native-at.public-source-preparation/1',**facts,files=files,
                       controls=list(DATA_CONTROLS),nativeQualification='not-selected')
    else:
        base, candidate = tree(root,facts['baseSha']), tree(root,facts['candidateSha'])
        bodies = {path:read(root/path) for path in (*PRODUCER,*PAYLOAD,LINT)}
        files = private_delta(base,candidate,bodies,blob(root,facts['baseSha'],LINT))
        receipt = dict(schema='fsgg.native-at.source-qualification/1',**facts,files=files,
                       retainedBarFiles={path:base[path] for path in RETAINED},
                       derivedActionlint=dict(baseBlobSha=base[LINT],candidateSha256=files[LINT]))
    require(git(root,'status','--porcelain=v1','--untracked-files=all') == b'', 'source changed during controls')
    return receipt


def source_selftests():
    class Controls(unittest.TestCase):
        def test_lint_is_exact_not_an_allowlist(self):
            self.assertEqual(LINT_DERIVED.splitlines()[-2:],[b'    - fsgg-native-linux-x64-v1',b'    - fsgg-native-at-x11-v1'])
            self.assertNotEqual(LINT_BASE,LINT_DERIVED)

        def test_context_refuses_forks_attempts_and_cross_repo(self):
            env=dict(GITHUB_REPOSITORY=PRIVATE,GITHUB_REPOSITORY_ID='1432517271',GITHUB_EVENT_NAME='pull_request',
                     GITHUB_SHA='c'*40,GITHUB_RUN_ID='7',GITHUB_RUN_ATTEMPT='1')
            repo=dict(full_name=PRIVATE,id=1432517271)
            event=dict(repository=repo,pull_request=dict(base=dict(sha='b'*40,ref='main',repo=repo),head=dict(sha='a'*40,repo=repo)))
            self.assertEqual(context('private',env,event)['testedCheckoutSha'],'a'*40)
            for key,value in [('GITHUB_RUN_ATTEMPT','2'),('GITHUB_REPOSITORY',PUBLIC),('GITHUB_EVENT_NAME','push'),('GITHUB_SHA','bad')]:
                with self.subTest(key=key),self.assertRaises(ValueError):context('private',{**env,key:value},event)
            event['pull_request']['head']['repo']=dict(full_name='other/fork',id=1)
            with self.assertRaises(ValueError):context('private',env,event)

        def test_held_route_rejects_wrong_trigger_runner_and_reusable_workflow(self):
            body=(b'name: held\non:\n  workflow_dispatch:\npermissions:\n  contents: read\njobs:\n  current-public-at:\n'
                  b'    if: ${{ github.ref == \'refs/heads/main\' && github.run_attempt == 1 }}\n'
                  b'    runs-on: [self-hosted, linux, x64, fsgg-native-at-x11-v1]\n    steps:\n'
                  b'      - uses: '+CHECKOUT.encode()+b'\n')
            self.assertTrue(workflow_controls(body,True))
            for before,after in [(b'workflow_dispatch',b'pull_request'),(b'fsgg-native-at-x11-v1',b'fsgg-native-linux-x64-v1'),
                                 (CHECKOUT.encode(),b'FS-GG/reusable@'+b'a'*40),(b'jobs:',b'jobs: &alias')]:
                with self.subTest(before=before),self.assertRaises(ValueError):workflow_controls(body.replace(before,after),True)

        def test_source_workflow_rejects_added_commands_even_with_valid_step_names(self):
            self.assertTrue(workflow_controls(private_workflow(),False))
            for before,after in [(b'    steps:',b'    steps:\n      - run: unselected-native-effect'),
                                 (b'  contents: read',b'  contents: write'),
                                 (CHECKOUT.encode(),b'actions/checkout@main'),
                                 (b'verify-source.py private',b'verify-source.py public')]:
                with self.subTest(before=before),self.assertRaises(ValueError):workflow_controls(private_workflow().replace(before,after),False)

        def test_actual_private_delta_refuses_extra_missing_and_changed_bar(self):
            held=(b'name: held\non:\n  workflow_dispatch:\npermissions:\n  contents: read\njobs:\n  current-public-at:\n'
                  b'    if: ${{ github.ref == \'refs/heads/main\' && github.run_attempt == 1 }}\n'
                  b'    runs-on: [self-hosted, linux, x64, fsgg-native-at-x11-v1]\n    steps:\n'
                  b'      - uses: '+CHECKOUT.encode()+b'\n')
            base={path:'b'*40 for path in (*RETAINED,LINT)}
            candidate={**base,**{path:'a'*40 for path in (*PRODUCER,*PAYLOAD,LINT)}}
            bodies={path:b'inert fixture' for path in (*PRODUCER,*PAYLOAD,LINT)}
            bodies.update({SOURCE_WORKFLOW:private_workflow(),HELD_WORKFLOW:held,LINT:LINT_DERIVED})
            self.assertEqual(len(private_delta(base,candidate,bodies,LINT_BASE)),9)
            for changed in ({**candidate,'extra':'a'*40},{**candidate,'README.md':'c'*40},
                            {k:v for k,v in candidate.items() if k!=HELD_WORKFLOW}):
                with self.assertRaises(ValueError):private_delta(base,changed,bodies,LINT_BASE)
            for bad in (LINT_BASE,LINT_DERIVED+b'    - extra-label\n',LINT_DERIVED.replace(b'fsgg-native-linux-x64-v1',b'wrong')):
                with self.assertRaises(ValueError):private_delta(base,candidate,{**bodies,LINT:bad},LINT_BASE)

    result=unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(Controls))
    require(result.wasSuccessful(),'source self-tests failed')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode',choices=('public','private','self-test'))
    args=parser.parse_args()
    os.sched_setaffinity(0,{min(os.sched_getaffinity(0))})
    resource.setrlimit(resource.RLIMIT_AS,(2*1024**3,2*1024**3))
    resource.setrlimit(resource.RLIMIT_CPU,(180,180))
    # Git metadata is the sole executable dependency. Pure controls cannot start helpers or connect.
    def inert(event, values):
        if event == 'subprocess.Popen':
            command=values[1]
            require(isinstance(command,list) and command[:8]==['git','--no-replace-objects','-c','core.hooksPath=/dev/null',
                    '-c','core.fsmonitor=false','-c','core.untrackedCache=false']
                    and len(command)>8 and command[8] in ('rev-parse','status','cat-file','ls-tree'), 'unexpected source-control subprocess')
        if event in ('socket.connect','os.system','os.exec','os.posix_spawn'):
            raise ValueError('native-at-source: source controls refuse native/network effects')
    sys.addaudithook(inert)
    source_selftests()
    if args.mode == 'self-test':
        return 0
    root=Path(os.environ['GITHUB_WORKSPACE']).resolve(strict=True)
    receipt=produce(args.mode,root,os.environ)
    raw=(json.dumps(receipt,sort_keys=True,separators=(',',':'))+'\n').encode()
    require(len(raw)<=65536,'source receipt exceeds64KiB')
    directory=Path(os.environ['RUNNER_TEMP'])/('native-at-public-source' if args.mode=='public' else 'native-at-source')
    directory.mkdir(mode=0o700,exist_ok=False)
    target=directory/('source-preparation.json' if args.mode=='public' else 'source-qualification.json')
    with target.open('xb') as handle:handle.write(raw)
    print('source controls passed; native qualification not selected')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError,OSError,KeyError,TypeError,subprocess.TimeoutExpired) as error:
        print(str(error),file=sys.stderr)
        raise SystemExit(1)
