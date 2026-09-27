#!/usr/bin/env python3
"""Focused tests for the runnable callable isolated v2 runtime artifact."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[3]
ENG = ROOT / "eng"
sys.path.insert(0, str(ENG))

import build_callable_isolated_v2_runtime as builder
import verify_callable_isolated_v2_runtime_artifact as verifier


class RuntimeArtifactTests(unittest.TestCase):
    def build(self, root: pathlib.Path):
        manifest = builder.build(root)
        return (root / builder.ARCHIVE_NAME,
                root / builder.MANIFEST_NAME, manifest)

    def custom_archive(self, members, *, compression=zipfile.ZIP_STORED,
                       unsafe_member=None):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", compression=compression,
                             allowZip64=False) as zipped:
            for path, raw in members.items():
                info = zipfile.ZipInfo(path, builder.FIXED_TIME)
                info.compress_type = compression
                info.create_system = 3
                mode = (stat.S_IFLNK | 0o777 if path == unsafe_member
                        else stat.S_IFREG | 0o644)
                info.external_attr = mode << 16
                zipped.writestr(info, raw)
        return output.getvalue()

    def reseal(self, manifest, archive, members):
        value = json.loads(json.dumps(manifest))
        value["archiveSha256"] = hashlib.sha256(archive).hexdigest()
        value["members"] = [
            {"path": path, "sha256": hashlib.sha256(raw).hexdigest(),
             "size": len(raw)} for path, raw in members.items()]
        value["retainedOperatorSha256"] = hashlib.sha256(members[
            "callable_isolated_v2_retained_operator.py"]).hexdigest()
        return builder.canonical(value)

    def test_two_builds_are_byte_identical_and_emit_only_frozen_names(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            left = root / "left"
            right = root / "right"
            left_archive, left_manifest, _ = self.build(left)
            right_archive, right_manifest, _ = self.build(right)
            self.assertEqual(left_archive.read_bytes(), right_archive.read_bytes())
            self.assertEqual(left_manifest.read_bytes(), right_manifest.read_bytes())
            self.assertEqual(sorted(path.name for path in left.iterdir()),
                [builder.MANIFEST_NAME, builder.ARCHIVE_NAME])

    def test_manifest_is_canonical_and_binds_exact_commit_tree_and_members(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, manifest_path, manifest = self.build(pathlib.Path(temp))
            raw = manifest_path.read_bytes()
            self.assertEqual(raw, builder.canonical(json.loads(raw)))
            revision, tree = builder.source_identity()
            self.assertEqual((manifest["sourceRevision"], manifest["sourceTree"]),
                             (revision, tree))
            self.assertEqual(manifest["runtimeRequirements"], {
                "pythonMin": "3.11", "sqlite": True,
                "opensslEd25519Pkeyutl": True})
            self.assertEqual([item["path"] for item in manifest["members"]],
                             sorted(builder.MEMBER_SOURCES))
            self.assertEqual(manifest["archiveSha256"],
                             hashlib.sha256(archive.read_bytes()).hexdigest())

    def test_archive_has_exact_frozen_seven_member_roster(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, _, _ = self.build(pathlib.Path(temp))
            with zipfile.ZipFile(archive) as zipped:
                self.assertEqual(zipped.namelist(), sorted(builder.MEMBER_SOURCES))
                self.assertEqual(len(zipped.infolist()), 7)
                self.assertTrue(all(info.date_time == builder.FIXED_TIME
                                    for info in zipped.infolist()))
                self.assertNotIn("entry.py", zipped.namelist())

    def test_verifier_accepts_exact_bytes_and_refuses_archive_mutation(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, manifest_path, _ = self.build(pathlib.Path(temp))
            raw_archive = archive.read_bytes()
            raw_manifest = manifest_path.read_bytes()
            self.assertTrue(verifier.verify_candidate_bytes(
                raw_archive, raw_manifest)["verified"])
            self.assertTrue(verifier.verify(raw_archive, raw_manifest)["verified"])
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-manifest-binding"):
                verifier.verify(raw_archive + b"x", raw_manifest)

    def test_candidate_byte_verifier_is_source_independent_but_checkout_is_exact(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, manifest_path, _ = self.build(pathlib.Path(temp))
            value = json.loads(manifest_path.read_bytes())
            value["sourceRevision"] = "a" * 40
            value["sourceTree"] = "b" * 40
            value["builderSha256"] = "c" * 64
            raw = builder.canonical(value)
            checked = verifier.verify_candidate_bytes(archive.read_bytes(), raw)
            self.assertEqual(checked["sourceRevision"], "a" * 40)
            self.assertEqual(checked["sourceTree"], "b" * 40)
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-manifest-binding"):
                verifier.verify(archive.read_bytes(), raw)

    def test_candidate_byte_verifier_refuses_compressed_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            _, _, manifest = self.build(pathlib.Path(temp))
            members = builder.source_members()
            archive = self.custom_archive(
                members, compression=zipfile.ZIP_DEFLATED)
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-archive-metadata"):
                verifier.verify_candidate_bytes(
                    archive, self.reseal(manifest, archive, members))

    def test_candidate_byte_verifier_refuses_oversized_member(self):
        with tempfile.TemporaryDirectory() as temp:
            _, _, manifest = self.build(pathlib.Path(temp))
            members = builder.source_members()
            members["__main__.py"] = b"x" * 256_001
            archive = self.custom_archive(members)
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-archive-metadata"):
                verifier.verify_candidate_bytes(
                    archive, self.reseal(manifest, archive, members))

    def test_candidate_byte_verifier_refuses_unsafe_member_metadata(self):
        with tempfile.TemporaryDirectory() as temp:
            _, _, manifest = self.build(pathlib.Path(temp))
            members = builder.source_members()
            archive = self.custom_archive(
                members, unsafe_member="__main__.py")
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-archive-metadata"):
                verifier.verify_candidate_bytes(
                    archive, self.reseal(manifest, archive, members))

    def test_verifier_refuses_noncanonical_duplicate_and_wrong_member_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            archive, manifest_path, _ = self.build(pathlib.Path(temp))
            raw_archive = archive.read_bytes()
            raw = manifest_path.read_bytes()
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-manifest-shape"):
                verifier.verify(raw_archive, raw + b" ")
            duplicate = raw.replace(b'"schema":', b'"schema":"duplicate","schema":', 1)
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-manifest-duplicate"):
                verifier.verify(raw_archive, duplicate)
            value = json.loads(raw)
            value["members"][0]["path"] = "../escape.py"
            with self.assertRaisesRegex(verifier.Refused,
                                        "runtime-member-binding"):
                verifier.verify(raw_archive, builder.canonical(value))
            for field, invalid in (("sourceRevision", 1),
                                   ("archiveSha256", []),
                                   ("runtimeRequirements", 1),
                                   ("members", {})):
                with self.subTest(field=field):
                    value = json.loads(raw)
                    value[field] = invalid
                    with self.assertRaises(verifier.Refused):
                        verifier.verify(raw_archive, builder.canonical(value))

    def test_relocated_direct_cli_checks_imports_and_refuses_all_other_routes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            archive, _, _ = self.build(root / "artifact")
            empty = root / "empty"
            empty.mkdir()
            environment = dict(os.environ)
            environment.pop("PYTHONPATH", None)
            checked = subprocess.run(
                [sys.executable, str(archive), "--check-imports"], cwd=empty,
                env=environment, text=True, capture_output=True)
            self.assertEqual(checked.returncode, 0, checked.stderr)
            self.assertEqual(checked.stdout.strip(), builder.SCHEMA)
            for arguments in ([], ["execute"], ["--endpoint", "x"],
                              ["--check-imports", "extra"]):
                refused = subprocess.run(
                    [sys.executable, str(archive), *arguments], cwd=empty,
                    env=environment, text=True, capture_output=True)
                self.assertEqual(refused.returncode, 64)
                self.assertEqual(refused.stdout, "")
                self.assertEqual(refused.stderr,
                    "callable-isolated-v2-runtime: direct invocation refused\n")

    def test_relocated_installed_api_allows_one_post_and_read_only_recovery(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            archive, _, _ = self.build(root / "artifact")
            empty = root / "empty"
            state = root / "state"
            empty.mkdir()
            state.mkdir()
            probe = pathlib.Path(__file__).with_name("synthetic_authority_probe.py")
            environment = dict(os.environ)
            environment.pop("PYTHONPATH", None)
            completed = subprocess.run(
                [sys.executable, str(probe), str(archive), str(state)],
                cwd=empty, env=environment, text=True, capture_output=True)
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertRegex(completed.stdout,
                             r"OFFLINE_INSTALLED_RUNTIME_OK posts=1 gets=\d+")

    def test_package_compatible_loader_preserves_source_mode(self):
        contracts_path = ENG / "callable_isolated_v2_runtime" / "contracts.py"
        spec = importlib.util.spec_from_file_location(
            "runtime_artifact_source_contracts", contracts_path)
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        operator = module.operator_module()
        self.assertEqual(operator.__file__,
            str(ENG / "callable-cli-isolated-operation-v2.py"))


if __name__ == "__main__":
    unittest.main()
