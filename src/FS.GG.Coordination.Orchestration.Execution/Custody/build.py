#!/usr/bin/env python3
"""Build the fixed linux-x64 assembly resource; never invoked during preparation."""
import hashlib, json, os, pathlib, subprocess, tempfile
root=pathlib.Path(__file__).resolve().parent
if os.uname().machine!='x86_64':raise SystemExit('unsupported build architecture')
flags=['-static','-Os','-s','-fstack-protector-strong','-D_FORTIFY_SOURCE=3','-Wl,--build-id=none','-Wall','-Wextra','-Werror']
compiler='/usr/bin/gcc' if pathlib.Path('/usr/bin/gcc').exists() else '/usr/sbin/gcc'
with tempfile.TemporaryDirectory(prefix='fsgg-custody-build-') as temporary:
 binary=pathlib.Path(temporary)/'bootstrap'
 subprocess.run([compiler,*flags,str(root/'bootstrap.c'),'-o',str(binary)],check=True,timeout=30)
 headers=subprocess.check_output(['readelf','-l',str(binary)],text=True)
 if 'INTERP' in headers:raise SystemExit('static bootstrap required')
 payload=binary.read_bytes();bpf=subprocess.check_output([str(binary),'--export-filter'],timeout=2)
 source=(root/'bootstrap.c').read_bytes()
 manifest={'schema':'fsgg.custody-bootstrap/1','profile':'linux-x64-no-process-descendants/1','architecture':'x86_64','sourceSha256':hashlib.sha256(source).hexdigest(),'bootstrapSha256':hashlib.sha256(payload).hexdigest(),'filterSha256':hashlib.sha256(bpf).hexdigest(),'bootstrapBytes':len(payload),'filterBytes':len(bpf),'compiler':subprocess.check_output([compiler,'--version'],text=True).splitlines()[0],'flags':flags,'staticExecutable':True,'processPidfdFlags':0,'threadCloneRequired':'0x00010900','threadCloneAllowed':'0x013d0f00','processCreationAction':'KILL_PROCESS','clone3Action':'ENOSYS','ioUringAction':'ENOSYS'}
 (root/'bootstrap.linux-x64.bin').write_bytes(payload)
 (root/'filter.linux-x64.bpf').write_bytes(bpf)
 (root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
 print(json.dumps(manifest))
