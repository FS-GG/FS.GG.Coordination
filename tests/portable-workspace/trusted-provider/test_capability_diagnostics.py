"""Pure capability probe marker controls; no provider commands are launched."""
import sys
sys.dont_write_bytecode=True
import ast,importlib.util,io,re,tempfile,unittest,json,subprocess
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch
HERE=Path(__file__).parent
spec=importlib.util.spec_from_file_location('collector',HERE/'collect_provider_capability.py');c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
STAGES={'arguments','locations','podman-info','uid-map','gid-map','podman-shape','helper-hashes','git-version','tar-version','podman-version','runtime-list','sdk-list','sdk-probe','resource-read','output'}
class Controls(unittest.TestCase):
 def test_probe_markers_are_fixed_ascii_tokens(self):
  source=(HERE/'collect_provider_capability.py').read_text();tree=ast.parse(source)
  calls=[n for n in ast.walk(tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Name) and n.func.id=='stage']
  actual=set()
  for call in calls:
   value=call.args[0]
   if isinstance(value,ast.Constant):actual.add(value.value)
   else:
    self.assertIsInstance(value,ast.Subscript);self.assertIsInstance(value.value,ast.Dict);actual.update(x.value for x in value.value.values)
  self.assertEqual(actual,STAGES)
  output=io.StringIO()
  with redirect_stdout(output):
   for token in sorted(STAGES):c.stage(token)
  self.assertEqual(output.getvalue().splitlines(),['PORTABLE_PROVIDER_CAPABILITY_STAGE='+token for token in sorted(STAGES)]);output.getvalue().encode('ascii','strict')
 def test_first_failed_provider_probe_records_entered_stage_without_error(self):
  with tempfile.TemporaryDirectory() as td:
   root=Path(td);locations=root/'locations';locations.write_text('/opt/hostedtoolcache\n');output=io.StringIO()
   argv=['collector','--account','fixture','--uid','987654','--state',td,'--storage',td,'--archive',str(root/'archive'),'--runtime',str(root/'runtime'),'--measured-locations',str(locations),'--podman-info',str(root/'podman'),'--output',str(root/'output')]
   with patch.object(sys,'argv',argv),patch.object(c,'runuser',side_effect=ValueError('PRIVATE_SENTINEL /private/path')),redirect_stdout(output):
    with self.assertRaises(ValueError):c.main()
   self.assertEqual(output.getvalue().splitlines(),['PORTABLE_PROVIDER_CAPABILITY_STAGE=arguments','PORTABLE_PROVIDER_CAPABILITY_STAGE=locations','PORTABLE_PROVIDER_CAPABILITY_STAGE=podman-info']);self.assertNotIn('PRIVATE_SENTINEL',output.getvalue());self.assertNotIn('/private/path',output.getvalue());self.assertFalse((root/'output').exists())
 def test_actual_entrypoint_classifies_transport_failure_without_private_text(self):
  cases=[(subprocess.CalledProcessError(125,['PRIVATE_SENTINEL'],output='PRIVATE_SENTINEL',stderr='PRIVATE_SENTINEL'),'child-exit',125,None),
         (subprocess.TimeoutExpired('PRIVATE_SENTINEL',20,output='PRIVATE_SENTINEL'),'timeout',None,None),
         (FileNotFoundError(2,'PRIVATE_SENTINEL','/private/path'),'os-error',None,2),
         (json.JSONDecodeError('PRIVATE_SENTINEL','PRIVATE_SENTINEL',0),'json',None,None),
         (UnicodeDecodeError('utf8',b'PRIVATE_SENTINEL',0,1,'PRIVATE_SENTINEL'),'unicode',None,None),
         (KeyError('PRIVATE_SENTINEL'),'json-shape',None,None),
         (ValueError('PRIVATE_SENTINEL'),'value',None,None),
         (RuntimeError('PRIVATE_SENTINEL'),'internal',None,None)]
  for error,kind,code,errno in cases:
   with self.subTest(kind=kind),tempfile.TemporaryDirectory() as td:
    root=Path(td);locations=root/'locations';locations.write_text('/fixture/sdk\n');output=io.StringIO()
    argv=['collector','--account','fixture','--uid','987654','--state',td,'--storage',td,'--archive',str(root/'archive'),'--runtime',str(root/'runtime'),'--measured-locations',str(locations),'--podman-info',str(root/'podman'),'--output',str(root/'output')]
    with patch.object(sys,'argv',argv),patch.object(c.subprocess,'check_output',side_effect=error) as transport,redirect_stdout(output):
     self.assertEqual(c.entrypoint(),2)
    transport.assert_called_once();self.assertEqual(transport.call_args.kwargs,{'text':True,'timeout':20})
    lines=output.getvalue().splitlines();diagnostics=[x.split('=',1)[1] for x in lines if x.startswith('PORTABLE_PROVIDER_CAPABILITY_FAILURE=')]
    self.assertEqual(len(diagnostics),1);self.assertEqual(json.loads(diagnostics[0]),{'stage':'podman-info','kind':kind,'exit':code,'errno':errno})
    self.assertNotIn('PRIVATE_SENTINEL',output.getvalue());self.assertNotIn('/private/path',output.getvalue());self.assertFalse((root/'output').exists())
 def test_broken_stage_and_refusal_sinks_preserve_transport_failure_exit(self):
  class BrokenSink(io.StringIO):
   def write(self,value): raise BrokenPipeError('PRIVATE_SENTINEL')
   def flush(self): raise BrokenPipeError('PRIVATE_SENTINEL')
  with tempfile.TemporaryDirectory() as td:
   root=Path(td);locations=root/'locations';locations.write_text('/fixture/sdk\n')
   argv=['collector','--account','fixture','--uid','987654','--state',td,'--storage',td,'--archive',str(root/'archive'),'--runtime',str(root/'runtime'),'--measured-locations',str(locations),'--podman-info',str(root/'podman'),'--output',str(root/'output')]
   error=subprocess.CalledProcessError(125,['PRIVATE_SENTINEL'])
   with patch.object(sys,'argv',argv),patch.object(c.subprocess,'check_output',side_effect=error) as transport,patch.object(c,'failure_diagnostic',wraps=c.failure_diagnostic) as classify,redirect_stdout(BrokenSink()):
    self.assertEqual(c.entrypoint(),2)
   transport.assert_called_once();classify.assert_called_once_with(error)
 def test_diagnostic_failure_preserves_original_refusal_exit(self):
  output=io.StringIO()
  with patch.object(c,'main',side_effect=RuntimeError('PRIVATE_SENTINEL')),patch.object(c,'failure_diagnostic',side_effect=RuntimeError('PRIVATE_SENTINEL')),redirect_stdout(output):
   self.assertEqual(c.entrypoint(),2)
  self.assertNotIn('PRIVATE_SENTINEL',output.getvalue())
  self.assertEqual(json.loads(output.getvalue().splitlines()[0].split('=',1)[1]),{'stage':'unavailable','kind':'unavailable','exit':None,'errno':None})
 def test_diagnostic_integer_bounds_and_unknown_stage(self):
  for code in [True,256,-256,'PRIVATE_SENTINEL']:
   self.assertIsNone(c.failure_diagnostic(subprocess.CalledProcessError(code,'PRIVATE_SENTINEL'))['exit'])
  c._last_stage='PRIVATE_SENTINEL'
  self.assertEqual(c.failure_diagnostic(RuntimeError('PRIVATE_SENTINEL'))['stage'],'unavailable')
  for errno in [True,0,4096]:
   error=OSError();error.errno=errno
   self.assertIsNone(c.failure_diagnostic(error)['errno'])
 def test_location_refusal_precedes_any_provider_call(self):
  with tempfile.TemporaryDirectory() as td:
   root=Path(td);locations=root/'locations';locations.write_text('relative/path\n');output=io.StringIO()
   argv=['collector','--account','fixture','--uid','987654','--state',td,'--storage',td,'--archive',str(root/'archive'),'--runtime',str(root/'runtime'),'--measured-locations',str(locations),'--podman-info',str(root/'podman'),'--output',str(root/'output')]
   with patch.object(sys,'argv',argv),patch.object(c,'runuser',side_effect=AssertionError('provider forbidden')) as run,redirect_stdout(output):
    with self.assertRaises(ValueError):c.main()
   run.assert_not_called();self.assertEqual(output.getvalue().splitlines()[-1],'PORTABLE_PROVIDER_CAPABILITY_STAGE=locations')
 def test_fresh_manifest_generation_and_native_refusal_fixture_stay_joined(self):
  root=HERE.parents[2]
  helper=(root/'eng/portable-p4-input-manifest.fsx').read_text()
  fixture=(HERE/'test_private_input_manifest.fsx').read_text()
  selected='portable-p4-python-private-inputs-20261005-root-runtime-e8eb322'
  consumed='portable-p4-python-private-inputs-20261005-capability-h-dc934643'
  pins=re.findall(r'^let releaseTag = "([^"\n]+)"$',helper,re.M)
  self.assertEqual(pins,[selected])
  self.assertIn('text "tag" release = releaseTag',helper)
  self.assertNotIn(consumed,helper)
  self.assertIn('"tag",JsonValue.Create("'+selected+'")',fixture)
  refusal_loop=fixture[fixture.index('for index,tag in ['):fixture.index('let body=')]
  self.assertIn('"'+consumed+'"',refusal_loop)
  self.assertIn('run "construct" wrongPath',refusal_loop)
  self.assertIn('assertTrue (refusedCode<>0)',refusal_loop)
if __name__=='__main__':unittest.main()
