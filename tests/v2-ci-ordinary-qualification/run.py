#!/usr/bin/env python3
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
POLICY_PATH = ROOT / 'policy/v2-ci-ordinary-settlement.json'
SPEC = importlib.util.spec_from_file_location('qualifier', ROOT / 'tools/v2-ci-ordinary-qualification.py')
Q = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(Q)
SOURCE = 'a' * 40
HEAD = 'b' * 40


class CoordinationQualifierTests(unittest.TestCase):
    def setUp(self):
        self.policy = json.loads(POLICY_PATH.read_text())
        self.digest = hashlib.sha256(POLICY_PATH.read_bytes()).hexdigest()
        self.profile = Q.COORDINATION_SOURCE_PROFILE
        self.runtime = {
            'schema': 'fsgg.github.v2-ci-runtime/1', 'eventName': 'push',
            'sourceProfile': 'coordination-v1', 'repository': 'FS-GG/FS.GG.Coordination',
            'repositoryId': 1346720714, 'ref': 'refs/heads/main', 'eventAfter': SOURCE,
            'sourceSha': SOURCE, 'workflowPath': '.github/workflows/v2-ci-ordinary-settlement.yml',
            'workflowRevision': SOURCE, 'environment': 'ordinary-v2',
            'runner': 'github-hosted-ephemeral', 'operationClass': 'ordinary-post-merge-delivery-settlement',
            'phase': 'secret-free-predecessor', 'credentialAccess': False,
        }
        self.pulls = [{
            'state': 'closed', 'merged_at': '2026-09-28T12:00:00Z',
            'merge_commit_sha': SOURCE, 'number': 876, 'node_id': 'PR_coord_876',
            'head': {'sha': HEAD}, 'base': {'ref': 'main', 'sha': 'c' * 40,
                                           'repo': {'full_name': 'FS-GG/FS.GG.Coordination'}},
        }]
        def check(name, index):
            producer = self.profile['checkProducers'][name]
            return {'name': name, 'conclusion': 'success', 'sourceSha': HEAD,
                    'appId': 15368, 'checkRunId': index + 1,
                    'workflowRunId': index + 10, 'runAttempt': 1,
                    'checkSuiteId': index + 20,
                    'workflowId': producer['workflowId'], 'workflowPath': producer['path']}
        self.evidence = {
            'schema': 'fsgg.github.v2-ci-qualification-evidence/1', 'status': 'passed',
            'subject': {'sourceSha': SOURCE, 'qualificationSha': HEAD,
                        'workflowPath': self.runtime['workflowPath'], 'workflowRevision': SOURCE,
                        'environment': 'ordinary-v2',
                        'operationClass': self.runtime['operationClass'], 'policySha256': self.digest},
            'checks': [check(name, i) for i, name in enumerate(self.profile['requiredChecks'])],
            'gateChecks': [check(name, i) for i, name in enumerate(self.profile['requiredGateChecks'])],
        }

    def qualify(self, runtime=None, pulls=None, evidence=None):
        return Q.qualify(self.policy, self.digest, self.runtime if runtime is None else runtime,
                         self.pulls if pulls is None else pulls,
                         self.evidence if evidence is None else evidence, 'coordination-v1')

    def test_disabled_receipt_binds_all_six_native_checks_and_exact_head(self):
        receipt = self.qualify()
        self.assertEqual('coordination-v1', receipt['sourceProfile'])
        self.assertEqual(1346720714, receipt['sourceRepositoryId'])
        self.assertEqual(HEAD, receipt['qualificationSha'])
        self.assertEqual(6, len(receipt['requiredChecks']))
        self.assertEqual(6, len(receipt['requiredGateChecks']))
        self.assertFalse(receipt['credentialAccess'])
        self.assertEqual({343352087}, {c['workflowId'] for c in receipt['requiredGateChecks']})

    def test_failed_skipped_missing_foreign_or_stale_check_refuses(self):
        for field, key, value in [('gateChecks', 'conclusion', 'failure'),
                                  ('gateChecks', 'conclusion', 'skipped'),
                                  ('checks', 'appId', 0),
                                  ('checks', 'sourceSha', SOURCE),
                                  ('checks', 'workflowId', 1)]:
            with self.subTest(field=field, key=key, value=value):
                evidence = copy.deepcopy(self.evidence)
                evidence[field][0][key] = value
                with self.assertRaises(Q.Refusal):
                    self.qualify(evidence=evidence)
        evidence = copy.deepcopy(self.evidence)
        evidence['gateChecks'].pop()
        with self.assertRaisesRegex(Q.Refusal, 'population'):
            self.qualify(evidence=evidence)

    def test_wrong_event_association_policy_and_reuse_claim_refuse(self):
        runtime = dict(self.runtime, eventName='workflow_dispatch')
        with self.assertRaisesRegex(Q.Refusal, 'wrong event'):
            self.qualify(runtime=runtime)
        with self.assertRaisesRegex(Q.Refusal, 'ambiguous merge association'):
            self.qualify(pulls=[])
        evidence = copy.deepcopy(self.evidence)
        evidence['subject']['policySha256'] = '0' * 64
        with self.assertRaisesRegex(Q.Refusal, 'stale or mismatched'):
            self.qualify(evidence=evidence)
        # A claimed reuse disposition is not a successful native check. The
        # later installed handoff must validate its exact-head witnesses.
        evidence = copy.deepcopy(self.evidence)
        evidence['gateChecks'][0]['conclusion'] = 'skipped'
        evidence['reuseDisposition'] = 'reused'
        evidence['reuseHeadSha'] = HEAD
        with self.assertRaisesRegex(Q.Refusal, 'failed or stale'):
            self.qualify(evidence=evidence)


if __name__ == '__main__':
    unittest.main()
