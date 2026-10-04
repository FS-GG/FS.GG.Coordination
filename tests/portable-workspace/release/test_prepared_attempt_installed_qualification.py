#!/usr/bin/env python3
"""Source-window controls only: Python checker and exact-package preparation.

No SDK, FSI, compiler, assembly loading, model, network or product operation runs.
The F# installed API acceptance is deliberately a separately admitted later step.
"""

from __future__ import annotations

import hashlib
import ctypes
import errno
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "eng" / "prepared-attempt-installed-qualification.fsx"
sys.dont_write_bytecode = True


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_packager():
    path = ROOT / "eng" / "run-packaged-portable-workspace-qualification.py"
    spec = importlib.util.spec_from_file_location("prepared_installed_packager", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SourceControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="prepared-installed-source-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        source = SCRIPT.read_text(encoding="utf-8")
        match = re.search(r'let checkerSource = """(.*?)"""', source, re.S)
        self.assertIsNotNone(match)
        self.checker = match.group(1)
        # This compile is Python's parser, never a CLR compiler or assembly loader.
        compile(self.checker, "checker.py", "exec")
        self.capsule = self.root / "capsule"
        self.capsule.mkdir()
        (self.capsule / "checker.py").write_text(self.checker)
        (self.capsule / "dependency.py").write_text("def ready(): return 42\n")
        (self.capsule / "entry.py").write_text("from dependency import ready\n")
        self.marker = self.root / "workload-marker"
        # A real discovered body fails if called. Import/discovery must leave its
        # side effect absent; no substitute Boolean readiness is supplied.
        (self.capsule / "test_entry.py").write_text(
            "import unittest\nfrom entry import ready\nfrom pathlib import Path\n"
            "class Entry(unittest.TestCase):\n def test_ready(self):\n"
            f"  Path({str(self.marker)!r}).write_text('workload')\n"
            "  raise RuntimeError('workload ran')\n"
        )

    def run_checker(self, mode: str, timeout=2):
        return subprocess.run(
            [str(Path(sys.executable).resolve()), "-B", "checker.py", mode],
            cwd=self.capsule,
            env={"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8"},
            timeout=timeout,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_real_import_discovery_and_no_workload_or_input_cache(self):
        before = {p.name: digest(p) for p in self.capsule.iterdir()}
        for mode, expected in (
            ("imports", ["entry", "dependency"]),
            ("discovery", ["test_entry.Entry.test_ready"]),
        ):
            result = self.run_checker(mode)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout), {
                "schema": "fsgg.capsule-observation/1",
                "checkId": mode,
                "discovered": expected,
            })
        self.assertFalse(self.marker.exists())
        self.assertEqual(before, {p.name: digest(p) for p in self.capsule.iterdir()})

    def test_missing_transitive_import_fails_both_calls_without_workload(self):
        (self.capsule / "dependency.py").unlink()
        for mode in ("imports", "discovery"):
            result = self.run_checker(mode)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "")
            self.assertIn("import", result.stderr)
        self.assertFalse(self.marker.exists())

    def test_empty_discovery_stays_empty_observation(self):
        (self.capsule / "test_entry.py").write_text("import unittest\n")
        result = self.run_checker("discovery")
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout)["discovered"], [])
        self.assertFalse(self.marker.exists())

    def test_unknown_checker_call_is_nonzero_and_has_no_readiness_json(self):
        result = self.run_checker("workload")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertFalse(self.marker.exists())

    def test_real_timeout_is_bounded_and_reaped(self):
        (self.capsule / "checker.py").write_text("import time\ntime.sleep(3)\n")
        with self.assertRaises(subprocess.TimeoutExpired):
            self.run_checker("imports", timeout=0.1)
        self.assertFalse(self.marker.exists())

    def test_malformed_observation_fixture_is_not_json(self):
        (self.capsule / "checker.py").write_text("print('malformed')\n")
        result = self.run_checker("imports")
        self.assertEqual(result.returncode, 0)
        with self.assertRaises(json.JSONDecodeError):
            json.loads(result.stdout)
        self.assertFalse(self.marker.exists())

    def make_package(self, name="exact.nupkg", execution=b"mock-execution", akka=b"mock-akka"):
        package = self.root / name
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("tools/net10.0/any/Akka.dll", akka)
            archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", execution)
            archive.writestr("fixture.nuspec", "<package><metadata><version>external-fixture</version></metadata></package>")
        return package

    def external_script(self):
        # A standalone copy represents the checkout-hidden input. Preparation
        # takes this path and never resolves the original repository reference.
        script = self.root / "external" / "eng" / SCRIPT.name
        script.parent.mkdir(parents=True)
        script.write_bytes(SCRIPT.read_bytes())
        return script

    def test_exact_package_rewriter_needs_only_external_inputs(self):
        packager = load_packager()
        package = self.make_package()
        script = self.external_script()
        prepared = packager.prepare(package, digest(package), script, self.root / "extracted")
        rewritten = Path(prepared["rewrittenScript"]).read_text()
        self.assertNotIn(packager.REFERENCE, rewritten)
        self.assertNotIn(str(ROOT), rewritten)
        self.assertNotIn("#load", rewritten)
        self.assertEqual(prepared["sourceScriptSha256"], digest(script))
        self.assertEqual(prepared["executionAssemblySha256"], hashlib.sha256(b"mock-execution").hexdigest())
        self.assertEqual(rewritten.splitlines()[2:], script.read_text().splitlines()[1:])

    def test_package_tamper_refuses_before_extraction(self):
        packager = load_packager()
        package = self.make_package()
        original = digest(package)
        package.write_bytes(package.read_bytes() + b"drift")
        with self.assertRaisesRegex(ValueError, "package digest changed"):
            packager.prepare(package, original, self.external_script(), self.root / "extracted")
        self.assertFalse((self.root / "extracted").exists())

    def test_duplicate_execution_archive_entry_refuses(self):
        packager = load_packager()
        package = self.make_package()
        with zipfile.ZipFile(package, "a") as archive:
            # Suppress zipfile's duplicate-name warning: refusal is the control.
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", b"replacement")
        with self.assertRaisesRegex(ValueError, "repeats assembly name"):
            packager.prepare(package, digest(package), self.external_script(), self.root / "extracted")

    def test_mock_wrapper_launch_is_external_and_records_exact_source_identity(self):
        packager = load_packager()
        package = self.make_package()
        script = self.external_script()
        dotnet = self.root / "mock-host"
        dotnet.write_bytes(b"not an executable; launch is mocked")
        receipt = self.root / "outer-receipt.json"
        evidence = self.root / "inner-evidence.json"
        evidence.write_bytes(b"source-window mock observation\n")
        argv = ["packager", "--package", str(package), "--expected-package-sha256", digest(package),
                "--script", str(script), "--work-dir", str(self.root / "extract"),
                "--receipt", str(receipt), "--dotnet", str(dotnet), "--", "--evidence", str(evidence)]
        with patch.object(sys, "argv", argv), patch.object(packager.subprocess, "run") as launch:
            launch.return_value = subprocess.CompletedProcess([], 0)
            self.assertEqual(packager.main(), 0)
        launch.assert_called_once()
        command = launch.call_args.args[0]
        self.assertEqual(command[0:2], [str(dotnet), "fsi"])
        self.assertTrue(Path(command[2]).is_relative_to(self.root))
        self.assertEqual(launch.call_args.kwargs["cwd"], script.parent.parent)
        self.assertNotIn(str(ROOT), " ".join(command))
        observed = json.loads(receipt.read_text())
        self.assertEqual(observed["sourceScriptSha256"], digest(script))
        self.assertEqual(observed["qualificationEvidenceSha256"], digest(evidence))
        self.assertFalse(observed["publicationAuthorized"])
        self.assertFalse(observed["activationAuthorized"])


class PolicyFixtureIdentityControls(unittest.TestCase):
    """Pure fixture validation against the accepted adapter shape; no CLR proof."""

    def test_declared_policy_digests_are_diverse_and_uniform_placeholders_refuse(self):
        source = SCRIPT.read_text()
        revision_label = re.search(r'let revision = hash \(Encoding.UTF8.GetBytes "([^"]+)"\) \|> fun value -> value.Substring\(0,40\)', source)
        policy_label = re.search(r'let sha = hash \(Encoding.UTF8.GetBytes "([^"]+)"\)', source)
        self.assertIsNotNone(revision_label)
        self.assertIsNotNone(policy_label)
        revision = hashlib.sha256(revision_label.group(1).encode()).hexdigest()[:40]
        policy = hashlib.sha256(policy_label.group(1).encode()).hexdigest()
        def shape(value, lengths):
            return len(value) in lengths and all(c in "0123456789abcdef" for c in value) and len(set(value)) > 1
        self.assertTrue(shape(revision, (40, 64)))
        self.assertTrue(shape(policy, (64,)))
        self.assertFalse(shape("a" * 40, (40, 64)))
        self.assertFalse(shape("b" * 64, (64,)))
        self.assertIn('reason.StartsWith "preparation-"', source)
        self.assertIn('runner.Calls=0 && runner.Cleanups=0', source)
        self.assertIn('receipt.CleanupCompleted && runner.Calls=1 && runner.Cleanups=1', source)


class PackageAssemblyLayoutControls(unittest.TestCase):
    """Pure census and actual unchanged packager controls; no F# caller proof."""

    @staticmethod
    def expected_names():
        source = SCRIPT.read_text()
        def names(binding):
            block = re.search(r"let " + binding + r" =\n    set \[([^]]*)\]", source, re.S)
            assert block is not None
            return set(re.findall(r'"([^"\n]+)"', block.group(1)))
        return names("selectedAssemblyNames"), names("inactiveAssemblyRelativeNames")

    @classmethod
    def validate_census(cls, entries):
        selected, inactive = cls.expected_names()
        prefix = "tools/net10.0/any/"
        dlls = [name for name in entries if name.lower().endswith(".dll")]
        if any(not name.startswith(prefix) for name in dlls):
            raise ValueError("unexpected-assembly-layout")
        relative = [name[len(prefix):] for name in dlls]
        if len(relative) != 39 or len(set(relative)) != len(relative):
            raise ValueError("duplicate-or-missing-package-assembly")
        if set(relative) != selected | inactive:
            raise ValueError("unexpected-package-assembly-census")
        return selected, inactive

    def test_closed_census_accepts21_top13_culture5_platform(self):
        selected, inactive = self.expected_names()
        names = ["tools/net10.0/any/" + name for name in selected | inactive]
        self.assertEqual(self.validate_census(names), (selected, inactive))
        self.assertEqual(len(selected), 21)
        self.assertEqual(sum(name.endswith("FSharp.Core.resources.dll") for name in inactive), 13)
        self.assertEqual(sum(name.startswith("runtimes/") for name in inactive), 5)

    def test_extra_duplicate_missing_and_unexpected_layout_refuse(self):
        selected, inactive = self.expected_names()
        names = sorted("tools/net10.0/any/" + name for name in selected | inactive)
        variants = [names + ["tools/net10.0/any/extra.dll"], names + [names[0]], names[:-1] + [names[0]],
                    names + ["tools/net10.0/any/rogue.DLL"],
                    names[1:], names[:-1] + ["tools/net10.0/any/unknown/resource.dll"],
                    names[:-1] + ["outside/rogue.dll"]]
        for mutant in variants:
            with self.subTest(mutant=mutant[-1]):
                with self.assertRaises(ValueError):
                    self.validate_census(mutant)

    def test_unchanged_packager_selects21_without_flattening18_and_tamper_refuses(self):
        selected, inactive = self.expected_names()
        with tempfile.TemporaryDirectory(prefix="assembly-census-source-") as name:
            root = Path(name)
            package = root / "fixture.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                for entry in selected | inactive:
                    archive.writestr("tools/net10.0/any/" + entry, entry.encode())
            expected = digest(package)
            script = root / "qualification.fsx"
            script.write_bytes(SCRIPT.read_bytes())
            packager = load_packager()
            prepared = packager.prepare(package, expected, script, root / "prepared")
            self.assertEqual(prepared["assemblyCount"], "21")
            self.assertEqual({p.name for p in (root / "prepared/assemblies").iterdir()}, selected)
            self.assertFalse(any(p.is_dir() for p in (root / "prepared/assemblies").iterdir()))
            package.write_bytes(package.read_bytes() + b"archive-drift")
            with self.assertRaisesRegex(ValueError, "package digest changed"):
                packager.prepare(package, expected, script, root / "tampered")
            self.assertFalse((root / "tampered").exists())


class HeldOutputFilesystemControls(unittest.TestCase):
    """Actual private filesystem conditions; no F# compiled-caller proof.

    libc's existing renameat2 entry is used only for reversible private file/directory
    moves and NO_REPLACE collision checks. No native product/workload runs.
    """

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="prepared-held-output-")
        self.addCleanup(self.temporary.cleanup)
        self.parent = Path(self.temporary.name)
        self.root = self.parent / "work"
        self.root.mkdir(mode=0o700)
        self.root_fd = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        self.addCleanup(os.close, self.root_fd)
        self.root_identity = self.identity(os.fstat(self.root_fd))
        self.renameat2 = ctypes.CDLL(None, use_errno=True).renameat2
        self.renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        self.renameat2.restype = ctypes.c_int

    @staticmethod
    def identity(value):
        return value.st_dev, value.st_ino

    def move_without_replacement(self, source, target, source_fd=None, target_fd=None):
        result = self.renameat2(
            self.root_fd if source_fd is None else source_fd, os.fsencode(source),
            self.root_fd if target_fd is None else target_fd, os.fsencode(target), 1,
        )
        if result != 0:
            error = ctypes.get_errno()
            raise OSError(error, os.strerror(error))

    def file(self, name, content=b"owned", mode=0o600):
        fd = os.open(name, os.O_CREAT | os.O_EXCL | os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC,
                     mode, dir_fd=self.root_fd)
        self.addCleanup(os.close, fd)
        os.write(fd, content)
        return fd, self.identity(os.fstat(fd))

    def captured_identity(self, name):
        return self.identity(os.stat(name, dir_fd=self.root_fd, follow_symlinks=False))

    def test_root_replacement_refuses_and_never_touches_foreign_tree(self):
        self.file("owned.bin")
        self.root.rename(self.parent / "displaced-work")
        self.root.mkdir(mode=0o700)
        foreign = self.root / "foreign.bin"
        foreign.write_bytes(b"foreign-root")
        self.assertNotEqual(self.identity(os.stat(self.root, follow_symlinks=False)), self.root_identity)
        # The named-root check refuses before retirement. Held reads still refer
        # to the displaced original, never the new named parent.
        self.assertEqual(os.listdir(self.root_fd), ["owned.bin"])
        self.assertEqual(foreign.read_bytes(), b"foreign-root")

    def test_owned_leaf_capture_and_delete_stays_on_held_root(self):
        fd, expected = self.file("owned.bin")
        captured = ".retire-" + uuid.uuid4().hex
        self.move_without_replacement("owned.bin", captured)
        self.assertEqual(self.captured_identity(captured), expected)
        self.assertEqual(self.identity(os.fstat(fd)), expected)
        os.unlink(captured, dir_fd=self.root_fd)
        self.assertEqual(os.listdir(self.root_fd), [])
        os.lseek(fd, 0, os.SEEK_SET)
        self.assertEqual(os.read(fd, 100), b"owned")

    def test_replacement_after_observation_is_captured_then_restored_not_deleted(self):
        fd, expected = self.file("owned.bin")
        self.assertEqual(self.captured_identity("owned.bin"), expected)
        self.move_without_replacement("owned.bin", "displaced-owned.bin")
        self.file("owned.bin", b"foreign-replacement")
        captured = ".retire-" + uuid.uuid4().hex
        self.move_without_replacement("owned.bin", captured)
        self.assertNotEqual(self.captured_identity(captured), expected)
        # Mismatch branch restores with NO_REPLACE and never calls unlink.
        self.move_without_replacement(captured, "owned.bin")
        self.assertEqual((self.root / "owned.bin").read_bytes(), b"foreign-replacement")
        self.assertEqual((self.root / "displaced-owned.bin").read_bytes(), b"owned")
        self.assertEqual(self.identity(os.fstat(fd)), expected)

    def test_restore_collision_retains_both_foreign_files(self):
        _, expected = self.file("owned.bin")
        self.move_without_replacement("owned.bin", "displaced-owned.bin")
        self.file("owned.bin", b"first-foreign")
        captured = ".retire-" + uuid.uuid4().hex
        self.move_without_replacement("owned.bin", captured)
        self.assertNotEqual(self.captured_identity(captured), expected)
        self.file("owned.bin", b"second-foreign")
        with self.assertRaises(OSError) as collision:
            self.move_without_replacement(captured, "owned.bin")
        self.assertEqual(collision.exception.errno, errno.EEXIST)
        self.assertEqual((self.root / captured).read_bytes(), b"first-foreign")
        self.assertEqual((self.root / "owned.bin").read_bytes(), b"second-foreign")
        self.assertEqual((self.root / "displaced-owned.bin").read_bytes(), b"owned")

    def test_evidence_chmod_and_readback_use_held_file_after_name_replacement(self):
        fd, expected = self.file("evidence.json", b"owned-evidence", mode=0o644)
        self.move_without_replacement("evidence.json", "displaced-evidence.json")
        foreign_fd, foreign_identity = self.file("evidence.json", b"foreign-evidence", mode=0o644)
        os.fchmod(foreign_fd, 0o644)
        os.fchmod(fd, 0o600)
        held = os.fstat(fd)
        self.assertEqual(self.identity(held), expected)
        self.assertEqual(held.st_mode & 0o7777, 0o600)
        self.assertNotEqual(self.captured_identity("evidence.json"), expected)
        self.assertEqual(self.identity(os.fstat(foreign_fd)), foreign_identity)
        self.assertEqual(os.fstat(foreign_fd).st_mode & 0o7777, 0o644)
        os.lseek(fd, 0, os.SEEK_SET)
        self.assertEqual(os.read(fd, 100), b"owned-evidence")
        self.assertEqual((self.root / "evidence.json").read_bytes(), b"foreign-evidence")

    def test_child_directory_replacement_never_deletes_foreign_contents(self):
        os.mkdir("fixture", mode=0o700, dir_fd=self.root_fd)
        fd = os.open("fixture", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                     dir_fd=self.root_fd)
        self.addCleanup(os.close, fd)
        expected = self.identity(os.fstat(fd))
        self.move_without_replacement("fixture", "displaced-fixture")
        os.mkdir("fixture", mode=0o700, dir_fd=self.root_fd)
        marker = self.root / "fixture" / "foreign.bin"
        marker.write_bytes(b"foreign-directory")
        captured = ".retire-" + uuid.uuid4().hex
        self.move_without_replacement("fixture", captured)
        self.assertNotEqual(self.captured_identity(captured), expected)
        self.move_without_replacement(captured, "fixture")
        self.assertEqual(marker.read_bytes(), b"foreign-directory")
        self.assertEqual(self.identity(os.fstat(fd)), expected)


if __name__ == "__main__":
    unittest.main()
