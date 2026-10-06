"""Closed diagnostic controls; native/network execution is forbidden."""
import contextlib,importlib.util,io,json,os,pathlib,subprocess,sys,tempfile,unittest
from unittest.mock import patch
import socket
sys.dont_write_bytecode=True
def load(name,path):
 s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);sys.modules[name]=m;s.loader.exec_module(m);return m
def forbidden(*args,**kwargs):raise AssertionError('native-network-forbidden')
subprocess.Popen=forbidden;subprocess.run=forbidden;subprocess.check_output=forbidden;subprocess.check_call=forbidden;os.system=forbidden;socket.socket.connect=forbidden;socket.create_connection=forbidden
c=load('collector_candidate',pathlib.Path(__file__).parent/'collect_provider_capability.py')
subprocess.Popen=forbidden;subprocess.run=forbidden
class Controls(unittest.TestCase):
 def provenance(self,text='podman version 4.9.3'):return c.podman_provenance(text,None)
 def test_unrecognized_version_never_publishes_arbitrary_text(self):
   value=self.provenance('PRIVATE-PATH SECRET TOKEN');self.assertIsNone(value['version']);self.assertNotIn('SECRET',json.dumps(value));self.assertEqual(value['versionObservation'],'unrecognized')
 def test_parent_override_key_presence_reads_no_values(self):
   class PresenceOnly(dict):
    def __getitem__(self,key):raise AssertionError('env-value-read')
   with patch.object(os,'environ',PresenceOnly(CONTAINER_HOST='SECRET',GH_TOKEN='SECRET',CONTAINERS_CONF='SECRET')):
    v=self.provenance();self.assertEqual(v['parentOverrideKeysPresent'],['CONTAINERS_CONF','CONTAINER_HOST']);self.assertNotIn('GH_TOKEN',json.dumps(v));self.assertNotIn('SECRET',json.dumps(v))
 def test_version_transport_exact_argv_timeout_fd_and_cap(self):
   seen={}
   def run(cmd,**kw):
    seen.update(command=cmd,timeout=kw['timeout'],stdout=kw['stdout'],stderr=kw['stderr']);kw['stdout'].write(b'podman version 4.9.3\n');kw['stdout'].flush()
    with patch.object(c.resource,'setrlimit',lambda *args:seen.update(limit=args)):kw['preexec_fn']()
    return type('R',(),dict(returncode=0))()
   with patch.object(c.subprocess,'run',run):self.assertEqual(c.podman_version_probe('p4executor'),'podman version 4.9.3')
   self.assertEqual(seen['command'],['/usr/sbin/runuser','--user','p4executor','--',*c.ACCOUNT_ENV,'podman','--version']);self.assertEqual(seen['timeout'],20);self.assertEqual(seen['limit'][1],(4097,4097));self.assertTrue(seen['stdout'].closed);self.assertTrue(seen['stderr'].closed)
 def test_version_oversize_and_child_exit_keep_private(self):
   def oversized(cmd,**kw):kw['stdout'].write(b'x'*4097);kw['stdout'].flush();return type('R',(),dict(returncode=0))()
   with patch.object(c.subprocess,'run',oversized):
    with self.assertRaisesRegex(ValueError,'response exceeded bound'):c.podman_version_probe('p4executor')
   def failed(cmd,**kw):kw['stderr'].write(b'PRIVATE');kw['stderr'].flush();return type('R',(),dict(returncode=125))()
   with patch.object(c.subprocess,'run',failed):
    with self.assertRaises(subprocess.CalledProcessError) as got:c.podman_version_probe('p4executor')
   self.assertEqual(got.exception.returncode,125);self.assertEqual(got.exception.stderr,b'PRIVATE')
 def test_numeric_version_and_unavailable_exit(self):
  self.assertEqual(self.provenance('podman version 5.4.2-ubuntu.1')['version'],'5.4.2')
  v=c.podman_provenance(None,subprocess.CalledProcessError(125,['SECRET']))
  self.assertEqual(v['versionExit'],125);self.assertIsNone(v['version']);self.assertNotIn('SECRET',json.dumps(v))
 def test_probe_timeout_preserves_info_failure(self):
  with tempfile.TemporaryDirectory() as td:
   root=pathlib.Path(td);locations=root/'locations';locations.write_text('/usr/share/dotnet/sdk\n')
   argv=['collector','--account','p4executor','--uid','12345','--state','/p4','--storage','/p4/runtime-v1/storage','--archive','/synthetic/image','--runtime','/synthetic/dotnet','--measured-locations',str(locations),'--podman-info',str(root/'info'),'--output',str(root/'output')]
   out=io.StringIO()
   with patch.object(sys,'argv',argv),patch.object(c,'podman_version_probe',side_effect=subprocess.TimeoutExpired('SECRET',20)),patch.object(c,'runuser',side_effect=subprocess.CalledProcessError(125,['podman'])),contextlib.redirect_stdout(out):self.assertEqual(c.entrypoint(),2)
   detail=json.loads(next(line.split('=',1)[1] for line in out.getvalue().splitlines() if line.startswith('PORTABLE_PROVIDER_CAPABILITY_FAILURE=')))
   self.assertEqual(detail['stage'],'podman-info');self.assertEqual(detail['exit'],125);self.assertNotIn('SECRET',out.getvalue())
if __name__=='__main__':unittest.main(verbosity=2)
