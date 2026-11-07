#!/usr/bin/env python3
"""Actual fresh SDK caller with injected native/transport/clock facts; no credentials/effects."""
from __future__ import annotations
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
import time
from unittest.mock import patch
import zipfile

ROOT=Path(__file__).resolve().parents[3]
spec=importlib.util.spec_from_file_location('sdk_entry',ROOT/'eng/ordinary-sdk-publisher.py')
entry=importlib.util.module_from_spec(spec);spec.loader.exec_module(entry)
P=entry.P
PIN=json.loads((ROOT/P.ADMISSION).read_bytes())['sharedHelper']
if not os.environ.get('FSGG_SHARED_HELPER_ROOT'):
    raise SystemExit('Set FSGG_SHARED_HELPER_ROOT to the verified pinned engine tree; controls never download or install it')
HELPER=Path(os.environ['FSGG_SHARED_HELPER_ROOT'])
ENGINE=P.F.load_engine(HELPER,PIN)


def archive(rows):
    b=io.BytesIO()
    with zipfile.ZipFile(b,'w') as z:
        for name,raw in rows.items():z.writestr(name,raw)
    return b.getvalue()


def fixture():
    package=archive({'tools/net10.0/any/FS.GG.Coordination.Cli.dll':b'synthetic1124',
                     P.PACKAGE+'.nuspec':('<package><metadata><id>'+P.PACKAGE+'</id><version>'+P.VERSION+'</version><repository url="https://github.com/'+P.REPO+'" commit="'+'a'*40+'" /></metadata></package>').encode()})
    manifest=P.canonical(dict(schema='fsgg.coordination.callable-cli-release-preparation/1',packageId=P.PACKAGE,version=P.VERSION,tag='v'+P.VERSION,sourceCommit='a'*40,sourceTree='b'*40,packageSha256=P.sha(package),publicationAuthorized=False,tagAuthorized=False))
    raw=archive({P.PACKAGE_FILE:package,P.MANIFEST:manifest})
    s=dict(operation='publish-sdk-profile-040-'+'a'*12,version=P.VERSION,source='a'*40,tree='b'*40,protectedMerge='a'*40,
           candidate=dict(runId=100,artifactId=101,archiveSha256=P.sha(raw),manifestSha256=P.sha(manifest),packageSha256=P.sha(package)),
           journal={'id':'sdk-profile-040-'+'a'*12},operator='EHotwagner',nugetUser='FixtureOnly',
           publisherFiles={n:P.sha((ROOT/n).read_bytes()) for n in P.B_PATHS-{P.ADMISSION}},
           liveEvidence={n:{'reference':'injected1124fixture','evidenceSha256':'f'*64} for n in ('ledger','namespace','tagProtection','trustedPublishing','operationAdmission')})
    desired=json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_bytes());desired['id']=21633398
    s['liveEvidence']['tagProtection']['evidenceSha256']=P.sha(P.canonical(desired))
    return s,package,manifest,raw


# Actual shapes measured 2026-11-07: GET rulesets/21633398 (update parameter omitted, viewer
# metadata present) and `dotnet nuget verify --all -v normal` (SDK 10.0.403) on served 0.3.0.
LIVE_TAG_RULESET=dict(id=21633398,name='release-tags-immutable',target='tag',source_type='Repository',source='FS-GG/FS.GG.Coordination',enforcement='active',
                      conditions={'ref_name':{'exclude':[],'include':['refs/tags/v*','refs/tags/execution/v*']}},
                      rules=[{'type':'deletion'},{'type':'non_fast_forward'},{'type':'required_signatures'},{'type':'update'}],
                      node_id='RRS_fixture',created_at='2026-08-27T09:36:56.365+02:00',updated_at='2026-10-12T13:19:45.228+02:00',
                      bypass_actors=[],current_user_can_bypass='never',_links={'self':{'href':'fixture'},'html':{'href':'fixture'}})
VERIFY_NORMAL=(b"Verifying FS.GG.Coordination.Cli.0.3.0\n\nSignature Hash Algorithm: SHA256\nSignature type: Repository\n"
               b"Service index: https://api.nuget.org/v3/index.json\nOwners: Paradigma11\n"
               b"Verifying the repository primary signature with certificate: \n"
               b"  Subject Name: CN=NuGet.org Repository by Microsoft, O=NuGet.org Repository by Microsoft, L=Redmond, S=Washington, C=US\n"
               b"Successfully verified package 'FS.GG.Coordination.Cli.0.3.0'.\n")
VERIFY_DEFAULT=(b"Verifying FS.GG.Coordination.Cli.0.3.0\nSignature type: Repository\n"
                b"Verifying the repository primary signature with certificate: \n"
                b"Successfully verified package 'FS.GG.Coordination.Cli.0.3.0'.\n")


class Journal:
    state=None; writes=[]; fail_cas=False; lose_cas=False
    def __init__(self,*args,**kw):pass
    def initialize(self,intent):
        P.require(Journal.state is None,'journal already exists')
        Journal.state=ENGINE.execution.JournalState(1,intent['contentId'],{})
    def validate_intent(self,intent):P.require(Journal.state.content_id==intent['contentId'],'original content differs')
    def read(self):return copy.deepcopy(Journal.state)
    def compare_and_swap(self,before,effect,state):
        if Journal.fail_cas:return False
        P.require(before==Journal.state,'CAS parent differs')
        Journal.state=ENGINE.execution.JournalState(before.generation+1,before.content_id,{**before.effects,effect:state})
        Journal.writes.append((effect,state))
        if Journal.lose_cas:raise RuntimeError('injected CAS reply loss')
        return True


class World:
    def __init__(self,s,package,manifest,raw):
        self.s,self.package,self.manifest,self.raw=s,package,manifest,raw
        self.feeds={};self.sends=[];self.gets=[];self.tokens=[];self.tags=None;self.release=None;self.assets={}
        self.run_id=999;self.run_heads={999:'c'*40};self.main='c'*40;self.main_reads=0;self.stale_at=None;self.journal_change_at=None;self.lost_send=False;self.reject_send=False
        self.overrides={};self.hide_packages=False;self.publics={};self.lost_feed=None;self.lost_asset=None
        self.foreign_releases=[];self.selftest=archive({'selftest.nuspec':b'<package/>','.signature.p7s':b'synthetic-public-signature'});self.verify_output=VERIFY_NORMAL
    def get(self,path):
        self.gets.append(path)
        if path in self.overrides:return copy.deepcopy(self.overrides[path])
        if path=='repos/'+P.REPO:return {'id':P.REPO_ID,'full_name':P.REPO}
        if path==f'repos/{P.REPO}/git/ref/heads/main':
            self.main_reads+=1
            if self.journal_change_at==self.main_reads and Journal.state is not None:
                before=Journal.state;Journal.state=ENGINE.execution.JournalState(before.generation+1,before.content_id,dict(before.effects))
            return {'object':{'sha':self.main if self.stale_at is None or self.main_reads<self.stale_at else 'd'*40}}
        if path==f'repos/{P.REPO}/actions/runs/100':return dict(id=100,repository={'id':P.REPO_ID},head_repository={'id':P.REPO_ID},path=P.PREPARE,event='workflow_dispatch',head_branch='main',head_sha='a'*40,run_attempt=1,conclusion='success')
        if path==f'repos/{P.REPO}/actions/runs/100/attempts/1/jobs?per_page=100':return {'total_count':1,'jobs':[dict(name='Prepare the strict-ten-gate CLI profile repair without publication',conclusion='success',head_sha='a'*40,run_attempt=1)]}
        if path==f'repos/{P.REPO}/actions/artifacts/101':return dict(id=101,expired=False,name='callable-cli-profile-repair-'+'a'*40,digest='sha256:'+P.sha(self.raw),workflow_run={'id':100,'head_sha':'a'*40})
        if path.startswith(f'repos/{P.REPO}/actions/runs/'):
            run=int(path.split('/runs/')[1].split('/')[0])
            if path.endswith('/attempts/1/jobs?per_page=100'):return {'total_count':1,'jobs':[dict(name='sdk_profile_public_load',conclusion='success',head_sha=self.run_heads.get(run,'c'*40),run_attempt=1)]}
            if path.endswith('/artifacts?per_page=100'):
                return {'total_count':1,'artifacts':[dict(id=1000+run,name='ordinary-sdk-public-load-'+str(run),expired=False,digest='sha256:'+P.sha(self.publics.get(run)),workflow_run={'id':run,'head_sha':self.run_heads.get(run,'c'*40)})]}
            return dict(id=run,repository={'id':P.REPO_ID},path=P.WORKFLOW,event='workflow_dispatch',head_branch='main',head_sha=self.run_heads.get(run,'c'*40),run_attempt=1,actor={'login':'EHotwagner'},status='in_progress' if run==self.run_id else 'completed')
        if path=='orgs/FS-GG/packages/nuget/fs.gg.coordination.cli':return {'name':P.PACKAGE.lower(),'package_type':'nuget','repository':{'id':P.REPO_ID}}
        if path.startswith('orgs/FS-GG/packages/nuget/'):return []
        if path==f'repos/{P.REPO}/git/ref/tags/v{P.VERSION}':
            if self.tags is None:raise ENGINE.provider.NotFound('fixture absent')
            return self.tags
        if path==f'repos/{P.REPO}/releases/tags/v{P.VERSION}':
            # Live behaviour: the by-tag endpoint 404s for a draft (.github#3578).
            if self.release is None or self.release.get('draft') is not False:raise ENGINE.provider.NotFound('fixture absent')
            return self.release
        if path==f'repos/{P.REPO}/releases?per_page=100&page=1':
            return [*self.foreign_releases,*([self.release] if self.release is not None else [])]
        if path.endswith('/releases/1/assets?per_page=100'):return [{'id':i,'name':n} for i,(n,_) in enumerate(self.assets.items(),1)]
        if path.endswith('/rulesets/21633398'):return copy.deepcopy(LIVE_TAG_RULESET)
        raise AssertionError('unexpected native fixture request '+path)
    def transport(self,url,*,headers=None,cap=None,**kw):
        if url.endswith('/artifacts/101/zip'):
            assert headers['Accept']=='application/vnd.github+json'
            return 200,self.raw
        if '/actions/artifacts/' in url:
            artifact=int(url.rsplit('/',2)[1]);return 200,self.publics.get(artifact-1000)
        if url.endswith('/fs.gg.coordination.cli/index.json'):return 200,P.canonical({'versions':['0.3.0']})
        if url==P.F.feed_url('nuget',P.PACKAGE,'0.3.0'):return 200,self.selftest
        for feed in ('github','nuget'):
            if url==P.F.feed_url(feed,P.PACKAGE,P.VERSION):
                return (200,self.feeds[feed]) if feed in self.feeds and not self.hide_packages else (404,b'')
        raise AssertionError('unexpected transport fixture request '+url)
    def execute(self,argv,root,**kw):
        if argv[:3]==['dotnet','nuget','verify']:
            assert argv[-3:]==['--all','-v','normal'],argv
            return types.SimpleNamespace(stdout=self.verify_output,stderr=b'')
        if argv[:3]==['dotnet','tool','install']:
            installed=Path(argv[argv.index('--tool-path')+1]);package_root=installed/'.store'/P.PACKAGE.lower()/P.VERSION/P.PACKAGE.lower()/P.VERSION
            with zipfile.ZipFile(io.BytesIO(self.package)) as z:
                for name in z.namelist():
                    if name.startswith('tools/'):
                        path=package_root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(z.read(name))
            return types.SimpleNamespace(stdout=b'',stderr=b'')
        if argv[0]=='python3':return types.SimpleNamespace(stdout=b'ORDINARY_SDK_PROFILE_FIXTURES passed=15 nativeEffects=0 githubTokenAbsent=true syntheticKeysOnly=true\n',stderr=b'')
        assert argv[:3]==['dotnet','nuget','push'],argv
        feed='github' if 'pkg.github.com' in argv[argv.index('--source')+1] else 'nuget'
        expected=P.effect_id(P.runtime_selection(self.s,{'sha':self.run_heads[self.run_id],'runId':self.run_id}),feed)
        assert Journal.state.effects.get(expected)=='intent','send occurred before durable original intent'
        assert expected.endswith('/run-'+str(self.run_id)+'/publisher-'+self.run_heads[self.run_id])
        self.sends.append(feed)
        if self.reject_send:raise subprocess.CalledProcessError(1,argv)
        if self.lost_send or self.lost_feed==feed:raise subprocess.TimeoutExpired(argv,1)
        with zipfile.ZipFile(io.BytesIO(self.package)) as z:members={n:z.read(n) for n in z.namelist()}
        self.feeds[feed]=self.package if feed=='github' else archive({**members,'.signature.p7s':b'synthetic-signature'})
        return types.SimpleNamespace(stdout=b'',stderr=b'')
    def api(self,token):
        self.tokens.append(token);world=self
        class API:
            get=staticmethod(world.get)
            def post(self,path,body):
                world.sends.append(path)
                if path.endswith('/git/refs'):world.tags={'object':{'type':'commit','sha':body['sha'],'url':'fixture'}}
                else:world.release={'id':1,**body}
                return {}
            def patch(self,path,body):world.sends.append(path);world.release.update(body);return {}
            def upload_asset(self,rid,name,path,digest):
                raw=path.read_bytes();assert P.sha(raw)==digest;world.assets[name]=raw;world.sends.append('asset:'+name)
                if world.lost_asset==name:raise RuntimeError('injected asset response loss')
            def download_asset(self,asset):return list(world.assets.values())[asset-1]
        return API()


class Controls(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='sdk-B-control-');self.root=Path(self.temp.name)
        self.s,self.package,self.manifest,self.raw=fixture();self.w=World(self.s,self.package,self.manifest,self.raw)
        Journal.state=None;Journal.writes=[];Journal.fail_cas=False;Journal.lose_cas=False
        self.advance_clock=None;self.forward=None;self.path=self.root/'admission.json';self.save()
        self.env=dict(PATH=os.environ['PATH'],FSGG_SHARED_HELPER_ROOT=str(HELPER),GITHUB_REPOSITORY=P.REPO,GITHUB_EVENT_NAME='workflow_dispatch',GITHUB_REF='refs/heads/main',GITHUB_RUN_ATTEMPT='1',GITHUB_ACTOR='EHotwagner',CLI_PROFILE_REPAIR='true',GITHUB_SHA='c'*40,GITHUB_RUN_ID='999',OPERATION=self.s['operation'],REQUESTED_SOURCE='a'*40,REQUESTED_MERGE='a'*40,REQUESTED_PUBLISHER='c'*40,REQUESTED_SHA256=self.s['candidate']['packageSha256'],GH_TOKEN='inert-read-fixture',ORDINARY_LEDGER_TOKEN='inert-ledger-fixture',NUGET_API_KEY='inert-nuget-fixture',POLICY_TOKEN='inert-policy-fixture')
    def tearDown(self):self.temp.cleanup()
    def save(self):self.path.write_bytes(P.canonical(dict(schema='fsgg.coordination.ordinary-sdk-profile-admission/1',enabled=True,selection=self.s,sharedHelper=PIN,forward=self.forward)))
    def git(self,*args):
        if args==('rev-parse','HEAD'):return self.env['GITHUB_SHA']
        if args[0]=='rev-parse':return 'b'*40
        if args[0]=='merge-base':return 'a'*40
        if args[0]=='status':return ''
        if args[0]=='diff':return '\n'.join(P.B_PATHS)
        raise AssertionError(args)
    def invoke(self,command='packages',original_run=None):
        if command=='observe':
            self.env['SDK_PROFILE_MODE']='observe';self.env['SDK_ORIGINAL_RUN']=str(original_run)
        binding=P.bind;advancing=P.advance
        with patch.dict(os.environ,self.env,clear=True),patch.object(P,'bind',side_effect=lambda s,e:binding(s,e,git=self.git)), \
             patch.object(P,'bounded_api',return_value=self.w.api),patch.object(P.F,'UrllibTransport',return_value=types.SimpleNamespace(get=self.w.transport)), \
             patch.object(ENGINE.journal,'ProtectedReleaseJournal',Journal),patch.object(ENGINE.route,'classify_journal_destination',return_value=(Journal.state is None,'d'*40)), \
             patch.object(ENGINE.route,'authority_main',return_value='d'*40),patch.object(ENGINE.route,'ProtectedPublisherAdmission',side_effect=lambda gate,*a:gate), \
             patch.object(P.F,'load_engine',return_value=ENGINE),patch.object(entry,'execute',side_effect=self.w.execute),patch.object(entry,'PUBLIC_SELFTEST_SHA256',P.sha(self.w.selftest)),patch.object(P,'advance',side_effect=lambda *a,**kw:advancing(*a,**kw,sleep=lambda _:None,**({'clock':self.advance_clock} if self.advance_clock else {}))):
            entry.run(command,self.root/('work-'+command+'-'+str(len(list(self.root.iterdir())))),admission_path=self.path,original_run=original_run)
    def test_actual_workflow_orders_inputs_before_credentials_and_isolates_public_load(self):
        import yaml
        workflow=yaml.safe_load((ROOT/P.WORKFLOW).read_text());jobs=workflow['jobs']
        expected={'sdk_profile_packages':['sdk_profile_preflight'],'sdk_profile_public_load':['sdk_profile_packages'],
                  'sdk_profile_finalize':['sdk_profile_preflight','sdk_profile_packages','sdk_profile_public_load']}
        for name,needs in expected.items():self.assertEqual(jobs[name]['needs'],needs)
        for name in ('preflight','publish','cli_successor','execution_pair_preflight','execution_pair'):
            self.assertTrue(jobs[name]['if'].endswith(' && !inputs.cli_profile_repair'))
        for name in ('sdk_profile_preflight',*expected):
            steps=jobs[name]['steps'];binding=next(i for i,v in enumerate(steps) if v.get('run')=='python3 -I -B eng/ordinary-sdk-publisher.py bind')
            credential_steps=[i for i,v in enumerate(steps) if 'secrets.' in str(v) or 'NuGet/login@' in str(v)]
            self.assertTrue(all(binding<i for i in credential_steps))
            self.assertNotIn('always()',jobs[name]['if'])
        self.assertEqual(jobs['sdk_profile_public_load']['permissions'],{'contents':'read','actions':'read'})
        self.assertNotIn('secrets.',str(jobs['sdk_profile_public_load']))
        self.assertNotIn('NUGET_API_KEY',str(jobs['sdk_profile_finalize']))
        self.assertNotIn('contents: write',str(jobs['sdk_profile_packages']))
    def test_actual_ledger_credentials_resolve_only_in_existing_protected_environment(self):
        # The ledger App id/key/installation exist only as execution-pair-forward environment
        # values; a job without that environment would mint with empty inputs after the lock.
        import yaml
        jobs=yaml.safe_load((ROOT/P.WORKFLOW).read_text())['jobs']
        ledger=[n for n,j in jobs.items() if n.startswith('sdk_profile_') and 'FSGG_ORDINARY_LEDGER' in str(j)]
        self.assertEqual(sorted(ledger),['sdk_profile_finalize','sdk_profile_observe','sdk_profile_packages'])
        for name,job in jobs.items():
            if not name.startswith('sdk_profile_'):continue
            with self.subTest(job=name):
                if name in ledger:self.assertEqual(job.get('environment'),'execution-pair-forward')
                else:
                    self.assertNotIn('environment',job);self.assertNotIn('secrets.',str(job))
    def test_actual_writer_jobs_take_the_lock_immediately_after_checkout(self):
        # ADR-0097 coverage: only checkout may precede the gate, and every credential-minting
        # job is a marked participant. Admission/binding still precedes every credential.
        import yaml
        jobs=yaml.safe_load((ROOT/P.WORKFLOW).read_text())['jobs']
        for name in ('sdk_profile_packages','sdk_profile_finalize','sdk_profile_observe'):
            with self.subTest(job=name):
                steps=jobs[name]['steps'];self.assertIn('[org-publication-writer]',jobs[name]['name'])
                self.assertTrue(steps[0]['uses'].startswith('actions/checkout@'))
                self.assertEqual(steps[1]['name'],'Acquire org publication writer lock')
                self.assertEqual(steps[2]['run'],'python3 -I -B eng/ordinary-sdk-publisher.py bind')
                self.assertEqual([i for i,v in enumerate(steps) if v.get('name')=='Acquire org publication writer lock'],[1])
                self.assertGreater(jobs[name]['timeout-minutes'],int(steps[1]['env']['LOCK_MAX_WAIT_MINUTES']))
        for name in ('sdk_profile_preflight','sdk_profile_public_load'):
            self.assertNotIn('[org-publication-writer]',jobs[name]['name'])
            self.assertNotIn('create-github-app-token',str(jobs[name]));self.assertNotIn('NuGet/login',str(jobs[name]))
    def test_publication_delta_admits_exactly_the_actual_A_to_B_population(self):
        actual=['.github/workflows/callable-cli-release-publish.yml','docs/atlas/catalog.json',
                'docs/atlas/records/'+'a'*64+'.json','eng/bootstrap-gates/compiler-and-tests.sh',
                'eng/ordinary-sdk-profile-admission.json','eng/ordinary-sdk-publisher.py','eng/ordinary_sdk_publisher.py',
                'eng/run-ordinary-sdk-publisher-controls.py','evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json',
                'tests/portable-workspace/release/test_execution_pair_forward.py','tests/portable-workspace/release/test_execution_pair_release.py',
                'tests/portable-workspace/release/test_ordinary_sdk_publisher.py','tests/portable-workspace/release/test_ordinary_sdk_publisher_runner.py',
                'tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs','tests/FS.GG.Coordination.Orchestration.Host.Tests/OrdinaryAssignmentPolicyTests.fs']
        self.assertTrue(P.publication_delta(actual))
        # When protected A is in the checkout, the real delta (not this typed list) must bind too.
        probe=subprocess.run(['git','diff','--name-only','--no-renames','965775d2b7e9df0b225490429936496f4e8fb97e','HEAD'],cwd=ROOT,capture_output=True,text=True)
        if probe.returncode==0:
            self.assertEqual(set(probe.stdout.split())-{p for p in probe.stdout.split() if p.startswith('docs/atlas/records/')},
                             {p for p in actual if not p.startswith('docs/atlas/records/')})
            self.assertTrue(P.publication_delta(probe.stdout.split()))
        for outside in ('src/FS.GG.Coordination.Cli/InstalledOrdinarySettlementProvider.fs','eng/execution_pair_forward.py',
                        '.github/workflows/callable-cli-release-prepare.yml','eng/execution-pair-release-admission.json',
                        'tests/portable-workspace/release/test_cli_profile_repair.py','docs/atlas/records/x.json'):
            with self.subTest(path=outside):self.assertFalse(P.publication_delta([*actual,outside]))
    def pinned_lock(self):
        import yaml
        jobs=yaml.safe_load((ROOT/P.WORKFLOW).read_text())['jobs']
        path=HELPER/'scripts/publication_writer_lock.py'
        self.assertTrue(path.is_file(),'Place the exact workflow-pinned lock helper in FSGG_SHARED_HELPER_ROOT/scripts/publication_writer_lock.py; controls never download it')
        for name in ('sdk_profile_packages','sdk_profile_finalize','sdk_profile_observe'):
            commands=[v for v in jobs[name]['steps'] if 'LOCK_SCRIPT_SHA256' in v.get('env',{})]
            self.assertEqual(len(commands),1)
            self.assertEqual(commands[0]['env']['LOCK_COMMIT'],'c799a9f9b21d52eb9766cf0950722e3e55145d39')
            self.assertEqual(P.sha(path.read_bytes()),commands[0]['env']['LOCK_SCRIPT_SHA256'])
        spec=importlib.util.spec_from_file_location('sdk_pinned_publication_lock',path)
        lock=importlib.util.module_from_spec(spec);spec.loader.exec_module(lock)
        return lock,jobs
    def lock_native_job(self,lock,job,*,job_id=700,runner='injected-writer',started='2026-01-01T00:00:00Z'):
        # The pinned helper checks the native marker; this adapter also joins the
        # actual YAML step to the helper/registry's canonical gate-step contract.
        gates=[v for v in job['steps'] if v.get('name')==lock.GATE_STEP]
        self.assertEqual(len(gates),1,'actual YAML lacks its unique canonical publication gate step')
        self.assertIn('"$lock" acquire',gates[0]['run'])
        return dict(id=job_id,name=job['name'],status='in_progress',runner_name=runner,started_at=started,
                    steps=[dict(name=v.get('name',''),number=i) for i,v in enumerate(job['steps'],1)])
    def lock_api(self,lock,records):
        calls=[]
        registry=dict(schema=1,gateStep=lock.GATE_STEP,writers=[dict(repo=P.REPO,workflows=[P.WORKFLOW])])
        def get(path,raw=False):
            calls.append((path,raw))
            if path==f'/repos/{lock.REGISTRY_REPOSITORY}/contents/{lock.REGISTRY_PATH}?ref=main':return registry
            if path==f'/repos/{P.REPO}/actions/runs/99/jobs?filter=latest&per_page=100':return {'jobs':copy.deepcopy(records)}
            if path==f'/repos/{P.REPO}/actions/runs?status=in_progress&per_page=100&page=1':return {'workflow_runs':[dict(id=99,path=P.WORKFLOW)]}
            raise AssertionError('unexpected pinned-lock fixture request '+path)
        return types.SimpleNamespace(get=get),calls
    def test_actual_yaml_writer_jobs_acquire_using_exact_pinned_lock(self):
        lock,jobs=self.pinned_lock()
        for name in ('sdk_profile_packages','sdk_profile_finalize','sdk_profile_observe'):
            with self.subTest(job=name):
                native=self.lock_native_job(lock,jobs[name]);api,calls=self.lock_api(lock,[native])
                self.assertTrue(lock.participates(native))
                found=lock.find_self(api,P.REPO,99,'injected-writer',lambda _:None)
                self.assertEqual(found['job'],700)
                result=lock.acquire(api,repo=P.REPO,run_id=99,runner_name='injected-writer',max_wait=10,
                                    clock=lambda:lock.parse_time(native['started_at']),sleep=lambda _:None,
                                    settle=0,confirm=0,log=lambda _:None)
                self.assertEqual(result['job'],700);self.assertEqual(result['otherLiveParticipants'],0)
                self.assertTrue(all(path.startswith('/repos/') for path,_ in calls))
    def test_actual_yaml_missing_writer_marker_is_refused_by_pinned_find_self(self):
        lock,jobs=self.pinned_lock()
        for name in ('sdk_profile_packages','sdk_profile_finalize'):
            with self.subTest(job=name):
                altered=copy.deepcopy(jobs[name]);altered['name']=altered['name'].replace(lock.MARKER,'').strip()
                native=self.lock_native_job(lock,altered);api,_=self.lock_api(lock,[native])
                self.assertFalse(lock.participates(native))
                with self.assertRaisesRegex(lock.LockError,'name lacks'):
                    lock.find_self(api,P.REPO,99,'injected-writer',lambda _:None)
    def test_actual_yaml_wrong_gate_step_fails_helper_registry_contract(self):
        lock,jobs=self.pinned_lock()
        for name in ('sdk_profile_packages','sdk_profile_finalize'):
            with self.subTest(job=name):
                altered=copy.deepcopy(jobs[name])
                for step in altered['steps']:
                    if step.get('name')==lock.GATE_STEP:step['name']='Acquire differently named lock'
                with self.assertRaisesRegex(AssertionError,'canonical publication gate'):
                    self.lock_native_job(lock,altered)
                with self.assertRaisesRegex(lock.LockError,'different gate step'):
                    lock.validate_registry(dict(schema=1,gateStep='Acquire differently named lock',writers=[dict(repo=P.REPO,workflows=[P.WORKFLOW])]))
    def test_actual_yaml_writers_see_and_wait_for_older_pinned_lock_participant(self):
        lock,jobs=self.pinned_lock()
        mine=self.lock_native_job(lock,jobs['sdk_profile_finalize'])
        older=self.lock_native_job(lock,jobs['sdk_profile_packages'],job_id=699,runner='injected-older',started='2025-12-31T23:59:59Z')
        api,_=self.lock_api(lock,[mine,older]);now=[lock.parse_time(mine['started_at'])]
        def sleep(seconds):now[0]+=max(1,seconds)
        with self.assertRaisesRegex(lock.LockError,'not acquired'):
            lock.acquire(api,repo=P.REPO,run_id=99,runner_name='injected-writer',max_wait=3,
                         clock=lambda:now[0],sleep=sleep,settle=0,confirm=0,poll_first=1,poll_max=1,log=lambda _:None)
    def test_actual_committed_admission_refuses_outside_native_caller_before_credentials(self):
        committed=json.loads((ROOT/P.ADMISSION).read_bytes())
        expected=b'publisher caller differs: GITHUB_REPOSITORY' if committed['enabled'] else b'disabled; selection absent'
        for command in ('bind','preflight','packages','public-load','finalize','observe','qualify-finalize','qualify-original'):
            env={'PATH':os.environ['PATH']}
            result=subprocess.run([sys.executable,str(ROOT/'eng/ordinary-sdk-publisher.py'),command,'--workdir',str(self.root/command)],env=env,capture_output=True,timeout=10)
            self.assertEqual(result.returncode,3);self.assertIn(expected,result.stderr);self.assertFalse((self.root/command).exists())
    def test_committed_enabled_admission_binds_exactly_the_accepted_candidate_and_live_policy(self):
        committed=json.loads((ROOT/P.ADMISSION).read_bytes())
        if not committed['enabled']:
            self.assertIsNone(committed['selection']);self.assertIsNone(committed['forward']);return
        s=P.selection(committed);self.assertIsNone(committed['forward'])
        source='965775d2b7e9df0b225490429936496f4e8fb97e'
        self.assertEqual({k:s[k] for k in ('operation','version','source','tree','protectedMerge','journal','operator','nugetUser')},
                         dict(operation='publish-sdk-profile-040-965775d2b7e9',version='0.4.0',source=source,tree='ffbdf32db309b300a4d60414d88c4e80fe80e8ad',
                              protectedMerge=source,journal={'id':'sdk-profile-040-965775d2b7e9'},operator='EHotwagner',nugetUser='Paradigma11'))
        self.assertEqual(s['candidate'],dict(runId=41992260121,artifactId=13397798973,archiveSha256='d21b339e301062cac39b2981678179973f70ed36aa1be654bc282e5c7e6ddff6',
                                             manifestSha256='8f0ef69486a167be19434add6435a31aa9c902ff0ce555a9c1915d68502bd6f1',
                                             packageSha256='262c366b4b0e0c766b302dac861806dd1a7a94b9b86e572f25eebbb31a31969f'))
        self.assertEqual(s['publisherFiles'],{n:P.sha((ROOT/n).read_bytes()) for n in P.B_PATHS-{P.ADMISSION}})
        self.assertEqual(s['liveEvidence']['tagProtection']['evidenceSha256'],P.sha(P.canonical(P.tag_projection(LIVE_TAG_RULESET))))
        self.assertEqual(committed['sharedHelper'],PIN)
        self.assertTrue(all(len(v['reference'])<=200 for v in s['liveEvidence'].values()))
    def test_selection_refuses_absent_and_malformed_native_ids(self):
        a=json.loads(self.path.read_bytes());self.assertEqual(P.selection(a),self.s)
        for key,value in [('runId',None),('runId',True),('artifactId',0),('archiveSha256','0'*64),('packageSha256',None)]:
            bad=copy.deepcopy(a);bad['selection']['candidate'][key]=value
            with self.subTest(key=key,value=value),self.assertRaises(P.Refused):P.selection(bad)
    def test_actual_caller_rejects_mixed_and_stale_source_before_tokens(self):
        for key,value in [('CLI_ONLY','true'),('READBACK_ONLY','true'),('EXECUTION_PAIR','true'),('PREFLIGHT_ONLY','true'),('PAIR_RECOVERY','true'),('GITHUB_RUN_ATTEMPT','2'),('GITHUB_REF','refs/heads/other'),('REQUESTED_SOURCE','e'*40),('REQUESTED_PUBLISHER','e'*40)]:
            before=dict(self.env);self.env[key]=value
            with self.subTest(key=key),self.assertRaises(P.Refused):self.invoke()
            self.assertEqual(self.w.tokens,[]);self.env=before
    def test_preflight_or_observe_mode_cannot_select_a_package_send(self):
        for mode in ('preflight','observe'):
            self.env['SDK_PROFILE_MODE']=mode
            if mode=='observe':self.env['SDK_ORIGINAL_RUN']='888'
            with self.assertRaises(P.Refused):self.invoke('packages')
            self.assertEqual(self.w.tokens,[]);self.assertEqual(self.w.sends,[])
    def test_positive_actual_package_caller_has_intent_before_each_send(self):
        self.invoke();self.assertEqual(self.w.sends,['github','nuget']);self.assertEqual(list(Journal.state.effects.values()),['verified','verified'])
    def test_archive_or_native_binding_failure_has_no_ledger_or_package_credential(self):
        cases=[(f'repos/{P.REPO}/actions/runs/100',{'id':100,'head_sha':'e'*40}),
               (f'repos/{P.REPO}/actions/artifacts/101',{'id':101,'expired':True})]
        for path,value in cases:
            self.w.overrides[path]=value
            with self.assertRaises(P.Refused):self.invoke()
            self.assertEqual(self.w.tokens,['inert-read-fixture']);self.w.tokens=[];self.w.overrides={}
    def test_original_archive_digest_refuses_valid_recompressed_bytes_before_credentials(self):
        b=io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(self.raw)) as original,zipfile.ZipFile(b,'w',compression=zipfile.ZIP_DEFLATED) as altered:
            for name in original.namelist():altered.writestr(name,original.read(name))
        metadata=self.w.get(f'repos/{P.REPO}/actions/artifacts/101')
        self.w.raw=b.getvalue();self.w.overrides[f'repos/{P.REPO}/actions/artifacts/101']=metadata
        with self.assertRaises(P.Refused):self.invoke()
        self.assertEqual(self.w.tokens,['inert-read-fixture']);self.assertEqual(self.w.sends,[])
    def test_cas_conflict_or_lost_reply_cannot_send(self):
        for mode in ('fail_cas','lose_cas'):
            Journal.state=None;setattr(Journal,mode,True)
            with self.subTest(mode=mode),self.assertRaises((P.Refused,ENGINE.EngineRefused,RuntimeError)):self.invoke()
            self.assertEqual(self.w.sends,[]);setattr(Journal,mode,False)
    def test_stale_native_main_after_intent_stops_dispatch(self):
        self.w.stale_at=4
        with self.assertRaises((P.Refused,ENGINE.EngineRefused)):self.invoke()
        self.assertEqual(self.w.sends,[]);self.assertIn('intent',Journal.state.effects.values())
    def test_lost_send_then_absence_new_dispatch_is_not_a_retry_permit(self):
        self.w.lost_send=True
        with self.assertRaisesRegex(P.Refused,'possible-send'):self.invoke()
        self.assertEqual(self.w.sends,['github']);self.assertIn('intent',Journal.state.effects.values())
        original=list(Journal.writes);self.w.run_id=1000;self.env['GITHUB_RUN_ID']='1000'
        with self.assertRaises(P.Refused):self.invoke()
        self.assertEqual(self.w.sends,['github']);self.assertEqual(Journal.writes,original)
        self.invoke('observe',original_run=999)
        self.assertEqual(Journal.writes,original);self.assertEqual(self.w.sends,['github'])
    def test_partial_github_success_then_nuget_unknown_stops_finalization(self):
        actual=self.w.execute
        def fail_nuget(argv,*a,**kw):
            if argv[:3]==['dotnet','nuget','push'] and 'api.nuget.org' in argv[argv.index('--source')+1]:raise subprocess.TimeoutExpired(argv,1)
            return actual(argv,*a,**kw)
        self.w.execute=fail_nuget
        with self.assertRaises(P.Refused):self.invoke()
        self.assertEqual(self.w.sends,['github']);self.assertEqual(list(Journal.state.effects.values()),['verified','intent'])
        with self.assertRaises((P.Refused,TypeError)):self.invoke('finalize')
        self.assertEqual(self.w.sends,['github'])
    def test_fence_rereads_exact_original_intent_immediately_before_send(self):
        self.w.journal_change_at=4
        with self.assertRaises(ENGINE.EngineRefused):self.invoke()
        self.assertEqual(self.w.sends,[])
    def test_actual_public_load_executes_only_verified_runtime_in_private_public_install(self):
        self.invoke();sends=list(self.w.sends);self.invoke('public-load')
        self.assertEqual(self.w.sends,sends)
        receipts=list(self.root.glob('work-public-load-*/ordinary-sdk-public-load.json'))
        self.assertEqual(len(receipts),1)
        v=P.strict_json(receipts[0].read_bytes());self.assertTrue(v['publicOnly']);self.assertFalse(v['consumerAdoption'])
    def test_actual_public_load_refuses_changed_installed_runtime_before_cli(self):
        self.invoke();actual=self.w.execute
        def changed(argv,root,**kw):
            result=actual(argv,root,**kw)
            if argv[:3]==['dotnet','tool','install']:
                for file in Path(root).rglob('FS.GG.Coordination.Cli.dll'):file.write_bytes(b'wrong-runtime')
            return result
        self.w.execute=changed
        with self.assertRaises(P.Refused):self.invoke('public-load')
        self.assertEqual(self.w.sends,['github','nuget'])
    def complete_public_receipt(self):
        context={'sha':self.env['GITHUB_SHA'],'runId':self.w.run_id}
        s=P.runtime_selection(self.s,{'sha':self.forward['originalPublisher'],'runId':self.forward['originalRun']}) if self.forward else P.runtime_selection(self.s,context)
        receipt=P.canonical(dict(schema='fsgg.coordination.ordinary-sdk-public-load/1',contentId=P.content_id(s),publisher=context['sha'],runId=context['runId'],source=self.s['source'],packageSha256=self.s['candidate']['packageSha256'],servedArchiveSha256=P.sha(self.w.feeds['nuget']),signature='verified',installedCliCases=15,publicOnly=True,consumerAdoption=False))
        self.w.publics[self.w.run_id]=archive({'ordinary-sdk-public-load.json':receipt})
    def test_positive_actual_finalize_caller_uses_same_custody_and_order(self):
        self.invoke();self.complete_public_receipt();self.invoke('finalize')
        self.assertEqual(len(Journal.state.effects),8)
        self.assertTrue(all(v=='verified' for v in Journal.state.effects.values()))
        self.assertEqual(self.w.release['draft'],False)
        self.assertEqual(len(self.w.sends),8)
        self.assertTrue(all('/run-999/publisher-'+'c'*40 in key for key in Journal.state.effects))
    def test_missing_native_public_proof_blocks_final_credentials_and_writes(self):
        self.invoke();self.complete_public_receipt()
        self.w.overrides[f'repos/{P.REPO}/actions/runs/999/attempts/1/jobs?per_page=100']={'jobs':[],'total_count':0}
        sends=list(self.w.sends)
        with self.assertRaises(P.Refused):self.invoke('qualify-finalize')
        self.assertEqual(self.w.sends,sends)
    def test_changed_public_payload_or_signature_cannot_finalize(self):
        self.invoke();self.complete_public_receipt();self.w.feeds['nuget']=self.package
        sends=list(self.w.sends)
        with self.assertRaises((P.Refused,ENGINE.EngineRefused)):self.invoke('finalize')
        self.assertEqual(self.w.sends,sends)
    def test_tag_policy_missing_bypass_or_drift_blocks_first_final_send(self):
        self.invoke();self.complete_public_receipt()
        self.w.overrides[f'repos/{P.REPO}/rulesets/21633398']={'id':21633398}
        sends=list(self.w.sends)
        with self.assertRaises((P.Refused,ENGINE.EngineRefused)):self.invoke('finalize')
        self.assertEqual(self.w.sends,sends)
    def test_tag_policy_projection_accepts_actual_rest_shape_and_refuses_drift(self):
        # Shape of the live GET (2026-11-07): default update parameter omitted, viewer/link metadata present.
        live=dict(id=21633398,name='release-tags-immutable',target='tag',source_type='Repository',source=P.REPO,enforcement='active',
                  conditions={'ref_name':{'exclude':[],'include':['refs/tags/v*','refs/tags/execution/v*']}},
                  rules=[{'type':'deletion'},{'type':'non_fast_forward'},{'type':'required_signatures'},{'type':'update'}],
                  node_id='fixture',created_at='2026-08-27T09:36:56.365+02:00',updated_at='2026-10-12T13:19:45.228+02:00',
                  bypass_actors=[],current_user_can_bypass='never',_links={'self':{'href':'fixture'}})
        P.tag_policy(live,self.s)
        for viewer in ('always','pull_requests_only'):
            P.tag_policy({**live,'current_user_can_bypass':viewer,'node_id':'other','_links':{}},self.s)
        drifts={'bypass-absent':lambda v:v.pop('bypass_actors'),'bypass-actor':lambda v:v.update(bypass_actors=[{'actor_id':1,'actor_type':'Integration','bypass_mode':'always'}]),
                'update-allows-merge':lambda v:v['rules'][3].update(parameters={'update_allows_fetch_and_merge':True}),
                'signature-dropped':lambda v:v['rules'].pop(2),'extra-rule':lambda v:v['rules'].append({'type':'creation'}),
                'evaluate':lambda v:v.update(enforcement='evaluate'),'scope':lambda v:v['conditions']['ref_name']['include'].pop(),
                'id':lambda v:v.update(id=1),'rules-malformed':lambda v:v.update(rules=['update'])}
        for name,mutate in drifts.items():
            changed=copy.deepcopy(live);mutate(changed)
            with self.subTest(drift=name),self.assertRaises(P.Refused):P.tag_policy(changed,self.s)
    def test_signature_requires_normal_verbosity_repository_identity(self):
        self.w.verify_output=VERIFY_NORMAL
        with patch.object(entry,'execute',side_effect=self.w.execute):
            entry.signature(b'fixture',self.root)
            for output in (VERIFY_DEFAULT,VERIFY_NORMAL.replace(b'Signature type: Repository',b'Signature type: Author'),
                           VERIFY_NORMAL.replace(b'https://api.nuget.org/v3/index.json',b'https://evil.invalid/v3/index.json'),
                           VERIFY_NORMAL.replace(b'Successfully verified',b'Failed to verify')):
                self.w.verify_output=output
                with self.subTest(output=output[:40]),self.assertRaises(P.Refused):entry.signature(b'fixture',self.root)
    def test_pre_send_policy_or_verifier_failure_has_no_journal_or_send(self):
        for case in ('policy','verifier','selftest-bytes'):
            with self.subTest(case=case):
                Journal.state=None;self.w.sends=[];self.w.overrides={};self.w.verify_output=VERIFY_NORMAL
                selftest=self.w.selftest
                if case=='policy':
                    drift=copy.deepcopy(LIVE_TAG_RULESET);drift['rules'].pop(2);self.w.overrides[f'repos/{P.REPO}/rulesets/21633398']=drift
                elif case=='verifier':self.w.verify_output=VERIFY_DEFAULT
                with self.assertRaises(P.Refused):
                    if case=='selftest-bytes':
                        with patch.object(entry,'PUBLIC_SELFTEST_SHA256','0'*64):
                            entry.signature_selftest(types.SimpleNamespace(get=self.w.transport),self.root)
                        raise AssertionError('unreachable')
                    self.invoke()
                self.assertIsNone(Journal.state);self.assertEqual(self.w.sends,[]);self.w.selftest=selftest
        self.w.overrides={};self.w.verify_output=VERIFY_NORMAL
    def test_draft_release_found_through_bounded_list_and_foreign_draft_occupies(self):
        self.invoke();self.complete_public_receipt();self.invoke('finalize')
        self.assertIn(f'repos/{P.REPO}/releases?per_page=100&page=1',self.w.gets)
        self.assertEqual(self.w.release['draft'],False);self.assertEqual(len(self.w.assets),3)
        fresh=World(self.s,self.package,self.manifest,self.raw);fresh.foreign_releases=[{'id':7,'tag_name':'v'+P.VERSION,'draft':True,'body':'foreign','target_commitish':'e'*40}]
        with self.assertRaisesRegex(P.Refused,'occupied'):P.vacant(fresh.api('t'),types.SimpleNamespace(get=fresh.transport),'t',self.s,ENGINE.provider.NotFound)
        fresh.foreign_releases=[{'id':7,'tag_name':'v'+P.VERSION}]*2
        with self.assertRaisesRegex(P.Refused,'duplicate'):P.find_release(fresh.api('t'),ENGINE.provider.NotFound)
    def test_native_budget_caps_bytes_calls_and_late_results(self):
        calls=[]
        class Response:
            status=200
            def __enter__(self):return self
            def __exit__(self,*a):pass
            def read(self,n):calls.append(('read',n));return b'x'*(P.F.MAX_JSON+1)
        def opener(*a,**kw):calls.append(('open',kw['timeout']));return Response()
        api=P.bounded_api(ENGINE,budget=P.NativeBudget(requests=1),opener=opener)('inert-fixture')
        with self.assertRaises(P.Refused):api.get('repos/'+P.REPO)
        with self.assertRaises(P.Refused):api.get('repos/'+P.REPO)
        self.assertEqual(len([v for v in calls if v[0]=='open']),1)
        self.assertEqual(calls[-1],('read',P.F.MAX_JSON+1))
        for url in ('https://evil.invalid/repos/'+P.REPO,'http://api.github.com/repos/'+P.REPO):
            with self.assertRaises(P.Refused):api._request(url)
        now=[0];budget=P.NativeBudget(clock=lambda:now[0],seconds=1);budget.reserve();now[0]=2
        with self.assertRaises(P.Refused):budget.returned()
    def admit_forward(self):
        state=Journal.state
        self.forward=dict(originalRun=999,originalPublisher='c'*40,contentId=state.content_id,
                          checkpoint={'generation':state.generation,'effects':copy.deepcopy(state.effects)},
                          tagEvidence=self.s['liveEvidence']['tagProtection'],authorization={'reference':'fixture explicit original-forward','evidenceSha256':'f'*64})
        self.w.run_id=1000;self.w.run_heads[1000]='e'*40;self.w.main='e'*40
        self.env.update(GITHUB_RUN_ID='1000',GITHUB_SHA='e'*40,REQUESTED_PUBLISHER='e'*40,SDK_PROFILE_MODE='forward')
        self.env.pop('SDK_ORIGINAL_RUN',None);self.save()
    def test_forward_absent_admission_refuses_before_credentials(self):
        self.env['SDK_PROFILE_MODE']='forward'
        with self.assertRaises(P.Refused):self.invoke('bind')
        self.assertEqual(self.w.tokens,[])
    def test_expired_original_unknown_settles_matched_then_remaining_effects_get_fresh_owner(self):
        self.w.lost_send=True
        with self.assertRaises(P.Refused):self.invoke()
        original=Journal.state.content_id;self.w.lost_send=False;self.w.feeds['github']=self.package
        expired=[0];budget=P.NativeBudget(clock=lambda:expired[0],seconds=600);expired[0]=676
        with self.assertRaises(P.Refused):budget.reserve()
        self.admit_forward();self.invoke();self.complete_public_receipt();self.invoke('finalize')
        self.assertEqual(self.w.sends.count('github'),1);self.assertEqual(self.w.sends.count('nuget'),1)
        self.assertEqual(Journal.state.content_id,original)
        self.assertTrue(all(v=='verified' for v in Journal.state.effects.values()))
        self.assertTrue(any(k.startswith('github/run-999/') for k in Journal.state.effects))
        self.assertTrue(any(k.startswith('nuget/run-1000/publisher-'+'e'*40) for k in Journal.state.effects))
    def test_observation_window_and_native_budget_cover_nuget_validation_within_writer_job(self):
        import yaml
        jobs=yaml.safe_load((ROOT/P.WORKFLOW).read_text())['jobs']
        budget=P.NativeBudget();self.assertGreater(budget.deadline-time.monotonic(),P.OBSERVATION_SECONDS)
        self.assertGreaterEqual(P.OBSERVATION_SECONDS,20*60);self.assertGreater(budget.remaining,3*P.OBSERVATION_SECONDS//P.OBSERVATION_CADENCE_SECONDS)
        for name in ('sdk_profile_packages','sdk_profile_finalize'):
            wait=int(next(v for v in jobs[name]['steps'] if v.get('name')=='Acquire org publication writer lock')['env']['LOCK_MAX_WAIT_MINUTES'])
            self.assertLess(P.OBSERVATION_SECONDS+10*60,(jobs[name]['timeout-minutes']-wait)*60)
    def test_acknowledged_send_outlives_original_visibility_window_then_forwards_without_replay(self):
        window=P.OBSERVATION_SECONDS;ticks=iter((0,window//2,window,window+1));self.advance_clock=lambda:next(ticks)
        self.w.hide_packages=True
        with self.assertRaisesRegex(P.Refused,'original observation bound exhausted'):self.invoke()
        self.assertEqual(self.w.sends,['github']);self.assertIn('intent',Journal.state.effects.values())
        self.w.hide_packages=False;self.advance_clock=None
        old=Journal.state.content_id;self.admit_forward();self.invoke();self.complete_public_receipt();self.invoke('finalize')
        self.assertEqual(self.w.sends.count('github'),1);self.assertEqual(Journal.state.content_id,old)
        self.assertEqual(self.w.release['draft'],False)
    def test_late_public_visibility_forward_requires_fresh_positive_public_proof(self):
        self.w.lost_feed='nuget'
        with self.assertRaises(P.Refused):self.invoke()
        with zipfile.ZipFile(io.BytesIO(self.package)) as z:members={n:z.read(n) for n in z.namelist()}
        self.w.feeds['nuget']=archive({**members,'.signature.p7s':b'synthetic-signature'});self.w.lost_feed=None
        original_sends=list(self.w.sends);self.admit_forward();self.invoke()
        self.assertEqual(self.w.sends,original_sends)
        with self.assertRaises((P.Refused,TypeError)):self.invoke('qualify-finalize')
        self.complete_public_receipt();self.invoke('qualify-finalize');self.invoke('finalize')
        self.assertEqual(self.w.sends[:2],['github','nuget']);self.assertEqual(self.w.release['draft'],False)
    def test_forward_after_lost_readback_asset_preserves_frozen_original_bytes(self):
        self.invoke();self.complete_public_receipt();old=self.w.publics[999]
        self.w.lost_asset='ordinary-sdk-release-readback.json'
        with self.assertRaises(P.Refused):self.invoke('finalize')
        frozen=self.w.assets['ordinary-sdk-release-readback.json'];sends=list(self.w.sends)
        self.w.lost_asset=None;self.admit_forward();self.invoke();self.complete_public_receipt();self.invoke('finalize')
        self.assertEqual(self.w.assets['ordinary-sdk-release-readback.json'],frozen)
        self.assertEqual(self.w.sends.count('asset:ordinary-sdk-release-readback.json'),1)
        self.assertEqual(self.w.publics[999],old);self.assertEqual(self.w.release['draft'],False)
    def test_forward_unknown_404_never_sends_and_checkpoint_drift_refuses(self):
        self.w.lost_send=True
        with self.assertRaises(P.Refused):self.invoke()
        self.admit_forward();before=list(self.w.sends)
        with self.assertRaises(P.Refused):self.invoke()
        self.assertEqual(self.w.sends,before)
        self.forward['checkpoint']['generation']+=100;self.save();self.w.feeds['github']=self.package;self.w.lost_send=False
        with self.assertRaisesRegex(P.Refused,'checkpoint'):self.invoke()
        self.assertEqual(self.w.sends,before)
    def test_children_exclude_every_credential(self):
        with patch.dict(os.environ,self.env,clear=True):env=entry.child_env(self.root)
        self.assertFalse(any(any(v in key for v in ('TOKEN','KEY','SECRET','PASSWORD')) for key in env))
    def test_unknown_namespace_and_preexisting_feed_refuse_without_effects(self):
        self.w.overrides['orgs/FS-GG/packages/nuget/fs.gg.coordination.cli/versions?per_page=100&page=1']=[{'name':'0.4.0'}]
        with self.assertRaises(P.Refused):self.invoke('preflight')
        self.assertEqual(self.w.sends,[]);self.assertIsNone(Journal.state)


if __name__=='__main__':unittest.main(verbosity=2)
