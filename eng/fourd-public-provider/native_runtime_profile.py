#!/usr/bin/env python3
"""Stage/verify the fixed consumer toolchain; never execute its binaries or producer."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tarfile
import time
import urllib.request

PROFILE_PATH = Path(__file__).with_name('native-runtime-profile.json')
sys.path.insert(0,str(Path(__file__).resolve().parent))
from native_bootstrap import RUNTIME_NAMES, ADAPTERS
RECEIPT = 'fsgg-native-runtime-install.json'
BASELINE = Path('/opt/fsgg/toolchains/dotnet-10.0.401')
MAX_FILES = 16000
MAX_BYTES = 2 * 1024**3

class Refused(RuntimeError): pass

def need(condition, message):
    if not condition: raise Refused(message)

def alive(deadline):
    need(time.monotonic() < deadline, 'original runtime preparation deadline exhausted')

def canonical(value):
    return (json.dumps(value,sort_keys=True,separators=(',',':'))+'\n').encode()

def closed_json(raw):
    def pairs(items):
        result={}
        for key,value in items:
            need(key not in result,'duplicate publication property');result[key]=value
        return result
    need(0<len(raw.encode())<=65536,'bounded publication input required')
    return json.loads(raw,object_pairs_hook=pairs)

def publication_fields(raw, p):
    value=closed_json(raw)
    need(isinstance(value,dict) and set(value)=={'producer','publication','runtime','adoptionSelected'}
         and value['adoptionSelected'] is False,'closed published producer result required')
    producer,release=value['producer'],value['publication']
    need(isinstance(producer,dict) and isinstance(release,dict) and set(producer)=={'sourceSha','sourceTree','bytes','sha256','bindingSha256','provenanceSha256','files','adapterFiles'}
         and set(release)=={'releaseObserved','publicationObserved','releaseId','assetId','authorId','tag','name','bytes','sha256'}
         and release['releaseObserved'] is True and release['publicationObserved'] is True,
         'published producer identity required')
    need(value['runtime']=={'sdkVersion':'10.0.400','runtimeVersion':p['runtimeVersion'],
                           'hostSha256':p['files']['dotnet']['sha256']}, 'published runtime profile differs')
    need(release['bytes']==producer['bytes'] and release['sha256']==producer['sha256']
         and all(type(release[k]) is int and release[k]>0 for k in ('releaseId','assetId','authorId')),
         'published release/asset facts differ')
    need(isinstance(producer['files'],dict) and isinstance(producer['adapterFiles'],dict)
         and set(producer['files'])==RUNTIME_NAMES and set(producer['adapterFiles'])==ADAPTERS
         and all(isinstance(v,str) and re.fullmatch('[0-9a-f]{64}',v) for v in [producer['sha256'],producer['bindingSha256'],producer['provenanceSha256'],
                    *producer['files'].values(),*producer['adapterFiles'].values()])
         and all(isinstance(producer[k],str) and re.fullmatch('[0-9a-f]{40}',producer[k]) for k in ('sourceSha','sourceTree'))
         and type(producer['bytes']) is int and 0<producer['bytes']<=64*1024**2,
         'published runtime/adapter closure differs')
    need(release['tag']=='fourd-native-producer-'+producer['sourceSha']
         and release['name']=='fourd-native-producer-linux-x64.tar','published immutable locator differs')
    return producer,release

def adoption_from_publication(raw, facts, p):
    producer,release=publication_fields(raw,p)
    need(facts['hostSha256']==p['files']['dotnet']['sha256'],'actual runtime profile differs')
    # This construction grants no success: bootstrap independently reads public
    # custody/bytes, then F# joins its real executing closure and loaded runtime.
    return dict(schema='fsgg.fourd.native-producer-adoption/1',**producer,
                **{k:release[k] for k in ('releaseId','assetId','authorId','tag','name')},hostPath=facts['hostPath'])

def profile():
    raw=PROFILE_PATH.read_bytes();p=json.loads(raw)
    need(set(p)=={'schema','sdkVersions','runtimeVersions','runtimeVersion','archives','files'}
         and p['schema']=='fsgg.fourd.native-runtime-profile/1'
         and p['sdkVersions']==['10.0.400','10.0.401'] and p['runtimeVersion']=='10.0.13'
         and p['runtimeVersions']==['10.0.11','10.0.12','10.0.13'], 'closed fixed runtime profile differs')
    need(len(p['archives'])==3, 'three fixed archives required')
    for a in p['archives']:
        need(set(a)=={'name','url','bytes','sha512','files','directories','unpackedBytes'}
             and re.fullmatch(r'dotnet-(sdk|runtime)-10\.0\.[0-9]+-linux-x64\.tar\.gz',a['name'])
             and a['url'].startswith('https://builds.dotnet.microsoft.com/dotnet/')
             and a['url'].endswith('/'+a['name']) and re.fullmatch('[0-9a-f]{128}',a['sha512'])
             and type(a['bytes']) is int and 0<a['bytes']<=300*1024**2, 'fixed official archive identity')
    return p,hashlib.sha256(raw).hexdigest()

def regular(path, maximum, deadline):
    alive(deadline)
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    with os.fdopen(fd,'rb') as stream:
        before=os.fstat(stream.fileno())
        need(stat.S_ISREG(before.st_mode) and 0<=before.st_size<=maximum, 'bounded regular runtime file required')
        digest=hashlib.sha256();size=0
        for block in iter(lambda:stream.read(1024*1024),b''):
            alive(deadline);size+=len(block);need(size<=maximum,'runtime file size bound');digest.update(block)
        after=os.fstat(stream.fileno());alive(deadline)
        need((before.st_dev,before.st_ino,before.st_size,before.st_mtime_ns)==
             (after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns) and size==before.st_size,
             'runtime file changed during read')
    return {'bytes':size,'sha256':digest.hexdigest()}

def root_path(path):
    need(path.is_absolute() and '\n' not in str(path) and '\r' not in str(path), 'absolute runtime prefix required')
    need(path.resolve()==path and path!=BASELINE and BASELINE not in path.parents,
         'runtime prefix aliases or immutable c068 baseline refused')
    return path

def archive_bytes(path, a, deadline):
    alive(deadline);info=path.lstat()
    need(stat.S_ISREG(info.st_mode) and info.st_size==a['bytes'], 'archive regular size differs')
    digest=hashlib.sha512()
    with path.open('rb') as source:
        for block in iter(lambda:source.read(1024*1024),b''):alive(deadline);digest.update(block)
    alive(deadline);need(digest.hexdigest()==a['sha512'],'official archive SHA512 differs')

def acquire(directory, p, deadline):
    root_path(directory)
    directory.mkdir(mode=0o700)
    for a in p['archives']:
        alive(deadline);path=directory/a['name'];size=0
        request=urllib.request.Request(a['url'],headers={'User-Agent':'fsgg-fourd-runtime-profile/1'})
        with urllib.request.urlopen(request,timeout=min(15,max(.001,deadline-time.monotonic()))) as response, path.open('xb') as output:
            alive(deadline);need(response.status==200,'official archive HTTP status differs')
            while True:
                block=response.read(1024*1024);alive(deadline)
                if not block:break
                size+=len(block);need(size<=a['bytes'],'official archive byte bound');output.write(block)
        need(size==a['bytes'],'official archive truncated');archive_bytes(path,a,deadline)

def unpack(directory, output, p, deadline):
    root_path(output)
    # Verify all frozen inputs before creating the target. No shared toolchain mutation.
    for a in p['archives']:archive_bytes(directory/a['name'],a,deadline)
    output.mkdir(mode=0o700);inventory={};total=0
    for a in p['archives']:
        seen=set();files=directories=unpacked=0
        with tarfile.open(directory/a['name'],mode='r:gz') as archive:
            for member in archive:
                alive(deadline);path=PurePosixPath(member.name)
                need(not path.is_absolute() and '..' not in path.parts and '\\' not in member.name,'archive path refused')
                name=str(path)
                if name=='.':need(member.isdir(),'archive root type refused');directories+=1;continue
                need(name not in seen and len(name.encode())<=4096,'duplicate/long archive member');seen.add(name)
                need(member.isdir() or member.isfile(),'archive links or special files refused')
                target=output/name
                if member.isdir():
                    directories+=1;target.mkdir(mode=0o700,parents=True,exist_ok=True);continue
                files+=1;unpacked+=member.size;total+=member.size
                need(files<=6000 and len(inventory)<MAX_FILES and total<=MAX_BYTES
                     and 0<=member.size<=64*1024**2,'archive extraction bound')
                target.parent.mkdir(mode=0o700,parents=True,exist_ok=True)
                if target.exists() or target.is_symlink():
                    info=target.lstat();need(name in inventory and stat.S_ISREG(info.st_mode)
                                           and info.st_nlink==1 and info.st_uid==os.getuid(),'archive target identity refused')
                fd=os.open(target,os.O_WRONLY|os.O_CREAT|os.O_TRUNC|os.O_NOFOLLOW,0o600)
                digest=hashlib.sha256();remaining=member.size
                with os.fdopen(fd,'wb') as dest,archive.extractfile(member) as source:
                    while remaining:
                        block=source.read(min(1024*1024,remaining));alive(deadline)
                        need(bool(block),'archive member truncated');remaining-=len(block);digest.update(block);dest.write(block)
                mode=0o700 if member.mode&0o111 else 0o600;target.chmod(mode)
                inventory[name]={'bytes':member.size,'sha256':digest.hexdigest(),'mode':mode}
        need((files,directories,unpacked)==(a['files'],a['directories'],a['unpackedBytes']), 'official archive inventory differs')
    return inventory

def verify(root, p, profile_sha, deadline):
    root_path(root);info=root.lstat()
    need(stat.S_ISDIR(info.st_mode) and info.st_uid==os.getuid() and stat.S_IMODE(info.st_mode)==0o700,'private owned runtime prefix required')
    receipt_path=root/RECEIPT;regular(receipt_path,4*1024**2,deadline);receipt=json.loads(receipt_path.read_bytes())
    need(set(receipt)=={'schema','profileSha256','archives','files'} and receipt['schema']=='fsgg.fourd.native-runtime-install/1'
         and receipt['profileSha256']==profile_sha and receipt['archives']==p['archives'],'runtime provenance differs')
    files=receipt['files'];need(isinstance(files,dict) and 1<=len(files)<=MAX_FILES,'runtime file census bound')
    seen=set()
    for directory,dirs,names in os.walk(root,followlinks=False):
        for name in dirs:need(not (Path(directory)/name).is_symlink(),'runtime directory link refused')
        for name in names:
            path=Path(directory)/name;relative=path.relative_to(root).as_posix()
            if relative==RECEIPT:continue
            need(relative in files,'unexpected runtime file');seen.add(relative)
            observed=regular(path,64*1024**2,deadline)
            need(observed=={k:files[relative][k] for k in ('bytes','sha256')}
                 and stat.S_IMODE(path.lstat().st_mode)==files[relative]['mode'],'runtime installed file differs')
    need(seen==set(files),'runtime installed file missing')
    for name,expected in p['files'].items():need(regular(root/name,64*1024**2,deadline)==expected,'pinned compiler/host/runtime bytes differ')
    need(sorted(x.name for x in (root/'sdk').iterdir())==p['sdkVersions']
         and sorted(x.name for x in (root/'shared/Microsoft.NETCore.App').iterdir())==p['runtimeVersions']
         and sorted(x.name for x in (root/'host/fxr').iterdir())==p['runtimeVersions'],'SDK/runtime directory closure differs')
    alive(deadline)
    return {'profileSha256':profile_sha,'hostPath':str(root/'dotnet'),'hostSha256':p['files']['dotnet']['sha256'],
            'sdkVersions':p['sdkVersions'],'runtimeVersion':p['runtimeVersion'],'filesVerified':len(files),
            'producerExecuted':False,'effectiveRuntimeJoinVerified':False,'nativeOperationSelected':False}

def stage(archives, output, p, profile_sha, deadline, *, download=True):
    root_path(output);need(not output.exists() and not output.is_symlink(),'runtime prefix must be fresh; no overwrite/retry')
    if download:acquire(archives,p,deadline)
    inventory=unpack(archives,output,p,deadline)
    with (output/RECEIPT).open('xb') as stream:stream.write(canonical({'schema':'fsgg.fourd.native-runtime-install/1','profileSha256':profile_sha,'archives':p['archives'],'files':inventory}))
    (output/RECEIPT).chmod(0o600)
    return verify(output,p,profile_sha,deadline)

def main():
    parser=argparse.ArgumentParser();sub=parser.add_subparsers(dest='command',required=True)
    build=sub.add_parser('stage');build.add_argument('--archives',type=Path,required=True);build.add_argument('--output',type=Path,required=True);build.add_argument('--acquired',action='store_true');build.add_argument('--budget-seconds',type=int,default=240);build.add_argument('--deadline',type=float)
    build.add_argument('--publication-env');build.add_argument('--adoption-output',type=Path);build.add_argument('--github-env',type=Path);build.add_argument('--github-path',type=Path)
    check=sub.add_parser('verify');check.add_argument('--root',type=Path);check.add_argument('--adoption-env');check.add_argument('--deadline',type=float,required=True);check.add_argument('--github-env',type=Path);check.add_argument('--github-path',type=Path)
    args=parser.parse_args();p,digest=profile()
    if args.command=='stage':
        need(1<=args.budget_seconds<=300,'finite staging budget required')
        deadline=args.deadline if args.deadline is not None else time.monotonic()+args.budget_seconds
        need(time.monotonic()<deadline<=time.monotonic()+300,'original bounded staging deadline required')
        if args.publication_env or args.adoption_output:
            need(args.publication_env and args.adoption_output,'publication and adoption output required together')
            publication_fields(os.environ.get(args.publication_env,''),p)  # Before network/extraction cost.
        result=stage(args.archives,args.output,p,digest,deadline,download=not args.acquired);root=args.output
        if args.publication_env:
            adoption=adoption_from_publication(os.environ.get(args.publication_env,''),result,p)
            with args.adoption_output.open('xb') as output:output.write(canonical(adoption))
            args.adoption_output.chmod(0o600)
        alive(deadline)
    else:
        need(time.monotonic()<args.deadline<=time.monotonic()+90,'original bounded verification deadline required')
        root=args.root
        if args.adoption_env:
            raw=os.environ.get(args.adoption_env,'');need(0<len(raw.encode())<=65536,'bounded adoption input required');adoption=json.loads(raw);host=Path(adoption['hostPath']);need(host.is_absolute() and host.name=='dotnet','actual adoption host path required');root=host.parent
        need(root is not None,'actual prepared runtime root required');result=verify(root,p,digest,args.deadline)
        deadline=args.deadline;alive(deadline)
    if args.github_env or args.github_path:
        need(args.github_env and args.github_path,'both GitHub output files required')
        with args.github_env.open('a') as output:output.write('DOTNET_ROOT='+str(root)+'\nDOTNET_INSTALL_DIR='+str(root)+'\n')
        with args.github_path.open('a') as output:output.write(str(root)+'\n')
    alive(deadline)
    print(json.dumps(result,sort_keys=True));return 0

if __name__=='__main__':
    try:raise SystemExit(main())
    except (Refused,OSError,ValueError,tarfile.TarError) as error:print('refused: '+str(error),file=sys.stderr);raise SystemExit(2)
