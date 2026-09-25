"""Local clean-install controls for exact #555 archive and #559 source pins."""

from __future__ import annotations

import hashlib
import io
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch

ENG = pathlib.Path(__file__).resolve().parents[2]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))
import build_callable_isolated_v2_zipapp as builder  # noqa: E402
import verify_callable_isolated_v2_install as verifier  # noqa: E402


def pin() -> dict:
    binary = pathlib.Path(sys.executable).resolve(strict=True)
    return {
        "schema": verifier.INTERPRETER_SCHEMA,
        "path": str(binary),
        "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "version": ".".join(map(str, sys.version_info[:3])),
        "implementation": "cpython",
        "flags": ["-I", "-S"],
    }


class InstallPinTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="fsgg-v2-install-test-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = pathlib.Path(self.temporary.name)
        self.install = self.directory / "install"
        self.install.mkdir()
        self.archive = self.install / verifier.ARCHIVE_NAME
        builder.build(self.archive)
        self.pin = pin()

    def test_exact_archive_source_and_interpreter_remain_non_dispatchable(self):
        sentinel = self.directory / "native-journal-sentinel"
        sentinel.write_text("untouched")
        original_run = subprocess.run
        commands: list[list[str]] = []

        def guarded_run(argv, **kwargs):
            commands.append(argv)
            self.assertEqual({"LC_ALL": "C", "PATH": "/usr/bin:/bin"}, kwargs["env"])
            self.assertEqual(["-I", "-S"], argv[1:3])
            return original_run(argv, **kwargs)

        with patch.dict(os.environ, {"GITHUB_TOKEN": "sentinel-secret",
                                          "FSGG_V2_JOURNAL_PATH": str(sentinel)}), \
             patch.object(verifier.subprocess, "run", side_effect=guarded_run):
            result = verifier.verify_clean_install(ROOT, self.archive, self.pin)
        self.assertEqual(3, len(commands))
        self.assertEqual(verifier.ARCHIVE_SHA256, result.archive_sha256)
        self.assertEqual(self.pin["sha256"], result.interpreter_sha256)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(0, result.live_effects)
        self.assertEqual("untouched", sentinel.read_text())
        self.assertEqual({self.archive.name}, {path.name for path in self.install.iterdir()})
        with zipfile.ZipFile(self.archive) as package:
            self.assertEqual(["__main__.py", "callable_isolated_v2_grant.py"], package.namelist())
            self.assertNotIn("callable_isolated_v2_authority.py", package.namelist())

    def test_archive_digest_and_nonclean_install_refuse_before_interpreter(self):
        original = self.archive.read_bytes()
        self.archive.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
        with patch.object(verifier.subprocess, "run", side_effect=AssertionError("interpreter reached")):
            with self.assertRaisesRegex(verifier.Refused, "archive-digest"):
                verifier.verify_clean_install(ROOT, self.archive, self.pin)
        self.archive.write_bytes(original)
        (self.install / "foreign.txt").write_text("unexpected")
        with self.assertRaisesRegex(verifier.Refused, "directory-not-clean"):
            verifier.verify_clean_install(ROOT, self.archive, self.pin)

    def test_wrong_member_and_imported_authority_module_refuse(self):
        with zipfile.ZipFile(self.archive) as package:
            entry = package.read("__main__.py")
            parser = package.read("callable_isolated_v2_grant.py")
        for members in (
            [("__main__.py", entry), ("wrong_parser.py", parser)],
            [("__main__.py", entry), ("callable_isolated_v2_grant.py", parser),
             ("callable_isolated_v2_authority.py", b"# foreign authority port")],
        ):
            with self.subTest(members=[name for name, _ in members]):
                output = io.BytesIO()
                with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as package:
                    for name, raw in members:
                        package.writestr(name, raw)
                with self.assertRaisesRegex(verifier.Refused, "member-set"):
                    verifier._verify_members(output.getvalue())
                self.archive.write_bytes(output.getvalue())
                with self.assertRaisesRegex(verifier.Refused, "archive-digest"):
                    verifier.verify_clean_install(ROOT, self.archive, self.pin)

    def test_wrong_source_and_manifest_refuse_before_interpreter(self):
        source = self.directory / "source"
        paths = ["work/gs2-09-9-isolated-operator-rotation/zipapp-manifest.json",
                 "eng/build_callable_isolated_v2_zipapp.py",
                 "eng/callable_isolated_v2_entry.py",
                 "eng/callable_isolated_v2_grant.py",
                 "eng/callable_isolated_v2_authority.py"]
        for relative in paths:
            destination = source / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / relative, destination)
        entry = source / "eng/callable_isolated_v2_entry.py"
        entry.write_bytes(entry.read_bytes() + b"\n# changed source\n")
        with patch.object(verifier.subprocess, "run", side_effect=AssertionError("interpreter reached")):
            with self.assertRaisesRegex(verifier.Refused, "source-digest"):
                verifier.verify_clean_install(source, self.archive, self.pin)
        shutil.copy2(ROOT / "eng/callable_isolated_v2_entry.py", entry)
        manifest = source / paths[0]
        parsed = json.loads(manifest.read_text())
        parsed["archiveSha256"] = "0" * 64
        manifest.write_text(json.dumps(parsed))
        with self.assertRaisesRegex(verifier.Refused, "manifest-digest"):
            verifier.verify_clean_install(source, self.archive, self.pin)

    def test_wrong_interpreter_hash_version_flags_or_path_refuse(self):
        mutated = {**self.pin, "sha256": "0" * 64}
        with patch.object(verifier.subprocess, "run", side_effect=AssertionError("interpreter reached")):
            with self.assertRaisesRegex(verifier.Refused, "interpreter-digest"):
                verifier.verify_clean_install(ROOT, self.archive, mutated)
        mutated = {**self.pin, "version": "0.0.0"}
        with self.assertRaisesRegex(verifier.Refused, "interpreter-runtime"):
            verifier.verify_clean_install(ROOT, self.archive, mutated)
        mutated = {**self.pin, "flags": ["-I"]}
        with self.assertRaisesRegex(verifier.Refused, "interpreter-pin"):
            verifier.verify_clean_install(ROOT, self.archive, mutated)
        link = self.directory / "python-symlink"
        link.symlink_to(self.pin["path"])
        mutated = {**self.pin, "path": str(link)}
        with self.assertRaisesRegex(verifier.Refused, "install-path"):
            verifier.verify_clean_install(ROOT, self.archive, mutated)


if __name__ == "__main__":
    unittest.main()
