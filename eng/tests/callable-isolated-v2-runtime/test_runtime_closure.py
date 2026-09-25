"""Local closure controls with independent tree, mapped-file and manifest mutations."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_zipapp as builder  # noqa: E402
import callable_isolated_v2_runtime_closure as closure  # noqa: E402
import verify_callable_isolated_v2_install as install  # noqa: E402


def interpreter_pin() -> dict:
    binary = pathlib.Path(sys.executable).resolve(strict=True)
    return {"schema": install.INTERPRETER_SCHEMA, "path": str(binary),
            "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "version": ".".join(map(str, sys.version_info[:3])),
            "implementation": "cpython", "flags": ["-I", "-S"]}


class RuntimeClosureTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="fsgg-v2-closure-test-")
        self.addCleanup(temporary.cleanup)
        self.directory = pathlib.Path(temporary.name)
        self.install_dir = self.directory / "install"
        self.install_dir.mkdir()
        self.archive = self.install_dir / builder.ARCHIVE_NAME
        builder.build(self.archive)
        self.pin = interpreter_pin()

    def test_exact_local_observation_remains_closed_and_sanitized(self):
        journal = self.directory / "native-journal-sentinel"
        journal.write_text("untouched")
        original_run = subprocess.run
        commands: list[list[str]] = []

        def guarded_run(argv, **kwargs):
            commands.append(argv)
            self.assertEqual({"LC_ALL": "C", "PATH": "/usr/bin:/bin"}, kwargs["env"])
            self.assertEqual(["-I", "-S"], argv[1:3])
            return original_run(argv, **kwargs)

        with patch.dict(os.environ, {"GITHUB_TOKEN": "sentinel-secret",
                                          "FSGG_V2_JOURNAL_PATH": str(journal)}), \
             patch.object(subprocess, "run", side_effect=guarded_run):
            raw = closure.capture_candidate(ROOT, self.archive, self.pin)
            digest = hashlib.sha256(raw).hexdigest()
            evidence = closure.verify_candidate(raw, digest, ROOT, self.archive, self.pin)
        self.assertEqual(8, len(commands))  # Three install probes plus one map probe, twice.
        self.assertFalse(evidence.authorized)
        self.assertFalse(evidence.can_dispatch)
        self.assertEqual(0, evidence.live_effects)
        self.assertEqual("untouched", journal.read_text())
        value = json.loads(raw)
        self.assertEqual(closure.SCHEMA, value["schema"])
        self.assertEqual(closure.STATE, value["state"])
        self.assertEqual(install.ARCHIVE_SHA256, value["archiveSha256"])
        self.assertEqual(self.pin["sha256"], value["interpreter"]["sha256"])
        self.assertGreater(value["stdlib"]["fileCount"], 0)
        self.assertGreater(value["stdlib"]["directoryCount"], 0)
        self.assertGreater(evidence.mapped_file_count, 0)
        self.assertEqual(value["mappedFiles"], sorted(value["mappedFiles"], key=lambda x: x["path"]))

    def test_manifest_digest_canonical_duplicate_and_foreign_mapping_refuse(self):
        raw = closure.capture_candidate(ROOT, self.archive, self.pin)
        digest = hashlib.sha256(raw).hexdigest()
        with self.assertRaisesRegex(closure.Refused, "manifest-digest"):
            closure.verify_candidate(raw, "0" * 64, ROOT, self.archive, self.pin)
        changed = json.loads(raw)
        changed["mappedFiles"][0]["sha256"] = "0" * 64
        altered = closure._canonical(changed)
        with self.assertRaisesRegex(closure.Refused, "closure-drift"):
            closure.verify_candidate(altered, hashlib.sha256(altered).hexdigest(),
                                     ROOT, self.archive, self.pin)
        duplicate = raw.replace(b'"state":"local-candidate-not-protected"',
                                b'"state":"local-candidate-not-protected","state":"local-candidate-not-protected"', 1)
        with self.assertRaisesRegex(closure.Refused, "duplicate-member"):
            closure.verify_candidate(duplicate, hashlib.sha256(duplicate).hexdigest(),
                                     ROOT, self.archive, self.pin)
        padded = raw + b" "
        with self.assertRaisesRegex(closure.Refused, "noncanonical"):
            closure.verify_candidate(padded, hashlib.sha256(padded).hexdigest(),
                                     ROOT, self.archive, self.pin)

    def test_independent_stdlib_tree_mutations_change_seal(self):
        root = self.directory / "stdlib"
        (root / "json").mkdir(parents=True)
        module = root / "json" / "decoder.py"
        module.write_bytes(b"original")
        baseline = closure._tree(root)
        module.write_bytes(b"changed!")
        self.assertNotEqual(baseline["treeSha256"], closure._tree(root)["treeSha256"])
        module.write_bytes(b"original")
        extra = root / "extra.py"
        extra.write_bytes(b"extra")
        self.assertNotEqual(baseline["treeSha256"], closure._tree(root)["treeSha256"])
        extra.unlink()
        (root / "empty").mkdir()
        self.assertNotEqual(baseline["treeSha256"], closure._tree(root)["treeSha256"])
        (root / "empty").rmdir()
        module.unlink()
        with self.assertRaisesRegex(closure.Refused, "stdlib-empty"):
            closure._tree(root)
        module.symlink_to(self.archive)
        with self.assertRaisesRegex(closure.Refused, "stdlib-symlink"):
            closure._tree(root)

    def test_independent_mapped_file_mutation_and_symlink_refuse(self):
        mapped = self.directory / "libfixture.so"
        mapped.write_bytes(b"ELF-fixture")
        first = closure._file_entry(mapped)
        mapped.write_bytes(b"ELF-mutated")
        second = closure._file_entry(mapped)
        self.assertNotEqual(first["sha256"], second["sha256"])
        link = self.directory / "lib-link.so"
        link.symlink_to(mapped)
        with self.assertRaisesRegex(closure.Refused, "runtime-path"):
            closure._file_entry(link)

    def test_archive_drift_after_runtime_probe_refuses(self):
        original_run = subprocess.run
        calls = 0

        def drifting_run(argv, **kwargs):
            nonlocal calls
            result = original_run(argv, **kwargs)
            calls += 1
            if calls == 4:
                self.archive.write_bytes(self.archive.read_bytes() + b"drift")
            return result

        with patch.object(subprocess, "run", side_effect=drifting_run):
            with self.assertRaisesRegex(closure.Refused, "probe-drift"):
                closure.capture_candidate(ROOT, self.archive, self.pin)
        self.assertEqual(4, calls)


if __name__ == "__main__":
    unittest.main()
