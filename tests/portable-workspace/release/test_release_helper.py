#!/usr/bin/env python3
"""Exercise portable release construction and fail-closed evidence checks."""

from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[3]


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_bytes(canonical(value))


def add_tar_file(archive: tarfile.TarFile, name: str, value: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(value)
    info.mtime = 0
    info.mode = 0o644
    archive.addfile(info, io.BytesIO(value))


def main() -> None:
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True).strip()

    with tempfile.TemporaryDirectory(prefix="portable-release-test-") as temporary:
        work = Path(temporary)
        package = work / "FS.GG.Coordination.Cli.0.2.0.nupkg"
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll", b"MZ-test")
        package_manifest = work / "callable-cli-release-manifest.json"
        write_json(package_manifest, {
            "schema": "fsgg.coordination.callable-cli-release-preparation/1",
            "packageId": "FS.GG.Coordination.Cli",
            "version": "0.2.0",
            "tag": "v0.2.0",
            "sourceCommit": source,
            "sourceTree": tree,
            "packageSha256": digest(package.read_bytes()),
            "publicationAuthorized": False,
            "tagAuthorized": False,
        })

        config = canonical({"architecture": "amd64", "os": "linux", "config": {"User": "32768:32768"}})
        config_digest = digest(config)
        layer = b"qualified-test-layer"
        layer_digest = digest(layer)
        image_manifest = canonical({
            "schemaVersion": 2,
            "mediaType": "application/vnd.oci.image.manifest.v1+json",
            "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": f"sha256:{config_digest}", "size": len(config)},
            "layers": [{"mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": f"sha256:{layer_digest}", "size": len(layer)}],
        })
        image_digest = digest(image_manifest)
        index_value = {
            "schemaVersion": 2,
            "manifests": [{
                "mediaType": "application/vnd.oci.image.manifest.v1+json",
                "digest": f"sha256:{image_digest}",
                "size": len(image_manifest),
            }],
        }
        index = canonical(index_value)
        image_archive = work / "candidate.oci.tar"
        def write_archive(index_bytes: bytes) -> None:
            with tarfile.open(image_archive, "w") as archive:
                add_tar_file(archive, "oci-layout", canonical({"imageLayoutVersion": "1.0.0"}))
                add_tar_file(archive, "index.json", index_bytes)
                add_tar_file(archive, f"blobs/sha256/{config_digest}", config)
                add_tar_file(archive, f"blobs/sha256/{layer_digest}", layer)
                add_tar_file(archive, f"blobs/sha256/{image_digest}", image_manifest)
        write_archive(index)

        reference = f"localhost/test@sha256:{image_digest}"
        image_id = f"sha256:{config_digest}"
        qualified_manifest = work / "image-manifest.json"
        write_json(qualified_manifest, {
            "schema": "fsgg.portable-workspace-local-image/1",
            "source": {"revision": source, "tree": tree},
            "image": {
                "reference": reference,
                "id": image_id,
                "digest": f"sha256:{image_digest}",
                "os": "linux",
                "architecture": "amd64",
            },
        })
        qualification = work / "image-qualification.json"
        write_json(qualification, {
            "candidateSha256": digest(image_archive.read_bytes()),
            "manifestSha256": digest(qualified_manifest.read_bytes()),
            "imageReference": reference,
            "imageId": image_id,
        })
        original_qualification = json.loads(qualification.read_text())
        evidence = work / "executor-evidence.json"
        passing = {
            "schema": "fsgg.portable-workspace-executor-qualification/1",
            "imageReference": reference,
            "imageId": image_id,
            "manifestSha256": digest(qualified_manifest.read_bytes()),
            "outcome": "passed",
            "strictAcceptance": True,
            "passed": 6,
            "failed": 0,
            "unknown": 0,
            "remainingExecutionRoots": 0,
            "duplicateOrPendingWithoutRelaunch": True,
            "sourceFenceBeforeWrite": True,
            "preCancelledBeforeLaunch": True,
            "hostEnvironmentInjectionCleared": True,
            "missingImageRefusedBeforeStart": True,
            "overflowArithmeticAccepted": True,
            "interruptedRecoveryNoRelaunch": True,
        }
        write_json(evidence, passing)

        arguments = [
            "dotnet", "fsi", "eng/portable-workspace-release.fsx", "--", "prepare",
            "--source", source, "--version", "0.2.0",
            "--package", str(package), "--package-manifest", str(package_manifest),
            "--image-archive", str(image_archive), "--image-manifest", str(qualified_manifest),
            "--image-qualification", str(qualification), "--executor-evidence", str(evidence),
        ]
        output = work / "release"
        subprocess.run([*arguments, "--output", str(output)], cwd=ROOT, check=True)
        expected = {
            "portable-workspace-v1-0.2.0.zip",
            "portable-workspace-linux-amd64-0.2.0.oci.tar",
            "portable-workspace-release-manifest.json",
        }
        assert {item.name for item in output.iterdir()} == expected
        release = json.loads((output / "portable-workspace-release-manifest.json").read_text())
        assert release["publicationAuthorized"] is False
        assert release["activationAuthorized"] is False
        assert release["imageArchiveSha256"] == digest(image_archive.read_bytes())

        bad_index = {**index_value, "manifests": [{**index_value["manifests"][0], "platform": {"os": "windows", "architecture": "amd64"}}]}
        write_archive(canonical(bad_index))
        write_json(qualification, {**original_qualification, "candidateSha256": digest(image_archive.read_bytes())})
        wrong_platform = subprocess.run([*arguments, "--output", str(work / "wrong-platform")], cwd=ROOT, text=True, capture_output=True)
        assert wrong_platform.returncode == 2
        assert "OCI image descriptor platform must be linux/amd64 when present" in wrong_platform.stderr
        write_archive(index)
        write_json(qualification, original_qualification)

        collision = subprocess.run([*arguments, "--output", str(output)], cwd=ROOT, text=True, capture_output=True)
        assert collision.returncode == 2
        assert "output must be empty" in collision.stderr

        original_package_manifest = json.loads(package_manifest.read_text())
        write_json(package_manifest, {**original_package_manifest, "sourceCommit": "0" * 40})
        wrong_source = subprocess.run([*arguments, "--output", str(work / "wrong-source")], cwd=ROOT, text=True, capture_output=True)
        assert wrong_source.returncode == 2
        assert "package source identity changed" in wrong_source.stderr
        write_json(package_manifest, original_package_manifest)

        write_json(qualification, {**original_qualification, "manifestSha256": "0" * 64})
        wrong_manifest = subprocess.run([*arguments, "--output", str(work / "wrong-manifest")], cwd=ROOT, text=True, capture_output=True)
        assert wrong_manifest.returncode == 2
        assert "qualified image manifest digest changed" in wrong_manifest.stderr
        write_json(qualification, original_qualification)

        original_archive = image_archive.read_bytes()
        image_archive.write_bytes(original_archive + b"mutated")
        wrong_asset = subprocess.run([*arguments, "--output", str(work / "wrong-asset")], cwd=ROOT, text=True, capture_output=True)
        assert wrong_asset.returncode == 2
        assert "qualified OCI archive digest changed" in wrong_asset.stderr
        image_archive.write_bytes(original_archive)

        write_json(evidence, {**passing, "unknown": 1, "strictAcceptance": False})
        refused = subprocess.run([*arguments, "--output", str(work / "refused")], cwd=ROOT, text=True, capture_output=True)
        assert refused.returncode == 2
        assert "executor qualification did not pass strictly" in refused.stderr


if __name__ == "__main__":
    main()
