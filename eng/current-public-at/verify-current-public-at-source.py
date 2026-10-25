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
from unittest import mock

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
RETAINED = ('README.md',)
BAR = '.github/workflows/bar-hosted-image-qualification.yml'
PUBLIC_BAR = PREFIX+'bar-hosted-image-qualification.yml'
BAR_GUARD = (
    b'          cmp -- "$template/bar-hosted-image-qualification.yml" caller/.github/workflows/bar-hosted-image-qualification.yml\n'
    b'          cmp -- "$template/README.md" caller/README.md\n'
    b'          cmp -- "$template/actionlint.yaml" caller/.github/actionlint.yaml\n'
    b'          test "$(git -C caller ls-files | LC_ALL=C sort | tr \'\\n\' \' \')" = \'.github/actionlint.yaml .github/workflows/bar-hosted-image-qualification.yml README.md \'\n')
# Actual protected .github Git blobs at this revision, not a native-installation claim.
# Freeze must still compare these five files to the actual selected protected producer.
PRODUCER_SOURCE = '0e8f845392baa5b1df4e28a436adfd3dbc338b04'
PRODUCER_SHA256 = dict(zip(PRODUCER, (
    '631b83bd0b2204016679497917411dc90fa53e4e88c625b043942c0a82c0070d',
    'c78d4513d755270d5b0d5fb6d8c5d873d372c0d8e19fba08b622ac6bb7129899',
    '7995b5331f2d9564313f6d317f733f53174781aeb3bd50ecfa28755c036553c2',
    'f311a6300f48cf0bca763c2e4092c3f026c13223c9868a9fd1afb611fb94ffaa',
    '9511b7f62ea101a397fd375d8653fc0b4fcb9a07ddb583e874d3cd60cc886d6c'), strict=True))
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
OPERATOR = 'eng/current-public-at/operate-current-public-at.py'
OPERATOR_TESTS = 'tests/qualification-runner/test_current_public_at_operation.py'
OPERATOR_CONTROLS = (
    'test_actual_reader_joins_source_run_job_metadata_and_original_archive_body',
    'test_wrong_original_scope_metadata_and_body_stop_without_native_acceptance',
    'test_response_status_is_checked_before_read_and_body_bound_before_digest',
    'test_public_metadata_never_sends_private_token_or_follows_redirect',
    'test_fixed_attachment_requires_real_root_file_mount_and_body_contract',
    'test_original_zip_requires_self_contained_exact_needed_inputs_without_reconstruction',
    'test_duplicate_json_and_expired_original_clock_refuse',
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


def derive_bar(body):
    require(body.count(BAR_GUARD) == 1, 'BAR baseline guard must occur exactly once')
    return body.replace(BAR_GUARD,
        b'          python3 -I -B source/eng/current-public-at/verify-current-public-at-source.py bar-runtime --caller caller --source source\n')


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


def private_delta(base, candidate, bodies, base_lint, base_bar):
    require(set(base) == set(RETAINED) | {LINT,BAR}, 'fresh receiver base must contain only the BAR three')
    require(set(candidate) == set(base) | set(PRODUCER) | set(PAYLOAD), 'receiver leaf population differs')
    require(all(candidate[path] == base[path] for path in RETAINED), 'retained BAR source changed')
    require(base_lint == LINT_BASE and bodies[LINT] == LINT_DERIVED, 'actionlint closed derivation differs')
    require(bodies[BAR] == derive_bar(base_bar), 'BAR closed guard derivation differs')
    require(set(bodies) == set(PRODUCER) | set(PAYLOAD) | {LINT,BAR}, 'ten source bodies required')
    require(all(digest(bodies[path]) == PRODUCER_SHA256[path] for path in PRODUCER), 'protected canonical producer bytes differ')
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


def pure_controls(root, path, class_name, names):
    # The fixed selected tests may use temporary files and fake HTTP/ZIP data, never helpers/network.
    active = True
    def deny_effect(event, values):
        if active and event in ('subprocess.Popen','socket.connect','os.system','os.exec','os.posix_spawn','os.fork'):
            raise ValueError('native-at-source: pure controls refuse process/network effects')
    sys.addaudithook(deny_effect)
    try:
        spec = importlib.util.spec_from_file_location('at_source_controls',root/path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        suite = unittest.TestSuite(getattr(module,class_name)(name) for name in names)
        result = unittest.TextTestRunner(verbosity=1).run(suite)
        require(result.wasSuccessful() and result.testsRun == len(names) and not result.skipped,
                'actual AT controls failed/skipped: '+path)
    finally:
        active = False


def driver_controls(root):
    # Other driver tests include inert Git, GPG and guest-shell cases; the existing workflow owns them.
    pure_controls(root,'tests/qualification-runner/test_dedicated_runner.py','DedicatedRunnerDataControls',DATA_CONTROLS)


def operator_controls(root):
    pure_controls(root,OPERATOR_TESTS,'Controls',OPERATOR_CONTROLS)


def bundle_sources(root):
    bodies = {path:read(root/path) for path in PAYLOAD.values()}
    require(bodies[PAYLOAD['tests/current-public-at/verify-source.py']] == read(root/VERIFIER), 'private verifier mirror differs')
    workflow_controls(bodies[PAYLOAD[SOURCE_WORKFLOW]],False)
    workflow_controls(bodies[PAYLOAD[HELD_WORKFLOW]],True)
    # BAR source/template stays outside the AT derivation and is not rewritten here.
    require(read(root/(PREFIX+'actionlint.yaml')) == LINT_BASE, 'BAR template baseline changed')
    derive_bar(read(root/PUBLIC_BAR))
    return bodies


def public_bundle(root):
    bodies=bundle_sources(root)
    driver_controls(root)
    operator_controls(root)
    for path in (SOURCE_WORKFLOW,VERIFIER,'eng/dedicated_qualification_runner.py',
                 'tests/qualification-runner/test_dedicated_runner.py',PREFIX+'actionlint.yaml',PUBLIC_BAR,PREFIX+'README.md',
                 OPERATOR,OPERATOR_TESTS):
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
                       controls=list(DATA_CONTROLS+OPERATOR_CONTROLS),nativeQualification='not-selected')
    else:
        base, candidate = tree(root,facts['baseSha']), tree(root,facts['candidateSha'])
        bodies = {path:read(root/path) for path in (*PRODUCER,*PAYLOAD,LINT,BAR)}
        files = private_delta(base,candidate,bodies,blob(root,facts['baseSha'],LINT),blob(root,facts['baseSha'],BAR))
        receipt = dict(schema='fsgg.native-at.source-qualification/1',**facts,files=files,
                       retainedBarFiles={path:base[path] for path in RETAINED},
                       derivedActionlint=dict(baseBlobSha=base[LINT],candidateSha256=files[LINT]),
                       derivedBARguard=dict(baseBlobSha=base[BAR],candidateSha256=files[BAR]))
    require(git(root,'status','--porcelain=v1','--untracked-files=all') == b'', 'source changed during controls')
    return receipt


def bar_runtime(caller, source, environment):
    """Source correspondence only; all existing BAR native/upstream/run guards still apply."""
    require(environment.get('GITHUB_REPOSITORY') == PRIVATE
            and environment.get('GITHUB_REPOSITORY_ID') == '1432517271', 'BAR caller repository differs')
    head, revision = environment.get('GITHUB_SHA'), environment.get('FSGG_SOURCE_REVISION')
    require(sha(head) and sha(revision), 'BAR source/caller identities unavailable')
    require(Path(__file__).resolve() == source/VERIFIER, 'BAR verifier must execute from selected public source')
    for root, expected in ((caller,head),(source,revision)):
        require(git(root,'rev-parse','HEAD').decode().strip() == expected, 'BAR checkout identity differs')
        require(git(root,'status','--porcelain=v1','--untracked-files=all','--ignored') == b'', 'BAR checkout has extra/changed source')
    git(source,'merge-base','--is-ancestor',revision,'refs/remotes/origin/main')
    bundle=bundle_sources(source)
    rows=tree(caller,head)
    require(set(rows)==set(PRODUCER)|set(PAYLOAD)|{LINT,BAR,'README.md'}, 'BAR complete caller population differs')
    expected={path:bundle[public] for path,public in PAYLOAD.items()}
    expected.update({LINT:LINT_DERIVED,BAR:derive_bar(read(source/PUBLIC_BAR)),
                     'README.md':read(source/(PREFIX+'README.md'))})
    for path,body in expected.items():
        require(read(caller/path)==body, 'BAR protected caller bytes differ: '+path)
    for path in PRODUCER:
        require(digest(read(caller/path))==PRODUCER_SHA256[path], 'BAR canonical producer bytes differ: '+path)
    for root in (caller,source):
        require(git(root,'status','--porcelain=v1','--untracked-files=all','--ignored') == b'', 'BAR source changed during collection')


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
            base={path:'b'*40 for path in (*RETAINED,LINT,BAR)}
            candidate={**base,**{path:'a'*40 for path in (*PRODUCER,*PAYLOAD,LINT,BAR)}}
            bodies={path:b'inert fixture' for path in (*PRODUCER,*PAYLOAD,LINT,BAR)}
            base_bar=b'old upstream guards\n'+BAR_GUARD+b'old native operation guards\n'
            bodies.update({SOURCE_WORKFLOW:private_workflow(),HELD_WORKFLOW:held,LINT:LINT_DERIVED,BAR:derive_bar(base_bar)})
            # Only this constructed control substitutes source bytes; actual producer pins are restored.
            with mock.patch.dict(PRODUCER_SHA256,{path:digest(b'inert fixture') for path in PRODUCER},clear=True):
                self.assertEqual(len(private_delta(base,candidate,bodies,LINT_BASE,base_bar)),10)
                for changed in ({**candidate,'extra':'a'*40},{**candidate,'README.md':'c'*40},
                                {k:v for k,v in candidate.items() if k!=HELD_WORKFLOW}):
                    with self.assertRaises(ValueError):private_delta(base,changed,bodies,LINT_BASE,base_bar)
                for path,bad in ((LINT,LINT_BASE),(LINT,LINT_DERIVED+b'    - extra-label\n'),
                                 (LINT,LINT_DERIVED.replace(b'fsgg-native-linux-x64-v1',b'wrong')),
                                 (BAR,bodies[BAR].replace(b'old upstream guards',b'weakened upstream guards')),
                                 (PRODUCER[0],b'changed protected producer')):
                    with self.assertRaises(ValueError):private_delta(base,candidate,{**bodies,path:bad},LINT_BASE,base_bar)

        def test_bar_guard_transform_preserves_all_surrounding_bytes(self):
            before=b'upstream source/run checks\n'+BAR_GUARD+b'unchanged native checks\n'
            expected=(b'upstream source/run checks\n'
                      b'          python3 -I -B source/eng/current-public-at/verify-current-public-at-source.py bar-runtime --caller caller --source source\n'
                      b'unchanged native checks\n')
            self.assertEqual(derive_bar(before),expected)
            for body in (b'no guard',before+BAR_GUARD,BAR_GUARD.replace(b'cmp --',b'cmp ')):
                with self.assertRaises(ValueError):derive_bar(body)

    result=unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(Controls))
    require(result.wasSuccessful(),'source self-tests failed')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode',choices=('public','private','self-test','bar-runtime'))
    parser.add_argument('--caller',type=Path)
    parser.add_argument('--source',type=Path)
    args=parser.parse_args()
    require((args.mode=='bar-runtime' and args.caller is not None and args.source is not None)
            or (args.mode!='bar-runtime' and args.caller is None and args.source is None), 'caller/source paths are only for fixed BAR source guard')
    os.sched_setaffinity(0,{min(os.sched_getaffinity(0))})
    resource.setrlimit(resource.RLIMIT_AS,(2*1024**3,2*1024**3))
    resource.setrlimit(resource.RLIMIT_CPU,(180,180))
    # Git metadata is the sole executable dependency. Pure controls cannot start helpers or connect.
    def inert(event, values):
        if event == 'subprocess.Popen':
            command=values[1]
            require(isinstance(command,list) and command[:8]==['git','--no-replace-objects','-c','core.hooksPath=/dev/null',
                    '-c','core.fsmonitor=false','-c','core.untrackedCache=false']
                    and len(command)>8 and command[8] in ('rev-parse','status','cat-file','ls-tree','merge-base'), 'unexpected source-control subprocess')
        if event in ('socket.connect','os.system','os.exec','os.posix_spawn'):
            raise ValueError('native-at-source: source controls refuse native/network effects')
    sys.addaudithook(inert)
    source_selftests()
    if args.mode == 'self-test':
        return 0
    if args.mode == 'bar-runtime':
        bar_runtime(args.caller.resolve(strict=True),args.source.resolve(strict=True),os.environ)
        print('BAR caller source correspondence passed; native/upstream/run gates remain separate')
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
