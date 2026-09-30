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
    with tempfile.TemporaryDirectory(prefix="portable-image-archive-") as temporary:
        root = Path(temporary)
        candidate = root / "candidate.oci.tar"
        manifest_digest, config_digest = archive(candidate)
        identity = IMAGE.inspect_oci_archive(candidate)
        assert identity == {"digest": f"sha256:{manifest_digest}", "configDigest": f"sha256:{config_digest}", "manifestBytes": identity["manifestBytes"]}

        state = root / "state"
        state.mkdir()
        value = {"image": {"digest": "sha256:" + "a" * 64, "reference": "old", "id": f"sha256:{config_digest}"}}
        path = IMAGE.write_manifest(state, value, identity)
        written = json.loads(path.read_text())
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
