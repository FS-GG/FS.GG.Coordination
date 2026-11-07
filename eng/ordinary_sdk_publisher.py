"""Disabled fresh CLI0.4 publisher. Uses the existing protected ordered-effect engine.

No journal, feed, tag, release or consumer action occurs at import or selection.
A recorded intent is possible-send custody, never a permit to resend after 404.
"""
from __future__ import annotations
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import zipfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('sdk_forward_primitives', ROOT/'eng/execution_pair_forward.py')
F = importlib.util.module_from_spec(spec)
spec.loader.exec_module(F)
Refused = F.Refused
require, canonical, strict_json = F.require, F.canonical, F.strict_json
sha = F.sha256_hex
REPO = 'FS-GG/FS.GG.Coordination'
REPO_ID = 1346720714
WORKFLOW = '.github/workflows/callable-cli-release-publish.yml'
PREPARE = '.github/workflows/callable-cli-release-prepare.yml'
ADMISSION = 'eng/ordinary-sdk-profile-admission.json'
PACKAGE = 'FS.GG.Coordination.Cli'
VERSION = '0.4.0'
PACKAGE_FILE = PACKAGE+'.'+VERSION+'.nupkg'
MANIFEST = 'callable-cli-release-manifest.json'
EFFECTS = ('github', 'nuget', 'tag', 'draft', 'asset:package', 'asset:manifest', 'asset:readback', 'promote')
B_PATHS = frozenset({ADMISSION, WORKFLOW, 'eng/ordinary_sdk_publisher.py', 'eng/ordinary-sdk-publisher.py',
 'tests/portable-workspace/release/test_ordinary_sdk_publisher.py',
 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json'})



def publication_delta(paths):
    # The two frozen 0.3.0/0.3.1 control suites are test-only: they prove the old routes are unchanged by this one.
    return all(p in B_PATHS or p in {'eng/bootstrap-gates/compiler-and-tests.sh','eng/run-ordinary-sdk-publisher-controls.py','tests/portable-workspace/release/test_ordinary_sdk_publisher_runner.py',
               'tests/portable-workspace/release/test_execution_pair_forward.py','tests/portable-workspace/release/test_execution_pair_release.py',
               'tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs'} or p=='docs/atlas/catalog.json' or re.fullmatch(r'docs/atlas/records/[0-9a-f]{64}\.json',p) for p in paths)

def selection(admission):
    require(type(admission) is dict and set(admission)=={'schema','enabled','selection','sharedHelper','forward'}, 'admission fields differ')
    require(admission['schema']=='fsgg.coordination.ordinary-sdk-profile-admission/1', 'admission schema differs')
    F.validate_shared_helper(admission['sharedHelper'])
    require(admission['enabled'] is True and type(admission['selection']) is dict, 'fresh SDK publication disabled; selection absent')
    s=admission['selection']
    require(set(s)=={'operation','version','source','tree','protectedMerge','candidate','journal','operator','nugetUser','publisherFiles','liveEvidence'}, 'selection fields differ')
    require(s['version']==VERSION and s['operator']=='EHotwagner', 'version/operator differs')
    for key in ('source','tree','protectedMerge'): F.hexid(s[key],40,key)
    require(type(s['operation']) is str and re.fullmatch('publish-sdk-profile-040-[0-9a-f]{12}',s['operation']) and s['operation'].endswith(s['source'][:12]), 'operation differs')
    require(type(s['journal']) is dict and set(s['journal'])=={'id'} and s['journal']['id']=='sdk-profile-040-'+s['source'][:12], 'journal identity differs')
    c=s['candidate']; require(type(c) is dict and set(c)=={'runId','artifactId','archiveSha256','manifestSha256','packageSha256'}, 'candidate selection differs')
    for key in ('runId','artifactId'): F.positive(c[key],key)
    for key in ('archiveSha256','manifestSha256','packageSha256'): F.hexid(c[key],64,key)
    require(type(s['nugetUser']) is str and re.fullmatch('[A-Za-z0-9_.-]{1,100}',s['nugetUser']), 'NuGet account absent')
    require(type(s['publisherFiles']) is dict and set(s['publisherFiles'])==B_PATHS-{ADMISSION}, 'publisher closure differs')
    for path,digest in s['publisherFiles'].items(): F.hexid(digest,64,path)
    require(type(s['liveEvidence']) is dict and set(s['liveEvidence'])=={'ledger','namespace','tagProtection','trustedPublishing','operationAdmission'}, 'live evidence fields differ')
    for key,value in s['liveEvidence'].items(): F._evidence(value,key)
    return s


def bind(s, env, *, git=None):
    for name,value in [('GITHUB_REPOSITORY',REPO),('GITHUB_EVENT_NAME','workflow_dispatch'),('GITHUB_REF','refs/heads/main'),
                       ('GITHUB_RUN_ATTEMPT','1'),('GITHUB_ACTOR',s['operator']),('CLI_PROFILE_REPAIR','true')]:
        require(env.get(name)==value,'publisher caller differs: '+name)
    require(all(env.get(k,'false')=='false' for k in ('CLI_ONLY','EXECUTION_PAIR','PAIR_RECOVERY','PREFLIGHT_ONLY','READBACK_ONLY')), 'mixed release modes refused')
    for name,key in [('OPERATION','operation'),('REQUESTED_SOURCE','source'),('REQUESTED_MERGE','protectedMerge')]:
        require(env.get(name)==s[key],'publisher input differs: '+name)
    F.hexid(env.get('GITHUB_SHA'),40,'publisher')
    require(env.get('REQUESTED_PUBLISHER')==env['GITHUB_SHA'] and env.get('REQUESTED_SHA256')==s['candidate']['packageSha256'], 'publisher/package input differs')
    require(env.get('GITHUB_RUN_ID','').isdigit() and int(env['GITHUB_RUN_ID'])>0,'native run absent')
    if git is None:
        def git(*args): return subprocess.check_output(['git',*args],cwd=ROOT,text=True,timeout=30).strip()
    require(git('rev-parse','HEAD')==env['GITHUB_SHA'] and not git('status','--porcelain'), 'checkout identity/cleanliness differs')
    require(git('rev-parse',s['source']+'^{tree}')==s['tree']==git('rev-parse',s['protectedMerge']+'^{tree}'), 'A/merge tree differs')
    for source in (s['source'],s['protectedMerge']):
        require(git('merge-base',source,env['GITHUB_SHA'])==source,'A/merge not ancestor of B')
    require(publication_delta(git('diff','--name-only','--no-renames',s['source'],env['GITHUB_SHA']).splitlines()),'A-to-B changes source outside publication controls')
    for path,digest in s['publisherFiles'].items():
        require(sha((ROOT/path).read_bytes())==digest,'reviewed B control changed: '+path)
    require(env.get('SDK_PROFILE_MODE','publish') in ('preflight','publish','observe','forward'),'unknown SDK mode')
    if env.get('SDK_PROFILE_MODE')=='observe':
        require(env.get('SDK_ORIGINAL_RUN','').isdigit() and int(env['SDK_ORIGINAL_RUN'])>0,'original run input absent')
    else: require(not env.get('SDK_ORIGINAL_RUN'),'original run input outside read-only observation')
    return {'sha':env['GITHUB_SHA'],'runId':int(env['GITHUB_RUN_ID'])}


def native_context(api,s,context):
    repo=api.get('repos/'+REPO)
    require(repo.get('id')==REPO_ID and repo.get('full_name')==REPO,'repository identity differs')
    run=api.get(f"repos/{REPO}/actions/runs/{context['runId']}")
    require(run.get('id')==context['runId'] and run.get('repository',{}).get('id')==REPO_ID and run.get('path')==WORKFLOW
            and run.get('event')=='workflow_dispatch' and run.get('head_branch')=='main' and run.get('head_sha')==context['sha']
            and run.get('run_attempt')==1 and run.get('actor',{}).get('login')==s['operator'] and run.get('status')=='in_progress','native publisher context differs')
    require(api.get(f'repos/{REPO}/git/ref/heads/main').get('object',{}).get('sha')==context['sha'],'protected main moved')


def candidate(api, transport, token, s, directory):
    c=s['candidate']; run=api.get(f"repos/{REPO}/actions/runs/{c['runId']}")
    require(run.get('id')==c['runId'] and run.get('repository',{}).get('id')==REPO_ID and run.get('head_repository',{}).get('id')==REPO_ID
            and run.get('path')==PREPARE and run.get('event')=='workflow_dispatch' and run.get('head_branch')=='main'
            and run.get('head_sha')==s['source'] and run.get('run_attempt')==1 and run.get('conclusion')=='success','native preparation differs')
    jobs=api.get(f"repos/{REPO}/actions/runs/{c['runId']}/attempts/1/jobs?per_page=100")
    require(type(jobs.get('jobs')) is list and jobs.get('total_count')==len(jobs['jobs'])<100,'preparation job census incomplete')
    rows=[j for j in jobs['jobs'] if j.get('name')=='Prepare the strict-ten-gate CLI profile repair without publication']
    require(len(rows)==1 and rows[0].get('conclusion')=='success' and rows[0].get('head_sha')==s['source'] and rows[0].get('run_attempt')==1,'fresh preparation job absent')
    artifact=api.get(f"repos/{REPO}/actions/artifacts/{c['artifactId']}")
    require(artifact.get('id')==c['artifactId'] and artifact.get('expired') is False and artifact.get('name')=='callable-cli-profile-repair-'+s['source']
            and artifact.get('digest')=='sha256:'+c['archiveSha256'] and artifact.get('workflow_run',{}).get('id')==c['runId']
            and artifact.get('workflow_run',{}).get('head_sha')==s['source'],'native original artifact differs')
    status,raw=transport.get(f"https://api.github.com/repos/{REPO}/actions/artifacts/{c['artifactId']}/zip",headers=F._api_headers(token),cap=32*1024*1024)
    require(status==200 and sha(raw)==c['archiveSha256'],'original archive bytes differ')
    directory=Path(directory); directory.mkdir(mode=0o700,parents=True,exist_ok=False)
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        require(len(z.infolist())==2 and set(z.namelist())=={PACKAGE_FILE,MANIFEST},'candidate archive population differs')
        for info in z.infolist():
            require(0<info.file_size<=F.MAX_PACKAGE and (info.external_attr>>16)&0o170000 in (0,0o100000),'candidate member unsafe')
            data=z.read(info); expected=c['packageSha256' if info.filename==PACKAGE_FILE else 'manifestSha256']
            require(sha(data)==expected,'candidate member digest differs')
            (directory/info.filename).write_bytes(data)
    manifest=strict_json((directory/MANIFEST).read_bytes())
    require(manifest.get('schema')=='fsgg.coordination.callable-cli-release-preparation/1' and manifest.get('packageId')==PACKAGE
            and manifest.get('version')==VERSION and manifest.get('tag')=='v'+VERSION and manifest.get('sourceCommit')==s['source']
            and manifest.get('sourceTree')==s['tree'] and manifest.get('packageSha256')==c['packageSha256']
            and manifest.get('publicationAuthorized') is False and manifest.get('tagAuthorized') is False,'candidate manifest binding differs')
    package=(directory/PACKAGE_FILE).read_bytes()
    F.payload_map(package)
    import xml.etree.ElementTree as ET
    with zipfile.ZipFile(io.BytesIO(package)) as z:
        names=[n for n in z.namelist() if n.endswith('.nuspec')]
        require(len(names)==1 and z.getinfo(names[0]).file_size<=65536,'package nuspec population/bound differs')
        root=ET.fromstring(z.read(names[0]))
        nodes={n.tag.split('}')[-1]:n for n in root.iter()}
        require(nodes.get('id') is not None and nodes['id'].text==PACKAGE and nodes.get('version') is not None
                and nodes['version'].text==VERSION and nodes.get('repository') is not None
                and nodes['repository'].get('commit')==s['source']
                and nodes['repository'].get('url')=='https://github.com/'+REPO,'package source provenance differs')
    return directory


def find_release(api,not_found):
    try: return api.get(f'repos/{REPO}/releases/tags/v{VERSION}')
    except not_found: pass
    for page in range(1,11):
        rows=api.get(f'repos/{REPO}/releases?per_page=100&page={page}')
        require(type(rows) is list and len(rows)<=100,'release list differs')
        matches=[r for r in rows if type(r) is dict and r.get('tag_name')=='v'+VERSION]
        require(len(matches)<=1,'duplicate releases share the fresh tag')
        if matches: return matches[0]
        if len(rows)<100: return None
    raise Refused('release list exceeds bounded lookup')


def owned_release(api,not_found,marker,source):
    value=find_release(api,not_found)
    if value is None: return None
    require(value.get('tag_name')=='v'+VERSION and value.get('body')==marker and value.get('target_commitish')==source,'foreign release collision')
    return value


def runtime_selection(s,context):
    require(set(context)=={'sha','runId'} and type(context['runId']) is int and context['runId']>0,'original native context absent')
    F.hexid(context['sha'],40,'original publisher')
    return {**s,'invocation':{'runId':context['runId'],'attempt':1,'publisher':context['sha'],'workflow':WORKFLOW}}


def effect_id(s,logical):
    require(logical in EFFECTS and 'invocation' in s,'effect original custody absent')
    if logical in s.get('effectOwners',{}): return s['effectOwners'][logical]
    owner=s.get('caller',s['invocation'])
    return logical+'/run-'+str(owner['runId'])+'/publisher-'+owner['publisher']


def logical_id(s,identity):
    for logical in EFFECTS:
        if effect_id(s,logical)==identity: return logical
    raise Refused('effect outside exact original custody')


def content_id(s):
    require('invocation' in s,'content identity lacks original native invocation')
    return 'sha256:'+sha(canonical({k:v for k,v in s.items() if k not in ('caller','effectOwners','forwardTagEvidence')}))
def journal_ref(s): return 'refs/heads/fsgg/v2/journal/release/'+s['journal']['id']
def intent(s): return dict(contentId=content_id(s),sourceSha=s['source'],version=VERSION,candidateArchiveSha256=s['candidate']['archiveSha256'],operator=s['operator'])


def effects(engine,s,directory,readback=None):
    package=(directory/PACKAGE_FILE).read_bytes()
    targets=[F.payload_digest(package),F.payload_digest(package),s['source'],content_id(s),s['candidate']['packageSha256'],s['candidate']['manifestSha256']]
    if readback is None: return tuple(engine.Effect(effect_id(s,e),t,sha(canonical({'effect':effect_id(s,e),'target':t,'operation':content_id(s)}))) for e,t in zip(EFFECTS[:2],targets[:2]))
    targets += [sha(readback),content_id(s)]
    return tuple(engine.Effect(effect_id(s,e),t,sha(canonical({'effect':effect_id(s,e),'target':t,'operation':content_id(s)}))) for e,t in zip(EFFECTS,targets))


class Admission:
    def __init__(self,api,s,context,planned,stage,policy_reader=None):
        self.api,self.s,self.context,self.planned,self.stage=api,s,context,{e.identity:e for e in planned},stage
        self.policy_reader=policy_reader
    def authorize(self,cid,e,action,digest):
        if cid!=content_id(self.s) or action not in ('intent','dispatch','settle'): return False
        if e=='journal':
            if digest!=cid: return False
        elif e not in self.planned or self.planned[e].request_digest!=digest or (self.stage=='packages' and e not in {effect_id(self.s,v) for v in EFFECTS[:2]}): return False
        native_context(self.api,self.s,self.context)
        if e!='journal' and action in ('intent','dispatch'):
            caller=self.s.get('caller',self.s['invocation'])
            require(e.endswith('/run-'+str(caller['runId'])+'/publisher-'+caller['publisher']),'old intent never permits a new caller send')
        if self.stage=='finalize' and action in ('intent','dispatch'):
            require(self.policy_reader is not None,'complete fresh tag policy reader absent')
            tag_policy(self.policy_reader(),self.s)
        return True


class Provider:
    def __init__(self,engine,api,s,directory,transport,*,stage,github_token='',nuget_key='',runner=None,signature=None,readback=None):
        self.engine,self.api,self.s,self.directory,self.transport=engine,api,s,directory,transport
        self.stage,self.github_token,self.nuget_key,self.runner,self.signature,self.readback=stage,github_token,nuget_key,runner,signature,readback
        self.marker='ordinary-sdk-profile:'+content_id(s); self.uncertain=False
    def release(self):
        # GET releases/tags/{tag} 404s for a draft (.github#3578), so absence there falls back to
        # the bounded authenticated release list before any draft is declared absent.
        return owned_release(self.api,self.engine.provider.NotFound,self.marker,self.s['source'])
    def assets(self):
        release=self.release(); require(release is not None,'owned release absent')
        rows=self.api.get(f"repos/{REPO}/releases/{release['id']}/assets?per_page=100")
        require(type(rows) is list and len(rows)<100 and len({v.get('name') for v in rows})==len(rows),'asset census incomplete/duplicate')
        return release,rows
    def asset_bytes(self,name):
        return {'package':(self.directory/PACKAGE_FILE).read_bytes(),'manifest':(self.directory/MANIFEST).read_bytes(),'readback':self.readback}[name]
    def observe(self,e):
        O=self.engine.Observation; identity=logical_id(self.s,e.identity)
        if identity in ('github','nuget'):
            raw=F.fetch_feed(self.transport,self.github_token,VERSION,identity,PACKAGE)
            if raw is None: return O('absent')
            expected=(self.directory/PACKAGE_FILE).read_bytes()
            if identity=='github' and sha(raw)!=sha(expected): return O('mismatched')
            if identity=='nuget':
                require(self.signature is not None,'public repository signature verifier unavailable')
                with zipfile.ZipFile(io.BytesIO(raw)) as z: require('.signature.p7s' in z.namelist(),'public repository signature absent')
                self.signature(raw)
                if self.readback is not None:
                    require(strict_json(self.readback)['servedArchiveSha256']==sha(raw),'public archive changed after installed qualification')
            return O('matched',e.target_digest) if F.payload_map(raw)==F.payload_map(expected) else O('mismatched')
        if identity=='tag':
            try: tag=self.api.get(f'repos/{REPO}/git/ref/tags/v{VERSION}')
            except self.engine.provider.NotFound: return O('absent')
            return O('matched',e.target_digest) if tag.get('object',{}).get('type')=='commit' and tag.get('object',{}).get('sha')==self.s['source'] else O('mismatched')
        if identity=='draft':
            r=self.release(); return O('matched',e.target_digest) if r else O('absent')
        if identity.startswith('asset:'):
            name=identity.split(':')[1]; release=self.release()
            if release is None: return O('absent')
            _,rows=self.assets(); filename={'package':PACKAGE_FILE,'manifest':MANIFEST,'readback':'ordinary-sdk-release-readback.json'}[name]
            matched=[v for v in rows if v.get('name')==filename]
            if not matched: return O('absent')
            raw=self.api.download_asset(matched[0]['id'])
            return O('matched',e.target_digest) if raw==self.asset_bytes(name) else O('mismatched')
        if identity=='promote':
            r=self.release()
            if not r or r.get('draft') is not False: return O('absent')
            return O('matched',e.target_digest) if all(self.observe(v).state=='matched' for v in self.final_effects[4:7]) else O('mismatched')
        raise Refused('unknown effect')
    def dispatch(self,e):
        logical=logical_id(self.s,e.identity)
        require(self.stage=='finalize' or logical in EFFECTS[:2],'effect outside stage window')
        try:
            if logical in EFFECTS[:2]:
                key=self.github_token if logical=='github' else self.nuget_key
                require(bool(key) and self.runner is not None,'package credential/runner absent')
                path=self.directory/PACKAGE_FILE; require(sha(path.read_bytes())==self.s['candidate']['packageSha256'],'candidate bytes changed before send')
                source='https://nuget.pkg.github.com/FS-GG/index.json' if logical=='github' else 'https://api.nuget.org/v3/index.json'
                self.runner(['dotnet','nuget','push',str(path),'--source',source,'--api-key',key])
            elif logical=='tag': self.api.post(f'repos/{REPO}/git/refs',{'ref':'refs/tags/v'+VERSION,'sha':self.s['source']})
            elif logical=='draft': self.api.post(f'repos/{REPO}/releases',dict(tag_name='v'+VERSION,target_commitish=self.s['source'],name='Coordination CLI '+VERSION,body=self.marker,draft=True,make_latest='false'))
            elif logical.startswith('asset:'):
                name=logical.split(':')[1]; raw=self.asset_bytes(name); filename={'package':PACKAGE_FILE,'manifest':MANIFEST,'readback':'ordinary-sdk-release-readback.json'}[name]
                path=self.directory.parent/('upload-'+filename); path.write_bytes(raw)
                self.api.upload_asset(self.release()['id'],filename,path,sha(raw))
            elif logical=='promote': self.api.patch(f"repos/{REPO}/releases/{self.release()['id']}",{'draft':False,'make_latest':'false'})
            else: raise Refused('unknown effect')
        except Exception:
            self.uncertain=True
            return self.engine.Dispatch('unknown')
        return self.engine.Dispatch('applied')


# NuGet.org validates and signs a push before its flat container serves it; that commonly takes
# minutes. The window matches the order of the Execution pair forward stage (45 min, which
# completed 0.3.1) while fitting the 60-minute working budget of a 90-minute writer job.
OBSERVATION_SECONDS=40*60
OBSERVATION_CADENCE_SECONDS=15


def advance(engine,s,planned,journal,admission,provider,context,*,sleep=time.sleep,clock=time.monotonic):
    """Existing engine owns intent CAS and fence. Unknown sends stop this invocation."""
    fence=engine.execution.BoundIntentFence('sdk-'+str(context['runId'])+'-'+provider.stage)
    deadline=clock()+OBSERVATION_SECONDS
    for _ in range(len(planned)*3+OBSERVATION_SECONDS//OBSERVATION_CADENCE_SECONDS+24):
        result=engine.execution.advance_effects(content_id(s),planned,journal,admission,provider,fence=fence)
        if provider.uncertain: raise Refused('possible-send retained in protected journal; reconcile original; no resend')
        if result=='complete': return {'status':'verified','contentId':content_id(s),'effects':list(provider.stage=='packages' and EFFECTS[:2] or EFFECTS),'sends':list(fence.sends)}
        require(clock()<deadline,'original observation bound exhausted; retain journal; no retry')
        sleep(OBSERVATION_CADENCE_SECONDS)
    raise Refused('finite transition bound exhausted; retain original')


def tag_projection(actual):
    """Viewer-independent policy projection. The REST response omits the default update parameter
    and carries viewer/link metadata (current_user_can_bypass, _links, node_id, timestamps), so only
    the desired keys plus id are hashed; an omitted update parameter means its default false."""
    desired=strict_json((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_bytes())
    require(type(actual) is dict and actual.get('id')==21633398 and 'bypass_actors' in actual,'tag protection incomplete')
    rules=actual.get('rules')
    require(type(rules) is list and all(type(r) is dict for r in rules),'tag protection rules malformed')
    rules=[{**r,'parameters':{'update_allows_fetch_and_merge':False}} if r.get('type')=='update' and set(r)=={'type'} else r for r in rules]
    projection={**{k:actual.get(k) for k in desired},'rules':rules,'id':actual['id']}
    require({k:projection[k] for k in desired}==desired,'tag protection differs')
    return projection


def tag_policy(actual,s):
    require(sha(canonical(tag_projection(actual)))==s.get('forwardTagEvidence',s['liveEvidence']['tagProtection'])['evidenceSha256'],'selected tag protection evidence changed')


def vacant(api,transport,token,s,not_found):
    namespace=api.get('orgs/FS-GG/packages/nuget/fs.gg.coordination.cli')
    require(namespace.get('name','').lower()==PACKAGE.lower() and namespace.get('package_type')=='nuget'
            and namespace.get('repository',{}).get('id')==REPO_ID,'known package namespace/repository binding unknown')
    versions=[]
    for page in range(1,11):
        rows=api.get(f'orgs/FS-GG/packages/nuget/fs.gg.coordination.cli/versions?per_page=100&page={page}')
        require(type(rows) is list and len(rows)<=100 and all(type(r.get('name')) is str for r in rows),'GitHub namespace incomplete')
        versions.extend(r['name'] for r in rows)
        if len(rows)<100: break
    else: raise Refused('GitHub namespace pagination incomplete')
    require(VERSION not in versions,'GitHub version occupied')
    status,raw=transport.get(F.feed_url('nuget',PACKAGE),headers={},cap=F.MAX_JSON)
    require(status==200 and VERSION not in F.parse_versions(raw),'public namespace occupied/unknown')
    for feed in ('github','nuget'):
        require(F.fetch_feed(transport,token,VERSION,feed,PACKAGE) is None,'feed cell occupied')
    try: api.get(f'repos/{REPO}/git/ref/tags/v{VERSION}')
    except not_found: pass
    else: raise Refused('fresh tag/release occupied')
    require(find_release(api,not_found) is None,'fresh tag/release occupied')


def public_receipt(s,context,raw):
    v=strict_json(raw)
    require(type(v) is dict and set(v)=={'schema','contentId','publisher','runId','source','packageSha256','servedArchiveSha256','signature','installedCliCases','publicOnly','consumerAdoption'},'public receipt fields differ')
    require(v['schema']=='fsgg.coordination.ordinary-sdk-public-load/1' and v['contentId']==content_id(s) and v['publisher']==context['sha']
            and v['runId']==context['runId'] and v['source']==s['source'] and v['packageSha256']==s['candidate']['packageSha256']
            and v['signature']=='verified' and v['installedCliCases']==15 and v['publicOnly'] is True and v['consumerAdoption'] is False,'public receipt binding differs')
    F.hexid(v['servedArchiveSha256'],64,'public archive')
    return v


def acquire_public(api,transport,token,s,context):
    jobs=api.get(f"repos/{REPO}/actions/runs/{context['runId']}/attempts/1/jobs?per_page=100")
    rows=jobs.get('jobs'); require(type(rows) is list and jobs.get('total_count')==len(rows)<100,'publisher job census incomplete')
    matching=[j for j in rows if j.get('name')=='sdk_profile_public_load']
    require(len(matching)==1 and matching[0].get('conclusion')=='success' and matching[0].get('run_attempt')==1 and matching[0].get('head_sha')==context['sha'],'public load native job absent')
    rows=api.get(f"repos/{REPO}/actions/runs/{context['runId']}/artifacts?per_page=100")
    values=rows.get('artifacts'); require(type(values) is list and rows.get('total_count')==len(values)<100,'public artifact census incomplete')
    matching=[a for a in values if a.get('name')=='ordinary-sdk-public-load-'+str(context['runId'])]
    require(len(matching)==1,'public artifact ambiguous/absent'); a=matching[0]
    require(a.get('expired') is False and a.get('workflow_run',{}).get('id')==context['runId'] and a.get('workflow_run',{}).get('head_sha')==context['sha'],'public artifact native binding differs')
    status,raw=transport.get(f"https://api.github.com/repos/{REPO}/actions/artifacts/{a['id']}/zip",headers=F._api_headers(token),cap=F.MAX_JSON)
    require(status==200 and a.get('digest')=='sha256:'+sha(raw),'public artifact bytes differ')
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        require(len(z.infolist())==1 and z.namelist()==['ordinary-sdk-public-load.json'] and z.infolist()[0].file_size<=65536,'public artifact population differs')
        receipt=z.read(z.infolist()[0])
    public_receipt(s,context,receipt)
    return receipt


class NativeBudget:
    # One finite native budget per command: preparation plus the whole observation window.
    def __init__(self,*,clock=time.monotonic,seconds=OBSERVATION_SECONDS+10*60,requests=2048):
        self.clock,self.deadline,self.remaining=clock,clock()+seconds,requests
    def reserve(self):
        require(self.clock()<self.deadline and self.remaining>0,'original native request/time bound exhausted')
        self.remaining-=1
    def returned(self):require(self.clock()<self.deadline,'native response exceeded original deadline; effect unconfirmed')


def bounded_api(engine,*,budget=None,opener=None):
    """Same shared API port with bounded JSON/request count and no mutation retry/redirect."""
    import urllib.request
    import urllib.parse
    import urllib.error
    budget=budget or NativeBudget()
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self,*args,**kwargs):raise Refused('native API redirect refused; preserve possible send')
    open_request=opener or urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect()).open
    base=F.make_api_class(engine)
    class API(base):
        def _request(self,url,method='GET',body=None,content_type='application/json'):
            parsed=urllib.parse.urlsplit(url)
            require(parsed.scheme=='https' and not parsed.fragment and not parsed.username and not parsed.password,'native API origin differs')
            asset=parsed.netloc=='uploads.github.com' and parsed.path.startswith('/repos/'+REPO+'/releases/') and parsed.path.endswith('/assets')
            normal=parsed.netloc=='api.github.com' and (parsed.path.startswith('/repos/'+REPO+'/') or parsed.path=='/repos/'+REPO
                    or parsed.path.startswith('/repos/FS-GG/FS.GG.Coordination.Authority/') or parsed.path=='/repos/FS-GG/FS.GG.Coordination.Authority'
                    or parsed.path=='/orgs/FS-GG/packages/nuget/fs.gg.coordination.cli' or parsed.path.startswith('/orgs/FS-GG/packages/nuget/fs.gg.coordination.cli/versions'))
            require(normal or asset,'native endpoint outside SDK/Authority scope')
            require(method in ('GET','POST','PATCH') and (body is None or len(body)<= (F.MAX_PACKAGE if asset else F.MAX_JSON)),'native request shape/size differs')
            budget.reserve()
            request=urllib.request.Request(url,data=body,method=method,headers={**F._api_headers(self.token),'Content-Type':content_type,'User-Agent':'fsgg-ordinary-sdk-publisher'})
            try:
                with open_request(request,timeout=min(60,max(0.001,budget.deadline-budget.clock()))) as response:
                    require(response.status in (200,201),'native HTTP outcome unconfirmed')
                    length=getattr(response,'headers',{}).get('Content-Length')
                    require(length is None or (length.isdigit() and int(length)<=F.MAX_JSON),'native declared JSON length exceeds bound')
                    raw=response.read(F.MAX_JSON+1)
                    require(length is None or len(raw)==int(length),'native JSON response length incomplete')
                    require(len(raw)<=F.MAX_JSON,'native JSON response exceeds bound')
                budget.returned();return raw
            except urllib.error.HTTPError as error:
                try:status=error.code
                finally:error.close()
                budget.returned()
                if status==404:raise engine.provider.NotFound('native resource absent') from None
                raise Refused('native HTTP '+str(status)+'; effect unconfirmed') from None
        def get(self,path):return strict_json(self._request('https://api.github.com/'+path))
        def post(self,path,body):return strict_json(self._request('https://api.github.com/'+path,'POST',canonical(body)))
        def patch(self,path,body):return strict_json(self._request('https://api.github.com/'+path,'PATCH',canonical(body)))
        def download_asset(self,asset_id):
            budget.reserve();raw=super().download_asset(asset_id);budget.returned();return raw
    return API


def effect_owners(state):
    owners={}
    for key,value in state.effects.items():
        require(value in ('intent','verified'),'unknown journal effect state')
        matches=[logical for logical in EFFECTS if re.fullmatch(re.escape(logical)+r'/run-[1-9][0-9]*/publisher-[0-9a-f]{40}',key)]
        require(len(matches)==1 and matches[0] not in owners,'journal has unknown or duplicate logical effect')
        owners[matches[0]]=key
    return owners


def forward_header(admission):
    f=admission['forward']
    require(type(f) is dict and set(f)=={'originalRun','originalPublisher','contentId','checkpoint','tagEvidence','authorization'},'explicit original-forward admission absent')
    F.positive(f['originalRun'],'original run');F.hexid(f['originalPublisher'],40,'original publisher')
    require(type(f['contentId']) is str and re.fullmatch('sha256:[0-9a-f]{64}',f['contentId']),'original content identity absent')
    F._evidence(f['tagEvidence'],'fresh forward tag evidence');F._evidence(f['authorization'],'original-forward admission')
    cp=f['checkpoint'];require(type(cp) is dict and set(cp)=={'generation','effects'} and type(cp['effects']) is dict,'forward checkpoint absent')
    F.positive(cp['generation'],'checkpoint generation')
    effect_owners(type('Checkpoint',(),{'effects':cp['effects']})())
    return f


def forward_selection(admission,s,context,api):
    f=forward_header(admission)
    original=api.get(f"repos/{REPO}/actions/runs/{f['originalRun']}")
    require(f['originalRun']!=context['runId'] and original.get('id')==f['originalRun'] and original.get('repository',{}).get('id')==REPO_ID
            and original.get('path')==WORKFLOW and original.get('head_sha')==f['originalPublisher'] and original.get('run_attempt')==1
            and original.get('event')=='workflow_dispatch' and original.get('head_branch')=='main'
            and original.get('actor',{}).get('login')==s['operator'] and original.get('status')=='completed','original caller is not terminal/matched')
    s=runtime_selection(s,{'sha':f['originalPublisher'],'runId':f['originalRun']})
    require(f['contentId']==content_id(s),'original-forward content binding differs')
    cp=f['checkpoint'];require(type(cp) is dict and set(cp)=={'generation','effects'} and type(cp['effects']) is dict,'forward checkpoint absent')
    F.positive(cp['generation'],'checkpoint generation')
    effect_owners(type('Checkpoint',(),{'effects':cp['effects']})())
    return {**s,'caller':{'runId':context['runId'],'publisher':context['sha']},'forwardTagEvidence':f['tagEvidence']}


def bind_checkpoint(s,state,checkpoint):
    require(state.content_id==content_id(s) and state.generation>=checkpoint['generation'],'original journal/checkpoint differs')
    owners=effect_owners(state)
    for key,value in checkpoint['effects'].items():
        require(state.effects.get(key)==value or (value=='intent' and state.effects.get(key)=='verified'),'checkpoint regressed or lost original effect')
    caller=s['caller'];suffix='/run-'+str(caller['runId'])+'/publisher-'+caller['publisher']
    require(all(key in checkpoint['effects'] or key.endswith(suffix) for key in state.effects),'effects advanced outside selected forward caller')
    return {**s,'effectOwners':owners}


def owner_context(identity):
    matched=re.search(r'/run-([1-9][0-9]*)/publisher-([0-9a-f]{40})$',identity)
    require(matched is not None,'original effect caller absent')
    return {'sha':matched[2],'runId':int(matched[1])}
