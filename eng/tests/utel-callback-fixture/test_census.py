#!/usr/bin/env python3
"""Pure preflight for the exact callback TRX census; launches no workload."""
import contextlib
import io
import json
import os
import pathlib
import runpy
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[3]
helper = runpy.run_path(str(ROOT / 'eng/utel-callback-fixture.py'), run_name='callback_census_test')
EXPECTED = json.loads((ROOT / 'eng/utel-callback-cases.json').read_bytes())['cases']


class CensusControls(unittest.TestCase):
    def check(self, names, outcomes=None):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / 'compiler-and-tests').mkdir()
            trx = ET.Element('TestRun')
            for name, outcome in zip(names, outcomes or ['Passed'] * len(names)):
                ET.SubElement(trx, 'UnitTestResult', testName=name, outcome=outcome)
            path = root / 'results.trx'
            ET.ElementTree(trx).write(path)
            with patch.dict(os.environ, {'RUNNER_TEMP': temporary}), contextlib.redirect_stdout(io.StringIO()):
                helper['census'](path)

    def test_exact_passing_cohort_is_accepted(self):
        self.check(EXPECTED)

    def test_empty_missing_duplicate_unknown_and_failed_results_refuse(self):
        for names, outcomes in [([], None), (EXPECTED[:-1], None), (EXPECTED + [EXPECTED[0]], None),
                                (EXPECTED[:-1] + ['different-case'], None),
                                (EXPECTED, ['Failed'] + ['Passed'] * (len(EXPECTED) - 1))]:
            with self.subTest(names=len(names), outcomes=outcomes):
                with self.assertRaises(RuntimeError): self.check(names, outcomes)


if __name__ == '__main__':
    unittest.main()
