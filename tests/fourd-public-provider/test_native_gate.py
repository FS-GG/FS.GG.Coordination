"""A signal during acknowledgement must not release a waiting effect child."""
from pathlib import Path
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'eng/fourd-public-provider'))
import native_io as io


class GateTests(unittest.TestCase):
    def test_signal_during_acknowledgement_keeps_effect_behind_gate(self):
        with tempfile.TemporaryDirectory(prefix='native-gate-test-',dir='/tmp') as directory:
            root=Path(directory)
            class Client:
                cancelled=False
                def observe(self,kind,**facts):
                    if kind=='ack':self.cancelled=self.cancel_on_ack
                    return dict(eventAccepted=True)
            def execute(client,marker):
                scope=io.Effects()
                try:
                    return io.run_gated(scope,['/usr/bin/python3','-c',
                        'import pathlib,sys;pathlib.Path(sys.argv[1]).write_text("effect")',str(marker)],
                        client=client,operation='image',cwd=root,env=io.safe_environment(root),timeout=3,total_timeout=40)
                finally:self.assertTrue(scope.settle())
            baseline=Client();baseline.cancel_on_ack=False
            self.assertEqual(0,execute(baseline,root/'positive').returncode)
            self.assertTrue((root/'positive').is_file())
            cancelled=Client();cancelled.cancel_on_ack=True
            with self.assertRaisesRegex(io.NativeIOError,'native-cancelled-before-child-gate'):
                execute(cancelled,root/'forbidden')
            self.assertFalse((root/'forbidden').exists())


if __name__=='__main__':unittest.main()
