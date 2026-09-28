#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('observer', ROOT / 'tools/v2-ci-ordinary-observe.py')
OBSERVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OBSERVER)
PROFILE = OBSERVER.QUALIFICATION.COORDINATION_SOURCE_PROFILE
NAMES = PROFILE['requiredGateChecks']


def rule(rule_id=21633423, names=NAMES, app=15368, source='FS-GG/FS.GG.Coordination', source_type='Repository'):
    return {'type': 'required_status_checks', 'ruleset_id': rule_id,
            'ruleset_source': source, 'ruleset_source_type': source_type,
            'parameters': {'required_status_checks': [
                {'context': name, 'integration_id': app} for name in names]}}


def classic(names=(), app=15368):
    return {'protected': True, 'protection': {'required_status_checks': {
        'checks': [{'context': name, 'app_id': app} for name in names],
        'contexts': list(names)}}}


class RulesetReaderTests(unittest.TestCase):
    def read(self, pages, branch=None, profile=None):
        calls = []
        def api(path):
            calls.append(path)
            if path.endswith('/branches/main'):
                return branch if branch is not None else classic()
            page = int(path.rsplit('page=', 1)[1])
            return pages[page - 1]
        with patch.object(OBSERVER, 'api', side_effect=api):
            result = OBSERVER.required_checks('FS-GG/FS.GG.Coordination',
                                              profile if profile is not None else PROFILE)
        return result, calls

    def test_ruleset_only_and_provenance(self):
        result, calls = self.read([[rule()]])
        self.assertEqual(set(NAMES), {c['context'] for c in result['checks']})
        self.assertTrue(all(c['sources'] == ['ruleset:21633423'] for c in result['checks']))
        self.assertEqual('repos/FS-GG/FS.GG.Coordination/rules/branches/main?per_page=100&page=1', calls[-1])

    def test_classic_only_and_mixed_union(self):
        profile = dict(PROFILE, requiredRulesetId=None)
        result, _ = self.read([[]], branch=classic(NAMES), profile=profile)
        self.assertTrue(all(c['sources'] == ['classic'] for c in result['checks']))
        self.assertEqual(NAMES, [c['context'] for c in result['classic']])
        result, _ = self.read([[rule(names=NAMES[3:])]], branch=classic(NAMES[:3]))
        self.assertEqual(set(NAMES), {c['context'] for c in result['checks']})
        self.assertEqual(set(NAMES[:3]), {c['context'] for c in result['classic']})
        result, _ = self.read([[rule()]], branch=classic(NAMES))
        self.assertTrue(all(len(c['sources']) == 2 for c in result['checks']))

    def test_organization_rule_and_multiple_pages(self):
        filler = [{'type': 'deletion', 'ruleset_id': n, 'ruleset_source': 'FS-GG',
                   'ruleset_source_type': 'Organization'} for n in range(1, 100)]
        result, calls = self.read([filler + [rule(99_999, names=NAMES[:3], source='FS-GG',
                                                source_type='Organization')],
                                   [rule(names=NAMES[3:])]])
        self.assertEqual(set(NAMES), {c['context'] for c in result['checks']})
        self.assertEqual(2, result['rulesetPages'][-1]['page'])
        self.assertTrue(calls[-1].endswith('page=2'))

    def test_conflict_duplicate_malformed_and_missing_page_refuse(self):
        cases = [
            ([[rule(app=1)]], classic(NAMES), 'conflicting'),
            ([[rule(names=NAMES + NAMES[:1])]], classic(), 'duplicate'),
            ([[rule(), rule()]], classic(), 'duplicate'),
            ([[rule()]], classic(NAMES[:1] + NAMES[:1]), 'duplicate'),
            ([[rule()]], dict(classic(), protection={'required_status_checks': {
                'checks': [], 'contexts': [NAMES[0]]}}), 'App-bound'),
            ([[rule()]], dict(classic(NAMES[:1]), protection={'required_status_checks': {
                'checks': [{'context': NAMES[0], 'app_id': 15368}],
                'contexts': [NAMES[0], NAMES[0]]}}), 'App-bound'),
            ([[rule(app=7)]], classic(), 'population'),
            ([[rule(names=NAMES + ['unexpected'])]], classic(), 'population'),
            ([[dict(rule(), parameters={'required_status_checks': [{'context': NAMES[0]}]})]],
             classic(), 'malformed'),
            ([[]], classic(), 'population'),
            ([[rule()]], dict(classic(), protected=False), 'protection'),
        ]
        for pages, branch, message in cases:
            with self.subTest(message=message), self.assertRaisesRegex(OBSERVER.QUALIFICATION.Refusal, message):
                self.read(pages, branch=branch)
        with patch.object(OBSERVER, 'api', side_effect=[classic(),
                [{'type': 'deletion', 'ruleset_id': i} for i in range(100)],
                OBSERVER.QUALIFICATION.Refusal('native page unavailable')]):
            with self.assertRaisesRegex(OBSERVER.QUALIFICATION.Refusal, 'native page unavailable'):
                OBSERVER.required_checks('FS-GG/FS.GG.Coordination', PROFILE)

    def test_installed_workflow_policy_and_public_anchor(self):
        workflow = (ROOT / '.github/workflows/v2-ci-ordinary-settlement.yml').read_text()
        policy = json.loads((ROOT / 'policy/v2-ci-ordinary-settlement.json').read_text())
        anchor = json.loads((ROOT / 'policy/v2-ci-ordinary-settlement-anchor.json').read_text())
        self.assertNotIn("if: ${{ github.event_name == 'pull_request' }}", workflow)
        self.assertIn('environment: ordinary-v2', workflow)
        self.assertIn('PACKAGE_VERSION: 0.1.7', workflow)
        self.assertIn('f0506cbd3bd8429d86cfcbdf5eb8c85f5cd229961cdc528014d712e3185c8c5c', workflow)
        self.assertIn('ordinary-settlement execute', workflow)
        self.assertTrue(policy['credentialJob']['installed'])
        self.assertEqual('published-verified', policy['packagePin']['status'])
        self.assertEqual(3, policy['credentialJob']['liveObservation']['secretCount'])
        self.assertEqual(21633423, policy['qualification']['requiredCheckAuthority']['rulesetId'])
        self.assertEqual(5064713, anchor['writer']['appId'])
        self.assertEqual(164553252, anchor['writer']['installationId'])
        self.assertEqual(0, len(anchor['rulesets']['integrity']['bypassActors']))
        for path, field in [('tools/v2-ci-ordinary-observe.py', 'observerSha256'),
                            ('tools/v2-ci-ordinary-qualification.py', 'qualificationSha256')]:
            import hashlib
            self.assertEqual(policy['sourceImplementation'][field],
                             hashlib.sha256((ROOT / path).read_bytes()).hexdigest())

    def test_installed_policy_is_admitted_before_runtime_event_fences(self):
        policy = json.loads((ROOT / 'policy/v2-ci-ordinary-settlement.json').read_text())
        env = {
            'FSGG_V2_SOURCE_PROFILE': 'coordination-v1',
            'GITHUB_SHA': 'a' * 40,
            'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Coordination',
            'GITHUB_EVENT_NAME': 'pull_request',
            'GITHUB_REF': 'refs/heads/main',
        }
        repository = {
            'id': 1346720714,
            'full_name': 'FS-GG/FS.GG.Coordination',
            'default_branch': 'main',
        }
        with patch.object(OBSERVER.QUALIFICATION, 'read_json', return_value=policy), \
                patch.object(OBSERVER, 'api', return_value=repository):
            with self.assertRaisesRegex(OBSERVER.QUALIFICATION.Refusal,
                                        'pinned protected-main event'):
                OBSERVER.observe(env)

    def test_non_boolean_activation_refuses(self):
        policy = json.loads((ROOT / 'policy/v2-ci-ordinary-settlement.json').read_text())
        policy['credentialJob']['installed'] = 'true'
        with patch.object(OBSERVER.QUALIFICATION, 'read_json', return_value=policy):
            with self.assertRaisesRegex(OBSERVER.QUALIFICATION.Refusal,
                                        'workflow or activation'):
                OBSERVER.observe({'FSGG_V2_SOURCE_PROFILE': 'coordination-v1'})

    def test_historical_false_activation_remains_a_valid_boolean(self):
        policy = json.loads((ROOT / 'policy/v2-ci-ordinary-settlement.json').read_text())
        policy['credentialJob']['installed'] = False
        env = {
            'FSGG_V2_SOURCE_PROFILE': 'coordination-v1',
            'GITHUB_SHA': 'a' * 40,
            'GITHUB_REPOSITORY': 'FS-GG/FS.GG.Coordination',
            'GITHUB_EVENT_NAME': 'pull_request',
            'GITHUB_REF': 'refs/heads/main',
        }
        repository = {
            'id': 1346720714,
            'full_name': 'FS-GG/FS.GG.Coordination',
            'default_branch': 'main',
        }
        with patch.object(OBSERVER.QUALIFICATION, 'read_json', return_value=policy), \
                patch.object(OBSERVER, 'api', return_value=repository):
            with self.assertRaisesRegex(OBSERVER.QUALIFICATION.Refusal,
                                        'pinned protected-main event'):
                OBSERVER.observe(env)


if __name__ == '__main__':
    unittest.main()
