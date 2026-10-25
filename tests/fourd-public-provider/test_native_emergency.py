"""Original cleanup windows refuse exhausted fallback without another child."""
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'eng/fourd-public-provider'))
import native_emergency as emergency
from native_io import capsules


class EmergencyTests(unittest.TestCase):
    def expired(self,mode,elapsed,end):
        with tempfile.TemporaryDirectory(prefix='native-emergency-test-',dir='/tmp') as directory:
            root=Path(directory);work=root/'control';work.mkdir(mode=0o700)
            session=work/'session.json'
            session.write_bytes(capsules.canonical(dict(executable='/unselected/FourD.Typed',host='/unselected/dotnet',
                mode=mode,started=time.monotonic()-elapsed,startedUnix=1800000000,history=[],
                response=dict(state=dict(phase='cleanup',operationDeadlines=dict(cleanup=end))),config={})))
            evidence=root/'emergency.json'
            with mock.patch.object(emergency,'Effects',side_effect=AssertionError('another cleanup scope was started')):
                self.assertEqual(2,emergency.retire(session,evidence))
            value=json.loads(evidence.read_bytes())
            self.assertTrue(value['cleanupBudgetExpired'])
            self.assertEqual(['cleanup-budget-exhausted'],value['failures'])

    def test_spent_480_seconds_is_not_renewed_inside_full_5400_window(self):
        self.expired('full',600,480)

    def test_capability_1020_cleanup_end_is_not_renewed_to_1200(self):
        self.expired('capability',1100,1020)


if __name__=='__main__':unittest.main()
