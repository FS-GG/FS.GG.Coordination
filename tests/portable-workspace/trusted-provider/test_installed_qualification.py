#!/usr/bin/env python3
"""Exercise complete package/OCI custody and authorization refusal."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import zipfile


ROOT = Path(__file__).resolve().parents[3]
HELPER_PATH = ROOT / "eng/qualify-installed-python-hello.py"
SPEC = importlib.util.spec_from_file_location("installed_python_qualification", HELPER_PATH)
assert SPEC and SPEC.loader
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def add(archive: tarfile.TarFile, name: str, value: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(value)
    info.mtime = 0
    archive.addfile(info, io.BytesIO(value))


def image_archive(path: Path) -> tuple[str, str]:
    config = canonical({
        "architecture": "amd64",
        "os": "linux",
        "config": {"User": "32768:32768"},
    })
    layer = b"qualified-layer"
    config_digest = digest(config)
    layer_digest = digest(layer)
    manifest = canonical({
        "schemaVersion": 2,
        "config": {"digest": f"sha256:{config_digest}", "size": len(config)},
        "layers": [{"digest": f"sha256:{layer_digest}", "size": len(layer)}],
    })
    manifest_digest = digest(manifest)
    index = canonical({
        "schemaVersion": 2,
        "manifests": [{"digest": f"sha256:{manifest_digest}", "size": len(manifest)}],
    })
    with tarfile.open(path, "w") as archive:
        add(archive, "oci-layout", canonical({"imageLayoutVersion": "1.0.0"}))
        add(archive, "index.json", index)
        add(archive, f"blobs/sha256/{manifest_digest}", manifest)
        add(archive, f"blobs/sha256/{config_digest}", config)
        add(archive, f"blobs/sha256/{layer_digest}", layer)
    return manifest_digest, config_digest


def package(path: Path, *, extra: tuple[str, bytes] | None = None) -> None:
    nuspec = b"""<?xml version="1.0"?><package><metadata><id>FS.GG.Coordination.Cli</id><version>0.2.1</version></metadata></package>"""
    files = {
        "FS.GG.Coordination.Cli.nuspec": nuspec,
        "tools/net10.0/any/FS.GG.Coordination.Cli.dll": b"MZ-cli",
        "tools/net10.0/any/FS.GG.Coordination.Cli.deps.json": b"{}",
        "tools/net10.0/any/FS.GG.Coordination.Cli.runtimeconfig.json": b"{}",
        "tools/net10.0/any/dependency.dll": b"MZ-dependency",
    }
    if extra:
        files[extra[0]] = extra[1]
    with zipfile.ZipFile(path, "w") as archive:
        for name, value in files.items():
            archive.writestr(name, value)


def prepare(root: Path) -> tuple[Path, Path]:
    candidate = root / "candidate.nupkg"
    archive = root / "candidate.oci.tar"
    manifest_path = root / "image-manifest.json"
    install = root / "install"
    receipt = root / "candidate-receipt.json"
    package(candidate)
    manifest_digest, config_digest = image_archive(archive)
    manifest_path.write_bytes(canonical({
        "schema": "fsgg.portable-workspace-local-image/1",
        "source": {"revision": "a" * 40, "tree": "b" * 40},
        "image": {
            "reference": f"{HELPER.IMAGE_NAME}@sha256:{manifest_digest}",
            "archiveConfigDigest": f"sha256:{config_digest}",
            "id": f"sha256:{config_digest}",
        },
    }))
    args = SimpleNamespace(
        package=candidate,
        expected_package_sha256=HELPER.sha256_file(candidate),
        expected_version="0.2.1",
        install_root=install,
        producer_source="c" * 40,
        producer_tree="d" * 40,
        image_archive=archive,
        expected_image_archive_sha256=HELPER.sha256_file(archive),
        image_manifest=manifest_path,
        expected_image_manifest_sha256=HELPER.sha256_file(manifest_path),
        receipt=receipt,
    )
    HELPER.prepare(args)
    return install, receipt


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="installed-python-qualification-") as temporary:
        root = Path(temporary)
        install, receipt_path = prepare(root)
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        assert receipt["schema"] == HELPER.SCHEMA
        assert receipt["authorizationState"] == "required-external"
        assert receipt["grantWritten"] is False
        assert receipt["nativeExecutionAuthorized"] is False
        assert receipt["package"]["version"] == "0.2.1"
        assert len(receipt["package"]["entries"]) == 5
        assert {item["path"] for item in receipt["installedCli"]["files"]} == {
            "FS.GG.Coordination.Cli.deps.json",
            "FS.GG.Coordination.Cli.dll",
            "FS.GG.Coordination.Cli.runtimeconfig.json",
            "dependency.dll",
        }
        tool_root = install / HELPER.TOOL_PREFIX
        actual_digest, actual_files = HELPER.payload_digest(tool_root)
        assert actual_digest == receipt["installedCli"]["payloadSha256"]
        assert actual_files == receipt["installedCli"]["files"]

        dependency = tool_root / "dependency.dll"
        dependency.chmod(0o644)
        dependency.write_bytes(b"changed")
        changed_digest, _ = HELPER.payload_digest(tool_root)
        assert changed_digest != receipt["installedCli"]["payloadSha256"]

    with tempfile.TemporaryDirectory(prefix="installed-python-traversal-") as temporary:
        root = Path(temporary)
        candidate = root / "unsafe.nupkg"
        package(candidate, extra=("../escape", b"bad"))
        try:
            HELPER.extract_package(candidate, root / "install")
            raise AssertionError("package traversal was accepted")
        except ValueError as error:
            assert "unsafe path" in str(error)

    with tempfile.TemporaryDirectory(prefix="installed-python-grant-") as temporary:
        root = Path(temporary)
        _, receipt = prepare(root)
        completed = subprocess.run(
            [
                sys.executable, str(HELPER_PATH), "invoke",
                "--candidate-receipt", str(receipt),
                "--dotnet", "/usr/bin/dotnet",
                "--workspace", str(root),
                "--profile", str(root / "profile.json"),
                "--command", str(root / "command.json"),
                "--mode", "execute",
                "--grant-path", str(root / "draft-grant.json"),
                "--receipt", str(root / "invoke.json"),
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=10,
        )
        assert completed.returncode == 2
        assert "fixed externally authorized grant is absent" in completed.stderr
        assert not (root / "invoke.json").exists()


if __name__ == "__main__":
    main()
