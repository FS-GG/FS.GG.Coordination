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


def layer(*, cache: bool = False) -> bytes:
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as value:
        add(value, "usr/local/bin/qualified-tool", b"qualified\n")
        if cache:
            add(value, "tmp/node-compile-cache/v24.8.0-x64-test/cache", b"generated\n")
    return output.getvalue()


def assert_fixed_build_inputs(containerfile: str, builder: str, inputs: dict[str, object]) -> None:
    copies = [line.split()[1] for line in containerfile.splitlines() if line.startswith("COPY ")]
    assert copies == ["node-v24.8.0-linux-x64.tar.gz", "typescript-5.9.2.tgz"]
    assert "SOURCE_REVISION" not in containerfile and "policy" not in containerfile.lower()
    assert 'NODE_DISABLE_COMPILE_CACHE=1 /opt/typescript/bin/tsc --version' in containerfile
    assert "test ! -e /tmp/node-compile-cache" in containerfile
    prepare_source = builder.split("def prepare(", 1)[1].split("def _archive_json", 1)[0]
    assert "--build-arg" not in prepare_source and '"--label"' not in prepare_source
    assert {
        "Containerfile": hashlib.sha256(containerfile.encode()).hexdigest(),
        inputs["node"]["archive"]: inputs["node"]["sha256"],
        inputs["typescript"]["archive"]: inputs["typescript"]["sha256"],
    } == {
        "Containerfile": "41f2ae90901851254556d87a85e17011b816e4f4847103376f213f651ef537b4",
        "node-v24.8.0-linux-x64.tar.gz": "daf68404b478b4c3616666580d02500a24148c0f439e4d0134d65ce70e90e655",
        "typescript-5.9.2.tgz": "67a3bc82e822b8f45f653a80fc3a9730d23214d36c83ba85dd7f5abebee82062",
    }


def archive(path: Path, *, cache_layer: bool = False, corrupt_layer: bool = False, malformed_layer: bool = False, user: str = "32768:32768") -> tuple[str, str]:
    config = canonical({"architecture": "amd64", "os": "linux", "config": {"User": user}})
    layer_bytes = b"not-a-tar" if malformed_layer else layer(cache=cache_layer)
    config_digest, layer_digest = digest(config), digest(layer_bytes)
    manifest = canonical({
        "schemaVersion": 2,
        "mediaType": "application/vnd.oci.image.manifest.v1+json",
        "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": f"sha256:{config_digest}", "size": len(config)},
        "layers": [{"mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": f"sha256:{layer_digest}", "size": len(layer_bytes)}],
    })
    manifest_digest = digest(manifest)
    index = canonical({"schemaVersion": 2, "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": f"sha256:{manifest_digest}", "size": len(manifest), "annotations": {"org.opencontainers.image.ref.name": IMAGE.IMAGE_NAME}}]})
    with tarfile.open(path, "w") as output:
        add(output, "oci-layout", canonical({"imageLayoutVersion": "1.0.0"}))
        add(output, "index.json", index)
        add(output, f"blobs/sha256/{config_digest}", config)
        add(output, f"blobs/sha256/{layer_digest}", b"x" + layer_bytes[1:] if corrupt_layer else layer_bytes)
        add(output, f"blobs/sha256/{manifest_digest}", manifest)
    return manifest_digest, config_digest


def main() -> None:
    source = (ROOT / "eng/portable-workspace-image.py").read_text()
    containerfile = (ROOT / "tests/portable-workspace/image/Containerfile").read_text()
    inputs = json.loads((ROOT / "tests/portable-workspace/image/inputs.json").read_text())
    assert hashlib.sha256(source.encode()).hexdigest() == "aa1d2ae85c35e89216bae3159a57e6aa13cf9464243142eae5abbea083977c58"
    assert_fixed_build_inputs(containerfile, source, inputs)
    IMAGE.require_containerfile_contract(containerfile)
    for changed, expected in (
        (containerfile.replace("NODE_DISABLE_COMPILE_CACHE=1 ", ""), "does not disable the Node compile cache"),
        (containerfile.replace("    test ! -e /tmp/node-compile-cache\n", ""), "does not refuse a generated Node compile cache"),
    ):
        try:
            IMAGE.require_containerfile_contract(changed)
            raise AssertionError("incomplete compile-cache guard was accepted")
        except RuntimeError as error:
            assert expected in str(error)
    for changed in (
        containerfile + "\nCOPY src/FS.GG.Coordination.Cli/PortableWorkspacePythonHelloPolicy.fs /policy\n",
        containerfile + "\nLABEL org.opencontainers.image.revision=$SOURCE_REVISION\n",
    ):
        refused = False
        try:
            assert_fixed_build_inputs(changed, source, inputs)
        except AssertionError:
            refused = True
        assert refused, "source-dependent image context was accepted"
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

        archive(candidate, cache_layer=True)
        try:
            IMAGE.inspect_oci_archive(candidate)
            raise AssertionError("generated Node compile cache was accepted")
        except RuntimeError as error:
            assert "generated Node compile cache" in str(error)

        archive(candidate, malformed_layer=True)
        try:
            IMAGE.inspect_oci_archive(candidate)
            raise AssertionError("malformed layer was accepted")
        except RuntimeError as error:
            assert "bounded tar stream" in str(error)

        archive(candidate)
        original_bound = IMAGE.MAX_LAYER_SCAN_MEMBERS
        IMAGE.MAX_LAYER_SCAN_MEMBERS = 0
        try:
            IMAGE.inspect_oci_archive(candidate)
            raise AssertionError("layer member bound was ignored")
        except RuntimeError as error:
            assert "member bound exceeded" in str(error)
        finally:
            IMAGE.MAX_LAYER_SCAN_MEMBERS = original_bound

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
