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
 fake_source=(root/'native-collector-fake.h').read_bytes()
 fake_filter=subprocess.check_output([str(binary),'--export-native-collector-fake-filter'],timeout=2)
 if hashlib.sha256(fake_filter).hexdigest()!='8924388729dfd5a649a6d4218ad83c8a9bcc07361c50ec6829a32b783808593a':raise SystemExit('fixed fake filter differs from selected workload profile')
 manifest={'schema':'fsgg.custody-bootstrap/1','profile':'linux-x64-no-process-descendants/1','architecture':'x86_64','sourceSha256':hashlib.sha256(source).hexdigest(),'bootstrapSha256':hashlib.sha256(payload).hexdigest(),'filterSha256':hashlib.sha256(bpf).hexdigest(),'bootstrapBytes':len(payload),'filterBytes':len(bpf),'compiler':subprocess.check_output([compiler,'--version'],text=True).splitlines()[0],'flags':flags,'staticExecutable':True,'processPidfdFlags':0,'threadCloneRequired':'0x00010900','threadCloneAllowed':'0x013d0f00','processCreationAction':'KILL_PROCESS','clone3Action':'ENOSYS','ioUringAction':'ENOSYS'}
 manifest['nativeCollectorFake']={'mode':'--native-collector-fake-v1','profile':'untrusted-workload-userns-seccomp-v1','headerSha256':hashlib.sha256(fake_source).hexdigest(),'filterSha256':hashlib.sha256(fake_filter).hexdigest(),'filterBytes':len(fake_filter),'hostAdmissionAvailable':False,'nativeQualificationRecorded':False,'appStdin':'closed','appStderr':'closed','statusFd':2,'blockFd':0,'filterFd':3}
 (root/'bootstrap.linux-x64.bin').write_bytes(payload)
 (root/'filter.linux-x64.bpf').write_bytes(bpf)
 (root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
 print(json.dumps(manifest))
