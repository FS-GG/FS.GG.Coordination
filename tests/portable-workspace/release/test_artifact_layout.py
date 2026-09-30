#!/usr/bin/env python3
"""Verify the preparation artifact downloads into the publisher's expected root."""

from __future__ import annotations

from pathlib import Path
import tempfile
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


def main() -> None:
    workflow = (ROOT / ".github/workflows/callable-cli-release-prepare.yml").read_text()
    assert "ARTIFACT_ROOT: /tmp/pw-artifact-${{ github.run_id }}-${{ github.run_attempt }}" in workflow
    assert "EVIDENCE_ROOT: /tmp/pw-evidence-${{ github.run_id }}-${{ github.run_attempt }}" in workflow
    assert 'install -d -m 0700 "$ARTIFACT_ROOT/evidence"' in workflow
    assert 'cp "$CANDIDATE_OUTPUT"/* "$ARTIFACT_ROOT/"' in workflow
    assert 'cp "$RELEASE_OUTPUT"/* "$ARTIFACT_ROOT/"' in workflow
    assert 'cp "$EVIDENCE_ROOT"/*.json "$ARTIFACT_ROOT/evidence/"' in workflow
    assert "path: ${{ env.ARTIFACT_ROOT }}/" in workflow
    upload = workflow.split("- name: Retain the coherent candidate and bounded evidence", 1)[1]
    assert "CANDIDATE_OUTPUT" not in upload
    assert "EVIDENCE_ROOT }}" not in upload

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
