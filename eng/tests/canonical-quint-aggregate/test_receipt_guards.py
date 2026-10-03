#!/usr/bin/env python3
"""Evaluate production receipt predicates; never aggregate or run a model."""
import copy
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SOURCE = (ROOT / 'eng/bootstrap-gates/canonical-quint-aggregate.sh').read_text()
FIXTURES = Path(__file__).resolve().parent / 'fixtures'
BASE = json.loads((FIXTURES / 'native-base-q1q2-37148499631.json').read_text())
SHARD = json.loads((FIXTURES / 'native-claim-election-37148499631.json').read_text())
BASE_PREDICATE = re.search(r"jq -e '(.*?)' \"\$base\"", SOURCE, re.S).group(1)
SHARD_PREDICATE = re.search(r"jq -e --arg id \"\$id\" '(.*?)' \"\$receipt\"", SOURCE, re.S).group(1)


def evaluate(expression, document, shard=False):
    argv = ['jq', '-e'] + (['--arg', 'id', 'claim-election'] if shard else []) + [expression]
    return subprocess.run(argv, input=json.dumps(document), text=True, capture_output=True).returncode


class ReceiptGuards(unittest.TestCase):
    def test_original_native_receipts_pass_pure_predicates(self):
        self.assertEqual(BASE['negativeControlCount'], 79)
        self.assertEqual(evaluate(BASE_PREDICATE, BASE), 0)
        self.assertEqual(evaluate(SHARD_PREDICATE, SHARD, True), 0)

    def test_base_refuses_old_count_missing_control_and_bad_accounting(self):
        for name, mutate in [
            ('old-71', lambda value: value.update(negativeControlCount=71)),
            ('missing-control', lambda value: value.update(negativeControlCount=78)),
            ('missing-field', lambda value: value.pop('negativeControlCount')),
            ('physical-accounting', lambda value: value['physicalProcessCounts'].update(external=130)),
            ('retry-classification', lambda value: value['startupRetries'].update(earlyLifecycleExit=0)),
        ]:
            with self.subTest(name=name):
                value = copy.deepcopy(BASE)
                mutate(value)
                self.assertNotEqual(evaluate(BASE_PREDICATE, value), 0)

    def test_shard_refuses_control_inventory_and_accounting_drift(self):
        for name, mutate in [
            ('missing-control', lambda value: value.update(negativeControlCount=4)),
            ('missing-count', lambda value: value.pop('negativeControlCount')),
            ('logical-accounting', lambda value: value['processCounts'].update(external=10)),
            ('physical-accounting', lambda value: value['executedProcessCounts'].update(external=7)),
            ('wrong-shard', lambda value: value.update(id='drift')),
        ]:
            with self.subTest(name=name):
                value = copy.deepcopy(SHARD)
                mutate(value)
                self.assertNotEqual(evaluate(SHARD_PREDICATE, value, True), 0)

    def test_actual_missing_shard_file_gate(self):
        self.assertIn('  test -f "$receipt"\n', SOURCE)
        gate = 'test -f "$receipt"'
        with tempfile.TemporaryDirectory(prefix='fsgg-shard-file-') as directory:
            path = Path(directory) / 'claim-election.json'
            for present in [False, True]:
                if present:
                    path.write_text(json.dumps(SHARD))
                result = subprocess.run(['bash', '-euo', 'pipefail', '-c',
                                         'receipt="$1"\n' + gate, 'guard', str(path)])
                self.assertEqual(result.returncode == 0, present)

    def test_exact_nineteen_shard_roster_gate(self):
        roster = re.search(r'(mapfile -t expected .*?test "\$\{#expected\[@\]\}" -eq 19)',
                           SOURCE, re.S).group(1)
        configuration = json.loads((ROOT / 'eng/quint-qualification.json').read_text())
        with tempfile.TemporaryDirectory(prefix='fsgg-shard-roster-') as directory:
            scratch = Path(directory)
            (scratch / 'eng').mkdir()
            for complete in [True, False]:
                value = copy.deepcopy(configuration)
                if not complete:
                    value['formalTests'].pop()
                (scratch / 'eng/quint-qualification.json').write_text(json.dumps(value))
                result = subprocess.run(['bash', '-euo', 'pipefail', '-c', roster], cwd=scratch)
                self.assertEqual(result.returncode == 0, complete)


if __name__ == '__main__':
    unittest.main()
