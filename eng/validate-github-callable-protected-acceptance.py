#!/usr/bin/env python3
"""Revalidate the one protected callable operation and discovery handoff.

This reads immutable historical evidence and public provider identities. It never
dispatches an operation, mints a credential, or changes a GitHub authority.
"""

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
NATIVE = ROOT / "evidence/github-substrate-v2/gs2-09-9/native-acceptance.json"
READINESS_MERGE = "1de1393750951065eb38950586bc6aefe24e264b"
PINNED = {
    "evidence/github-substrate-v2/gs2-09-9/native-acceptance.json": "3cb0dbf912c406f6a3a20607f06c307da69e7e50e0551936f31ef0dd7832b67e",
    "evidence/github-substrate-v2/gs2-09-9/callable-discovery-handoff.json": "b71ab73cb6c9fa98a250c9b4a97b1c6df1c969cc1165699428f79041c1ea6235",
    "eng/validate-callable-native-acceptance.py": "87eedfa76e0a3daddbf6865c08a183d305ed577dcc340531edcb2487845a4980",
    "eng/validate-callable-discovery-handoff.py": "b7be648e103dd48772637dc2c23ad1c0c788aa8587caf185708cc2a4f340fc89",
}


def run(*args: str, cwd: pathlib.Path = ROOT, input_bytes: bytes | None = None) -> bytes:
    result = subprocess.run(args, cwd=cwd, input=input_bytes, capture_output=True)
    if result.returncode:
        raise ValueError(f"{args[0]} {args[1] if len(args) > 1 else ''} failed: "
                         f"{result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def main() -> int:
    for relative, expected in PINNED.items():
        if hashlib.sha256((ROOT / relative).read_bytes()).hexdigest() != expected:
            raise ValueError(f"pinned historical bytes changed: {relative}")
    evidence = json.loads(NATIVE.read_bytes())
    if evidence.get("schema") != "fsgg.coordination.callable-native-acceptance/1":
        raise ValueError("native evidence schema")
    prior = evidence["retainedInterruption"]["checkpointArtifactId"]
    settled = evidence["settledArtifacts"]
    ids = [prior, settled["checkpointArtifactId"], settled["receiptArtifactId"]]
    if ids != [10647264981, 10654184892, 10653844968]:
        raise ValueError("native artifact identities")

    with tempfile.TemporaryDirectory(prefix="gs2-09-9-acceptance-") as directory:
        temporary = pathlib.Path(directory)
        archives = []
        for identity in ids:
            archive = temporary / f"{identity}.zip"
            archive.write_bytes(run("gh", "api", f"repos/FS-GG/.github/actions/artifacts/{identity}/zip"))
            archives.append(archive)
        validator = ROOT / "eng/validate-callable-native-acceptance.py"
        arguments = [sys.executable, str(validator), str(NATIVE), *(str(path) for path in archives)]
        run(*arguments)

        corrupt = temporary / "corrupt.zip"
        damaged = bytearray(archives[0].read_bytes())
        if not damaged:
            raise ValueError("empty prior archive")
        damaged[-1] ^= 1
        corrupt.write_bytes(damaged)
        negative = subprocess.run([sys.executable, str(validator), str(NATIVE), str(corrupt),
                                   str(archives[1]), str(archives[2])], cwd=ROOT,
                                  capture_output=True)
        if negative.returncode == 0:
            raise ValueError("corrupt native archive was accepted")

        historical = temporary / "readiness-source"
        run("git", "worktree", "add", "--detach", str(historical), READINESS_MERGE)
        try:
            run(sys.executable, str(historical / "eng/validate-callable-readiness.py"), "--live", cwd=historical)
        finally:
            run("git", "worktree", "remove", "--force", str(historical))

        run(sys.executable, "eng/validate-callable-discovery-handoff.py", "--live")

    print("GS2-09.9 protected native acceptance and handoff revalidated; no provider mutation")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (KeyError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"callable-protected-acceptance-refused: {error}", file=sys.stderr)
        sys.exit(1)
