#!/usr/bin/env python3
from __future__ import annotations
import argparse, hashlib, importlib.util, json, os, stat, tempfile, zipfile
from pathlib import Path
HERE=Path(__file__).parent
def load(name,file):
 s=importlib.util.spec_from_file_location(name,HERE/file); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); return m
R=load('runtime','provider_runtime.py'); Q=load('qualify',Path('../../../eng/qualify-installed-python-hello.py').resolve()) if False else None
# load helper from repository
root=HERE.parents[2]; s=importlib.util.spec_from_file_location('qualify',root/'eng/qualify-installed-python-hello.py'); Q=importlib.util.module_from_spec(s); s.loader.exec_module(Q)
def write(p,b=b'x'): p.parent.mkdir(parents=True,exist_ok=True); p.write_bytes(b)
def main():
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
  flood=d/'flood'; flood.write_text('#!/usr/bin/python3\nimport sys\nsys.stdout.write("x"*(2*1024*1024))\n'); flood.chmod(0o755); args.dotnet=flood; args.receipt=d/'flood.json'; args.timeout_seconds=5
  try: Q.invoke(args); raise AssertionError('oversized output admitted')
  except ValueError as e: assert 'output exceeds' in str(e)
if __name__=='__main__': main()
