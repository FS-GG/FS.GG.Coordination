#!/usr/bin/env python3
"""Fixed private AT factory-prefix preparation; no installer, desktop or arbitrary URI API.

The held workflow first authenticates its actual private run/job and protected sources.
This helper checks the selected original public artifact metadata against a fixed
root-owned read-only guest attachment and prepares declared archives as UID1001. Only the separately
admitted baked driver can capture the resulting pool or seal privileged factory code.
"""
import argparse
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import re
import signal
import stat
import time
import urllib.error
import urllib.request
import zipfile

STARTED=time.monotonic()
PRIVATE='FS-GG/FS.GG.Native.Qualification'
TEMPLATES='FS-GG/FS.GG.Templates'
PREFIX='tests/composition/fable-game/'
ARCHIVE_INPUT=Path('/mnt/fsgg-current-at-inputs/source-artifact.zip')


def need(value,message):
    if not value:raise ValueError('native-at-prefix: '+message)


def unique(rows):
    value={}
    for key,item in rows:
        need(key not in value,'duplicate JSON field');value[key]=item
    return value


def parse(raw):return json.loads(raw,object_pairs_hook=unique)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs):return None


class Budget:
    def __init__(self,deadline):
        need(math.isfinite(deadline),'finite original preparation deadline required');self.deadline=deadline
    def left(self):
        left=self.deadline-time.monotonic();need(left>0,'original preparation deadline exhausted');return left


def read_response(response,cap,budget):
    need(response.status==200,'actual HTTP200 required before digest')
    length=response.headers.get('Content-Length')
    need(length is None or (length.isdecimal() and int(length)<=cap),'declared HTTP body exceeds bound')
    output=bytearray()
    while True:
        budget.left();chunk=response.read(min(65536,cap-len(output)+1))
        if not chunk:break
        output.extend(chunk);need(len(output)<=cap,'HTTP body exceeded bound')
    budget.left();return bytes(output)


def api(path,budget,opener=None):
    need(path.startswith('repos/'+TEMPLATES+'/') and '..' not in path,'fixed public GitHub metadata path required')
    opener=opener or urllib.request.build_opener(NoRedirect(),urllib.request.ProxyHandler({}))
    # Public metadata is readable without repository permissions. The private
    # job token is never used to download a foreign repository's Actions ZIP.
    request=urllib.request.Request('https://api.github.com/'+path,headers={
        'Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'})
    try:response=opener.open(request,timeout=min(15,budget.left()))
    except urllib.error.HTTPError as error:
        error.close();raise
    with response:return read_response(response,1024**2,budget)


def read_attached_archive(plan,budget):
    """One fixed root-owned read-only guest attachment; no URI/path override."""
    path=ARCHIVE_INPUT;need(path.is_absolute(),'fixed attachment path required')
    fd=os.open('/',os.O_RDONLY|os.O_DIRECTORY)
    try:
        for part in path.parts[1:-1]:
            child=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=fd)
            os.close(fd);fd=child;info=os.fstat(fd)
            need(info.st_uid==0 and not info.st_mode&0o022,'attachment ancestor must be root/nonwritable')
        leaf=os.open(path.name,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)
        with os.fdopen(leaf,'rb') as stream:
            info=os.fstat(stream.fileno())
            need(stat.S_ISREG(info.st_mode) and info.st_uid==0 and info.st_nlink==1 and not info.st_mode&0o022 and
                 type(plan['artifactBytes']) is int and info.st_size==plan['artifactBytes']<=256*1024**2,
                 'exact bounded root attachment required')
            # Join the opened file's actual mount, rather than a guessed path prefix.
            def proc_text(name,cap):
                with Path(name).open('r') as source:text=source.read(cap+1)
                need(len(text)<=cap,'bounded actual mount metadata required');return text
            descriptor=proc_text('/proc/self/fdinfo/'+str(stream.fileno()),16384)
            ids=re.findall(r'(?m)^mnt_id:\s*([0-9]+)$',descriptor);need(len(ids)==1,'actual attachment mount identity absent')
            mounts=proc_text('/proc/self/mountinfo',512*1024).splitlines()
            need(len(mounts)<=4096 and all(len(line.split())>=10 for line in mounts),'bounded complete mount metadata required')
            selected=[line.split() for line in mounts if line.split()[0]==ids[0]]
            need(len(selected)==1 and selected[0][4]==str(path.parent) and 'ro' in selected[0][5].split(',') and
                 {'nodev','nosuid','noexec'}<=set(selected[0][5].split(',')),
                 'fixed source-bound attachment must be mounted read-only/nodev/nosuid/noexec')
            body=bytearray();digest=hashlib.sha256()
            while True:
                budget.left();chunk=stream.read(65536)
                if not chunk:break
                body.extend(chunk);digest.update(chunk);need(len(body)<=plan['artifactBytes'],'attachment body exceeded')
            after=os.fstat(stream.fileno())
            need(all(getattr(info,key)==getattr(after,key) for key in ('st_dev','st_ino','st_size','st_mtime_ns','st_ctime_ns')) and
                 len(body)==plan['artifactBytes'] and digest.hexdigest()==plan['artifactSha256'],'original attached artifact bytes changed/differ')
            budget.left();return bytes(body)
    finally:os.close(fd)


def native_artifact(plan,budget,request=api,read_archive=read_attached_archive):
    repo='repos/'+TEMPLATES
    run_raw=request(repo+'/actions/runs/'+str(plan['runId']),budget);run=parse(run_raw)
    need(run.get('id')==plan['runId'] and type(run.get('run_attempt')) is int and run['run_attempt']==1 and
         run.get('head_sha')==plan['source'] and run.get('path')==plan['workflowPath'] and
         run.get('event')=='pull_request' and run.get('status')=='completed' and run.get('conclusion')=='success' and
         run.get('repository',{}).get('id')==1281961814,'original public source run differs')
    jobs_raw=request(repo+'/actions/runs/'+str(plan['runId'])+'/attempts/1/jobs?per_page=100',budget);jobs=parse(jobs_raw)
    need(type(jobs.get('total_count')) is int and type(jobs.get('jobs')) is list and jobs['total_count']==len(jobs['jobs'])<=100,
         'original source jobs population unavailable')
    selected=[row for row in jobs['jobs'] if row.get('name')=='External reference workflow static controls']
    need(len(selected)==1 and selected[0].get('status')=='completed' and selected[0].get('conclusion')=='success' and
         selected[0].get('head_sha')==plan['source'] and selected[0].get('run_id')==plan['runId'] and
         type(selected[0].get('id')) is int and selected[0]['id']>0,'original successful source job differs')
    artifact_raw=request(repo+'/actions/artifacts/'+str(plan['artifactId']),budget);artifact=parse(artifact_raw)
    origin=artifact.get('workflow_run',{})
    need(artifact.get('id')==plan['artifactId'] and artifact.get('expired') is False and
         artifact.get('size_in_bytes')==plan['artifactBytes'] and artifact.get('digest')=='sha256:'+plan['artifactSha256'] and
         artifact.get('name')=='noble-no-install-source-'+str(plan['runId'])+'-1' and
         origin.get('id')==plan['runId'] and origin.get('head_sha')==plan['source'] and origin.get('repository_id')==1281961814,
         'native original source artifact identity differs')
    raw=read_archive(plan,budget)
    need(len(raw)==plan['artifactBytes'] and hashlib.sha256(raw).hexdigest()==plan['artifactSha256'],'original artifact body differs')
    return raw,{'run':run,'job':selected[0],'artifact':artifact,
                'nativeRunBodySha256':hashlib.sha256(run_raw).hexdigest(),'nativeJobsBodySha256':hashlib.sha256(jobs_raw).hexdigest(),
                'nativeArtifactBodySha256':hashlib.sha256(artifact_raw).hexdigest()}


def originals(raw,out,budget):
    needed={'originals/'+name:'noble-source-transaction/'+name for name in
            ('transaction.json','apt-config.log','apt-update.log','apt-simulate.log','status')}
    for suite in ('noble','noble-updates','noble-security'):
        names=[suite+'.InRelease']+[suite+'-'+component+'-'+kind for component in ('main','universe')
               for kind in ('binary-amd64-Packages.xz','source-Sources.xz')]
        needed.update({'metadata/'+name:'noble-source-transaction/metadata/'+name for name in names})
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        entries=archive.infolist();need(0<len(entries)<=4096,'bounded source ZIP population required')
        seen={};expanded=0
        for entry in entries:
            need(entry.filename not in seen and not entry.flag_bits&1 and
                 stat.S_IFMT(entry.external_attr>>16) in (0,stat.S_IFREG) and not entry.is_dir() and
                 not entry.filename.startswith('/') and all(p not in ('','.','..') for p in entry.filename.split('/')),
                 'original source ZIP member shape differs')
            seen[entry.filename]=entry;expanded+=entry.file_size
            need(entry.file_size<=128*1024**2 and expanded<=512*1024**2,'original ZIP expansion exceeded')
        need(set(needed.values())<=set(seen),'original artifact lacks self-contained signed inputs; no reconstruction')
        for target,source in needed.items():
            budget.left();entry=seen[source]
            body=archive.read(entry);need(len(body)==entry.file_size,'original ZIP member body differs')
            path=out/target;path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
            with path.open('xb') as stream:stream.write(body)
            budget.left()


def prepare(templates,startup):
    need(os.geteuid()==1001,'private preparation must run as runner UID1001')
    need(os.environ.get('GITHUB_REPOSITORY')==PRIVATE and os.environ.get('GITHUB_JOB')=='current-public-at' and
         os.environ.get('GITHUB_RUN_ATTEMPT')=='1','actual private AT context differs')
    overall=float(os.environ['FSGG_AT_DEADLINE_MONOTONIC'])
    need(math.isfinite(overall) and STARTED<overall<=STARTED+2100,'finite original overall clock required')
    deadline=min(STARTED+240,overall-15);budget=Budget(deadline)
    root=Path('/etc/fsgg-qualification-runner');binding_path=root/'instance-binding.json';info=binding_path.lstat()
    need(stat.S_ISREG(info.st_mode) and info.st_uid==0 and stat.S_IMODE(info.st_mode)==0o644,'root AT binding required')
    binding=parse(binding_path.read_bytes());software=binding['atSoftwareContract']
    need(binding.get('schema')=='fsgg.dedicated-qualification-instance/2' and software.get('stage')=='facility-prefix',
         'only selected ATv2 factory prefix supported')
    value=parse(startup.read_bytes())
    need(value.get('disposition')=='accepted' and value.get('bootId')==Path('/proc/sys/kernel/random/boot_id').read_text().strip() and
         value.get('callerSource')==os.environ['GITHUB_SHA'] and
         value.get('runId')==int(os.environ['GITHUB_RUN_ID']) and value.get('attempt')==1 and
         value.get('rootBindingSha256')==hashlib.sha256(binding_path.read_bytes()).hexdigest(),
         'original authenticated private startup differs')
    need(hashlib.sha256(json.dumps(software,sort_keys=True,separators=(',',':')).encode()).hexdigest()==binding['atSoftwareContractSha256'],
         'root software body differs')
    # The workflow authenticates these exact protected checkouts before this import.
    spec=importlib.util.spec_from_file_location('selected_at_transaction',templates/PREFIX/'current-reference-noble-transaction.py')
    T=importlib.util.module_from_spec(spec);spec.loader.exec_module(T)
    T.runtime_guard()
    need(not any(os.environ.get(key) for key in ('GH_TOKEN','GITHUB_TOKEN')),'private token must not enter public input acquisition')
    artifact,custody=native_artifact(software['frozenPlan'],budget)
    base=Path(os.environ['RUNNER_TEMP']).resolve();capture=base/'native-at-originals';out=base/'native-at-pool'
    need(not capture.exists() and not out.exists() and not capture.is_symlink() and not out.is_symlink(),'fresh private input domains required; no retry')
    capture.mkdir(mode=0o700);originals(artifact,capture,budget)
    (capture/'original-custody.json').write_text(json.dumps(custody,sort_keys=True)+'\n');budget.left()
    # Acquisition/signature children inherit no credential or ambient loader state.
    for key in ('GH_TOKEN','GITHUB_TOKEN','LD_PRELOAD','LD_LIBRARY_PATH','PYTHONPATH','PYTHONHOME'):os.environ.pop(key,None)
    roots=T.request((templates/PREFIX/'fixtures/noble-source-prefix-request.json').read_bytes())
    remaining=T.A.Budget(0);remaining.deadline=deadline;remaining.remaining()
    result=T.prepare_frozen_inputs(capture/'originals',software['frozenPlan']['transactionSha256'],roots,
        capture/'metadata',out,Path('/usr/share/keyrings/ubuntu-archive-keyring.gpg'),budget=remaining)
    budget.left()
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--templates',type=Path,required=True)
    parser.add_argument('--startup',type=Path,required=True);args=parser.parse_args()
    def expired(number,frame):raise TimeoutError('original private preparation clock expired/interrupted')
    for number in (signal.SIGALRM,signal.SIGTERM,signal.SIGINT):signal.signal(number,expired)
    overall=float(os.environ['FSGG_AT_DEADLINE_MONOTONIC'])
    need(math.isfinite(overall) and STARTED<overall<=STARTED+2100,'finite original overall clock required')
    signal.setitimer(signal.ITIMER_REAL,max(.001,min(STARTED+240,overall-15)-time.monotonic()))
    prepare(args.templates.resolve(),args.startup.resolve())


if __name__=='__main__':main()
