#!/usr/bin/env python3
"""Build one inactive UTEL C fixture into a fresh directory; no release or credential effect."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = "src/FS.GG.Coordination.Orchestration.Execution/Custody/utel-auth-bootstrap.c"
INPUTS = (
    SOURCE,
    "src/FS.GG.Coordination.Orchestration.Execution/Custody/build-utel-auth.py",
    "tests/utel-auth-bootstrap/test_static.py",
    "tests/utel-auth-bootstrap/test_runtime.py",
    ".github/workflows/utel-auth-bootstrap-qualification.yml",
    "evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json",
)
FLAGS = (
    "-static", "-Os", "-s", "-fstack-protector-strong", "-D_FORTIFY_SOURCE=3",
    "-Wl,--build-id=none", "-Wall", "-Wextra", "-Werror",
)
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
MAX_BINARY_BYTES = 2 * 1024 * 1024


def fail(reason: str) -> None:
    raise SystemExit(f"utel-auth-bootstrap-build refused: {reason}")


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def capture(argv: list[str], *, timeout: int = 30) -> str:
    result = subprocess.run(argv, check=True, capture_output=True, text=True, timeout=timeout)
    return result.stdout


def build(output: pathlib.Path, head: str, run_id: int, attempt: int) -> pathlib.Path:
    if not HEX40.fullmatch(head) or run_id < 1 or attempt < 1:
        fail("invalid source/run identity")
    if capture(["git", "rev-parse", "HEAD"], timeout=5).strip() != head:
        fail("checked-out source differs")
    if os.uname().machine != "x86_64" or not sys.platform.startswith("linux"):
        fail("unsupported build platform")
    runner_temp = os.environ.get("RUNNER_TEMP")
    if runner_temp and output.parent.resolve() != pathlib.Path(runner_temp).resolve():
        fail("output must be directly under runner temporary root")
    if output.exists() or output.is_symlink() or not output.parent.is_dir():
        fail("output must be a fresh directory")
    for relative in INPUTS:
        path = ROOT / relative
        if not path.is_file() or path.is_symlink():
            fail(f"missing or linked closure input: {relative}")
    compiler = pathlib.Path("/usr/bin/gcc").resolve()
    if not compiler.is_file():
        fail("selected compiler unavailable")
    version = capture([str(compiler), "--version"], timeout=5).splitlines()[0]
    compiler_hash = digest(compiler)
    output.mkdir(mode=0o700)
    binary = output / "utel-auth-bootstrap.linux-x64.bin"
    subprocess.run([str(compiler), *FLAGS, str(ROOT / SOURCE), "-o", str(binary)],
                   check=True, timeout=30, cwd=ROOT)
    body = binary.read_bytes()
    if not 0 < len(body) <= MAX_BINARY_BYTES or body[:4] != b"\x7fELF":
        fail("compiled binary is missing, oversized or not ELF")
    header = capture(["readelf", "-h", str(binary)], timeout=5)
    segments = capture(["readelf", "-l", str(binary)], timeout=5)
    dynamic = capture(["readelf", "-d", str(binary)], timeout=5)
    if (not re.search(r"Class:\s+ELF64\b", header)
            or not re.search(r"Machine:\s+Advanced Micro Devices X86-64\b", header)
            or "INTERP" in segments or "NEEDED" in dynamic):
        fail("ELF architecture or static linkage refused")
    manifest = {
        "schema": "fsgg.utel.auth-bootstrap.inert-artifact/1",
        "profile": "linux-x64-utel-auth-inert-source/1",
        "referenceOperation": "utel-rel-21/original-tag-authentication-only/2",
        "source": {"repository": "FS-GG/FS.GG.Coordination", "head": head,
                   "runId": run_id, "attempt": attempt},
        "inputs": {relative: digest(ROOT / relative) for relative in INPUTS},
        "compiler": {"path": str(compiler), "sha256": compiler_hash,
                     "version": version, "flags": list(FLAGS)},
        "elf": {"class": "ELF64", "machine": "Advanced Micro Devices X86-64",
                "interpreter": False, "neededLibraries": []},
        "binary": {"path": binary.name, "bytes": len(body),
                   "sha256": hashlib.sha256(body).hexdigest()},
        "entrypoints": ["--describe", "--inert-fixture"],
        "capabilities": {"productionAvailable": False, "credentialIngressAvailable": False,
                         "sameTokenCleanupAvailable": False,
                         "nativeConstructorAvailable": False, "authorized": False},
    }
    manifest_path = output / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n",
                             encoding="utf-8")
    return manifest_path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--head", required=True)
    parser.add_argument("--run-id", required=True, type=int)
    parser.add_argument("--attempt", required=True, type=int)
    args = parser.parse_args()
    try:
        manifest = build(args.output, args.head, args.run_id, args.attempt)
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        fail(type(error).__name__)
    print(manifest)


if __name__ == "__main__":
    main()
