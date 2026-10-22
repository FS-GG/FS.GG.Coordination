#!/usr/bin/env python3
"""Inert good/fault/causal controls for the dormant forward Execution pair route.

No credential, network mutation, workflow dispatch, tag, package or release is used.
The exact pinned shared journal engine runs unchanged against an in-memory GitHub
and feed double; every guard has a paired causal control showing the harm that
appears when that single guard is neutralized.
"""
from __future__ import annotations

import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import sys
import tempfile
import zipfile
from unittest.mock import patch

if sys.flags.optimize != 0:
    raise SystemExit('forward controls require Python optimize=0')

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('execution_pair_forward', ROOT / 'eng/execution_pair_forward.py')
F = importlib.util.module_from_spec(spec)
sys.modules['execution_pair_forward'] = F
spec.loader.exec_module(F)
LEGACY = runpy.run_path(str(ROOT / 'tests/portable-workspace/release/test_execution_pair_release.py'))
IDS = F.IDS
COUNTS = {}

SOURCE, TREE, PUBLISHER, RUN, OPERATOR = '7' * 40, 'b' * 40, '9' * 40, 4242, 'EHotwagner'
POLICY_HASH = '5182bb35a6dd682cef84ac9ce4210cfd09f38b7410d8c9cf9bae42866bcb81da'
ORIGINAL_ADMISSION_SHA256 = 'f0859d88276effeaf3a3b038b79b448809557cb7b8c6e32d509b2bd791e20826'
ORIGINAL_PUBLISHER_SHA256 = 'ac736727db6dbf7a4d4c16d8daea9e9e684a2bca32c970389b4df80324a44908'


def count(name):
    COUNTS[name] = COUNTS.get(name, 0) + 1


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def refused(fn, *args, exceptions=None, **kwargs):
    """Refusals come from this module and from the three shared engine modules, each with its own class."""
    if exceptions is None:
        e = engine()
        exceptions = (F.Refused, e.execution.Refused, e.journal.Refused, e.provider.Refused)
    try:
        with contextlib.redirect_stderr(io.StringIO()):
            fn(*args, **kwargs)
    except exceptions:
        return True
    return False


def must_refuse(label, fn, *args, **kwargs):
    count('refusals')
    assert refused(fn, *args, **kwargs), f'{label}: fault was accepted'


@contextlib.contextmanager
def neutralized(owner, name, replacement):
    """Causal control: remove exactly one guard and let the paired fault through."""
    original = getattr(owner, name)
    setattr(owner, name, replacement)
    try:
        yield
    finally:
        setattr(owner, name, original)


# ---------------------------------------------------------------------------
# The exact pinned shared journal engine

_ENGINE = []


def engine():
    if _ENGINE:
        return _ENGINE[0]
    pin = F.load_admission()['sharedHelper']
    override = os.environ.get('FSGG_SHARED_HELPER_ROOT')
    if override:
        root = Path(override)
    else:
        root = Path(tempfile.mkdtemp(prefix='forward-shared-helper-'))
        F.fetch_shared_helper(pin, root)
    _ENGINE.append(F.load_engine(root, pin))
    _ENGINE[0].helper_root = root
    return _ENGINE[0]


# ---------------------------------------------------------------------------
# Fixtures

def zip_bytes(members):
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w') as z:
        for name, data in members.items():
            info = zipfile.ZipInfo(name, (2000, 1, 1, 0, 0, 0))
            info.external_attr = 0o100644 << 16
            z.writestr(info, data)
    return out.getvalue()


def sign(raw):
    """nuget.org adds exactly one repository signature member."""
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        members = {name: z.read(name) for name in z.namelist()}
    members['.signature.p7s'] = b'repository-signature'
    return zip_bytes(members)


class World:
    def __init__(self, tmp, version='0.3.1'):
        self.tmp, self.version = Path(tmp), version
        self.directory = self.tmp / 'candidate'
        self.directory.mkdir()
        rows = []
        for id in IDS:
            raw = zip_bytes({f'lib/net10.0/{id}.dll': f'dll-{id}-{version}'.encode(), 'README.md': b'readme'})
            (self.directory / f'{id}.{version}.nupkg').write_bytes(raw)
            rows.append({'packageId': id, 'file': f'{id}.{version}.nupkg', 'sha256': sha(raw), 'bytes': len(raw),
                         'assemblySha256': sha(f'dll-{id}-{version}'.encode())})
        manifest = {'schema': 'fsgg.coordination.execution-pair-release-preparation/1', 'profile': 'execution-pair', 'sourceCommit': SOURCE,
                    'sourceTree': TREE, 'version': version, 'tag': 'execution/v' + version, 'publicationAuthorized': False, 'tagAuthorized': False,
                    'publicationOrder': 'github-packages-pair-then-identical-nuget-org-inputs',
                    'ownership': {'reservation': 'unreserved', 'githubPackages': 'not-observed-by-preparation', 'nugetOrg': 'not-observed-by-preparation'},
                    'packages': rows}
        (self.directory / F.ASSET_NAMES['manifest']).write_bytes(json.dumps(manifest, separators=(',', ':')).encode() + b'\n')
        self.archive = zip_bytes({p.name: p.read_bytes() for p in sorted(self.directory.iterdir())})
        a = LEGACY['admission']()
        a.update(version=version, tag='execution/v' + version, source=SOURCE, tree=TREE, protectedMerge=SOURCE, runId=1, jobId=2, artifactId=3,
                 archiveSha256=sha(self.archive), manifestSha256=sha((self.directory / F.ASSET_NAMES['manifest']).read_bytes()),
                 packages={row['packageId']: row['sha256'] for row in rows})
        self.receivers = self.tmp / 'fixture-receivers'
        LEGACY['consumer_fixture'](self.receivers, a)
        n = a['namespaceEntitlement']
        n['publisherWorkflow'] = F.PUBLISH_WORKFLOW
        self.selection = {'operation': f'publish-execution-pair-forward-{version}-{SOURCE[:12]}', 'version': version, 'tag': 'execution/v' + version,
                          'source': SOURCE, 'tree': TREE, 'operator': OPERATOR, 'journal': {'id': f'execution-pair-{version}-{SOURCE[:12]}-01'},
                          'candidate': {'runId': 1, 'jobId': 2, 'artifactId': 3, 'archiveSha256': a['archiveSha256'], 'manifestSha256': a['manifestSha256'],
                                        'packages': a['packages']},
                          'consumerQualification': a['consumerQualification'], 'namespaceEntitlement': n,
                          'tagProtection': {'rulesetId': F.RULESET_ID, 'policySha256': POLICY_HASH}}
        F.validate_selection(self.selection)
        self.candidate = F.Candidate(self.directory, self.selection)


def evidence(name):
    return {'evidenceSha256': sha(name.encode()), 'reference': f'fixture:{name}'}


def enabled_admission(world, *, fence=True):
    admission = F.load_admission()
    live = {key: evidence(key) for key in F.LIVE_EFFECT_KEYS}
    live['environment']['name'] = F.ENVIRONMENT
    live['nugetTrustedPublishing']['user'] = world.selection['namespaceEntitlement']['nugetUser']
    admission.update(enabled=True, selection=copy.deepcopy(world.selection), liveEffects=live)
    if fence:
        admission['sharedHelper']['boundIntentFence'] = evidence('fence')
    return admission


class MemoryJournal:
    """The engine's Journal protocol with the single-change CAS discipline of the protected journal."""

    def __init__(self, cid, effects=None):
        self.state = engine().execution.JournalState(1, cid, dict(effects or {}))
        self.cas = []

    def read(self):
        return self.state

    def compare_and_swap(self, expected, effect, state):
        if expected != self.state:
            return False
        old = self.state.effects.get(effect)
        assert (old, state) in {(None, 'intent'), ('intent', 'verified')}, (effect, old, state)
        self.state = engine().execution.JournalState(self.state.generation + 1, self.state.content_id, {**self.state.effects, effect: state})
        self.cas.append((effect, state))
        return True


class Hub:
    """In-memory GitHub, release, tag and feed surface plus the package-push runner."""

    def __init__(self, world):
        self.world = world
        self.main, self.run_attempt, self.actor = PUBLISHER, 1, OPERATOR
        self.tags, self.releases, self.assets = {}, [], {}
        self.feeds = {'github': {}, 'nuget': {}}
        self.pushes, self.requests, self.posts = [], [], []
        self.push_fail = {}  # package id -> 'unknown-applied' | 'unknown-not-applied'
        self.extra_versions = {}  # (feed, package) -> list
        self.extra_tags = []
        self.sign_nuget = True
        self.tamper = {}  # (feed, package) -> bytes served instead
        self.policy_bad = False
        self.release_asset_serial = 1000
        self.drop_requests = set()
        self.hidden_github_listing = set()  # a token that cannot see an existing package gets 404
        self.release_read_guard = None

    # -- GitHub REST (api.get/post/patch/upload_asset/download_asset)
    def api_get(self, path):
        E = engine()
        R = F.REPOSITORY
        self.requests.append(('GET', path))
        if path == f'repos/{R}':
            return {'id': F.REPOSITORY_ID, 'full_name': R}
        if path == f'repos/{R}/actions/runs/{RUN}':
            return {'id': RUN, 'repository': {'id': F.REPOSITORY_ID}, 'path': F.PUBLISH_WORKFLOW, 'event': 'workflow_dispatch', 'head_branch': 'main',
                    'head_sha': PUBLISHER, 'run_attempt': self.run_attempt, 'actor': {'login': self.actor}, 'status': 'in_progress'}
        if path == f'repos/{R}/git/ref/heads/main':
            return {'object': {'sha': self.main, 'type': 'commit'}}
        if path.startswith(f'repos/{R}/git/ref/tags/'):
            tag = path.removeprefix(f'repos/{R}/git/ref/tags/')
            if tag not in self.tags:
                raise E.provider.NotFound(path)
            return {'object': {'sha': self.tags[tag], 'type': 'commit'}}
        if path.startswith(f'repos/{R}/releases?'):
            if self.release_read_guard and self.release_read_guard():
                raise OSError('transient release listing failure')
            page = int(path.rsplit('page=', 1)[1])
            return [dict(r) for r in self.releases] if page == 1 else []
        m = re.fullmatch(rf'repos/{re.escape(R)}/releases/(\d+)/assets\?per_page=100', path)
        if m:
            return [{'id': aid, 'name': name} for name, (aid, _) in self.assets.get(int(m.group(1)), {}).items()]
        raise AssertionError('unexpected API read ' + path)

    def api_post(self, path, body):
        R = F.REPOSITORY
        self.posts.append((path, body))
        if path == f'repos/{R}/git/refs':
            ref = body['ref'].removeprefix('refs/tags/')
            assert ref not in self.tags
            self.tags[ref] = body['sha']
            return {}
        if path == f'repos/{R}/releases':
            release = dict(body, id=500 + len(self.releases))
            self.releases.append(release)
            self.assets[release['id']] = {}
            return release
        raise AssertionError('unexpected API write ' + path)

    def api_patch(self, path, body):
        m = re.fullmatch(rf'repos/{re.escape(F.REPOSITORY)}/releases/(\d+)', path)
        assert m
        self.posts.append((path, body))
        for release in self.releases:
            if release['id'] == int(m.group(1)):
                release.update(body)
                return release
        raise AssertionError('unknown release')

    def api_upload(self, release_id, name, path, sha256):
        raw = Path(path).read_bytes()
        assert sha(raw) == sha256
        self.posts.append(('upload', name))
        self.release_asset_serial += 1
        self.assets[release_id][name] = (self.release_asset_serial, raw)
        return {}

    def api_download(self, asset_id):
        for assets in self.assets.values():
            for aid, raw in assets.values():
                if aid == asset_id:
                    return raw
        raise AssertionError('unknown asset')

    # -- feeds, GitHub listings and native actions evidence (transport.get)
    def transport_get(self, url, headers=None, cap=0):
        self.requests.append(('GET', url))
        for needle in self.drop_requests:
            if needle in url:
                raise F.Refused('transport dropped ' + needle)
        world = self.world
        R = F.REPOSITORY
        m = re.fullmatch(r'https://(nuget\.pkg\.github\.com/fs-gg/download|api\.nuget\.org/v3-flatcontainer)/([a-z.]+)/(\d+\.\d+\.\d+)/\2\.\3\.nupkg', url)
        if m:
            feed = 'github' if m.group(1).startswith('nuget.pkg') else 'nuget'
            package = next(i for i in IDS if i.lower() == m.group(2))
            if (feed, package) in self.tamper:
                return 200, self.tamper[(feed, package)]
            if m.group(3) == world.version and package in self.feeds[feed]:
                return 200, self.feeds[feed][package]
            return 404, b''
        m = re.fullmatch(r'https://api\.nuget\.org/v3-flatcontainer/([a-z.]+)/index\.json', url)
        if m:
            package = next(i for i in IDS if i.lower() == m.group(1))
            versions = list(self.extra_versions.get(('nuget', package), [])) + ([world.version] if package in self.feeds['nuget'] else [])
            return (200, json.dumps({'versions': versions}).encode()) if versions else (404, b'')
        m = re.fullmatch(r'https://api\.github\.com/orgs/FS-GG/packages/nuget/([a-z.]+)/versions\?per_page=100', url)
        if m:
            package = next(i for i in IDS if i.lower() == m.group(1))
            if package in self.hidden_github_listing:
                return 404, b''
            versions = list(self.extra_versions.get(('github', package), [])) + ([world.version] if package in self.feeds['github'] else [])
            return (200, json.dumps([{'name': v} for v in versions]).encode()) if versions else (404, b'')
        if url == f'https://api.github.com/repos/{R}/git/matching-refs/tags/execution/v':
            return 200, json.dumps([{'ref': 'refs/tags/' + t} for t in [*self.extra_tags, *self.tags]]).encode()
        if url == f'https://api.github.com/repos/{R}/git/ref/tags/{world.selection["tag"]}':
            return (200, b'{}') if world.selection['tag'] in self.tags else (404, b'')
        if url == f'https://api.github.com/repos/{R}/releases/tags/' + world.selection['tag'].replace('/', '%2F'):
            return (200, b'{}') if any(r['tag_name'] == world.selection['tag'] and not r.get('draft') for r in self.releases) else (404, b'')
        # candidate and receiver evidence
        a = f'https://api.github.com/repos/{R}/actions'
        c = world.selection['candidate']
        q = world.selection['consumerQualification']
        if url == f'{a}/runs/{c["runId"]}':
            return 200, json.dumps({'id': c['runId'], 'repository': {'id': F.REPOSITORY_ID}, 'head_repository': {'id': F.REPOSITORY_ID}, 'head_branch': 'main',
                                    'event': 'workflow_dispatch', 'head_sha': SOURCE, 'run_attempt': 1, 'status': 'completed', 'conclusion': 'success',
                                    'path': F.PREPARE_WORKFLOW}).encode()
        if url == f'{a}/runs/{c["runId"]}/attempts/1/jobs?per_page=100':
            return 200, json.dumps({'total_count': 1, 'jobs': [{'id': c['jobId'], 'run_id': c['runId'], 'run_attempt': 1, 'head_sha': SOURCE,
                                    'name': 'Prepare the routine Execution pair without publication', 'status': 'completed', 'conclusion': 'success'}]}).encode()
        if url == f'{a}/artifacts/{c["artifactId"]}':
            return 200, json.dumps({'id': c['artifactId'], 'expired': False, 'name': 'execution-pair-' + SOURCE, 'digest': 'sha256:' + c['archiveSha256'],
                                    'workflow_run': {'id': c['runId'], 'head_sha': SOURCE}}).encode()
        if url == f'{a}/artifacts/{c["artifactId"]}/zip':
            return 200, world.archive
        if url == f'{a}/runs/{q["runId"]}':
            return 200, (world.receivers / 'run.json').read_bytes()
        if url == f'{a}/runs/{q["runId"]}/attempts/1/jobs?per_page=100':
            return 200, (world.receivers / 'jobs.json').read_bytes()
        for case, row in q['cases'].items():
            if url == f'{a}/artifacts/{row["artifactId"]}':
                return 200, (world.receivers / f'{case}.artifact.json').read_bytes()
            if url == f'{a}/jobs/{row["jobId"]}/logs':
                return 200, (world.receivers / f'{case}.job.log').read_bytes()
            if url == f'{a}/artifacts/{row["artifactId"]}/zip':
                return 200, (world.receivers / f'{case}.zip').read_bytes()
        # this run's public-load evidence
        if url == f'{a}/runs/{RUN}/attempts/1/jobs?per_page=100':
            return 200, json.dumps({'total_count': 1, 'jobs': [{'name': 'public-load', 'status': 'completed', 'conclusion': self.public_job, 'run_id': RUN, 'run_attempt': 1}]}).encode()
        if url == f'{a}/runs/{RUN}/artifacts?name={F.public_receipt_artifact_name(RUN)}':
            return 200, json.dumps({'artifacts': self.receipt_listing()}).encode()
        if url == f'{a}/artifacts/9001/zip':
            return 200, self.receipt_archive
        raise AssertionError('unexpected transport read ' + url)

    public_job = 'success'
    receipt_archive = b''

    def receipt_listing(self):
        if not self.receipt_archive:
            return []
        return [{'name': F.public_receipt_artifact_name(RUN), 'expired': False, 'id': 9001, 'workflow_run': {'id': RUN}, 'digest': 'sha256:' + sha(self.receipt_archive)}]

    def store_receipt(self, raw):
        self.receipt_archive = zip_bytes({'public-load-receipt.json': raw})

    # -- package push runner
    def push(self, argv):
        assert argv[:3] == ['dotnet', 'nuget', 'push']
        path = Path(argv[3])
        source, key = argv[argv.index('--source') + 1], argv[argv.index('--api-key') + 1]
        feed = 'github' if 'nuget.pkg.github.com' in source else 'nuget'
        package = next(i for i in IDS if path.name.startswith(i + '.'))
        self.pushes.append((feed, package))
        assert key
        mode = self.push_fail.get(package) if self.push_fail.get('feed', feed) == feed else None
        raw = path.read_bytes()
        if mode != 'unknown-not-applied':
            self.feeds[feed][package] = sign(raw) if feed == 'nuget' and self.sign_nuget else raw
        if mode:
            raise subprocess.CalledProcessError(1, argv)


class HubAPI:
    def __init__(self, hub):
        self.hub = hub

    def get(self, path):
        return self.hub.api_get(path)

    def post(self, path, body):
        return self.hub.api_post(path, body)

    def patch(self, path, body):
        return self.hub.api_patch(path, body)

    def upload_asset(self, release_id, name, path, expected_sha256=None):
        return self.hub.api_upload(release_id, name, path, expected_sha256)

    def download_asset(self, asset_id):
        return self.hub.api_download(asset_id)


class HubTransport:
    def __init__(self, hub):
        self.hub = hub

    def get(self, url, *, headers=None, cap=0, timeout=0):
        return self.hub.transport_get(url, headers, cap)


def installed_check(world):
    def check(directory, mode, output):
        assert mode == 'public'
        Path(output).write_bytes(json.dumps({
            'schema': 'fsgg.coordination.execution-pair-installed-load/1', 'mode': 'public', 'publicOnly': True, 'producerProjectReferenced': False,
            'packageInputsOnly': True, 'physicalSourceExclusionEstablished': False, 'privateCachesUnavailable': True,
            'effectiveProjectIsolationGuard': True, 'effectiveNuGetFoldersVerified': True, 'actualCompilerReferencesVerified': True,
            'packages': [{'packageId': i, 'version': world.version, 'runtimeSha256': world.candidate.assemblies[i]} for i in IDS],
            'nativeFixtureInvoked': False, 'consumerAdoptionAccepted': False}).encode())
    return check


class Clock:
    def __init__(self):
        self.now = 0

    def __call__(self):
        self.now += 1
        return self.now


def policy_reader(hub):
    def read():
        desired = json.loads((ROOT / 'eng/repository-settings/release-tag-ruleset.json').read_bytes())
        actual = dict(desired, id=F.RULESET_ID)
        if hub.policy_bad:
            actual['bypass_actors'] = [{'actor_id': 1, 'actor_type': 'Integration', 'bypass_mode': 'always'}]
        return F.canonical(actual)
    return read


def stage(world, hub, journal, which, *, generated=None, nuget_key='nuget-key', provider_allowed=None, effects=None, report=None):
    E = engine()
    candidate = F.Candidate(world.directory, world.selection)
    plan = effects or F.plan_effects(E, world.selection, candidate, generated, which)
    admission = F.PairAdmission(HubAPI(hub), world.selection, plan, stage=which, publisher_sha=PUBLISHER, run_id=RUN, actor=OPERATOR, ref='refs/heads/main',
                                policy_reader=policy_reader(hub) if which == 'finalize' else None)
    provider = F.PairProvider(E, HubAPI(hub), world.selection, candidate, transport=HubTransport(hub), github_token='gh', nuget_key=nuget_key if which == 'packages' else '',
                              generated=generated, asset_dir=world.tmp / 'assets', runner=hub.push,
                              allowed=F.sendable(which) if provider_allowed is None else provider_allowed)
    return F.run_stage(E, which, F.content_id(world.selection), plan, journal, admission, provider, sleep=lambda _: None, clock=Clock(),
                       report=report or (lambda message: None))


def finalize_inputs(world, hub):
    """Public execution, then the authenticated derivation the finalize job performs (assets land beside the world)."""
    candidate = F.Candidate(world.directory, world.selection)
    receipt = F.run_public_load(world.selection, candidate, transport=HubTransport(hub), verify_signature=lambda _: None,
                                installed_check=installed_check(world), workdir=Path(tempfile.mkdtemp(dir=world.tmp)),
                                sleep=lambda _: None, clock=Clock())
    hub.store_receipt(receipt)
    return F.derive_finalize_inputs(HubTransport(hub), 'gh', world.selection, candidate, RUN, world.tmp)


def full_run(tmp, version='0.3.1'):
    """Packages, public-load and finalize against one journal; returns everything."""
    world = World(tmp, version)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    fence_a = stage(world, hub, journal, 'packages')
    generated = finalize_inputs(world, hub)
    fence_b = stage(world, hub, journal, 'finalize', generated=generated)
    return world, hub, journal, fence_a, fence_b, generated


# ---------------------------------------------------------------------------
# Controls


def version_controls():
    allowed = ['0.3.1', '0.3.2', '0.3.10', '0.4.0', '0.10.0', '0.99.5', '1.0.0', '2.10.3']
    refused_versions = ['0.3.0', '0.2.9', '0.3.01', '0.03.1', '0.3', '0.3.1.0', '0.3.1-rc1', '0.3.1+b', 'v0.3.1', ' 0.3.1', '0.3.1\n', '1.0', '01.0.0', '', None, 3.1]
    for v in allowed:
        assert F.forward_version_allowed(v), v
        count('version-allowed')
    for v in refused_versions:
        assert not F.forward_version_allowed(v), v
        count('version-refused')
    # One rule: the release script, the shared harness and this module agree.
    fsx = (ROOT / 'eng/callable-cli-release.fsx').read_text()
    literal = re.search(r'let forwardVersionPattern = "(.*)"', fsx).group(1).replace('\\\\', '\\')
    assert literal == F.FORWARD_VERSION_PATTERN, 'release script and module version rules differ'
    harness = runpy.run_path(str(ROOT / 'eng/test-callable-cli-installed-harness.py'))
    for v in allowed:
        assert harness['pair_version'](v) == v
    for v in ['0.3.0', '0.2.0']:
        assert harness['pair_version'](v) == v  # historical literals survive
    for v in ['0.2.9', '0.3.1-rc1', '0.3.01', '[0.3.0]', '0.3.1"/>']:
        must_refuse('harness version ' + v, harness['pair_version'], v, exceptions=(RuntimeError,))
    # Causal: a rule that admits the original identity would let forward reuse 0.3.0.
    with patch.object(F, 'FORWARD_VERSION', re.compile(r'^0\.3\.[0-9]+$')):
        assert F.forward_version_allowed('0.3.0')
    # select_version: only a strictly newer, occupancy-aware choice.
    assert F.select_version('0.3.1', [], []) == '0.3.1'
    assert F.select_version('0.3.1', ['0.2.0'], ['execution/v0.2.0']) == '0.3.1'
    assert F.select_version('0.3.1', ['0.3.1'], []) == '0.3.2'
    assert F.select_version('0.3.1', ['0.3.0'], []) == '0.3.1'
    assert F.select_version('0.3.1', ['0.3.5-rc1'], []) == '0.3.6'
    assert F.select_version('0.3.1', [], ['execution/v0.3.4']) == '0.3.5'
    assert F.select_version('0.3.0', [], []) == '0.3.1'
    must_refuse('unparseable occupancy', F.select_version, '0.3.1', ['not-a-version'], [])
    count('select-version')


def committed_state_controls():
    a = F.load_admission()
    assert F.effective_state(a) == 'disabled' and a['selection'] is None and a['enabled'] is False
    assert all(v is None for v in a['liveEffects'].values()) and a['sharedHelper']['boundIntentFence'] is None
    assert a['original'] == {'version': '0.3.0', 'operation': F.ORIGINAL_OPERATION, 'tag': 'execution/v0.3.0', 'retained': True}
    assert len(F.EFFECT_IDS) == 13 == len(set(F.EFFECT_IDS)) and list(F.EFFECT_IDS) == a['effects']
    assert F.STAGES['packages'] == F.EFFECT_IDS[:4] and F.STAGES['finalize'] == F.EFFECT_IDS
    # The original operation's admission, publisher and preparation route are byte-for-byte untouched.
    original = (ROOT / 'eng/execution-pair-release-admission.json').read_bytes()
    assert sha(original) == ORIGINAL_ADMISSION_SHA256, 'original 0.3.0 admission changed'
    publish = (ROOT / '.github/workflows/callable-cli-release-publish.yml').read_bytes()
    assert sha(publish) == ORIGINAL_PUBLISHER_SHA256, 'original 0.3.0 publisher changed'
    original_admission = json.loads(original)
    assert original_admission['enabled'] is False and original_admission['version'] == '0.3.0'
    must_refuse('original admission is not a forward admission', F.validate_admission, original_admission)
    count('committed-state')
    # Every closed-shape fault is refused; the same fault with the shape check removed would pass.
    for label, mutate in {
        'schema': lambda v: v.update(schema='x'), 'enabled-type': lambda v: v.update(enabled=1), 'proposed-original': lambda v: v.update(proposedVersion='0.3.0'),
        'original-changed': lambda v: v['original'].update(version='0.3.1'), 'original-operation': lambda v: v['original'].update(operation='publish-execution-pair-forward-x'),
        'effects-reordered': lambda v: v['effects'].reverse(), 'effects-short': lambda v: v['effects'].pop(), 'stage-widened': lambda v: v['stages'].update(packages=v['effects']),
        'extra-key': lambda v: v.update(extra=1), 'helper-repo': lambda v: v['sharedHelper'].update(repository='FS-GG/other'),
        'helper-file-missing': lambda v: v['sharedHelper']['files'].pop('scripts/fsgg_feed.py'),
        'helper-digest': lambda v: v['sharedHelper']['files'].update({'scripts/fsgg_feed.py': 'g' * 64}),
        'live-key-missing': lambda v: v['liveEffects'].pop('oldWriterFence'), 'live-malformed': lambda v: v['liveEffects'].update(oldWriterFence={'x': 1}),
    }.items():
        broken = copy.deepcopy(a)
        mutate(broken)
        must_refuse('admission ' + label, F.validate_admission, broken)


def selection_controls(tmp):
    world = World(tmp)
    sel = world.selection
    good = enabled_admission(world)
    F.validate_admission(good)
    assert F.effective_state(good) == 'enabled'
    selected_only = copy.deepcopy(good)
    selected_only['enabled'] = False
    assert F.effective_state(F.validate_admission(selected_only)) == 'selected'
    # Enabling needs every live prerequisite, a selection and the shared fence.
    for key in F.LIVE_EFFECT_KEYS:
        broken = copy.deepcopy(good)
        broken['liveEffects'][key] = None
        must_refuse('enabled without ' + key, F.validate_admission, broken)
    broken = copy.deepcopy(good)
    broken['sharedHelper']['boundIntentFence'] = None
    must_refuse('enabled without shared bound-intent fence', F.validate_admission, broken)
    broken = copy.deepcopy(good)
    broken['selection'] = None
    must_refuse('enabled without selection', F.validate_admission, broken)
    broken = copy.deepcopy(good)
    broken['liveEffects']['environment']['name'] = 'other'
    must_refuse('wrong environment', F.validate_admission, broken)
    broken = copy.deepcopy(good)
    broken['liveEffects']['nugetTrustedPublishing']['user'] = 'Someone'
    must_refuse('wrong trusted publishing user', F.validate_admission, broken)
    faults = {
        'reuses-original-version': lambda s: s.update(version='0.3.0', tag='execution/v0.3.0'),
        'tag-differs': lambda s: s.update(tag='execution/v0.3.9'), 'operation-original': lambda s: s.update(operation=F.ORIGINAL_OPERATION),
        'operation-source': lambda s: s.update(operation=s['operation'][:-12] + '0' * 12), 'operator': lambda s: s.update(operator='someone'),
        'journal-id': lambda s: s['journal'].update(id='../main'), 'journal-extra': lambda s: s['journal'].update(x=1),
        'candidate-job': lambda s: s['candidate'].update(jobId=0), 'candidate-digest': lambda s: s['candidate'].update(archiveSha256='f' * 63),
        'candidate-package-missing': lambda s: s['candidate']['packages'].pop(IDS[0]),
        'executor-moved': lambda s: s['consumerQualification'].update(executor='1' * 40),
        'receiver-same-run': lambda s: s['consumerQualification'].update(runId=s['candidate']['runId']),
        'receiver-case-missing': lambda s: s['consumerQualification']['cases'].pop('sdd-rollback'),
        'receiver-duplicate-job': lambda s: s['consumerQualification']['cases']['sdd-s1'].update(jobId=s['consumerQualification']['cases']['fsquint-s1']['jobId']),
        'namespace-workflow': lambda s: s['namespaceEntitlement'].update(publisherWorkflow=F.PREPARE_WORKFLOW),
        'namespace-unaccepted': lambda s: s['namespaceEntitlement'].update(accepted=False),
        'protection-ruleset': lambda s: s['tagProtection'].update(rulesetId=7), 'extra-key': lambda s: s.update(extra=1),
    }
    for label, mutate in faults.items():
        broken = copy.deepcopy(sel)
        mutate(broken)
        must_refuse('selection ' + label, F.validate_selection, broken)
    # The pair-admission projection feeds the maintained validators unchanged in shape.
    projected = F.selection_as_pair_admission(sel)
    assert projected['profile'] == 'execution-pair' and projected['enabled'] is False and projected['protectedMerge'] == SOURCE
    # Content identity covers every immutable input and nothing generated.
    base = F.content_id(sel)
    for label, mutate in {'receivers': lambda s: s['consumerQualification']['cases']['sdd-s1'].update(archiveSha256='e' * 64),
                          'journal': lambda s: s['journal'].update(id='execution-pair-0.3.1-777777777777-02'),
                          'candidate': lambda s: s['candidate'].update(archiveSha256='e' * 64), 'version': lambda s: s.update(version='0.3.2')}.items():
        other = copy.deepcopy(sel)
        mutate(other)
        assert F.content_id(other) != base, label
    count('selection')


def helper_pin_controls(tmp):
    E = engine()
    pin = F.load_admission()['sharedHelper']
    F.helper_digest_report(E.helper_root, pin)
    count('helper-pin')
    # A single changed byte in any pinned engine file is refused before anything is imported.
    for name in pin['files']:
        copy_root = Path(tempfile.mkdtemp(dir=tmp))
        shutil.copytree(E.helper_root / 'scripts', copy_root / 'scripts')
        target = copy_root / name
        target.write_bytes(target.read_bytes() + b'\n# tampered\n')
        must_refuse('tampered ' + name, F.load_engine, copy_root, pin)
    missing = Path(tempfile.mkdtemp(dir=tmp))
    must_refuse('missing helper', F.helper_digest_report, missing, pin)

    class Opener:
        def __init__(self, files, tamper=None):
            self.files, self.tamper = files, tamper

        def open(self, request, timeout=0):
            name = request.full_url.split(pin['commit'] + '/', 1)[1]
            assert f"/{pin['commit']}/" in request.full_url and request.full_url.startswith('https://raw.githubusercontent.com/FS-GG/.github/')
            raw = (E.helper_root / name).read_bytes()
            if self.tamper == name:
                raw += b'x'
            return io.BytesIO(raw)

    out = Path(tempfile.mkdtemp(dir=tmp))
    F.fetch_shared_helper(pin, out, opener=Opener(None))
    F.helper_digest_report(out, pin)
    must_refuse('fetched bytes differ', F.fetch_shared_helper, pin, Path(tempfile.mkdtemp(dir=tmp)), opener=Opener(None, tamper='scripts/fsgg_feed.py'))
    count('helper-fetch')


def effect_plan_controls(tmp):
    world = World(tmp)
    E = engine()
    sel, cand = world.selection, world.candidate
    plan = F.plan_effects(E, sel, cand, {k: b'generated-' + k.encode() for k in F.GENERATED_ASSETS}, 'finalize')
    assert [e.identity for e in plan] == list(F.EFFECT_IDS) and len(plan) == 13
    assert [e.identity for e in F.plan_effects(E, sel, cand, None, 'packages')] == list(F.EFFECT_IDS[:4])
    must_refuse('finalize plan needs generated bytes', F.plan_effects, E, sel, cand, None, 'finalize')
    must_refuse('unknown stage', F.plan_effects, E, sel, cand, None, 'tag')
    by = {e.identity: e for e in plan}
    for id in IDS:
        for feed in ('github', 'nuget'):
            e = by[f'{feed}:{id}']
            assert e.request_digest == cand.packages[id] and e.target_digest == cand.payloads[id]
        assert by[f'asset:{id}'].target_digest == cand.packages[id]
    assert by['tag'].target_digest == SOURCE and by['asset:manifest'].target_digest == sha(cand.manifest_bytes)
    # Credential classes: a stage may originate only effects whose credential it holds.
    packages_send, finalize_send = F.sendable('packages'), F.sendable('finalize')
    assert packages_send == set(F.EFFECT_IDS[:4]) and finalize_send == set(F.EFFECT_IDS[4:]) and not packages_send & finalize_send
    assert F.STAGE_CREDENTIALS['finalize'].isdisjoint({'nuget', 'github-packages'}) and 'nuget' not in F.STAGE_CREDENTIALS['finalize']
    assert 'github-contents' not in F.STAGE_CREDENTIALS['packages'] and 'policy-read' not in F.STAGE_CREDENTIALS['packages']
    # Candidate directory faults.
    for label, mutate in {
        'extra-file': lambda d: (d / 'extra').write_bytes(b'x'),
        'missing-manifest': lambda d: (d / F.ASSET_NAMES['manifest']).unlink(),
        'changed-package': lambda d: (d / f'{IDS[0]}.0.3.1.nupkg').write_bytes(b'changed'),
        'symlink': lambda d: ((d / f'{IDS[1]}.0.3.1.nupkg').unlink(), (d / f'{IDS[1]}.0.3.1.nupkg').symlink_to(d / f'{IDS[0]}.0.3.1.nupkg')),
    }.items():
        copy_dir = Path(tempfile.mkdtemp(dir=tmp)) / 'c'
        shutil.copytree(world.directory, copy_dir)
        mutate(copy_dir)
        must_refuse('candidate ' + label, F.Candidate, copy_dir, sel)
    changed = copy.deepcopy(sel)
    changed['source'] = '1' * 40
    must_refuse('manifest source differs', F.Candidate, world.directory, changed)
    count('effect-plan')


def occupancy_controls(tmp):
    world = World(tmp)
    hub = Hub(world)
    sel = world.selection
    clean = F.occupancy_join(sel, F.observe_occupancy(HubTransport(hub), sel, 'gh'))
    assert clean['cells'] == 'absent' and clean['version'] == '0.3.1'
    count('occupancy-good')
    cases = {
        'github-cell-populated': lambda h: h.feeds['github'].update({IDS[0]: b'x'}),
        'nuget-cell-populated': lambda h: h.feeds['nuget'].update({IDS[1]: b'x'}),
        'tag-populated': lambda h: h.tags.update({'execution/v0.3.1': SOURCE}),
        'release-populated': lambda h: h.releases.append({'tag_name': 'execution/v0.3.1', 'draft': False, 'id': 1}),
        'higher-github-version': lambda h: h.extra_versions.update({('github', IDS[0]): ['0.3.5'], ('github', IDS[1]): ['0.3.5']}),
        'equal-nuget-version': lambda h: h.extra_versions.update({('nuget', IDS[0]): ['0.3.1'], ('nuget', IDS[1]): ['0.3.1']}),
        'higher-tag': lambda h: h.extra_tags.append('execution/v0.4.0'),
        'github-listing-hidden': lambda h: h.hidden_github_listing.update({IDS[0]}) or h.extra_versions.update({('nuget', IDS[0]): ['0.2.0'], ('nuget', IDS[1]): ['0.2.0']}),
    }
    for label, mutate in cases.items():
        fresh = Hub(world)
        mutate(fresh)
        must_refuse('occupancy ' + label, lambda f=fresh: F.occupancy_join(sel, F.observe_occupancy(HubTransport(f), sel, 'gh')))
    # An unreadable authority (HTTP 500 / full page / malformed) never means absence.
    class Flaky(HubTransport):
        def __init__(self, hub, needle, status, body=b''):
            super().__init__(hub)
            self.needle, self.status, self.body = needle, status, body

        def get(self, url, **kwargs):
            if self.needle in url:
                return self.status, self.body
            return super().get(url, **kwargs)

    for needle, status, body in [('index.json', 500, b''), ('versions?per_page=100', 403, b''), ('matching-refs', 500, b''),
                                 ('releases/tags', 500, b''), ('/git/ref/tags/', 502, b''),
                                 ('versions?per_page=100', 200, json.dumps([{'name': '0.1.%d' % i} for i in range(100)]).encode()),
                                 ('index.json', 200, b'{"versions":"x"}'), ('matching-refs', 200, b'{}')]:
        must_refuse(f'unreadable {needle} {status}', lambda n=needle, s=status, b=body: F.observe_occupancy(Flaky(Hub(world), n, s, b), sel, 'gh'))
    # Causal: a join without the strict-newer rule accepts a populated higher history.
    higher = Hub(world)
    higher.extra_versions[('github', IDS[0])] = higher.extra_versions[('github', IDS[1])] = ['0.3.5']
    observed = F.observe_occupancy(HubTransport(higher), sel, 'gh')
    must_refuse('higher history', F.occupancy_join, sel, observed)
    with neutralized(F, 'select_version', lambda proposed, versions, tags=(): proposed):
        assert F.occupancy_join(sel, observed)['version'] == '0.3.1'
    # Causal: without the ambiguity rule a hidden GitHub listing (404) is silently treated as an empty history.
    hidden = Hub(world)
    hidden.hidden_github_listing.add(IDS[0])
    hidden.extra_versions[('nuget', IDS[0])] = hidden.extra_versions[('nuget', IDS[1])] = ['0.2.0']
    observed = F.observe_occupancy(HubTransport(hidden), sel, 'gh')
    must_refuse('hidden github listing', F.occupancy_join, sel, observed)
    observed['listing'].update({f'github:{i}': 200 for i in IDS})
    assert F.occupancy_join(sel, observed)['version'] == '0.3.1'
    count('occupancy')


def policy_controls(tmp):
    world = World(tmp)
    hub = Hub(world)
    good = policy_reader(hub)()
    assert F.policy_join(good, world.selection)['rulesetId'] == F.RULESET_ID
    desired = json.loads((ROOT / 'eng/repository-settings/release-tag-ruleset.json').read_bytes())
    faults = {
        'bypass-actor': lambda p: p.update(bypass_actors=[{'actor_id': 1, 'actor_type': 'Integration', 'bypass_mode': 'always'}]),
        'bypass-omitted': lambda p: p.pop('bypass_actors'), 'enforcement': lambda p: p.update(enforcement='evaluate'),
        'rule-removed': lambda p: p.update(rules=[r for r in p['rules'] if r['type'] != 'deletion']),
        'update-fetch-merge': lambda p: p.update(rules=[{'type': 'update', 'parameters': {'update_allows_fetch_and_merge': True}} if r['type'] == 'update' else r for r in p['rules']]),
        'conditions': lambda p: p.update(conditions={'ref_name': {'include': ['refs/tags/v*'], 'exclude': []}}),
        'id': lambda p: p.update(id=1), 'target': lambda p: p.update(target='branch'),
    }
    for label, mutate in faults.items():
        actual = dict(desired, id=F.RULESET_ID)
        mutate(actual)
        must_refuse('policy ' + label, F.policy_join, F.canonical(actual), world.selection)
    must_refuse('policy not json', F.policy_join, b'not-json', world.selection)
    wrong = copy.deepcopy(world.selection)
    wrong['tagProtection']['policySha256'] = 'a' * 64
    must_refuse('policy hash pin', F.policy_join, good, wrong)
    count('policy')


def acquisition_controls(tmp):
    world = World(tmp)
    hub = Hub(world)
    sel = world.selection
    c = F.acquire_candidate(HubTransport(hub), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    assert c.packages == world.candidate.packages
    proof = F.acquire_receivers(HubTransport(hub), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    assert sorted(proof['cases']) == sorted(sel['consumerQualification']['cases']) and proof['runId'] == 17
    assert len({r['runnerId'] for r in proof['cases'].values()}) == 4
    count('acquisition-good')

    class Alter(HubTransport):
        def __init__(self, hub, needle, fn):
            super().__init__(hub)
            self.needle, self.fn = needle, fn

        def get(self, url, **kwargs):
            status, raw = super().get(url, **kwargs)
            hit = url.endswith(self.needle[1:]) if self.needle.startswith('=') else self.needle in url
            return (status, self.fn(raw)) if hit else (status, raw)

    def edit(fn):
        return lambda raw: json.dumps(fn(json.loads(raw))).encode()

    candidate_faults = {
        'archive-bytes': ('=artifacts/3/zip', lambda raw: raw[:-1] + bytes([raw[-1] ^ 1])),
        'artifact-expired': ('=artifacts/3', edit(lambda v: dict(v, expired=True))), 'artifact-name': ('=artifacts/3', edit(lambda v: dict(v, name='other'))),
        'artifact-digest': ('=artifacts/3', edit(lambda v: dict(v, digest='sha256:' + '0' * 64))),
        'run-failed': ('=runs/1', edit(lambda v: dict(v, conclusion='failure'))), 'run-branch': ('=runs/1', edit(lambda v: dict(v, head_branch='feature'))),
        'run-attempt-2': ('=runs/1', edit(lambda v: dict(v, run_attempt=2))), 'run-source': ('=runs/1', edit(lambda v: dict(v, head_sha='1' * 40))),
        'job-name': ('runs/1/attempts', edit(lambda v: {'total_count': 1, 'jobs': [dict(v['jobs'][0], name='other')]})),
    }
    for label, (needle, fn) in candidate_faults.items():
        must_refuse('candidate ' + label, F.acquire_candidate, Alter(hub, needle, fn), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    must_refuse('oversized candidate', F.acquire_candidate, Alter(hub, '=artifacts/3/zip', lambda raw: b'x' * 50331649), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))

    class Status(HubTransport):
        def __init__(self, hub, needle, status):
            super().__init__(hub)
            self.needle, self.status = needle, status

        def get(self, url, **kwargs):
            return (self.status, b'') if self.needle in url else super().get(url, **kwargs)

    must_refuse('candidate 404', F.acquire_candidate, Status(hub, 'artifacts/3/zip', 404), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    must_refuse('candidate 500', F.acquire_candidate, Status(hub, 'runs/1', 500), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    # Four distinct receiver cases at the exact candidate source.
    q = sel['consumerQualification']
    case = 'sdd-s1'
    receiver_faults = {
        'run-source': ('=runs/17', edit(lambda v: dict(v, head_sha='1' * 40))), 'run-attempt': ('=runs/17', edit(lambda v: dict(v, run_attempt=2))),
        'job-failed': ('runs/17/attempts', edit(lambda v: dict(v, jobs=[dict(j, conclusion='failure') if j['name'].endswith(case) else j for j in v['jobs']]))),
        'same-runner': ('runs/17/attempts', edit(lambda v: dict(v, jobs=[dict(j, runner_id=v['jobs'][0]['runner_id']) for j in v['jobs']]))),
        'archive': (f'artifacts/{q["cases"][case]["artifactId"]}/zip', lambda raw: raw + b'x'),
        'artifact-expired': (f'=artifacts/{q["cases"][case]["artifactId"]}', edit(lambda v: dict(v, expired=True))),
        'log-image': (f'jobs/{q["cases"][case]["jobId"]}/logs', lambda raw: b'no image'),
    }
    for label, (needle, fn) in receiver_faults.items():
        must_refuse('receiver ' + label, F.acquire_receivers, Alter(hub, needle, fn), 'gh', sel, Path(tempfile.mkdtemp(dir=tmp)))
    # The receivers must have exercised the *candidate*: an observation for another archive is refused.
    other = copy.deepcopy(sel)
    other['candidate']['archiveSha256'] = 'c' * 64
    must_refuse('receivers observed another candidate', F.acquire_receivers, HubTransport(hub), 'gh', other, Path(tempfile.mkdtemp(dir=tmp)))
    # Causal: with the maintained candidate/receiver join removed, receivers that observed another candidate are accepted.
    fake = {'pair_consumers': lambda admission, root: {'cases': {c: {'runnerId': n} for n, c in enumerate(sel['consumerQualification']['cases'])}}, 'pair_consumer_selection': lambda a: None}
    with neutralized(F, 'readback_module', lambda: fake):
        accepted = F.acquire_receivers(HubTransport(hub), 'gh', other, Path(tempfile.mkdtemp(dir=tmp)))
    assert len(accepted['cases']) == 4
    count('acquisition')


def preflight_controls(tmp):
    world = World(tmp)
    hub = Hub(world)
    receipt = F.run_preflight(HubTransport(hub), world.selection, token='gh', policy_raw=policy_reader(hub)(), workdir=Path(tempfile.mkdtemp(dir=tmp)))
    assert receipt['liveEffectsAuthorized'] is False and receipt['effects'] == list(F.EFFECT_IDS) and receipt['occupancy']['cells'] == 'absent'
    assert not hub.posts, 'preflight performed a write'
    assert all(method == 'GET' for method, _ in hub.requests)
    assert not any('uploads.github.com' in u or '/git/refs' in u for _, u in hub.requests)
    count('preflight-good')
    populated = Hub(world)
    populated.feeds['nuget'][IDS[0]] = b'x'
    must_refuse('preflight populated cell', F.run_preflight, HubTransport(populated), world.selection, token='gh', policy_raw=policy_reader(populated)(),
                workdir=Path(tempfile.mkdtemp(dir=tmp)))
    bad = Hub(world)
    bad.policy_bad = True
    must_refuse('preflight policy', F.run_preflight, HubTransport(bad), world.selection, token='gh', policy_raw=policy_reader(bad)(), workdir=Path(tempfile.mkdtemp(dir=tmp)))
    count('preflight')


def end_to_end_controls(tmp):
    world, hub, journal, fa, fb, generated = full_run(tmp)
    C, P = IDS
    assert journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS}
    assert journal.cas == [(e, st) for e in F.EFFECT_IDS for st in ('intent', 'verified')], 'journal did not advance one effect at a time in order'
    assert hub.pushes == [('github', C), ('github', P), ('nuget', C), ('nuget', P)], 'GitHub Packages first, then identical retained nuget.org inputs'
    assert fa.sends == list(F.EFFECT_IDS[:4]) and fb.sends == list(F.EFFECT_IDS[4:]) and len(fa.sends) + len(fb.sends) == 13
    assert hub.tags == {world.selection['tag']: SOURCE} and 'execution/v0.3.0' not in hub.tags
    release, = hub.releases
    assert release['draft'] is False and release['make_latest'] == 'false' and release['target_commitish'] == SOURCE and release['tag_name'] == world.selection['tag']
    names = set(hub.assets[release['id']])
    expected_names = {f'{C}.0.3.1.nupkg', f'{P}.0.3.1.nupkg', F.ASSET_NAMES['manifest'], F.ASSET_NAMES['consumer-qualification'],
                      F.ASSET_NAMES['readback'], F.ASSET_NAMES['publication-journal']}
    assert names == expected_names
    for name, (_, raw) in hub.assets[release['id']].items():
        want = {f'{C}.0.3.1.nupkg': world.candidate.packages[C], f'{P}.0.3.1.nupkg': world.candidate.packages[P],
                F.ASSET_NAMES['manifest']: sha(world.candidate.manifest_bytes)}.get(name)
        if want:
            assert sha(raw) == want
    # Order of irreversible release writes: tag, draft, six assets, one promotion.
    kinds = [('tag' if p[0].endswith('/git/refs') else 'draft' if p[0].endswith('/releases') else 'upload' if p[0] == 'upload' else 'promote') for p in hub.posts]
    assert kinds == ['tag', 'draft'] + ['upload'] * 6 + ['promote'], kinds
    assert [p[1] for p in hub.posts if p[0] == 'upload'] == [f'{C}.0.3.1.nupkg', f'{P}.0.3.1.nupkg', F.ASSET_NAMES['manifest'],
                                                                F.ASSET_NAMES['consumer-qualification'], F.ASSET_NAMES['readback'], F.ASSET_NAMES['publication-journal']]
    readback = json.loads(hub.assets[release['id']][F.ASSET_NAMES['readback']][1])
    assert readback['consumerAdoptionAccepted'] is False and readback['originalOperationUnchanged'] == F.ORIGINAL_OPERATION and readback['contentId'] == F.content_id(world.selection)
    count('end-to-end')
    # A repeated invocation on a complete journal sends nothing and changes nothing.
    pushes, posts, cas = list(hub.pushes), list(hub.posts), list(journal.cas)
    again = stage(world, hub, journal, 'finalize', generated=generated)
    assert again.sends == [] and hub.pushes == pushes and hub.posts == posts and journal.cas == cas
    must_refuse('packages rerun after the journal advanced past its window', stage, world, hub, journal, 'packages')
    assert hub.pushes == pushes and journal.cas == cas
    count('complete-rerun-inert')
    # Generated evidence is a pure function of authenticated inputs: a recovery invocation reproduces it byte for byte.
    candidate = F.Candidate(world.directory, world.selection)
    second = F.derive_finalize_inputs(HubTransport(hub), 'gh', world.selection, candidate, RUN, Path(tempfile.mkdtemp(dir=tmp)))
    assert second == generated
    receivers = F.acquire_receivers(HubTransport(hub), 'gh', world.selection, Path(tempfile.mkdtemp(dir=tmp)))
    public = F.fetch_public_load_receipt(HubTransport(hub), 'gh', world.selection, RUN, Path(tempfile.mkdtemp(dir=tmp)))
    feeds = {f'{f}:{i}': F.feed_fact(f, candidate.packages[i], hub.feeds[f][i], candidate.bytes[i]) for i in IDS for f in ('github', 'nuget')}
    assert F.build_assets(world.selection, candidate, receivers=receivers, public_load=public, feeds=feeds) == generated
    changed_feeds = copy.deepcopy(feeds)
    changed_feeds[f'nuget:{C}']['servedArchiveSha256'] = '0' * 64
    assert F.build_assets(world.selection, candidate, receivers=receivers, public_load=public, feeds=changed_feeds)['readback'] != generated['readback']
    count('deterministic-assets')
    # A different version flows through the same code (forward versions are not special-cased).
    other, other_hub, other_journal, *_ = full_run(Path(tempfile.mkdtemp(dir=tmp)), '1.2.0')
    assert other_journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS} and other_hub.tags == {'execution/v1.2.0': SOURCE}
    count('other-version')


class Boom(BaseException):
    """A process death between the durable intent and the provider send."""


def recovery_controls(tmp):
    C, P = IDS
    # (1) Response lost after the feed applied the write: Unknown, then observed and settled, never resent.
    world = World(tmp)
    hub = Hub(world)
    hub.push_fail = {P: 'unknown-applied', 'feed': 'nuget'}
    journal = MemoryJournal(F.content_id(world.selection))
    fence = stage(world, hub, journal, 'packages')
    assert journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS[:4]}
    assert hub.pushes.count(('nuget', P)) == 1 and fence.sends.count(f'nuget:{P}') == 1
    count('unknown-applied-settled-once')

    # (2) The send never reached the feed: observation stays absent, the effect stays in intent, nothing is resent.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    hub.push_fail = {C: 'unknown-not-applied', 'feed': 'github'}
    journal = MemoryJournal(F.content_id(world.selection))
    must_refuse('unapplied send waits then refuses', stage, world, hub, journal, 'packages')
    assert journal.state.effects == {f'github:{C}': 'intent'} and hub.pushes == [('github', C)]
    hub.push_fail = {}
    must_refuse('recovery never resends an intent', stage, world, hub, journal, 'packages')
    assert hub.pushes == [('github', C)], 'a prior intent was resent by a later invocation'
    count('unknown-not-applied-never-resent')

    # (3) Process death after intent, before send: a new invocation observes absence and does not send.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    real = F.PairProvider.dispatch
    state = {'n': 0}

    def dying(self, effect):
        state['n'] += 1
        if effect.identity == f'github:{P}':
            raise Boom()
        return real(self, effect)

    with patch.object(F.PairProvider, 'dispatch', dying):
        try:
            stage(world, hub, journal, 'packages')
        except Boom:
            pass
    assert journal.state.effects[f'github:{P}'] == 'intent' and hub.pushes == [('github', C)]
    must_refuse('post-crash invocation waits', stage, world, hub, journal, 'packages')
    assert hub.pushes == [('github', C)], 'process-death intent was resent'
    count('crash-after-intent-never-resent')

    # (4) Same-journal continuation: a later invocation resumes at the first unverified effect without resending verified ones.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    original = hub.push
    seen = []

    def stop_after_two(argv):
        original(argv)
        seen.append(argv[3])
        if len(seen) == 2:
            raise Boom()

    hub.push = stop_after_two
    try:
        stage(world, hub, journal, 'packages')
    except Boom:
        pass
    assert hub.pushes == [('github', C), ('github', P)]
    hub.push = original
    stage(world, hub, journal, 'packages')
    assert hub.pushes == [('github', C), ('github', P), ('nuget', C), ('nuget', P)] and journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS[:4]}
    count('same-journal-continuation')


def fence_controls(tmp):
    E = engine()
    f = F.SendFence()
    must_refuse('send without intent', f.consume, 'tag')
    f.arm('tag')
    must_refuse('double arm', f.arm, 'draft')
    must_refuse('send for another effect', f.consume, 'draft')
    f.consume('tag')
    must_refuse('second send for the same effect', f.consume, 'tag')
    f.arm('tag')
    must_refuse('re-arm spent effect then consume twice', lambda: (f.consume('tag'), f.consume('tag')))
    count('fence-unit')

    # A defective driver that dispatches an intent it merely *found* in the journal (a prior invocation's).
    world = World(tmp)
    hub = Hub(world)
    candidate = F.Candidate(world.directory, world.selection)
    plan = F.plan_effects(E, world.selection, candidate, None, 'packages')
    cid = F.content_id(world.selection)

    def defective_advance(content_id, effects, journal, admission, provider):
        current = journal.read()
        for effect in effects:
            if current.effects.get(effect.identity) == 'intent':
                return provider.dispatch(effect)  # resend of an inherited intent
        return 'complete'

    def provider_for(h):
        return F.PairProvider(E, HubAPI(h), world.selection, candidate, transport=HubTransport(h), github_token='gh', nuget_key='k', runner=h.push, allowed=F.sendable('packages'))

    inherited = MemoryJournal(cid, {plan[0].identity: 'intent'})
    # Unfenced: the defective driver causes a duplicate irreversible send.
    unfenced = provider_for(hub)
    defective_advance(cid, plan, inherited, None, unfenced)
    assert hub.pushes == [('github', IDS[0])], 'control: without the fence the inherited intent is resent'
    # Fenced: same driver, same journal, the send is refused before the provider is touched.
    hub2 = Hub(world)
    fence = F.SendFence()
    fenced = F.FencedProvider(provider_for(hub2), fence)
    must_refuse('inherited intent send', defective_advance, cid, plan, F.FencedJournal(inherited, fence), None, fenced)
    assert hub2.pushes == []
    count('fence-causal-inherited-intent')

    # The same driver may send only the intent this invocation itself wrote, exactly once.
    hub3 = Hub(world)
    fence = F.SendFence()
    journal = F.FencedJournal(MemoryJournal(cid), fence)
    state = journal.read()
    assert journal.compare_and_swap(state, plan[0].identity, 'intent')
    fenced = F.FencedProvider(provider_for(hub3), fence)
    assert fenced.dispatch(plan[0]).state == 'applied' and hub3.pushes == [('github', IDS[0])]
    must_refuse('second dispatch of the same intent', fenced.dispatch, plan[0])
    assert hub3.pushes == [('github', IDS[0])]
    # A lost CAS never arms a send.
    fence = F.SendFence()
    mj = MemoryJournal(cid)
    journal = F.FencedJournal(mj, fence)
    stale = mj.read()
    assert journal.compare_and_swap(stale, plan[0].identity, 'intent')
    assert journal.compare_and_swap(stale, plan[1].identity, 'intent') is False and fence.armed == plan[0].identity
    count('fence')


def admission_controls(tmp):
    C, P = IDS
    base = World(tmp)

    def run(label, mutate, which='packages', preface=None, expect_pushes=0):
        world = World(Path(tempfile.mkdtemp(dir=tmp)))
        hub = Hub(world)
        journal = MemoryJournal(F.content_id(world.selection))
        if preface:
            preface(world, hub, journal)
        mutate(hub)
        before = list(journal.cas)
        must_refuse(label, stage, world, hub, journal, which, generated=getattr(world, 'generated', None))
        assert journal.cas == before, f'{label}: journal mutated'
        assert len(hub.pushes) == expect_pushes, f'{label}: package pushed'
        return world, hub, journal

    run('main moved', lambda h: setattr(h, 'main', '1' * 40))
    run('rerun attempt', lambda h: setattr(h, 'run_attempt', 2))
    run('wrong actor', lambda h: setattr(h, 'actor', 'mallory'))
    # A publisher that moves mid-run stops before the next irreversible send.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    original = hub.push

    def move_after_two(argv):
        original(argv)
        if len(hub.pushes) == 2:
            hub.main = '2' * 40

    hub.push = move_after_two
    must_refuse('main moved mid-run', stage, world, hub, journal, 'packages')
    assert hub.pushes == [('github', C), ('github', P)] and f'nuget:{C}' not in journal.state.effects
    count('admission-mid-run-movement')
    # Causal: the same moved-main fault with an allow-everything admission reaches the irreversible send.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    hub.main = '1' * 40
    journal = MemoryJournal(F.content_id(world.selection))
    E = engine()
    candidate = F.Candidate(world.directory, world.selection)
    plan = F.plan_effects(E, world.selection, candidate, None, 'packages')
    provider = F.PairProvider(E, HubAPI(hub), world.selection, candidate, transport=HubTransport(hub), github_token='gh', nuget_key='k', runner=hub.push, allowed=F.sendable('packages'))

    class AllowAll:
        content_id = F.content_id(world.selection)

        def authorize(self, *args):
            return True

    F.run_stage(E, 'packages', F.content_id(world.selection), plan, journal, AllowAll(), provider, sleep=lambda _: None, clock=Clock(), report=lambda m: None)
    assert len(hub.pushes) == 4, 'control: without admission a moved publisher still publishes'
    count('admission-causal')

    # Request-digest / content binding of the admission itself.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    plan = F.plan_effects(E, world.selection, F.Candidate(world.directory, world.selection), None, 'packages')
    adm = F.PairAdmission(HubAPI(hub), world.selection, plan, stage='packages', publisher_sha=PUBLISHER, run_id=RUN, actor=OPERATOR, ref='refs/heads/main')
    cid = F.content_id(world.selection)
    first = plan[0]
    assert adm.authorize(cid, first.identity, 'intent', first.request_digest)
    for label, args in {'content': ('sha256:' + '0' * 64, first.identity, 'intent', first.request_digest), 'request': (cid, first.identity, 'intent', '0' * 64),
                        'action': (cid, first.identity, 'delete', first.request_digest), 'unknown-effect': (cid, 'tag-extra', 'intent', first.request_digest),
                        'out-of-window': (cid, 'tag', 'intent', cid)}.items():
        assert adm.authorize(*args) is False, label
    assert adm.authorize(cid, 'journal', 'intent', cid)
    for caller in [dict(actor='mallory'), dict(ref='refs/heads/feature')]:
        must_refuse('caller binding', F.PairAdmission, HubAPI(hub), world.selection, plan, stage='packages', publisher_sha=PUBLISHER, run_id=RUN,
                    **{'actor': OPERATOR, 'ref': 'refs/heads/main', **caller})
    # Stage windows and credentials: finalize holds no package credential, packages holds no release credential.
    full = F.plan_effects(E, world.selection, F.Candidate(world.directory, world.selection), {k: b'g' for k in F.GENERATED_ASSETS}, 'finalize')
    fin = F.PairAdmission(HubAPI(hub), world.selection, full, stage='finalize', publisher_sha=PUBLISHER, run_id=RUN, actor=OPERATOR, ref='refs/heads/main',
                          policy_reader=policy_reader(hub))
    for e in full[:4]:
        assert fin.authorize(cid, e.identity, 'intent', e.request_digest) is False and fin.authorize(cid, e.identity, 'dispatch', e.request_digest) is False
        assert fin.authorize(cid, e.identity, 'settle', e.request_digest) is True  # a verified package may still be settled by readback
    assert fin.authorize(cid, 'draft', 'intent', cid)
    for e in plan:
        assert adm.authorize(cid, e.identity, 'intent', e.request_digest)
    assert adm.authorize(cid, 'tag', 'intent', cid) is False
    count('admission')

    # Tag creation requires a fresh complete tag-protection policy at intent and at dispatch.
    for bad_policy in (True,):
        world = World(Path(tempfile.mkdtemp(dir=tmp)))
        hub = Hub(world)
        journal = MemoryJournal(F.content_id(world.selection))
        stage(world, hub, journal, 'packages')
        generated = finalize_inputs(world, hub)
        hub.policy_bad = True
        before = list(journal.cas)
        must_refuse('tag with weakened policy', stage, world, hub, journal, 'finalize', generated=generated)
        assert journal.cas == before and not hub.tags and not hub.releases
        # Causal: the same weakened policy with the policy gate removed creates the immutable tag.
        class NoPolicy(F.PairAdmission):
            def authorize(self, *args):
                self.policy_reader = lambda: policy_reader(Hub(world))()
                return super().authorize(*args)
        with neutralized(F, 'PairAdmission', NoPolicy):
            stage(world, hub, journal, 'finalize', generated=generated)
        assert hub.tags, 'control: without a fresh policy read the immutable tag is created under weakened protection'
    count('policy-gate')


def stage_window_controls(tmp):
    E = engine()
    C, P = IDS
    # finalize cannot start before the package window is complete
    world = World(tmp)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    generated = {k: b'g' + k.encode() for k in F.GENERATED_ASSETS}
    (world.tmp / 'assets').mkdir()
    for kind, raw in generated.items():
        (world.tmp / 'assets' / F.ASSET_NAMES[kind]).write_bytes(raw)
    must_refuse('finalize before packages', stage, world, hub, journal, 'finalize', generated=generated)
    assert journal.cas == [] and hub.pushes == []
    # Causal 1: without the prefix check, admission still refuses to originate a package send in finalize.
    with neutralized(F, 'prefix_verified', lambda *a, **k: None):
        must_refuse('finalize before packages (prefix check removed)', stage, world, hub, journal, 'finalize', generated=generated)
    assert journal.cas == [] and hub.pushes == [], 'admission credential window did not hold'
    # Causal 2: with both removed, an intent is written that the stage can never send: a stuck Unknown.
    class AllowAll:
        content_id = F.content_id(world.selection)

        def authorize(self, *args):
            return True

    candidate = F.Candidate(world.directory, world.selection)
    plan = F.plan_effects(E, world.selection, candidate, generated, 'finalize')
    provider = F.PairProvider(E, HubAPI(hub), world.selection, candidate, transport=HubTransport(hub), github_token='gh', runner=hub.push, allowed=F.sendable('finalize'),
                              generated=generated, asset_dir=world.tmp / 'assets')
    with neutralized(F, 'prefix_verified', lambda *a, **k: None):
        must_refuse('stuck intent', F.run_stage, E, 'finalize', F.content_id(world.selection), plan, journal, AllowAll(), provider, sleep=lambda _: None, clock=Clock(),
                    report=lambda m: None)
    assert journal.state.effects.get(f'github:{C}') == 'intent' and hub.pushes == [], 'control: intent persisted for an effect finalize cannot send'
    count('finalize-window')

    # A stale packages invocation after finalize began is refused by the engine (the journal holds effects it does not know).
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    stage(world, hub, journal, 'packages')
    journal.state = E.execution.JournalState(journal.state.generation + 1, journal.state.content_id, {**journal.state.effects, 'tag': 'intent'})
    pushes = list(hub.pushes)
    must_refuse('stale packages after finalize started', stage, world, hub, journal, 'packages')
    assert hub.pushes == pushes
    # An out-of-order journal is refused outright.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection), {f'github:{P}': 'verified'})
    must_refuse('out-of-order journal', stage, world, hub, journal, 'packages')
    assert hub.pushes == [] and journal.cas == []
    # A journal for another operation (content id) is refused.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal('sha256:' + '0' * 64)
    must_refuse('foreign journal', stage, world, hub, journal, 'packages')
    assert hub.pushes == []
    count('journal-order')


def readiness_controls(tmp):
    """Every send a stage may originate must be possible before its first durable intent."""
    C, P = IDS
    E = engine()
    # packages without the NuGet credential: refused before genesis-to-intent, nothing pushed, journal untouched.
    world = World(tmp)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    must_refuse('missing nuget credential', stage, world, hub, journal, 'packages', nuget_key='')
    assert journal.cas == [] and hub.pushes == [], 'a stage started without a credential it needs'
    # Causal: without the readiness check, github pushes verify and then the nuget effect becomes a permanent Unknown.
    with neutralized(F.PairProvider, 'ready', lambda self, effects: None):
        must_refuse('missing nuget credential (readiness removed)', stage, world, hub, journal, 'packages', nuget_key='')
    assert journal.state.effects[f'nuget:{C}'] == 'intent' and hub.pushes == [('github', C), ('github', P)], 'control: the stage wrote an intent it could never send'
    count('readiness-credential')
    # Retained bytes that changed on disk after authentication.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    plan = F.plan_effects(E, world.selection, world.candidate, None, 'packages')
    (world.directory / f'{P}.0.3.1.nupkg').write_bytes(b'swapped')
    must_refuse('swapped retained package', stage, world, hub, journal, 'packages', effects=plan)
    assert journal.cas == [] and hub.pushes == []
    # Finalize with an unstaged generated asset.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    stage(world, hub, journal, 'packages')
    generated = finalize_inputs(world, hub)
    (world.tmp / 'assets' / F.ASSET_NAMES['readback']).write_bytes(b'altered')
    before = list(journal.cas)
    must_refuse('altered staged asset', stage, world, hub, journal, 'finalize', generated=generated)
    assert journal.cas == before and not hub.tags
    count('readiness')


def observation_controls(tmp):
    C, P = IDS
    E = engine()
    world = World(tmp)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    # Pre-existing foreign bytes in a selected cell are never adopted, and nothing is sent after discovering them.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h = Hub(w)
    h.feeds['github'][C] = b'foreign'
    j = MemoryJournal(F.content_id(w.selection))
    must_refuse('preexisting github cell', stage, w, h, j, 'packages')
    assert h.pushes == [] and j.cas == []
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h = Hub(w)
    h.feeds['nuget'][C] = b'foreign'
    j = MemoryJournal(F.content_id(w.selection))
    must_refuse('preexisting nuget cell', stage, w, h, j, 'packages')
    assert h.pushes == [('github', C), ('github', P)] and f'nuget:{C}' not in j.state.effects, 'a foreign public cell must stop the pair before its first public push'
    # Feed exactness: GitHub is byte exact, nuget.org may add only its repository signature.
    cand = world.candidate
    raw = cand.bytes[C]
    assert F.feed_fact('github', cand.packages[C], raw, raw)['payloadMatch'] is True
    assert F.feed_fact('nuget', cand.packages[C], sign(raw), raw)['servedArchiveSha256'] == sha(sign(raw))
    must_refuse('github signed bytes differ', F.feed_fact, 'github', cand.packages[C], sign(raw), raw)
    must_refuse('nuget extra member', F.feed_fact, 'nuget', cand.packages[C], zip_bytes({**{'lib/net10.0/%s.dll' % C: b'dll-%s-0.3.1' % C.encode(), 'README.md': b'readme'}, 'extra': b'x'}), raw)
    must_refuse('nuget changed member', F.feed_fact, 'nuget', cand.packages[C], zip_bytes({'lib/net10.0/%s.dll' % C: b'different', 'README.md': b'readme'}), raw)
    must_refuse('nuget missing member', F.feed_fact, 'nuget', cand.packages[C], zip_bytes({'README.md': b'readme'}), raw)
    must_refuse('not a zip', F.feed_fact, 'nuget', cand.packages[C], b'not-a-zip', raw)
    must_refuse('traversal member', F.payload_map, zip_bytes({'../evil': b'x'}))
    dup = io.BytesIO()
    with zipfile.ZipFile(dup, 'w') as z:
        z.writestr('a', b'1')
        with patch.object(zipfile.ZipFile, '_writecheck', lambda self, zinfo: None):
            z.writestr('a', b'2')
    must_refuse('duplicate member', F.payload_map, dup.getvalue())
    count('feed-exactness')

    # After the identical retained write, a different served byte stream is a refusal, not a success.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h = Hub(w)
    sent = h.push

    def push_then_serve_other_bytes(argv):
        sent(argv)
        h.tamper[('github', C)] = b'x' + w.candidate.bytes[C]

    h.push = push_then_serve_other_bytes
    j = MemoryJournal(F.content_id(w.selection))
    must_refuse('tampered served bytes', stage, w, h, j, 'packages')
    assert j.state.effects.get(f'github:{C}') == 'intent' and len(h.pushes) == 1
    count('served-bytes-join')

    # Provider-level observations for tag/release/assets/promotion.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h = Hub(w)
    j = MemoryJournal(F.content_id(w.selection))
    stage(w, h, j, 'packages')
    generated = finalize_inputs(w, h)
    candidate = F.Candidate(w.directory, w.selection)
    plan = {e.identity: e for e in F.plan_effects(E, w.selection, candidate, generated, 'finalize')}
    provider = F.PairProvider(E, HubAPI(h), w.selection, candidate, transport=HubTransport(h), github_token='gh', generated=generated, asset_dir=w.tmp / 'assets')
    obs = lambda i: provider.observe(plan[i]).state
    assert obs('tag') == 'absent' and obs('draft') == 'absent' and obs('promote') == 'absent' and obs('asset:manifest') == 'absent'
    h.tags[w.selection['tag']] = '1' * 40
    assert obs('tag') == 'mismatched'
    h.tags[w.selection['tag']] = SOURCE
    assert obs('tag') == 'matched'
    h.releases.append({'tag_name': w.selection['tag'], 'draft': True, 'prerelease': False, 'target_commitish': SOURCE, 'body': 'foreign', 'id': 77})
    h.assets[77] = {}
    assert obs('draft') == 'mismatched'
    h.releases[0]['body'] = provider.marker
    assert obs('draft') == 'matched'
    for label, change in {'target': {'target_commitish': '1' * 40}, 'prerelease': {'prerelease': True}}.items():
        saved = {k: h.releases[0][k] for k in change}
        h.releases[0].update(change)
        assert obs('draft') == 'mismatched', label
        h.releases[0].update(saved)
    assert obs('asset:manifest') == 'absent'
    h.assets[77][F.ASSET_NAMES['manifest']] = (1, b'foreign')
    assert obs('asset:manifest') == 'mismatched'
    h.assets[77][F.ASSET_NAMES['manifest']] = (1, candidate.manifest_bytes)
    assert obs('asset:manifest') == 'matched'
    h.releases.append({'tag_name': w.selection['tag'], 'draft': True, 'id': 78})
    must_refuse('duplicate releases', obs, 'draft')
    h.releases.pop()
    # A published release with a missing asset is not a completed promotion.
    h.releases[0]['draft'] = False
    assert obs('promote') == 'mismatched'
    for name, raw in {F.ASSET_NAMES['consumer-qualification']: generated['consumer-qualification'], F.ASSET_NAMES['readback']: generated['readback'],
                      F.ASSET_NAMES['publication-journal']: generated['publication-journal'], f'{C}.0.3.1.nupkg': candidate.bytes[C],
                      f'{P}.0.3.1.nupkg': candidate.bytes[P]}.items():
        h.assets[77][name] = (hash(name) % 100000, raw)
    assert obs('promote') == 'matched'
    count('provider-observations')

    # Sends outside the stage credential window are impossible even if reached.
    closed = F.PairProvider(E, HubAPI(h), w.selection, candidate, transport=HubTransport(h), github_token='gh', runner=h.push, allowed=F.sendable('packages'))
    before = len(h.posts)
    assert closed.dispatch(plan['tag']).state == 'unknown' and len(h.posts) == before
    assert closed.last_dispatch_diagnostic == {'category': 'provider-refused'}
    nopush = F.PairProvider(E, HubAPI(h), w.selection, candidate, transport=HubTransport(h), github_token='gh', nuget_key='', runner=h.push, allowed=F.sendable('packages'))
    pushes = len(h.pushes)
    assert nopush.dispatch(plan['nuget:' + C]).state == 'unknown' and len(h.pushes) == pushes, 'push without its credential'
    count('credential-window')


def public_load_controls(tmp):
    C, P = IDS
    world = World(tmp)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    stage(world, hub, journal, 'packages')
    candidate = F.Candidate(world.directory, world.selection)
    n_before = len(hub.posts)

    def load(h=hub, **overrides):
        kwargs = dict(transport=HubTransport(h), verify_signature=lambda _: None, installed_check=installed_check(world), workdir=Path(tempfile.mkdtemp(dir=tmp)),
                      sleep=lambda _: None, clock=Clock())
        kwargs.update(overrides)
        return F.run_public_load(world.selection, candidate, **kwargs)

    receipt = load()
    value = F.validate_public_load(receipt, world.selection, candidate)
    assert [r['signature'] for r in value['packages']] == ['verified', 'verified'] and len(hub.posts) == n_before and journal.cas[-1][1] == 'verified'
    assert all(method == 'GET' for method, _ in hub.requests)
    unsigned = Hub(world)
    unsigned.sign_nuget = False
    stage_hub = unsigned
    stage(world, stage_hub, MemoryJournal(F.content_id(world.selection)), 'packages')
    assert [r['signature'] for r in F.validate_public_load(load(stage_hub), world.selection, candidate)['packages']] == ['absent', 'absent']
    called = []
    load(verify_signature=lambda p: called.append(p.name))
    assert len(called) == 2
    count('public-load-good')

    def bad_signature(path):
        raise F.Refused('repository signature verification failed')

    must_refuse('signature verification', load, verify_signature=bad_signature)
    tampered = Hub(world)
    tampered.feeds = {k: dict(v) for k, v in hub.feeds.items()}
    tampered.tamper[('nuget', P)] = sign(zip_bytes({'lib/net10.0/%s.dll' % P: b'other', 'README.md': b'readme'}))
    must_refuse('public bytes differ', load, tampered)
    absent = Hub(world)
    must_refuse('public package never visible', load, absent)

    def installed_fails(directory, mode, output):
        raise RuntimeError('public restore failed')

    must_refuse('installed load failure', load, installed_check=installed_fails, exceptions=(RuntimeError,))

    def wrong_runtime(directory, mode, output):
        installed_check(world)(directory, mode, output)
        value = json.loads(Path(output).read_bytes())
        value['packages'][0]['runtimeSha256'] = '0' * 64
        Path(output).write_bytes(json.dumps(value).encode())

    must_refuse('installed runtime differs from candidate', load, installed_check=wrong_runtime)
    for key, bad in {'publicOnly': False, 'producerProjectReferenced': True, 'packageInputsOnly': False, 'privateCachesUnavailable': False,
                     'effectiveProjectIsolationGuard': False, 'nativeFixtureInvoked': True, 'consumerAdoptionAccepted': True}.items():
        def weak(directory, mode, output, key=key, bad=bad):
            installed_check(world)(directory, mode, output)
            value = json.loads(Path(output).read_bytes())
            value[key] = bad
            Path(output).write_bytes(json.dumps(value).encode())
        must_refuse('installed claim ' + key, load, installed_check=weak)
    # receipt validation is bound to this operation and these packages
    value = json.loads(receipt)
    for label, mutate in {'content': lambda v: v.update(contentId='sha256:' + '0' * 64), 'operation': lambda v: v.update(operation='x'),
                          'source': lambda v: v.update(source='1' * 40), 'package': lambda v: v['packages'][0].update(candidateSha256='0' * 64),
                          'order': lambda v: v['packages'].reverse(), 'signature': lambda v: v['packages'][0].update(signature='forged'),
                          'extra': lambda v: v.update(x=1)}.items():
        broken = copy.deepcopy(value)
        mutate(broken)
        must_refuse('receipt ' + label, F.validate_public_load, F.canonical(broken), world.selection, candidate)
    count('public-load')

    # Finalization needs this run's successful public-load job and its authentic receipt artifact.
    hub.store_receipt(receipt)
    ok = F.fetch_public_load_receipt(HubTransport(hub), 'gh', world.selection, RUN, Path(tempfile.mkdtemp(dir=tmp)))
    assert ok == receipt
    for label, mutate in {'job-failed': lambda h: setattr(h, 'public_job', 'failure'), 'job-skipped': lambda h: setattr(h, 'public_job', 'skipped'),
                          'no-artifact': lambda h: setattr(h, 'receipt_archive', b''),
                          'archive-changed': lambda h: setattr(h, 'receipt_archive', zip_bytes({'public-load-receipt.json': receipt, 'extra': b'x'}))}.items():
        h = Hub(world)
        h.feeds = hub.feeds
        h.store_receipt(receipt)
        mutate(h)
        must_refuse('gate ' + label, F.fetch_public_load_receipt, HubTransport(h), 'gh', world.selection, RUN, Path(tempfile.mkdtemp(dir=tmp)))
    # Causal: finalization on a failed/absent public execution proceeds only when this gate is removed.
    h = Hub(world)
    h.feeds = hub.feeds
    h.public_job = 'failure'
    fabricated = receipt
    h.store_receipt(fabricated)
    must_refuse('failed public job gate', F.derive_finalize_inputs, HubTransport(h), 'gh', world.selection, candidate, RUN, Path(tempfile.mkdtemp(dir=tmp)))
    with neutralized(F, 'fetch_public_load_receipt', lambda *a, **k: fabricated):
        generated = F.derive_finalize_inputs(HubTransport(h), 'gh', world.selection, candidate, RUN, world.tmp)
    h.releases = []
    stage(world, h, journal, 'finalize', generated=generated)
    assert h.tags, 'control: without the native job/artifact gate the tag is created although public execution failed'
    count('public-load-gate')


# ---------------------------------------------------------------------------
# Workflow isolation, release script, harness and CLI


def workflow_jobs(text):
    body = text.split('\njobs:\n', 1)[1]
    parts = re.split(r'(?m)^  ([a-z][a-z-]*):\n', body)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


def permission_block(job):
    m = re.search(r'(?m)^    permissions:\n((?:      [a-z-]+: [a-z]+\n)+)', job)
    return dict(line.strip().split(': ') for line in m.group(1).splitlines()) if m else None


EXPECTED_PERMISSIONS = {'preflight': {'contents': 'read', 'actions': 'read', 'packages': 'read'},
                        'packages': {'contents': 'read', 'actions': 'read', 'packages': 'write', 'id-token': 'write'},
                        'public-load': {'contents': 'read', 'actions': 'read'},
                        'finalize': {'contents': 'write', 'actions': 'read', 'packages': 'read'}}


def workflow_findings(text):
    """Every finding is a closed statement about credential isolation or admission ordering."""
    findings = []
    head = text.split('\njobs:\n', 1)[0]
    if not re.search(r'(?m)^on:\n  workflow_dispatch:\n', head) or re.search(r'(?m)^  (push|pull_request|schedule|workflow_run|workflow_call):', head):
        findings.append('trigger-not-manual')
    if '\npermissions:\n  contents: read\n' not in head:
        findings.append('workflow-default-permissions')
    if 'cancel-in-progress: false' not in head:
        findings.append('concurrency')
    live = '\n'.join(line for line in text.splitlines() if not line.lstrip().startswith('#'))
    if re.search(r'continue-on-error|retry|attempt: *[2-9]|max-attempts|rerun|--skip-duplicate', live, re.I):
        findings.append('retry-or-tolerance')
    for ref in re.findall(r'(?m)^\s+(?:- )?uses: (\S+)', text):
        if not re.fullmatch(r'[\w.-]+/[\w./-]+@[0-9a-f]{40}', ref):
            findings.append('unpinned-action:' + ref)
    if re.search(r'execution/v0\.3\.0|publish-execution-pair-030|original38495729817|v0\.3\.0-publisher', live):
        findings.append('touches-original-operation')
    jobs = workflow_jobs(text)
    if set(jobs) != set(EXPECTED_PERMISSIONS):
        findings.append('job-set')
    for name, job in jobs.items():
        if permission_block(job) != EXPECTED_PERMISSIONS.get(name):
            findings.append('permissions:' + name)
        has_env = re.search(r'(?m)^    environment: execution-pair-forward$', job) is not None
        if has_env != (name != 'public-load'):
            findings.append('environment:' + name)
        if name == 'public-load' and re.search(r'secrets\.|vars\.|NUGET|create-github-app-token|id-token', job):
            findings.append('public-load-has-credentials')
        if name != 'packages' and re.search(r'NuGet/login|NUGET_API_KEY|id-token: write|packages: write', job):
            findings.append('nuget-or-package-write-outside-packages:' + name)
        if name != 'finalize' and re.search(r'(?m)^      contents: write$', job):
            findings.append('contents-write-outside-finalize:' + name)
        gate = job.find('eng/execution-pair-forward.py state')
        first_credential = min([i for i in (job.find('create-github-app-token'), job.find('NuGet/login'), job.find('secrets.')) if i >= 0] or [10 ** 9])
        if name != 'public-load' and not (0 <= gate < first_credential):
            findings.append('credential-before-admission-gate:' + name)
        if name == 'public-load' and gate < 0:
            findings.append('missing-admission-gate:' + name)
        if name == 'packages' and 'ORDINARY_LEDGER_TOKEN' not in job:
            findings.append('ledger-missing')
        if name in ('preflight', 'packages', 'finalize') and 'run-attempt' in job:
            findings.append('attempt-override')
    for number, line in enumerate(live.splitlines(), 1):
        if re.search(r'\bpython3\b', line) and not re.search(r'python3 -I -B\b', line):
            findings.append(f'python-not-hermetic:{number}')
    if ('needs: preflight\n' not in jobs.get('packages', '') or 'needs: [preflight, packages]\n' not in jobs.get('public-load', '')
            or 'needs: [preflight, public-load]\n' not in jobs.get('finalize', '')):
        findings.append('stage-order')
    if "needs.packages.result == 'skipped'" not in jobs.get('public-load', '') or "needs.preflight.outputs.plan == 'packages'" not in jobs.get('packages', ''):
        findings.append('resume-plan-gating')
    if 'permission-administration: read' not in jobs.get('preflight', '') or 'permission-administration: write' in text:
        findings.append('policy-token-scope')
    return findings


def workflow_controls():
    text = (ROOT / '.github/workflows/execution-pair-forward-publish.yml').read_text()
    assert workflow_findings(text) == [], workflow_findings(text)
    count('workflow-clean')
    mutations = {
        'permissions:packages': lambda t: t.replace('      packages: write\n      id-token: write\n', '      packages: write\n      id-token: write\n      contents: write\n', 1),
        'nuget-or-package-write-outside-packages:finalize': lambda t: t.replace('    permissions:\n      contents: write\n      actions: read\n      packages: read\n', '    permissions:\n      contents: write\n      actions: read\n      packages: write\n'),
        'public-load-has-credentials': lambda t: t.replace('GH_TOKEN: ${{ github.token }}\n        run: |\n          set -euo pipefail\n          python3 -I -B eng/execution-pair-forward.py public-load', 'GH_TOKEN: ${{ github.token }}\n          X: ${{ secrets.FSGG_DISPATCH_APP_ID }}\n        run: |\n          set -euo pipefail\n          python3 -I -B eng/execution-pair-forward.py public-load'),
        'environment:finalize': lambda t: t.replace('    needs: [preflight, public-load]\n    runs-on: ubuntu-24.04\n    environment: execution-pair-forward\n', '    needs: [preflight, public-load]\n    runs-on: ubuntu-24.04\n'),
        'credential-before-admission-gate:packages': lambda t: t.replace(
            '      - name: Require the enabled admission and exact publisher binding before any credential exists\n        id: admitted\n',
            '      - name: Exchange early\n        uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841\n        with:\n          user: x\n'
            '      - name: Require the enabled admission and exact publisher binding before any credential exists\n        id: admitted\n', 1),
        'retry-or-tolerance': lambda t: t.replace('    timeout-minutes: 75\n    permissions:\n      contents: read\n      actions: read\n      packages: write', '    continue-on-error: true\n    timeout-minutes: 75\n    permissions:\n      contents: read\n      actions: read\n      packages: write', 1),
        'unpinned-action:actions/checkout@v4': lambda t: t.replace('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'actions/checkout@v4', 1),
        'trigger-not-manual': lambda t: t.replace('on:\n  workflow_dispatch:', 'on:\n  push:\n  workflow_dispatch:', 1),
        'touches-original-operation': lambda t: t.replace("PYTHONOPTIMIZE: '0'", "PYTHONOPTIMIZE: '0'\n      ORIGINAL: execution/v0.3.0", 1),
        'stage-order': lambda t: t.replace('    needs: [preflight, public-load]\n', '    needs: preflight\n', 1),
        'python-not-hermetic': lambda t: t.replace('python3 -I -B eng/execution-pair-forward.py public-load', 'python3 eng/execution-pair-forward.py public-load', 1),
        'resume-plan-gating': lambda t: t.replace("needs.preflight.outputs.plan == 'packages' && github.ref", "github.ref", 1),
        'policy-token-scope': lambda t: t.replace('permission-administration: read', 'permission-administration: write', 1),
        'permissions:preflight': lambda t: t.replace('      packages: read\n    env:\n      PYTHONOPTIMIZE', '      packages: write\n    env:\n      PYTHONOPTIMIZE', 1),
    }
    for expected, mutate in mutations.items():
        mutated = mutate(text)
        assert mutated != text, expected
        found = workflow_findings(mutated)
        assert any(f == expected or f.startswith(expected.split(':')[0]) for f in found), (expected, found)
        count('workflow-mutations')
    # the admission gate genuinely precedes every credential inside the real file
    jobs = workflow_jobs(text)
    for name in ('preflight', 'packages', 'finalize'):
        job = jobs[name]
        assert job.index('execution-pair-forward.py state') < job.index('create-github-app-token')
    assert jobs['packages'].index('execution-pair-forward.py state') < jobs['packages'].index('NuGet/login')
    # The prepare workflow keeps the original 0.3.0 literal path and adds only the forward profile selection.
    prepare = (ROOT / '.github/workflows/callable-cli-release-prepare.yml').read_text()
    assert "'0.3.0',os.environ['SOURCE_A']" not in prepare and prepare.count("original_pair(transfer/'candidate.zip',os.environ['ARCHIVE_SHA'],os.environ['CANDIDATE_VERSION'],os.environ['SOURCE_A'])") == 4
    assert prepare.count('--profile "$PAIR_PROFILE"') == 4 and prepare.count('PAIR_PROFILE=execution-pair-forward') == 1 and prepare.count('profile=execution-pair-forward') == 1
    assert prepare.count('if test "$CANDIDATE_VERSION" = 0.3.0;') == 2 and 'test "$SOURCE_A" = "$GITHUB_SHA"; fi' in prepare
    assert prepare.count('CANDIDATE_VERSION: ${{ inputs.version }}') >= 7
    assert 'nuget push' not in prepare and 'gh release' not in prepare
    # The immutable execution-pin inventory covers both changed workflows byte for byte.
    pins = {d['path']: d for d in json.loads((ROOT / 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json').read_bytes())['documents']}
    for path in ('.github/workflows/execution-pair-forward-publish.yml', '.github/workflows/callable-cli-release-prepare.yml'):
        assert pins[path]['sha256'] == sha((ROOT / path).read_bytes()), f'execution-pin inventory is stale for {path}'
        used = set(re.findall(r'(?m)^\s*(?:-\s*)?uses:\s*([^\s#]+)', (ROOT / path).read_text()))
        assert set(pins[path]['externalReferences']) == used
    count('prepare-workflow')


def release_script_controls(tmp):
    dotnet = shutil.which('dotnet')
    assert dotnet, 'dotnet is required for the release-script controls'
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    head = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()

    def check(profile, version):
        return subprocess.run([dotnet, 'fsi', 'eng/callable-cli-release.fsx', 'check-profile', '--profile', profile, '--version', version,
                               '--source', head, '--output', str(Path(tmp) / 'unused')], cwd=ROOT, env=env, capture_output=True, text=True, timeout=300)

    for version in ('0.3.1', '0.3.10', '0.4.0', '1.0.0'):
        r = check('execution-pair-forward', version)
        assert r.returncode == 0 and 'EXECUTION_PAIR_PROFILE_CHECKED' in r.stdout, (version, r.stderr)
        count('release-script-forward-accepts')
    for version in ('0.3.0', '0.2.9', '0.3.01', '0.3.1-rc1', ''):
        r = check('execution-pair-forward', version)
        assert r.returncode != 0 and 'CALLABLE_CLI_RELEASE_REFUSED' in r.stderr, (version, r.stdout)
        count('release-script-forward-refuses')
    r = check('execution-pair', '0.3.0')
    assert r.returncode == 0, 'the original profile must keep accepting exactly 0.3.0'
    for version in ('0.3.1', '0.4.0'):
        r = check('execution-pair', version)
        assert r.returncode != 0, 'the original profile must not accept a forward version'
        count('release-script-original-unchanged')
    dirty = subprocess.run(['git', 'status', '--porcelain'], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    if dirty:
        assert not os.environ.get('FSGG_REQUIRE_CLEAN_CANDIDATE'), 'the protected gate requires a clean committed candidate'
        COUNTS['candidate-build-skipped-dirty-tree'] = 1
        return
    output = Path(tmp) / 'forward-candidate'
    run = lambda command: subprocess.run([dotnet, 'fsi', 'eng/callable-cli-release.fsx', command, '--profile', 'execution-pair-forward', '--version', '0.3.1',
                                          '--source', head, '--output', str(output)], cwd=ROOT, env=env, capture_output=True, text=True, timeout=900)
    prepared = run('prepare')
    assert prepared.returncode == 0 and 'EXECUTION_PAIR_RELEASE_PREPARED' in prepared.stdout, prepared.stderr[-2000:]
    assert run('verify').returncode == 0
    manifest = json.loads((output / F.ASSET_NAMES['manifest']).read_bytes())
    assert manifest['profile'] == 'execution-pair' and manifest['version'] == '0.3.1' and manifest['tag'] == 'execution/v0.3.1' and manifest['sourceCommit'] == head
    # The real candidate satisfies the consumer-side authentication unchanged.
    selection = {'version': '0.3.1', 'tag': 'execution/v0.3.1', 'source': head, 'tree': manifest['sourceTree'],
                 'candidate': {'manifestSha256': sha((output / F.ASSET_NAMES['manifest']).read_bytes()), 'packages': {r['packageId']: r['sha256'] for r in manifest['packages']}}}
    real = F.Candidate(output, selection)
    assert set(real.payloads) == set(IDS)
    for id in IDS:
        with zipfile.ZipFile(output / f'{id}.0.3.1.nupkg') as z:
            nuspec = z.read(id + '.nuspec').decode()
        assert '<version>0.3.1</version>' in nuspec
        if id == IDS[1]:
            assert f'id="{IDS[0]}" version="0.3.1"' in nuspec, 'coherent Contracts dependency'
    # Reproducible: a second preparation of the same source and version has identical bytes.
    second = Path(tmp) / 'forward-candidate-2'
    again = subprocess.run([dotnet, 'fsi', 'eng/callable-cli-release.fsx', 'prepare', '--profile', 'execution-pair-forward', '--version', '0.3.1',
                            '--source', head, '--output', str(second)], cwd=ROOT, env=env, capture_output=True, text=True, timeout=900)
    assert again.returncode == 0 and all((output / p.name).read_bytes() == p.read_bytes() for p in second.iterdir())
    # A different forward version yields different bytes under the same source.
    count('real-candidate')


def harness_controls():
    for version in ('0.3.1', '1.2.0'):
        for mode in ('candidate', 'public'):
            LEGACY['installed_isolation_diagnostic_controls'](version, mode)
            count('installed-isolation-forward')


def cli_controls(tmp):
    cli = ROOT / 'eng/execution-pair-forward.py'
    env = {k: v for k, v in os.environ.items() if not k.startswith('GITHUB_') and k not in ('OPERATION', 'GH_TOKEN', 'ORDINARY_LEDGER_TOKEN', 'NUGET_API_KEY', 'POLICY_TOKEN', 'PYTHONOPTIMIZE')}
    env['PYTHONDONTWRITEBYTECODE'] = '1'

    def call(*args, flags=(), env_extra=None, admission=None):
        extra = ['--admission', str(admission)] if admission else []
        return subprocess.run([sys.executable, *flags, str(cli), *args, *extra], cwd=ROOT, env={**env, **(env_extra or {})}, capture_output=True, text=True, timeout=120)

    r = call('state')
    assert r.returncode == 0 and r.stdout.strip() == 'disabled'
    optimized = call('state', flags=('-O',))
    assert optimized.returncode == 1 and 'optimize=0' in optimized.stderr
    # Causal: under -O the legacy asserts would be stripped, so the entry point itself must refuse.
    assert sys.flags.optimize == 0
    for command in ('preflight', 'packages', 'public-load', 'finalize', 'bind'):
        work = Path(tmp) / ('work-' + command)
        r = call(command, '--workdir', str(work))
        assert r.returncode == 1 and 'refused' in r.stderr and 'disabled' in r.stderr, (command, r.stderr)
        assert not any(work.glob('*')) if work.exists() else True, 'a disabled route created state'
        count('cli-disabled-refusals')
    # selected (reviewable) admission: preflight is reachable but live modes are not
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    admission = enabled_admission(world)
    selected_path, enabled_path = Path(tmp) / 'selected.json', Path(tmp) / 'enabled.json'
    chosen = copy.deepcopy(admission)
    chosen['enabled'] = False
    selected_path.write_text(json.dumps(chosen))
    enabled_path.write_text(json.dumps(admission))
    assert call('state', admission=selected_path).stdout.strip() == 'selected' and call('state', admission=enabled_path).stdout.strip() == 'enabled'
    for command in ('packages', 'finalize'):
        r = call(command, '--workdir', str(Path(tmp) / ('sel-' + command)), admission=selected_path)
        assert r.returncode == 1 and 'not admitted' in r.stderr
    # enabled but not inside the publisher context: refused before any network, credential or helper fetch
    for label, extra in {'event': {'GITHUB_EVENT_NAME': 'push'}, 'repo': {'GITHUB_REPOSITORY': 'FS-GG/other'}, 'ref': {'GITHUB_REF': 'refs/heads/x'},
                         'attempt': {'GITHUB_RUN_ATTEMPT': '2'}, 'actor': {'GITHUB_ACTOR': 'mallory'}, 'operation': {'OPERATION': 'publish-execution-pair-forward-x'}}.items():
        good = {'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REPOSITORY': F.REPOSITORY, 'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1',
                'GITHUB_ACTOR': OPERATOR, 'GITHUB_SHA': PUBLISHER, 'GITHUB_RUN_ID': str(RUN), 'OPERATION': world.selection['operation']}
        r = call('bind', '--workdir', str(Path(tmp) / ('ctx-' + label)), env_extra={**good, **extra}, admission=enabled_path)
        assert r.returncode == 1 and 'refused' in r.stderr and 'ancestor' not in r.stderr, (label, r.stderr)
        count('cli-context-refusals')
    observed = Path(tmp) / 'observed.json'
    observed.write_text(json.dumps({'versions': ['0.2.0', '0.3.1'], 'tags': ['execution/v0.2.0']}))
    r = call('select-version', '--observed', str(observed))
    assert r.returncode == 0 and r.stdout.strip() == '0.3.2'
    observed.write_text(json.dumps({'versions': ['not-a-version']}))
    assert call('select-version', '--observed', str(observed)).returncode == 1
    count('cli')
    # publication delta: only the admission may differ between candidate A and publisher B
    sel = world.selection
    F.bind_publisher(sel, PUBLISHER, [F.ADMISSION_PATH], ancestor=True, tree=TREE)
    F.bind_publisher(sel, PUBLISHER, [], ancestor=True, tree=TREE)
    # Unrelated repository history between A and B does not strand a partly published pair.
    F.bind_publisher(sel, PUBLISHER, [F.ADMISSION_PATH, 'src/FS.GG.Coordination.Cli/Program.fs', 'docs/other.md', 'eng/execution-pair-release-admission.json'], ancestor=True, tree=TREE)
    for label, kwargs in {'not-ancestor': dict(paths=[], ancestor=False, tree=TREE), 'tree': dict(paths=[], ancestor=True, tree='c' * 40),
                          **{f'closure-{path}': dict(paths=[F.ADMISSION_PATH, path], ancestor=True, tree=TREE) for path in sorted(F.PUBLISHER_CLOSURE - {F.ADMISSION_PATH})}}.items():
        must_refuse('publication delta ' + label, F.bind_publisher, sel, PUBLISHER, kwargs['paths'], ancestor=kwargs['ancestor'], tree=kwargs['tree'])
    count('publication-delta')


def cli_module():
    spec_ = importlib.util.spec_from_file_location('execution_pair_forward_cli', ROOT / 'eng/execution-pair-forward.py')
    module = importlib.util.module_from_spec(spec_)
    spec_.loader.exec_module(module)
    return module


class Authority:
    """The protected journal route as the CLI sees it: destination class, main head, genesis and a shared journal."""

    def __init__(self):
        self.journal, self.intent, self.main = None, None, 'a' * 40

    def handle(self):
        authority = self
        E = engine()

        class Handle:
            def initialize(self, intent):
                assert authority.journal is None
                authority.intent, authority.journal = dict(intent), MemoryJournal(intent['contentId'])
                return authority.journal.read()

            def read(self):
                return authority.journal.read()

            def compare_and_swap(self, expected, effect, state):
                return authority.journal.compare_and_swap(expected, effect, state)

            def validate_intent(self, intent):
                if authority.intent != intent:
                    raise E.journal.Refused('journal intent differs')

        return Handle()


@contextlib.contextmanager
def cli_world(world, hub, authority, tmp, *, bind=None):
    """Run the real entry point with only the network, the GitHub Apps and the ledger replaced."""
    E, CLI = engine(), cli_module()
    admission_path = Path(tmp) / 'enabled-admission.json'
    admission_path.write_text(json.dumps(enabled_admission(world)))
    context = {'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REPOSITORY': F.REPOSITORY, 'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1',
               'GITHUB_ACTOR': OPERATOR, 'GITHUB_SHA': PUBLISHER, 'GITHUB_RUN_ID': str(RUN), 'OPERATION': world.selection['operation'], 'GH_TOKEN': 'gh',
               'ORDINARY_LEDGER_TOKEN': 'ledger', 'NUGET_API_KEY': 'nuget', 'POLICY_TOKEN': 'policy', 'FSGG_SHARED_HELPER_ROOT': str(E.helper_root)}

    class Gate:
        def __init__(self, publisher, api, ref):
            self.publisher = publisher

        def authorize(self, *args):
            return self.publisher.authorize(*args)

    with patch.dict(os.environ, context), \
         patch.object(F, 'UrllibTransport', lambda: HubTransport(hub)), \
         patch.object(F, 'load_engine', lambda root, pin: E), \
         patch.object(F, 'make_api_class', lambda engine_: (lambda token: HubAPI(hub))), \
         patch.object(F, 'read_policy', lambda api: policy_reader(hub)()), \
         patch.object(E.route, 'classify_journal_destination', lambda api, ref: (authority.journal is None, authority.main)), \
         patch.object(E.route, 'authority_main', lambda api: authority.main), \
         patch.object(E.route, 'ProtectedPublisherAdmission', Gate), \
         patch.object(E.journal, 'ProtectedReleaseJournal', lambda api, ref, main_directory: authority.handle()), \
         patch.object(CLI, 'bind_publisher', bind or (lambda selection, sha: None)), \
         patch.object(CLI, 'push_runner', lambda argv: hub.push(argv)), \
         patch.object(CLI, 'verify_signature', lambda path: None), \
         patch.object(CLI, 'installed_check', installed_check(world)), \
         patch.object(F.PairProvider, 'download_feed', F.PairProvider.download_feed), \
         patch.object(time_module, 'sleep', lambda s: None):
        yield lambda *argv: run_cli(CLI, [*argv, '--admission', str(admission_path)])


import time as time_module


def run_cli(CLI, argv):
    err = io.StringIO()
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
        code = CLI.main(argv)
    return code, err.getvalue()


def cli_wiring_controls(tmp):
    C, P = IDS
    world = World(tmp)
    hub, authority = Hub(world), Authority()
    with cli_world(world, hub, authority, tmp) as cli:
        # Dormant until every stage is explicitly entered; the first stage creates genesis exactly once.
        code, err = cli('packages', '--workdir', str(Path(tmp) / 'w-packages'))
        assert code == 0, err
        assert authority.intent == F.journal_intent(world.selection) and authority.journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS[:4]}
        assert hub.pushes == [('github', C), ('github', P), ('nuget', C), ('nuget', P)]
        code, err = cli('finalize', '--workdir', str(Path(tmp) / 'w-early-finalize'))
        assert code == 1 and 'public-load receipt' in err and not hub.tags, 'finalize must wait for the public-load gate'
        receipt = Path(tmp) / 'w-public' / 'public-load-receipt.json'
        code, err = cli('public-load', '--workdir', str(Path(tmp) / 'w-public'))
        assert code == 0, err
        hub.store_receipt(receipt.read_bytes())
        code, err = cli('finalize', '--workdir', str(Path(tmp) / 'w-finalize'))
        assert code == 0, err
        assert authority.journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS} and hub.releases[0]['draft'] is False
        # A repeated package stage on the advanced journal is refused and sends nothing.
        pushes = list(hub.pushes)
        code, err = cli('packages', '--workdir', str(Path(tmp) / 'w-again'))
        assert code == 1 and hub.pushes == pushes
        # finalize needs an existing journal.
        code, err = cli('finalize', '--workdir', str(Path(tmp) / 'w-again-final'))
        assert code == 0, 'a complete journal is idempotent for its own stage'
        count('cli-wiring-good')
    # Genesis is the last pre-send write: any pre-genesis fault leaves no journal and no send.
    cases = {
        'populated-cell': lambda h: h.feeds['nuget'].update({C: b'foreign'}),
        'tag-exists': lambda h: h.tags.update({'execution/v0.3.1': SOURCE}),
        'higher-version': lambda h: h.extra_versions.update({('github', C): ['0.9.0'], ('github', P): ['0.9.0']}),
        'main-moved': lambda h: setattr(h, 'main', '1' * 40),
        'rerun': lambda h: setattr(h, 'run_attempt', 2),
    }
    for label, mutate in cases.items():
        w = World(Path(tempfile.mkdtemp(dir=tmp)))
        h, a = Hub(w), Authority()
        mutate(h)
        with cli_world(w, h, a, Path(tempfile.mkdtemp(dir=tmp))) as cli:
            code, err = cli('packages', '--workdir', str(Path(tempfile.mkdtemp(dir=tmp)) / 'w'))
        assert code == 1 and a.journal is None and h.pushes == [], (label, err)
        count('cli-pregenesis-refusals')
    # Receivers are re-authenticated by the first irreversible job itself.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    (w.receivers / 'sdd-s1.zip').write_bytes((w.receivers / 'sdd-s1.zip').read_bytes() + b'x')
    h, a = Hub(w), Authority()
    with cli_world(w, h, a, Path(tempfile.mkdtemp(dir=tmp))) as cli:
        code, err = cli('packages', '--workdir', str(Path(tempfile.mkdtemp(dir=tmp)) / 'w'))
        assert code == 1 and a.journal is None and h.pushes == [], err
        # Causal: without that receiver join the same corrupted evidence lets the pair publish.
        with neutralized(F, 'acquire_receivers', lambda *args, **kwargs: {}):
            code, err = cli('packages', '--workdir', str(Path(tempfile.mkdtemp(dir=tmp)) / 'w2'))
        assert code == 0 and len(h.pushes) == 4, err
    count('cli-receiver-gate')
    # Finalize without a journal is refused; it never creates genesis.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h, a = Hub(w), Authority()
    h.store_receipt(b'{}')
    with cli_world(w, h, a, Path(tempfile.mkdtemp(dir=tmp))) as cli:
        code, err = cli('finalize', '--workdir', str(Path(tempfile.mkdtemp(dir=tmp)) / 'w'))
    assert code == 1 and a.journal is None and not h.posts
    # A journal bound to different intent is refused before any send.
    w = World(Path(tempfile.mkdtemp(dir=tmp)))
    h, a = Hub(w), Authority()
    a.intent = dict(F.journal_intent(w.selection), operator='someone')
    a.journal = MemoryJournal(F.content_id(w.selection))
    with cli_world(w, h, a, Path(tempfile.mkdtemp(dir=tmp))) as cli:
        code, err = cli('packages', '--workdir', str(Path(tempfile.mkdtemp(dir=tmp)) / 'w'))
    assert code == 1 and h.pushes == []
    count('cli-wiring')


def publisher_binding_controls(tmp):
    """The real git-backed binding: candidate A may differ from publisher B only by the admission."""
    CLI = cli_module()
    repo = Path(tmp) / 'repo'
    repo.mkdir()
    git = lambda *a: subprocess.run(['git', '-c', 'user.name=t', '-c', 'user.email=t@t', '-c', 'commit.gpgsign=false', *a], cwd=repo, check=True, capture_output=True, text=True).stdout.strip()
    git('init', '-q', '-b', 'main')
    (repo / 'eng').mkdir()
    (repo / 'eng/execution-pair-forward-admission.json').write_text('{"a":1}\n')
    (repo / 'eng/execution_pair_forward.py').write_text('# code\n')
    git('add', '-A')
    git('commit', '-q', '-m', 'a')
    a = git('rev-parse', 'HEAD')
    tree = git('rev-parse', 'HEAD^{tree}')
    sel = {'source': a, 'tree': tree}
    old = os.getcwd()
    os.chdir(repo)
    try:
        CLI.bind_publisher(sel, a)  # A == B
        (repo / 'eng/execution-pair-forward-admission.json').write_text('{"a":2}\n')
        git('commit', '-q', '-am', 'admission')
        b = git('rev-parse', 'HEAD')
        CLI.bind_publisher(sel, b)
        count('binding-admission-only')
        (repo / 'eng/execution_pair_forward.py').write_text('# changed\n')
        git('commit', '-q', '-am', 'code')
        c = git('rev-parse', 'HEAD')
        must_refuse('publisher code differs from candidate source', CLI.bind_publisher, sel, c)
        git('checkout', '-q', '--orphan', 'other')
        git('commit', '-q', '--allow-empty', '-m', 'unrelated')
        unrelated = git('rev-parse', 'HEAD')
        must_refuse('candidate is not an ancestor', CLI.bind_publisher, sel, unrelated)
        must_refuse('tree differs', CLI.bind_publisher, dict(sel, tree='c' * 40), b)
        # Causal: without the closure rule the changed publisher code is accepted.
        with patch.object(F, 'publisher_surface', lambda path: path == F.ADMISSION_PATH):
            CLI.bind_publisher(sel, c)
    finally:
        os.chdir(old)
    count('publisher-binding')


def closure_controls():
    """The publisher closure is exactly what the publisher reads from the repository."""
    sources = {name: (ROOT / name).read_text() for name in ('eng/execution_pair_forward.py', 'eng/execution-pair-forward.py')}
    read = set()
    for text in sources.values():
        read |= {m for m in re.findall(r'(?:ROOT|ENG|HERE) / "([A-Za-z0-9_./-]+)"', text)}
        read |= set(re.findall(r'"(eng/[A-Za-z0-9_./-]+)"', text))
    read = {('eng/' + r if '/' not in r and r.endswith(('.py', '.json')) else r) for r in read}
    read.discard('eng/')
    assert 'eng/test-callable-cli-installed-harness.py' in read and 'eng/write-callable-cli-release-readback.py' in read and 'eng/repository-settings/release-tag-ruleset.json' in read
    stray = {r for r in read if r not in F.PUBLISHER_CLOSURE and not r.startswith(('eng/execution-pair-release', 'eng/callable-cli-release.fsx'))}
    assert not stray, f'the publisher reads repository files outside its closure: {sorted(stray)}'
    # The workflow executes only closure files and the checked-out global.json (the harness copies it).
    workflow = (ROOT / F.PUBLISH_WORKFLOW).read_text()
    referenced = set(re.findall(r'\b(eng/[A-Za-z0-9_./-]+\.(?:py|json|fsx))\b', workflow))
    assert referenced <= F.PUBLISHER_CLOSURE, sorted(referenced - F.PUBLISHER_CLOSURE)
    assert 'global.json' in workflow
    # Every closure member exists and the original-route admission is not an input of the publisher.
    assert all((ROOT / path).is_file() for path in F.PUBLISHER_CLOSURE)
    assert 'eng/execution-pair-release-admission.json' not in F.PUBLISHER_CLOSURE and 'eng/execution-pair-release-admission.json' not in read
    # Python or dotnet resolving a name from the checkout can never be a free change.
    for path in ('eng/zipfile.py', 'json.py', 'subprocess.py', 'eng/sitecustomize.py', 'x.pth', 'NuGet.Config', 'sub/nuget.config', 'Directory.Build.props',
                 'tools/Directory.Build.targets', 'global.json', 'eng/runpy.py', 'src/x/app.config'):
        assert F.publisher_surface(path), path
        assert not F.publication_delta_allowed([F.ADMISSION_PATH, path]), path
        count('surface-refused')
    for path in ('docs/a.md', 'src/FS.GG.Coordination.Cli/Program.fs', 'tests/x/test.py', 'specs/a.json', 'eng/some-script.sh', 'eng/execution-pair-release-admission.json'):
        assert not F.publisher_surface(path) and F.publication_delta_allowed([F.ADMISSION_PATH, path]), path
    # Causal: the stdlib shadow runs under plain python3 and does not under the hermetic invocation.
    scratch = Path(tempfile.mkdtemp())
    (scratch / 'eng').mkdir()
    for name in ('eng/execution_pair_forward.py', 'eng/execution-pair-forward.py', 'eng/test-callable-cli-installed-harness.py', 'eng/write-callable-cli-release-readback.py',
                 F.ADMISSION_PATH):
        shutil.copyfile(ROOT / name, scratch / name)
    (scratch / 'eng/zipfile.py').write_text('print("SHADOW-RAN")\nraise SystemExit(0)\n')
    clean_env = {k: v for k, v in os.environ.items() if not k.startswith(('PYTHON', 'GITHUB_'))}
    plain = subprocess.run([sys.executable, 'eng/execution-pair-forward.py', 'state'], cwd=scratch, env=clean_env, capture_output=True, text=True)
    assert 'SHADOW-RAN' in plain.stdout, 'control: plain python3 lets a module in eng/ shadow the standard library'
    hermetic = subprocess.run([sys.executable, '-I', '-B', 'eng/execution-pair-forward.py', 'state'], cwd=scratch, env=clean_env, capture_output=True, text=True)
    assert hermetic.stdout.strip() == 'disabled' and 'SHADOW' not in hermetic.stdout, hermetic.stderr
    (scratch / 'json.py').write_text('print("ROOT-SHADOW-RAN")\n')
    inline = subprocess.run([sys.executable, '-I', '-B', '-'], input='import json\nprint("inline-ok")\n', cwd=scratch, env=clean_env, capture_output=True, text=True)
    assert inline.stdout.strip() == 'inline-ok'
    count('hermetic-python')
    # dotnet runs from a clean directory outside the checkout.
    CLI = cli_module()
    seen = []
    with patch.object(CLI.subprocess, 'run', lambda *a, **k: (seen.append(k.get('cwd')), subprocess.CompletedProcess(a[0], 0, '', ''))[1]):
        CLI.push_runner(['dotnet', 'nuget', 'push', 'x'])
        CLI.verify_signature(Path('x'))
    assert len(seen) == 2 and all(c and not Path(c).resolve().is_relative_to(ROOT) for c in seen), seen
    count('dotnet-clean-cwd')
    # Causal: a publisher that starts reading a file outside the closure is found by this scan.
    probe = 'x = ROOT / "eng/not-in-closure.py"'
    found = set(re.findall(r'(?:ROOT|ENG|HERE) / "([A-Za-z0-9_./-]+)"', probe))
    assert found == {'eng/not-in-closure.py'} and not found <= F.PUBLISHER_CLOSURE
    count('publisher-closure')


def resume_controls(tmp):
    C, P = IDS
    E = engine()
    cid = 'sha256:' + '1' * 64
    st = lambda eff: E.execution.JournalState(2, cid, eff)
    assert F.resume_plan(st({})) == 'packages'
    assert F.resume_plan(st({f'github:{C}': 'verified', f'github:{P}': 'intent'})) == 'packages'
    pk = {e: 'verified' for e in F.EFFECT_IDS[:4]}
    assert F.resume_plan(st(pk)) == 'finalize'
    assert F.resume_plan(st({**pk, 'tag': 'intent'})) == 'finalize'
    assert F.resume_plan(st({e: 'verified' for e in F.EFFECT_IDS})) == 'complete'
    must_refuse('foreign journal effect', F.resume_plan, st({'other': 'verified'}))
    must_refuse('finalize effect before packages complete', F.resume_plan, st({f'github:{C}': 'verified', 'tag': 'verified'}))
    # The workflow is the only consumer of the plan: each stage runs exactly when the plan names it.
    world = World(tmp)
    hub, authority = Hub(world), Authority()
    plans = []

    def plan_of(receipt_dir):
        return json.loads((Path(receipt_dir) / 'preflight-receipt.json').read_bytes())['plan']

    with cli_world(world, hub, authority, tmp) as cli:
        # Fresh: only the non-resume preflight is accepted and it plans the package stage.
        code, err = cli('preflight', '--workdir', str(Path(tmp) / 'p0'))
        assert code == 0 and plan_of(Path(tmp) / 'p0') == 'packages', err
        code, err = cli('preflight', '--resume', '--workdir', str(Path(tmp) / 'p0r'))
        assert code == 1 and 'existing forward journal' in err
        # Interrupted mid-package-window (a process death after two pushes).
        original = hub.push
        seen = []

        def die_after_two(argv):
            original(argv)
            seen.append(argv)
            if len(seen) == 2:
                raise Boom()

        hub.push = die_after_two
        try:
            cli('packages', '--workdir', str(Path(tmp) / 'k1'))
        except Boom:
            pass
        hub.push = original
        assert authority.journal is not None and len(hub.pushes) == 2
        # A plain preflight now refuses (journal begun, cells populated); the resume preflight continues it.
        code, err = cli('preflight', '--workdir', str(Path(tmp) / 'p1'))
        assert code == 1
        code, err = cli('preflight', '--resume', '--workdir', str(Path(tmp) / 'p1r'))
        assert code == 0 and plan_of(Path(tmp) / 'p1r') == 'packages', err
        code, err = cli('packages', '--workdir', str(Path(tmp) / 'k2'))
        assert code == 0 and hub.pushes == [('github', C), ('github', P), ('nuget', C), ('nuget', P)], err
        code, err = cli('preflight', '--resume', '--workdir', str(Path(tmp) / 'p2r'))
        assert code == 0 and plan_of(Path(tmp) / 'p2r') == 'finalize', err
        # The next dispatch reads the plan, re-runs public-load and finalizes.
        code, err = cli('public-load', '--workdir', str(Path(tmp) / 'k3'))
        assert code == 0
        hub.store_receipt((Path(tmp) / 'k3' / 'public-load-receipt.json').read_bytes())
        code, err = cli('finalize', '--workdir', str(Path(tmp) / 'k4'))
        assert code == 0 and authority.journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS}, err
        code, err = cli('preflight', '--resume', '--workdir', str(Path(tmp) / 'p3r'))
        assert code == 0 and plan_of(Path(tmp) / 'p3r') == 'complete'
        # Causal: a non-resuming preflight on a begun journal is exactly what stranded the route before.
        assert cli('preflight', '--workdir', str(Path(tmp) / 'p4'))[0] == 1
    # Unrelated main history does not strand the continuation (closure rule), the publisher still must be on main.
    count('resume')


def release_dispatch_reads_nothing_controls(tmp):
    """A send is never preceded by a remote read that could fail after the intent was recorded."""
    world = World(tmp)
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    stage(world, hub, journal, 'packages')
    generated = finalize_inputs(world, hub)
    # Reads of the release list fail once the promote intent exists; the send has to survive that.
    hub.release_read_guard = lambda: ('promote', 'intent') in journal.cas and ('promote', 'verified') not in journal.cas
    must_refuse('observation outage after the promote send', stage, world, hub, journal, 'finalize', generated=generated)
    assert hub.releases[0]['draft'] is False, 'the promote send must not depend on a read made after its intent'
    posts = list(hub.posts)
    hub.release_read_guard = None
    stage(world, hub, journal, 'finalize', generated=generated)
    assert journal.state.effects == {e: 'verified' for e in F.EFFECT_IDS} and hub.posts == posts, 'recovery settles by observation without resending'
    # Same for an asset upload: the draft observed in the same transition is the only input.
    world = World(Path(tempfile.mkdtemp(dir=tmp)))
    hub = Hub(world)
    journal = MemoryJournal(F.content_id(world.selection))
    stage(world, hub, journal, 'packages')
    generated = finalize_inputs(world, hub)
    hub.release_read_guard = lambda: ('asset:FS.GG.Execution.Contracts', 'intent') in journal.cas and ('asset:FS.GG.Execution.Contracts', 'verified') not in journal.cas
    must_refuse('observation outage after an asset send', stage, world, hub, journal, 'finalize', generated=generated)
    assert F.asset_name('asset:' + IDS[0], '0.3.1') in hub.assets[hub.releases[0]['id']]
    hub.release_read_guard = None
    stage(world, hub, journal, 'finalize', generated=generated)
    count('dispatch-reads-nothing')


def env_injection_controls(tmp):
    """The prepare workflow validates the version before it writes it to the environment file."""
    prepare = (ROOT / '.github/workflows/callable-cli-release-prepare.yml').read_text()
    job = prepare.split('  execution_pair_installed:\n', 1)[1]
    gate = job.split('          set -euo pipefail\n', 1)[1].split('      - name: Run actual shared pair guards', 1)[0]
    gate = 'set -euo pipefail\n' + '\n'.join(line[10:] if line.startswith('          ') else line for line in gate.splitlines())
    stub = Path(tmp) / 'bin'
    stub.mkdir()
    (stub / 'git').write_text("#!/usr/bin/python3\nimport os,sys\na=sys.argv[1:]\nif a[0]=='rev-parse':print(os.environ['GITHUB_SHA'])\nif a[0]=='diff':pass\n")
    (stub / 'git').chmod(0o700)

    def run(version, *, source=None, script=gate):
        out = Path(tempfile.mkdtemp(dir=tmp))
        env = {**os.environ, 'PATH': f'{stub}{os.pathsep}{os.environ["PATH"]}', 'MODE': 'execution-pair-installed', 'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ATTEMPT': '1',
               'GITHUB_REPOSITORY': F.REPOSITORY, 'GITHUB_SHA': '9' * 40, 'EXECUTOR_SOURCE': '9' * 40, 'SOURCE_A': source or '9' * 40, 'CANDIDATE_RUN': '1',
               'CANDIDATE_ARTIFACT': '3', 'ARCHIVE_SHA': 'c' * 64, 'CANDIDATE_VERSION': version, 'CANDIDATE_OUTPUT': str(out / 'candidate'), 'READBACK_OUTPUT': str(out / 'readback'),
               'GITHUB_ENV': str(out / 'env')}
        result = subprocess.run(['bash', '-c', script], cwd=ROOT, env=env, capture_output=True, text=True)
        return result.returncode, (out / 'env').read_text() if (out / 'env').exists() else ''

    code, env_file = run('0.3.1')
    assert code == 0 and 'PAIR_PROFILE=execution-pair-forward' in env_file and 'CANDIDATE_VERSION=0.3.1' in env_file
    code, env_file = run('0.3.0', source='9' * 40)
    assert code == 0 and 'PAIR_PROFILE=execution-pair\n' in env_file
    for bad in ('0.3.1\nBASH_ENV=/tmp/evil', '0.3.1 ', '0.3.1\n', '0.3', '0.3.1-rc1', '$(id)', '0.2.0', ''):
        code, env_file = run(bad)
        assert code != 0 and env_file == '', repr(bad)
        count('env-injection-refused')
    # Forward candidates run at one exact source.
    code, env_file = run('0.3.1', source='8' * 40)
    assert code != 0 and env_file == ''
    # Causal: remove the version validation and the injected line reaches the environment file.
    unguarded = '\n'.join(line for line in gate.splitlines() if 'forward_version_allowed' not in line and 'The version is written' not in line)
    assert unguarded != gate
    code, env_file = run('0.3.1\nBASH_ENV=/tmp/evil', script=unguarded)
    assert 'BASH_ENV=/tmp/evil' in env_file, 'control: without validation the version injects an environment variable'
    count('env-injection')


def api_class_controls(tmp):
    E = engine()
    API = F.make_api_class(E)
    seen = []

    class Recorder(API):
        def _request(self, url, method='GET', body=None, content_type='application/json'):
            seen.append((url, method, content_type, body))
            return b'{}'

    api = Recorder('token')
    path = Path(tmp) / 'asset.bin'
    path.write_bytes(b'bytes')
    api.upload_asset(5, 'asset.bin', path, sha(b'bytes'))
    assert seen[-1][0].startswith(f'https://uploads.github.com/repos/{F.REPOSITORY}/releases/5/assets?') and seen[-1][3] == b'bytes'
    must_refuse('upload without bound digest', api.upload_asset, 5, 'asset.bin', path)
    must_refuse('upload with changed bytes', api.upload_asset, 5, 'asset.bin', path, '0' * 64)
    # Causal: the shared helper's own upload targets FS-GG/.github, so reuse needs the repository-parametrized subclass.
    seen.clear()

    class Shared(E.provider.GitHubAPI):
        def _request(self, url, method='GET', body=None, content_type='application/json'):
            seen.append(url)
            return b'{}'

    Shared('token').upload_asset(5, 'asset.bin', path)
    assert seen and '/repos/FS-GG/.github/' in seen[-1] and F.REPOSITORY not in seen[-1]
    count('api-class')


def original_route_controls():
    """The forward route neither reads nor rewrites any original 0.3.0 identity."""
    source = (ROOT / 'eng/execution_pair_forward.py').read_text() + (ROOT / 'eng/execution-pair-forward.py').read_text()
    assert 'execution-pair-release-admission.json' not in source
    forward_workflow = (ROOT / '.github/workflows/execution-pair-forward-publish.yml').read_text()
    assert 'execution-pair-release-admission' not in forward_workflow and 'callable-cli-release-publish' not in forward_workflow
    count('original-untouched')


def main():
    version_controls()
    committed_state_controls()
    with tempfile.TemporaryDirectory(prefix='forward-pair-') as scratch:
        tmp = Path(scratch)

        def fresh():
            return Path(tempfile.mkdtemp(dir=tmp))

        selection_controls(fresh())
        helper_pin_controls(fresh())
        effect_plan_controls(fresh())
        occupancy_controls(fresh())
        policy_controls(fresh())
        acquisition_controls(fresh())
        preflight_controls(fresh())
        api_class_controls(fresh())
        closure_controls()
        resume_controls(fresh())
        release_dispatch_reads_nothing_controls(fresh())
        env_injection_controls(fresh())
        cli_wiring_controls(fresh())
        publisher_binding_controls(fresh())
        fence_controls(fresh())
        end_to_end_controls(fresh())
        recovery_controls(fresh())
        admission_controls(fresh())
        stage_window_controls(fresh())
        readiness_controls(fresh())
        observation_controls(fresh())
        public_load_controls(fresh())
        workflow_controls()
        harness_controls()
        original_route_controls()
        cli_controls(fresh())
        release_script_controls(fresh())
    print('EXECUTION_PAIR_FORWARD_INERT_CONTROLS passed ' + ' '.join(f'{k}={v}' for k, v in sorted(COUNTS.items())) + ' no-native-effects')


if __name__ == '__main__':
    main()
