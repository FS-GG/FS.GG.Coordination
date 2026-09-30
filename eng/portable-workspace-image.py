#!/usr/bin/env python3
"""Prepare and qualify the pinned portable workspace OCI image locally."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import uuid


IMAGE_NAME = "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2"
INPUTS_SHA256 = "c5ef4bb365b37c9213015166758e0fd62bd9230d2bde51c2f2d265ae21f66065"
BASE_DIGEST = "sha256:9bbb8720ae0a24a6ca8dd678bfdf57818fe70caf54c52a53ec01d8db43405056"
NODE_SHA256 = "daf68404b478b4c3616666580d02500a24148c0f439e4d0134d65ce70e90e655"
TYPESCRIPT_SHA256 = "67a3bc82e822b8f45f653a80fc3a9730d23214d36c83ba85dd7f5abebee82062"
EXPECTED_OUTPUTS = {
    "backend-test.json": "96755bcdb1d9b0a64ecb07d8e01ea22070bdc889712002b58d69f11902a81502",
    "backend/service.pyc": "cd136bcbbc1c5dbd87c0811c21d8e6d53d7ae873caef496f23664c62b09f2147",
    "composed-journey.json": "8fc03ef420b45054d93981848a4ba644e88a0fd6bdd47b124b18acc9b2b38b3d",
    "frontend/app.js": "75d96c0c9851a8d87141ac50d3c4fc5059c0090ad0568be62682bc72681c3137",
    "python-test.json": "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a",
    "python/app.pyc": "ab89c3c1b5404d87622387ba576e44ffb6e089b768a19f268886fc5b42ac3f93",
}
REQUIRED_BUILD_OPTIONS = (
    "--file",
    "--format",
    "--network",
    "--platform",
    "--pull",
    "--tag",
    "--timestamp",
)
REQUIRED_CREATE_OPTIONS = (
    "--cap-drop",
    "--entrypoint",
    "--env",
    "--label",
    "--name",
    "--network",
    "--pull",
    "--read-only",
    "--security-opt",
    "--tmpfs",
    "--unsetenv-all",
    "--volume",
    "--workdir",
)


def run(
    argv: list[str],
    *,
    cwd: Path | None = None,
    check: bool = True,
    timeout_seconds: int = 120,
) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            argv,
            cwd=cwd,
            check=check,
            text=True,
            capture_output=True,
            timeout=timeout_seconds,
        )
    except subprocess.CalledProcessError as error:
        stdout = (error.stdout or "").strip()[-4096:]
        stderr = (error.stderr or "").strip()[-4096:]
        raise RuntimeError(
            f"command failed with exit {error.returncode}; stdout={stdout!r}; stderr={stderr!r}"
        ) from error
    except subprocess.TimeoutExpired as error:
        stdout = (error.stdout or "")[-4096:]
        stderr = (error.stderr or "")[-4096:]
        raise RuntimeError(
            f"command timed out after {timeout_seconds} seconds; stdout={stdout!r}; stderr={stderr!r}"
        ) from error


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_bytes(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def download(url: str, target: Path, expected_sha256: str) -> None:
    if target.is_file() and sha256_file(target) == expected_sha256:
        return
    temporary = target.with_name(f".{target.name}.{uuid.uuid4().hex}.partial")
    with urllib.request.urlopen(url, timeout=60) as response, temporary.open("xb") as output:
        shutil.copyfileobj(response, output)
        output.flush()
        os.fsync(output.fileno())
    actual = sha256_file(temporary)
    if actual != expected_sha256:
        raise RuntimeError(f"download digest mismatch for {url}: {actual}")
    os.replace(temporary, target)


def git_value(source: Path, *args: str) -> str:
    return run(["git", *args], cwd=source).stdout.strip()


def require_exact_source(source: Path, expected_revision: str) -> tuple[str, str, str]:
    if git_value(source, "status", "--porcelain"):
        raise RuntimeError("qualification source must be a clean committed Git worktree")
    revision = git_value(source, "rev-parse", "HEAD")
    if revision != expected_revision:
        raise RuntimeError(f"source revision mismatch: expected {expected_revision}, got {revision}")
    tree = git_value(source, "rev-parse", "HEAD^{tree}")
    archive = subprocess.run(
        ["git", "archive", "HEAD", "tests/portable-workspace/image"],
        cwd=source,
        check=True,
        capture_output=True,
    ).stdout
    return revision, tree, hashlib.sha256(archive).hexdigest()


def podman_prefix(args: argparse.Namespace) -> list[str]:
    return [
        str(Path(args.podman).resolve()),
        "--storage-driver=vfs",
        "--root",
        str(Path(args.root).resolve()),
        "--runroot",
        str(Path(args.runroot).resolve()),
    ]


def inspect_image(prefix: list[str], image: str) -> dict:
    values = json.loads(run(prefix + ["image", "inspect", image]).stdout)
    if not isinstance(values, list) or len(values) != 1:
        raise RuntimeError("expected exactly one inspected image")
    return values[0]


def verify_image(image: dict) -> None:
    config = image["Config"]
    entrypoint = config.get("Entrypoint")
    command = config.get("Cmd")
    volumes = config.get("Volumes")
    if image.get("Os") != "linux" or image.get("Architecture") != "amd64":
        raise RuntimeError("image platform is not linux/amd64")
    if config.get("User") not in ("32768", "32768:32768"):
        raise RuntimeError("image does not select the fixed non-root user")
    if entrypoint not in (None, []) or command not in (None, []) or volumes not in (None, {}):
        raise RuntimeError("image declares an entrypoint, command, or volume hook")


def require_podman_options(prefix: list[str], command: str, required: tuple[str, ...]) -> None:
    help_text = run(prefix + [command, "--help"]).stdout
    missing = [
        option
        for option in required
        if re.search(rf"(?<![\w-]){re.escape(option)}(?=[\s=,]|$)", help_text) is None
    ]
    if missing:
        raise RuntimeError(f"Podman {command} lacks required options: {', '.join(missing)}")


def preflight(args: argparse.Namespace, source: Path, state: Path) -> Path:
    revision, tree, fixture_sha256 = require_exact_source(source, args.expected_source_revision)
    image_dir = source / "tests" / "portable-workspace" / "image"
    inputs_path = image_dir / "inputs.json"
    containerfile = image_dir / "Containerfile"
    inputs = json.loads(inputs_path.read_text())
    if sha256_file(inputs_path) != INPUTS_SHA256:
        raise RuntimeError("portable image input manifest digest is not approved")
    if inputs["base"]["ociDigest"] != BASE_DIGEST:
        raise RuntimeError("portable image base digest is not approved")
    if inputs["node"]["sha256"] != NODE_SHA256 or inputs["typescript"]["sha256"] != TYPESCRIPT_SHA256:
        raise RuntimeError("portable image archive digest is not approved")
    recipe = containerfile.read_text()
    if f"FROM docker.io/library/python@{BASE_DIGEST}" not in recipe:
        raise RuntimeError("Containerfile does not use the approved single-platform base digest")
    forbidden = tuple("--" + name + suffix for name, suffix in (("privileged", ""), ("pid=", "host"), ("uts=", "host"), ("userns=", "host")))
    checked_text = recipe + Path(__file__).read_text()
    if any(value in checked_text for value in forbidden):
        raise RuntimeError("portable image qualification contains a forbidden isolation override")

    prefix = podman_prefix(args)
    info = json.loads(run(prefix + ["info", "--format=json"]).stdout)
    require_podman_options(prefix, "build", REQUIRED_BUILD_OPTIONS)
    require_podman_options(prefix, "create", REQUIRED_CREATE_OPTIONS)
    podman_version = run(prefix + ["version", "--format", "{{.Client.Version}}"]).stdout.strip()
    host = info["host"]
    if host.get("os") != "linux" or host.get("arch") != "amd64" or not host["security"].get("rootless"):
        raise RuntimeError("runner does not provide rootless Podman on linux/amd64")
    uid_maps = host.get("idMappings", {}).get("uidmap", [])
    if not any(mapping["container_id"] <= 32768 < mapping["container_id"] + mapping["size"] for mapping in uid_maps):
        raise RuntimeError("rootless Podman user mapping does not cover container uid 32768")

    receipt = {
        "schema": "fsgg.portable-workspace-image-preflight/1",
        "sourceRevision": revision,
        "sourceTree": tree,
        "fixtureArchiveSha256": fixture_sha256,
        "containerfileSha256": sha256_file(containerfile),
        "inputsSha256": INPUTS_SHA256,
        "baseDigest": BASE_DIGEST,
        "nodeSha256": NODE_SHA256,
        "typescriptSha256": TYPESCRIPT_SHA256,
        "rootless": True,
        "os": host["os"],
        "architecture": host["arch"],
        "containerUser": "32768:32768",
        "forbiddenIsolationOverrides": "absent",
        "podmanVersion": podman_version,
        "deterministicBuildTimestamp": "--timestamp=0",
    }
    path = state / "preflight.json"
    payload = canonical_bytes(receipt)
    if path.exists():
        if path.read_bytes() != payload:
            raise RuntimeError("existing preflight receipt conflicts with current source or runtime")
        return path
    with path.open("xb") as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return path


def prepare(args: argparse.Namespace, source: Path, state: Path) -> tuple[str, dict]:
    revision, tree, fixture_sha256 = require_exact_source(source, args.expected_source_revision)
    image_dir = source / "tests" / "portable-workspace" / "image"
    inputs = json.loads((image_dir / "inputs.json").read_text())
    cache = state / "inputs"
    cache.mkdir(parents=True, exist_ok=True, mode=0o700)
    for key in ("node", "typescript"):
        item = inputs[key]
        download(item["url"], cache / item["archive"], item["sha256"])

    context = state / "build-context" / revision
    context.mkdir(parents=True, exist_ok=True, mode=0o700)
    shutil.copyfile(image_dir / "Containerfile", context / "Containerfile")
    for key in ("node", "typescript"):
        archive = inputs[key]["archive"]
        shutil.copyfile(cache / archive, context / archive)

    prefix = podman_prefix(args)
    built = run(
        prefix
        + [
            "build",
            "--pull=never",
            "--network=none",
            "--timestamp=0",
            "--platform=linux/amd64",
            "--format=oci",
            "--tag",
            IMAGE_NAME,
            "--file",
            str(context / "Containerfile"),
            str(context),
        ],
        timeout_seconds=600,
    )
    build_output = built.stdout
    image = inspect_image(prefix, IMAGE_NAME)
    verify_image(image)
    digest = image.get("Digest")
    image_id = image.get("Id", "").removeprefix("sha256:")
    if not isinstance(digest, str) or not digest.startswith("sha256:") or len(image_id) != 64:
        raise RuntimeError("Podman did not expose an immutable image digest and ID")

    manifest = {
        "schema": "fsgg.portable-workspace-local-image/1",
        "source": {
            "revision": revision,
            "tree": tree,
            "fixtureArchiveSha256": fixture_sha256,
            "containerfileSha256": sha256_file(image_dir / "Containerfile"),
            "inputsSha256": sha256_file(image_dir / "inputs.json"),
        },
        "image": {
            "name": IMAGE_NAME,
            "digest": digest,
            "id": f"sha256:{image_id}",
            "reference": f"{IMAGE_NAME}@{digest}",
            "os": image["Os"],
            "architecture": image["Architecture"],
            "user": image["Config"]["User"],
        },
        "inputs": inputs,
        "buildStdoutSha256": hashlib.sha256(build_output.encode()).hexdigest(),
        "podman": run(prefix + ["version", "--format", "{{.Client.Version}}"]).stdout.strip(),
    }
    return f"{IMAGE_NAME}@{digest}", manifest


def _archive_json(archive: tarfile.TarFile, name: str) -> tuple[dict, bytes]:
    try:
        member = archive.getmember(name)
    except KeyError as error:
        raise RuntimeError(f"OCI archive lacks {name}") from error
    if not member.isfile() or member.issym() or member.islnk():
        raise RuntimeError(f"OCI archive member is not a regular file: {name}")
    stream = archive.extractfile(member)
    if stream is None:
        raise RuntimeError(f"OCI archive member cannot be read: {name}")
    payload = stream.read()
    try:
        return json.loads(payload), payload
    except json.JSONDecodeError as error:
        raise RuntimeError(f"OCI archive member is not JSON: {name}") from error


def inspect_oci_archive(candidate: Path) -> dict:
    with tarfile.open(candidate, "r:*") as archive:
        layout, _ = _archive_json(archive, "oci-layout")
        index, _ = _archive_json(archive, "index.json")
        if layout != {"imageLayoutVersion": "1.0.0"}:
            raise RuntimeError("OCI archive layout version changed")
        manifests = index.get("manifests")
        if not isinstance(manifests, list) or len(manifests) != 1:
            raise RuntimeError("OCI archive must contain exactly one image manifest")
        descriptor = manifests[0]
        digest = descriptor.get("digest", "")
        if descriptor.get("mediaType") != "application/vnd.oci.image.manifest.v1+json" or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise RuntimeError("OCI archive image descriptor is not a sha256 OCI manifest")
        if descriptor.get("annotations", {}).get("org.opencontainers.image.ref.name") != IMAGE_NAME:
            raise RuntimeError("OCI archive does not retain the qualified stable image tag")
        platform = descriptor.get("platform")
        if platform is not None and (platform.get("os"), platform.get("architecture")) != ("linux", "amd64"):
            raise RuntimeError("OCI archive descriptor platform differs from linux/amd64")
        manifest, manifest_bytes = _archive_json(archive, f"blobs/sha256/{digest.removeprefix('sha256:')}")
        if len(manifest_bytes) != descriptor.get("size") or sha256_file_bytes(manifest_bytes) != digest.removeprefix("sha256:"):
            raise RuntimeError("OCI archive image manifest descriptor differs from retained bytes")
        config_descriptor = manifest.get("config", {})
        config_digest = config_descriptor.get("digest", "")
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", config_digest):
            raise RuntimeError("OCI archive config descriptor is invalid")
        config, config_bytes = _archive_json(archive, f"blobs/sha256/{config_digest.removeprefix('sha256:')}")
        if len(config_bytes) != config_descriptor.get("size") or sha256_file_bytes(config_bytes) != config_digest.removeprefix("sha256:"):
            raise RuntimeError("OCI archive config descriptor differs from retained bytes")
        if config.get("os") != "linux" or config.get("architecture") != "amd64" or config.get("config", {}).get("User") not in ("32768", "32768:32768"):
            raise RuntimeError("OCI archive config is not the qualified linux/amd64 non-root image")
        layers = manifest.get("layers")
        if not isinstance(layers, list) or not layers:
            raise RuntimeError("OCI archive image manifest has no layers")
        for layer in layers:
            layer_digest = layer.get("digest", "")
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", layer_digest):
                raise RuntimeError("OCI archive layer descriptor is invalid")
            try:
                member = archive.getmember(f"blobs/sha256/{layer_digest.removeprefix('sha256:')}")
            except KeyError as error:
                raise RuntimeError("OCI archive layer blob is absent") from error
            if not member.isfile() or member.size != layer.get("size"):
                raise RuntimeError("OCI archive layer size differs")
            stream = archive.extractfile(member)
            if stream is None or sha256_stream(stream) != layer_digest.removeprefix("sha256:"):
                raise RuntimeError("OCI archive layer digest differs")
        return {"digest": digest, "configDigest": config_digest, "manifestBytes": len(manifest_bytes)}


def sha256_file_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_stream(stream) -> str:
    digest = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def persist_manifest(state: Path, manifest: dict) -> Path:
    payload = canonical_bytes(manifest)
    digest = hashlib.sha256(payload).hexdigest()
    directory = state / "manifests"
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    path = directory / f"sha256-{digest}.json"
    with path.open("xb") as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return path


def write_manifest(state: Path, manifest: dict, archive_identity: dict) -> Path:
    if manifest["image"]["id"] != archive_identity["configDigest"]:
        raise RuntimeError("OCI archive config identity differs from the qualified image ID")
    build_digest = manifest["image"]["digest"]
    manifest["image"]["buildDigest"] = build_digest
    manifest["image"]["digest"] = archive_identity["digest"]
    manifest["image"]["reference"] = f"{IMAGE_NAME}@{archive_identity['digest']}"
    manifest["image"]["archiveConfigDigest"] = archive_identity["configDigest"]
    return persist_manifest(state, manifest)


def append_journal(path: Path, record: dict) -> None:
    with path.open("ab") as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(canonical_bytes(record))
        stream.flush()
        os.fsync(stream.fileno())


def qualify(args: argparse.Namespace, source: Path, state: Path, image_reference: str) -> Path:
    revision, tree, fixture_sha256 = require_exact_source(source, args.expected_source_revision)
    prefix = podman_prefix(args)
    run_id = uuid.uuid4().hex
    run_dir = state / "runs" / run_id
    output = run_dir / "output"
    output.mkdir(parents=True, mode=0o755)
    journal = run_dir / "journal.jsonl"
    os.chmod(run_dir, 0o700)
    run(prefix + ["unshare", "chown", "32768:32768", str(output)])

    common_env = [
        "--unsetenv-all",
        "--env=HOME=/tmp",
        "--env=LANG=C.UTF-8",
        "--env=PATH=/usr/local/bin:/opt/typescript/bin:/usr/bin:/bin",
        "--env=PYTHONDONTWRITEBYTECODE=1",
        "--env=PYTHONPYCACHEPREFIX=/output/pycache",
    ]
    fixture = "tests/portable-workspace/image/fixture"
    expected_image_id = inspect_image(prefix, image_reference)["Id"].removeprefix("sha256:")
    operations = [
        ("versions", "/source", "/usr/local/bin/python3", ["-c", "import subprocess,sys; assert sys.version_info[:3] == (3,14,0); subprocess.run(['/usr/local/bin/node','--version'],check=True); subprocess.run(['/opt/typescript/bin/tsc','--version'],check=True)"]),
        ("python-build", f"/source/{fixture}/python", "/usr/local/bin/python3", ["build.py"]),
        ("python-test", f"/source/{fixture}/python", "/usr/local/bin/python3", ["test.py"]),
        ("frontend-build", f"/source/{fixture}/composed/frontend", "/opt/typescript/bin/tsc", ["--project", "tsconfig.json", "--outDir", "/output/frontend"]),
        ("backend-build", f"/source/{fixture}/composed/backend", "/usr/local/bin/python3", ["build.py"]),
        ("backend-test", f"/source/{fixture}/composed/backend", "/usr/local/bin/python3", ["test.py"]),
        ("composed-journey", f"/source/{fixture}/composed/product", "/usr/local/bin/python3", ["journey.py", "/output/frontend/app.js", "/usr/local/bin/node"]),
    ]
    append_journal(journal, {"event": "run-start", "runId": run_id, "sourceRevision": revision, "sourceTree": tree, "fixtureArchiveSha256": fixture_sha256, "image": image_reference, "strictIsolation": True})
    try:
        for index, (name, workdir, executable, arguments) in enumerate(operations):
            container = f"fsgg-portable-{run_id[:12]}-{index}"
            try:
                create = run(
                    prefix
                    + [
                        "create",
                        "--name",
                        container,
                        "--label=fsgg.portable-workspace.qualification=true",
                        f"--label=fsgg.portable-workspace.run={run_id}",
                        "--pull=never",
                        "--read-only",
                        "--network=none",
                        "--cap-drop=all",
                        "--security-opt=no-new-privileges",
                        f"--volume={source}:/source:ro",
                        f"--volume={output}:/output:rw",
                        "--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=64m",
                        f"--workdir={workdir}",
                        f"--entrypoint={executable}",
                        *common_env,
                        image_reference,
                        *arguments,
                    ]
                )
                container_id = create.stdout.strip()
                started = run(prefix + ["start", "--attach", container], check=False)
                inspected = json.loads(run(prefix + ["container", "inspect", container]).stdout)[0]
                actual_image_id = inspected["Image"].removeprefix("sha256:")
                if actual_image_id != expected_image_id:
                    raise RuntimeError(f"qualification container image mismatch: {name}")
                append_journal(
                    journal,
                    {
                        "event": "operation",
                        "name": name,
                        "containerId": container_id,
                        "imageId": f"sha256:{actual_image_id}",
                        "exitCode": inspected["State"]["ExitCode"],
                        "stdout": started.stdout,
                        "stderr": started.stderr,
                    },
                )
                if started.returncode != 0 or inspected["State"]["ExitCode"] != 0:
                    raise RuntimeError(f"qualification operation failed: {name}")
            finally:
                run(prefix + ["container", "rm", "--force", container], check=False)
        output_digests = {
            str(path.relative_to(output)): sha256_file(path)
            for path in sorted(output.rglob("*"))
            if path.is_file()
        }
        if output_digests != EXPECTED_OUTPUTS:
            raise RuntimeError(f"qualification output mismatch: {json.dumps(output_digests, sort_keys=True)}")
        append_journal(journal, {"event": "verification", "outputs": output_digests})
        append_journal(journal, {"event": "run-complete", "runId": run_id, "outcome": "passed"})
    except BaseException as error:
        append_journal(journal, {"event": "run-complete", "runId": run_id, "outcome": "failed", "error": str(error)})
        raise
    return journal


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("preflight", "qualify"))
    parser.add_argument("--source", required=True)
    parser.add_argument("--expected-source-revision", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--podman", default="/usr/bin/podman")
    parser.add_argument("--root", required=True)
    parser.add_argument("--runroot", required=True)
    args = parser.parse_args()
    source = Path(args.source).resolve()
    state = Path(args.state_dir).resolve()
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(state, 0o700)
    preflight_path = preflight(args, source, state)
    if args.mode == "preflight":
        result = {
            "preflight": str(preflight_path),
            "preflightSha256": sha256_file(preflight_path),
        }
        print(json.dumps(result, sort_keys=True, separators=(",", ":")))
        return 0
    build_reference, manifest = prepare(args, source, state)
    build_manifest_path = persist_manifest(state, manifest)
    journal = qualify(args, source, state, build_reference)
    candidate = state / "candidate.oci.tar"
    run(
        podman_prefix(args) + ["save", "--format=oci-archive", "--output", str(candidate), IMAGE_NAME],
        timeout_seconds=300,
    )
    os.chmod(candidate, 0o600)
    archive_identity = inspect_oci_archive(candidate)
    manifest_path = write_manifest(state, manifest, archive_identity)
    image_reference = manifest["image"]["reference"]
    result = {
        "candidate": str(candidate),
        "candidateSha256": sha256_file(candidate),
        "imageReference": image_reference,
        "buildImageReference": build_reference,
        "buildManifest": str(build_manifest_path),
        "buildManifestSha256": build_manifest_path.stem.removeprefix("sha256-"),
        "imageId": manifest["image"]["id"],
        "manifest": str(manifest_path),
        "manifestSha256": manifest_path.stem.removeprefix("sha256-"),
        "journal": str(journal),
        "journalSha256": sha256_file(journal),
        "preflight": str(preflight_path),
        "preflightSha256": sha256_file(preflight_path),
    }
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
