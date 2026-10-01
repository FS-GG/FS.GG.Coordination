#!/usr/bin/env python3
"""Focused tests for package-bound FSI qualification preparation."""

from __future__ import annotations

import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
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
            archive.writestr("tools/net10.0/any/Akka.dll", b"akka")
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
        assert "/assemblies/Akka.dll" in rewritten.splitlines()[0]
        assert "/assemblies/FS.GG.Coordination.Orchestration.Execution.dll" in rewritten.splitlines()[1]
        assert MODULE.REFERENCE not in rewritten
        assert prepared["assemblyCount"] == "3"
        assert prepared["executionAssemblySha256"] == hashlib.sha256(b"execution").hexdigest()
        assert prepared["fsiReferences"] == [
            {"entry": "tools/net10.0/any/Akka.dll", "sha256": hashlib.sha256(b"akka").hexdigest()},
            {"entry": "tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", "sha256": hashlib.sha256(b"execution").hexdigest()},
        ]

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

        missing = root / "missing-akka.nupkg"
        with zipfile.ZipFile(missing, "w") as archive:
            archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", b"execution")
        try:
            MODULE.prepare(missing, hashlib.sha256(missing.read_bytes()).hexdigest(), ROOT / "eng" / "portable-workspace-executor-qualification.fsx", root / "missing-work")
            raise AssertionError("missing Akka assembly was accepted")
        except ValueError as error:
            assert str(error) == "package lacks required FSI reference: Akka.dll"

        tampered = root / "tampered.nupkg"
        tampered.write_bytes(package.read_bytes() + b"tampered")
        try:
            MODULE.prepare(tampered, package_sha, ROOT / "eng" / "portable-workspace-executor-qualification.fsx", root / "tampered-work")
            raise AssertionError("tampered package was accepted")
        except ValueError as error:
            assert str(error) == "package digest changed"

        real_package_value = os.environ.get("PACKAGED_QUALIFICATION_REAL_PACKAGE")
        if real_package_value:
            real_package = Path(real_package_value).resolve()
            probe = root / "akka-probe.fsx"
            probe.write_text(MODULE.REFERENCE + "\nopen FS.GG.Coordination.Orchestration.Execution\nlet props: Akka.Actor.Props = ExecutionSessionActor.Props(Unchecked.defaultof<ExecutionSessionCoordinator>)\nprintfn \"PACKAGED_FSI_AKKA_OK %s\" props.Type.FullName\n", encoding="utf-8")
            real = MODULE.prepare(real_package, hashlib.sha256(real_package.read_bytes()).hexdigest(), probe, root / "real-work")
            completed = subprocess.run(["dotnet", "fsi", real["rewrittenScript"]], cwd=ROOT, text=True, capture_output=True)
            assert completed.returncode == 0, completed.stderr
            assert "PACKAGED_FSI_AKKA_OK FS.GG.Coordination.Orchestration.Execution.ExecutionSessionActor" in completed.stdout
            canonical_real = MODULE.prepare(real_package, hashlib.sha256(real_package.read_bytes()).hexdigest(), ROOT / "eng" / "portable-workspace-executor-qualification.fsx", root / "real-canonical-work")
            canonical_check = subprocess.run(["dotnet", "fsi", canonical_real["rewrittenScript"]], cwd=ROOT, text=True, capture_output=True)
            assert canonical_check.returncode != 0
            assert "missing --fixture" in canonical_check.stderr
            assert "error FS0074" not in canonical_check.stderr


if __name__ == "__main__":
    main()
