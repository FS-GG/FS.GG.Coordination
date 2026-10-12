#!/usr/bin/env python3
"""Run actual pair admission/archive/readback guards with inert good and bad inputs."""
from __future__ import annotations
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
IDS = ('FS.GG.Execution.Contracts', 'FS.GG.Execution.Processes')

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, separators=(',', ':')))

def heredoc(marker):
    text = (ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
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

def main():
    workflow=(ROOT/'.github/workflows/callable-cli-release-publish.yml').read_text()
    prepare=(ROOT/'.github/workflows/callable-cli-release-prepare.yml').read_text()
    assert "if: inputs.preparation_mode == 'portable'" in prepare
    assert "if: inputs.preparation_mode == 'cli-only'" in prepare
    assert "if: inputs.preparation_mode == 'execution-pair'" in prepare
    for job in ('preflight','publish','cli_successor'):
        gate=workflow.split('  '+job+':\n',1)[1].splitlines()
        gate=next(line for line in gate if line.startswith('    if:'))
        assert '!inputs.execution_pair' in gate
    job=workflow.split('  execution_pair:\n',1)[1]
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
    guard=heredoc('PY_PAIR_ARCHIVE')
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
                    try:exec(guard,{})
                    except (AssertionError,KeyError,TypeError):refused=True
            finally:os.chdir(old)
            assert refused==(mode!='good'),mode
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
    for mode in ('good','unprotected-component','inactive','bypass','deleted-signature-rule','foreign-ruleset','wrong-pin'):
        with tempfile.TemporaryDirectory(prefix='pair-protection-test-') as temporary:
            root=Path(temporary);readback=root/'readback';readback.mkdir()
            live=copy.deepcopy(policy); live['id']=4
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
            assert refused==(mode!='good'),mode
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
    print('EXECUTION_PAIR_RELEASE_INERT_CONTROLS passed admission=12 archive=7 readback=8 tag-protection=7 four-feed-cells=3 selectors=exclusive')

if __name__=='__main__':main()
