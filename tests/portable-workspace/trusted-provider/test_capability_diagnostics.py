"""Pure capability probe marker controls; no provider commands are launched."""
import sys
sys.dont_write_bytecode=True
import ast,importlib.util,io,tempfile,unittest
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
 def test_location_refusal_precedes_any_provider_call(self):
  with tempfile.TemporaryDirectory() as td:
   root=Path(td);locations=root/'locations';locations.write_text('relative/path\n');output=io.StringIO()
   argv=['collector','--account','fixture','--uid','987654','--state',td,'--storage',td,'--archive',str(root/'archive'),'--runtime',str(root/'runtime'),'--measured-locations',str(locations),'--podman-info',str(root/'podman'),'--output',str(root/'output')]
   with patch.object(sys,'argv',argv),patch.object(c,'runuser',side_effect=AssertionError('provider forbidden')) as run,redirect_stdout(output):
    with self.assertRaises(ValueError):c.main()
   run.assert_not_called();self.assertEqual(output.getvalue().splitlines()[-1],'PORTABLE_PROVIDER_CAPABILITY_STAGE=locations')
if __name__=='__main__':unittest.main()
