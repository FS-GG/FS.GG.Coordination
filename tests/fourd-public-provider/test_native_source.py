#!/usr/bin/env python3
"""Actual compiled constructor controls; no native/image/credential operations."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def inventory(root):
    digest = hashlib.sha256()
    for path in sorted(root.rglob("*"), key=lambda p: p.relative_to(root).as_posix()):
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode() + b"\0")
            digest.update(path.read_bytes())
            digest.update(b"\0")
    return digest.hexdigest()


class NativeSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix="fourd-native-source-build-", dir="/tmp")
        cls.exe = Path(cls.build.name) / "publish/FourD.Typed"
        build = subprocess.run([
            "dotnet", "publish", str(ROOT / "eng/fourd-public-provider/typed/FourD.Typed.fsproj"),
            "-c", "Release", "-p:RestoreLockedMode=true", "-p:BuildInParallel=false", "-m:1",
            f"-p:BaseIntermediateOutputPath={cls.build.name}/obj/",
            f"-p:BaseOutputPath={cls.build.name}/bin/", "-o", str(cls.exe.parent),
        ], cwd=ROOT, env={**os.environ, "DOTNET_PROCESSOR_COUNT": "2"}, check=False,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            raise RuntimeError(build.stdout.decode("utf-8", errors="replace"))

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="fourd-native-source-", dir="/tmp")
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        self.source = self.work / "snapshot"
        self.source.mkdir(mode=0o700)
        (self.source / "file.fs").write_bytes(b"module Example\nlet value = 42\n")
        self.request = {
            "schema": "fsgg.fourd.native-source-request/1", "sourceRoot": str(self.source),
            "expectedRevision": "a" * 40, "expectedTree": "b" * 40,
            "observedRevision": "a" * 40, "observedTree": "b" * 40,
            "expectedInventorySha256": inventory(self.source),
        }

    def invoke(self, request=None, *, raw=None):
        input_path, output_path = self.work / "request.json", self.work / "result.json"
        if output_path.exists():
            output_path.unlink()
        input_path.write_text(raw if raw is not None else json.dumps(request or self.request))
        result = subprocess.run([str(self.exe), "validate-native-source", str(input_path), str(output_path)],
                                cwd=ROOT, capture_output=True, timeout=10)
        self.assertEqual(b"", result.stdout)
        self.assertEqual(b"", result.stderr)
        return result.returncode, json.loads(output_path.read_bytes()) if output_path.exists() else None

    def refused(self, request=None, **kwargs):
        code, result = self.invoke(request, **kwargs)
        self.assertEqual(2, code)
        self.assertIsNone(result)

    def test_actual_bytes_and_unicode_order_bind_identity(self):
        # Python scalar order and UTF-16 ordinal order differ for these names.
        for name in ("\ue000.fs", "\U00010000.fs", "nested/empty"):
            path = self.source / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(b"")
        self.request["expectedInventorySha256"] = inventory(self.source)
        code, result = self.invoke()
        self.assertEqual(0, code)
        self.assertEqual(4, result["fileCount"])
        self.assertEqual(len((self.source / "file.fs").read_bytes()), result["totalBytes"])
        self.assertEqual(self.request["expectedInventorySha256"], result["inventorySha256"])
        joined = "\0".join(self.request[key] for key in (
            "expectedRevision", "expectedTree", "expectedInventorySha256"))
        self.assertEqual(hashlib.sha256(joined.encode()).hexdigest(), result["identity"])

    def test_raw_git_commit_reconstruction_under_umask077(self):
        repo, snapshot = self.work / "git", self.work / "reconstructed"
        repo.mkdir()
        def git(*arguments):
            return subprocess.run(["git", "-C", str(repo), *arguments], check=True,
                                  capture_output=True).stdout.strip().decode()
        git("init", "-q")
        (repo / "file.fs").write_bytes(b"module Committed\n")
        git("add", "file.fs")
        git("-c", "user.name=Local control", "-c", "user.email=local@example.invalid", "commit", "-qm", "source")
        revision, tree = git("rev-parse", "HEAD"), git("rev-parse", "HEAD^{tree}")
        archive = self.work / "source.tar"
        git("archive", "--output", str(archive), revision)
        previous = os.umask(0o077)
        try:
            snapshot.mkdir(mode=0o700)
            with tarfile.open(archive) as package:
                package.extractall(snapshot, filter="data")
        finally:
            os.umask(previous)
        self.request.update(sourceRoot=str(snapshot), expectedRevision=revision, observedRevision=revision,
                            expectedTree=tree, observedTree=tree, expectedInventorySha256=inventory(snapshot))
        self.assertEqual(0, self.invoke()[0])

    def test_stale_source_revision_tree_and_inventory_refuse(self):
        self.assertEqual(0, self.invoke()[0])  # Same source/setup passes before changing only the join.
        for field, value in (("observedRevision", "c" * 40), ("observedTree", "c" * 40),
                             ("expectedInventorySha256", "0" * 64), ("expectedRevision", "Ａ" * 40)):
            with self.subTest(field=field):
                self.refused({**self.request, field: value})

    def test_changed_missing_and_extra_bytes_refuse(self):
        self.assertEqual(0, self.invoke()[0])
        file = self.source / "file.fs"
        file.write_bytes(b"module Changed\n")
        self.refused()
        file.unlink()
        self.refused()
        file.write_bytes(b"module Example\nlet value = 42\n")
        (self.source / "extra").write_bytes(b"")
        self.refused()

    def test_replacement_symlink_and_directory_symlink_refuse_without_touching_target(self):
        target = self.work / "replacement"
        target.write_bytes((self.source / "file.fs").read_bytes())
        (self.source / "file.fs").unlink()
        (self.source / "file.fs").symlink_to(target)
        self.refused()
        self.assertEqual(b"module Example\nlet value = 42\n", target.read_bytes())
        (self.source / "file.fs").unlink()
        (self.source / "linked").symlink_to(self.work, target_is_directory=True)
        self.refused()

    def test_linked_source_root_and_ancestor_refuse(self):
        alias = self.work / "alias"
        alias.symlink_to(self.work, target_is_directory=True)
        self.refused({**self.request, "sourceRoot": str(alias / "snapshot")})

    def test_fifo_and_oversize_sparse_file_refuse_without_reading(self):
        (self.source / "file.fs").unlink()
        os.mkfifo(self.source / "fifo")
        self.refused()
        (self.source / "fifo").unlink()
        with (self.source / "large").open("wb") as output:
            output.truncate(64 * 1024 * 1024 + 1)
        self.refused()

    def test_duplicate_extra_missing_and_wrong_scalar_refuse(self):
        encoded = json.dumps(self.request)
        self.refused(raw='{"schema":"duplicate",' + encoded[1:])
        self.refused({**self.request, "accepted": True})
        self.refused({key: value for key, value in self.request.items() if key != "expectedTree"})
        self.refused({**self.request, "observedRevision": True})


if __name__ == "__main__":
    unittest.main()
