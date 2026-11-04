#!/usr/bin/env python3
"""Run SDK publisher controls with fresh, hash-verified immutable test inputs.

The maintained compiler-and-tests gate calls this runner. It never publishes,
installs tools or supplies credentials to tests. Local --input-root is an offline
fixture source only; every file is still checked against the maintained pins.
"""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.request

ROOT=Path(__file__).resolve().parent.parent
spec=importlib.util.spec_from_file_location('sdk_runner_primitives',ROOT/'eng/execution_pair_forward.py')
F=importlib.util.module_from_spec(spec);spec.loader.exec_module(F)


def required_inputs():
    import yaml
    admission=F.strict_json((ROOT/'eng/ordinary-sdk-profile-admission.json').read_bytes())
    pin=F.validate_shared_helper(admission['sharedHelper'])
    rows=[dict(path=p,repository=pin['repository'],commit=pin['commit'],sha256=h) for p,h in pin['files'].items()]
    jobs=yaml.safe_load((ROOT/'.github/workflows/callable-cli-release-publish.yml').read_bytes())['jobs']
    locks=[]
    for name in ('sdk_profile_packages','sdk_profile_finalize'):
        steps=[s for s in jobs[name]['steps'] if s.get('name')=='Acquire org publication writer lock']
        F.require(len(steps)==1,'SDK writer canonical lock step absent')
        env=steps[0]['env'];commit=F.hexid(env['LOCK_COMMIT'],40,'publication lock commit');digest=F.hexid(env['LOCK_SCRIPT_SHA256'],64,'publication lock digest')
        locks.append(dict(path='scripts/publication_writer_lock.py',repository='FS-GG/.github',commit=commit,sha256=digest))
    F.require(locks[0]==locks[1],'SDK writers use different lock pins')
    rows.append(locks[0])
    F.require(len(rows)==8 and len({r['path'] for r in rows})==8,'SDK control input population differs')
    return rows


def acquire(row,input_root=None,opener=None):
    if input_root is not None:
        path=Path(input_root)/row['path']
        F.require(path.is_file() and not path.is_symlink(),'immutable control input missing/unsafe: '+row['path'])
        with path.open('rb') as stream:return stream.read(1048577)
    url='https://raw.githubusercontent.com/'+row['repository']+'/'+row['commit']+'/'+row['path']
    request=urllib.request.Request(url,headers={'User-Agent':'fsgg-sdk-source-controls'})
    open_request=opener or urllib.request.build_opener(urllib.request.ProxyHandler({})).open
    with open_request(request,timeout=15) as response:
        F.require(response.status==200,'immutable control input unavailable: '+row['path'])
        return response.read(1048577)


def provision(destination,rows,*,input_root=None,fetch=acquire):
    destination=Path(destination);destination.mkdir(mode=0o700,parents=True,exist_ok=False)
    reports=[];errors=[]
    for row in rows:
        try:
            raw=fetch(row,input_root)
            F.require(0<len(raw)<=1048576 and hashlib.sha256(raw).hexdigest()==row['sha256'],'immutable control input digest/size differs: '+row['path'])
            path=destination/row['path'];path.parent.mkdir(mode=0o700,parents=True,exist_ok=True);path.write_bytes(raw)
            reports.append({**row,'bytes':len(raw)})
        except Exception as error:
            errors.append(dict(path=row['path'],cause=str(error) if isinstance(error,F.Refused) else type(error).__name__))
    return reports,errors


def test_environment(workdir,helper):
    workdir=Path(workdir)
    for name in ('home','tmp'):(workdir/name).mkdir(mode=0o700,exist_ok=True)
    return dict(PATH=os.environ.get('PATH','/usr/bin:/bin'),HOME=str(workdir/'home'),TMPDIR=str(workdir/'tmp'),
                PYTHONDONTWRITEBYTECODE='1',FSGG_SHARED_HELPER_ROOT=str(helper))


def run(output,*,input_root=None):
    F.require(not input_root or not os.environ.get('GITHUB_ACTIONS'),'hosted control-input override is refused')
    output=Path(output).resolve();output.mkdir(mode=0o700,parents=True,exist_ok=False)
    result={'schema':'fsgg.coordination.ordinary-sdk-source-controls/1','status':'preparing','testsLaunched':False,'nativeEffects':False,'inputs':[]}
    started=time.monotonic()
    try:
        rows=required_inputs()
        result['inputs'],errors=provision(output/'helpers',rows,input_root=input_root)
        result['preparationErrors']=errors
        F.require(not errors,'input preparation refused: '+json.dumps(errors,sort_keys=True))
        clean_env=test_environment(output,output/'helpers')
        with (output/'runner-controls.stdout').open('wb') as stdout,(output/'runner-controls.stderr').open('wb') as stderr:
            runner_checks=subprocess.run([sys.executable,'-I','-B',str(ROOT/'tests/portable-workspace/release/test_ordinary_sdk_publisher_runner.py')],
                                         cwd=ROOT,env=clean_env,stdout=stdout,stderr=stderr,timeout=30,check=False)
        runner_text=(output/'runner-controls.stderr').read_text()
        runner_counts=re.findall(r'^Ran ([0-9]+) tests? in ',runner_text,re.M)
        F.require(runner_checks.returncode==0 and len(runner_counts)==1 and int(runner_counts[0])>=7 and re.search(r'^OK$',runner_text,re.M) is not None,'SDK runner/provisioning assertions failed or census absent')
        result['runnerControlTests']=int(runner_counts[0])
        result['testsLaunched']=True
        with (output/'controls.stdout').open('wb') as stdout,(output/'controls.stderr').open('wb') as stderr:
            child=subprocess.run([sys.executable,'-I','-B',str(ROOT/'tests/portable-workspace/release/test_ordinary_sdk_publisher.py')],
                                 cwd=ROOT,env=clean_env,stdout=stdout,stderr=stderr,timeout=120,check=False)
        F.require((output/'controls.stdout').stat().st_size<=1048576 and (output/'controls.stderr').stat().st_size<=1048576,'control output exceeds report bound')
        text=(output/'controls.stderr').read_text()
        counts=re.findall(r'^Ran ([0-9]+) tests? in ',text,re.M)
        F.require(child.returncode==0 and len(counts)==1 and int(counts[0])>=32 and re.search(r'^OK$',text,re.M) is not None,'SDK publisher assertions failed or test census absent')
        result.update(status='passed',tests=int(counts[0]),exit=child.returncode)
    except Exception as error:
        result.update(status='refused',cause=str(error) if isinstance(error,F.Refused) else type(error).__name__)
    finally:
        result['elapsedSeconds']=round(time.monotonic()-started,3)
        (output/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    print('SDK_PUBLISHER_SOURCE_CONTROLS '+json.dumps(result,sort_keys=True))
    return 0 if result['status']=='passed' else 3


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',required=True,type=Path);p.add_argument('--input-root',type=Path)
    args=p.parse_args()
    try:sys.exit(run(args.output,input_root=args.input_root))
    except Exception as error:
        print('SDK_PUBLISHER_SOURCE_CONTROLS_REFUSED '+(str(error) if isinstance(error,F.Refused) else type(error).__name__),file=sys.stderr);sys.exit(3)
