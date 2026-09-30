#!/usr/bin/env python3
"""Run portable qualification against the Execution DLL from an exact CLI package."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import zipfile


REFERENCE = '#r "../src/FS.GG.Coordination.Orchestration.Execution/bin/Debug/net10.0/FS.GG.Coordination.Orchestration.Execution.dll"'
EXECUTION_DLL = "FS.GG.Coordination.Orchestration.Execution.dll"
REQUIRED_FSI_REFERENCES = ("Akka.dll", EXECUTION_DLL)


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def prepare(package: Path, expected_package_sha256: str, script: Path, work: Path) -> dict[str, object]:
    if not package.is_file() or not script.is_file():
        raise ValueError("package and qualification script must exist")
    if len(expected_package_sha256) != 64 or any(value not in "0123456789abcdef" for value in expected_package_sha256):
        raise ValueError("expected package digest must be lowercase SHA-256")
    if sha256(package) != expected_package_sha256:
        raise ValueError("package digest changed")
    if work.exists() and any(work.iterdir()):
        raise ValueError("packaged qualification work directory must be empty")
    work.mkdir(parents=True, mode=0o700, exist_ok=True)
    os.chmod(work, 0o700)
    assemblies = work / "assemblies"
    assemblies.mkdir(mode=0o700)
    extracted: list[tuple[str, Path, str]] = []
    with zipfile.ZipFile(package) as archive:
        for info in archive.infolist():
            name = PurePosixPath(info.filename)
            if name.is_absolute() or ".." in name.parts:
                raise ValueError("package contains an unsafe path")
            if len(name.parts) == 4 and name.parts[:3] == ("tools", "net10.0", "any") and name.suffix == ".dll":
                target = assemblies / name.name
                if target.exists():
                    raise ValueError(f"package repeats assembly name: {name.name}")
                value = archive.read(info)
                target.write_bytes(value)
                os.chmod(target, 0o600)
                extracted.append((info.filename, target, hashlib.sha256(value).hexdigest()))
    execution = [item for item in extracted if item[1].name == EXECUTION_DLL]
    if len(execution) != 1:
        raise ValueError("package must contain exactly one Execution assembly")
    by_name = {item[1].name: item for item in extracted}
    missing = [name for name in REQUIRED_FSI_REFERENCES if name not in by_name]
    if missing:
        raise ValueError(f"package lacks required FSI reference: {', '.join(missing)}")
    source = script.read_text(encoding="utf-8-sig")
    if source.count(REFERENCE) != 1 or not source.startswith(REFERENCE + "\n"):
        raise ValueError("qualification script package reference changed")
    reference_items = [by_name[name] for name in REQUIRED_FSI_REFERENCES]
    replacement = "\n".join(f'#r @"{str(item[1].resolve()).replace(chr(34), chr(34) * 2)}"' for item in reference_items)
    rewritten = replacement + source[len(REFERENCE):]
    rewritten_path = work / "packaged-portable-workspace-qualification.fsx"
    rewritten_path.write_text(rewritten, encoding="utf-8", newline="\n")
    os.chmod(rewritten_path, 0o600)
    return {
        "packageSha256": expected_package_sha256,
        "sourceScriptSha256": sha256(script),
        "rewrittenScriptSha256": sha256(rewritten_path),
        "executionAssemblyEntry": execution[0][0],
        "executionAssemblySha256": execution[0][2],
        "fsiReferences": [
            {"entry": item[0], "sha256": item[2]}
            for item in reference_items
        ],
        "assemblyCount": str(len(extracted)),
        "rewrittenScript": str(rewritten_path),
    }


def main() -> int:
    if "--" not in sys.argv:
        raise SystemExit("qualification arguments must follow --")
    separator = sys.argv.index("--")
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--expected-package-sha256", required=True)
    parser.add_argument("--script", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path)
    parser.add_argument("--dotnet", default="/usr/bin/dotnet", type=Path)
    args = parser.parse_args(sys.argv[1:separator])
    qualification_args = sys.argv[separator + 1:]
    if not qualification_args or len(qualification_args) % 2:
        raise SystemExit("qualification arguments must be --name value pairs")
    try:
        prepared = prepare(
            args.package.resolve(),
            args.expected_package_sha256,
            args.script.resolve(),
            args.work_dir.resolve(),
        )
        dotnet = args.dotnet.resolve()
        if not dotnet.is_file():
            raise ValueError("dotnet host is absent")
        environment = {
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_PROCESSOR_COUNT": "4",
            "HOME": os.environ.get("HOME", "/tmp"),
            "LANG": "C.UTF-8",
            "PATH": "/usr/local/bin:/usr/bin:/bin",
        }
        completed = subprocess.run(
            [str(dotnet), "fsi", prepared["rewrittenScript"], "--", *qualification_args],
            cwd=args.script.resolve().parent.parent,
            env=environment,
            check=False,
        )
        evidence = None
        if "--evidence" in qualification_args:
            evidence_path = Path(qualification_args[qualification_args.index("--evidence") + 1]).resolve()
            if evidence_path.is_file():
                evidence = sha256(evidence_path)
        receipt = {
            "schema": "fsgg.portable-workspace-packaged-qualification/1",
            **prepared,
            "qualificationExitCode": completed.returncode,
            "qualificationEvidenceSha256": evidence,
            "publicationAuthorized": False,
            "activationAuthorized": False,
        }
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_bytes(canonical(receipt))
        os.chmod(args.receipt, 0o600)
        return completed.returncode
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print(f"PACKAGED_PORTABLE_QUALIFICATION_REFUSED {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
