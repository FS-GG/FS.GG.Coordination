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
PYTHON_FIXTURE_SHA256 = {
    "app.py": "3fef6bef50317388f407c27cee3361b2d1b7e9e4e2eda30d804952e320d0c030",
    "build.py": "f64fb64121b4470a1d7ada581cf9d9cdeb5c61ac776196699bac33534948aeba",
    "test.py": "acd1cf621e8f41d55337f773584551644372cea86137299b981aad020bb6b9c9",
}
PYTHON_EXPECTED_OUTPUTS = {
    "python-test.json": "2ed645adefe2c23308832036a3b5163dc39faaf152c2c9d1d3afb3bd637f146a",
    "python/app.pyc": "fa24f499efed4a2edaf770742fa63e4fc0ac4c40f1805ad64d12b76b1cde02c7",
}
EXPECTED_OUTPUTS = {
    "backend-test.json": "96755bcdb1d9b0a64ecb07d8e01ea22070bdc889712002b58d69f11902a81502",
    "backend/service.pyc": "cd136bcbbc1c5dbd87c0811c21d8e6d53d7ae873caef496f23664c62b09f2147",
    "composed-journey.json": "8fc03ef420b45054d93981848a4ba644e88a0fd6bdd47b124b18acc9b2b38b3d",
    "frontend/app.js": "75d96c0c9851a8d87141ac50d3c4fc5059c0090ad0568be62682bc72681c3137",
    **PYTHON_EXPECTED_OUTPUTS,
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
MAX_LAYER_SCAN_MEMBERS = 100_000
MAX_LAYER_SCAN_BYTES = 1024 * 1024 * 1024
MAX_LAYER_MEMBER_NAME_BYTES = 4096
NODE_COMPILE_CACHE_PREFIX = "tmp/node-compile-cache"


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


def require_python_fixture_oracle(python_fixture: Path) -> dict[str, str]:
    actual = {name: sha256_file(python_fixture / name) for name in PYTHON_FIXTURE_SHA256}
    if actual != PYTHON_FIXTURE_SHA256:
        raise RuntimeError(f"canonical Python fixture digest mismatch: {json.dumps(actual, sort_keys=True)}")
    return dict(PYTHON_EXPECTED_OUTPUTS)


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


def require_storage_paths(args: argparse.Namespace, source: Path, state: Path) -> None:
    """Check declared paths without allocating storage or invoking the engine."""
    paths = {}
    for name, value in (("graphroot", args.root), ("runroot", args.runroot), ("state", args.state_dir)):
        if ".." in Path(value).parts:
            raise RuntimeError(f"portable storage {name} contains parent traversal")
        path = Path(os.path.abspath(value))
        if any(part.is_symlink() for part in (path, *path.parents)):
            raise RuntimeError(f"portable storage {name} has a symlink component")
        paths[name] = path

    def overlaps(left: Path, right: Path) -> bool:
        return left == right or left in right.parents or right in left.parents

    source = source.resolve()
    for name, path in paths.items():
        if overlaps(path, source):
            raise RuntimeError(f"portable storage {name} overlaps the source checkout")
    for left, right in (("graphroot", "runroot"), ("graphroot", "state"), ("runroot", "state")):
        if overlaps(paths[left], paths[right]):
            raise RuntimeError(f"portable storage {left} overlaps {right}")
    home = Path.home()
    global_stores = [home / ".local/share/containers", Path("/var/lib/containers/storage"),
                     Path(f"/run/user/{os.getuid()}/containers"), Path("/run/containers/storage")]
    if os.environ.get("XDG_DATA_HOME"):
        global_stores.append(Path(os.environ["XDG_DATA_HOME"]) / "containers")
    for name, path in paths.items():
        if any(overlaps(path, store.resolve()) for store in global_stores):
            raise RuntimeError(f"portable storage {name} overlaps a global container store")
    if paths["state"] != state:
        raise RuntimeError("portable storage state path does not match the declared path")


def require_effective_storage(info: dict, args: argparse.Namespace) -> None:
    """A CLI storage request is not evidence that the engine obtained it."""
    store = info.get("store")
    if not isinstance(store, dict):
        raise RuntimeError("Podman effective storage observation is missing")
    expected = {"graphRoot": str(Path(args.root).resolve()),
                "runRoot": str(Path(args.runroot).resolve()), "graphDriverName": "vfs"}
    for field, value in expected.items():
        if store.get(field) != value:
            raise RuntimeError(f"Podman effective storage {field} does not match the declared profile")


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


def require_containerfile_contract(recipe: str) -> None:
    if f"FROM docker.io/library/python@{BASE_DIGEST}" not in recipe:
        raise RuntimeError("Containerfile does not use the approved single-platform base digest")
    if 'NODE_DISABLE_COMPILE_CACHE=1 /opt/typescript/bin/tsc --version' not in recipe:
        raise RuntimeError("Containerfile does not disable the Node compile cache for TypeScript")
    if "test ! -e /tmp/node-compile-cache" not in recipe:
        raise RuntimeError("Containerfile does not refuse a generated Node compile cache")


def preflight(args: argparse.Namespace, source: Path, state: Path) -> Path:
    require_storage_paths(args, source, state)
    revision, tree, fixture_sha256 = require_exact_source(source, args.expected_source_revision)
    image_dir = source / "tests" / "portable-workspace" / "image"
    require_python_fixture_oracle(image_dir / "fixture" / "python")
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
    require_containerfile_contract(recipe)
    forbidden = tuple("--" + name + suffix for name, suffix in (("privileged", ""), ("pid=", "host"), ("uts=", "host"), ("userns=", "host")))
    checked_text = recipe + Path(__file__).read_text()
    if any(value in checked_text for value in forbidden):
        raise RuntimeError("portable image qualification contains a forbidden isolation override")

    prefix = podman_prefix(args)
    info = json.loads(run(prefix + ["info", "--format=json"]).stdout)
    require_effective_storage(info, args)
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
        "storage": {field: info["store"][field] for field in ("graphRoot", "runRoot", "graphDriverName")},
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


def require_layer_cache_absent(stream) -> None:
    members = 0
    content_bytes = 0
    try:
        with tarfile.open(fileobj=stream, mode="r|*") as layer:
            for member in layer:
                members += 1
                if members > MAX_LAYER_SCAN_MEMBERS:
                    raise RuntimeError("OCI archive layer member bound exceeded")
                if not isinstance(member.name, str) or len(member.name.encode()) > MAX_LAYER_MEMBER_NAME_BYTES:
                    raise RuntimeError("OCI archive layer member name bound exceeded")
                if member.size < 0:
                    raise RuntimeError("OCI archive layer member size is invalid")
                content_bytes += member.size
                if content_bytes > MAX_LAYER_SCAN_BYTES:
                    raise RuntimeError("OCI archive layer content bound exceeded")
                normalized = member.name
                while normalized.startswith("./"):
                    normalized = normalized[2:]
                normalized = normalized.lstrip("/")
                if normalized == NODE_COMPILE_CACHE_PREFIX or normalized.startswith(NODE_COMPILE_CACHE_PREFIX + "/"):
                    raise RuntimeError("OCI archive contains a generated Node compile cache")
    except (tarfile.TarError, OSError, EOFError) as error:
        raise RuntimeError("OCI archive layer cannot be inspected as a bounded tar stream") from error


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
            inspection_stream = archive.extractfile(member)
            if inspection_stream is None:
                raise RuntimeError("OCI archive layer cannot be inspected")
            require_layer_cache_absent(inspection_stream)
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


# Common-Arch is an inert input seam. It cannot admit native execution.
COMMON_PINS = {
    'Containerfile': 'cde44e5b391adf6bf6c9ae3d98d64869f7e1d352143c25b00f5fd5bb23a15576',
    'pacman.conf': '4fce63852fc318bff9146dea8487fd5c58837c4e35277a4903c4788be2fced8c',
    'inputs.json': 'f1b9d132f10c8bd0bd8643781a13321c1498b0f62f48b26f0075951eb09d7caf',
}
# Finite observed immutable donor policy: Optional local files tighten to Required.
# Public content pins; actual acquisition custody/authentication remains external.
COMMON_DONOR_REFERENCE = 'docker.io/library/archlinux@sha256:363129b67b2bc6c5a634f8fb4961a7e2eba213a2ec075d159127b1260290a722'
COMMON_DONOR_CONFIG_DIGEST = 'sha256:5a30b4acc83ff841682eb21795e00f7ff030796f5e6fd2a127decd7a45ba7cef'
COMMON_DONOR_PACMAN_CONFIG_SHA256 = '1b1c6195b21865a660480f41a29a172f103b1aa3e846d1db877e04cd6220e454'
COMMON_DONOR_POLICY_SHA256 = '60d18150be3245f6df3e53a6a301380e9eb5cab17047d5112b6709eedaf9d0ac'
COMMON_DONOR_POLICY_BYTES = 1751
COMMON_ACQUISITION_CONFIG_SHA256 = '513b2a7cb4de9afdbaf275d880b7a077bdb96c3975f4eac50ae9bcfd4d0c1cc9'
COMMON_IMAGE_NAME = 'localhost/fsgg-provisioned-common:arch-20261012'
MAX_LOCK_BYTES = 8 * 1024 * 1024
MAX_PAYLOAD_BYTES = 8 * 1024 * 1024 * 1024
MAX_PROOF_BYTES = 64 * 1024 * 1024


def common_require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError('common-arch: ' + message)


def common_fields(value, fields: str) -> None:
    common_require(isinstance(value, dict) and set(value) == set(fields.split()),
                   'missing or unknown fields: ' + fields)


def common_text(value, pattern=None):
    common_require(isinstance(value, str) and 0 < len(value.encode()) <= 4096
                   and all(ord(c) >= 32 and ord(c) != 127 for c in value), 'invalid string')
    if pattern:
        common_require(re.fullmatch(pattern, value) is not None, 'invalid string syntax')
    return value


def common_sha(value):
    return common_text(value, r'[0-9a-f]{64}')


def common_size(value):
    common_require(type(value) is int and value >= 0, 'invalid byte size')
    return value


def common_path(value, prefix=None):
    common_text(value)
    parts = value.split('/')
    common_require(not value.startswith('/') and all(p not in ('', '.', '..') for p in parts),
                   'unsafe relative path')
    common_require(all(re.fullmatch(r'[A-Za-z0-9@._+:-]+', p) for p in parts), 'unsafe filename')
    if prefix:
        common_require(value.startswith(prefix + '/'), 'wrong path population')
    return value


def common_raw_path(value):
    common_text(str(value))
    common_require('..' not in Path(value).parts, 'parent traversal')
    path = Path(os.path.abspath(value))
    common_require(not any(p.is_symlink() for p in (path, *path.parents)), 'symlink path component')
    return path


def common_read(path: Path, limit: int, expected=None, destination=None):
    """Stream a no-follow one-link file, detecting substitution and byte drift."""
    import stat
    common_raw_path(path)
    try:
        before = path.lstat()
    except OSError as error:
        raise RuntimeError('common-arch: missing or unreadable input') from error
    common_require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= limit,
                   'nonregular, hardlinked or overlarge input')
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    except OSError as error:
        raise RuntimeError('common-arch: input open failed') from error
    digest = hashlib.sha256()
    payload = bytearray() if destination is None and limit <= MAX_LOCK_BYTES else None
    total = 0
    try:
        with os.fdopen(descriptor, 'rb') as stream:
            opened = os.fstat(stream.fileno())
            common_require((before.st_dev, before.st_ino) == (opened.st_dev, opened.st_ino), 'input substituted')
            output = None
            try:
                if destination is not None:
                    output = destination.open('xb')
                    os.fchmod(output.fileno(), 0o600)
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    total += len(chunk)
                    common_require(total <= limit, 'input byte bound exceeded')
                    digest.update(chunk)
                    if payload is not None:
                        payload.extend(chunk)
                    if output is not None:
                        output.write(chunk)
                after = os.fstat(stream.fileno())
                current = path.lstat()
                common_require(all((s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns, s.st_nlink)
                                   == (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns,
                                       before.st_ctime_ns, 1) for s in (after, current)), 'input changed during read')
            finally:
                if output is not None:
                    output.close()
    except OSError as error:
        raise RuntimeError('common-arch: input read/copy failed') from error
    identity = (total, digest.hexdigest())
    if expected is not None:
        common_require(identity == (expected['bytes'], expected['sha256']), 'input bytes differ')
    return bytes(payload) if payload is not None else identity


def common_json(payload):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            common_require(key not in result, 'duplicate JSON key')
            result[key] = value
        return result
    try:
        return json.loads(payload, object_pairs_hook=unique,
                          parse_constant=lambda _: common_require(False, 'nonfinite JSON number'))
    except (ValueError, RecursionError) as error:
        raise RuntimeError('common-arch: invalid bounded JSON') from error


def common_population(root: Path, prefix: str, bound: int):
    """Bound every directory entry, including empty directories, without following links."""
    import stat
    found = set()
    count = 0
    pending = [root / prefix]
    while pending:
        directory = pending.pop()
        common_raw_path(directory)
        common_require(stat.S_ISDIR(directory.lstat().st_mode), 'payload directory is not regular')
        with os.scandir(directory) as entries:
            for entry in entries:
                count += 1
                common_require(count <= bound * 4 + 16, 'population entry bound exceeded')
                path = Path(entry.path)
                relative = common_path(path.relative_to(root).as_posix(), prefix)
                mode = entry.stat(follow_symlinks=False).st_mode
                if stat.S_ISDIR(mode):
                    pending.append(path)
                else:
                    common_require(stat.S_ISREG(mode), 'nonregular population entry')
                    found.add(relative)
                    common_require(len(found) <= bound, 'population file bound exceeded')
    return found


def common_descriptor(value, prefix):
    common_fields(value, 'path bytes sha256')
    common_path(value['path'], prefix)
    common_size(value['bytes'])
    common_sha(value['sha256'])
    return value


def common_inventory(payload):
    common_require(payload.endswith(b'\n') and len(payload) <= MAX_LOCK_BYTES, 'inventory is not canonical')
    try:
        rows = payload.decode('ascii').splitlines()
    except UnicodeError as error:
        raise RuntimeError('common-arch: inventory is not ASCII') from error
    result = {}
    common_require(rows == sorted(rows) and bool(rows), 'inventory is not sorted/nonempty')
    for row in rows:
        common_require(re.fullmatch(r'[A-Za-z0-9@._+-]+ [^\s\x00-\x1f\x7f]+', row) is not None,
                       'invalid inventory row')
        name, version = row.split(' ')
        common_require(name not in result, 'duplicate inventory package')
        result[name] = version
    return result


def validate_common_inputs(bundle: Path, lock: dict, source_spec: dict, image_identity: dict):
    """Validate finite correspondence; external custody/authentication remains required."""
    common_require(isinstance(lock, dict), 'lock must be an object')
    projection = dict(lock)
    acquired = projection.get('acquiredInputs')
    projection['classification'] = source_spec['classification']
    projection['acquiredInputs'] = None
    common_require(lock.get('classification') == 'acquired-input-lock' and projection == source_spec,
                   'source declaration mismatch')
    common_fields(acquired, 'imageSource donor snapshot trust transaction packages files checksumsSha256 evidence')
    common_fields(acquired['imageSource'], 'repository revision tree containerfileSha256 pacmanConfigSha256 inputsSha256')
    common_require(acquired['imageSource'] == image_identity, 'image source mismatch')
    evidence = acquired['evidence']
    common_require(isinstance(evidence, dict) and 0 < len(evidence) <= 4096, 'evidence count bound')
    refs = set()
    total = 0
    proof_paths = set()
    for key, descriptor in evidence.items():
        common_text(key, r'[a-z0-9][a-z0-9-]{0,63}')
        common_descriptor(descriptor, 'proof')
        common_require(descriptor['path'] not in proof_paths, 'duplicate proof path')
        proof_paths.add(descriptor['path'])
        total += descriptor['bytes']
        common_require(total <= MAX_PROOF_BYTES, 'proof byte bound')
        common_read(bundle / descriptor['path'], MAX_PROOF_BYTES, descriptor)
    common_require(common_population(bundle, 'proof', 4096) == proof_paths, 'extra/missing proof population')
    def ref(value):
        common_require(isinstance(value, str) and value in evidence, 'missing evidence reference')
        refs.add(value)
        return evidence[value]
    def ref_list(values):
        common_require(isinstance(values, list) and 0 < len(values) <= 4096 and all(isinstance(v, str) for v in values) and len(set(values)) == len(values),
                       'invalid evidence reference list')
        for value in values:
            ref(value)
    donor = acquired['donor']
    common_fields(donor, 'reference indexDigest configDigest layers evidenceRefs')
    common_require({k: donor[k] for k in ('reference', 'indexDigest', 'configDigest', 'layers')}
                   == {k: source_spec['donor'][k] for k in ('reference', 'indexDigest', 'configDigest', 'layers')},
                   'donor mismatch')
    common_require(isinstance(donor['layers'], list) and 0 < len(donor['layers']) <= 32768, 'donor layer count bound')
    for layer in donor['layers']:
        common_fields(layer, 'sha256 bytes')
        common_sha(layer['sha256'])
        common_size(layer['bytes'])
    ref_list(donor['evidenceRefs'])
    files = acquired['files']
    common_require(isinstance(files, list) and 0 < len(files) <= 32768, 'payload count bound')
    descriptors = {}
    total = 0
    for value in files:
        common_fields(value, 'path bytes sha256 role')
        common_descriptor({k: value[k] for k in ('path', 'bytes', 'sha256')}, 'acquired')
        path, role = value['path'], value['role']
        common_require(path not in descriptors, 'duplicate payload path')
        common_require(role in ('inventory', 'database', 'database-signature', 'package', 'package-signature'), 'unknown role')
        if role == 'inventory':
            common_require(path == 'acquired/package-inventory.expected', 'inventory path mismatch')
        else:
            common_require(re.fullmatch(r'acquired/repositories/(core|extra)/os/x86_64/[A-Za-z0-9_+.:-]+', path)
                           is not None, 'repository path mismatch')
        suffix = {'database': '.db', 'database-signature': '.db.sig', 'package': '.pkg.tar.zst',
                  'package-signature': '.pkg.tar.zst.sig'}.get(role)
        common_require(suffix is None or path.endswith(suffix), 'role suffix mismatch')
        total += value['bytes']
        common_require(total <= MAX_PAYLOAD_BYTES, 'payload byte bound')
        common_read(bundle / path, MAX_PAYLOAD_BYTES, value)
        descriptors[path] = value
    common_require(list(descriptors) == sorted(descriptors), 'payload descriptors not sorted')
    used = set()
    def join(path, value, role):
        common_size(value.get('bytes'))
        common_sha(value.get('sha256'))
        common_require(path in descriptors and descriptors[path]['role'] == role
                       and all(descriptors[path][k] == value[k] for k in ('bytes', 'sha256')), 'payload join mismatch')
        used.add(path)
    snapshot = acquired['snapshot']
    common_fields(snapshot, 'date acquisitionServer repositories databases')
    common_require({k: snapshot[k] for k in ('date', 'acquisitionServer', 'repositories')} == source_spec['snapshot'],
                   'snapshot mismatch')
    databases = snapshot['databases']
    common_require(isinstance(databases, list) and len(databases) == 2, 'database count mismatch')
    db_paths = {}
    for db in databases:
        common_fields(db, 'repository path bytes sha256 signature evidenceRef')
        repository = db['repository']
        common_require(repository in ('core', 'extra') and repository not in db_paths, 'database repository mismatch')
        common_require(db['path'] == f'acquired/repositories/{repository}/os/x86_64/{repository}.db', 'database path mismatch')
        join(db['path'], db, 'database')
        db_paths[repository] = db['path']
        ref(db['evidenceRef'])
        signature = db['signature']
        common_fields(signature, 'availability carrierPath sha256 evidenceRef')
        ref(signature['evidenceRef'])
        if signature['availability'] == 'present':
            common_require(signature['carrierPath'] == db['path'] + '.sig', 'database signature path mismatch')
            carrier = descriptors.get(signature['carrierPath'], {})
            join(signature['carrierPath'], carrier, 'database-signature')
            common_require(carrier['sha256'] == signature['sha256'], 'database signature digest mismatch')
        else:
            common_require(signature == {'availability': 'absent-observed', 'carrierPath': None,
                           'sha256': None, 'evidenceRef': signature['evidenceRef']}, 'unobserved database signature')
    trust = acquired['trust']
    common_fields(trust, 'pacmanVersion libalpmVersion keyringPackageVersion donorPacmanConfigSha256 acquisitionConfigSha256 finalConfigSha256 donorEffectiveSigLevel donorEffectiveLocalFileSigLevel finalEffectiveSigLevel finalEffectiveLocalFileSigLevel donorPolicyEvidenceRef keyringEvidenceRef reconciliationEvidenceRef')
    for key, value in trust.items():
        if key.endswith('EvidenceRef'):
            ref(value)
        elif key.endswith('Sha256'):
            common_sha(value)
        else:
            common_text(value)
    # This is one evidence-grounded tuple, never a generic policy strength comparison.
    policy_proof = ref(trust['donorPolicyEvidenceRef'])  # already size/hash-checked above
    common_require(donor['reference'] == COMMON_DONOR_REFERENCE
                   and donor['configDigest'] == COMMON_DONOR_CONFIG_DIGEST
                   and trust['donorPacmanConfigSha256'] == COMMON_DONOR_PACMAN_CONFIG_SHA256
                   and policy_proof['sha256'] == COMMON_DONOR_POLICY_SHA256
                   and policy_proof['bytes'] == COMMON_DONOR_POLICY_BYTES
                   and trust['acquisitionConfigSha256'] == COMMON_ACQUISITION_CONFIG_SHA256
                   and trust['finalConfigSha256'] == COMMON_PINS['pacman.conf'],
                   'selected donor/config/policy proof mismatch')
    common_require(source_spec['trust']['candidateSigLevel'] == 'Required DatabaseOptional'
                   and source_spec['trust']['candidateLocalFileSigLevel'] == 'Required'
                   and trust['donorEffectiveSigLevel'] == 'Required DatabaseOptional'
                   and trust['finalEffectiveSigLevel'] == 'Required DatabaseOptional'
                   and trust['donorEffectiveLocalFileSigLevel'] == 'Optional'
                   and trust['finalEffectiveLocalFileSigLevel'] == 'Required',
                   'selected Optional-to-Required trusted policy tuple mismatch')
    transaction = acquired['transaction']
    common_fields(transaction, 'method targets resolutionEvidenceRef downloadEvidenceRef offlineTransactionEvidenceRef hookEvidenceRef beforeInventory finalInventory providers replacements removals')
    common_require(transaction['method'] == 'pacman-full-system' and transaction['targets'] == source_spec['targets'], 'transaction mismatch')
    for key in ('resolutionEvidenceRef', 'downloadEvidenceRef', 'offlineTransactionEvidenceRef', 'hookEvidenceRef'):
        ref(transaction[key])
    before = transaction['beforeInventory']
    common_fields(before, 'path bytes sha256 evidenceRef')
    common_require({k: before[k] for k in ('path', 'bytes', 'sha256')} == ref(before['evidenceRef']), 'before inventory proof mismatch')
    old = common_inventory(common_read(bundle / before['path'], MAX_LOCK_BYTES, before))
    final = common_descriptor(transaction['finalInventory'], 'acquired')
    common_require(final['path'] == 'acquired/package-inventory.expected', 'final inventory path mismatch')
    join(final['path'], final, 'inventory')
    new = common_inventory(common_read(bundle / final['path'], MAX_LOCK_BYTES, final))
    removed = set()
    for key, fields in (('providers', 'observation evidenceRef'), ('replacements', 'removedName installedName evidenceRef'),
                        ('removals', 'name evidenceRef')):
        records = transaction[key]
        common_require(isinstance(records, list) and len(records) <= 32768, 'transaction observation count bound')
        for record in records:
            common_fields(record, fields)
            ref(record['evidenceRef'])
            for field in fields.split():
                common_text(record[field])
            if key != 'providers':
                name = record['removedName'] if key == 'replacements' else record['name']
                common_require(name in old and name not in new and name not in removed, 'removal/replacement mismatch')
                removed.add(name)
                if key == 'replacements':
                    common_require(record['installedName'] in new, 'replacement absent after transaction')
    common_require(set(old) - removed <= set(new), 'donor inventory missing after transaction')
    packages = acquired['packages']
    common_require(isinstance(packages, list) and 0 < len(packages) <= 32768, 'package count bound')
    additions = {}
    package_paths = set()
    for package in packages:
        common_fields(package, 'repository name version architecture path bytes sha256 signature transactionEvidenceRef')
        repository = package['repository']
        common_require(repository in ('core', 'extra') and package['architecture'] in ('x86_64', 'any'), 'package platform/repository mismatch')
        name = common_text(package['name'], r'[A-Za-z0-9@._+-]+')
        version = common_text(package['version'], r'[^\s]+')
        common_require(name not in additions and new.get(name) == version, 'package identity/final inventory mismatch')
        additions[name] = version
        common_require(package['path'] not in package_paths, 'duplicate package archive path')
        package_paths.add(package['path'])
        common_require(package['path'].startswith(f'acquired/repositories/{repository}/os/x86_64/'), 'package repository path mismatch')
        join(package['path'], package, 'package')
        ref(package['transactionEvidenceRef'])
        signature = package['signature']
        common_fields(signature, 'kind carrierPath sha256 signerFingerprint evidenceRef')
        common_sha(signature['sha256'])
        common_text(signature['signerFingerprint'], r'(?:[0-9A-F]{40}|[0-9A-F]{64})')
        ref(signature['evidenceRef'])
        if signature['kind'] == 'detached':
            common_require(signature['carrierPath'] == package['path'] + '.sig', 'package signature path mismatch')
            carrier = descriptors.get(signature['carrierPath'], {})
            join(signature['carrierPath'], carrier, 'package-signature')
            common_require(carrier['sha256'] == signature['sha256'], 'package signature mismatch')
        else:
            common_require(signature['kind'] == 'database-embedded' and signature['carrierPath'] == db_paths[repository],
                           'unbound embedded signature')
    common_require(set(new) <= set(old) | set(additions), 'unaccounted final package')
    common_require(all(new[name] == old[name] for name in (set(old) & set(new)) - set(additions)), 'unaccounted package version change')
    common_require(used == set(descriptors), 'unused payload descriptors')
    common_require(refs == set(evidence), 'unused evidence descriptors')
    checksums = common_read(bundle / 'acquired/SHA256SUMS', MAX_LOCK_BYTES)
    common_require(sha256_file_bytes(checksums) == common_sha(acquired['checksumsSha256']), 'checksum digest mismatch')
    expected = ''.join(f"{descriptors[p]['sha256']}  {p.removeprefix('acquired/')}\n" for p in sorted(descriptors)).encode()
    common_require(checksums == expected, 'checksum coverage mismatch')
    common_require(common_population(bundle, 'acquired', 32769) == set(descriptors) | {'acquired/SHA256SUMS'}, 'payload population mismatch')
    return descriptors


def check_common_inputs(args):
    source = common_raw_path(args.source)
    image_source = common_raw_path(args.image_source)
    bundle = common_raw_path(args.acquisition_dir)
    state = common_raw_path(args.state_dir)
    for checkout in (source, image_source, bundle):
        require_storage_paths(args, checkout, state)
    common_require(Path(__file__).absolute() == source / 'eng/portable-workspace-image.py', 'executing helper source substitution')
    for checkout, expected in ((source, args.expected_source_revision), (image_source, args.expected_image_source_revision)):
        common_text(expected, r'[0-9a-f]{40}')
        common_require(not git_value(checkout, 'status', '--porcelain') and git_value(checkout, 'rev-parse', 'HEAD') == expected,
                       'dirty/wrong exact source')
    image_identity = {'repository': 'FS-GG/.github', 'revision': args.expected_image_source_revision,
                      'tree': git_value(image_source, 'rev-parse', 'HEAD^{tree}'),
                      'containerfileSha256': COMMON_PINS['Containerfile'], 'pacmanConfigSha256': COMMON_PINS['pacman.conf'],
                      'inputsSha256': COMMON_PINS['inputs.json']}
    common_text(image_identity['tree'], r'[0-9a-f]{40}')
    recipe = image_source / 'deployment/provisioned-podman'
    source_bytes = {}
    for name, digest in COMMON_PINS.items():
        value = common_read(recipe / name, MAX_LOCK_BYTES)
        common_require(sha256_file_bytes(value) == digest, 'pinned recipe bytes differ')
        source_bytes[name] = value
    payload = common_read(bundle / 'inputs.acquired.json', MAX_LOCK_BYTES)
    common_require(sha256_file_bytes(payload) == common_sha(args.expected_acquired_inputs_sha256), 'external lock digest mismatch')
    descriptors = validate_common_inputs(bundle, common_json(payload), common_json(source_bytes['inputs.json']), image_identity)
    # This fresh context is private staging, never a runtime preflight receipt or shared cache.
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    common_raw_path(state)
    os.chmod(state, 0o700)
    context = state / 'common-arch-context'
    common_require(not os.path.lexists(context), 'context already exists')
    context.mkdir(mode=0o700)
    owned_context = context.lstat()
    def require_owned_context():
        current = context.lstat()
        common_require((current.st_dev, current.st_ino) == (owned_context.st_dev, owned_context.st_ino)
                       and not context.is_symlink(), 'staging context substituted; cleanup ownership uncertain')
    staged = {'Containerfile': {'bytes': len(source_bytes['Containerfile']), 'sha256': COMMON_PINS['Containerfile']},
              'pacman.conf': {'bytes': len(source_bytes['pacman.conf']), 'sha256': COMMON_PINS['pacman.conf']},
              **descriptors, 'acquired/SHA256SUMS': {'bytes': (bundle / 'acquired/SHA256SUMS').stat().st_size,
                                                   'sha256': common_json(payload)['acquiredInputs']['checksumsSha256']}}
    try:
        for path, descriptor in staged.items():
            require_owned_context()
            target = context / path
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            origin = recipe / path if path in ('Containerfile', 'pacman.conf') else bundle / path
            common_read(origin, MAX_PAYLOAD_BYTES, descriptor, target)
        common_require(common_population(context, 'acquired', 32769) == set(descriptors) | {'acquired/SHA256SUMS'}, 'staged population mismatch')
        for path, descriptor in staged.items():
            common_read(context / path, MAX_PAYLOAD_BYTES, descriptor)
        require_owned_context()
        context_digest = sha256_file_bytes(canonical_bytes([{'path': path, 'bytes': staged[path]['bytes'],
                                                            'sha256': staged[path]['sha256']} for path in sorted(staged)]))
    except BaseException as error:
        try:
            require_owned_context()
            shutil.rmtree(context)
        except BaseException as cleanup_error:
            error.add_note('common-arch staging cleanup unconfirmed: ' + str(cleanup_error))
        raise
    prefix = podman_prefix(args)
    commands = [prefix + ['build', '--pull=never', '--network=none', '--timestamp=0', '--platform=linux/amd64',
                         '--format=oci', '--tag', COMMON_IMAGE_NAME, '--file', str(context / 'Containerfile'), str(context)],
                prefix + ['save', '--format=oci-archive', '--output', str(state / 'candidate.oci.tar'), COMMON_IMAGE_NAME]]
    return {'profile': 'common-arch', 'helperSourceRevision': args.expected_source_revision,
            'imageSourceRevision': args.expected_image_source_revision, 'acquiredInputsSha256': args.expected_acquired_inputs_sha256,
            'context': str(context), 'contextSha256': context_digest, 'buildCommands': commands, 'nativeExecutionEnabled': False}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("preflight", "qualify", "check-inputs"))
    parser.add_argument("--source", required=True)
    parser.add_argument("--expected-source-revision", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--podman", default="/usr/bin/podman")
    parser.add_argument("--root", required=True)
    parser.add_argument("--runroot", required=True)
    parser.add_argument("--profile", choices=("portable-python", "common-arch"), default="portable-python")
    parser.add_argument("--image-source")
    parser.add_argument("--expected-image-source-revision")
    parser.add_argument("--acquisition-dir")
    parser.add_argument("--expected-acquired-inputs-sha256")
    args = parser.parse_args()
    common_flags = (args.image_source, args.expected_image_source_revision,
                    args.acquisition_dir, args.expected_acquired_inputs_sha256)
    if args.profile == "common-arch":
        if args.mode != "check-inputs":
            parser.error("common-arch native preflight/qualification disabled pending consumer guards")
        if not all(common_flags):
            parser.error("common-arch check-inputs requires image source and externally bound acquisition inputs")
        print(json.dumps(check_common_inputs(args), sort_keys=True, separators=(",", ":")))
        return 0
    if args.mode == "check-inputs" or any(value is not None for value in common_flags):
        parser.error("common input mode/flags require common-arch profile")
    source = Path(args.source).resolve()
    state = Path(args.state_dir).resolve()
    require_storage_paths(args, source, state)
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
