#!/usr/bin/env python3
"""Measure selected-account provider capability; performs no provisioning."""
from __future__ import annotations
import argparse, hashlib, json, os, shutil, subprocess
from pathlib import Path
REQUIRED_DIRS=['/p4','/p4/runtime-v1','/p4/runtime-v1/home','/p4/runtime-v1/xdg-config','/p4/runtime-v1/xdg-config/containers','/p4/runtime-v1/xdg-runtime','/p4/runtime-v1/xdg-runtime/containers-runroot','/p4/runtime-v1/storage','/p4/evidence']
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
ACCOUNT_ENV=['/usr/bin/env','HOME=/p4/runtime-v1/home','XDG_CONFIG_HOME=/p4/runtime-v1/xdg-config','XDG_RUNTIME_DIR=/p4/runtime-v1/xdg-runtime','PATH=/usr/local/bin:/usr/bin:/bin']
def runuser(account,args): return subprocess.check_output(['/usr/sbin/runuser','--user',account,'--',*ACCOUNT_ENV,*args],text=True,timeout=20)
def main():
 ap=argparse.ArgumentParser(); ap.add_argument('--account',required=True); ap.add_argument('--uid',type=int,required=True); ap.add_argument('--state',type=Path,required=True); ap.add_argument('--storage',type=Path,required=True); ap.add_argument('--archive',type=Path,required=True); ap.add_argument('--runtime',type=Path,required=True); ap.add_argument('--measured-locations',type=Path,required=True); ap.add_argument('--podman-info',type=Path,required=True); ap.add_argument('--output',type=Path,required=True); a=ap.parse_args()
 locations=[x for x in a.measured_locations.read_text().splitlines() if x]
 if len(locations)!=len(set(locations)) or any(not Path(x).is_absolute() for x in locations): raise ValueError('measured location inventory is malformed')
 podman=json.loads(runuser(a.account,['podman','--storage-driver=vfs','--root',str(a.storage),'--runroot','/p4/runtime-v1/xdg-runtime/containers-runroot','info','--format=json']))
 namespace_uid=runuser(a.account,['podman','--storage-driver=vfs','--root',str(a.storage),'--runroot','/p4/runtime-v1/xdg-runtime/containers-runroot','unshare','cat','/proc/self/uid_map']).strip()
 namespace_gid=runuser(a.account,['podman','--storage-driver=vfs','--root',str(a.storage),'--runroot','/p4/runtime-v1/xdg-runtime/containers-runroot','unshare','cat','/proc/self/gid_map']).strip()
 a.podman_info.write_text(json.dumps(podman,sort_keys=True,separators=(',',':'))+'\n')
 host=podman['host']; uidmap=host['idMappings']['uidmap']; gidmap=host['idMappings']['gidmap']
 helpers={n:{'path':shutil.which(n),'sha256':sha(shutil.which(n))} for n in ('newuidmap','newgidmap') if shutil.which(n)}
 tools={}
 for name in ('git','tar','podman'):
  path=shutil.which(name)
  version=runuser(a.account,[path,'--version']).splitlines()[0]
  tools[name]={'path':path,'sha256':sha(path),'version':version}
 runtimes=runuser(a.account,[str(a.runtime),'--list-runtimes']).splitlines(); sdks=runuser(a.account,[str(a.runtime),'--list-sdks']).splitlines()
 accessible=[]
 for location in locations:
  observed=subprocess.run(['/usr/sbin/runuser','--user',a.account,'--','find',location,'-mindepth','1','-maxdepth','1','-print','-quit'],capture_output=True,text=True,timeout=5)
  if observed.returncode!=0 or observed.stdout.strip(): accessible.append(location)
 sv=os.statvfs(a.storage); memory=0
 for line in Path('/proc/meminfo').read_text().splitlines():
  if line.startswith('MemAvailable:'): memory=int(line.split()[1])*1024
 cgroup=Path('/sys/fs/cgroup/memory.max')
 if cgroup.is_file() and cgroup.read_text().strip()!='max': memory=min(memory,int(cgroup.read_text().strip()))
 required=a.archive.stat().st_size*3 + 1024*1024*1024
 platform={'kernel':os.uname().release,'cgroupVersion':2 if Path('/sys/fs/cgroup/cgroup.controllers').exists() else 1}
 value={'account':{'name':a.account,'uid':int(runuser(a.account,['id','-u']).strip()),'gid':int(runuser(a.account,['id','-g']).strip())},'groups':runuser(a.account,['id','-nG']).split(),'uidMappings':uidmap,'gidMappings':gidmap,'helpers':helpers,'tools':tools,'platform':platform,'podman':{'rootless':host['security']['rootless'],'os':host['os'],'architecture':host['arch'],'namespaceUidMap':namespace_uid,'namespaceGidMap':namespace_gid},'runtime':{'version':runtimes[0] if runtimes else '', 'hostFxrCount':len(runtimes),'sdkCount':len(sdks),'measuredLocations':locations,'hiddenLocations':locations,'accessibleLocations':accessible},'resources':{'freeBytes':sv.f_bavail*sv.f_frsize,'requiredBytes':required,'freeInodes':sv.f_favail,'memoryBytes':memory,'stateDevice':a.state.stat().st_dev,'storageDevice':a.storage.stat().st_dev},'directories':[{'path':x,'uid':Path(x).stat().st_uid,'gid':Path(x).stat().st_gid,'mode':format(Path(x).stat().st_mode&0o777,'04o')} for x in REQUIRED_DIRS]}
 a.output.write_text(json.dumps(value,sort_keys=True,separators=(',',':'))+'\n')
if __name__=='__main__':
 try: main()
 except Exception as e: print('PORTABLE_PROVIDER_CAPABILITY_REFUSED',e); raise SystemExit(2)
