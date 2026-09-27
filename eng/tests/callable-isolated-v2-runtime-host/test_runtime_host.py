"""Synthetic host boundary tests; no production protected authority is supplied."""

import dataclasses
import hashlib
import io
import pathlib
import sys
import tempfile
from types import SimpleNamespace
import unittest
import zipfile

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_runtime as builder
import callable_isolated_v2_runtime_candidate_witness as candidate
import callable_isolated_v2_runtime_host as host_loader


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def fixture():
    revision, tree = builder.source_identity()
    members = builder.source_members()
    archive = builder._archive(members)
    manifest = builder.canonical({
        "schema": builder.SCHEMA, "sourceRevision": revision,
        "sourceTree": tree, "builderSha256": sha(builder.source_blob(
            builder.BUILDER_SOURCE)),
        "retainedOperatorSha256": sha(members[
            "callable_isolated_v2_retained_operator.py"]),
        "archiveSha256": sha(archive),
        "runtimeRequirements": builder.RUNTIME_REQUIREMENTS,
        "members": [{"path": path, "sha256": sha(raw), "size": len(raw)}
                    for path, raw in members.items()],
    })
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as z:
        z.writestr(candidate.ARCHIVE_NAME, archive)
        z.writestr(candidate.MANIFEST_NAME, manifest)
    bundle = output.getvalue()
    expected = host_loader.ExpectedRuntime(candidate.REPOSITORY, 77,
        revision, tree, "c" * 64, 101, 1, 104, sha(bundle), sha(archive),
        sha(manifest), 201, 1)
    witness = candidate.CandidateWitness(candidate.REPOSITORY, 77,
        revision, tree, "c" * 64, 101, 1, 104, sha(bundle), sha(archive),
        sha(manifest))
    return expected, witness, bundle


class Host:
    def __init__(self, expected, bundle, authority):
        self.expected, self.bundle, self.authority = expected, bundle, authority
        self.calls = []

    def expected_runtime(self):
        self.calls.append("expected")
        return self.expected

    def read_candidate_bundle(self, artifact_id):
        self.calls.append(("bundle", artifact_id))
        return self.bundle

    def execution_run(self):
        self.calls.append("execution")
        return self.expected.execution_run_id, self.expected.execution_run_attempt

    def protected_authority(self):
        self.calls.append("authority")
        return self.authority


class WrongExecutionHost(Host):
    def execution_run(self):
        self.calls.append("execution")
        return 999, 1


class ConfigurationAuthority:
    def __init__(self, expected, change=None):
        binding = SimpleNamespace(
            source_revision=expected.source_revision,
            source_tree=expected.source_tree,
            artifact_sha256=expected.archive_sha256,
            workflow_sha256=expected.workflow_sha256,
            run_id=expected.execution_run_id,
            run_attempt=expected.execution_run_attempt)
        self.configuration = SimpleNamespace(
            revision=expected.source_revision,
            source_tree=expected.source_tree, binding=binding)
        if change:
            change(self.configuration)
        self.calls = []

    def read_installed_configuration(self):
        self.calls.append("configuration")
        return self.configuration

    def __getattr__(self, name):
        if name in ("trusted_now", "scope", "read_grant", "read_parent",
                    "native_get", "native_post", "issue_execution_token"):
            def unexpected(*_args):
                self.calls.append(name)
                raise AssertionError("effect reached before binding")
            return unexpected
        raise AttributeError(name)


class RuntimeHostTests(unittest.TestCase):
    def test_exact_archive_loads_and_missing_authority_refuses_before_import(self):
        expected, witness, bundle = fixture()
        missing = Host(expected, bundle, None)
        with self.assertRaisesRegex(host_loader.Refused, "authority-unavailable"):
            host_loader.run(missing, witness)
        self.assertNotIn("callable_isolated_v2_runtime", sys.modules)
        self.assertEqual(missing.calls, ["expected", ("bundle", 104),
                                         "execution", "authority"])

        authority = ConfigurationAuthority(expected)
        present = Host(expected, bundle, authority)
        result = host_loader.run(present, witness)
        self.assertEqual(type(result).__name__, "Unknown")
        self.assertEqual(authority.calls,
                         ["configuration"] * 4)
        # A reused memfd number must not reuse zipimport's prior directory cache.
        self.assertEqual(type(host_loader.run(present, witness)).__name__, "Unknown")
        self.assertEqual(authority.calls, ["configuration"] * 8)
        self.assertEqual(present.calls, ["expected", ("bundle", 104),
                                         "execution", "authority"] * 2)
        self.assertNotIn("callable_isolated_v2_runtime", sys.modules)

    def test_candidate_witness_never_substitutes_host_expectation(self):
        expected, witness, bundle = fixture()
        changes = (
            dataclasses.replace(expected, source_tree="d" * 40),
            dataclasses.replace(expected, workflow_sha256="d" * 64),
            dataclasses.replace(expected, producer_run_id=201),
            dataclasses.replace(expected, execution_run_id=101),
            dataclasses.replace(expected, archive_sha256="d" * 64),
        )
        for changed in changes:
            with self.subTest(changed=changed):
                host = Host(changed, bundle, object())
                with self.assertRaises(host_loader.Refused):
                    host_loader.run(host, witness)
                self.assertEqual(host.calls, ["expected"])

    def test_swapped_bytes_and_manifest_refuse_before_authority(self):
        expected, witness, bundle = fixture()
        for changed in (bundle + b"x", bundle[:-1] + b"x"):
            with self.subTest(changed=changed[-1:]):
                host = Host(expected, changed, object())
                with self.assertRaises(host_loader.Refused):
                    host_loader.run(host, witness)
                self.assertEqual(host.calls, ["expected", ("bundle", 104)])

    def test_actual_execution_run_must_match_host_expectation(self):
        expected, witness, bundle = fixture()
        host = WrongExecutionHost(expected, bundle, object())
        with self.assertRaisesRegex(host_loader.Refused, "execution-run"):
            host_loader.run(host, witness)
        self.assertEqual(host.calls, ["expected", ("bundle", 104), "execution"])

    def test_authority_binding_refuses_each_mismatch_before_effects(self):
        expected, witness, bundle = fixture()
        changes = {
            "configuration revision": lambda c: setattr(c, "revision", "d" * 40),
            "configuration tree": lambda c: setattr(c, "source_tree", "d" * 40),
            "binding revision": lambda c: setattr(c.binding, "source_revision", "d" * 40),
            "binding tree": lambda c: setattr(c.binding, "source_tree", "d" * 40),
            "artifact": lambda c: setattr(c.binding, "artifact_sha256", "d" * 64),
            "workflow": lambda c: setattr(c.binding, "workflow_sha256", "d" * 64),
            "execution run": lambda c: setattr(c.binding, "run_id", 999),
            "execution attempt": lambda c: setattr(c.binding, "run_attempt", 2),
        }
        with tempfile.TemporaryDirectory() as directory:
            journal = pathlib.Path(directory) / "runtime.db"
            for name, change in changes.items():
                with self.subTest(name=name):
                    authority = ConfigurationAuthority(expected, change)
                    authority.configuration.journal_database_path = str(journal)
                    with self.assertRaisesRegex(host_loader.Refused,
                                                "configuration-binding"):
                        host_loader.run(Host(expected, bundle, authority), witness)
                    self.assertEqual(authority.calls, ["configuration"])
                    self.assertFalse(journal.exists())

    def test_import_shadow_refuses_before_runtime_composition(self):
        expected, witness, bundle = fixture()
        name = "callable_isolated_v2_runtime"
        shadow = object()
        sys.modules[name] = shadow
        try:
            with self.assertRaisesRegex(host_loader.Refused, "import-shadow"):
                host_loader.run(Host(expected, bundle, object()), witness)
            self.assertIs(sys.modules[name], shadow)
        finally:
            sys.modules.pop(name, None)


if __name__ == "__main__":
    unittest.main()
