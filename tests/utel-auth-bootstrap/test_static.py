#!/usr/bin/env python3
"""Cheap source controls; never compile or execute the native candidate locally."""

from __future__ import annotations

import ast
import os
import pathlib
import re
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/FS.GG.Coordination.Orchestration.Execution/Custody/utel-auth-bootstrap.c"
BUILDER = ROOT / "src/FS.GG.Coordination.Orchestration.Execution/Custody/build-utel-auth.py"
WORKFLOW = ROOT / ".github/workflows/utel-auth-bootstrap-qualification.yml"
FALSE_FIELDS = (
    "productionAvailable", "credentialIngressAvailable", "sameTokenCleanupAvailable",
    "nativeConstructorAvailable", "authorized",
)


def check_c(text: str) -> None:
    for field in FALSE_FIELDS:
        assert text.count(f'\\"{field}\\":false') == 2, field
        assert f'\\"{field}\\":true' not in text, field
    for value in (
        'fsgg.utel.auth-bootstrap.inert/1', 'linux-x64-utel-auth-inert-source/1',
        'utel-rel-21/original-tag-authentication-only/2',
        'strcmp(argv[1], "--describe")', 'strcmp(argv[1], "--inert-fixture")',
        'prefix[3] != FIXTURE_BYTES', 'exact_eof(3, deadline)', 'S_ISFIFO(source.st_mode)',
        'S_ISFIFO(sink.st_mode)', 'no_extra_descriptors()', 'CLOCK_MONOTONIC', 'SIGUSR1',
    ):
        assert value in text, value
    for forbidden in ('execve(', 'fork(', 'socket(', 'getenv(', 'open(', 'system('):
        assert forbidden not in text, forbidden


def check_workflow(text: str) -> None:
    assert re.search(r'(?m)^permissions:\s*\n  contents: read\s*$', text)
    assert not re.search(r'(?m)^\s*(id-token|packages|actions):', text)
    for forbidden in ('workflow_dispatch:', 'secrets.', 'environment:', 'gh release',
                      'npm publish', 'dotnet pack', 'GITHUB_TOKEN', 'pull_request_target:'):
        assert forbidden not in text, forbidden
    for value in ('persist-credentials: false', 'test_runtime.py',
                  'if: always()', 'if-no-files-found: warn',
                  'github.event.pull_request.head.sha || github.sha'):
        assert value in text, value


class StaticControls(unittest.TestCase):
    def test_c_closed_inert_source(self) -> None:
        check_c(SOURCE.read_text())

    def test_c_false_to_true_mutant_refused(self) -> None:
        source = SOURCE.read_text()
        for field in FALSE_FIELDS:
            with self.subTest(field=field), self.assertRaises(AssertionError):
                check_c(source.replace(f'\\"{field}\\":false', f'\\"{field}\\":true', 1))

    def test_c_unsupported_mode_mutant_refused(self) -> None:
        with self.assertRaises(AssertionError):
            check_c(SOURCE.read_text().replace('"--inert-fixture"', '"--run"'))

    def test_python_source_parses(self) -> None:
        for path in (BUILDER, ROOT / 'tests/utel-auth-bootstrap/test_runtime.py'):
            ast.parse(path.read_text(), filename=str(path))

    def test_workflow_read_only(self) -> None:
        check_workflow(WORKFLOW.read_text())

    def test_workflow_oidc_mutant_refused(self) -> None:
        with self.assertRaises(AssertionError):
            check_workflow(WORKFLOW.read_text().replace('  contents: read',
                                                     '  contents: read\n  id-token: write', 1))

    def test_builder_fresh_directory_refusal_before_compiler(self) -> None:
        import importlib.util
        spec = importlib.util.spec_from_file_location('utel_auth_builder', BUILDER)
        assert spec and spec.loader
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        with tempfile.TemporaryDirectory(dir=os.environ.get('RUNNER_TEMP')) as directory:
            existing = pathlib.Path(directory)
            with self.assertRaises(SystemExit) as refusal:
                module.build(existing, head, 1, 1)
            self.assertIn('fresh directory', str(refusal.exception))
            linked = existing.with_name(existing.name + '-linked')
            linked.symlink_to(existing)
            try:
                with self.assertRaises(SystemExit):
                    module.build(linked, head, 1, 1)
            finally:
                linked.unlink()
        if os.environ.get('RUNNER_TEMP'):
            with tempfile.TemporaryDirectory() as directory:
                outside = pathlib.Path(directory) / 'candidate'
                if outside.parent.resolve() != pathlib.Path(os.environ['RUNNER_TEMP']).resolve():
                    with self.assertRaises(SystemExit) as refusal:
                        module.build(outside, head, 1, 1)
                    self.assertIn('runner temporary root', str(refusal.exception))


if __name__ == '__main__':
    unittest.main(verbosity=2)
