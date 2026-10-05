#!/usr/bin/env python3
from __future__ import annotations
import ast, argparse, contextlib, hashlib, importlib.util, io, json, os, stat, subprocess, sys, tempfile, time, unittest, zipfile
from pathlib import Path
from unittest.mock import patch
HERE=Path(__file__).parent
def load(name,file):
 s=importlib.util.spec_from_file_location(name,HERE/file); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); return m
R=load('runtime','provider_runtime.py'); Q=load('qualify',Path('../../../eng/qualify-installed-python-hello.py').resolve()) if False else None
# load helper from repository
root=HERE.parents[2]; s=importlib.util.spec_from_file_location('qualify',root/'eng/qualify-installed-python-hello.py'); Q=importlib.util.module_from_spec(s); s.loader.exec_module(Q)
def write(p,b=b'x'): p.parent.mkdir(parents=True,exist_ok=True); p.write_bytes(b)
def capability_collector_contract():
 # Pure command observations: never launch runuser, Podman, account effects or CLR.
 collector=load('collector_contract','collect_provider_capability.py')
 parent_home=os.environ.get('HOME'); commands=[]
 with patch.object(collector.subprocess,'check_output',side_effect=lambda argv,**kwargs:commands.append(argv) or ''):
  collector.runuser('p4executor',['podman','info'])
 assert commands==[['/usr/sbin/runuser','--user','p4executor','--','/usr/bin/env','HOME=/p4/runtime-v1/home','XDG_CONFIG_HOME=/p4/runtime-v1/xdg-config','XDG_RUNTIME_DIR=/p4/runtime-v1/xdg-runtime','PATH=/usr/local/bin:/usr/bin:/bin','podman','info']]
 assert os.environ.get('HOME')==parent_home
 with patch.object(collector.subprocess,'check_output',side_effect=subprocess.CalledProcessError(2,['fixture'])) as child:
  try: collector.runuser('p4executor',['podman','info']); raise AssertionError('child failure accepted')
  except subprocess.CalledProcessError: pass
  assert child.call_count==1
 tree=ast.parse((HERE/'collect_provider_capability.py').read_bytes())
 main=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='main')
 prefix=[]
 for node in main.body:
  if isinstance(node,ast.Expr) and isinstance(node.value,ast.Call) and isinstance(node.value.func,ast.Attribute) and node.value.func.attr=='write_text': break
  prefix.append(node)
 program=ast.Module(body=prefix,type_ignores=[]); ast.fix_missing_locations(program)
 with tempfile.TemporaryDirectory() as td:
  measured=Path(td)/'measured'; measured.write_text('/fixture/hidden\n'); calls=[]
  def observe(account,args):
   assert account=='p4executor'; calls.append(args)
   return json.dumps({'host':{}}) if len(calls)==1 else 'fixture-map'
  namespace={'argparse':argparse,'Path':Path,'json':json,'runuser':observe,'stage':collector.stage}
  argv=['fixture','--account','p4executor','--uid','32001','--state','/p4','--storage','/p4/runtime-v1/storage','--archive','/fixture/archive','--runtime','/fixture/runtime','--measured-locations',str(measured),'--podman-info','/fixture/info','--output','/fixture/output']
  markers=io.StringIO()
  with patch.object(sys,'argv',argv),contextlib.redirect_stdout(markers): exec(compile(program,'<collector contract fixture>','exec'),namespace)
  assert markers.getvalue().splitlines()==['PORTABLE_PROVIDER_CAPABILITY_STAGE='+name for name in ['arguments','locations','podman-info','uid-map','gid-map','podman-shape']]
  assert len(calls)==3
  for command in calls:
   assert command[:3]==['podman','--storage-driver=vfs','--root']
   assert command[command.index('--root')+1]=='/p4/runtime-v1/storage'
   assert command[command.index('--runroot')+1]=='/p4/runtime-v1/xdg-runtime/containers-runroot'
  assert [command[7:] for command in calls]==[['--format=json'],['cat','/proc/self/uid_map'],['cat','/proc/self/gid_map']]
 assert os.environ.get('HOME')==parent_home
 diagnostics=load('fixed_diagnostic_controls','test_capability_diagnostics.py')
 result=unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.loadTestsFromTestCase(diagnostics.Controls))
 assert result.wasSuccessful() and result.testsRun==3 and not result.skipped

def main():
 capability_collector_contract()
 # Producer metadata is an exact tuple.
 expected={'runId':11,'runAttempt':2,'sourceRevision':'a'*40,'workflow':'.github/workflows/x.yml','artifactId':22,'artifactDigest':'sha256:'+'b'*64,'artifactName':'candidate'}
 run={'id':11,'run_attempt':2,'head_sha':'a'*40,'path':'.github/workflows/x.yml','conclusion':'success','event':'workflow_dispatch'}
 artifact={'id':22,'name':'candidate','digest':'sha256:'+'b'*64,'expired':False,'workflow_run':{'id':11,'head_sha':'a'*40}}
 assert R.artifact_custody(run,artifact,expected)==expected
 changed=dict(artifact,digest='sha256:'+'c'*64)
 try: R.artifact_custody(run,changed,expected); raise AssertionError('changed artifact admitted')
 except ValueError: pass
 # Account mappings refuse name/uid/gid and range collisions.
 plan=R.account_plan('p4executor',32001,32001,100000,65536,'root:x:0:0::/:/bin/sh\n','other:200000:65536\n','other:200000:65536\n')
 for passwd,sub in [('p4executor:x:32001:32001::/:/bin/sh\n',''),('', 'other:120000:65536\n')]:
  try: R.account_plan('p4executor',32001,32001,100000,65536,passwd,sub,sub); raise AssertionError('collision admitted')
  except ValueError: pass
 cap={'account':{'name':'p4executor','uid':32001,'gid':32001},'groups':['p4executor'],'uidMappings':[{'container_id':0,'host_id':100000,'size':65536}], 'gidMappings':[{'container_id':0,'host_id':100000,'size':65536}], 'tools':{'git':{'path':'/usr/bin/git','sha256':'5'*64,'version':'git version 1'},'tar':{'path':'/usr/bin/tar','sha256':'6'*64,'version':'tar 1'},'podman':{'path':'/usr/bin/podman','sha256':'7'*64,'version':'podman 4'}},'helpers':{'newuidmap':{'path':'/usr/bin/newuidmap','sha256':'1'*64},'newgidmap':{'path':'/usr/bin/newgidmap','sha256':'2'*64}},'platform':{'kernel':'6.8.0','cgroupVersion':2},'podman':{'rootless':True,'os':'linux','architecture':'amd64','namespaceUidMap':'0 100000 65536','namespaceGidMap':'0 100000 65536'},'runtime':{'sdkCount':0,'hostFxrCount':1,'version':'Microsoft.NETCore.App 10.0.1','hiddenLocations':['/sdk'],'measuredLocations':['/sdk'],'accessibleLocations':[]},'resources':{'freeBytes':4000000000,'requiredBytes':2000000000,'freeInodes':20000,'memoryBytes':4000000000,'stateDevice':1,'storageDevice':1},'directories':[{'path':x,'uid':32001,'gid':32001,'mode':'0700'} for x in R.REQUIRED_DIRS]}
 changed=json.loads(json.dumps(cap)); changed['resources']['freeBytes']-=1
 assert R.compare_capability(cap,changed,plan)['bindingsMatch'] is True
 private=R.private_facts({'schema':'fsgg.portable-workspace-python-provider-facts/1','nativeExecutionAuthorized':False},cap,plan)
 assert private['allowedUid']==32001 and private['executables']['podman']['sha256']=='7'*64
 drift=json.loads(json.dumps(cap)); drift['helpers']['newuidmap']['sha256']='3'*64
 try: R.compare_capability(cap,drift,plan); raise AssertionError('helper drift admitted')
 except ValueError: pass
 with tempfile.TemporaryDirectory() as t:
  d=Path(t); archive=d/'x.nupkg'; target=d/'out'
  with zipfile.ZipFile(archive,'w') as z: z.writestr('tools/a.dll',b'a')
  digest=hashlib.sha256(archive.read_bytes()).hexdigest(); assert R.extract_zip(archive,target,digest)['entries']==1
  bad=d/'bad.zip'
  with zipfile.ZipFile(bad,'w') as z: z.writestr('../escape',b'x')
  try: R.extract_zip(bad,d/'bad-out',hashlib.sha256(bad.read_bytes()).hexdigest()); raise AssertionError('traversal admitted')
  except ValueError: pass
  cleanup=d/'cleanup.json'; cleanup.write_text('{"complete":true,"failures":[],"schema":"fsgg.portable-provider-cleanup/1","survivors":[]}\n')
  private=d/'private.json'; private.write_bytes(R.canonical({'schema':'fsgg.portable-python-provider-private-summary/1','sourceRevision':'a'*40,'sourceTree':'b'*40,'providerFactsSha256':'c'*64,'candidateReceiptSha256':'d'*64,'profileSha256':'e'*64,'commandSha256':'f'*64,'executeResultSha256':'1'*64,'duplicateResultSha256':'2'*64,'recoverResultSha256':'3'*64,'journalSha256':'4'*64,'oneLaunchObserved':True,'duplicateObserved':True,'recoverSettled':True}))
  assert R.public_projection(private,cleanup)['privateEvidencePublished'] is False
  ledger=d/'ledger.json'; ledger.write_bytes(R.canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':'a'*40,'created':[{'kind':'path','value':'/tmp/not-owned'}]}))
  failed=R.cleanup_paths(ledger); assert failed['complete'] is False and failed['failures']
  cleanup.write_bytes(R.canonical(failed))
  try: R.public_projection(private,cleanup); raise AssertionError('incomplete cleanup admitted')
  except ValueError: pass
  cleanup.write_text('{"complete":true,"failures":[],"schema":"fsgg.portable-provider-cleanup/1","survivors":[]}\n')
  owned=d/'owned'; foreign=d/'foreign'; owned.mkdir(); foreign.mkdir(); R.ALLOWED_PATHS=(owned,)
  ledger.write_bytes(R.canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':'a'*40,'created':[{'kind':'path','value':str(owned)}]}))
  completed=R.cleanup_paths(ledger); assert completed['complete'] is True and not owned.exists() and foreign.exists()
  # Cleanup CLI always emits its final receipt and fails on malformed ledgers,
  # deletion errors, and survivors rather than returning a green status.
  cli=HERE/'provider_runtime.py'; empty=d/'empty-ledger.json'; empty.write_bytes(R.canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':'a'*40,'created':[]}))
  cli_receipt=d/'cli-clean.json'; clean_run=subprocess.run([sys.executable,str(cli),'cleanup-paths','--ledger',str(empty),'--output',str(cli_receipt)],capture_output=True,text=True)
  assert clean_run.returncode==0 and json.loads(cli_receipt.read_text())['complete'] is True
  malformed=d/'malformed-ledger.json'; malformed.write_text('{'); malformed_receipt=d/'malformed-cleanup.json'
  malformed_run=subprocess.run([sys.executable,str(cli),'cleanup-paths','--ledger',str(malformed),'--output',str(malformed_receipt)],capture_output=True,text=True)
  assert malformed_run.returncode==2 and json.loads(malformed_receipt.read_text())['complete'] is False
  survivor=d/'survivor'; survivor.mkdir(); survivor_ledger=d/'survivor-ledger.json'; survivor_ledger.write_bytes(R.canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':'a'*40,'created':[{'kind':'path','value':str(survivor)}]})); survivor_receipt=d/'survivor-cleanup.json'
  survivor_run=subprocess.run([sys.executable,str(cli),'cleanup-paths','--ledger',str(survivor_ledger),'--output',str(survivor_receipt)],capture_output=True,text=True)
  assert survivor_run.returncode==2 and json.loads(survivor_receipt.read_text())['survivors']==[str(survivor)]
  failing=d/'deletion-failure'; failing.mkdir(); failure_ledger=d/'failure-ledger.json'; failure_ledger.write_bytes(R.canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':'a'*40,'created':[{'kind':'path','value':str(failing)}]})); failure_receipt=d/'failure-cleanup.json'
  original_allowed,original_rmtree,original_argv=R.ALLOWED_PATHS,R.shutil.rmtree,sys.argv
  try:
   R.ALLOWED_PATHS=(failing,); R.shutil.rmtree=lambda _: (_ for _ in ()).throw(OSError('injected deletion failure'))
   sys.argv=['provider_runtime.py','cleanup-paths','--ledger',str(failure_ledger),'--output',str(failure_receipt)]
   assert R.main()==2
  finally: R.ALLOWED_PATHS, R.shutil.rmtree, sys.argv=original_allowed,original_rmtree,original_argv
  failed_receipt=json.loads(failure_receipt.read_text()); assert failed_receipt['complete'] is False and failed_receipt['survivors']==[str(failing)] and failed_receipt['failures']
  # Invoke validates real production-shaped output and bounds descendants/output.
  tool=d/'tool'; tool.mkdir(); entry=tool/Q.CLI_DLL; write(entry,b'dll')
  payload,files=Q.payload_digest(tool); candidate=d/'candidate.json'; grant=d/'grant.json'; profile=d/'profile.json'; command=d/'command.json'; workspace=d/'workspace'; workspace.mkdir()
  write(grant,b'{}')
  receipt={'schema':Q.SCHEMA,'authorizationState':'required-external','grantWritten':False,'nativeExecutionAuthorized':False,'package':{},'installedCli':{'root':str(tool),'entryAssembly':str(entry),'entryAssemblySha256':Q.sha256_file(entry),'payloadSha256':payload,'files':files},'producer':{},'image':{'qualifiedImage':'image@sha256:'+'a'*64}}
  candidate.write_bytes(Q.canonical(receipt)); Q.GRANT_PATH=grant
  command_value={'commandId':'10000000-0000-0000-0000-000000000001','sourceRevision':'c'*40,'expectedWorkflowRevision':'7','fenceGeneration':'9'}
  profile_value={'qualifiedImage':receipt['image']['qualifiedImage']}; command.write_text(json.dumps(command_value)); profile.write_text(json.dumps(profile_value))
  result={'schema':'fsgg.portable-workspace-runtime-result/1','outcome':'completed','commandId':command_value['commandId'],'operation':'test','entryPoint':'python-test','workspaceScope':'fs-gg/p4-python-receiver','sourceRevision':command_value['sourceRevision'],'qualifiedImage':receipt['image']['qualifiedImage'],'cleanupCompleted':True,'executionStarted':True,'outputSha256':'b'*64,'result':{'workflowRevision':'7','fenceGeneration':'9','exitCode':{'state':'known','value':0},'error':None}}
  fake=d/'dotnet'; fake.write_text('#!/usr/bin/python3\nimport json\nprint('+repr(json.dumps(result))+')\n'); fake.chmod(0o755)
  args=argparse.Namespace(candidate_receipt=candidate,grant_path=grant,dotnet=fake,workspace=workspace,profile=profile,command=command,mode='execute',expected_outcome='completed',timeout_seconds=2,receipt=d/'invoke.json')
  assert Q.invoke(args)==0 and json.loads(args.receipt.read_text())['authorizationConsumedFromFixedGrant'] is True
  sleeper=d/'sleep'; sleeper.write_text('#!/bin/sh\nsleep 10\n'); sleeper.chmod(0o755); args.dotnet=sleeper; args.receipt=d/'timeout.json'; args.timeout_seconds=1
  try: Q.invoke(args); raise AssertionError('timeout admitted')
  except ValueError as e: assert 'terminate' in str(e)
  # The session leader exits on TERM while its descendant ignores TERM and
  # retains both output pipes. The helper must escalate, settle, and reap it.
  descendant_pid=d/'descendant.pid'; stubborn=d/'stubborn'
  stubborn.write_text('#!/usr/bin/python3\nimport pathlib,signal,subprocess,sys,time\np=subprocess.Popen([sys.executable,"-c","import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(30)"])\npathlib.Path('+repr(str(descendant_pid))+').write_text(str(p.pid))\ntime.sleep(30)\n'); stubborn.chmod(0o755)
  args.dotnet=stubborn; args.receipt=d/'stubborn.json'; args.timeout_seconds=1
  try: Q.invoke(args); raise AssertionError('stubborn descendant admitted')
  except ValueError as e: assert 'terminate' in str(e)
  pid=int(descendant_pid.read_text()); deadline=time.monotonic()+2
  while time.monotonic()<deadline and Path(f'/proc/{pid}').exists(): time.sleep(.05)
  assert not Path(f'/proc/{pid}').exists(), 'owned descendant survived refusal'
  flood=d/'flood'; flood.write_text('#!/usr/bin/python3\nimport sys\nsys.stdout.write("x"*(2*1024*1024))\n'); flood.chmod(0o755); args.dotnet=flood; args.receipt=d/'flood.json'; args.timeout_seconds=5
  try: Q.invoke(args); raise AssertionError('oversized output admitted')
  except ValueError as e: assert 'output exceeds' in str(e)
  # Model a legal schedule where the leader is already complete and reader
  # work is deferred until join. Late stdout and stderr overflow must refuse.
  class DeferredThread:
   def __init__(self,target,args,daemon): self.target,self.args,self.ran=target,args,False
   def start(self): pass
   def join(self,timeout=None):
    if not self.ran: self.ran=True; self.target(*self.args)
   def is_alive(self): return False
  class CompletedChild:
   def __init__(self,stdout,stderr): self.pid=987654; self.stdout=io.BytesIO(stdout); self.stderr=io.BytesIO(stderr); self.returncode=0
   def poll(self): return 0
   def wait(self,timeout=None): return 0
   def kill(self): raise AssertionError('completed leader killed')
  production=json.dumps(result,separators=(',',':')).encode()
  original_popen,original_thread,original_getpgid,original_getsid=Q.subprocess.Popen,Q.threading.Thread,Q.os.getpgid,Q.os.getsid
  try:
   Q.threading.Thread=DeferredThread; Q.os.getpgid=lambda _:987654; Q.os.getsid=lambda _:987654
   for label,stdout,stderr in [('stdout',production+b' '*(Q.MAX_OUTPUT_BYTES+1),b''),('stderr',production,b'x'*(Q.MAX_OUTPUT_BYTES+1))]:
    Q.subprocess.Popen=lambda *a,_stdout=stdout,_stderr=stderr,**k: CompletedChild(_stdout,_stderr)
    args.receipt=d/f'late-{label}.json'
    try: Q.invoke(args); raise AssertionError(f'late {label} overflow admitted')
    except ValueError as e: assert 'output exceeds' in str(e)
    assert not args.receipt.exists()
  finally: Q.subprocess.Popen,Q.threading.Thread,Q.os.getpgid,Q.os.getsid=original_popen,original_thread,original_getpgid,original_getsid
if __name__=='__main__': main()
