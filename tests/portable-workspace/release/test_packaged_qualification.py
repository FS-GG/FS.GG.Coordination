#!/usr/bin/env python3
"""Focused tests for package-bound FSI qualification preparation."""

from __future__ import annotations

import hashlib
import importlib.util
from pathlib import Path
import sys
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[3]
sys.dont_write_bytecode = True
MODULE_PATH = ROOT / "eng" / "run-packaged-portable-workspace-qualification.py"
SPEC = importlib.util.spec_from_file_location("packaged_qualification", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="packaged-qualification-test-") as temporary:
        root = Path(temporary)
        package = root / "candidate.nupkg"
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("tools/net10.0/any/Dependency.dll", b"dependency")
            archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", b"execution")
        package_sha = hashlib.sha256(package.read_bytes()).hexdigest()
        prepared = MODULE.prepare(
            package,
            package_sha,
            ROOT / "eng" / "portable-workspace-executor-qualification.fsx",
            root / "work",
        )
        rewritten = Path(prepared["rewrittenScript"]).read_text()
        assert rewritten.startswith('#r @"')
        assert "/assemblies/FS.GG.Coordination.Orchestration.Execution.dll" in rewritten.splitlines()[0]
        assert MODULE.REFERENCE not in rewritten
        assert prepared["assemblyCount"] == "2"
        assert prepared["executionAssemblySha256"] == hashlib.sha256(b"execution").hexdigest()

        try:
            MODULE.prepare(package, "0" * 64, ROOT / "eng" / "portable-workspace-executor-qualification.fsx", root / "wrong")
            raise AssertionError("wrong package digest was accepted")
        except ValueError as error:
            assert str(error) == "package digest changed"

        duplicate = root / "duplicate.nupkg"
        with zipfile.ZipFile(duplicate, "w") as archive:
            archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", b"one")
            archive.writestr("tools/net10.0/any/sub/../FS.GG.Coordination.Orchestration.Execution.dll", b"two")
        try:
            MODULE.prepare(duplicate, hashlib.sha256(duplicate.read_bytes()).hexdigest(), ROOT / "eng" / "portable-workspace-executor-qualification.fsx", root / "duplicate-work")
            raise AssertionError("unsafe package path was accepted")
        except ValueError as error:
            assert "unsafe path" in str(error)


if __name__ == "__main__":
    main()
