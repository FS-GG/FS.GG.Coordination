#!/usr/bin/env python3
"""Verify the preparation artifact downloads into the publisher's expected root."""

from __future__ import annotations

from pathlib import Path
import tempfile
import os
import subprocess
import zipfile


ROOT = Path(__file__).resolve().parents[3]
EXPECTED_ROOT_FILES = {
    "FS.GG.Coordination.Cli.0.2.0.nupkg",
    "callable-cli-release-manifest.json",
    "portable-workspace-v1-0.2.0.zip",
    "portable-workspace-linux-amd64-0.2.0.oci.tar",
    "portable-workspace-release-manifest.json",
    "portable-workspace-image-manifest.json",
    "portable-workspace-image-qualification.json",
    "portable-workspace-executor-evidence.json",
    "portable-workspace-packaged-qualification.json",
}



def check_cli_only(job: str) -> None:
    assert "inputs.preparation_mode == 'cli-only'" in job
    assert "podman" not in job and "portable-workspace" not in job
    assert "nuget push" not in job and "contents: write" not in job
    assert "callable-cli-only-${{ inputs.source }}" in job
    assert "retention-days: 90" in job
    prepare = job.split("- name: Prepare and install", 1)[1]
    assert "callable-cli-release.fsx prepare" in prepare
    assert "callable-cli-release.fsx verify" in prepare
    assert "--locked-mode" in prepare
    block = job.split("- name: Bind CLI-only source", 1)[1].split("- name: Set up", 1)[0]
    shell = block.split("run: |\n", 1)[1]
    shell = "\n".join(line[10:] for line in shell.splitlines() if line.strip())
    with tempfile.TemporaryDirectory(prefix="cli-only-preflight-") as temporary:
        root = Path(temporary)
        git = root / "git"
        git.write_text("#!/bin/sh\ncase \"$1\" in\n rev-parse) echo \"$OBSERVED_SOURCE\";;\n fetch) exit 0;;\n status) printf '%s' \"$DIRTY\";;\n *) exit 91;;\nesac\n")
        git.chmod(0o700)
        source = "a" * 40
        env = dict(os.environ, PATH=str(root)+os.pathsep+os.environ["PATH"],
                   GITHUB_REF="refs/heads/main", GITHUB_RUN_ATTEMPT="1",
                   CANDIDATE_SOURCE=source, GITHUB_SHA=source, OBSERVED_SOURCE=source,
                   CANDIDATE_VERSION="0.3.0", CANDIDATE_OUTPUT=str(root/"candidate"), DIRTY="")
        cases = [{}, {"GITHUB_REF":"refs/heads/topic"}, {"GITHUB_RUN_ATTEMPT":"2"},
                 {"CANDIDATE_SOURCE":"invalid"}, {"CANDIDATE_VERSION":"0.2.1"},
                 {"GITHUB_SHA":"b"*40}, {"OBSERVED_SOURCE":"b"*40}, {"DIRTY":"M file"}]
        for index, change in enumerate(cases):
            result = subprocess.run(["bash", "-c", shell], env={**env, **change},
                                    capture_output=True, timeout=5)
            assert (result.returncode == 0) == (index == 0), (change, result.stderr)
            assert not (root/"candidate").exists()
        (root/"candidate").mkdir()
        result = subprocess.run(["bash", "-c", shell], env=env, capture_output=True, timeout=5)
        assert result.returncode != 0


def main() -> None:
    workflow = (ROOT / ".github/workflows/callable-cli-release-prepare.yml").read_text()
    cli_only = workflow.split("\n  cli_only:", 1)[1]
    workflow = workflow.split("\n  cli_only:", 1)[0]
    check_cli_only(cli_only)
    assert "ARTIFACT_ROOT: /tmp/pw-artifact-${{ github.run_id }}-${{ github.run_attempt }}" in workflow
    assert "EVIDENCE_ROOT: /tmp/pw-evidence-${{ github.run_id }}-${{ github.run_attempt }}" in workflow
    assert 'install -d -m 0700 "$ARTIFACT_ROOT/evidence"' in workflow
    assert 'cp "$CANDIDATE_OUTPUT"/* "$ARTIFACT_ROOT/"' in workflow
    assert 'cp "$RELEASE_OUTPUT"/* "$ARTIFACT_ROOT/"' in workflow
    assert 'cp "$EVIDENCE_ROOT"/*.json "$ARTIFACT_ROOT/evidence/"' in workflow
    assert "path: ${{ env.ARTIFACT_ROOT }}/" in workflow
    diagnostics = workflow.index("- name: Retain bounded preparation diagnostics")
    cleanup = workflow.index("- name: Remove exact private runtime state")
    upload = workflow.index("- name: Retain the coherent candidate and bounded evidence")
    assert diagnostics < cleanup < upload
    assert '"qualificationPassed": status == "success"' in workflow
    assert '("*.json", "preflight.json", "manifests/*.json", "runs/*/journal.jsonl")' in workflow
    assert 'source.is_symlink() or source.suffix not in (".json", ".jsonl") or info.st_size > 1048576' in workflow
    assert 'podman unshare /usr/bin/rm -rf -- "$path"' in workflow
    assert 'refusing cleanup outside exact run scope' in workflow
    assert "shutil.rmtree" not in workflow
    upload_text = workflow.split("- name: Retain the coherent candidate and bounded evidence", 1)[1]
    assert "CANDIDATE_OUTPUT" not in upload_text
    assert "EVIDENCE_ROOT }}" not in upload_text

    with tempfile.TemporaryDirectory(prefix="portable-artifact-layout-") as temporary:
        root = Path(temporary)
        artifact = root / "artifact"
        evidence = artifact / "evidence"
        evidence.mkdir(parents=True)
        for name in EXPECTED_ROOT_FILES:
            (artifact / name).write_text(name, encoding="utf-8")
        (evidence / "cleanup-state.json").write_text("{}\n", encoding="utf-8")
        archive = root / "upload.zip"
        with zipfile.ZipFile(archive, "w") as output:
            for path in sorted(artifact.rglob("*")):
                if path.is_file():
                    output.write(path, path.relative_to(artifact))
        downloaded = root / "downloaded"
        with zipfile.ZipFile(archive) as source:
            source.extractall(downloaded)
        assert EXPECTED_ROOT_FILES <= {path.name for path in downloaded.iterdir() if path.is_file()}
        assert (downloaded / "evidence" / "cleanup-state.json").is_file()
        assert not (downloaded / artifact.name).exists()


if __name__ == "__main__":
    main()
