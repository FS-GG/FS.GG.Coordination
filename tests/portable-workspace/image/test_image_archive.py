#!/usr/bin/env python3
"""Exercise retained OCI archive identity and closure without Podman."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import shutil


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("portable_workspace_image", ROOT / "eng/portable-workspace-image.py")
assert SPEC and SPEC.loader
IMAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMAGE)


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def add(archive: tarfile.TarFile, name: str, payload: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(payload)
    info.mtime = 0
    archive.addfile(info, io.BytesIO(payload))


def archive(path: Path, *, corrupt_layer: bool = False, user: str = "32768:32768") -> tuple[str, str]:
    config = canonical({"architecture": "amd64", "os": "linux", "config": {"User": user}})
    layer = b"qualified-layer"
    config_digest, layer_digest = digest(config), digest(layer)
    manifest = canonical({
        "schemaVersion": 2,
        "mediaType": "application/vnd.oci.image.manifest.v1+json",
        "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": f"sha256:{config_digest}", "size": len(config)},
        "layers": [{"mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": f"sha256:{layer_digest}", "size": len(layer)}],
    })
    manifest_digest = digest(manifest)
    index = canonical({"schemaVersion": 2, "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": f"sha256:{manifest_digest}", "size": len(manifest), "annotations": {"org.opencontainers.image.ref.name": IMAGE.IMAGE_NAME}}]})
    with tarfile.open(path, "w") as output:
        add(output, "oci-layout", canonical({"imageLayoutVersion": "1.0.0"}))
        add(output, "index.json", index)
        add(output, f"blobs/sha256/{config_digest}", config)
        add(output, f"blobs/sha256/{layer_digest}", b"qualified-layez" if corrupt_layer else layer)
        add(output, f"blobs/sha256/{manifest_digest}", manifest)
    return manifest_digest, config_digest


def main() -> None:
    source = (ROOT / "eng/portable-workspace-image.py").read_text()
    assert '["save", "--format=oci-archive", "--output", str(candidate), IMAGE_NAME]' in source
    assert '["save", "--format=oci-archive", "--output", str(candidate), build_reference]' not in source
    workflow = (ROOT / ".github/workflows/portable-workspace-executor-qualification.yml").read_text()
    assert 'manifest = pathlib.Path(result["buildManifest"])' in workflow
    assert 'result["buildManifestSha256"]' in workflow
    assert 'value["image"]["reference"] == result["buildImageReference"]' in workflow
    assert 'evidence["imageReference"] == qualification["buildImageReference"]' in workflow
    assert 'exported["image"]["reference"] == result["imageReference"]' in workflow
    expected_python = {
        "python-test.json": "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a",
        "python/app.pyc": "fa24f499efed4a2edaf770742fa63e4fc0ac4c40f1805ad64d12b76b1cde02c7",
    }
    assert IMAGE.PYTHON_EXPECTED_OUTPUTS == expected_python
    assert all(IMAGE.EXPECTED_OUTPUTS[path] == digest for path, digest in expected_python.items())
    executor = (ROOT / "eng" / "portable-workspace-executor-qualification.fsx").read_text()
    assert executor.count(expected_python["python/app.pyc"]) == 1
    fixture_roots = (
        ROOT / "tests" / "portable-workspace" / "image" / "fixture" / "python",
        ROOT / "tests" / "portable-workspace" / "executor-qualification" / "fixture" / "python",
        ROOT / "tests" / "portable-workspace" / "executor" / "python",
    )
    for fixture_root in fixture_roots:
        assert IMAGE.require_python_fixture_oracle(fixture_root) == expected_python
    with tempfile.TemporaryDirectory(prefix="portable-python-oracle-") as temporary:
        changed = Path(temporary) / "python"
        shutil.copytree(fixture_roots[0], changed)
        (changed / "app.py").write_text((changed / "app.py").read_text() + "# drift\n")
        try:
            IMAGE.require_python_fixture_oracle(changed)
            raise AssertionError("changed canonical Python fixture was accepted")
        except RuntimeError as error:
            assert "canonical Python fixture digest mismatch" in str(error)
    with tempfile.TemporaryDirectory(prefix="portable-image-archive-") as temporary:
        root = Path(temporary)
        candidate = root / "candidate.oci.tar"
        manifest_digest, config_digest = archive(candidate)
        identity = IMAGE.inspect_oci_archive(candidate)
        assert identity == {"digest": f"sha256:{manifest_digest}", "configDigest": f"sha256:{config_digest}", "manifestBytes": identity["manifestBytes"]}

        state = root / "state"
        state.mkdir()
        value = {"image": {"digest": "sha256:" + "a" * 64, "reference": "old", "id": f"sha256:{config_digest}"}}
        build_path = IMAGE.persist_manifest(state, value)
        build = json.loads(build_path.read_text())
        path = IMAGE.write_manifest(state, value, identity)
        written = json.loads(path.read_text())
        assert build["image"] == {"digest": "sha256:" + "a" * 64, "reference": "old", "id": f"sha256:{config_digest}"}
        assert build_path != path
        assert written["image"]["buildDigest"] == "sha256:" + "a" * 64
        assert written["image"]["digest"] == f"sha256:{manifest_digest}"
        assert written["image"]["reference"] == f"{IMAGE.IMAGE_NAME}@sha256:{manifest_digest}"
        assert path.stem == "sha256-" + digest(path.read_bytes())

        archive(candidate, corrupt_layer=True)
        try:
            IMAGE.inspect_oci_archive(candidate)
            raise AssertionError("corrupt layer was accepted")
        except RuntimeError as error:
            assert "layer digest differs" in str(error)

        archive(candidate, user="0")
        try:
            IMAGE.inspect_oci_archive(candidate)
            raise AssertionError("root image was accepted")
        except RuntimeError as error:
            assert "non-root image" in str(error)


if __name__ == "__main__":
    main()
