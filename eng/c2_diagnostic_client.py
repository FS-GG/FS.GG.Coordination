#!/usr/bin/env python3
"""Build/consume one source-pinned C2 diagnostic derivative; never canonical Quint."""
import argparse, base64, hashlib, io, json, os, pathlib, re, selectors, shutil, signal, stat, subprocess, tarfile, time, urllib.request, zipfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
INPUT=ROOT/'eng/c2-diagnostic-client-inputs.json'
REPO='FS-GG/FS.GG.Coordination'; REPO_ID=1346720714
BIN='quint-c2'; LIMIT=160*1024**2
FILES=('eng/c2_diagnostic_client.py','eng/c2-diagnostic-client-inputs.json','eng/vendor/quint-c2-diagnostic/compile-explanation.patch','eng/vendor/quint-c2-diagnostic/cmdExecutor.proto','eng/vendor/quint-c2-diagnostic/LICENSE','eng/tests/c2-diagnostic-client/production-cli-seam.cjs','eng/tests/c2-diagnostic-client/production-function-controls.cjs','eng/shared_process_owner.py','eng/tests/c2-diagnostic-client/test_client.py')
PRODUCERS={'.github/workflows/bootstrap-qualification.yml':'canonical-quint-prepare','.github/workflows/optimistic-parallel-validation.yml':'formal-prepare'}
def require(ok,message):
    if not ok: raise ValueError('C2 diagnostic client: '+message)
def digest(raw): return hashlib.sha256(raw).hexdigest()
def file_sha(p):
    fd=os.open(p,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    try:
        before=os.fstat(fd);require(stat.S_ISREG(before.st_mode) and 0<=before.st_size<=LIMIT,'bounded regular digest input')
        h=hashlib.sha256();count=0
        while True:
            b=os.read(fd,65536)
            if not b:break
            count+=len(b);require(count<=before.st_size,'digest input growth');h.update(b)
        after=os.fstat(fd);identity=lambda x:(x.st_dev,x.st_ino,x.st_size,x.st_mode,x.st_uid,x.st_gid,x.st_mtime_ns,x.st_ctime_ns)
        require(count==before.st_size and identity(before)==identity(after),'digest input changed')
        return h.hexdigest()
    finally:os.close(fd)
def document(raw):
    def unique(pairs):
        d={}
        for k,v in pairs:
            require(k not in d,'duplicate JSON property');d[k]=v
        return d
    return json.loads(raw,object_pairs_hook=unique)
def regular(p,cap):
    fd=os.open(p,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    try:
        before=os.fstat(fd);require(stat.S_ISREG(before.st_mode) and 0<before.st_size<=cap,'regular bounded file '+str(p))
        raw=os.read(fd,cap+1);after=os.fstat(fd)
        identity=lambda x:(x.st_dev,x.st_ino,x.st_size,x.st_mode,x.st_uid,x.st_gid,x.st_mtime_ns,x.st_ctime_ns)
        require(len(raw)==before.st_size and identity(before)==identity(after),'regular input changed')
        return raw
    finally:os.close(fd)
def inputs():
    v=document(regular(INPUT,8192));require(v['schema']=='fsgg.c2-diagnostic-client-inputs/1','input schema')
    require(set(v)=={'schema','identity','upstreamRevision','source','node','deno','denort','packageLockSha256','originalVerifySha256','patchedVerifySha256','patchSha256','nodeExecutableSha256','denoExecutableSha256','denortExecutableSha256','npmVersion','runtimeInventorySha256'},'input fields')
    require(v['identity']=='quint-0.32.0-fsgg-c2-diagnostic-1' and re.fullmatch('[a-f0-9]{40}',v['upstreamRevision']),'upstream/distinct identity')
    for field in ('packageLockSha256','originalVerifySha256','patchedVerifySha256','patchSha256','nodeExecutableSha256','denoExecutableSha256','denortExecutableSha256','runtimeInventorySha256'):require(re.fullmatch('[a-f0-9]{64}',v[field]),'declared input digest')
    for role in ('source','node','deno','denort'):
        x=v[role];require(set(x)=={'url','sha256','maxBytes','bytes'} and x['url'].startswith('https://') and re.fullmatch('[0-9a-f]{64}',x['sha256']) and type(x['maxBytes']) is int and type(x['bytes']) is int and 0<x['bytes']<=x['maxBytes']<=67108864,'input transport')
    require(file_sha(ROOT/'eng/vendor/quint-c2-diagnostic/compile-explanation.patch')==v['patchSha256'],'patch digest')
    return v
def recipe():return {p:file_sha(ROOT/p) for p in FILES}
def revision():return subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
def custody(env):
    source=revision();workflow=env.get('GITHUB_WORKFLOW_REF','').split('@')[0]
    prefix=REPO+'/';require(workflow.startswith(prefix),'workflow repository');workflow=workflow[len(prefix):]
    require(workflow in PRODUCERS and env.get('GITHUB_JOB')==PRODUCERS[workflow] and env.get('GITHUB_REPOSITORY')==REPO and env.get('GITHUB_RUN_ATTEMPT')=='1','producer selection')
    run=int(env['GITHUB_RUN_ID']);head=env['FSGG_C2_RUN_HEAD'];require(run>0 and re.fullmatch('[0-9a-f]{40}',source) and re.fullmatch('[0-9a-f]{40}',head),'source/run identity')
    return dict(repository=REPO,repositoryId=REPO_ID,run=run,attempt=1,source=source,runHead=head,workflow=workflow,producer=PRODUCERS[workflow])
def name(c):return 'c2-diagnostic-client-'+str(c['run'])+'-1-'+c['source']
def inventory(root):
    rows=[]
    for p in sorted((root/'node_modules').rglob('*')):
        if p.is_symlink():
            require(p.resolve().is_relative_to(root.resolve()),'dependency symlink escape');rows.append(dict(path=p.relative_to(root).as_posix(),kind='symlink',target=os.readlink(p)))
        elif p.is_file():rows.append(dict(path=p.relative_to(root).as_posix(),kind='file',bytes=p.stat().st_size,sha256=file_sha(p),mode=oct(stat.S_IMODE(p.stat().st_mode))))
        else:require(p.is_dir(),'dependency type')
    return digest((json.dumps(rows,sort_keys=True,separators=(',',':'))+'\n').encode())
def native_join(m,run,jobs,artifacts,expected_source,expected_run,artifact_id,artifact_digest):
    c=m['custody'];require(set(c)=={'repository','repositoryId','run','attempt','source','runHead','workflow','producer'},'custody fields')
    require(c['repository']==REPO and c['repositoryId']==REPO_ID and type(c['run']) is int and c['run']==expected_run and type(c['attempt']) is int and c['attempt']==1 and c['source']==expected_source and c['workflow'] in PRODUCERS and c['producer']==PRODUCERS[c['workflow']],'custody join')
    require(type(run['id']) is int and run['id']==expected_run and type(run['run_attempt']) is int and run['run_attempt']==1 and run['head_sha']==c['runHead'] and run['repository']['id']==REPO_ID and run['repository']['full_name']==REPO and run['path']==c['workflow'] and run['event'] in ('pull_request','push','merge_group','workflow_dispatch'),'current native run')
    require(type(jobs['total_count']) is int and jobs['total_count']==len(jobs['jobs']) and len(jobs['jobs'])<=100 and len({j['id'] for j in jobs['jobs']})==len(jobs['jobs']),'complete producer jobs')
    matches=[j for j in jobs['jobs'] if j['name']==c['producer']];require(len(matches)==1,'unique producer job');j=matches[0]
    require(j['run_id']==expected_run and type(j['run_attempt']) is int and j['run_attempt']==1 and j['head_sha']==c['runHead'] and j['status']=='completed' and j['conclusion']=='success' and type(j['id']) is int and j['id']>0,'producer job ownership/success')
    require(type(artifacts['total_count']) is int and artifacts['total_count']==len(artifacts['artifacts']) and len(artifacts['artifacts'])<=100,'complete artifacts')
    matches=[a for a in artifacts['artifacts'] if a['name']==name(c)];require(len(matches)==1,'unique current artifact');a=matches[0]
    require(type(a['id']) is int and a['id']>0 and a['expired'] is False and a['workflow_run']['id']==expected_run and a['workflow_run']['head_sha']==c['runHead'] and a['workflow_run']['repository_id']==REPO_ID and a['workflow_run']['head_repository_id']==REPO_ID and type(a['size_in_bytes']) is int and 0<a['size_in_bytes']<=LIMIT and re.fullmatch('sha256:[0-9a-f]{64}',a['digest']),'artifact native identity')
    require(a['id']==artifact_id and a['digest']=='sha256:'+artifact_digest,'producer upload output ownership')
    return a

def validate_manifest(m,source,run):
    v=inputs();require(set(m)=={'schema','identity','custody','inputsSha256','recipe','binary','preflight','buildRoot','license'},'manifest fields')
    require(m['schema']=='fsgg.c2-diagnostic-client-build/1' and m['identity']==v['identity'] and m['inputsSha256']==file_sha(INPUT) and m['recipe']==recipe(),'manifest source/recipe identity')
    license_path=ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE'
    require(m['license']==dict(path='LICENSE',bytes=license_path.stat().st_size,sha256=file_sha(license_path)),'license output identity')
    c=m['custody'];require(c['source']==source and type(c['run']) is int and c['run']==run and type(c['attempt']) is int and c['attempt']==1,'manifest current source/run')
    b=m['binary'];require(set(b)=={'path','bytes','sha256'} and b['path']==BIN and type(b['bytes']) is int and 0<b['bytes']<=LIMIT and re.fullmatch('[a-f0-9]{64}',b['sha256']),'binary closed identity')
    require(type(m['buildRoot']) is str and m['buildRoot'].startswith('/') and len(m['buildRoot'])<1024,'declared build-root identity')
    p=m['preflight'];require(set(p)=={'oldMissingExplanation','newRetainedExplanation','exitPreserved','oneCompileEach','functionControls','runtimeInventorySha256'} and all(p[k] is True for k in ('oldMissingExplanation','newRetainedExplanation','exitPreserved','oneCompileEach')) and type(p['functionControls']) is int and p['functionControls']==6 and p['runtimeInventorySha256']==v['runtimeInventorySha256'],'production-entry preflight')
    return b

def bounded_gh(endpoint,cap,deadline):
    p=subprocess.Popen(['gh','api',endpoint],stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True)
    out=bytearray();err=bytearray();sel=selectors.DefaultSelector();sel.register(p.stdout,selectors.EVENT_READ,out);sel.register(p.stderr,selectors.EVENT_READ,err)
    try:
        while sel.get_map():
            require(time.monotonic()<deadline,'native read deadline')
            for key,_ in sel.select(min(.2,max(0,deadline-time.monotonic()))):
                b=os.read(key.fileobj.fileno(),65536)
                if not b:sel.unregister(key.fileobj);continue
                key.data.extend(b);require(len(out)<=cap and len(err)<=8192,'native read byte cap')
        require(p.wait(timeout=max(.1,deadline-time.monotonic()))==0,'native read refused')
        return bytes(out)
    finally:
        sel.close()
        if p.poll() is None:
            os.killpg(p.pid,signal.SIGTERM)
            try:p.wait(timeout=2)
            except subprocess.TimeoutExpired:os.killpg(p.pid,signal.SIGKILL);p.wait(timeout=2)
        p.stdout.close();p.stderr.close()

def extract(archive,meta,destination):
    require(len(archive)==meta['size_in_bytes'] and 'sha256:'+digest(archive)==meta['digest'],'native archive digest/size')
    require(archive[-22:-18]==b'PK\x05\x06' and archive[-2:]==b'\x00\x00','closed ZIP end record')
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        rows=z.infolist();require(len(rows)==3 and {r.filename for r in rows}=={'manifest.json',BIN,'LICENSE'},'closed artifact members')
        for r in rows:require(not r.is_dir() and not r.flag_bits&1 and stat.S_IFMT(r.external_attr>>16) in (0,stat.S_IFREG) and 0<r.file_size<=(65536 if r.filename in ('manifest.json','LICENSE') else LIMIT),'artifact member size/type')
        manifest_raw=z.read('manifest.json');manifest=document(manifest_raw);binary=z.read(BIN);license_raw=z.read('LICENSE')
    b=validate_manifest(manifest,revision(),int(os.environ['GITHUB_RUN_ID']));require(len(binary)==b['bytes'] and digest(binary)==b['sha256'],'binary bytes')
    require(license_raw==(ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE').read_bytes(),'license bytes')
    destination.mkdir(mode=0o700)
    for path,raw in [('manifest.json',manifest_raw),(BIN,binary),('LICENSE',license_raw)]:
        with (destination/path).open('xb') as f:f.write(raw)
    (destination/BIN).chmod(0o700)
    return manifest

def consume(destination):
    end=time.monotonic()+120;run_id=int(os.environ['GITHUB_RUN_ID']);source=revision()
    require(os.environ.get('GITHUB_RUN_ATTEMPT')=='1' and os.environ.get('GITHUB_REPOSITORY')==REPO,'consumer current attempt/repository')
    prefix='repos/'+REPO+'/actions/';read=lambda p:document(bounded_gh(prefix+p,1048576,end))
    run=read('runs/'+str(run_id));jobs=read('runs/'+str(run_id)+'/attempts/1/jobs?per_page=100');artifacts=read('runs/'+str(run_id)+'/artifacts?per_page=100')
    # Expected source/run names select exactly one immutable artifact before bytes are read.
    expected='c2-diagnostic-client-'+str(run_id)+'-1-'+source
    selected=[a for a in artifacts['artifacts'] if a['name']==expected];require(len(selected)==1,'selected artifact absent/duplicate');a=selected[0]
    require(type(a['size_in_bytes']) is int and 0<a['size_in_bytes']<=LIMIT,'archive admission size')
    selected_id=int(os.environ['FSGG_C2_ARTIFACT_ID']);selected_digest=os.environ['FSGG_C2_ARTIFACT_DIGEST']
    require(selected_id>0 and re.fullmatch('[a-f0-9]{64}',selected_digest) and a['id']==selected_id and a['digest']=='sha256:'+selected_digest,'current producer upload outputs')
    raw=bounded_gh(prefix+'artifacts/'+str(a['id'])+'/zip',LIMIT,end)
    # Read the manifest without executing/extracting any artifact code.
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        require(sum(i.filename=='manifest.json' for i in z.infolist())==1 and z.getinfo('manifest.json').file_size<=65536,'manifest member')
        m=document(z.read('manifest.json'))
    validate_manifest(m,source,run_id);meta=native_join(m,run,jobs,artifacts,source,run_id,selected_id,selected_digest)
    m=extract(raw,meta,destination)
    receipt=dict(schema='fsgg.c2-diagnostic-client-custody/1',artifactId=meta['id'],artifactDigest=meta['digest'],manifestSha256=file_sha(destination/'manifest.json'),source=source,run=run_id,attempt=1)
    (destination/'custody.json').write_text(json.dumps(receipt,sort_keys=True)+'\n')
    print(str(destination/BIN))

PROFILE_FIELDS={'identity','source','run','attempt','artifactId','artifactDigest','manifestSha256','inputsSha256','binarySha256'}
def profile_shape(value):
    require(isinstance(value,dict) and set(value)==PROFILE_FIELDS and value['identity']=='quint-0.32.0-fsgg-c2-diagnostic-1','distinct diagnostic profile')
    require(re.fullmatch('[a-f0-9]{40}',value['source']) and all(type(value[k]) is int and value[k]>0 for k in ('run','artifactId')) and type(value['attempt']) is int and value['attempt']==1,'profile source/run/attempt')
    require(re.fullmatch('sha256:[a-f0-9]{64}',value['artifactDigest']) and all(re.fullmatch('[a-f0-9]{64}',value[k]) for k in ('manifestSha256','inputsSha256','binarySha256')),'profile byte identities')
    return value

def local_client(path):
    require(not path.is_symlink() and path.is_dir(),'client root')
    require(os.environ.get('FSGG_C2_CLIENT_MANIFEST_SHA256')==file_sha(path/'manifest.json'),'trusted consumer manifest binding')
    m=document(regular(path/'manifest.json',65536));c=document(regular(path/'custody.json',8192));b=validate_manifest(m,revision(),int(os.environ['GITHUB_RUN_ID']))
    require(set(c)=={'schema','artifactId','artifactDigest','manifestSha256','source','run','attempt'} and c['schema']=='fsgg.c2-diagnostic-client-custody/1' and c['manifestSha256']==file_sha(path/'manifest.json') and c['source']==revision() and c['run']==int(os.environ['GITHUB_RUN_ID']) and c['attempt']==1 and type(c['artifactId']) is int and c['artifactId']>0 and re.fullmatch('sha256:[a-f0-9]{64}',c['artifactDigest']),'local custody')
    require(not (path/BIN).is_symlink() and (path/BIN).is_file() and 0<(path/BIN).stat().st_size<=LIMIT,'bounded local executable');require((path/BIN).stat().st_size==b['bytes'] and file_sha(path/BIN)==b['sha256'],'local binary drift')
    return dict(identity=m['identity'],source=c['source'],run=c['run'],attempt=1,artifactId=c['artifactId'],artifactDigest=c['artifactDigest'],manifestSha256=c['manifestSha256'],inputsSha256=m['inputsSha256'],binarySha256=b['sha256'])

def build(output):
    import importlib.util
    v=inputs();c=custody(os.environ);declared_recipe=recipe();tracked=subprocess.check_output(['git','ls-files','-z','--',*FILES],cwd=ROOT).decode().split('\0');require(set(tracked)-{''}==set(FILES),'tracked producer input closure');require(subprocess.run(['git','diff','--quiet','HEAD'],cwd=ROOT).returncode==0,'clean producer source');require(not output.exists(),'fresh output root')
    scratch=output.parent/('build-'+name(c));scratch.mkdir(mode=0o700);end=time.monotonic()+480
    for p in ('inputs','source','tools','home','tmp','cache','outputs'): (scratch/p).mkdir()
    env={'PATH':'/usr/bin:/bin','HOME':str(scratch/'home'),'TMPDIR':str(scratch/'tmp'),'NPM_CONFIG_CACHE':str(scratch/'cache/npm'),'DENO_DIR':str(scratch/'cache/deno'),'NODE_OPTIONS':'--max-old-space-size=2048','CI':'true','NO_COLOR':'1'}
    spec=importlib.util.spec_from_file_location('c2_build_owner',ROOT/'eng/shared_process_owner.py');owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    ordinal=0
    def run(argv,cwd):
        nonlocal ordinal
        ordinal+=1;require(time.monotonic()<end-10,'original build deadline')
        log=scratch/'outputs'/('%02d.log'%ordinal)
        owner.run([str(a) for a in argv],str(cwd),env,min(end-10,time.monotonic()+240),10,4*1024**3,str(log),8*1024**2)
        return log
    run(['/usr/bin/python3','-I','-B',ROOT/'eng/tests/c2-diagnostic-client/test_client.py'],ROOT)
    for role in ('source','node','deno','denort'):
        x=v[role];p=scratch/'inputs'/role
        run(['curl','--fail','--location','--proto','=https','--proto-redir','=https','--silent','--show-error','--max-time','90','--max-filesize',str(x['maxBytes']),x['url'],'--output',p],scratch)
        regular(p,x['maxBytes']);require(p.stat().st_size==x['bytes'] and file_sha(p)==x['sha256'],'download '+role+' digest')
    with tarfile.open(scratch/'inputs/source') as t:
        selected=[m for m in t.getmembers() if '/quint/' in m.name]
        require(sum(m.size for m in selected)<=128*1024**2 and len(selected)<=4096,'source archive population')
        for m in selected:
            rel=m.name.split('/quint/',1)[1]
            if not rel:continue
            require(not rel.startswith('/') and '..' not in pathlib.PurePosixPath(rel).parts and (m.isdir() or m.isfile()),'source archive path/type')
            target=scratch/'source'/rel
            if m.isdir():target.mkdir(parents=True,exist_ok=True)
            else:target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(t.extractfile(m).read());target.chmod(0o644)
    with tarfile.open(scratch/'inputs/node') as t:
        require(sum(m.size for m in t.getmembers())<256*1024**2,'node expanded bound');t.extractall(scratch/'tools',filter='data')
    for role,member in [('deno','deno'),('denort','denort')]:
        with zipfile.ZipFile(scratch/'inputs'/role) as z:
            require(z.namelist()==[member] and 0<z.getinfo(member).file_size<160*1024**2,'compiler archive members')
            require(digest(z.read(member))==v[role+'ExecutableSha256'],'compiler runtime executable digest')
            if role=='deno':p=scratch/'tools/deno';p.write_bytes(z.read(member));p.chmod(0o700)
    # Provision the declared official runtime before compile: cached-only does not govern this input.
    denort=scratch/'cache/deno/dl/release/v2.9.8/denort-x86_64-unknown-linux-gnu.zip';denort.parent.mkdir(parents=True);shutil.copyfile(scratch/'inputs/denort',denort)
    node=scratch/'tools/node-v18.20.8-linux-x64';require(file_sha(node/'bin/node')==v['nodeExecutableSha256'],'Node executable digest')
    require(document(regular(node/'lib/node_modules/npm/package.json',65536))['version']==v['npmVersion'],'npm identity')
    env['PATH']=str(node/'bin')+':/usr/bin:/bin';source=scratch/'source';runtime=scratch/'runtime'
    require(file_sha(source/'package-lock.json')==v['packageLockSha256'] and file_sha(source/'src/verify.ts')==v['originalVerifySha256'],'source/lock bytes')
    run([node/'bin/npm','ci','--ignore-scripts','--no-audit','--no-fund'],source)
    run([node/'bin/npm','run','compile'],source);shutil.copytree(source/'dist',scratch/'old-dist')
    runtime.mkdir()
    for p in ('package.json','package-lock.json'):shutil.copyfile(source/p,runtime/p)
    shutil.copytree(source/'dist',runtime/'dist')
    run([node/'bin/npm','ci','--omit=dev','--ignore-scripts','--offline','--no-audit','--no-fund'],runtime)
    require(inventory(runtime)==v['runtimeInventorySha256'],'closed runtime dependency inventory')
    args=[scratch/'tools/deno','compile','--allow-all','--node-modules-dir=manual','--unstable-detect-cjs','--no-check','--no-config','--no-lock','--cached-only','--include','dist/src']
    old=scratch/'outputs/control-quint';new=scratch/'outputs'/BIN
    run(args+['--output',old,'dist/src/cli.js'],runtime)
    before=run([node/'bin/node',ROOT/'eng/tests/c2-diagnostic-client/production-cli-seam.cjs',scratch,old,'absent',ROOT/'eng/vendor/quint-c2-diagnostic/cmdExecutor.proto'],scratch)
    old_receipt=document(before.read_bytes());require(old_receipt['passed'] is True,'old production-entry RED control')
    patch=regular(ROOT/'eng/vendor/quint-c2-diagnostic/compile-explanation.patch',4096).decode();old_line="return cliErr('error', { ...verifying, errors: tlaResult.value.errors })";new_line='return cliErr(tlaResult.value.explanation, { ...verifying, errors: tlaResult.value.errors })'
    raw=(source/'src/verify.ts').read_text();require(raw.count(old_line)==1 and '-    '+old_line in patch and '+    '+new_line in patch,'one reviewed patch hunk')
    (source/'src/verify.ts').write_text(raw.replace(old_line,new_line));require(file_sha(source/'src/verify.ts')==v['patchedVerifySha256'],'patched source digest')
    run([node/'bin/npm','run','compile'],source)
    changed=[p.relative_to(scratch/'old-dist').as_posix() for p in (scratch/'old-dist').rglob('*') if p.is_file() and file_sha(p)!=file_sha(source/'dist'/p.relative_to(scratch/'old-dist'))]
    require(sorted(changed)==['src/verify.js','src/verify.js.map'],'compiled source delta')
    for p in ('verify.js','verify.js.map'):shutil.copyfile(source/'dist/src'/p,runtime/'dist/src'/p)
    run(args+['--output',new,'dist/src/cli.js'],runtime)
    after=run([node/'bin/node',ROOT/'eng/tests/c2-diagnostic-client/production-cli-seam.cjs',scratch,new,'present',ROOT/'eng/vendor/quint-c2-diagnostic/cmdExecutor.proto'],scratch)
    functions=run([node/'bin/node',ROOT/'eng/tests/c2-diagnostic-client/production-function-controls.cjs',scratch],scratch)
    a=document(after.read_bytes());f=document(functions.read_bytes());require(a['passed'] is True and len(f['controls'])==6 and all(x['passed'] is True for x in f['controls']),'new production-entry controls')
    require(inventory(runtime)==v['runtimeInventorySha256'] and file_sha(denort)==v['denort']['sha256'],'post-build runtime input drift')
    require(recipe()==declared_recipe and revision()==c['source'] and subprocess.run(['git','diff','--quiet','HEAD'],cwd=ROOT).returncode==0,'producer source changed during build')
    output.mkdir(mode=0o700);shutil.copyfile(new,output/BIN);(output/BIN).chmod(0o700)
    license_path=ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE';shutil.copyfile(license_path,output/'LICENSE')
    manifest=dict(license=dict(path='LICENSE',bytes=license_path.stat().st_size,sha256=file_sha(license_path)),schema='fsgg.c2-diagnostic-client-build/1',identity=v['identity'],custody=c,inputsSha256=file_sha(INPUT),recipe=declared_recipe,binary=dict(path=BIN,bytes=new.stat().st_size,sha256=file_sha(new)),buildRoot=str(scratch),preflight=dict(oldMissingExplanation=old_receipt['explanationPresent'] is False,newRetainedExplanation=a['explanationPresent'] is True,exitPreserved=a['code']==old_receipt['code']==1,oneCompileEach=a['requests']==old_receipt['requests']==['TLA'],functionControls=len(f['controls']),runtimeInventorySha256=inventory(runtime)))
    validate_manifest(manifest,c['source'],c['run']);(output/'manifest.json').write_text(json.dumps(manifest,sort_keys=True)+'\n')
    print(json.dumps({'artifact':name(c),'binary':manifest['binary'],'manifestSha256':file_sha(output/'manifest.json')}))

def main():
    p=argparse.ArgumentParser();p.add_argument('command',choices=['build','consume','check']);p.add_argument('root',type=pathlib.Path);a=p.parse_args()
    if a.command=='build':build(a.root)
    elif a.command=='consume':consume(a.root)
    else:print(json.dumps(local_client(a.root),sort_keys=True))
if __name__=='__main__':main()
