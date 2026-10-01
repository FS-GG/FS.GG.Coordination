#!/usr/bin/env python3
"""Prove the canonical Python fixture, its projections, and real entrypoint."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[3]
RECORD = Path(__file__).with_name("python-fixture-provenance.json")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    value = json.loads(RECORD.read_text(encoding="utf-8"))
    assert set(value) == {"schema", "sourceRoot", "projections", "files"}
    assert value["schema"] == "fsgg.portable-workspace-python-fixture-provenance/1"
    assert value["sourceRoot"] == "tests/portable-workspace/image/fixture/python"
    assert value["projections"] == [
        "tests/portable-workspace/executor-qualification/fixture/python",
        "tests/portable-workspace/executor/python",
    ]
    expected = {item["path"]: item["sha256"] for item in value["files"]}
    assert set(expected) == {"app.py", "build.py", "test.py"}
    source = ROOT / value["sourceRoot"]
    for name, digest in expected.items():
        assert sha256(source / name) == digest
        for projection in value["projections"]:
            target = ROOT / projection / name
            assert target.read_bytes() == (source / name).read_bytes()
            assert sha256(target) == digest

    environment = {
        "HOME": os.environ.get("HOME", "/tmp"),
        "PATH": "/usr/local/bin:/usr/bin:/bin",
    }
    greeting = subprocess.run(
        [sys.executable, "app.py"],
        cwd=source,
        env=environment,
        check=True,
        capture_output=True,
        text=True,
        timeout=10,
    )
    assert greeting.stdout == "hello from portable python\n"
    assert greeting.stderr == ""

    with tempfile.TemporaryDirectory(prefix="portable-python-fixture-") as output:
        qualified = subprocess.run(
            [sys.executable, "test.py"],
            cwd=source,
            env={**environment, "PORTABLE_OUTPUT_ROOT": output},
            check=True,
            capture_output=True,
            text=True,
            timeout=20,
        )
        assert qualified.stdout == "python-build-ok\npython-test-ok\n"
        assert qualified.stderr == ""
        output_root = Path(output)
        assert (output_root / "python/app.pyc").is_file()
        assert (output_root / "python-test.json").read_bytes() == (
            b'{"outcome":"passed","verification":"python-test-v1"}\n'
        )


if __name__ == "__main__":
    main()
