#!/usr/bin/env python3
"""Evaluate production receipt predicates; never aggregate or run a model."""
import copy
import importlib.util
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
BASE_PREDICATE = re.search(r"jq -e --argjson expected [^\n]+ '\n(.*?)' \"\$base\"", SOURCE, re.S).group(1)
HISTORICAL_BASE_PREDICATE = (FIXTURES / 'historical-base-predicate.jq').read_text()
SHARD_PREDICATE = re.search(r"jq -e --arg id \"\$id\" '(.*?)' \"\$receipt\"", SOURCE, re.S).group(1)
spec = importlib.util.spec_from_file_location('accounting', ROOT / 'eng/check-canonical-quint-accounting.py')
ACCOUNTING = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ACCOUNTING)
VALIDATOR = (ROOT / 'eng/validate-canonical-quint-protocol.fsx').read_text()
CONFIGURATION = json.loads((ROOT / 'eng/quint-qualification.json').read_text())
PLAN = ACCOUNTING.accounting(VALIDATOR, len(CONFIGURATION['formalTests']), len(CONFIGURATION['roots']))
# Synthetic CURRENT predicate input; the retained native original above stays unchanged.
CURRENT_BASE = copy.deepcopy(BASE)
CURRENT_BASE['negativeControlCount'] = PLAN['baseRejections']
CURRENT_BASE['processCounts'] = dict(external=PLAN['baseExternal'], quintCli=PLAN['baseQuint'], apalacheVerify=PLAN['baseVerify'])
CURRENT_BASE['physicalProcessCounts'] = dict(
    external=PLAN['baseExternal'] + BASE['startupRetries']['total'],
    quintCli=PLAN['baseQuint'] + BASE['startupRetries']['total'],
    apalacheVerify=PLAN['baseVerify'] + BASE['startupRetries']['verify'])


def evaluate(expression, document, shard=False):
    argv = ['jq', '-e', '--argjson', 'expected', json.dumps(PLAN)] + (['--arg', 'id', 'claim-election'] if shard else []) + [expression]
    return subprocess.run(argv, input=json.dumps(document), text=True, capture_output=True).returncode


class ReceiptGuards(unittest.TestCase):
    def test_original_native_receipts_pass_pure_predicates(self):
        self.assertEqual(BASE['negativeControlCount'], 79)
        self.assertEqual(evaluate(HISTORICAL_BASE_PREDICATE, BASE), 0)
        self.assertEqual(evaluate(SHARD_PREDICATE, SHARD, True), 0)

    def test_current_source_plan_and_synthetic_predicate_input(self):
        self.assertEqual((PLAN['roots'], PLAN['preflightMutants'], PLAN['formalTests']), (9, 21, 19))
        self.assertEqual((PLAN['totalRejections'], PLAN['external'], PLAN['quint'], PLAN['verify']), (187, 289, 254, 71))
        self.assertEqual(evaluate(BASE_PREDICATE, CURRENT_BASE), 0)
        self.assertNotEqual(evaluate(BASE_PREDICATE, BASE), 0)
        self.assertEqual(ACCOUNTING.self_test(VALIDATOR, 19, 9), 9)
        ACCOUNTING.verify_consumers(ROOT, PLAN)

    def test_current_typed_consumers_refuse_stale_cardinalities(self):
        production = 'src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs'
        fixture = 'tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs'
        originals = {path: (ROOT / path).read_text() for path in [production, fixture]}
        changes = [
            (production, 'passed|passed|8|187|289|254|71|', 'passed|passed|8|174|263|232|71|'),
            (production, 'Some 187L', 'Some 174L'),
            (production, 'Some 289L', 'Some 263L'),
            (production, 'Some(289L + startupRetryCount)', 'Some(263L + startupRetryCount)'),
            (fixture, 'passed|passed|8|187|289|254|71|', 'passed|passed|8|174|263|232|71|'),
        ]
        with tempfile.TemporaryDirectory(prefix='fsgg-accounting-consumer-') as directory:
            root = Path(directory)
            for path, before, after in changes:
                with self.subTest(path=path, mutation=before):
                    for name, text in originals.items():
                        target = root / name
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_text(text)
                    self.assertIn(before, originals[path])
                    (root / path).write_text(originals[path].replace(before, after))
                    with self.assertRaises(ValueError):
                        ACCOUNTING.verify_consumers(root, PLAN)

    def test_pure_guard_precedes_native_and_has_reuse_closure(self):
        entry = (ROOT / 'eng/bootstrap-gates/canonical-quint.sh').read_text()
        self.assertLess(entry.index('check-canonical-quint-accounting.py'), entry.index('dotnet restore'))
        paths = {'eng/check-canonical-quint-accounting.py',
                 'src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs',
                 'src/FS.GG.Coordination.Qualification.Contracts/CanonicalProtocolSourceIdentity.fs',
                 'tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs'}
        formal = json.loads((ROOT / 'eng/bootstrap-qualification-plan.json').read_text())
        offline = json.loads((ROOT / 'eng/offline-formal-pilot.json').read_text())
        self.assertTrue(paths <= set(formal['formalReuse']['exactPaths']))
        self.assertTrue(paths <= set(offline['boundPaths']))
        self.assertEqual(offline['boundPaths'], sorted(set(offline['boundPaths'])))
        harness = (ROOT / 'eng/bootstrap-gates/optimistic-prepare.sh').read_text().split('--harness ', 1)[1].split('\n', 1)[0]
        for path in paths:
            self.assertIn(path, harness)

    def test_base_refuses_old_count_missing_control_and_bad_accounting(self):
        for name, mutate in [
            ('old-71', lambda value: value.update(negativeControlCount=71)),
            ('historical-79', lambda value: value.update(negativeControlCount=79)),
            ('old-eleven-mutants', lambda value: value.update(negativeControlCount=82)),
            ('missing-control', lambda value: value.update(negativeControlCount=78)),
            ('missing-field', lambda value: value.pop('negativeControlCount')),
            ('physical-accounting', lambda value: value['physicalProcessCounts'].update(external=130)),
            ('retry-classification', lambda value: value['startupRetries'].update(earlyLifecycleExit=0)),
        ]:
            with self.subTest(name=name):
                value = copy.deepcopy(CURRENT_BASE)
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
