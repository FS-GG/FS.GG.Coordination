"""Qualification for the source-only installed runtime composition."""

import dataclasses
import hashlib
import importlib.util
import pathlib
import sys
import unittest
from unittest import mock


ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))

import callable_isolated_v2_installed_composer as installed
import callable_isolated_v2_runtime_host as host_loader


def _module(name, relative):
    spec = importlib.util.spec_from_file_location(name, ENG / relative)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


CANDIDATE_FIXTURE = _module(
    "installed_candidate_fixtures",
    "tests/callable-isolated-v2-runtime-candidate-read/"
    "test_runtime_candidate_read_adapter.py")
HOST_FIXTURE = _module(
    "installed_host_fixtures",
    "tests/callable-isolated-v2-runtime-host/test_runtime_host.py")


class InstalledHost:
    def __init__(self):
        selection, workflow, reader, downloader = CANDIDATE_FIXTURE.fixture()
        bundle = downloader.raw
        archive, manifest = installed.candidate._read_bundle(bundle)
        sha = lambda raw: hashlib.sha256(raw).hexdigest()
        expected = host_loader.ExpectedRuntime(
            installed.candidate.REPOSITORY, selection.repository_id,
            selection.source_sha, selection.source_tree,
            selection.workflow_sha256, selection.run_id,
            selection.run_attempt, selection.artifact_id, sha(bundle),
            sha(archive), sha(manifest), 201, 1)
        witness = installed.candidate.CandidateWitness(
            installed.candidate.REPOSITORY, selection.repository_id,
            selection.source_sha, selection.source_tree,
            selection.workflow_sha256, selection.run_id,
            selection.run_attempt, selection.artifact_id, sha(bundle),
            sha(archive), sha(manifest))
        self.selection = selection
        self.workflow = workflow
        self.reader = reader
        self.downloader = downloader
        self.expected = expected
        self.witness = witness
        self.bundle = bundle
        self.authority = HOST_FIXTURE.ConfigurationAuthority(expected)
        self.calls = []

    def expected_runtime(self):
        self.calls.append("expected")
        return self.expected

    def candidate_selection(self):
        self.calls.append("selection")
        return self.selection

    def candidate_observation_time(self):
        self.calls.append("observation-time")
        return CANDIDATE_FIXTURE.s5.NOW

    def installed_workflow_bytes(self):
        self.calls.append("workflow")
        return self.workflow

    def candidate_read_transport(self):
        self.calls.append("candidate-read")
        return self.reader

    def candidate_download_transport(self):
        self.calls.append("candidate-download")
        return self.downloader

    def read_candidate_bundle(self, artifact_id):
        self.calls.append(("bundle", artifact_id))
        return self.bundle

    def execution_run(self):
        self.calls.append("execution")
        return self.expected.execution_run_id, self.expected.execution_run_attempt

    def protected_authority(self):
        self.calls.append("authority")
        return self.authority


class InstalledComposerTests(unittest.TestCase):
    def test_native_candidate_reaches_exact_protected_host_boundary(self):
        host = InstalledHost()
        marker = object()
        with mock.patch.object(installed.runtime_host, "run",
                               return_value=marker) as delegated:
            result = installed.run(host)
        self.assertIs(result, marker)
        delegated.assert_called_once_with(host, host.witness, recovery=False)
        self.assertEqual(host.downloader.calls,
                         [(104, host.downloader.calls[0][1])])
        self.assertNotIn(("bundle", 104), host.calls)
        self.assertNotIn("authority", host.calls)
        self.assertEqual(host.authority.calls, [])

    def test_host_selection_mismatch_refuses_before_native_reads(self):
        changes = (
            {"source_sha": "f" * 40},
            {"source_tree": "f" * 40},
            {"run_id": 999},
            {"run_attempt": 2},
            {"artifact_id": 999},
            {"workflow_sha256": "f" * 64},
        )
        for change in changes:
            with self.subTest(change=change):
                host = InstalledHost()
                host.selection = dataclasses.replace(host.selection, **change)
                with self.assertRaisesRegex(
                        installed.Refused, "installed-candidate-selection"):
                    installed.run(host)
                self.assertEqual(host.reader.calls, [])
                self.assertEqual(host.downloader.calls, [])
                self.assertEqual(host.authority.calls, [])

    def test_failed_native_observation_never_reaches_host_bundle_or_authority(self):
        host = InstalledHost()
        run_path = next(path for path in host.reader.pages
                        if path.endswith("/attempts/1"))
        host.reader.pages[run_path]["conclusion"] = "failure"
        with self.assertRaisesRegex(
                installed.Refused, "installed-candidate-unavailable"):
            installed.run(host)
        self.assertNotIn(("bundle", 104), host.calls)
        self.assertNotIn("authority", host.calls)
        self.assertEqual(host.authority.calls, [])

    def test_swapped_host_bundle_refuses_after_read_only_candidate(self):
        host = InstalledHost()
        host.bundle += b"x"
        with self.assertRaisesRegex(host_loader.Refused,
                                    "runtime-host-bundle"):
            installed.run(host)
        self.assertEqual(len(host.downloader.calls), 1)
        self.assertNotIn("authority", host.calls)
        self.assertEqual(host.authority.calls, [])

    def test_candidate_reader_roles_must_remain_distinct(self):
        host = InstalledHost()
        host.candidate_download_transport = host.candidate_read_transport
        with self.assertRaisesRegex(
                installed.Refused, "installed-candidate-observation"):
            installed.run(host)
        self.assertEqual(host.reader.calls, [])
        self.assertEqual(host.authority.calls, [])

    def test_recovery_uses_the_same_candidate_qualification(self):
        host = InstalledHost()
        marker = object()
        with mock.patch.object(installed.runtime_host, "run",
                               return_value=marker) as delegated:
            result = installed.run(host, recovery=True)
        self.assertIs(result, marker)
        delegated.assert_called_once()
        call = delegated.call_args
        self.assertIs(call.args[0], host)
        self.assertEqual(call.args[1], host.witness)
        self.assertEqual(call.kwargs, {"recovery": True})
        self.assertEqual(len(host.downloader.calls), 1)
        self.assertEqual(host.authority.calls, [])

    def test_invalid_mode_refuses_without_observation(self):
        host = InstalledHost()
        with self.assertRaisesRegex(installed.Refused, "installed-mode"):
            installed.run(host, recovery=1)
        self.assertEqual(host.calls, [])
        self.assertEqual(host.reader.calls, [])
        self.assertEqual(host.downloader.calls, [])


if __name__ == "__main__":
    unittest.main()
