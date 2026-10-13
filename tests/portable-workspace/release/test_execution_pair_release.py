#!/usr/bin/env python3
"""Run actual pair admission/archive/readback guards with inert good and bad inputs."""
from __future__ import annotations
import copy
import hashlib
import contextlib
import io
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import tempfile
import zipfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
IDS = ('FS.GG.Execution.Contracts', 'FS.GG.Execution.Processes')

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, separators=(',', ':')))

def heredoc(marker,workflow='publish'):
    text = (ROOT/('.github/workflows/callable-cli-release-'+workflow+'.yml')).read_text()
    code = text.split("          python3 - <<'"+marker+"'\n",1)[1].split('          '+marker+'\n',1)[0]
    return compile('\n'.join(line[10:] for line in code.splitlines()),marker,'exec')

def admission():
    return {'schema':'fsgg.coordination.execution-pair-admission/1','profile':'execution-pair','enabled':True,
            'operation':'publish-execution-pair-020-fixture','version':'0.2.0','tag':'execution/v0.2.0',
            'source':'a'*40,'tree':'b'*40,'protectedMerge':'a'*40,'runId':1,'jobId':2,'artifactId':3,
            'archiveSha256':'c'*64,'manifestSha256':'d'*64,'packages':{id:'e'*64 for id in IDS},
            'namespaceEntitlement':{'accepted':True,'packageIds':sorted(IDS),'nugetUser':'fixture-user',
               'githubRepository':'FS-GG/FS.GG.Coordination','publisherWorkflow':'.github/workflows/callable-cli-release-publish.yml','evidenceSha256':'f'*64},
            'tagProtection':{'rulesetId':4,'policySha256':'1'*64}}

def installed_isolation_diagnostic_controls():
    # Inert SDK substitute drives the actual maintained caller; no CLR/native acceptance.
    check = runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))['check_execution_pair']
    for mode in ('good', 'bare-folder', 'escaped-path', 'extra-folder', 'sdk-folder', 'missing-folder',
                 'descendant-folder', 'parent-folder', 'double-slash', 'case-variant', 'duplicate-folders',
                 'trailing-slash', 'oversized'):
        with tempfile.TemporaryDirectory(prefix='pair-isolation-diagnostic-test-') as temporary:
            candidate=Path(temporary); rows=[]
            for id in IDS:
                archive=candidate/(id+'.0.2.0.nupkg')
                with zipfile.ZipFile(archive,'w') as z: z.writestr('lib/net10.0/'+id+'.dll',id.encode())
                rows.append({'packageId':id,'file':archive.name,'sha256':hashlib.sha256(archive.read_bytes()).hexdigest()})
            write(candidate/'execution-pair-release-manifest.json',{'profile':'execution-pair','version':'0.2.0','packages':rows})
            calls=[]; observed={}
            def fake_sdk(command, *, cwd, env):
                calls.append(command[1]); root=Path(cwd); packages=str(root/'packages'); folder=packages+os.sep
                assert env['NUGET_PACKAGES']==packages and env['TMPDIR']==str(root/'tmp')
                if command[1]=='restore': (root/'effective-isolation.txt').write_text('PAIR_EFFECTIVE_ISOLATION_OK\n')
                if command[1]=='build':
                    restore={'packagesPath':packages,'fallbackFolders':[],'sources':{str(candidate.resolve()):{},'https://api.nuget.org/v3/index.json':{}}}
                    folders={folder:{}}
                    if mode=='escaped-path': restore['packagesPath']='/outside-cache'
                    if mode=='extra-folder': folders['/sdk-home/fallback/']={}; restore['fallbackFolders']=['/sdk-home/fallback/']
                    if mode=='bare-folder': folders={packages:{}}
                    if mode=='sdk-folder': folders={'/sdk-home/fallback/':{}}
                    if mode=='missing-folder': folders={}
                    if mode=='descendant-folder': folders={folder+'extra':{}}
                    if mode=='parent-folder': folders={folder+'..':{}}
                    if mode=='double-slash': folders={folder+os.sep:{}}
                    if mode=='case-variant': folders={packages.upper():{}}
                    if mode=='duplicate-folders': folders={packages:{},folder:{}}
                    if mode=='trailing-slash': restore['packagesPath']=folder
                    if mode=='oversized': restore['packagesPath']='/'+'x'*5000
                    observed.update({'expectedPackagesPath':packages,'expectedPackageFolders':sorted([packages,folder]),
                                     'observedPackagesPath':restore['packagesPath'],'observedPackageFolders':list(folders),
                                     'observedFallbackFolders':restore['fallbackFolders'],'observedSources':list(restore['sources'])})
                    write(root/'obj/project.assets.json',{'packageFolders':folders,'project':{'restore':restore},
                                                        'libraries':{name:{'type':'package'} for name in ['FSharp.Core/10.1.302',*(id+'/0.2.0' for id in IDS)]}})
                    references=[]
                    for id in IDS:
                        compiler=root/'packages'/id.lower()/'0.2.0/lib/net10.0'/(id+'.dll'); compiler.parent.mkdir(parents=True);compiler.write_bytes(id.encode()); references.append(str(compiler))
                        target=root/'bin/Release/net10.0'/(id+'.dll');target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(id.encode())
                    (root/'compiler-pair-references.txt').write_text('\n'.join(references)+'\n')
                return subprocess.CompletedProcess(command,0,'EXECUTION_PAIR_INSTALLED_LOAD_OK\n' if len(calls)==3 else '', '')
            original=check.__globals__['run']; check.__globals__['run']=fake_sdk; output=candidate/'proof.json'
            try:
                try: check(candidate,'candidate',output); refused=False
                except RuntimeError as error:
                    refused=True; message=str(error); prefix='effective NuGet package/fallback folder escaped isolation: '
                    assert message.startswith(prefix),message
                    if mode=='oversized': assert message==prefix+'observed path fields exceed the 4096-byte diagnostic bound'
                    else: assert json.loads(message[len(prefix):])==observed
                    assert len(message.encode())<=len(prefix)+4096
            finally: check.__globals__['run']=original
            accepted=mode in ('good','bare-folder')
            assert refused==(not accepted) and output.exists()==accepted,mode
            assert calls[:2]==['restore','build']
            assert (len(calls)==3 and calls[2].endswith('/PairInstalled.dll')) if accepted else len(calls)==2

def pair_observation_controls():
    helper = runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))
    observe = helper['observe_execution_pair']; original = helper['PAIR_OBSERVATION_CANDIDATE']
    assert original == {
        'source':'c6764ac0cd62cb14372c89c14234292315000380','runId':38274623070,
        'runAttempt':1,'jobId':114896943746,'artifactId':11759090717,
        'archiveSha256':'6aedfb7e4eda796fdea3fc0326003a6f90f859352d7394d311f4c5d867687819',
        'manifestSha256':'4a09ead11044d34cccb2c305c563d95bf0781c918433d19c0bc631ee8b479e18',
        'packages':{'FS.GG.Execution.Contracts':'62b1f85b8622d6719f086d73f4dbf29bd251dbb083a9b9588535d6b86531665d',
                    'FS.GG.Execution.Processes':'20d09f3bc71c78c46809931a8d1f7b4009d381fb0c4f203db2d7c846266e4f41'}}
    publisher = '9'*40
    env = {'GITHUB_REPOSITORY':'FS-GG/FS.GG.Coordination','GITHUB_REF':'refs/heads/main',
           'GITHUB_RUN_ATTEMPT':'1','GITHUB_SHA':publisher,'REQUESTED_PUBLISHER':publisher,
           'REQUESTED_SOURCE':original['source'],'REQUESTED_SHA256':original['manifestSha256'],
           'REQUESTED_MERGE':'','OPERATION':'','EXECUTION_PAIR':'true','PREFLIGHT_ONLY':'true',
           'CLI_ONLY':'false','READBACK_ONLY':'false','SELECTED_NUGET_USER':'fixture-user'}
    run = {'id':original['runId'],'head_sha':original['source'],'head_branch':'main',
           'event':'workflow_dispatch','run_attempt':1,'status':'completed','conclusion':'success',
           'path':'.github/workflows/callable-cli-release-prepare.yml','repository':{'id':1346720714},'head_repository':{'id':1346720714}}
    jobs = {'total_count':1,'jobs':[{'id':original['jobId'],'run_id':original['runId'],'run_attempt':1,
            'head_sha':original['source'],'name':'Prepare the routine Execution pair without publication',
            'status':'completed','conclusion':'success'}]}
    artifact = {'id':original['artifactId'],'expired':False,'size_in_bytes':150889,
                'name':'execution-pair-'+original['source'],'digest':'sha256:'+original['archiveSha256'],
                'workflow_run':{'id':original['runId'],'head_sha':original['source']}}
    desired = json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_bytes());desired['id']=21633398
    input_changes = {'wrong-repository':('GITHUB_REPOSITORY','foreign/repo'),'wrong-ref':('GITHUB_REF','refs/heads/other'),
                     'second-attempt':('GITHUB_RUN_ATTEMPT','2'),'wrong-source':('REQUESTED_SOURCE','a'*40),
                     'malformed-source':('REQUESTED_SOURCE','true'),'wrong-publisher':('REQUESTED_PUBLISHER','8'*40),
                     'wrong-manifest':('REQUESTED_SHA256','0'*64),'wrong-merge':('REQUESTED_MERGE','a'*40),
                     'mixed-cli':('CLI_ONLY','true'),'mixed-readback':('READBACK_ONLY','true'),
                     'effect-operation':('OPERATION','publish'),'not-preflight':('PREFLIGHT_ONLY','false'),
                     'wrong-user':('SELECTED_NUGET_USER','user\nkey')}
    modes = ['good','disabled-admission','moved-main','wrong-run','foreign-repository','wrong-job','jobs-pagination',
             'foreign-artifact','expired','changed-hash','occupied','denied','malformed','pagination','budget','network',
             'wrong-protection','wrong-owner'] + list(input_changes)
    for mode in modes:
        selected = dict(env)
        if mode in input_changes: selected[input_changes[mode][0]]=input_changes[mode][1]
        calls = []
        def read(url, authenticated):
            calls.append((url,authenticated))
            assert len(calls)<=20
            if mode=='budget' and len(calls)==3: raise helper['PairObservationBound']('fixture budget exhausted')
            if mode=='network' and len(calls)==3: raise OSError('fixture unavailable')
            status=200
            if '/git/ref/heads/main' in url: data={'object':{'sha':'8'*40 if mode=='moved-main' and len(calls)>1 else publisher}}
            elif '/attempts/1/jobs?' in url:
                data=copy.deepcopy(jobs)
                if mode=='wrong-job':data['jobs'][0]['id']=True
                if mode=='jobs-pagination':data['total_count']=101
            elif '/actions/runs/' in url:
                data=copy.deepcopy(run)
                if mode=='wrong-run':data['run_attempt']=2
                if mode=='foreign-repository':data['head_repository']['id']=1
            elif '/actions/artifacts/' in url:
                data=copy.deepcopy(artifact)
                if mode=='foreign-artifact':data['id']=1
                if mode=='expired':data['expired']=True
                if mode=='changed-hash':data['digest']='sha256:'+'0'*64
            elif '/versions?' in url:
                data=[{'name':'0.2.0'}] if mode=='occupied' else [{'name':'0.1.0'}]*100 if mode=='pagination' else []
                if mode=='denied': status=403;data=None
                if mode=='malformed':data={'versions':[]}
            elif '/packages/nuget/' in url:
                data={'name':url.rsplit('/',1)[1],'package_type':'nuget','owner':{'login':'other' if mode=='wrong-owner' else 'FS-GG'}}
            elif '/v3-flatcontainer/' in url:
                assert not authenticated
                status=404;data=None
            elif '/git/ref/tags/' in url or '/releases/tags/' in url:status=404;data=None
            elif '/rulesets/' in url:
                data=copy.deepcopy(desired)
                if mode=='wrong-protection':data['enforcement']='disabled'
            elif url.endswith('/user'): data={'login':'fixture-github'}
            else:raise AssertionError(url)
            return status,data,'a'*64
        with tempfile.TemporaryDirectory(prefix='pair-observation-test-') as temporary:
            out=Path(temporary)/'observation.json'
            # No SDK/subprocess or credential/login path can be invoked by this real collector.
            with patch('subprocess.run',side_effect=AssertionError('workload forbidden')), patch('urllib.request.urlopen',side_effect=AssertionError('real network forbidden')):
                result=observe(out,read=read,environment=selected)
            report=json.loads(out.read_bytes());rows={r['name']:r for r in report['observations']}
            assert report['candidate']==original
            assert report['publicationAuthorized'] is False and report['versionReserved'] is False and report['publicationEligible'] is False
            assert report['namespaceEntitlement']=='unknown' and report['trustedPublishingLoginSucceeded'] is None
            assert report['credentialProbe']=='not-selected' and report['reportingOutcome']=='written'
            success=mode in ('good','disabled-admission')
            assert (result==0)==success,(mode,report)
            if mode in input_changes: assert not calls and rows['candidate-run']['state']=='blocked'
            elif mode=='budget':
                assert len(calls)==3 and rows['candidate-artifact']['state']=='budget-limited'
                assert report['firstFailure']['name']=='candidate-job'
            else:
                assert len(calls)==15 and rows['main-after']['state'] in ('passed','foreign')
                if mode=='denied':assert rows['github-cell-FS.GG.Execution.Contracts']['state']=='denied' and rows['nuget-cell-FS.GG.Execution.Processes']['state']=='absent'
                if mode=='occupied':assert rows['github-cell-FS.GG.Execution.Contracts']['state']=='occupied'
                if mode=='pagination':assert rows['github-cell-FS.GG.Execution.Contracts']['state']=='unknown'
                if mode=='network':assert report['firstFailure']['name']=='candidate-job' and rows['candidate-artifact']['state']=='passed'
            # Report-write failure has its own terminal exception; no new reads/retries.
            if mode in ('good','denied'):
                count=len(calls);calls.clear();stderr=io.StringIO()
                with contextlib.redirect_stderr(stderr):
                    assert observe(Path(temporary)/'missing'/'report.json',read=read,environment=selected)==2
                failure=json.loads(stderr.getvalue())
                assert failure['reportingOutcome']=='failed' and failure['firstFailure']==report['firstFailure']
                assert failure['cleanupOutcome']=='no-workload-or-credential-created'
                assert len(calls)==count
    # Actual finite transport: oversized/timeout/denied/redirect/malformed bodies never pass.
    reader_class=helper['PairMetadataReader']
    class Response:
        def __init__(self,raw,code=200):self.raw=raw;self.code=code
        def __enter__(self):return self
        def __exit__(self,*args):return False
        def read(self,limit):assert limit==1048577;return self.raw[:limit]
    for mode in ('good','oversize','malformed','denied','redirect','deadline','requests','wrong-origin','credential-origin','request-timeout'):
        reader=reader_class();seen=[]
        def open_request(request,timeout):
            if mode=='request-timeout':raise TimeoutError('fixture timeout')
            seen.append(request);assert request.method=='GET' and 0<timeout<=15
            return Response(b'x'*1048577 if mode=='oversize' else b'bad' if mode=='malformed' else b'{}',403 if mode=='denied' else 302 if mode=='redirect' else 200)
        reader.opener.open=open_request
        if mode=='deadline':reader.deadline=0
        if mode=='requests':reader.count=20
        try:
            status,data,_=reader('https://foreign.invalid/' if mode=='wrong-origin' else 'https://api.nuget.org/v3-flatcontainer/x/index.json' if mode=='credential-origin' else 'https://api.github.com/user',mode=='credential-origin')
            refused=False
        except (RuntimeError,ValueError,OSError):refused=True
        assert refused==(mode not in ('good','denied','redirect')),mode
        if not refused:assert status==({'denied':403,'redirect':302}.get(mode,200)) and (data=={} if mode=='good' else data is None)
    workflow=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    text=workflow.split('  execution_pair_preflight:\n',1)[1].split('  execution_pair:\n',1)[0]
    permissions=text.split('    permissions:\n',1)[1].split('    steps:\n',1)[0]
    assert permissions=='      contents: read\n      actions: read\n      packages: read\n'
    assert '    timeout-minutes: 5\n' in text and 'needs:' not in text
    for forbidden in ('NuGet/login','setup-dotnet','dotnet ','gh ','git tag','nuget push','actions/attest','id-token:','write'):
        assert forbidden not in text,forbidden
    for flags in __import__('itertools').product((False,True),repeat=4):
        execution,preflight,cli,readback=flags
        values={'execution_pair':execution,'preflight_only':preflight,'cli_only':cli,'readback_only':readback}
        active=[]
        for name in ('execution_pair_preflight','execution_pair','preflight','publish','cli_successor'):
            expression=next(line[8:] for line in workflow.split('\n  '+name+':\n',1)[1].splitlines() if line.startswith('    if: ')).replace('&&',' and ').replace('!= true','!= True').replace('== true','== True')
            expression=re.sub(r'!inputs\.([a-z_]+)',r'not inputs.\1',expression)
            for key,value in values.items():expression=expression.replace('inputs.'+key,str(value))
            if eval(expression,{'__builtins__':{}}):active.append(name)
        if execution and preflight:
            assert active==(['execution_pair_preflight'] if not cli and not readback else []),flags
        if execution and not preflight:
            assert active==(['execution_pair'] if not cli and not readback else []),flags
    a=json.loads((ROOT/'eng/execution-pair-release-admission.json').read_bytes())
    assert a['enabled'] is False and a['operation'] is None and a['namespaceEntitlement'] is None
    print('PAIR_OBSERVATION_INERT_CONTROLS passed cases='+str(len(modes))+' transport=10 selectors=16 read-only/no-eligibility')

def main():
    pair_observation_controls()
    installed_isolation_diagnostic_controls()
    workflow=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    prepare=(ROOT/'.github/workflows/callable-cli-release-prepare.yml').read_text()
    assert "if: inputs.preparation_mode == 'portable'" in prepare
    assert "if: inputs.preparation_mode == 'cli-only'" in prepare
    assert "if: inputs.preparation_mode == 'execution-pair'" in prepare
    for job in ('preflight','publish','cli_successor'):
        gate=workflow.split('  '+job+':\n',1)[1].splitlines()
        gate=next(line for line in gate if line.startswith('    if:'))
        assert '!inputs.execution_pair' in gate
    job=workflow.split('\n  execution_pair:\n',1)[1]
    for gate in ('test "$CLI_ONLY" = false','test "$PREFLIGHT_ONLY" = false','test "$READBACK_ONLY" = false'):
        assert job.index(gate)<job.index('Attest only')<job.index('Push both retained')
    assert 'nuget push' not in prepare
    assert 'skip-duplicate' not in job and 'dotnet pack' not in job
    actual=heredoc('PY_PAIR_ADMISSION'); base=admission()
    cases={'good':lambda a:None,'disabled':lambda a:a.update(enabled=False),
           'wrong-profile':lambda a:a.update(profile='cli'),'wrong-member':lambda a:a['packages'].pop(IDS[1]),
           'foreign-source':lambda a:a.update(source='z'*40),'missing-capability':lambda a:a.update(namespaceEntitlement=None),
           'not-entitled':lambda a:a['namespaceEntitlement'].update(accepted=False),
           'wrong-entitlement-pair':lambda a:a['namespaceEntitlement'].update(packageIds=[IDS[0]]),
           'wrong-workflow':lambda a:a['namespaceEntitlement'].update(publisherWorkflow='other.yml'),
           'unknown-protection':lambda a:a.update(tagProtection=None),
           'fake-native-id':lambda a:a.update(runId=True),'unexpected-key':lambda a:a.update(ignored=True)}
    for name,mutate in cases.items():
        with tempfile.TemporaryDirectory(prefix='pair-admission-test-') as temporary:
            root=Path(temporary); a=copy.deepcopy(base);mutate(a)
            write(root/'eng/execution-pair-release-admission.json',a)
            environment={'OPERATION':base['operation'],'REQUESTED_SOURCE':base['source'],'REQUESTED_MERGE':base['protectedMerge'],
                         'REQUESTED_SHA256':base['manifestSha256'],'REQUESTED_PUBLISHER':'9'*40,'GITHUB_ENV':str(root/'env')}
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,environment,clear=True):
                    try:exec(actual,{})
                    except (AssertionError,KeyError,TypeError):refused=True
            finally:os.chdir(old)
            assert refused == (name!='good'), name
            assert (root/'env').exists()==(name=='good'),name
    extract=runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))['extract_execution_pair_artifact']
    with tempfile.TemporaryDirectory(prefix='pair-archive-test-') as temporary:
        root=Path(temporary);readback=root/'readback';readback.mkdir()
        a=admission();manifest={'schema':'fsgg.coordination.execution-pair-release-preparation/1','profile':'execution-pair',
            'version':'0.2.0','tag':'execution/v0.2.0','sourceCommit':a['source'],'sourceTree':a['tree'],
            'publicationAuthorized':False,'tagAuthorized':False,'ownership':{'reservation':'unreserved'},
            'packages':[{'packageId':id,'file':id+'.0.2.0.nupkg','sha256':hashlib.sha256(id.encode()).hexdigest()} for id in IDS]}
        payloads={id+'.0.2.0.nupkg':id.encode() for id in IDS}
        payloads['execution-pair-release-manifest.json']=json.dumps(manifest,separators=(',',':')).encode()
        a['manifestSha256']=hashlib.sha256(payloads['execution-pair-release-manifest.json']).hexdigest()
        a['packages']={row['packageId']:row['sha256'] for row in manifest['packages']}
        run={'id':1,'repository':{'id':1346720714},'head_repository':{'id':1346720714},'head_branch':'main',
             'event':'workflow_dispatch','head_sha':a['source'],'run_attempt':1,'status':'completed','conclusion':'success',
             'path':'.github/workflows/callable-cli-release-prepare.yml'}
        jobs={'total_count':1,'jobs':[{'id':2,'run_id':1,'run_attempt':1,'head_sha':a['source'],
             'name':'Prepare the routine Execution pair without publication','status':'completed','conclusion':'success'}]}
        for mode in ('good','extra-member','duplicate-member','wrong-hash','wrong-run','wrong-job','expired'):
            archive=readback/'candidate.zip'
            with zipfile.ZipFile(archive,'w') as z:
                for filename,data in payloads.items():z.writestr(filename,data)
                if mode=='extra-member':z.writestr('unselected',b'bad')
                if mode=='duplicate-member':z.writestr(next(iter(payloads)),b'duplicate')
            a['archiveSha256']=hashlib.sha256(archive.read_bytes()).hexdigest()
            current=copy.deepcopy(a)
            if mode=='wrong-hash':current['packages'][IDS[0]]='0'*64
            artifact={'id':3,'expired':mode=='expired','name':'execution-pair-'+a['source'],'digest':'sha256:'+a['archiveSha256'],
                      'workflow_run':{'id':1,'head_sha':a['source']}}
            selectedrun=copy.deepcopy(run)
            if mode=='wrong-run':selectedrun['run_attempt']=2
            selectedjobs=copy.deepcopy(jobs)
            if mode=='wrong-job':selectedjobs['jobs'][0]['name']='Prepare the CLI-only successor without publication'
            write(root/'eng/execution-pair-release-admission.json',current)
            write(readback/'run.json',selectedrun);write(readback/'jobs.json',selectedjobs);write(readback/'artifact.json',artifact)
            out=root/mode;out.mkdir();old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,{'READBACK_OUTPUT':str(readback),'CANDIDATE_OUTPUT':str(out)},clear=True):
                    try:extract(Path('eng/execution-pair-release-admission.json'),readback,out)
                    except (AssertionError,KeyError,TypeError):refused=True
            finally:os.chdir(old)
            assert refused==(mode!='good'),mode
    # Exercise the real nonpublishing installed job's input/archive bindings.
    installed_job=prepare.split('  execution_pair_installed:\n',1)[1]
    assert "if: inputs.preparation_mode == 'execution-pair-installed'" in installed_job
    assert '      contents: read\n      actions: read\n' in installed_job
    assert 'id-token:' not in installed_job and 'packages:' not in installed_job
    assert 'nuget push' not in installed_job and 'dotnet pack' not in installed_job
    assert 'gh release' not in installed_job and 'git tag' not in installed_job
    assert installed_job.index('--execution-pair-artifact') < installed_job.index('candidate "$READBACK_OUTPUT/candidate-load.json"')
    bind=heredoc('PY_PAIR_INSTALLED_BINDINGS','prepare')
    for mode in ('good','changed-archive','foreign-source','wrong-job','enabled'):
        with tempfile.TemporaryDirectory(prefix='pair-installed-bindings-test-') as temporary:
            root=Path(temporary);readback=root/'readback';readback.mkdir()
            m={'sourceCommit':'a'*40,'sourceTree':'b'*40,'packages':[{'packageId':id,'sha256':str(i+1)*64} for i,id in enumerate(IDS)]}
            with zipfile.ZipFile(readback/'candidate.zip','w') as z:z.writestr('execution-pair-release-manifest.json',json.dumps(m))
            a=admission();a['enabled']=mode=='enabled'
            write(root/'eng/execution-pair-release-admission.json',a)
            write(readback/'jobs.json',{'jobs':[{'id':2,'name':'foreign' if mode=='wrong-job' else 'Prepare the routine Execution pair without publication'}]})
            env={'READBACK_OUTPUT':str(readback),'ARCHIVE_SHA':'0'*64 if mode=='changed-archive' else hashlib.sha256((readback/'candidate.zip').read_bytes()).hexdigest(),
                 'SOURCE_A':'c'*40 if mode=='foreign-source' else 'a'*40,'CANDIDATE_RUN':'1','CANDIDATE_ARTIFACT':'3'}
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,env,clear=True):
                    try:exec(bind,{})
                    except (AssertionError,KeyError,TypeError):refused=True
            finally:os.chdir(old)
            assert refused==(mode!='good'),mode
            assert (readback/'candidate-bindings.json').exists()==(mode=='good'),mode
    gate=installed_job.split('          set -euo pipefail\n',1)[1].split('      - name: Run actual shared pair guards',1)[0]
    gate='set -euo pipefail\n'+'\n'.join(line[10:] if line.startswith('          ') else line for line in gate.splitlines())
    for mode in ('good','package-change','bad-source','bad-run','bad-hash','moved-executor','diff-failure','metadata-change'):
        with tempfile.TemporaryDirectory(prefix='pair-installed-source-test-') as temporary:
            root=Path(temporary);stub=root/'git'
            stub.write_text("""#!/usr/bin/python3
import os,sys
args=sys.argv[1:]
if args[0]=='diff' and os.environ['FIXTURE_MODE']=='diff-failure':sys.exit(1)
if args[0]=='rev-parse':print(os.environ['GITHUB_SHA'])
if args[0]=='diff':print('src/FS.GG.Execution.Processes/ProcessSessions.fs' if os.environ['FIXTURE_MODE']=='package-change' else 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json' if os.environ['FIXTURE_MODE']=='metadata-change' else '.github/workflows/callable-cli-release-prepare.yml')
""")
            stub.chmod(0o700)
            env={**os.environ,'PATH':str(root)+os.pathsep+os.environ['PATH'],'FIXTURE_MODE':mode,
                 'GITHUB_REF':'refs/heads/main','GITHUB_RUN_ATTEMPT':'1','GITHUB_REPOSITORY':'FS-GG/FS.GG.Coordination','GITHUB_SHA':'9'*40,
                 'EXECUTOR_SOURCE':'8'*40 if mode=='moved-executor' else '9'*40,
                 'SOURCE_A':'z'*40 if mode=='bad-source' else 'a'*40,'CANDIDATE_RUN':'true' if mode=='bad-run' else '1',
                 'CANDIDATE_ARTIFACT':'3','ARCHIVE_SHA':'x'*64 if mode=='bad-hash' else 'c'*64,'CANDIDATE_VERSION':'0.2.0',
                 'CANDIDATE_OUTPUT':str(root/'candidate'),'READBACK_OUTPUT':str(root/'readback'),'GITHUB_ENV':str(root/'env')}
            result=subprocess.run(['bash','-c',gate],env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode in {'good','metadata-change'}),(mode,result.stderr)
            assert (root/'env').exists()==(mode in {'good','metadata-change'}),mode
    publisher_source=job.split('      - name: Bind pair A ancestry and publication-only B changes\n',1)[1].split('      - name: Authenticate and extract',1)[0]
    publisher_source=publisher_source.split('          set -euo pipefail\n',1)[1]
    publisher_source='set -euo pipefail\n'+'\n'.join(line[10:] if line.startswith('          ') else line for line in publisher_source.splitlines())
    for mode in ('good','proof-change','diff-failure','package-change','metadata-change'):
        with tempfile.TemporaryDirectory(prefix='pair-publisher-source-test-') as temporary:
            root=Path(temporary);stub=root/'git'
            stub.write_text("""#!/usr/bin/python3
import os,sys
args=sys.argv[1:]
if args[0]=='diff':
    if os.environ['FIXTURE_MODE']=='diff-failure':sys.exit(1)
    print('src/FS.GG.Execution.Processes/ProcessSessions.fs' if os.environ['FIXTURE_MODE']=='package-change' else 'eng/test-callable-cli-installed-harness.py' if os.environ['FIXTURE_MODE']=='proof-change' else 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json' if os.environ['FIXTURE_MODE']=='metadata-change' else 'eng/execution-pair-release-admission.json')
if args[0]=='rev-parse':print(os.environ['TREE_A'])
""")
            stub.chmod(0o700)
            env={**os.environ,'PATH':str(root)+os.pathsep+os.environ['PATH'],'FIXTURE_MODE':mode,'SOURCE_A':'a'*40,'MERGE_A':'a'*40,'TREE_A':'b'*40,'GITHUB_SHA':'9'*40}
            result=subprocess.run(['bash','-c',publisher_source],env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode in {'good','proof-change','metadata-change'}),(mode,result.stderr)
    with tempfile.TemporaryDirectory(prefix='pair-readback-test-') as temporary:
        root=Path(temporary)
        manifest={'schema':'fsgg.coordination.execution-pair-release-preparation/1','profile':'execution-pair',
                  'version':'0.2.0','tag':'execution/v0.2.0','sourceCommit':'a'*40,'sourceTree':'b'*40,
                  'publicationAuthorized':False,'tagAuthorized':False,
                  'packages':[{'packageId':id,'sha256':str(index+1)*64,'assemblySha256':str(index+3)*64} for index,id in enumerate(IDS)]}
        installed={'schema':'fsgg.coordination.execution-pair-installed-load/1','mode':'public','publicOnly':True,
                   'producerProjectReferenced':False,'packageInputsOnly':True,'physicalSourceExclusionEstablished':False,
                   'privateCachesUnavailable':True,'effectiveProjectIsolationGuard':True,'effectiveNuGetFoldersVerified':True,'actualCompilerReferencesVerified':True,'nativeFixtureInvoked':False,'consumerAdoptionAccepted':False,
                   'packages':[{'packageId':row['packageId'],'version':'0.2.0','runtimeSha256':row['assemblySha256']} for row in manifest['packages']]}
        for mode in ('good','missing-second-feed','wrong-candidate','github-raw-mismatch','payload-unknown','wrong-runtime','wrong-version','claimed-adoption'):
            current=copy.deepcopy(manifest);smoke=copy.deepcopy(installed)
            if mode=='wrong-version':current['version']='0.3.0'
            if mode=='wrong-runtime':smoke['packages'][0]['runtimeSha256']='8'*64
            if mode=='claimed-adoption':smoke['consumerAdoptionAccepted']=True
            write(root/'manifest.json',current);write(root/'installed.json',smoke)
            for index,row in enumerate(current['packages']):
                for name,feedname in [('github','github-packages'),('nuget','nuget-org')]:
                    path=root/(row['packageId']+'.'+name+'.json')
                    value={'feed':feedname,'candidateSha256':row['sha256'],'servedArchiveSha256':row['sha256'] if name=='github' else '9'*64,'payloadMatch':True}
                    if index==0 and mode=='wrong-candidate':value['candidateSha256']='8'*64
                    if index==0 and name=='github' and mode=='github-raw-mismatch':value['servedArchiveSha256']='8'*64
                    if index==0 and mode=='payload-unknown':value['payloadMatch']=None
                    write(path,value)
                    if mode=='missing-second-feed' and index==1 and name=='nuget':path.unlink()
            output=root/(mode+'.json')
            command=['python3',str(ROOT/'eng/write-callable-cli-release-readback.py'),'--execution-pair',
                     '--publisher','c'*40,'--source','a'*40,'--tree','b'*40,'--merge','a'*40,
                     '--manifest',str(root/'manifest.json'),'--manifest-sha256',hashlib.sha256((root/'manifest.json').read_bytes()).hexdigest(),
                     '--feed-root',str(root),'--installed',str(root/'installed.json'),'--output',str(output)]
            result=subprocess.run(command,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode=='good'),(mode,result.stderr)
            assert output.exists()==(mode=='good'),mode
    actual=heredoc('PY_PAIR_PROTECTION')
    desired=json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_text())
    keys=('target','enforcement','conditions','bypass_actors','rules')
    policy={key:desired[key] for key in keys}
    for mode in ('good','good-omitted-tag-parameters','update-enabled','update-numeric-zero','update-null','update-unknown-parameter','update-unknown-field','unprotected-component','inactive','bypass','deleted-signature-rule','foreign-ruleset','wrong-pin'):
        with tempfile.TemporaryDirectory(prefix='pair-protection-test-') as temporary:
            root=Path(temporary);readback=root/'readback';readback.mkdir()
            live=copy.deepcopy(policy); live['id']=4
            update=next(rule for rule in live['rules'] if rule['type']=='update')
            if mode=='good-omitted-tag-parameters':update.pop('parameters')
            if mode=='update-enabled':update['parameters']['update_allows_fetch_and_merge']=True
            if mode=='update-numeric-zero':update['parameters']['update_allows_fetch_and_merge']=0
            if mode=='update-null':update['parameters']=None
            if mode=='update-unknown-parameter':update['parameters']['ignored']=False
            if mode=='update-unknown-field':update['ignored']=False
            if mode=='unprotected-component':live['conditions']['ref_name']['include'].remove('refs/tags/execution/v*')
            if mode=='inactive':live['enforcement']='disabled'
            if mode=='bypass':live['bypass_actors']=[{'actor_id':1,'actor_type':'Integration','bypass_mode':'always'}]
            if mode=='deleted-signature-rule':live['rules']=[rule for rule in live['rules'] if rule['type']!='required_signatures']
            if mode=='foreign-ruleset':live['id']=5
            a=admission();a['tagProtection']['policySha256']=hashlib.sha256(json.dumps(policy,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            if mode=='wrong-pin':a['tagProtection']['policySha256']='0'*64
            write(root/'eng/execution-pair-release-admission.json',a)
            write(root/'eng/repository-settings/release-tag-ruleset.json',desired)
            write(readback/'tag-protection.json',live)
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,{'READBACK_OUTPUT':str(readback)},clear=True):
                    try:exec(actual,{})
                    except (AssertionError,KeyError,TypeError):refused=True
            finally:os.chdir(old)
            assert refused==(mode not in {'good','good-omitted-tag-parameters'}),mode
    # Exercise the actual four-cell shell loop with a finite inert curl replacement.
    gate=job.split('      - name: Observe four feed cells and component protection before first push\n',1)[1].split('          for id in FS.GG.Execution.Contracts FS.GG.Execution.Processes; do\n',1)[1]
    gate='for id in FS.GG.Execution.Contracts FS.GG.Execution.Processes; do\n'+gate.split('      - name: Obtain selected',1)[0]
    gate='\n'.join(line[10:] if line.startswith('          ') else line for line in gate.splitlines())
    gate='set -euo pipefail\n'+gate
    for mode in ('good','occupied','unknown'):
        with tempfile.TemporaryDirectory(prefix='pair-four-cell-test-') as temporary:
            root=Path(temporary);stub=root/'curl'
            stub.write_text("""#!/usr/bin/python3
import os,pathlib,sys
counter=pathlib.Path(os.environ['READBACK_OUTPUT'])/'calls'
value=int(counter.read_text())+1 if counter.exists() else 1
counter.write_text(str(value))
args=sys.argv[1:];target=pathlib.Path(args[args.index('--output')+1]);target.write_bytes(b'fixture-only')
mode=os.environ['FIXTURE_MODE']
print('200' if mode=='occupied' and value==3 else '403' if mode=='unknown' and value==3 else '404',end='')
""")
            stub.chmod(0o700)
            env={**os.environ,'PATH':str(root)+os.pathsep+os.environ['PATH'],'READBACK_OUTPUT':str(root),
                 'GH_TOKEN':'fixture-only','GITHUB_ACTOR':'fixture','FIXTURE_MODE':mode}
            result=subprocess.run(['bash','-c',gate],env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode=='good'),(mode,result.stderr)
            count=int((root/'calls').read_text());assert count==(4 if mode=='good' else 3)
            status=list(root.glob('*.status'));assert len(status)==(4 if mode=='good' else 2)
    print('EXECUTION_PAIR_RELEASE_INERT_CONTROLS passed admission=12 archive=7 readback=8 tag-protection=13 four-feed-cells=3 installed-bindings=5 installed-source=8 publisher-source=5 isolation-diagnostics=13 selectors=exclusive')

if __name__=='__main__':main()
