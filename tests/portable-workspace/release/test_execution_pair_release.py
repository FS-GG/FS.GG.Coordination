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
import signal
import time
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
            'operation':'publish-execution-pair-030-fixture','version':'0.3.0','tag':'execution/v0.3.0',
            'source':'a'*40,'tree':'b'*40,'protectedMerge':'a'*40,'runId':1,'jobId':2,'artifactId':3,
            'archiveSha256':'c'*64,'manifestSha256':'d'*64,'packages':{id:'e'*64 for id in IDS},
            'namespaceEntitlement':{'accepted':True,'packageIds':sorted(IDS),'nugetUser':'Paradigma11',
               'githubRepository':'FS-GG/FS.GG.Coordination','publisherWorkflow':'.github/workflows/callable-cli-release-publish.yml','evidenceSha256':'47a06f26b2bbd9924df2a9778cfdeb8bf29ee618c88908f652dbe2cbb5019fcb'},
            'tagProtection':{'rulesetId':4,'policySha256':'1'*64}}

def installed_isolation_diagnostic_controls(version, installed_mode="candidate"):
    # Inert SDK substitute drives the actual maintained caller; no CLR/native acceptance.
    check = runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))['check_execution_pair']
    for mode in ('good', 'bare-folder', 'escaped-path', 'extra-folder', 'sdk-folder', 'missing-folder',
                 'descendant-folder', 'parent-folder', 'double-slash', 'case-variant', 'duplicate-folders',
                 'trailing-slash', 'oversized', 'mixed-assets', 'mixed-compiler', 'wrong-runtime'):
        with tempfile.TemporaryDirectory(prefix='pair-isolation-diagnostic-test-') as temporary:
            candidate=Path(temporary); rows=[]
            for id in IDS:
                archive=candidate/(id+'.'+version+'.nupkg')
                with zipfile.ZipFile(archive,'w') as z: z.writestr('lib/net10.0/'+id+'.dll',id.encode())
                rows.append({'packageId':id,'file':archive.name,'sha256':hashlib.sha256(archive.read_bytes()).hexdigest()})
            write(candidate/'execution-pair-release-manifest.json',{'profile':'execution-pair','version':version,'tag':'execution/v'+version,'packages':rows})
            calls=[]; observed={}
            def fake_sdk(command, *, cwd, env):
                calls.append(command[1]); root=Path(cwd); packages=str(root/'packages'); folder=packages+os.sep
                assert env['NUGET_PACKAGES']==packages and env['TMPDIR']==str(root/'tmp')
                if command[1]=='restore':
                    import xml.etree.ElementTree as ET
                    references={row.attrib['Include']:row.attrib['Version'] for row in ET.parse(root/'PairInstalled.fsproj').getroot().findall('.//PackageReference')}
                    assert references=={'FSharp.Core':'[10.1.302]', **{id:'['+version+']' for id in IDS}}
                    smoke=(root/'Program.fs').read_text()
                    assert ('let textFactory: ITextProcessSessionFactory = ProcessSessions.createTextFactory()' in smoke)==(version=='0.3.0')
                    assert '.RunAsync' not in smoke and '.Prepare(' not in smoke
                    (root/'effective-isolation.txt').write_text('PAIR_EFFECTIVE_ISOLATION_OK\n')
                if command[1]=='build':
                    restore={'packagesPath':packages,'fallbackFolders':[],'sources':{**({str(candidate.resolve()):{}} if installed_mode=='candidate' else {}),'https://api.nuget.org/v3/index.json':{}}}
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
                    libraries={name:{'type':'package'} for name in ['FSharp.Core/10.1.302',*(id+'/'+version for id in IDS)]}
                    if mode=='mixed-assets': libraries[IDS[0]+'/0.1.0']=libraries.pop(IDS[0]+'/'+version)
                    write(root/'obj/project.assets.json',{'packageFolders':folders,'project':{'restore':restore},'libraries':libraries})
                    references=[]
                    for id in IDS:
                        compiler=root/'packages'/id.lower()/version/'lib/net10.0'/(id+'.dll'); compiler.parent.mkdir(parents=True);compiler.write_bytes(id.encode())
                        if mode=='mixed-compiler' and id==IDS[0]:
                            wrong=root/'packages'/id.lower()/'0.1.0/lib/net10.0'/(id+'.dll');wrong.parent.mkdir(parents=True);wrong.write_bytes(id.encode());references.append(str(wrong))
                        else: references.append(str(compiler))
                        target=root/'bin/Release/net10.0'/(id+'.dll');target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(b'wrong-runtime' if mode=='wrong-runtime' and id==IDS[0] else id.encode())
                    (root/'compiler-pair-references.txt').write_text('\n'.join(references)+'\n')
                return subprocess.CompletedProcess(command,0,'EXECUTION_PAIR_INSTALLED_LOAD_OK\n' if len(calls)==3 else '', '')
            original=check.__globals__['run']; check.__globals__['run']=fake_sdk; output=candidate/'proof.json'
            try:
                try: check(candidate,installed_mode,output); refused=False
                except RuntimeError as error:
                    refused=True; message=str(error); prefix='effective NuGet package/fallback folder escaped isolation: '
                    if mode in {'mixed-assets','mixed-compiler','wrong-runtime'}:
                        expected={'mixed-assets':'effective installed dependency population changed','mixed-compiler':'actual pair compiler references escaped isolated installed packages','wrong-runtime':'Installed pair runtime differs from original candidate payload'}
                        assert message==expected[mode],message
                    elif mode=='oversized': assert message==prefix+'observed path fields exceed the 4096-byte diagnostic bound'
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
    assert helper['PAIR_HISTORICAL_CANDIDATE'] == {
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
           'CLI_ONLY':'false','READBACK_ONLY':'false','SELECTED_NUGET_USER':'Paradigma11'}
    run = {'id':original['runId'],'head_sha':original['source'],'head_branch':'main',
           'event':'workflow_dispatch','run_attempt':1,'status':'completed','conclusion':'success',
           'path':'.github/workflows/callable-cli-release-prepare.yml','repository':{'id':1346720714},'head_repository':{'id':1346720714}}
    jobs = {'total_count':1,'jobs':[{'id':original['jobId'],'run_id':original['runId'],'run_attempt':1,
            'head_sha':original['source'],'name':'Prepare the routine Execution pair without publication',
            'status':'completed','conclusion':'success'}]}
    artifact = {'id':original['artifactId'],'expired':False,'size_in_bytes':169508,
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
                data=[{'name':'0.3.0'}] if mode=='occupied' else [{'name':'0.1.0'}]*100 if mode=='pagination' else []
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
            assert report['namespaceEntitlement']==helper['PAIR_ACCOUNT_OWNER_EVIDENCE'] and report['trustedPublishingLoginSucceeded'] is None
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
    # A real pre-existing caller watchdog must survive refusal without even one GET.
    assert signal.getitimer(signal.ITIMER_REAL)==(0.0,0.0)
    caller_handler=lambda signum,frame: None
    previous_handler=signal.signal(signal.SIGALRM,caller_handler)
    signal.setitimer(signal.ITIMER_REAL,10,3)
    try:
        before=signal.getitimer(signal.ITIMER_REAL);started=time.monotonic()
        reader=reader_class();calls=[]
        def forbidden_get(*args,**kwargs):
            calls.append(args)
            raise AssertionError('GET must not start with an active caller watchdog')
        reader.opener.open=forbidden_get
        with tempfile.TemporaryDirectory(prefix='pair-caller-watchdog-') as temporary:
            out=Path(temporary)/'observation.json'
            with patch.object(signal,'setitimer',wraps=signal.setitimer) as mutate_timer, patch.object(signal,'signal',wraps=signal.signal) as mutate_handler:
                assert observe(out,read=reader,environment=env)==1
                mutate_timer.assert_not_called();mutate_handler.assert_not_called()
            report=json.loads(out.read_bytes())
        after=signal.getitimer(signal.ITIMER_REAL)
        assert not calls and reader.count==0
        assert signal.getsignal(signal.SIGALRM) is caller_handler
        assert after[1]==before[1]==3 and 0<after[0]<=before[0]
        assert abs((before[0]-after[0])-(time.monotonic()-started))<0.1
        assert report['firstFailure']=={'name':'main-before','state':'budget-limited',
                                      'reason':'active caller watchdog prevents metadata acquisition'}
        assert all(row['state']=='budget-limited' for row in report['observations'][1:-1])
        assert report['collectionComplete'] is False and report['publicationEligible'] is False
    finally:
        signal.setitimer(signal.ITIMER_REAL,0)
        signal.signal(signal.SIGALRM,previous_handler)
    workflow=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    text=workflow.split('  execution_pair_preflight:\n',1)[1].split('  execution_pair:\n',1)[0]
    permissions=text.split('    permissions:\n',1)[1].split('    steps:\n',1)[0]
    assert permissions=='      contents: read\n      actions: read\n      packages: read\n'
    assert '    timeout-minutes: 5\n' in text and 'needs:' not in text
    for forbidden in ('NuGet/login','setup-dotnet','dotnet ','git tag','nuget push','actions/attest','id-token:','packages: write','contents: write'):
        assert forbidden not in text,forbidden
    assert 'permission-administration: write' in text and 'scoped policy observation remains disabled' in text
    for flags in __import__('itertools').product((False,True),repeat=5):
        execution,preflight,cli,readback,recovery=flags
        values={'execution_pair':execution,'preflight_only':preflight,'cli_only':cli,'readback_only':readback,'pair_recovery':recovery}
        active=[]
        for name in ('execution_pair_preflight','execution_pair','preflight','publish','cli_successor'):
            expression=next(line[8:] for line in workflow.split('\n  '+name+':\n',1)[1].splitlines() if line.startswith('    if: ')).replace('&&',' and ').replace('!= true','!= True').replace('== true','== True')
            expression=re.sub(r'!inputs\.([a-z_]+)',r'not inputs.\1',expression)
            for key,value in values.items():expression=expression.replace('inputs.'+key,str(value))
            if eval(expression,{'__builtins__':{}}):active.append(name)
        if execution and preflight:
            assert active==(['execution_pair_preflight'] if not cli and not readback and not recovery else []),flags
        if execution and not preflight:
            assert active==(['execution_pair'] if not cli and not readback and recovery else []),flags
        assert len(active)<=1,flags
    a=json.loads((ROOT/'eng/execution-pair-release-admission.json').read_bytes())
    assert a['enabled'] is False and a['operation']=='publish-execution-pair-030-b11fb121-four38484632631' and a['namespaceEntitlement']['evidenceSha256']==helper['PAIR_ACCOUNT_OWNER_EVIDENCE']['evidenceSha256']
    print('PAIR_OBSERVATION_INERT_CONTROLS passed cases='+str(len(modes))+' transport=11 selectors=16 read-only/no-eligibility')

def archive_controls(version):
    extract=runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))['extract_execution_pair_artifact']
    with tempfile.TemporaryDirectory(prefix='pair-archive-test-') as temporary:
        root=Path(temporary);readback=root/'readback';readback.mkdir()
        a=admission();a.update(version=version,tag='execution/v'+version);manifest={'schema':'fsgg.coordination.execution-pair-release-preparation/1','profile':'execution-pair',
            'version':version,'tag':'execution/v'+version,'sourceCommit':a['source'],'sourceTree':a['tree'],
            'publicationAuthorized':False,'tagAuthorized':False,'ownership':{'reservation':'unreserved'},
            'packages':[{'packageId':id,'file':id+'.'+version+'.nupkg','sha256':hashlib.sha256(id.encode()).hexdigest()} for id in IDS]}
        payloads={id+'.'+version+'.nupkg':id.encode() for id in IDS}
        payloads['execution-pair-release-manifest.json']=json.dumps(manifest,separators=(',',':')).encode()
        a['manifestSha256']=hashlib.sha256(payloads['execution-pair-release-manifest.json']).hexdigest()
        a['packages']={row['packageId']:row['sha256'] for row in manifest['packages']}
        run={'id':1,'repository':{'id':1346720714},'head_repository':{'id':1346720714},'head_branch':'main',
             'event':'workflow_dispatch','head_sha':a['source'],'run_attempt':1,'status':'completed','conclusion':'success',
             'path':'.github/workflows/callable-cli-release-prepare.yml'}
        jobs={'total_count':1,'jobs':[{'id':2,'run_id':1,'run_attempt':1,'head_sha':a['source'],
             'name':'Prepare the routine Execution pair without publication','status':'completed','conclusion':'success'}]}
        for mode in ('good','extra-member','duplicate-member','wrong-hash','wrong-run','wrong-job','expired','wrong-version','wrong-tag','mixed-member'):
            archive=readback/'candidate.zip'
            with zipfile.ZipFile(archive,'w') as z:
                for filename,data in payloads.items():
                    if mode=='mixed-member' and filename.startswith(IDS[0]):filename=IDS[0]+'.0.1.0.nupkg'
                    z.writestr(filename,data)
                if mode=='extra-member':z.writestr('unselected',b'bad')
                if mode=='duplicate-member':z.writestr(next(iter(payloads)),b'duplicate')
            a['archiveSha256']=hashlib.sha256(archive.read_bytes()).hexdigest()
            current=copy.deepcopy(a)
            if mode=='wrong-version':current['version']='0.4.0'
            if mode=='wrong-tag':current['tag']='execution/v0.1.0'
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
                    except (AssertionError,KeyError,TypeError,RuntimeError):refused=True
            finally:os.chdir(old)
            assert refused==(mode!='good'),mode

def installed_version_preflight_controls():
    helper=runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))
    check=helper['check_execution_pair']
    for version in ('0.2.0','0.3.0'):
        for mode in ('unsupported-version','wrong-tag','wrong-contracts-file','wrong-processes-file'):
            with tempfile.TemporaryDirectory(prefix='pair-version-preflight-test-') as temporary:
                root=Path(temporary);rows=[]
                for id in IDS:
                    archive=root/(id+'.'+version+'.nupkg')
                    with zipfile.ZipFile(archive,'w') as z:z.writestr('lib/net10.0/'+id+'.dll',id.encode())
                    rows.append({'packageId':id,'file':archive.name,'sha256':hashlib.sha256(archive.read_bytes()).hexdigest()})
                manifest={'profile':'execution-pair','version':version,'tag':'execution/v'+version,'packages':rows}
                if mode=='unsupported-version':manifest['version']='0.4.0'
                if mode=='wrong-tag':manifest['tag']='execution/v0.1.0'
                if mode=='wrong-contracts-file':rows[0]['file']=IDS[0]+'.0.1.0.nupkg'
                if mode=='wrong-processes-file':rows[1]['file']=IDS[1]+'.0.1.0.nupkg'
                write(root/'execution-pair-release-manifest.json',manifest)
                original=check.__globals__['run'];calls=[]
                def forbidden_sdk(*args,**kwargs):calls.append(args);raise AssertionError('SDK launched for refused version/tag/member')
                check.__globals__['run']=forbidden_sdk
                try:
                    try:check(root,'candidate',root/'proof.json');refused=False
                    except RuntimeError:refused=True
                finally:check.__globals__['run']=original
                assert refused and not calls and not (root/'proof.json').exists(),(version,mode)
    for version in ('0.4.0','[0.3.0]','0.3.0\"/>'):
        with tempfile.TemporaryDirectory(prefix='pair-version-project-test-') as temporary:
            root=Path(temporary)
            try:helper['write_pair_installed_project'](root,version);refused=False
            except RuntimeError:refused=True
            assert refused and not (root/'PairInstalled.fsproj').exists()

def consumer_fixture(root, a):
    """Synthetic byte/native records only; never an accepted hosted proof."""
    helper=runpy.run_path(str(ROOT/'eng/write-callable-cli-release-readback.py'))
    q={'executor':'7'*40,'runId':17,'workflowSha256':'8'*64,'cases':{}}
    a['consumerQualification']=q
    native_jobs=[]
    for index,(case,(repository,receiver,count)) in enumerate(helper['PAIR_RECEIVERS'].items()):
        job_id=30+index;artifact_id=40+index
        profile=json.dumps({'installation':'/inert/proc6-dotnet','hostPath':'/inert/proc6-dotnet/dotnet','fixtureOnly':True}).encode()
        closure={'physicalSourceExclusionEstablished':True,'networkReachable':True,'hardContainment':False,
                 'scope':'known fresh hosted workspace/temp and evaluated compiler inputs','nativeFixtureInvoked':False,'consumerAdoptionAccepted':False,
                 'sdkVersion':'10.0.401','runtimeFrameworkVersion':'10.0.12','dotnetProfileSha256':hashlib.sha256(profile).hexdigest(),
                 'compilerInputs':['inert'],'compilerEvaluations':['inert'],'runtimeCopies':{},'cachePackageIdentities':[],
                 'sdkBundledInput':None,'actualLocks':[],'actualAssetHashes':[],'runtimeConfigurations':[{'runtimeVersion':'10.0.12','rollForward':'Disable'}]}
        version='0.3.0' if case.endswith('s1') else '0.2.0' if case=='sdd-rollback' else '0.1.0'
        hashes=a['packages'] if case.endswith('s1') else {'FS.GG.Execution.Contracts':'62b1f85b8622d6719f086d73f4dbf29bd251dbb083a9b9588535d6b86531665d','FS.GG.Execution.Processes':'20d09f3bc71c78c46809931a8d1f7b4009d381fb0c4f203db2d7c846266e4f41'} if case=='sdd-rollback' else {'FsQuint':'e170d70a683321818944d86bb9c9e087323e01391738b3b4a621892ef91a2a8a','FsQuint.Tooling':'d2a58ae6431281e02fe19a26670225a5bd77c50450f473e4a8031c6fa47ebc79'}
        closure['packageVersions']={}
        for id,digest in hashes.items():
            closure['packageVersions'][id]={'version':version,'packageSha256':digest,'packageBytes':1,'dllSha256':'8'*64}
            closure['runtimeCopies'][id]=['fixture.dll']
            closure['cachePackageIdentities'].append({'packageId':id,'version':version,'archiveSha256':digest,'archiveBytes':1,'cacheSha512ConventionAsserted':False,'metadataContentHash':'fixture-normalized'})
            closure['actualLocks'].append({'schemaVersion':2 if case.startswith('sdd-') else 1,'entries':[{'packageId':id,'entry':{'resolved':version,'contentHash':'fixture-normalized'}}]})
            closure['actualAssetHashes'].append({'packageId':id,'contentHash':'fixture-normalized'})
        source_pins={'receiverSource':receiver,'locks':[]}
        if case=='fsquint-s1':
            core=copy.deepcopy(helper['PAIR_SDK_CORE']);lockmap=helper['PAIR_SDK_SOURCE_LOCKS']
            entry={'framework':'net10.0','packageId':'FSharp.Core','entry':{'type':'Direct','requested':'[10.1.401, )','resolved':core['version'],'contentHash':core['contentHash']}}
            source_pins['locks']=[{'path':path,'sha256':digest,'entries':[copy.deepcopy(entry)]} for path,digest in lockmap.items()]
            binding={'sdkVersion':'10.0.401','sdkSourcePath':'/inert/proc6-dotnet/sdk/10.0.401/FSharp/library-packs/FSharp.Core.10.1.401.nupkg','coldFeedPath':'/inert/proc6-feed/FSharp.Core.10.1.401.nupkg','expected':core,'assemblyPayloads':{'lib/netstandard2.0/FSharp.Core.dll':'26d0e3e00c769b9b6e9d8c1925885f7c16a51c605f795093386a25b909012991',core['dllRelativePath']:core['dllSha256']},'sourceLockPinsSha256':hashlib.sha256(json.dumps(source_pins).encode()).hexdigest(),'implicitFallbackEnabled':False,'sameVersionPublicSubstitutionAccepted':False}
            closure['sdkBundledInput']=binding
            closure['cachePackageIdentities'].append({'packageId':'FSharp.Core','version':core['version'],'archiveBytes':core['packageBytes'],'archiveSha256':core['packageSha256'],'metadataContentHash':core['contentHash'],'cacheSha512ConventionAsserted':False})
            closure['compilerInputs'].append(['R','/inert/proc6-state/packages/fsharp.core/10.1.401/'+core['dllRelativePath']])
            closure['runtimeCopies']['FSharp.Core']=['tests/FsQuint.Tests/bin/Release/net10.0/FSharp.Core.dll']
            for path in ('src/FsQuint/packages.lock.json','src/FsQuint.Tooling/packages.lock.json','tests/FsQuint.Tests/packages.lock.json'):
                closure['actualLocks'].append({'path':path,'sha256':lockmap[path],'schemaVersion':1,'generatedForThisReceiver':False,'entries':[copy.deepcopy(entry)]})
                closure['actualAssetHashes'].append({'path':str(Path(path).parent/'obj/project.assets.json'),'packageId':'FSharp.Core','contentHash':core['contentHash']})
        closure_raw=json.dumps(closure).encode();members={'proc6-state/proof/source-closure.json':closure_raw,'proc6-state/proof/dotnet-profile.json':profile}
        members['proc6-state/source-inputs.json']=json.dumps({'receiverRepository':repository,'receiverCommit':receiver,'producerSourceTransferred':False,'networkReachable':True,'hardContainment':False,'expectedPackages':closure['packageVersions']}).encode()
        members['proc6-state/budget.json']=json.dumps({'workSeconds':600,'cleanupSeconds':30,'reportSeconds':30}).encode()
        members['proc6-state/proof/source-lock-pins.json']=json.dumps(source_pins).encode()
        if case=='fsquint-s1':members['proc6-state/proof/sdk-core-input.json']=json.dumps(binding).encode()
        members['proc6-transfer-native.json']=json.dumps({'id':80,'expired':False,'digest':'sha256:'+'3'*64,'name':'execution-pair-installed-'+a['source']+'-17','workflow_run':{'id':17,'head_sha':q['executor'],'repository_id':1346720714,'head_repository_id':1346720714}}).encode()
        fixtures=[]
        if case.startswith('sdd-'):
            for group,n in enumerate((18 if case=='sdd-s1' else 13,8,17,27,1)):
                names=[f'fixture-{group}-{i}' for i in range(n)];fixtures+=names
                xml='<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'+''.join(f'<UnitTestResult testId="{name}" testName="{name}" outcome="Passed" />' for name in names)+'</Results><ResultSummary outcome="Completed"><Counters '+f'total="{n}" executed="{n}" passed="{n}" failed="0" error="0" timeout="0" aborted="0" notExecuted="0" /></ResultSummary></TestRun>'
                members[f'proc6-state/proof/results/{group}.trx']=xml.encode()
        else:
            fixtures=[f'fixture-{i}' for i in range(count)]
            members['proc6-state/logs/fixture-0.log']=(''.join('PASS '+n+'\n' for n in fixtures)+str(count)+' checks passed\n').encode()
        signature={'currentVerificationObserved':True,'historicalVerificationSubstituted':False,'packages':[{'packageId':id,'packageSha256':digest,'exitCode':0,'verifyAll':True,'logSha256':hashlib.sha256(b'inert-signature-log').hexdigest()} for id,digest in hashes.items()]}
        for id in hashes:members['proc6-state/logs/'+id+'-signature.log']=b'inert-signature-log'
        if case=='fsquint-rollback':members['proc6-state/proof/public-signature-verification.json']=json.dumps(signature).encode()
        observation={'schema':'fsgg.coordination.execution-pair-consumer-observation/1','case':case,'source':a['source'],'executor':q['executor'],
                     'runId':str(q['runId']),'attempt':1,'receiver':receiver,'receiverRepository':repository,'candidateArchiveSha256':a['archiveSha256'],
                     'sourceClosureSha256':hashlib.sha256(closure_raw).hexdigest(),'fixtureNames':fixtures,'fixtureCount':count,
                     'nativeFixtureInvoked':True,'selectedFixturesPassed':True,'fixtureCleanupObserved':True,'networkReachable':True,
                     'consumerAdoptionAccepted':False,'publicationAuthorized':False,'hardContainment':False,'outerCustodyQualified':False,
                     'pipelineDeadlineQualified':False,'currentCrossRepoArtifactAccessEstablished':False,'elapsedSeconds':1,'originalWorkSeconds':600,
                     'ordinaryCommandTimeoutSeconds':90,'sdkVersion':'10.0.401','runtimeFrameworkVersion':'10.0.12','dotnetProfileSha256':hashlib.sha256(profile).hexdigest(),
                     'selectedPackageArchiveSha256':a['archiveSha256'] if case.endswith('s1') else '6aedfb7e4eda796fdea3fc0326003a6f90f859352d7394d311f4c5d867687819' if case=='sdd-rollback' else None,
                     'originalArtifactReacquired':False,'historicalOriginalArchiveSha256':'7078f08faed9d8f1e6a7a662fd33c04861a106990db61b184d3e573c413f8c1f',
                     'historicalReceiptSha256':'fc0c5c4f774b7210dbfda88fcbae97759994f36424b9c889c693dcf8d7ac4f3c','freshPublicSignatureVerification':signature}
        observation_raw=json.dumps(observation).encode();members['proc6-state/proof/consumer-observation.json']=observation_raw
        root.mkdir(parents=True,exist_ok=True)
        with zipfile.ZipFile(root/(case+'.zip'),'w') as z:
            for name,raw in members.items():z.writestr(name,raw)
        archive=(root/(case+'.zip')).read_bytes();digest=hashlib.sha256(archive).hexdigest()
        q['cases'][case]={'jobId':job_id,'artifactId':artifact_id,'archiveSha256':digest,'observationSha256':hashlib.sha256(observation_raw).hexdigest(),'sourceClosureSha256':hashlib.sha256(closure_raw).hexdigest()}
        write(root/(case+'.artifact.json'),{'id':artifact_id,'name':f'execution-pair-consumer-{case}-17-1','expired':False,'size_in_bytes':len(archive),'digest':'sha256:'+digest,
              'workflow_run':{'id':17,'head_sha':q['executor'],'repository_id':1346720714,'head_repository_id':1346720714}})
        native_jobs.append({'id':job_id,'name':'Installed receiver '+case,'run_id':17,'head_sha':q['executor'],'run_attempt':1,'status':'completed','conclusion':'success',
                            'runner_id':50+index,'runner_name':'fixture-'+str(index),'runner_group_name':'GitHub Actions','labels':['ubuntu-24.04'],'steps':[{'name':'Set up job','conclusion':'success'}]})
        (root/(case+'.job.log')).write_text('##[group]Runner Image\nImage: ubuntu-24.04\nVersion: 20261013.1.0\n##[endgroup]\n')
    write(root/'jobs.json',{'total_count':4,'jobs':native_jobs})
    write(root/'run.json',{'id':17,'workflow_id':361024581,'repository':{'id':1346720714},'head_repository':{'id':1346720714},'head_sha':q['executor'],
          'head_branch':'main','path':'.github/workflows/callable-cli-release-prepare.yml','event':'workflow_dispatch','run_attempt':1,'status':'completed','conclusion':'success'})
    return helper


def consumer_readback_controls():
    modes=('good','unresolved','missing-case','duplicate-job','wrong-executor','second-attempt','foreign-repo','failed-job','same-runner',
           'missing-image','expired','wrong-native-digest','wrong-archive','wrong-receipt-pin','missing-closure','wrong-receiver','false-source-exclusion',
           'wrong-runtime','missing-cache','wrong-fixture','failed-trx','signed-rollback-not-verified','rollback-archive-fiction','unsupported-containment','wrong-cached-package','wrong-normalized-hash','bad-signature-log','wrong-source-input','renewed-budget','foreign-transfer','schema-bool','schema-zero','schema-three','schema-text','missing-sdk-binding')
    for mode in modes:
        with tempfile.TemporaryDirectory(prefix='pair-four-receivers-inert-') as tmp:
            root=Path(tmp);a=admission();helper=consumer_fixture(root,a);q=a['consumerQualification']
            def edit_json(name,fn):
                value=json.loads((root/name).read_bytes());fn(value);write(root/name,value)
            if mode=='unresolved':q['executor']=None
            if mode=='missing-case':q['cases'].pop('fsquint-rollback')
            if mode=='duplicate-job':q['cases']['fsquint-s1']['jobId']=q['cases']['sdd-s1']['jobId']
            if mode=='wrong-executor':edit_json('run.json',lambda r:r.update(head_sha='9'*40))
            if mode=='second-attempt':edit_json('run.json',lambda r:r.update(run_attempt=2))
            if mode=='foreign-repo':edit_json('run.json',lambda r:r['repository'].update(id=1))
            if mode=='failed-job':edit_json('jobs.json',lambda r:r['jobs'][0].update(conclusion='failure'))
            if mode=='same-runner':edit_json('jobs.json',lambda r:r['jobs'][1].update(runner_id=50))
            if mode=='missing-image':(root/'sdd-s1.job.log').write_text('fixture unknown')
            if mode=='expired':edit_json('sdd-s1.artifact.json',lambda r:r.update(expired=True))
            if mode=='wrong-native-digest':edit_json('sdd-s1.artifact.json',lambda r:r.update(digest='sha256:'+'9'*64))
            if mode=='wrong-archive':(root/'sdd-s1.zip').write_bytes(b'wrong')
            if mode=='wrong-receipt-pin':q['cases']['sdd-s1']['observationSha256']='9'*64
            mutated=mode in ('missing-closure','wrong-receiver','false-source-exclusion','wrong-runtime','missing-cache','wrong-fixture','failed-trx','signed-rollback-not-verified','rollback-archive-fiction','unsupported-containment','wrong-cached-package','wrong-normalized-hash','bad-signature-log','wrong-source-input','renewed-budget','foreign-transfer','schema-bool','schema-zero','schema-three','schema-text','missing-sdk-binding')
            if mutated:
                case='fsquint-s1' if mode=='missing-sdk-binding' else 'fsquint-rollback' if mode in ('signed-rollback-not-verified','rollback-archive-fiction','bad-signature-log') else 'sdd-s1'
                path=root/(case+'.zip')
                with zipfile.ZipFile(path) as z:members={n:z.read(n) for n in z.namelist()}
                op='proc6-state/proof/consumer-observation.json';cp='proc6-state/proof/source-closure.json'
                o=json.loads(members[op]);c=json.loads(members[cp])
                if mode=='wrong-receiver':o['receiver']='9'*40
                if mode.startswith('schema-'):c['actualLocks'][0]['schemaVersion']={'schema-bool':True,'schema-zero':0,'schema-three':3,'schema-text':'2'}[mode]
                if mode=='missing-sdk-binding':members.pop('proc6-state/proof/sdk-core-input.json')
                if mode=='false-source-exclusion':c['physicalSourceExclusionEstablished']=False
                if mode=='wrong-runtime':c['runtimeFrameworkVersion']='10.0.11'
                if mode=='missing-cache':c['cachePackageIdentities']=[]
                if mode=='wrong-fixture':o['fixtureNames'][0]='wrong'
                if mode=='wrong-source-input':
                    key='proc6-state/source-inputs.json';v=json.loads(members[key]);v['receiverCommit']='9'*40;members[key]=json.dumps(v).encode()
                if mode=='renewed-budget':
                    key='proc6-state/budget.json';v=json.loads(members[key]);v['workSeconds']=900;members[key]=json.dumps(v).encode()
                if mode=='foreign-transfer':
                    key='proc6-transfer-native.json';v=json.loads(members[key]);v['workflow_run']['id']=99;members[key]=json.dumps(v).encode()
                if mode=='unsupported-containment':o['hardContainment']=True
                if mode=='wrong-cached-package':c['cachePackageIdentities'][0]['archiveSha256']='9'*64
                if mode=='wrong-normalized-hash':c['actualAssetHashes'][0]['contentHash']='different'
                if mode=='bad-signature-log':members['proc6-state/logs/FsQuint-signature.log']=b'changed'
                if mode=='rollback-archive-fiction':o['selectedPackageArchiveSha256']='9'*64
                if mode=='signed-rollback-not-verified':
                    o['freshPublicSignatureVerification']['currentVerificationObserved']=False
                    members['proc6-state/proof/public-signature-verification.json']=json.dumps(o['freshPublicSignatureVerification']).encode()
                if mode=='failed-trx':members['proc6-state/proof/results/0.trx']=members['proc6-state/proof/results/0.trx'].replace(b'outcome="Passed"',b'outcome="Failed"',1)
                members[cp]=json.dumps(c).encode();q['cases'][case]['sourceClosureSha256']=hashlib.sha256(members[cp]).hexdigest();o['sourceClosureSha256']=q['cases'][case]['sourceClosureSha256']
                members[op]=json.dumps(o).encode();q['cases'][case]['observationSha256']=hashlib.sha256(members[op]).hexdigest()
                if mode=='missing-closure':members.pop(cp)
                with zipfile.ZipFile(path,'w') as z:
                    for name,raw in members.items():z.writestr(name,raw)
                raw=path.read_bytes();q['cases'][case]['archiveSha256']=hashlib.sha256(raw).hexdigest()
                edit_json(case+'.artifact.json',lambda r:r.update(size_in_bytes=len(raw),digest='sha256:'+q['cases'][case]['archiveSha256']))
            with contextlib.redirect_stderr(io.StringIO()):
                try:helper['pair_consumers'](a,root);refused=False
                except (SystemExit,ValueError,KeyError,TypeError):refused=True
            assert refused==(mode!='good'),mode
    print('PAIR_FOUR_RECEIVER_INERT_CONTROLS passed cases='+str(len(modes))+' no-native-evidence')


def consumer_sdk_readback_controls():
    modes=('good','nonselected-profile','missing-binding','public-core-substitution','wrong-core-size','wrong-dll-payload','wrong-sdk-source',
           'wrong-feed','implicit-fallback','public-substitution-flag','wrong-lock-pin-digest','missing-source-lock','foreign-source-lock-hash',
           'implicit-request-changed','wrong-cache-raw','wrong-cache-normalized','cache-convention-claim','missing-compiled-lock',
           'compiled-lock-changed','compiled-request-changed','generated-lock','wrong-asset-normalized','missing-asset','wrong-compiler-reference',
           'missing-runtime-copy','escaping-runtime-copy')
    with tempfile.TemporaryDirectory(prefix='pair-sdk-readback-inert-') as tmp:
        root=Path(tmp);a=admission();helper=consumer_fixture(root,a)
        with zipfile.ZipFile(root/'fsquint-s1.zip') as z:source={name:z.read(name) for name in z.namelist()}
        for mode in modes:
            c=json.loads(source['proc6-state/proof/source-closure.json']);b=json.loads(source['proc6-state/proof/sdk-core-input.json']);pins=json.loads(source['proc6-state/proof/source-lock-pins.json']);profile=json.loads(source['proc6-state/proof/dotnet-profile.json']);case='fsquint-s1'
            if mode=='nonselected-profile':case='sdd-s1'
            if mode=='public-core-substitution':b['expected']['packageSha256']='f'*64
            if mode=='wrong-core-size':b['expected']['packageBytes']+=1
            if mode=='wrong-dll-payload':b['assemblyPayloads']['lib/netstandard2.0/FSharp.Core.dll']='f'*64
            if mode=='wrong-sdk-source':b['sdkSourcePath']='/public/FSharp.Core.10.1.401.nupkg'
            if mode=='wrong-feed':b['coldFeedPath']='/outside/FSharp.Core.10.1.401.nupkg'
            if mode=='implicit-fallback':b['implicitFallbackEnabled']=True
            if mode=='public-substitution-flag':b['sameVersionPublicSubstitutionAccepted']=True
            if mode=='missing-source-lock':pins['locks'].pop()
            if mode=='foreign-source-lock-hash':pins['locks'][0]['sha256']='f'*64
            if mode=='implicit-request-changed':pins['locks'][0]['entries'][0]['entry']['requested']='[10.1.401]'
            cache=next(v for v in c['cachePackageIdentities'] if v['packageId']=='FSharp.Core')
            locks=[v for v in c['actualLocks'] if any(e['packageId']=='FSharp.Core' for e in v['entries'])]
            assets=[v for v in c['actualAssetHashes'] if v['packageId']=='FSharp.Core']
            if mode=='wrong-cache-raw':cache['archiveSha256']='f'*64
            if mode=='wrong-cache-normalized':cache['metadataContentHash']='public-package-hash'
            if mode=='cache-convention-claim':cache['cacheSha512ConventionAsserted']=True
            if mode=='missing-compiled-lock':c['actualLocks'].remove(locks[0])
            if mode=='compiled-lock-changed':locks[0]['sha256']='f'*64
            if mode=='compiled-request-changed':locks[0]['entries'][0]['entry']['requested']='[10.1.401]'
            if mode=='generated-lock':locks[0]['generatedForThisReceiver']=True
            if mode=='wrong-asset-normalized':assets[0]['contentHash']='public-package-hash'
            if mode=='missing-asset':c['actualAssetHashes'].remove(assets[0])
            if mode=='wrong-compiler-reference':c['compilerInputs'][-1][1]='/outside/FSharp.Core.dll'
            if mode=='missing-runtime-copy':c['runtimeCopies']['FSharp.Core']=[]
            if mode=='escaping-runtime-copy':c['runtimeCopies']['FSharp.Core']=['../bin/FSharp.Core.dll']
            # Keep receipt and nested binding hashes coherent so semantic guards decide.
            pins_raw=json.dumps(pins).encode();b['sourceLockPinsSha256']=hashlib.sha256(pins_raw).hexdigest()
            if mode=='wrong-lock-pin-digest':b['sourceLockPinsSha256']='f'*64
            c['sdkBundledInput']=copy.deepcopy(b);binding_raw=None if mode=='missing-binding' else json.dumps(b).encode()
            with contextlib.redirect_stderr(io.StringIO()):
                try:helper['pair_sdk_bundled_input'](case,c,profile,pins_raw,binding_raw);refused=False
                except (SystemExit,ValueError,KeyError,TypeError):refused=True
            assert refused==(mode!='good'),mode
    print('PAIR_R8_SDK_READBACK_INERT_CONTROLS passed cases='+str(len(modes))+' sdk-input-separate-from-pair')


def complete_policy_source_controls():
    """Actual publisher path, original immutable admission and policy, inert transports only."""
    workflow=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    job=workflow.split('\n  execution_pair:\n',1)[1]
    binding=job.split('      - name: Require existing App bindings',1)[1].split('      - name:',1)[0]
    mint=job.split('      - name: Mint repository-scoped token',1)[1].split('      - name:',1)[0]
    observe=job.split('      - name: Observe four feed cells and component protection before first push\n',1)[1].split('      - name:',1)[0]
    assert 'RULESET_APP_ID: ${{ secrets.FSGG_DISPATCH_APP_ID }}' in binding
    assert 'RULESET_APP_PRIVATE_KEY: ${{ secrets.FSGG_DISPATCH_APP_PRIVATE_KEY }}' in binding
    for required in ('id: pair-ruleset-reader','actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1',
                     'app-id: ${{ secrets.FSGG_DISPATCH_APP_ID }}','private-key: ${{ secrets.FSGG_DISPATCH_APP_PRIVATE_KEY }}',
                     'owner: FS-GG','repositories: FS.GG.Coordination','permission-administration: write'):
        assert required in mint,required
    assert mint.count('permission-')==1 and 'skip-token-revoke:' not in mint
    assert job.count('${{ steps.pair-ruleset-reader.outputs.token }}')==1
    assert job.count('GH_TOKEN="$PAIR_RULESET_TOKEN"')==1
    assert 'GH_TOKEN: ${{ github.token }}' in observe
    assert job.index('Verify pair provenance')<job.index('Require existing App bindings')<job.index('Mint repository-scoped token')<job.index('Observe four feed cells')<job.index('Obtain selected Trusted Publishing')<job.index('Push both retained')
    def shell(body):
        return '\n'.join(line[10:] for line in body.split('        run: |\n',1)[1].splitlines())
    for app_id,key,accepted in (('fixture-id','fixture-key',True),('','fixture-key',False),('fixture-id','',False),('','',False)):
        result=subprocess.run(['bash','-c',shell(binding)],env={'RULESET_APP_ID':app_id,'RULESET_APP_PRIVATE_KEY':key},capture_output=True,timeout=5)
        assert (result.returncode==0)==accepted
        if not accepted:assert b'App bindings are missing' in result.stdout
    original=json.loads((ROOT/'eng/execution-pair-release-admission.json').read_text())
    expected_hash='5182bb35a6dd682cef84ac9ce4210cfd09f38b7410d8c9cf9bae42866bcb81da'
    assert original['operation']=='publish-execution-pair-030-b11fb121-four38484632631'
    assert original['tagProtection']=={'rulesetId':21633398,'policySha256':expected_hash}
    desired=json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_text())
    keys=('target','enforcement','conditions','bypass_actors','rules')
    policy={key:desired[key] for key in keys}
    assert hashlib.sha256(json.dumps(policy,sort_keys=True,separators=(',',':')).encode()).hexdigest()==expected_hash
    # Record controls use authentic retained source policy, never claim a fresh native GET.
    actual=heredoc('PY_PAIR_PROTECTION')
    for mode in ('full','missing','nonempty','null'):
        with tempfile.TemporaryDirectory(prefix='pair-original-policy-test-') as tmp:
            root=Path(tmp);readback=root/'readback';readback.mkdir()
            live=copy.deepcopy(policy);live['id']=21633398;live['current_user_can_bypass']='never'
            if mode=='missing':live.pop('bypass_actors')
            if mode=='nonempty':live['bypass_actors']=[{'actor_id':1,'actor_type':'Integration','bypass_mode':'always'}]
            if mode=='null':live['bypass_actors']=None
            write(root/'eng/execution-pair-release-admission.json',original)
            write(root/'eng/repository-settings/release-tag-ruleset.json',desired)
            (root/'eng/test-callable-cli-installed-harness.py').write_bytes((ROOT/'eng/test-callable-cli-installed-harness.py').read_bytes())
            write(readback/'tag-protection.json',live)
            with contextlib.chdir(root),patch.dict(os.environ,{'READBACK_OUTPUT':str(readback)},clear=True):
                try:exec(actual,{});refusal=None
                except SystemExit as error:refusal=str(error)
            assert (refusal is None)==(mode=='full'),(mode,refusal)
            if mode=='missing':assert refusal=='incomplete ruleset evidence: required policy fields are missing'
            if mode=='null':assert refusal=='incomplete ruleset evidence: bypass_actors must be an explicit complete list'
            if mode=='nonempty':assert refusal=='ruleset policy refused: bypass actors are present'
    # Run just the actual read shell, up to its parser, using finite credential-aware stubs.
    prefix=shell(observe).split("python3 - <<'PY_PAIR_PROTECTION'",1)[0]
    for mode in ('good','missing-token','denied'):
        with tempfile.TemporaryDirectory(prefix='pair-ruleset-get-test-') as tmp:
            root=Path(tmp)
            write(root/'eng/execution-pair-release-admission.json',original)
            stub=root/'gh';stub.write_text("""#!/usr/bin/python3
import json,os,sys,pathlib
assert sys.argv[1:]==['api','repos/FS-GG/FS.GG.Coordination/rulesets/21633398']
assert os.environ['GH_TOKEN']=='fixture-ruleset-token'
pathlib.Path('ruleset-called').write_text('called')
if os.environ['FIXTURE_MODE']=='denied':sys.exit(1)
print(json.dumps({'fixture':'only'}))
""");stub.chmod(0o700)
            stub=root/'git';stub.write_text('#!/bin/bash\nexit 0\n');stub.chmod(0o700)
            stub=root/'curl';stub.write_text("""#!/usr/bin/python3
import os
assert os.environ['GH_TOKEN']=='fixture-default-token'
print('404',end='')
""");stub.chmod(0o700)
            env={'PATH':str(root)+':/usr/bin:/bin','READBACK_OUTPUT':str(root),'GITHUB_REPOSITORY':'FS-GG/FS.GG.Coordination',
                 'GH_TOKEN':'fixture-default-token','PAIR_RULESET_TOKEN':'' if mode=='missing-token' else 'fixture-ruleset-token','FIXTURE_MODE':mode}
            result=subprocess.run(['bash','-c',prefix],cwd=root,env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode=='good'),(mode,result.stderr)
            assert (root/'ruleset-called').exists()==(mode!='missing-token')
            if mode=='missing-token':assert b'App token is missing' in result.stdout
    print('EXECUTION_PAIR_COMPLETE_POLICY_CONTROLS passed original-policy=4 app-binding=4 inert-token-routing=3 native-effects=0')


def recovery_inert_fixture():
    """Native-shaped control data; signatures are never authenticated by fixture construction."""
    a=json.loads((ROOT/'eng/execution-pair-release-admission.json').read_text())
    run={'id':38495729817,'workflow_id':361038177,'repository':{'id':1346720714},'head_repository':{'id':1346720714},
         'path':'.github/workflows/callable-cli-release-publish.yml','event':'workflow_dispatch','head_branch':'main',
         'head_sha':'0ccfb6d067d912bbd8a1e26c4b56449358443b2e','run_attempt':1,'status':'completed','conclusion':'failure'}
    helpers=runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))
    job={'id':115630940784,'run_id':38495729817,'head_sha':run['head_sha'],'run_attempt':1,'status':'completed',
         'conclusion':'failure','name':'Publish the admitted original routine Execution pair',
         'steps':[{'name':name,'conclusion':state,'status':'completed','number':i+1} for i,(name,state) in enumerate(helpers['PAIR_ORIGINAL_STEPS'])]}
    jobs={'total_count':1,'jobs':[job]}
    verified=[{'verificationResult':json.loads(VERIFIED_PAIR_CONTROL)}]
    return a,run,jobs,verified


VERIFIED_PAIR_CONTROL = r'''{"mediaType":"application/vnd.dev.sigstore.verificationresult+json;version=0.1","signature":{"certificate":{"buildConfigDigest":"0ccfb6d067d912bbd8a1e26c4b56449358443b2e","buildConfigURI":"https://github.com/FS-GG/FS.GG.Coordination/.github/workflows/callable-cli-release-publish.yml@refs/heads/main","buildSignerDigest":"0ccfb6d067d912bbd8a1e26c4b56449358443b2e","buildSignerURI":"https://github.com/FS-GG/FS.GG.Coordination/.github/workflows/callable-cli-release-publish.yml@refs/heads/main","buildTrigger":"workflow_dispatch","certificateIssuer":"CN=sigstore-intermediate,O=sigstore.dev","githubWorkflowName":"callable-cli-release-publish","githubWorkflowRef":"refs/heads/main","githubWorkflowRepository":"FS-GG/FS.GG.Coordination","githubWorkflowSHA":"0ccfb6d067d912bbd8a1e26c4b56449358443b2e","githubWorkflowTrigger":"workflow_dispatch","issuer":"https://token.actions.githubusercontent.com","runInvocationURI":"https://github.com/FS-GG/FS.GG.Coordination/actions/runs/38495729817/attempts/1","runnerEnvironment":"github-hosted","sourceRepositoryDigest":"0ccfb6d067d912bbd8a1e26c4b56449358443b2e","sourceRepositoryIdentifier":"1346720714","sourceRepositoryOwnerIdentifier":"293607011","sourceRepositoryOwnerURI":"https://github.com/FS-GG","sourceRepositoryRef":"refs/heads/main","sourceRepositoryURI":"https://github.com/FS-GG/FS.GG.Coordination","sourceRepositoryVisibilityAtSigning":"public","subjectAlternativeName":"https://github.com/FS-GG/FS.GG.Coordination/.github/workflows/callable-cli-release-publish.yml@refs/heads/main"}},"statement":{"_type":"https://in-toto.io/Statement/v1","predicate":{"buildDefinition":{"buildType":"https://actions.github.io/buildtypes/workflow/v1","externalParameters":{"workflow":{"path":".github/workflows/callable-cli-release-publish.yml","ref":"refs/heads/main","repository":"https://github.com/FS-GG/FS.GG.Coordination"}},"internalParameters":{"github":{"event_name":"workflow_dispatch","repository_id":"1346720714","repository_owner_id":"293607011","runner_environment":"github-hosted"}},"resolvedDependencies":[{"digest":{"gitCommit":"0ccfb6d067d912bbd8a1e26c4b56449358443b2e"},"uri":"git+https://github.com/FS-GG/FS.GG.Coordination@refs/heads/main"}]},"runDetails":{"builder":{"id":"https://github.com/FS-GG/FS.GG.Coordination/.github/workflows/callable-cli-release-publish.yml@refs/heads/main"},"metadata":{"invocationId":"https://github.com/FS-GG/FS.GG.Coordination/actions/runs/38495729817/attempts/1"}}},"predicateType":"https://slsa.dev/provenance/v1","subject":[{"digest":{"sha256":"bfd248eb935b4868bb1a7fb431bdb1ec26d3c1fedd2a294c34cf7b546bed151a"},"name":"FS.GG.Execution.Contracts.0.3.0.nupkg"},{"digest":{"sha256":"978ac179d1983b6a3346655cd904391fd8dc2ff7f3b0558ed9fd40fa9aa56cbd"},"name":"FS.GG.Execution.Processes.0.3.0.nupkg"}]},"verifiedIdentity":{"issuer":{"issuer":"","regexp":".*"},"runnerEnvironment":"github-hosted","subjectAlternativeName":{"regexp":"^https://github\\.com/FS-GG/FS\\.GG\\.Coordination/\\.github/workflows/callable-cli-release-publish\\.yml(@(refs/.*|[0-9a-fA-F]+))?$","subjectAlternativeName":""}},"verifiedTimestamps":[{"timestamp":"2026-10-14T03:26:51+02:00","type":"Tlog","uri":"https://rekor.sigstore.dev"}]}'''


def pair_recovery_source_controls():
    helpers=runpy.run_path(str(ROOT/'eng/test-callable-cli-installed-harness.py'))
    a,run,jobs,verified=recovery_inert_fixture()
    assert a['enabled'] is False and a['recovery']['enabled'] is False and a['recovery']['policyObservationEnabled'] is False
    helpers['pair_original_native'](a,run,jobs)
    helpers['pair_verified_attestation'](a,verified)
    for mode in ('repository','publisher','attempt','workflow','partial-jobs','duplicate-job','duplicate-step','extra-step','effect-success','attest-skipped','failure-order'):
        r=copy.deepcopy(run);j=copy.deepcopy(jobs)
        if mode=='repository':r['repository']['id']=1
        if mode=='publisher':r['head_sha']='a'*40
        if mode=='attempt':r['run_attempt']=2
        if mode=='workflow':r['workflow_id']=1
        if mode=='partial-jobs':j['total_count']=2
        if mode=='duplicate-job':j['jobs'].append(copy.deepcopy(j['jobs'][0]));j['total_count']=2
        if mode=='duplicate-step':j['jobs'][0]['steps'].append(copy.deepcopy(j['jobs'][0]['steps'][0]))
        if mode=='extra-step':j['jobs'][0]['steps'].append({'name':'foreign','status':'completed','conclusion':'success','number':99})
        if mode=='effect-success':j['jobs'][0]['steps'][12]['conclusion']='success'
        if mode=='attest-skipped':j['jobs'][0]['steps'][7]['conclusion']='skipped'
        if mode=='failure-order':j['jobs'][0]['steps'][9]['number']=1
        try:helpers['pair_original_native'](a,r,j)
        except ValueError:pass
        else:raise AssertionError(mode)
    for mode in ('missing','decoded-only','signer','invocation','subjects','repository','build-type','ref','publisher'):
        v=copy.deepcopy(verified)
        if mode=='missing':v=[]
        elif mode=='decoded-only':v=[{'statement':v[0]['verificationResult']['statement']}]
        elif mode=='signer':v[0]['verificationResult']['signature']['certificate']['buildSignerURI']='foreign'
        elif mode=='invocation':v[0]['verificationResult']['statement']['predicate']['runDetails']['metadata']['invocationId']='foreign'
        elif mode=='subjects':v[0]['verificationResult']['statement']['subject'][0]['digest']['sha256']='0'*64
        elif mode=='repository':v[0]['verificationResult']['signature']['certificate']['sourceRepositoryIdentifier']='1'
        elif mode=='build-type':v[0]['verificationResult']['statement']['predicate']['buildDefinition']['buildType']='foreign'
        elif mode=='ref':v[0]['verificationResult']['signature']['certificate']['sourceRepositoryRef']='refs/heads/foreign'
        elif mode=='publisher':v[0]['verificationResult']['signature']['certificate']['buildSignerDigest']='a'*40
        try:helpers['pair_verified_attestation'](a,v)
        except (ValueError,KeyError):pass
        else:raise AssertionError(mode)
    text=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    job=text.split('\n  execution_pair:\n',1)[1]
    assert 'inputs.pair_recovery' in job.splitlines()[0] and '!inputs.readback_only' in job.splitlines()[0]
    attest=job.split('      - name: Attest only',1)[1].split('      - name:',1)[0]
    assert 'if: ${{ false }}' in attest
    assert job.index('Authenticate consumed original failure')<job.index('Require existing App bindings')<job.index('Obtain selected Trusted Publishing')
    preflight=text.split('  execution_pair_preflight:\n',1)[1].split('  execution_pair:\n',1)[0]
    for forbidden in ('NuGet/login','dotnet ','actions/attest@','nuget push','gh release','packages: write','contents: write'):
        assert forbidden not in preflight,forbidden
    assert 'publicationAuthorized' in preflight and 'pending-native-token-revocation-observation' in preflight
    print('PAIR_RECOVERY_SOURCE_CONTROLS passed native-negatives=11 verified-provenance-negatives=9 effects-disabled=true')


def main():
    pair_recovery_source_controls()
    complete_policy_source_controls()
    consumer_sdk_readback_controls()
    consumer_source_only_controls()
    consumer_readback_controls()
    pair_observation_controls()
    installed_version_preflight_controls()
    for version in ("0.2.0","0.3.0"):
        installed_isolation_diagnostic_controls(version)
    installed_isolation_diagnostic_controls("0.3.0","public")
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
    # A failed test inside command substitution does not itself stop a shell function.
    # Never emit zero to curl --max-time/timeout: zero disables their time bound.
    remaining=next(line.strip() for line in job.splitlines() if line.strip().startswith('remaining() {'))
    for seconds,deadline,expected in ((10,70,'30'),(10,17,'7'),(10,10,None),(10,9,None)):
        script=remaining+f'\nSECONDS={seconds}\ndeadline={deadline}\nvalue=$(remaining); code=$?\nprintf "%s:%s" "$code" "$value"\n'
        result=subprocess.run(['bash','-c',script],capture_output=True,text=True,timeout=5)
        assert result.returncode==0 and result.stdout==('0:'+expected if expected is not None else '1:'),(seconds,deadline,result.stdout)
    for gate in ('test "$CLI_ONLY" = false','test "$PREFLIGHT_ONLY" = false','test "$READBACK_ONLY" = false'):
        assert job.index(gate)<job.index('Attest only')<job.index('Push both retained')
    assert 'nuget push' not in prepare
    assert 'skip-duplicate' not in job and 'dotnet pack' not in job
    actual=heredoc('PY_PAIR_ADMISSION'); base=admission()
    base['enabled']=False
    base['operation']='publish-execution-pair-030-b11fb121-four38484632631'
    base['recovery']=copy.deepcopy(json.loads((ROOT/'eng/execution-pair-release-admission.json').read_text())['recovery'])
    base['recovery']['enabled']=True
    with tempfile.TemporaryDirectory(prefix='pair-selection-inert-') as tmp:consumer_fixture(Path(tmp),base)
    cases={'good':lambda a:None,'disabled':lambda a:a['recovery'].update(enabled=False),'original-replay':lambda a:a.update(enabled=True),
           'wrong-profile':lambda a:a.update(profile='cli'),'wrong-member':lambda a:a['packages'].pop(IDS[1]),
           'foreign-source':lambda a:a.update(source='z'*40),'missing-capability':lambda a:a.update(namespaceEntitlement=None),
           'not-entitled':lambda a:a['namespaceEntitlement'].update(accepted=False),
           'foreign-policy-owner':lambda a:a['namespaceEntitlement'].update(nugetUser='another-owner'),
           'foreign-policy-evidence':lambda a:a['namespaceEntitlement'].update(evidenceSha256='9'*64),
           'unknown-consumer-run':lambda a:a['consumerQualification'].update(runId=None),
           'unknown-executor':lambda a:a['consumerQualification'].update(executor=None),
           'wrong-entitlement-pair':lambda a:a['namespaceEntitlement'].update(packageIds=[IDS[0]]),
           'wrong-workflow':lambda a:a['namespaceEntitlement'].update(publisherWorkflow='other.yml'),
           'unknown-protection':lambda a:a.update(tagProtection=None),
           'fake-native-id':lambda a:a.update(runId=True),'unexpected-key':lambda a:a.update(ignored=True)}
    for name,mutate in cases.items():
        with tempfile.TemporaryDirectory(prefix='pair-admission-test-') as temporary:
            root=Path(temporary); a=copy.deepcopy(base);mutate(a)
            write(root/'eng/execution-pair-release-admission.json',a)
            (root/'eng/write-callable-cli-release-readback.py').write_bytes((ROOT/'eng/write-callable-cli-release-readback.py').read_bytes())
            (root/'eng/test-callable-cli-installed-harness.py').write_bytes((ROOT/'eng/test-callable-cli-installed-harness.py').read_bytes())
            environment={'PAIR_RECOVERY':'true','OPERATION':base['recovery']['operation'],'REQUESTED_SOURCE':base['source'],'REQUESTED_MERGE':base['protectedMerge'],
                         'REQUESTED_SHA256':base['manifestSha256'],'REQUESTED_PUBLISHER':'9'*40,'GITHUB_ENV':str(root/'env')}
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,environment,clear=True):
                    try:exec(actual,{})
                    except (AssertionError,KeyError,TypeError,ValueError,SystemExit):refused=True
            finally:os.chdir(old)
            assert refused == (name!='good'), name
            assert (root/'env').exists()==(name=='good'),name
    for version in ('0.2.0','0.3.0'): archive_controls(version)
    # Exercise the real nonpublishing installed job's input/archive bindings.
    installed_job=prepare.split('  execution_pair_installed:\n',1)[1]
    assert "if: inputs.preparation_mode == 'execution-pair-installed'" in installed_job
    assert '      contents: read\n      actions: read\n' in installed_job
    assert 'id-token:' not in installed_job and 'packages:' not in installed_job
    assert 'nuget push' not in installed_job and 'dotnet pack' not in installed_job
    assert 'gh release' not in installed_job and 'git tag' not in installed_job
    assert installed_job.index('--execution-pair-artifact') < installed_job.index('candidate "$READBACK_OUTPUT/candidate-load.json"')
    bind=heredoc('PY_PAIR_INSTALLED_BINDINGS','prepare')
    for mode in ('good','changed-archive','foreign-source','wrong-job','enabled','wrong-version','wrong-tag','old-manifest'):
        with tempfile.TemporaryDirectory(prefix='pair-installed-bindings-test-') as temporary:
            root=Path(temporary);readback=root/'readback';readback.mkdir()
            m={'version':'0.2.0' if mode=='old-manifest' else '0.3.0','tag':'execution/v0.2.0' if mode=='old-manifest' else 'execution/v0.3.0','sourceCommit':'a'*40,'sourceTree':'b'*40,'packages':[{'packageId':id,'sha256':str(i+1)*64} for i,id in enumerate(IDS)]}
            if mode=='wrong-tag':m['tag']='execution/v0.2.0'
            with zipfile.ZipFile(readback/'candidate.zip','w') as z:z.writestr('execution-pair-release-manifest.json',json.dumps(m))
            a=json.loads((ROOT/'eng/execution-pair-release-admission.json').read_bytes());a['enabled']=mode=='enabled';a['operation']=None
            write(root/'eng/execution-pair-release-admission.json',a)
            write(readback/'jobs.json',{'jobs':[{'id':2,'name':'foreign' if mode=='wrong-job' else 'Prepare the routine Execution pair without publication'}]})
            env={'READBACK_OUTPUT':str(readback),'ARCHIVE_SHA':'0'*64 if mode=='changed-archive' else hashlib.sha256((readback/'candidate.zip').read_bytes()).hexdigest(),
                 'SOURCE_A':'c'*40 if mode=='foreign-source' else 'a'*40,'CANDIDATE_VERSION':'0.2.0' if mode=='wrong-version' else '0.3.0','CANDIDATE_RUN':'1','CANDIDATE_ARTIFACT':'3'}
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,env,clear=True):
                    try:exec(bind,{})
                    except (AssertionError,KeyError,TypeError,ValueError,SystemExit):refused=True
            finally:os.chdir(old)
            assert refused==(mode!='good'),mode
            assert (readback/'candidate-bindings.json').exists()==(mode=='good'),mode
            assert json.loads((root/'eng/execution-pair-release-admission.json').read_bytes())==a
            if mode=='good':
                bound=json.loads((readback/'candidate-bindings.json').read_bytes())
                assert bound['version']=='0.3.0' and bound['tag']=='execution/v0.3.0' and bound['enabled'] is False
                assert bound['operation'] is None
                for key in ('namespaceEntitlement','tagProtection','consumerQualification'):assert bound[key]==a[key]
    gate=installed_job.split('          set -euo pipefail\n',1)[1].split('      - name: Run actual shared pair guards',1)[0]
    gate='set -euo pipefail\n'+'\n'.join(line[10:] if line.startswith('          ') else line for line in gate.splitlines())
    for mode in ('good','package-change','bad-source','bad-run','bad-hash','moved-executor','diff-failure','metadata-change','bad-version'):
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
                 'MODE':'execution-pair-installed',
                 'GITHUB_REF':'refs/heads/main','GITHUB_RUN_ATTEMPT':'1','GITHUB_REPOSITORY':'FS-GG/FS.GG.Coordination','GITHUB_SHA':'9'*40,
                 'EXECUTOR_SOURCE':'8'*40 if mode=='moved-executor' else '9'*40,
                 'SOURCE_A':'z'*40 if mode=='bad-source' else 'a'*40,'CANDIDATE_RUN':'true' if mode=='bad-run' else '1',
                 'CANDIDATE_ARTIFACT':'3','ARCHIVE_SHA':'x'*64 if mode=='bad-hash' else 'c'*64,'CANDIDATE_VERSION':'0.2.0' if mode=='bad-version' else '0.3.0',
                 'CANDIDATE_OUTPUT':str(root/'candidate'),'READBACK_OUTPUT':str(root/'readback'),'GITHUB_ENV':str(root/'env')}
            result=subprocess.run(['bash','-c',gate],env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode in {'good','metadata-change'}),(mode,result.stderr)
            assert (root/'env').exists()==(mode in {'good','metadata-change'}),mode
            if mode in {'good','metadata-change'}:assert 'CANDIDATE_VERSION=0.3.0\n' in (root/'env').read_text()
    publisher_source=job.split('      - name: Bind pair A ancestry and publication-only B changes\n',1)[1].split('      - name: Authenticate and extract',1)[0]
    publisher_source=publisher_source.split('          set -euo pipefail\n',1)[1]
    publisher_source='set -euo pipefail\n'+'\n'.join(line[10:] if line.startswith('          ') else line for line in publisher_source.splitlines())
    for mode in ('good','proof-change','diff-failure','package-change','metadata-change',
                 'custody-guard','custody-tests','foreign-owner','foreign-sibling',
                 'foreign-test-sibling','contracts-change','guard-descendant','guard-plus-foreign'):
        with tempfile.TemporaryDirectory(prefix='pair-publisher-source-test-') as temporary:
            root=Path(temporary);stub=root/'git'
            stub.write_text("""#!/usr/bin/python3
import os,sys
args=sys.argv[1:]
if args[0]=='diff':
    if os.environ['FIXTURE_MODE']=='diff-failure':sys.exit(1)
    paths={'package-change':'src/FS.GG.Execution.Processes/ProcessSessions.fs',
           'proof-change':'eng/test-callable-cli-installed-harness.py',
           'metadata-change':'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json',
           'custody-guard':'eng/retain-choreo-c2-diagnostics.py',
           'custody-tests':'eng/tests/choreo-c2-tlc-retry/test_diagnostics.py',
           'foreign-owner':'eng/shared_process_owner.py',
           'foreign-sibling':'eng/other-diagnostics.py',
           'foreign-test-sibling':'eng/tests/choreo-c2-tlc-retry/other.py',
           'contracts-change':'src/FS.GG.Execution.Contracts/Types.fs',
           'guard-descendant':'eng/retain-choreo-c2-diagnostics.py/extra',
           'guard-plus-foreign':'eng/retain-choreo-c2-diagnostics.py\\nforeign.txt'}
    print(paths.get(os.environ['FIXTURE_MODE'],'eng/execution-pair-release-admission.json'))
if args[0]=='rev-parse':print(os.environ['TREE_A'])
""")
            stub.chmod(0o700)
            env={**os.environ,'PATH':str(root)+os.pathsep+os.environ['PATH'],'FIXTURE_MODE':mode,'SOURCE_A':'a'*40,'MERGE_A':'a'*40,'TREE_A':'b'*40,'GITHUB_SHA':'9'*40}
            result=subprocess.run(['bash','-c',publisher_source],env=env,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode in {'good','proof-change','metadata-change','custody-guard','custody-tests'}),(mode,result.stderr)
    with tempfile.TemporaryDirectory(prefix='pair-readback-test-') as temporary:
        root=Path(temporary)
        manifest={'schema':'fsgg.coordination.execution-pair-release-preparation/1','profile':'execution-pair',
                  'version':'0.3.0','tag':'execution/v0.3.0','sourceCommit':'a'*40,'sourceTree':'b'*40,
                  'publicationAuthorized':False,'tagAuthorized':False,
                  'packages':[{'packageId':id,'sha256':str(index+1)*64,'assemblySha256':str(index+3)*64} for index,id in enumerate(IDS)]}
        installed={'schema':'fsgg.coordination.execution-pair-installed-load/1','mode':'public','publicOnly':True,
                   'producerProjectReferenced':False,'packageInputsOnly':True,'physicalSourceExclusionEstablished':False,
                   'privateCachesUnavailable':True,'effectiveProjectIsolationGuard':True,'effectiveNuGetFoldersVerified':True,'actualCompilerReferencesVerified':True,'nativeFixtureInvoked':False,'consumerAdoptionAccepted':False,
                   'packages':[{'packageId':row['packageId'],'version':'0.3.0','runtimeSha256':row['assemblySha256']} for row in manifest['packages']]}
        for mode in ('good','missing-second-feed','wrong-candidate','github-raw-mismatch','payload-unknown','wrong-runtime','wrong-version','claimed-adoption','contradictory-admission-packages','contradictory-admission-merge','contradictory-admission-version','contradictory-admission-tag'):
            current=copy.deepcopy(manifest);smoke=copy.deepcopy(installed)
            if mode=='wrong-version':current['version']='0.2.0'
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
            selection=admission();selection['manifestSha256']=hashlib.sha256((root/'manifest.json').read_bytes()).hexdigest()
            selection['packages']={row['packageId']:row['sha256'] for row in current['packages']}
            # Keep the manifest, feed receipts and coherent consumer proof mutually valid;
            # each negative contradicts only the final admission cross-field join.
            if mode=='contradictory-admission-packages':selection['packages'][IDS[0]]='8'*64
            if mode=='contradictory-admission-merge':selection['protectedMerge']='8'*40
            if mode=='contradictory-admission-version':selection['version']='0.2.0'
            if mode=='contradictory-admission-tag':selection['tag']='execution/v0.2.0'
            consumer_fixture(root/'consumers',selection);write(root/'selection.json',selection)
            output=root/(mode+'.json')
            command=['python3',str(ROOT/'eng/write-callable-cli-release-readback.py'),'--execution-pair',
                     '--publisher','c'*40,'--source','a'*40,'--tree','b'*40,'--merge','a'*40,
                     '--manifest',str(root/'manifest.json'),'--manifest-sha256',hashlib.sha256((root/'manifest.json').read_bytes()).hexdigest(),
                     '--admission',str(root/'selection.json'),'--consumer-root',str(root/'consumers'),'--feed-root',str(root),'--installed',str(root/'installed.json'),'--output',str(output)]
            result=subprocess.run(command,capture_output=True,timeout=5)
            assert (result.returncode==0)==(mode=='good'),(mode,result.stderr)
            assert output.exists()==(mode=='good'),mode
    actual=heredoc('PY_PAIR_PROTECTION')
    desired=json.loads((ROOT/'eng/repository-settings/release-tag-ruleset.json').read_text())
    keys=('target','enforcement','conditions','bypass_actors','rules')
    policy={key:desired[key] for key in keys}
    for mode in ('good','good-omitted-tag-parameters','update-enabled','update-numeric-zero','update-null','update-unknown-parameter','update-unknown-field','unprotected-component','inactive','bypass','missing-actors','null-actors','object-actors','missing-actors-requester-never','missing-target','null-policy','foreign-id-type','deleted-signature-rule','foreign-ruleset','wrong-pin'):
        with tempfile.TemporaryDirectory(prefix='pair-protection-test-') as temporary:
            root=Path(temporary);readback=root/'readback';readback.mkdir()
            live=copy.deepcopy(policy); live['id']=21633398
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
            if mode in {'missing-actors','missing-actors-requester-never'}:live.pop('bypass_actors')
            if mode=='missing-actors-requester-never':live['current_user_can_bypass']='never'
            if mode=='null-actors':live['bypass_actors']=None
            if mode=='object-actors':live['bypass_actors']={}
            if mode=='missing-target':live.pop('target')
            if mode=='null-policy':live=None
            if mode=='foreign-id-type':live['id']=21633398.0
            if mode=='deleted-signature-rule':live['rules']=[rule for rule in live['rules'] if rule['type']!='required_signatures']
            if mode=='foreign-ruleset':live['id']=5
            a=admission();a['tagProtection']={'rulesetId':21633398,'policySha256':hashlib.sha256(json.dumps(policy,sort_keys=True,separators=(',',':')).encode()).hexdigest()}
            if mode=='wrong-pin':a['tagProtection']['policySha256']='0'*64
            write(root/'eng/execution-pair-release-admission.json',a)
            write(root/'eng/repository-settings/release-tag-ruleset.json',desired)
            (root/'eng/test-callable-cli-installed-harness.py').write_bytes((ROOT/'eng/test-callable-cli-installed-harness.py').read_bytes())
            write(readback/'tag-protection.json',live)
            old=Path.cwd();os.chdir(root);refused=False
            try:
                with patch.dict(os.environ,{'READBACK_OUTPUT':str(readback)},clear=True):
                    try:exec(actual,{})
                    except (AssertionError,KeyError,TypeError,ValueError,SystemExit):refused=True
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
    print('EXECUTION_PAIR_RELEASE_INERT_CONTROLS passed consumer-deadline=4 admission=16 archive=20 readback=12 tag-protection=21 four-feed-cells=3 installed-bindings=8 installed-source=9 publisher-source=13 isolation-and-byte-joins=48 version-preflight=11 selectors=exclusive')

def consumer_source_only_controls():
    """Exercise workflow predicates with inert records; never run a job or SDK."""
    import ast
    import stat
    import xml.etree.ElementTree as E
    prepare=(ROOT/'.github/workflows/callable-cli-release-prepare.yml').read_text()
    scope={'json':json,'re':re,'stat':stat,'pathlib':__import__('pathlib'),'E':E,'hashlib':__import__('hashlib')}
    markers=('PY_CONSUMER_SIGNATURE','PY_CONSUMER_DOTNET','PY_CONSUMER_EXECUTOR_PATHS','PY_CONSUMER_SELECTION','PY_CONSUMER_TRANSFER','PY_CONSUMER_PAYLOAD','PY_CONSUMER_INPUT_CLOSURE','PY_CONSUMER_RESULTS')
    for marker in markers:
        body=prepare.split("          python3 - <<'"+marker+"'\n",1)[1].split('          '+marker+'\n',1)[0]
        module=ast.parse('\n'.join(line[10:] for line in body.splitlines()))
        # Only the actual pure predicates are evaluated, never their job bodies.
        definitions=[node for node in module.body if isinstance(node,ast.FunctionDef) and node.name!='original_pair']
        exec(compile(ast.Module(body=definitions,type_ignores=[]),marker,'exec'),scope)
    count=0
    def check(fn,*args,refuse=False):
        nonlocal count
        rejected=False
        try:fn(*args)
        except (ValueError,TypeError,KeyError,AttributeError):rejected=True
        assert rejected==refuse,(getattr(fn,'__name__',str(fn)),refuse,args)
        count+=1
    # Original receipt bytes are historical data, not a synthesized authority record.
    import base64
    public_body=prepare.split("          python3 - <<'PY_FSQUINT_PUBLIC'\n",1)[1].split('          PY_FSQUINT_PUBLIC\n',1)[0]
    public_ast=ast.parse('\n'.join(line[10:] for line in public_body.splitlines()))
    literal=next(n.value.args[0].value for n in public_ast.body if isinstance(n,ast.Assign) and any(isinstance(v,ast.Name) and v.id=='raw' for v in n.targets))
    historical=base64.b64decode(literal,validate=True)
    receipt_fn=scope['consumer_historical_receipt'];check(receipt_fn,historical)
    check(receipt_fn,historical+b' ',refuse=True);check(receipt_fn,historical[:-1],refuse=True)
    receipt=json.loads(historical)
    raw_fn=scope['consumer_public_raw'];members_fn=scope['consumer_public_members'];maps_fn=scope['consumer_public_payloads']
    for id,size,digest,dll,n in scope['consumer_public_specs']():
        check(raw_fn,size,digest,id)
        check(raw_fn,size,receipt[id+':github']['archiveSha256'],id,refuse=True)
        check(raw_fn,size+1,digest,id,refuse=True)
        expected=receipt[id+':nuget']['payloads'];assert len(expected)==n
        rows=[(name,100,stat.S_IFREG) for name in expected]+[('.signature.p7s',100,stat.S_IFREG)]
        check(members_fn,rows,expected);check(maps_fn,dict(expected),expected)
        for name in expected:
            changed=dict(expected);changed[name]='0'*64;check(maps_fn,changed,expected,refuse=True)
        check(maps_fn,{**expected,'extra':'a'*64},expected,refuse=True)
        check(maps_fn,dict(list(expected.items())[1:]),expected,refuse=True)
        for changed in (rows[:-1],rows+rows[-1:],rows+[('extra',1,stat.S_IFREG)],rows[:-1]+[('nested/.signature.p7s',100,stat.S_IFREG)],[(rows[0][0],2097153,stat.S_IFREG)]+rows[1:],[(rows[0][0],1,stat.S_IFLNK)]+rows[1:]):check(members_fn,changed,expected,refuse=True)
        for unsafe in ('/abs','../up','a/./b','a//b','C:/file','a\\b'):
            check(members_fn,[(unsafe,1,stat.S_IFREG),('.signature.p7s',1,stat.S_IFREG)],{unsafe:'a'*64},refuse=True)
        deps='<dependency id="FSharp.Core" version="10.1.302" exclude="Build,Analyzers" />'
        if id=='FsQuint.Tooling':deps='<dependency id="FsQuint" version="0.1.0" exclude="Build,Analyzers" />'+deps
        nuspec=('<package><metadata><id>'+id+'</id><version>0.1.0</version><repository type="git" url="https://github.com/FS-GG/FsQuint" commit="34eeca981c136144ced73a3f98b5ce04218e89c7" /><dependencies><group>'+deps+'</group></dependencies></metadata></package>').encode()
        check(scope['consumer_public_nuspec'],nuspec,id)
        for old,new in ((b'0.1.0',b'0.2.0'),(b'34eeca981',b'000000000'),(b'10.1.302',b'10.1.401'),(b'FS-GG/FsQuint',b'other/FsQuint')):check(scope['consumer_public_nuspec'],nuspec.replace(old,new),id,refuse=True)
    signature=scope['consumer_signature_result'];check(signature,0,42)
    for code,size in ((1,42),(124,42),(0,0),(0,65537),(None,42),(True,42)):check(signature,code,size,refuse=True)
    sdd=scope['consumer_sdd_expected'];assert sdd('sdd-s1')==(18,8,17,27,1) and sdd('sdd-rollback')==(13,8,17,27,1)
    check(sdd,'sdd-s1');check(sdd,'sdd-rollback');check(sdd,'other',refuse=True)
    assert 'repos/FS-GG/FsQuint/actions/' not in prepare
    assert 'fsquint-original/candidate.zip' not in prepare
    public_shell=prepare.split('          PUBLIC_ROLLBACK_START=',1)[1].split("          python3 - <<'PY_FSQUINT_PUBLIC'",1)[0]
    assert 'env -u GH_TOKEN -u GITHUB_TOKEN curl --disable' in public_shell
    assert '--location' not in public_shell and '--header' not in public_shell and '--retry' not in public_shell
    assert 'https://api.nuget.org/v3-flatcontainer/$id/0.1.0/$id.0.1.0.nupkg' in public_shell
    assert '--max-filesize "$size"' in public_shell and '--max-time "$remaining"' in public_shell
    # Signed raw identity and normalized NuGet identity are distinct domains.
    normalized='S1dCVHdcLRdXmZnjCOk+nJLhjO3acz705bEeQm2cBrwxiEsDpVj7PD8tECcKo/Bzpbw5ZOI4bLMWdlqSkISsbg=='
    observed='A'*86+'=='
    expected_cache={'version':'0.1.0','packageBytes':80175,'packageSha256':'e170d70a683321818944d86bb9c9e087323e01391738b3b4a621892ef91a2a8a','contentHash':normalized}
    cache=scope['consumer_cache_identity'];lock_entry=scope['consumer_lock_entry'];asset_hash=scope['consumer_asset_hash']
    check(cache,80175,expected_cache['packageSha256'],normalized,observed,expected_cache)
    for size,digest,meta,observed_value in ((80174,expected_cache['packageSha256'],normalized,observed),(80175,'0'*64,normalized,observed),(80175,expected_cache['packageSha256'],observed,observed),(80175,expected_cache['packageSha256'],None,observed),(80175,expected_cache['packageSha256'],normalized,'')):
        check(cache,size,digest,meta,observed_value,expected_cache,refuse=True)
    direct={'type':'Direct','requested':'[0.1.0, 0.1.0]','resolved':'0.1.0','contentHash':normalized}
    check(lock_entry,direct,expected_cache)
    check(lock_entry,{'type':'Transitive','resolved':'0.1.0','contentHash':normalized},expected_cache)
    for changed in ({**direct,'type':'Project'},{**direct,'resolved':'0.2.0'},{**direct,'contentHash':observed},{**direct,'requested':'[0.1.0, )'},{k:v for k,v in direct.items() if k!='contentHash'}):check(lock_entry,changed,expected_cache,refuse=True)
    check(asset_hash,{'type':'package','sha512':normalized},expected_cache)
    for changed in ({'type':'project','sha512':normalized},{'type':'package','sha512':observed},{'type':'package'}):check(asset_hash,changed,expected_cache,refuse=True)
    # Genuine NuGet schema 2 retains the same package/hash/request obligations.
    schema=scope['consumer_lock_schema']
    for version in (1,2):check(schema,{'version':version,'dependencies':{'net10.0':{'FS.GG.Execution.Contracts':direct}}})
    for version in (True,False,1.0,2.0,'1','2',0,3,None):check(schema,{'version':version,'dependencies':{'net10.0':{}}},refuse=True)
    for document in ({'version':2},{'version':2,'dependencies':[]},{'version':2,'dependencies':{'net10.0':[]}},[],None):check(schema,document,refuse=True)
    # These are actual original SDK401 package/member identities, not the same-version public payload.
    core_assemblies={'lib/netstandard2.0/FSharp.Core.dll':'26d0e3e00c769b9b6e9d8c1925885f7c16a51c605f795093386a25b909012991','lib/netstandard2.1/FSharp.Core.dll':'9abd1f889fc8e2d2a74d6ac83a31e6c3012c075a17946a7d687c0cbe33ed15be'}
    core_raw='17a9cfd456ce207ebf721eef50d8e1f70aca5e70e2d265a62ca704211526c0ff';core_normalized='IGqjL9U8pQl7CaCpFP3f5slZMduE8c7ZBx7HycAQ9dc1tawyniNyw7Fh6L2cShIoQDcaDL+S8/kokhWs8WwnBQ=='
    payload=scope['consumer_sdk_core_payload'];check(payload,3049582,core_raw,('FSharp.Core','10.1.401'),core_assemblies)
    for size,digest,identity,assemblies in ((True,core_raw,('FSharp.Core','10.1.401'),core_assemblies),(3049581,core_raw,('FSharp.Core','10.1.401'),core_assemblies),(3065170,'cf1f4e69fc2d1351af9fa9923ac90ed466088730a00867cbe0eb1b3b2c68963e',('FSharp.Core','10.1.401'),core_assemblies),(3049582,core_raw,('FSharp.Core','10.1.302'),core_assemblies),(3049582,core_raw,('FsQuint','10.1.401'),core_assemblies),(3049582,core_raw,('FSharp.Core','10.1.401'),{**core_assemblies,'lib/netstandard2.1/FSharp.Core.dll':'39b0b7f06c11bedd93f94b6f101fffd645f1c4437a5a6d3ee79259f855aeb11a'}),(3049582,core_raw,('FSharp.Core','10.1.401'),{})):
        check(payload,size,digest,identity,assemblies,refuse=True)
    feed=scope['consumer_sdk_core_feed'];pair_files=['FS.GG.Execution.Contracts.0.3.0.nupkg','FS.GG.Execution.Processes.0.3.0.nupkg'];core_file='FSharp.Core.10.1.401.nupkg'
    check(feed,'fsquint-s1',pair_files+[core_file],pair_files)
    for case in ('sdd-s1','sdd-rollback','fsquint-rollback'):
        check(feed,case,pair_files,pair_files);check(feed,case,pair_files+[core_file],pair_files,refuse=True)
    for files in (pair_files,pair_files+[core_file,core_file],pair_files+[core_file,'extra.nupkg'],[core_file]):check(feed,'fsquint-s1',files,pair_files,refuse=True)
    check(feed,'unsupported',pair_files,pair_files,refuse=True)
    core_expected={'version':'10.1.401','packageBytes':3049582,'packageSha256':core_raw,'contentHash':core_normalized,'dllSha256':core_assemblies['lib/netstandard2.1/FSharp.Core.dll'],'dllRelativePath':'lib/netstandard2.1/FSharp.Core.dll'}
    original_core={'type':'Direct','requested':'[10.1.401, )','resolved':'10.1.401','contentHash':core_normalized}
    core_lock=scope['consumer_sdk_core_lock_entry'];check(core_lock,dict(original_core),core_expected,original_core)
    for row in ({**original_core,'requested':'[10.1.401]'},{**original_core,'type':'Project'},{**original_core,'resolved':'10.1.302'},{**original_core,'contentHash':'/lvOK51KD8IangBYkfDVna3/HrE/r4VUnX3dI01KDspctoYXmind3POeoYkqWKeXHwiKlf6Hc9TxwSnoSCWcjw=='}):check(core_lock,row,core_expected,original_core,refuse=True)
    # Preserve the existing exact-direct pair rule; Core uses byte-pinned original implicit request.
    check(lock_entry,original_core,core_expected,refuse=True)
    bind=scope['consumer_sdk_core_binding'];binding={'sdkVersion':'10.0.401','sdkSourcePath':'/sdk/sdk/10.0.401/FSharp/library-packs/FSharp.Core.10.1.401.nupkg','coldFeedPath':'/feed/'+core_file,'expected':core_expected,'assemblyPayloads':core_assemblies,'sourceLockPinsSha256':'a'*64,'implicitFallbackEnabled':False,'sameVersionPublicSubstitutionAccepted':False}
    check(bind,binding,'/sdk','/feed','a'*64)
    for changed in ({**binding,'sdkVersion':'10.0.400'},{**binding,'sdkSourcePath':'/public/'+core_file},{**binding,'coldFeedPath':'/public/'+core_file},{**binding,'expected':{**core_expected,'packageSha256':'cf1f4e69fc2d1351af9fa9923ac90ed466088730a00867cbe0eb1b3b2c68963e'}},{**binding,'assemblyPayloads':{}},{**binding,'sourceLockPinsSha256':'b'*64},{**binding,'implicitFallbackEnabled':True},{**binding,'sameVersionPublicSubstitutionAccepted':True}):check(bind,changed,'/sdk','/feed','a'*64,refuse=True)
    selectors={k:str(i+1)*40 for i,k in enumerate(('sdd_source','sdd_tree','fsquint_source','fsquint_tree'))}
    select=scope['consumer_selection']
    check(select,'execution-pair-consumers',json.dumps(selectors))
    check(select,'execution-pair-installed','')
    for raw in ('', '{}','[]',json.dumps({**selectors,'extra':'a'*40}),json.dumps({**selectors,'sdd_source':'main'}),json.dumps({**selectors,'sdd_source':'0'*40}),json.dumps({**selectors,'sdd_tree':True}),json.dumps(selectors)[:-1]+',"sdd_source":"'+'a'*40+'"}'):
        check(select,'execution-pair-consumers',raw,refuse=True)
    check(select,'execution-pair-installed',json.dumps(selectors),refuse=True)
    check(select,'portable',json.dumps(selectors),refuse=True)
    paths=scope['consumer_executor_paths']
    three=('.github/workflows/callable-cli-release-prepare.yml','tests/portable-workspace/release/test_execution_pair_release.py','evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json')
    five=three+('.github/workflows/callable-cli-release-publish.yml','eng/test-callable-cli-installed-harness.py')
    check(paths,'execution-pair-consumers','\n'.join(three))
    check(paths,'execution-pair-consumers','')
    for path in three:check(paths,'execution-pair-consumers',path)
    for path in five[3:]+('src/FS.GG.Execution.Processes/Process.fs','../eng/test-callable-cli-installed-harness.py'):
        check(paths,'execution-pair-consumers','\n'.join(three+(path,)),refuse=True)
    for path in five:check(paths,'execution-pair-installed',path)
    check(paths,'portable','\n'.join(three),refuse=True)
    check(paths,'execution-pair-consumers','x'*16385,refuse=True)
    installed=prepare.split('  execution_pair_installed:\n',1)[1].split('  execution_pair_sdd_s1:\n',1)[0]
    legacy=installed.split('            case "$path" in\n',1)[1].split(') ;;',1)[0].strip()
    assert set(legacy.split('|'))==set(five),'installed-only original five-path population drift'
    assert installed.index('PY_CONSUMER_EXECUTOR_PATHS')<installed.index('            case "$path" in')
    assert '          MODE: ${{ inputs.preparation_mode }}' in installed
    count+=3
    transfer={'id':7,'expired':False,'name':'execution-pair-installed-'+'a'*40+'-8','size_in_bytes':100,'workflow_run':{'id':8,'head_sha':'b'*40,'repository_id':1346720714,'head_repository_id':1346720714}}
    check(scope['consumer_transfer'],transfer,'7','8','b'*40,'a'*40)
    for field,value in [('id',True),('id',9),('expired',True),('name','other'),('size_in_bytes',0),('size_in_bytes',True),('size_in_bytes',67108865)]:
        row=copy.deepcopy(transfer);row[field]=value
        check(scope['consumer_transfer'],row,'7','8','b'*40,'a'*40,refuse=True)
    for field,value in [('id',9),('head_sha','c'*40),('repository_id',1),('head_repository_id',1)]:
        row=copy.deepcopy(transfer);row['workflow_run'][field]=value
        check(scope['consumer_transfer'],row,'7','8','b'*40,'a'*40,refuse=True)
    member=scope['consumer_members'];expected={'a':4,'b':4}
    check(member,[('a',4,stat.S_IFREG),('b',1,0)],expected)
    for rows in ([('a',4,0)],[('a',1,0),('a',1,0)],[('a',5,0),('b',1,0)],[('a',0,0),('b',1,0)],[('a',1,stat.S_IFLNK),('b',1,0)],[('../a',1,0),('b',1,0)]):check(member,rows,expected,refuse=True)
    manifest={'schema':'fsgg.coordination.execution-pair-release-preparation/1','profile':'execution-pair','version':'0.3.0','tag':'execution/v0.3.0','sourceCommit':'a'*40,'sourceTree':'b'*40,'publicationAuthorized':False,'tagAuthorized':False,'ownership':{'reservation':'unreserved'},'packages':[{'packageId':id,'file':id+'.0.3.0.nupkg','bytes':1,'sha256':'c'*64,'assemblySha256':'d'*64} for id in IDS]}
    check(scope['consumer_manifest'],manifest,'0.3.0','a'*40)
    for field,value in [('version','0.2.0'),('sourceCommit','e'*40),('tag','v0.3.0'),('publicationAuthorized',True),('tagAuthorized',True),('sourceTree','main'),('packages',manifest['packages'][:1]),('ownership',{'reservation':'reserved'})]:
        row=copy.deepcopy(manifest);row[field]=value
        check(scope['consumer_manifest'],row,'0.3.0','a'*40,refuse=True)
    for field,value in [('bytes',True),('bytes',16777217),('file','other.nupkg'),('sha256','x'*64),('assemblySha256','')]:
        row=copy.deepcopy(manifest);row['packages'][0][field]=value
        check(scope['consumer_manifest'],row,'0.3.0','a'*40,refuse=True)
    layout=scope['consumer_dotnet_layout']
    check(layout,'/private/sdk/dotnet','/private/sdk',['10.0.401'])
    check(layout,'/global/dotnet','/private/sdk',['10.0.401'],refuse=True)
    check(layout,'/private/sdk/dotnet','/private/sdk',['10.0.401','10.0.402'],refuse=True)
    check(layout,'/private/sdk/dotnet','/private/sdk',[],refuse=True)
    toolpins={'sdk/10.0.401/.version':{'bytes':98,'sha256':'496179aced13abd185f027a1ce8519cc57aa194a5538c51226c0c12e63a4a5ba'},'sdk/10.0.401/FSharp/fsc.dll':{'bytes':97104,'sha256':'2bd6190eafa94f47739538467f5a7cd52de233a627789c69f07959d7578e4d6b'},'shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so':{'bytes':7110952,'sha256':'df5cfe7ba5abf7931f4836726f2fbb0d47dc32caa45d3c209ddf7ccd25c32f4c'}}
    check(scope['consumer_dotnet_pins'],toolpins)
    for name in toolpins:
        row=copy.deepcopy(toolpins);row[name]['sha256']='0'*64
        check(scope['consumer_dotnet_pins'],row,refuse=True)
    check(scope['consumer_dotnet_pins'],{},refuse=True)
    compiler={'D':'10.0.401','H':'/private/sdk/dotnet','F':'/private/sdk/dotnet','S':'/private/sdk/sdk/10.0.401/Sdks','X':'/private/sdk/sdk/10.0.401/FSharp/fsc.dll'}
    check(scope['consumer_compiler_evaluation'],compiler,'/private/sdk/dotnet','/private/sdk')
    for key in compiler:
        row=copy.deepcopy(compiler);row[key]='other'
        check(scope['consumer_compiler_evaluation'],row,'/private/sdk/dotnet','/private/sdk',refuse=True)
    check(scope['consumer_compiler_evaluation'],{**compiler,'extra':'other'},'/private/sdk/dotnet','/private/sdk',refuse=True)
    check(scope['consumer_compiler_evaluation'],{},'/private/sdk/dotnet','/private/sdk',refuse=True)
    runtime={'runtimeOptions':{'tfm':'net10.0','rollForward':'Disable','framework':{'name':'Microsoft.NETCore.App','version':'10.0.12'}}}
    check(scope['consumer_runtime_config'],runtime)
    for key,value in [('rollForward','Major'),('rollForward',None),('tfm','net8.0'),('framework',{'name':'Microsoft.NETCore.App','version':'10.0.0'}),('frameworks',[{'name':'Microsoft.NETCore.App','version':'10.0.12'}])]:
        row=copy.deepcopy(runtime);row['runtimeOptions'][key]=value
        check(scope['consumer_runtime_config'],row,refuse=True)
    restore_sources=scope['consumer_restore_sources']
    assert restore_sources({'original':{},'https://api.nuget.org/v3/index.json':{}},'original')
    assert not restore_sources({'original':{},'https://api.nuget.org/v3/index.json':{},'/sdk/FSharp/library-packs':{}},'original')
    assert not restore_sources({'other':{},'https://api.nuget.org/v3/index.json':{}},'original')
    count+=3
    exact=scope['consumer_exact_range']
    assert exact('[0.3.0]','0.3.0') and exact('[0.3.0, 0.3.0]','0.3.0')
    assert not any(exact(v,'0.3.0') for v in ('0.3.0','[0.3.0,)','[0.2.0]','*',None))
    count+=7
    check(scope['consumer_path_allowed'],str(ROOT/'.github/workflows/callable-cli-release-prepare.yml'),[ROOT])
    check(scope['consumer_path_allowed'],str(ROOT/'.github/workflows/callable-cli-release-prepare.yml'),[ROOT/'tests'],refuse=True)
    check(scope['consumer_path_allowed'],'relative.fs',[ROOT],refuse=True)
    relative=scope['consumer_producer_relative']
    assert not relative(('receiver-source','src','Program.fs'))
    assert relative(('src','FS.GG.Execution.Processes','Process.fs'))
    assert relative(('FS.GG.Execution.Contracts.fsproj',))
    assert not relative(('fs.gg.execution.contracts','0.3.0','lib','net10.0','FS.GG.Execution.Contracts.dll'))
    count+=4
    ns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    trx=('<TestRun xmlns="'+ns+'"><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" notExecuted="0"/></ResultSummary><Results><UnitTestResult testId="a" testName="selected" outcome="Passed"/></Results></TestRun>').encode()
    check(scope['consumer_trx'],trx,1)
    check(scope['consumer_trx'],trx,2,refuse=True)
    check(scope['consumer_trx'],trx.replace(b'outcome="Passed"',b'outcome="Failed"'),1,refuse=True)
    check(scope['consumer_trx'],trx.replace(b'notExecuted="0"',b'notExecuted="1"'),1,refuse=True)
    # Derive the finite required labels from the actual function, not invented receipts.
    result_body=prepare.split("          python3 - <<'PY_CONSUMER_RESULTS'\n",1)[1].split('          PY_CONSUMER_RESULTS\n',1)[0]
    result_ast=ast.parse('\n'.join(line[10:] for line in result_body.splitlines()))
    fn=next(node for node in result_ast.body if isinstance(node,ast.FunctionDef) and node.name=='consumer_fsquint')
    labels=[]
    for node in ast.walk(fn):
        if isinstance(node,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='required' for t in node.targets):labels+=ast.literal_eval(node.value)
        if isinstance(node,ast.AugAssign) and isinstance(node.target,ast.Name) and node.target.id=='required':labels+=ast.literal_eval(node.value)
    # Independent selected-source lifecycle inventory: actual immutable Program708587.
    lifecycle_labels=['Quint cancellation reaches actual workload', 'Quint cancellation fixture child is executing', 'Quint caller cancellation returns within observation allowance', 'Quint caller token retains cancellation first cause', 'Quint cancellation known child retired', 'held pipe fixture reaches workload', 'held pipe leader exits before work deadline', 'held pipe recorded child remains executing', 'passing marker and leader exit cannot complete held pipe', 'held pipe observation returns within fixture allowance', 'held pipe work deadline retains timeout first cause', 'held pipe known child retired by fixture']
    labels+=lifecycle_labels
    assert len(labels)==len(set(labels)) and len(labels)<124
    labels+=['inert unrelated case '+str(i) for i in range(124-len(labels))]
    log=('\n'.join('PASS '+v for v in labels)+'\n'+str(len(labels))+' checks passed\n').encode()
    check(scope['consumer_fsquint'],log,True)
    for index,label in enumerate(lifecycle_labels):
        missing=log.replace(('PASS '+label+'\n').encode(),('PASS omitted lifecycle '+str(index)+'\n').encode())
        check(scope['consumer_fsquint'],missing,True,refuse=True)
    duplicate=log.replace(('PASS '+labels[-1]+'\n').encode(),('PASS '+labels[-2]+'\n').encode())
    check(scope['consumer_fsquint'],duplicate,True,refuse=True)
    for old_count in (85,112):
        stale=('\n'.join('PASS '+name for name in labels[:old_count])+'\n'+str(old_count)+' checks passed\n').encode()
        check(scope['consumer_fsquint'],stale,True,refuse=True)
    legacy=('\n'.join('PASS '+name for name in labels[:27])+'\n27 checks passed\n').encode()
    check(scope['consumer_fsquint'],legacy,False)
    check(scope['consumer_fsquint'],log.replace(b'PASS batch stdin is closed',b'absent batch stdin is closed'),True,refuse=True)
    check(scope['consumer_fsquint'],log+b'FAIL later\n',True,refuse=True)
    check(scope['consumer_fsquint'],b'0 checks passed\n',True,refuse=True)
    clock_lines=[line[10:] for line in prepare.splitlines() if line.startswith('          PUBLIC_ROLLBACK_START=') or line=='          export PUBLIC_ROLLBACK_START']
    assert len(clock_lines)==2 and clock_lines[1]=='export PUBLIC_ROLLBACK_START'
    assert 'export PUBLIC_ROLLBACK_START=' not in prepare
    for fails in (False,True):
        with tempfile.TemporaryDirectory(prefix='proc6-clock-binding-test-') as temporary:
            root=Path(temporary);stub=root/'python3'
            stub.write_text('#!/usr/bin/python3\nimport os,sys\nif os.environ["FIXTURE_CLOCK_FAIL"]=="1":sys.exit(7)\nprint("123.5")\n')
            stub.chmod(0o700)
            env={**os.environ,'PATH':str(root)+os.pathsep+os.environ['PATH'],'FIXTURE_CLOCK_FAIL':'1' if fails else '0'}
            script='set -euo pipefail\n'+'\n'.join(clock_lines)+'\nprintf "clock=%s\n" "$PUBLIC_ROLLBACK_START"\n'
            result=subprocess.run(['bash','-c',script],env=env,capture_output=True,timeout=5)
            assert result.returncode==(7 if fails else 0) and result.stdout==(b'' if fails else b'clock=123.5\n')
            count+=1
    jobs=('sdd_s1','fsquint_s1','sdd_rollback','fsquint_rollback');bodies=[]
    for key in jobs:
        body=prepare.split('  execution_pair_'+key+':\n',1)[1].split('\n  execution_pair_',1)[0]
        assert 'needs: execution_pair_installed' in body and "needs.execution_pair_installed.result == 'success'" in body
        assert 'runs-on: ubuntu-24.04' in body and 'timeout-minutes: 15' in body
        assert '      contents: read\n      actions: read\n' in body
        assert 'DOTNET_INSTALL_DIR: ${{ runner.temp }}/proc6-dotnet' not in body
        sdk_bind=body.split('      - name: Bind private SDK installation below runner temp\n',1)[1].split('      - name:',1)[0]
        assert body.index('Bind private SDK installation below runner temp')<body.index('Bind receiver identity and cold input roots')<body.index('uses: actions/setup-dotnet@')
        assert 'printf \'DOTNET_INSTALL_DIR=%s/proc6-dotnet\\n\' "$RUNNER_TEMP" >> "$GITHUB_ENV"' in sdk_bind
        sdk_script=sdk_bind.split('        run: |\n',1)[1]
        sdk_script='\n'.join(line[10:] for line in sdk_script.splitlines())
        for missing in (False,True):
            with tempfile.TemporaryDirectory(prefix='proc6-sdk-binding-test-') as temporary:
                root=Path(temporary);envfile=root/'env';runner=root/'runner temp';runner.mkdir()
                env={**os.environ,'RUNNER_TEMP':'' if missing else str(runner),'GITHUB_ENV':str(envfile)}
                result=subprocess.run(['bash','-c',sdk_script],env=env,capture_output=True,timeout=5)
                assert (result.returncode==0)==(not missing)
                assert envfile.exists()==(not missing)
                if not missing:assert envfile.read_text()=='DOTNET_INSTALL_DIR='+str(runner)+'/proc6-dotnet\n'
                count+=1
        assert 'persist-credentials: false' in body and 'fetch-depth: 1' in body
        assert 'artifact-ids: ${{ needs.execution_pair_installed.outputs.proof_artifact_id }}' in body
        assert '-p:DisableImplicitLibraryPacksFolder=true' in body
        assert body.index('consumer_sdk_core_payload(len(raw)')<body.index('NuGet.Config')<body.index('\"$DOTNET_HOST_PATH\" restore ')
        assert "if os.environ['CONSUMER_CASE']=='fsquint-s1':E.SubElement(one,'package',pattern='FSharp.Core')" in body
        assert 'consumer_lock_schema(doc)' in body and "'schemaVersion':doc['version']" in body
        assert "reference_expected['FSharp.Core']=sdk_core" in body and "'sdkBundledInput':" in body
        assert '"${remaining}s" "$DOTNET_HOST_PATH"' in body
        assert "'hostBytesObservedOnly':True" in body and "'hostMatchesHistoricalProfileClaim':False" in body
        signature_step=body.split('      - name: Verify both fresh public rollback signatures before any restore',1)[1].split('      - name:',1)[0]
        assert "if: env.CONSUMER_CASE == 'fsquint-rollback'" in signature_step and 'set -euo pipefail' in signature_step
        assert 'nuget verify' in signature_step and '--all 2>&1 | head -c 65537' in signature_step and 'test -s' in signature_step
        assert body.index('Verify both fresh public rollback signatures')<body.index('"$DOTNET_HOST_PATH" restore ')
        assert "else None,'historicalOriginalArchiveSha256'" in body and "'originalArtifactReacquired':False" in body
        assert body.index('source-lock-pins.json')<body.index('uses: actions/setup-dotnet@')
        assert "'cacheSha512ConventionAsserted':False" in body and "'normalizedHashExpectedOrigin':normalized_origin" in body
        assert "'actualLocks':lock_records" in body and "'actualAssetHashes':asset_records" in body
        assert "raise ValueError('immutable receiver source lock changed')" in body
        assert "raise ValueError('actual restore lock missing or unsafe')" in body
        assert 'matrix:' not in body and 'id-token:' not in body and 'packages:' not in body
        assert 'hardContainment\':False' in body and "'outerCustodyQualified':False" in body
        assert 'eng/test-callable-cli-installed-harness.py' not in body and 'dotnet pack' not in body and 'nuget push' not in body
        bodies.append(body.split('    steps:\n',1)[1].strip())
    assert len(set(bodies))==1,'fixed receiver job logic drift'
    assert 'RECEIVER_SOURCE: c246d2cf12eeb04d6894bab7528c7929bb87a762' in prepare
    assert 'RECEIVER_SOURCE: 34eeca981c136144ced73a3f98b5ce04218e89c7' in prepare
    assert 'REFRESH' not in prepare
    print('EXECUTION_PAIR_CONSUMER_SOURCE_ONLY_CONTROLS passed cases='+str(count)+' fixed-fresh-jobs=4 native-effects=0')

if __name__=='__main__':main()
