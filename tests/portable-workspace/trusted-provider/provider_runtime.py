#!/usr/bin/env python3
"""Bounded provider preflight, ownership ledger, and public evidence projection."""
from __future__ import annotations
import argparse, hashlib, json, os, re, shutil, stat, zipfile, xml.etree.ElementTree as ET
from pathlib import Path
from pathlib import PurePosixPath

SHA=re.compile(r'^[0-9a-f]{64}$')
ALLOWED_PATHS=(Path('/p4'),Path('/opt/p4'),Path('/opt/p4-requests'),Path('/srv/p4-receiver'),Path('/tmp/p4fr'),Path('/tmp/p4fu'),Path('/tmp/p4-public-candidate'),Path('/run/user/32001'),Path('/etc/fsgg/portable-workspaces'),Path('/etc/fsgg'))
REQUIRED_DIRS=(
 '/p4','/p4/runtime-v1','/p4/runtime-v1/home','/p4/runtime-v1/xdg-config',
 '/p4/runtime-v1/xdg-config/containers','/p4/runtime-v1/xdg-runtime',
 '/p4/runtime-v1/xdg-runtime/containers-runroot','/p4/runtime-v1/storage','/p4/evidence')

def canonical(v): return (json.dumps(v,sort_keys=True,separators=(',',':'))+'\n').encode()
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def ranges(text):
 out=[]
 for line in text.splitlines():
  if not line or line.startswith('#'): continue
  parts=line.split(':')
  if len(parts)!=3: raise ValueError('mapping file is malformed')
  start,count=int(parts[1]),int(parts[2])
  if start<=0 or count<=0: raise ValueError('mapping range is malformed')
  out.append((parts[0],start,count))
 return out
def overlap(a,n,b,m): return a < b+m and b < a+n

def account_plan(name,uid,gid,start,count,passwd,subuid,subgid):
 if uid<=0 or gid<=0 or count<32769: raise ValueError('account selection is outside bounds')
 for line in passwd.splitlines():
  p=line.split(':')
  if len(p)>=4 and (p[0]==name or int(p[2])==uid or int(p[3])==gid):
   raise ValueError('selected account identity already exists')
 for label,text in [('uid',subuid),('gid',subgid)]:
  for owner,other,size in ranges(text):
   if overlap(start,count,other,size): raise ValueError(f'selected {label} mapping collides with {owner}')
 return {'name':name,'uid':uid,'gid':gid,'subuid':{'start':start,'count':count},'subgid':{'start':start,'count':count}}

def capability(value,plan):
 if set(value)!={'account','groups','uidMappings','gidMappings','helpers','tools','platform','podman','runtime','resources','directories'}:
  raise ValueError('capability shape changed')
 a=value['account']
 if a!={'name':plan['name'],'uid':plan['uid'],'gid':plan['gid']}: raise ValueError('actual selected account differs')
 if set(value['groups'])!={plan['name']}: raise ValueError('selected account has unexpected groups')
 def covers(rows,expected):
  return any(r.get('container_id',-1)<=32768<r.get('container_id',-1)+r.get('size',0) and r.get('host_id')==expected['start'] for r in rows)
 if not covers(value['uidMappings'],plan['subuid']) or not covers(value['gidMappings'],plan['subgid']):
  raise ValueError('rootless mappings do not cover container uid and gid 32768')
 for helper in ('newuidmap','newgidmap'):
  h=value['helpers'].get(helper,{})
  if not Path(h.get('path','')).is_absolute() or not SHA.fullmatch(h.get('sha256','')): raise ValueError('rootless helper custody is incomplete')
 for name in ('git','tar','podman'):
  tool=value['tools'].get(name,{})
  if not Path(tool.get('path','')).is_absolute() or not SHA.fullmatch(tool.get('sha256','')) or not tool.get('version'): raise ValueError('host tool custody is incomplete')
 p=value['podman']
 if (p.get('rootless') is not True or p.get('os')!='linux' or p.get('architecture')!='amd64'
     or not p.get('namespaceUidMap') or not p.get('namespaceGidMap')): raise ValueError('podman capability differs')
 if not value['platform'].get('kernel') or value['platform'].get('cgroupVersion') not in (1,2): raise ValueError('host platform capability differs')
 rt=value['runtime']
 if rt.get('sdkCount')!=0 or rt.get('hostFxrCount',0)<1 or rt.get('version') in ('',None): raise ValueError('runtime-only inventory differs')
 if rt.get('hiddenLocations')!=rt.get('measuredLocations') or any(rt.get('accessibleLocations',[])):
  raise ValueError('measured SDK or provisioning location remains accessible')
 resources=value['resources']
 if resources.get('freeBytes',0)<resources.get('requiredBytes',1) or resources.get('freeInodes',0)<10000 or resources.get('memoryBytes',0)<2147483648:
  raise ValueError('provider resources are insufficient')
 if resources.get('stateDevice')!=resources.get('storageDevice'): raise ValueError('provider storage devices differ')
 directories=value['directories']
 if [d.get('path') for d in directories]!=list(REQUIRED_DIRS): raise ValueError('provider directory inventory differs')
 if any(d.get('uid')!=plan['uid'] or d.get('gid')!=plan['gid'] or d.get('mode')!='0700' for d in directories):
  raise ValueError('provider directory ownership differs')
 return value

def init_ledger(path,source):
 if path.exists(): raise ValueError('cleanup ledger already exists')
 path.parent.mkdir(parents=True,exist_ok=True)
 path.write_bytes(canonical({'schema':'fsgg.portable-provider-owned-resources/1','source':source,'created':[]}))
 os.chmod(path,0o600)
def register(path,kind,value):
 d=json.loads(path.read_text()); item={'kind':kind,'value':value}
 if item in d['created']: raise ValueError('owned resource already registered')
 d['created'].append(item); path.write_bytes(canonical(d))
def cleanup_paths(path):
 d=json.loads(path.read_text()); failures=[]
 for item in reversed(d['created']):
  if item['kind']!='path': continue
  p=Path(item['value'])
  if p not in ALLOWED_PATHS: failures.append({'path':str(p),'reason':'outside-owned-scope'}); continue
  try:
   if p.is_symlink(): raise ValueError('owned path became a link')
   if p.exists():
    if str(p).startswith('/etc/'): p.rmdir()
    else: shutil.rmtree(p)
  except Exception as e: failures.append({'path':str(p),'reason':str(e)})
 return {'schema':'fsgg.portable-provider-cleanup/1','complete':not failures,'failures':failures,'survivors':[x['value'] for x in d['created'] if x['kind']=='path' and Path(x['value']).exists()]}
def public_projection(private,cleanup):
 p=json.loads(private.read_text()); c=json.loads(cleanup.read_text())
 allowed={'schema','sourceRevision','sourceTree','providerFactsSha256','candidateReceiptSha256','profileSha256','commandSha256','executeResultSha256','duplicateResultSha256','recoverResultSha256','journalSha256','oneLaunchObserved','duplicateObserved','recoverSettled'}
 if set(p)!=allowed: raise ValueError('private qualification shape changed')
 if c.get('complete') is not True or c.get('survivors')!=[]: raise ValueError('cleanup is incomplete')
 out={k:p[k] for k in sorted(allowed)}; out['cleanupSha256']=sha(cleanup); out['privateEvidencePublished']=False; return out

def artifact_custody(run,artifact,expected):
 if (run.get('id')!=expected['runId'] or run.get('run_attempt')!=expected['runAttempt']
     or run.get('head_sha')!=expected['sourceRevision'] or run.get('path')!=expected['workflow']
     or run.get('conclusion')!='success' or run.get('event')!='workflow_dispatch'):
  raise ValueError('producer workflow identity differs')
 wr=artifact.get('workflow_run',{})
 if (artifact.get('id')!=expected['artifactId'] or artifact.get('name')!=expected['artifactName']
     or artifact.get('digest')!=expected['artifactDigest'] or artifact.get('expired') is not False
     or wr.get('id')!=expected['runId'] or wr.get('head_sha')!=expected['sourceRevision']):
  raise ValueError('producer artifact identity differs')
 return expected

def compare_capability(previous,current,plan):
 previous=capability(previous,plan); current=capability(current,plan)
 stable=('account','groups','uidMappings','gidMappings','helpers','tools','platform','podman','runtime')
 if any(previous[key]!=current[key] for key in stable): raise ValueError('private provider binding drifted')
 return {'schema':'fsgg.portable-provider-capability-comparison/1','bindingsMatch':True,'factsResourcesPassed':True,'currentResourcesPassed':True}

def private_facts(candidate,measured,plan):
 capability(measured,plan)
 if candidate.get('schema')!='fsgg.portable-workspace-python-provider-facts/1' or candidate.get('nativeExecutionAuthorized') is not False:
  raise ValueError('candidate facts are not the unauthorized closed input')
 value=dict(candidate)
 value.update({'allowedUid':plan['uid'],'allowedGid':plan['gid'],'selectedAccount':measured['account'],'helpers':measured['helpers'],
               'executables':{name:{'path':item['path'],'sha256':item['sha256']} for name,item in measured['tools'].items()},
               'privateProvider':{'schema':'fsgg.portable-python-private-provider-facts/1','capability':measured,
                                  'policy':{'containerUid':32768,'containerGid':32768,'storageDriver':'vfs','minimumFreeInodes':10000,'minimumMemoryBytes':2147483648}}})
 return value

def extract_zip(archive,target,expected,package_id=None,version=None):
 if sha(archive)!=expected: raise ValueError('package digest changed')
 if target.exists() and (target.is_symlink() or not target.is_dir() or any(target.iterdir())): raise ValueError('package target is not empty')
 target.mkdir(parents=True,exist_ok=True); total=0; seen=set()
 with zipfile.ZipFile(archive) as z:
  infos=z.infolist()
  if not infos or len(infos)>4096: raise ValueError('package entry count is outside bounds')
  if package_id is not None or version is not None:
   nuspec=[x for x in infos if PurePosixPath(x.filename).suffix=='.nuspec']
   if len(nuspec)!=1: raise ValueError('package identity document is ambiguous')
   metadata={x.tag.rsplit('}',1)[-1]:(x.text or '') for x in ET.fromstring(z.read(nuspec[0])).iter()}
   if metadata.get('id')!=package_id or metadata.get('version')!=version: raise ValueError('package identity differs')
  for info in infos:
   raw=info.filename.rstrip('/'); p=PurePosixPath(raw)
   folded=raw.casefold()
   if not raw or '\\' in raw or p.is_absolute() or '..' in p.parts or '.' in p.parts or folded in seen: raise ValueError('package path is unsafe or repeated')
   seen.add(folded); mode=info.external_attr>>16
   if stat.S_ISLNK(mode) or info.flag_bits&1: raise ValueError('package link or encryption is refused')
   out=target.joinpath(*p.parts)
   if info.is_dir(): out.mkdir(parents=True,exist_ok=True); continue
   total+=info.file_size
   if info.file_size>16*1024*1024 or total>128*1024*1024: raise ValueError('package exceeds bounds')
   out.parent.mkdir(parents=True,exist_ok=True); data=z.read(info)
   with out.open('xb') as f: f.write(data)
 return {'sha256':expected,'entries':len(infos),'bytes':total}

def main():
 ap=argparse.ArgumentParser(); sub=ap.add_subparsers(dest='cmd',required=True)
 a=sub.add_parser('account-plan'); a.add_argument('--passwd',type=Path,required=True); a.add_argument('--subuid',type=Path,required=True); a.add_argument('--subgid',type=Path,required=True); a.add_argument('--name',required=True); a.add_argument('--uid',type=int,required=True); a.add_argument('--gid',type=int,required=True); a.add_argument('--start',type=int,required=True); a.add_argument('--count',type=int,required=True); a.add_argument('--output',type=Path,required=True)
 c=sub.add_parser('capability'); c.add_argument('--input',type=Path,required=True); c.add_argument('--plan',type=Path,required=True); c.add_argument('--output',type=Path,required=True)
 i=sub.add_parser('ledger-init'); i.add_argument('--ledger',type=Path,required=True); i.add_argument('--source',required=True)
 g=sub.add_parser('ledger-register'); g.add_argument('--ledger',type=Path,required=True); g.add_argument('--kind',choices=['path','account','grant','container'],required=True); g.add_argument('--value',required=True)
 x=sub.add_parser('cleanup-paths'); x.add_argument('--ledger',type=Path,required=True); x.add_argument('--output',type=Path,required=True)
 p=sub.add_parser('public-projection'); p.add_argument('--private',type=Path,required=True); p.add_argument('--cleanup',type=Path,required=True); p.add_argument('--output',type=Path,required=True)
 v=sub.add_parser('artifact-custody'); v.add_argument('--run',type=Path,required=True); v.add_argument('--artifact',type=Path,required=True); v.add_argument('--expected',type=Path,required=True); v.add_argument('--output',type=Path,required=True)
 z=sub.add_parser('extract-zip'); z.add_argument('--archive',type=Path,required=True); z.add_argument('--target',type=Path,required=True); z.add_argument('--sha256',required=True); z.add_argument('--package-id'); z.add_argument('--version'); z.add_argument('--output',type=Path,required=True)
 q=sub.add_parser('compare-capability'); q.add_argument('--facts',type=Path,required=True); q.add_argument('--current',type=Path,required=True); q.add_argument('--plan',type=Path,required=True); q.add_argument('--output',type=Path,required=True)
 f=sub.add_parser('private-facts'); f.add_argument('--candidate',type=Path,required=True); f.add_argument('--capability',type=Path,required=True); f.add_argument('--plan',type=Path,required=True); f.add_argument('--output',type=Path,required=True)
 args=ap.parse_args()
 try:
  if args.cmd=='account-plan': args.output.write_bytes(canonical(account_plan(args.name,args.uid,args.gid,args.start,args.count,args.passwd.read_text(),args.subuid.read_text(),args.subgid.read_text())))
  elif args.cmd=='capability': args.output.write_bytes(canonical(capability(json.loads(args.input.read_text()),json.loads(args.plan.read_text()))))
  elif args.cmd=='ledger-init': init_ledger(args.ledger,args.source)
  elif args.cmd=='ledger-register': register(args.ledger,args.kind,args.value)
  elif args.cmd=='cleanup-paths': args.output.write_bytes(canonical(cleanup_paths(args.ledger)))
  elif args.cmd=='public-projection': args.output.write_bytes(canonical(public_projection(args.private,args.cleanup)))
  elif args.cmd=='artifact-custody': args.output.write_bytes(canonical(artifact_custody(json.loads(args.run.read_text()),json.loads(args.artifact.read_text()),json.loads(args.expected.read_text()))))
  elif args.cmd=='extract-zip': args.output.write_bytes(canonical(extract_zip(args.archive,args.target,args.sha256,args.package_id,args.version)))
  elif args.cmd=='compare-capability': args.output.write_bytes(canonical(compare_capability(json.loads(args.facts.read_text()),json.loads(args.current.read_text()),json.loads(args.plan.read_text()))))
  else: args.output.write_bytes(canonical(private_facts(json.loads(args.candidate.read_text()),json.loads(args.capability.read_text()),json.loads(args.plan.read_text()))))
  return 0
 except (OSError,ValueError,KeyError,json.JSONDecodeError,ET.ParseError) as e:
  print('PORTABLE_PROVIDER_RUNTIME_REFUSED',e); return 2
if __name__=='__main__': raise SystemExit(main())
