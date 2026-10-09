#!/usr/bin/env python3
"""Focused exact release-readback collision tests."""

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "eng/write-callable-cli-release-readback.py"
SOURCE = "1" * 40
TREE = "2" * 40
MERGE = "3" * 40
PACKAGE = "4" * 64


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="release-readback-test-") as temporary:
        work = Path(temporary)
        github = work / "github.json"
        nuget = work / "nuget.json"
        write(github, {"feed":"github-packages","candidateSha256":PACKAGE,"servedArchiveSha256":"5"*64,"payloadMatch":True})
        write(nuget, {"feed":"nuget-org","candidateSha256":PACKAGE,"servedArchiveSha256":"6"*64,"payloadMatch":True})
        output = work / "expected.json"
        command = [
            "python3", str(SCRIPT), "--source", SOURCE, "--tree", TREE, "--merge", MERGE,
            "--package-id", "FS.GG.Coordination.Cli", "--version", "0.2.0",
            "--package-sha256", PACKAGE, "--bundle-sha256", "7"*64,
            "--image-sha256", "8"*64, "--manifest-sha256", "9"*64,
            "--github-feed", str(github), "--nuget-feed", str(nuget), "--output", str(output),
        ]
        subprocess.run(command, check=True)
        exact = work / "existing-exact.json"
        exact.write_bytes(output.read_bytes())
        subprocess.run([*command, "--existing", str(exact)], check=True)

        tampered = json.loads(output.read_text())
        tampered["schema"] = "wrong-schema"
        tampered["packageId"] = "wrong"
        tampered["version"] = "wrong"
        tampered["feeds"] = {}
        tampered["extra"] = True
        wrong = work / "existing-wrong.json"
        write(wrong, tampered)
        refused = subprocess.run([*command, "--existing", str(wrong)], text=True, capture_output=True)
        assert refused.returncode == 2
        assert "differs from the complete expected receipt" in refused.stderr

        cli = command[:]
        for flag in ('--bundle-sha256','--image-sha256','--manifest-sha256'):
            index=cli.index(flag); del cli[index:index+2]
        cli[cli.index('--version')+1]='0.3.0'
        cli.extend(['--cli-only','--publisher','a'*40])
        subprocess.run(cli,check=True)
        value=json.loads(output.read_bytes())
        assert value['schema']=='fsgg.coordination.callable-cli-release-readback/3'
        assert value['source']==SOURCE and value['publisherSource']=='a'*40
        assert 'portableAssets' not in value
        for extra in [['--bundle-sha256','7'*64], ['--version','0.2.0'], ['--publisher','invalid']]:
            refused=subprocess.run([*cli,*extra],capture_output=True)
            assert refused.returncode==2
        assert subprocess.run([*command,'--cli-only'],capture_output=True).returncode==2
        github.unlink()
        absent_feed = subprocess.run([*command, "--existing", str(exact)], text=True, capture_output=True)
        assert absent_feed.returncode == 2
        assert "verified github-packages feed result is absent" in absent_feed.stderr


if __name__ == "__main__":
    main()
