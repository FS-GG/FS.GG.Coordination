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
COMMON_CAPTURE_LIMIT_BYTES = 8 * 1024 * 1024
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
    capture_limit_bytes: int | None = None,
    execution=None,
    label: str | None = None,
    environment: dict[str, str] | None = None,
    retirement: bool = False,
) -> subprocess.CompletedProcess[str]:
    # Existing profiles retain their historical command semantics. The closed
    # product profile supplies one enclosing lifetime/capture owner, never a new
    # timeout per stage or an unbounded-capture path.
    if execution is not None and capture_limit_bytes is None:
        raise RuntimeError('enclosing execution requires bounded capture')
    if execution is None and (label is not None or retirement):
        raise RuntimeError('execution-only command options require an enclosing owner')
    if environment is not None and (capture_limit_bytes is None or type(environment) is not dict
                                    or not all(type(k) is str and type(v) is str for k, v in environment.items())):
        raise RuntimeError('explicit child environment requires bounded capture and string values')
    if capture_limit_bytes is not None:
        import math
        import selectors
        import time
        if (type(capture_limit_bytes) is not int or not 0 < capture_limit_bytes <= COMMON_CAPTURE_LIMIT_BYTES
                or type(timeout_seconds) not in (int, float) or not math.isfinite(timeout_seconds) or timeout_seconds <= 0):
            raise RuntimeError('invalid bounded command capture/deadline')
        clock = execution.now if execution is not None else time.monotonic
        deadline = (execution.command_deadline(timeout_seconds, retirement)
                    if execution is not None else time.monotonic() + timeout_seconds)
        slot = execution.command_intent(argv, cwd, environment, label, retirement) if execution is not None else None
        if execution is not None and clock() >= deadline:
            error = RuntimeError('command phase deadline exhausted before launch')
            execution.command_abandoned(slot, error)
            raise error
        output = {'stdout': bytearray(), 'stderr': bytearray()}
        total = 0
        try:
            process = subprocess.Popen(argv, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       **({'env': environment} if environment is not None else {}),
                                       **({'start_new_session': True} if execution is not None else {}))
        except BaseException as error:
            if execution is not None:
                execution.command_launch_unknown(slot, error)
            raise
        selector = None
        primary = None
        try:
            if execution is not None:
                execution.command_started(slot, process)
            selector = selectors.DefaultSelector()
            for name in output:
                selector.register(getattr(process, name), selectors.EVENT_READ, name)
            while selector.get_map() or process.poll() is None:
                if execution is not None:
                    execution.command_tick(slot, process, retirement)
                remaining = deadline - clock()
                if remaining <= 0:
                    raise RuntimeError(f'command timed out after {timeout_seconds} seconds')
                for key, _ in selector.select(min(remaining, 0.1)):
                    chunk = os.read(key.fileobj.fileno(), min(65536, capture_limit_bytes - total + 1))
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    output[key.data].extend(chunk)
                    total += len(chunk)
                    if execution is not None:
                        execution.command_output(slot, key.data, chunk)
                    if total > capture_limit_bytes:
                        raise RuntimeError(f'command output exceeded combined {capture_limit_bytes} byte capture limit')
            if execution is not None and clock() >= deadline:
                raise RuntimeError('command completed outside its original clipped phase deadline')
            status = process.wait(timeout=max(0.001, deadline - clock()))
            result = subprocess.CompletedProcess(argv, status,
                output['stdout'].decode('utf-8', errors='replace'), output['stderr'].decode('utf-8', errors='replace'))
            if execution is not None:
                execution.command_settled(slot, process, status)
            if check and status:
                raise RuntimeError(f'command failed with exit {status}')
            return result
        except BaseException as error:
            primary = error
            error.add_note('bounded command stdout tail: ' + output['stdout'][-4096:].decode('utf-8', errors='replace'))
            error.add_note('bounded command stderr tail: ' + output['stderr'][-4096:].decode('utf-8', errors='replace'))
            try:
                if execution is not None:
                    execution.command_stop(slot, process, error)
                elif process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=1)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=1)
                else:
                    process.wait(timeout=1)
                if execution is None:
                    error.add_note(f'direct child {process.pid} reaped with exit {process.returncode}; descendant/build effects unconfirmed')
            except BaseException as cleanup_error:
                error.add_note('direct child termination/reap unconfirmed: ' + str(cleanup_error)[-4096:])
            raise
        finally:
            close_primary = primary
            for handle in ([selector] if selector is not None else []) + [getattr(process, name) for name in output]:
                try:
                    handle.close()
                except BaseException as close_error:
                    if execution is not None:
                        execution.command_close_failed(slot, close_error)
                    if close_primary is None:
                        close_primary = close_error
                    else:
                        close_primary.add_note('bounded command capture close failed: ' + str(close_error)[-4096:])
            if primary is None and close_primary is not None:
                raise close_primary
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


def git_value(source: Path, *args: str, capture_limit_bytes=None) -> str:
    options = {} if capture_limit_bytes is None else {"capture_limit_bytes": capture_limit_bytes}
    return run(["git", *args], cwd=source, **options).stdout.strip()


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
    if getattr(args, 'profile', 'portable-python') == 'common-arch' and getattr(args, 'engine_allocation', None):
        return common_engine_prefix(args)
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
    def overlaps(left: Path, right: Path) -> bool:
        return left == right or left in right.parents or right in left.parents

    home = Path.home()
    global_stores = [home / ".local/share/containers", Path("/var/lib/containers/storage"),
                     Path(f"/run/user/{os.getuid()}/containers"), Path("/run/containers/storage")]
    if os.environ.get("XDG_DATA_HOME"):
        global_stores.append(Path(os.environ["XDG_DATA_HOME"]) / "containers")
    paths = {}
    for name, value in (("graphroot", args.root), ("runroot", args.runroot), ("state", args.state_dir)):
        if ".." in Path(value).parts:
            raise RuntimeError(f"portable storage {name} contains parent traversal")
        path = Path(os.path.abspath(value))
        # Refuse known global scopes before asking the unprivileged caller to
        # traverse them. Python versions differ in is_symlink's handling of an
        # inaccessible ancestor; neither result grants global-store access.
        if any(overlaps(path, Path(os.path.abspath(store))) for store in global_stores):
            raise RuntimeError(f"portable storage {name} overlaps a global container store")
        if any(part.is_symlink() for part in (path, *path.parents)):
            raise RuntimeError(f"portable storage {name} has a symlink component")
        paths[name] = path

    source = source.resolve()
    for name, path in paths.items():
        if overlaps(path, source):
            raise RuntimeError(f"portable storage {name} overlaps the source checkout")
    for left, right in (("graphroot", "runroot"), ("graphroot", "state"), ("runroot", "state")):
        if overlaps(paths[left], paths[right]):
            raise RuntimeError(f"portable storage {left} overlaps {right}")
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


def inspect_image(prefix: list[str], image: str, *, capture_limit_bytes=None) -> dict:
    options = {} if capture_limit_bytes is None else {"capture_limit_bytes": capture_limit_bytes}
    values = json.loads(run(prefix + ["image", "inspect", image], **options).stdout)
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


def require_podman_options(prefix: list[str], command: str, required: tuple[str, ...], *, capture_limit_bytes=None) -> None:
    options = {} if capture_limit_bytes is None else {"capture_limit_bytes": capture_limit_bytes}
    help_text = run(prefix + [command, "--help"], **options).stdout
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


def inspect_oci_archive(candidate: Path, *, profile: str = 'portable-python', expected_inventory=None,
                        built_image_id=None, product_policy=None, observe=None) -> dict:
    if profile == 'sc2-headless':
        if product_policy is None:
            raise RuntimeError('closed product OCI policy required')
        return _inspect_common_oci_archive(candidate, None, built_image_id,
                                           product_policy=product_policy, observe=observe)
    if product_policy is not None or observe is not None:
        raise RuntimeError('product OCI options require the closed product profile')
    if profile == 'common-arch':
        return inspect_common_oci_archive(candidate, expected_inventory, built_image_id)
    if profile != 'portable-python':
        raise RuntimeError('unsupported OCI image profile')
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


def write_manifest(state: Path, manifest: dict, archive_identity: dict, *, image_name: str = IMAGE_NAME) -> Path:
    if manifest["image"]["id"] != archive_identity["configDigest"]:
        raise RuntimeError("OCI archive config identity differs from the qualified image ID")
    build_digest = manifest["image"]["digest"]
    manifest["image"]["buildDigest"] = build_digest
    manifest["image"]["digest"] = archive_identity["digest"]
    manifest["image"]["reference"] = f"{image_name}@{archive_identity['digest']}"
    manifest["image"]["archiveConfigDigest"] = archive_identity["configDigest"]
    return persist_manifest(state, manifest)


def append_journal(path: Path, record: dict, *, expected_identity=None) -> None:
    # Preserve both existing profile contracts; only the new Linux enclosing
    # owner supplies an already-created exact journal object.
    if expected_identity is None:
        stream = path.open('ab')
    else:
        descriptor = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW)
        try:
            stream = os.fdopen(descriptor, 'ab')
        except BaseException:
            os.close(descriptor)
            raise
    with stream:
        if expected_identity is not None:
            actual = os.fstat(stream.fileno())
            if (actual.st_dev, actual.st_ino, actual.st_nlink) != (*expected_identity, 1):
                raise RuntimeError('journal object identity changed')
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
    'inputs.json': '53c30494e1098af88d7efb78b3b0b80f7bee7894df6b6a902a82e40f3b8da8ce',
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
        common_require(not git_value(checkout, 'status', '--porcelain', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES) and git_value(checkout, 'rev-parse', 'HEAD', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES) == expected,
                       'dirty/wrong exact source')
    image_identity = {'repository': 'FS-GG/.github', 'revision': args.expected_image_source_revision,
                      'tree': git_value(image_source, 'rev-parse', 'HEAD^{tree}', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES),
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
    hooks = common_engine_paths(args)['hooks'] if getattr(args, 'engine_allocation', None) else None
    commands = common_build_commands(prefix, context, state, hooks_dir=hooks)
    return {'profile': 'common-arch', 'helperSourceRevision': args.expected_source_revision,
            'imageSourceRevision': args.expected_image_source_revision, 'acquiredInputsSha256': args.expected_acquired_inputs_sha256,
            'context': str(context), 'contextSha256': context_digest, 'buildCommands': commands, 'nativeExecutionEnabled': False}


# Root-reviewed source dispatch; operational admission remains separate.
COMMON_NATIVE_DISPATCH_ENABLED = True
COMMON_DEFAULT_CAPABILITIES = ('CHOWN', 'DAC_OVERRIDE', 'FOWNER', 'FSETID', 'KILL',
                               'NET_BIND_SERVICE', 'SETFCAP', 'SETGID', 'SETPCAP',
                               'SETUID', 'SYS_CHROOT')
COMMON_BUILD_CAPABILITIES = tuple(cap for cap in COMMON_DEFAULT_CAPABILITIES if cap != 'NET_BIND_SERVICE')


def common_build_commands(prefix, context, state, *, hooks_dir=None):
    """An explicit command boundary works for both short and closed engine prefixes."""
    hook_options = [] if hooks_dir is None else ['--hooks-dir=' + str(hooks_dir)]
    return [prefix + ['build', '--pull=never', '--isolation=rootless', '--network=none', '--http-proxy=false', '--timestamp=0',
                      '--platform=linux/amd64', '--format=oci', '--cap-drop=NET_BIND_SERVICE',
                      '--security-opt=no-new-privileges', '--tag', COMMON_IMAGE_NAME,
                      '--file', str(context / 'Containerfile'), *hook_options, str(context)],
            prefix + ['save', '--format=oci-archive', '--output', str(state / 'candidate.oci.tar'), COMMON_IMAGE_NAME]]


def common_engine_paths(args):
    allocation = common_raw_path(args.engine_allocation)
    paths = {name: allocation / name for name in ('home', 'xdg-runtime', 'config', 'data',
                                                   'download-tmp', 'libpod-tmp', 'hooks', 'network')}
    scratch = getattr(args, 'engine_scratch_dir', None)
    if scratch is not None:
        paths['download-tmp'] = common_raw_path(scratch)
        for name, other in paths.items():
            if name != 'download-tmp':
                chosen = paths['download-tmp']
                common_require(chosen != other and chosen not in other.parents and other not in chosen.parents,
                               'engine scratch overlaps namespace: ' + name)
    for name, path in paths.items():
        common_raw_path(path)
        for value in (args.source, args.image_source, args.acquisition_dir, args.root, args.runroot, args.state_dir):
            other = common_raw_path(value)
            common_require(path != other and path not in other.parents and other not in path.parents,
                           'engine namespace overlaps input/state/storage: ' + name)
    return paths


def common_scratch_identity(args, *, require_empty=False):
    """An optional common-only TMPDIR; never change libpod's persisted --tmpdir."""
    import stat
    if getattr(args, 'engine_scratch_dir', None) is None:
        return None
    path = common_engine_paths(args)['download-tmp']
    try:
        identity = path.lstat()
    except OSError as error:
        raise RuntimeError('common-arch: engine scratch directory missing/unreadable') from error
    common_require(stat.S_ISDIR(identity.st_mode) and identity.st_uid == os.geteuid()
                   and stat.S_IMODE(identity.st_mode) == 0o700, 'engine scratch must be owned directory mode0700')
    mountinfo = common_read(Path('/proc') / str(os.getpid()) / 'mountinfo', MAX_LOCK_BYTES)
    candidates = []
    for row in mountinfo.decode('utf-8').splitlines():
        fields = row.split()
        common_require(len(fields) >= 10 and '-' in fields, 'invalid engine scratch mount observation')
        mountpoint = Path(re.sub(r'\\([0-7]{3})', lambda match: chr(int(match[1], 8)), fields[4]))
        if (mountpoint == path or mountpoint in path.parents) and fields[2] == f'{os.major(identity.st_dev)}:{os.minor(identity.st_dev)}':
            candidates.append((len(mountpoint.parts), fields[fields.index('-') + 1]))
    common_require(candidates and max(candidates)[1] == 'tmpfs', 'engine scratch filesystem must be tmpfs')
    if require_empty:
        common_require(not any(path.iterdir()), 'engine scratch must be fresh and empty before preflight')
    current = path.lstat()
    common_require((current.st_dev, current.st_ino, current.st_uid, current.st_mode)
                   == (identity.st_dev, identity.st_ino, identity.st_uid, identity.st_mode), 'engine scratch changed during observation')
    return {'path': str(path), 'device': identity.st_dev, 'inode': identity.st_ino,
            'uid': identity.st_uid, 'mode': '0700', 'filesystem': 'tmpfs'}


def common_engine_prefix(args):
    paths = common_engine_paths(args)
    return [str(Path(args.podman).resolve()), '--storage-driver=vfs', '--root', str(common_raw_path(args.root)),
            '--runroot', str(common_raw_path(args.runroot)), '--remote=false', '--runtime=/usr/bin/crun',
            '--conmon=/usr/bin/conmon', '--cgroup-manager=cgroupfs', '--tmpdir=' + str(paths['libpod-tmp']),
            '--hooks-dir=' + str(paths['hooks']), '--network-config-dir=' + str(paths['network']),
            '--events-backend=file', '--storage-opt=']


def common_engine_environment(args):
    import tomllib
    paths = common_engine_paths(args)
    common_scratch_identity(args)
    expected = {'PATH': '/usr/bin:/bin', 'HOME': str(paths['home']), 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
                'XDG_RUNTIME_DIR': str(paths['xdg-runtime']), 'XDG_CONFIG_HOME': str(paths['config']),
                'XDG_DATA_HOME': str(paths['data']), 'TMPDIR': str(paths['download-tmp'])}
    config_names = {'CONTAINERS_CONF': 'containers.conf', 'CONTAINERS_STORAGE_CONF': 'storage.conf',
                    'CONTAINERS_REGISTRIES_CONF': 'registries.conf', 'REGISTRY_AUTH_FILE': 'auth.json',
                    'PODMAN_CONNECTIONS_CONF': 'podman-connections.json'}
    expected.update({key: str(paths['config'] / 'containers' / name) for key, name in config_names.items()})
    common_require(all(os.environ.get(key) == value for key, value in expected.items()), 'engine namespace environment mismatch')
    common_require(set(os.environ) <= set(expected) | {'USER', 'LOGNAME'}, 'unexpected engine environment variable')
    forbidden = ('CONTAINER_HOST', 'CONTAINER_CONNECTION', 'CONTAINERS_CONF_OVERRIDE', 'CONTAINERS_CONF_MODULES',
                 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy')
    common_require(not any(os.environ.get(key) for key in forbidden), 'engine remote/config/proxy override present')
    common_require(paths['hooks'].is_dir() and not any(paths['hooks'].iterdir()), 'engine OCI hooks directory is not empty')
    identities = {}
    for key in config_names:
        payload = common_read(Path(expected[key]), MAX_LOCK_BYTES)
        identities[key] = sha256_file_bytes(payload)
        if key == 'CONTAINERS_CONF':
            try:
                config = tomllib.loads(payload.decode())
            except (ValueError, UnicodeError) as error:
                raise RuntimeError('common-arch: invalid closed containers configuration') from error
            expected_config = {
                'containers': {'default_capabilities': config.get('containers', {}).get('default_capabilities'),
                               'volumes': [], 'mounts': [], 'env': [], 'env_host': False, 'http_proxy': False},
                'engine': {'env': [], 'cgroup_manager': 'cgroupfs', 'events_logger': 'file', 'runtime': 'crun',
                           'conmon_path': ['/usr/bin/conmon'], 'hooks_dir': [str(paths['hooks'])],
                           'runtimes': {'crun': ['/usr/bin/crun']}, 'runtimes_flags': {'crun': []}}}
            common_require(config == expected_config, 'unreviewed containers configuration key/value')
            capabilities = config.get('containers', {}).get('default_capabilities')
            common_require(isinstance(capabilities, list) and len(capabilities) == len(COMMON_BUILD_CAPABILITIES)
                           and set(capabilities) == {'CAP_' + cap for cap in COMMON_BUILD_CAPABILITIES}, 'builder configured capability set mismatch')
            common_require(config.get('engine', {}).get('runtimes_flags') == {'crun': []}, 'builder runtime flags must be explicitly empty')
            common_require(config.get('engine', {}).get('env') == []
                           and config.get('containers', {}).get('env') == []
                           and config.get('containers', {}).get('env_host') is False
                           and config.get('containers', {}).get('http_proxy') is False, 'engine configured environment injection refused')
        elif key in ('CONTAINERS_STORAGE_CONF', 'CONTAINERS_REGISTRIES_CONF'):
            try:
                config = tomllib.loads(payload.decode())
            except (ValueError, UnicodeError) as error:
                raise RuntimeError('common-arch: invalid closed storage/registry configuration') from error
            expected_config = ({'storage': {'driver': 'vfs', 'runroot': str(common_raw_path(args.runroot)),
                                           'graphroot': str(common_raw_path(args.root))}}
                               if key == 'CONTAINERS_STORAGE_CONF' else
                               {'unqualified-search-registries': [], 'short-name-mode': 'disabled'})
            common_require(config == expected_config, 'storage/registry configuration is not closed')
        elif key == 'REGISTRY_AUTH_FILE':
            common_require(common_json(payload) in ({}, {'auths': {}}), 'registry credentials are forbidden')
        elif key == 'PODMAN_CONNECTIONS_CONF':
            common_require(common_json(payload) in ({}, {'Connection': {}}), 'engine remote connections are forbidden')
    for mount_file in (Path('/etc/containers/mounts.conf'), Path('/usr/share/containers/mounts.conf'),
                       paths['config'] / 'containers/mounts.conf'):
        if os.path.lexists(mount_file):
            content = common_read(mount_file, MAX_LOCK_BYTES)
            common_require(not any(line.strip() and not line.strip().startswith(b'#') for line in content.splitlines()),
                           'implicit host mounts are not permitted')
            identities[str(mount_file)] = sha256_file_bytes(content)
    return identities


def verify_common_image(image):
    verify_image(image)
    config = image['Config']
    common_require(config.get('User') == '32768:32768', 'common image user/group mismatch')
    common_require(config.get('WorkingDir') == '/workspace', 'common image working directory mismatch')
    env = config.get('Env', [])
    common_require(isinstance(env, list) and all(isinstance(item, str) and '=' in item for item in env), 'invalid image environment')
    names = [item.split('=', 1)[0] for item in env]
    common_require(len(names) == len(set(names)) and 'HOME=/home/fsgg' in env, 'common image home/environment mismatch')


def common_layer_path(value):
    common_text(value)
    while value.startswith('./'):
        value = value[2:]
    if value in ('', '.'):
        return ''
    common_require(not value.startswith('/') and all(p not in ('', '.', '..') for p in value.split('/')),
                   'unsafe OCI layer path')
    return value


def common_virtual_resolve(path, population, *, reject_fifo_alias=False):
    """Resolve links inside the image root; never touch host filesystem paths."""
    from posixpath import normpath
    current = path
    followed_link = False
    for _ in range(40):
        parts = current.split('/')
        replaced = False
        for index in range(len(parts)):
            prefix = '/'.join(parts[:index+1])
            entry = population.get(prefix)
            if reject_fifo_alias and entry and entry['kind'] == 'fifo':
                common_require(not followed_link and index == len(parts) - 1,
                               'product OCI alias/traversal to FIFO refused')
            if entry and entry['kind'] in ('symlink', 'hardlink'):
                target = entry['target']
                suffix = '/'.join(parts[index+1:])
                base = '' if target.startswith('/') or entry['kind'] == 'hardlink' else '/'.join(parts[:index])
                joined = '/'.join(p for p in (base, target.lstrip('/'), suffix) if p)
                current = normpath('/' + joined).lstrip('/')
                replaced = True
                followed_link = True
                break
        if not replaced:
            return current
    raise RuntimeError('common-arch: OCI link cycle/bound exceeded')


def common_private_key_path(path):
    return (path == 'etc/pacman.d/gnupg/secring.gpg' or path.endswith('/secring.gpg')
            or '/private-keys-v1.d' in '/' + path)


def common_forbidden_image_path(path):
    fixed = ('var/lib/containers/storage', 'run/containers/storage', 'run/podman/podman.sock',
             'run/docker.sock', 'var/run/docker.sock', 'etc/pacman.d/gnupg/private-keys-v1.d',
             'etc/pacman.d/gnupg/secring.gpg')
    return (any(path == prefix or path.startswith(prefix + '/') for prefix in fixed)
            or '/.local/share/containers' in '/' + path
            or '/private-keys-v1.d' in '/' + path or path.endswith('/secring.gpg'))


class CommonTarInfo(tarfile.TarInfo):
    """Bound metadata before stdlib tar parsing can allocate extended headers."""
    @classmethod
    def fromtarfile(cls, archive):
        count = getattr(archive, '_common_header_count', 0) + 1
        common_require(count <= MAX_LAYER_SCAN_MEMBERS, 'common tar header bound exceeded')
        archive._common_header_count = count
        return super().fromtarfile(archive)

    def _proc_pax(self, archive):
        common_require(0 <= self.size <= MAX_LOCK_BYTES, 'common tar extended metadata bound exceeded')
        return super()._proc_pax(archive)

    def _proc_gnulong(self, archive):
        common_require(0 <= self.size <= MAX_LAYER_MEMBER_NAME_BYTES + 512, 'common tar long name bound exceeded')
        return super()._proc_gnulong(archive)


def product_tar_info(headers, json_bytes):
    """Product metadata guard around stdlib parsing, not another tar parser.

    _proc_member covers nested extension headers on both older and newer
    Python tarfile implementations; fromtarfile alone does not.
    """
    class ProductTarInfo(CommonTarInfo):
        @classmethod
        def fromtarfile(cls, archive):
            return tarfile.TarInfo.fromtarfile.__func__(cls, archive)

        def _proc_member(self, archive):
            count = getattr(archive, '_common_header_count', 0) + 1
            common_require(count <= headers, 'product tar header bound exceeded')
            archive._common_header_count = count
            common_require(self.type not in (tarfile.XGLTYPE, tarfile.GNUTYPE_SPARSE),
                           'product global PAX/sparse metadata refused before parsing')
            extended = self.type in (tarfile.XHDTYPE, tarfile.SOLARIS_XHDTYPE,
                                     tarfile.GNUTYPE_LONGNAME, tarfile.GNUTYPE_LONGLINK)
            depth = getattr(archive, '_product_metadata_depth', 0)
            if extended:
                common_require(depth < 16, 'product tar nested metadata bound exceeded')
                archive._product_metadata_depth = depth + 1
            try:
                return super()._proc_member(archive)
            finally:
                archive._product_metadata_depth = depth

        def _proc_pax(self, archive):
            common_require(0 <= self.size <= json_bytes, 'product tar extended metadata bound exceeded')
            return tarfile.TarInfo._proc_pax(self, archive)

        def _proc_gnusparse_00(self, *args):
            common_require(False, 'product sparse PAX metadata refused before expansion')

        _proc_gnusparse_01 = _proc_gnusparse_00
        _proc_gnusparse_10 = _proc_gnusparse_00
    return ProductTarInfo


def product_fifo_entry(member, name):
    """The one SC2-only FIFO metadata tuple; never read FIFO content."""
    entry = {'kind': 'fifo', 'type': '6', 'bytes': member.size, 'mode': member.mode,
             'uid': member.uid, 'gid': member.gid,
             'deviceMajor': member.devmajor, 'deviceMinor': member.devminor}
    common_require(member.type == tarfile.FIFOTYPE
                   and re.fullmatch(r'tmp/clr-debug-pipe-[1-9][0-9]{0,19}-[0-9]{1,20}-(in|out)', name)
                   is not None and member.linkname == '' and not member.issparse()
                   and all(type(value) is int for key, value in entry.items() if key not in ('kind', 'type'))
                   and (member.size, member.mode, member.uid, member.gid, member.devmajor, member.devminor)
                   == (0, 0o700, 0, 0, 0, 0),
                   'product CLR FIFO metadata refused: ' + name + ' ' + repr(entry))
    return entry


def common_layer_entries(stream, media_type, *, policy=None, observe=None, account_path=None):
    import gzip
    limits = policy or {'layerBytes': MAX_LAYER_SCAN_BYTES, 'headers': MAX_LAYER_SCAN_MEMBERS,
                        'hashFiles': False, 'strictTypes': False}
    tick = observe or (lambda: None)
    common_require(media_type in ('application/vnd.oci.image.layer.v1.tar', 'application/vnd.oci.image.layer.v1.tar+gzip'),
                   'unsupported common OCI layer compression')
    raw = gzip.GzipFile(fileobj=stream) if media_type.endswith('+gzip') else stream
    class BoundedHashReader:
        def __init__(self):
            self.bytes = 0
            self.digest = hashlib.sha256()
        def read(self, size=-1):
            common_require(size >= 0, 'unbounded layer read refused')
            tick()
            value = raw.read(min(size, 1024 * 1024, limits['layerBytes'] - self.bytes + 1))
            self.bytes += len(value)
            common_require(self.bytes <= limits['layerBytes'], 'common layer uncompressed byte bound exceeded')
            self.digest.update(value)
            return value
    reader = BoundedHashReader()
    entries, whiteouts, opaque = {}, set(), set()
    members = 0
    seen_names = set()
    try:
        info_type = product_tar_info(limits['headers'], limits['jsonBytes']) if policy is not None else CommonTarInfo
        with tarfile.open(fileobj=reader, mode='r|', tarinfo=info_type) as layer:
            for member in layer:
                tick()
                # Streaming mode still caches TarInfo objects by default. None
                # are needed after this iteration; retain only bounded semantic
                # entries, never a second unbounded header population.
                layer.members.clear()
                members += 1
                common_require(members <= limits['headers'], 'common layer member bound exceeded')
                name = common_layer_path(member.name)
                if account_path is not None:
                    account_path(name)
                if not name:
                    common_require(member.isdir(), 'invalid root layer member')
                    continue
                common_require(name not in seen_names, 'duplicate common layer path')
                seen_names.add(name)
                directory, _, basename = name.rpartition('/')
                if basename.startswith('.wh.'):
                    common_require(member.isfile() and member.size == 0, 'invalid OCI whiteout')
                    if basename == '.wh..wh..opq':
                        opaque.add(directory)
                    else:
                        common_require(basename != '.wh.', 'empty OCI whiteout target')
                        whiteouts.add('/'.join(p for p in (directory, basename[4:]) if p))
                    continue
                common_require(member.size >= 0, 'invalid common layer member size')
                if limits['strictTypes']:
                    common_require(not member.issparse() and
                                   (member.isfile() or member.isdir() or member.issym() or member.islnk()
                                    or (limits.get('clrFifoMetadata') is True and member.type == tarfile.FIFOTYPE)),
                                   'product OCI special/sparse member refused: ' + name +
                                   ' type=' + repr(member.type) + ' sparse=' + str(member.issparse()))
                entry = {'kind': 'other'}
                if limits.get('clrFifoMetadata') is True and member.type == tarfile.FIFOTYPE:
                    entry = product_fifo_entry(member, name)
                elif member.isdir():
                    entry['kind'] = 'directory'
                elif member.issym() or member.islnk():
                    common_text(member.linkname)
                    if account_path is not None:
                        account_path(member.linkname)
                    entry = {'kind': 'symlink' if member.issym() else 'hardlink', 'target': member.linkname}
                elif member.isfile():
                    entry['kind'] = 'file'
                    entry['bytes'] = member.size
                    common_require(not (member.size > 0 and common_private_key_path(name)),
                                   'OCI ancestor contains nonzero private key payload')
                    if name == 'opt/fsgg/package-inventory.txt':
                        common_require(member.size <= MAX_LOCK_BYTES, 'image inventory byte bound')
                        content = layer.extractfile(member)
                        common_require(content is not None, 'image inventory missing data')
                        entry['inventory'] = content.read(MAX_LOCK_BYTES + 1)
                        common_require(len(entry['inventory']) == member.size, 'image inventory size mismatch')
                    if limits['hashFiles'] and 'inventory' in entry:
                        entry['sha256'] = sha256_file_bytes(entry['inventory'])
                    elif limits['hashFiles']:
                        content = layer.extractfile(member)
                        common_require(content is not None, 'product file bytes missing')
                        value = hashlib.sha256()
                        count = 0
                        while True:
                            tick()
                            chunk = content.read(1024 * 1024)
                            if not chunk:
                                break
                            count += len(chunk)
                            common_require(count <= member.size, 'product member framing exceeded')
                            value.update(chunk)
                        common_require(count == member.size, 'product member framing incomplete')
                        entry['sha256'] = value.hexdigest()
                entries[name] = entry
        # Include all trailing tar blocks in the actual uncompressed diffID.
        while reader.read(1024 * 1024):
            pass
    except (OSError, tarfile.TarError, EOFError, RecursionError) as error:
        raise RuntimeError('common-arch: bounded OCI layer read failed') from error
    return entries, whiteouts, opaque, 'sha256:' + reader.digest.hexdigest(), layer._common_header_count, reader.bytes


def inspect_common_oci_archive(candidate, expected_inventory, built_image_id):
    try:
        return _inspect_common_oci_archive(candidate, expected_inventory, built_image_id)
    except (OSError, tarfile.TarError, EOFError, RecursionError, AttributeError, TypeError, KeyError) as error:
        raise RuntimeError('common-arch: malformed/bounded OCI archive read failed') from error


def _inspect_common_oci_archive(candidate, expected_inventory, built_image_id, *, product_policy=None, observe=None):
    policy = product_policy
    tick = observe or (lambda: None)
    archive_bound = policy['archiveBytes'] if policy is not None else MAX_PAYLOAD_BYTES
    expanded_bound = policy['expandedBytes'] if policy is not None else MAX_PAYLOAD_BYTES
    header_bound = policy['headers'] if policy is not None else MAX_LAYER_SCAN_MEMBERS
    image_name = policy['imageName'] if policy is not None else COMMON_IMAGE_NAME
    if policy is None:
        common_require(isinstance(expected_inventory, bytes), 'externally bound common inventory required')
        common_inventory(expected_inventory)
    else:
        common_require(candidate.stat().st_size <= archive_bound, 'product OCI archive byte bound exceeded')
    info_type = product_tar_info(264, policy['jsonBytes']) if policy is not None else CommonTarInfo
    with tarfile.open(candidate, 'r:', tarinfo=info_type) as archive:
        members = {}
        total = 0
        directory_names = set()
        for member in archive:
            tick()
            archive.members.clear()
            if member.isdir():
                name = member.name.rstrip('/')
                common_require(name in ('blobs', 'blobs/sha256') and name not in directory_names, 'invalid OCI archive directory')
                directory_names.add(name)
                continue
            common_require(len(members) < (132 if policy is not None else MAX_LAYER_SCAN_MEMBERS)
                           and member.name not in members, 'OCI archive population bound/duplicate')
            common_require(member.isfile() and not member.issym() and not member.islnk(), 'OCI archive member is not regular')
            common_path(member.name)
            common_require(member.size >= 0, 'invalid OCI archive member size')
            total += member.size
            common_require(total <= archive_bound, 'OCI archive byte bound exceeded')
            members[member.name] = member
        used = set()
        def read_json(name):
            json_bound = policy['jsonBytes'] if policy is not None else MAX_LOCK_BYTES
            common_require(name in members and members[name].size <= json_bound, 'bounded OCI JSON missing/overlarge')
            used.add(name)
            stream = archive.extractfile(members[name])
            payload = stream.read(json_bound + 1)
            common_require(len(payload) == members[name].size, 'OCI JSON bytes mismatch')
            return common_json(payload), payload
        layout, _ = read_json('oci-layout')
        common_require(layout == {'imageLayoutVersion': '1.0.0'}, 'OCI layout mismatch')
        index, _ = read_json('index.json')
        common_require(index.get('schemaVersion') == 2 and isinstance(index.get('manifests'), list)
                       and len(index['manifests']) == 1, 'OCI index population mismatch')
        descriptor = index['manifests'][0]
        def descriptor_json(value):
            common_require(isinstance(value, dict), 'invalid OCI descriptor')
            digest = common_text(value.get('digest'), r'sha256:[0-9a-f]{64}')
            common_size(value.get('size'))
            result, payload = read_json('blobs/sha256/' + digest[7:])
            common_require(len(payload) == value['size'] and sha256_file_bytes(payload) == digest[7:], 'OCI descriptor bytes mismatch')
            return result, payload
        common_require(descriptor.get('mediaType') == 'application/vnd.oci.image.manifest.v1+json'
                       and descriptor.get('annotations', {}).get('org.opencontainers.image.ref.name') == image_name,
                       'common OCI descriptor/tag mismatch')
        platform = descriptor.get('platform')
        common_require(platform is None or (platform.get('os'), platform.get('architecture')) == ('linux', 'amd64'), 'OCI descriptor platform mismatch')
        manifest, manifest_bytes = descriptor_json(descriptor)
        common_require(manifest.get('schemaVersion') == 2, 'OCI manifest version mismatch')
        config_descriptor = manifest.get('config', {})
        common_require(config_descriptor.get('mediaType') == 'application/vnd.oci.image.config.v1+json', 'OCI config media type mismatch')
        config, _ = descriptor_json(config_descriptor)
        common_require(config_descriptor['digest'] == built_image_id, 'common exported config differs from built image ID')
        observation = {'Os': config.get('os'), 'Architecture': config.get('architecture'), 'Config': config.get('config', {})}
        if policy is None:
            verify_common_image(observation)
        else:
            policy['verifyConfig'](observation)
        layers = manifest.get('layers')
        rootfs = config.get('rootfs', {})
        common_require(isinstance(layers, list) and 0 < len(layers) <= 128
                       and rootfs.get('type') == 'layers' and isinstance(rootfs.get('diff_ids'), list)
                       and len(rootfs['diff_ids']) == len(layers), 'common OCI layers/diffIDs mismatch')
        population = {}
        children = {}
        total_headers = total_expanded = total_fifo_entries = 0
        path_metadata_bytes = 0
        def account_path(value):
            nonlocal path_metadata_bytes
            path_metadata_bytes += len(value.encode('utf-8'))
            common_require(path_metadata_bytes <= policy['pathMetadataBytes'], 'product OCI path metadata byte bound exceeded')
        def resolve(value):
            return common_virtual_resolve(value, population, reject_fifo_alias=policy is not None)
        def deletion_parent(value, *, include_self=False):
            if policy is None:
                return
            parts = value.split('/') if value else []
            for index in range(1, len(parts) + (1 if include_self else 0)):
                parent = '/'.join(parts[:index])
                common_require(population.get(parent, {}).get('kind', 'directory') == 'directory',
                               'product OCI whiteout below non-directory refused')
        def remove_path(path):
            pending = [path]
            while pending:
                current = pending.pop()
                pending.extend(children.pop(current, set()))
                population.pop(current, None)
                parent = current.rpartition('/')[0]
                children.get(parent, set()).discard(current)
        def insert_path(path, entry):
            if population.get(path, {}).get('kind') == 'directory' and entry['kind'] != 'directory':
                remove_path(path)
            parts = path.split('/')
            for index in range(1, len(parts)):
                parent = '/'.join(parts[:index])
                existing = population.get(parent)
                common_require(existing is None or existing['kind'] == 'directory', 'OCI child below non-directory/link refused')
                if existing is None:
                    if policy is not None:
                        account_path(parent)
                    population[parent] = {'kind': 'directory'}
                    children.setdefault(parent.rpartition('/')[0], set()).add(parent)
            population[path] = entry
            children.setdefault(path.rpartition('/')[0], set()).add(path)
            common_require(len(population) <= header_bound, 'effective image population bound exceeded')
        for layer, expected_diff in zip(layers, rootfs['diff_ids']):
            tick()
            digest = common_text(layer.get('digest'), r'sha256:[0-9a-f]{64}')
            name = 'blobs/sha256/' + digest[7:]
            common_require(name in members and members[name].size == common_size(layer.get('size')), 'OCI layer size mismatch')
            used.add(name)
            stream = archive.extractfile(members[name])
            actual_hash = hashlib.sha256()
            while True:
                tick()
                block = stream.read(1024 * 1024)
                if not block:
                    break
                actual_hash.update(block)
            common_require(actual_hash.hexdigest() == digest[7:], 'OCI layer compressed digest mismatch')
            remaining_policy = (dict(policy, layerBytes=min(policy['layerBytes'], expanded_bound - total_expanded),
                                     headers=header_bound - total_headers) if policy is not None else None)
            if remaining_policy is not None:
                common_require(remaining_policy['layerBytes'] > 0 and remaining_policy['headers'] > 0,
                               'product cumulative OCI aperture exhausted before layer')
            entries, whiteouts, opaque, actual_diff, headers, expanded = common_layer_entries(
                archive.extractfile(members[name]), layer.get('mediaType'),
                policy=remaining_policy, observe=tick if policy is not None else None,
                account_path=account_path if policy is not None else None)
            total_headers += headers
            total_expanded += expanded
            common_require(total_headers <= header_bound and total_expanded <= expanded_bound,
                           'common OCI total expanded/header bound exceeded')
            common_require(actual_diff == common_text(expected_diff, r'sha256:[0-9a-f]{64}'), 'OCI layer uncompressed diffID mismatch')
            # Apply deletions to prior layers first, independently of tar member order.
            for removed in whiteouts:
                deletion_parent(removed)
                remove_path(removed)
            for directory in opaque:
                deletion_parent(directory, include_self=True)
                for removed in list(children.get(directory, set())):
                    remove_path(removed)
            # Dict insertion order is tar order. Bind a hardlink to the inode
            # available at its header, before any later same-layer replacement.
            # Forward links refuse rather than guessing an extraction policy.
            for path, entry in entries.items():
                tick()
                if entry['kind'] == 'fifo':
                    total_fifo_entries += 1
                if entry['kind'] == 'hardlink':
                    resolved = resolve(common_layer_path(entry['target']))
                    target = population.get(resolved, {})
                    common_require(target.get('kind') == 'file', 'unresolved/forward OCI hardlink')
                    common_require(not (common_private_key_path(path) and target.get('bytes', 0) > 0),
                                   'OCI ancestor private key hardlink payload')
                    entry = {**target, 'sensitiveOrigin': target.get('sensitiveOrigin', False)
                             or (common_private_key_path(resolved) and target.get('bytes', 0) > 0)}
                insert_path(path, entry)
            # Also catch a private-key directory alias to nonzero bytes elsewhere.
            for path in population:
                tick()
                if common_private_key_path(path):
                    pending = [resolve(path)]
                    visited = set()
                    while pending:
                        target_path = resolve(pending.pop())
                        if target_path in visited:
                            continue
                        visited.add(target_path)
                        target = population.get(target_path, {})
                        common_require(not (target.get('kind') == 'file' and target.get('bytes', 0) > 0),
                                       'OCI ancestor private key alias payload')
                        if target.get('kind') == 'directory':
                            pending.extend(children.get(target_path, set()))
        common_require(used == set(members), 'unbound OCI archive payload')
        effective_fifos = 0
        for path in population:
            tick()
            resolved = resolve(path)
            common_require(not common_forbidden_image_path(path) and not common_forbidden_image_path(resolved)
                           and not population[path].get('sensitiveOrigin'), 'effective image contains engine/private key state')
            entry = population[path]
            if entry['kind'] == 'fifo':
                effective_fifos += 1
            if entry['kind'] == 'hardlink':
                common_require(resolved in population and population[resolved]['kind'] == 'file', 'unresolved OCI hardlink')
        if policy is None:
            retained = population.get('opt/fsgg/package-inventory.txt', {})
            common_require(retained.get('kind') == 'file' and retained.get('inventory') == expected_inventory,
                           'effective common image inventory differs from acquired input')
        else:
            for path, expected in policy['files'].items():
                actual = population.get(path, {})
                common_require(actual.get('kind') == 'file' and actual.get('sha256') == expected,
                               'effective product source/input bytes mismatch: ' + path)
        result = {'digest': descriptor['digest'], 'configDigest': config_descriptor['digest'], 'manifestBytes': len(manifest_bytes)}
        if policy is not None:
            result.update({'rootfsDiffIds': rootfs['diff_ids'], 'config': config['config'],
                           'headers': total_headers, 'expandedBytes': total_expanded,
                           'effectiveMembers': len(population), 'pathMetadataBytes': path_metadata_bytes,
                           'fifoEntries': total_fifo_entries, 'effectiveFifos': effective_fifos})
        return result


def common_context_inventory(checked):
    context = common_raw_path(checked['context'])
    paths = common_population(context, 'acquired', 32769) | {'Containerfile', 'pacman.conf'}
    rows = []
    for path in sorted(paths):
        size, digest = common_read(context / path, MAX_PAYLOAD_BYTES)
        rows.append({'path': path, 'bytes': size, 'sha256': digest})
    common_require(sha256_file_bytes(canonical_bytes(rows)) == checked['contextSha256'], 'staged context changed before native effect')
    return common_read(context / 'acquired/package-inventory.expected', MAX_LOCK_BYTES)


def common_preflight(args, source, state, checked):
    scratch_identity = common_scratch_identity(args, require_empty=True)
    config_identities = common_engine_environment(args)
    expected_inventory = common_context_inventory(checked)
    prefix = podman_prefix(args)
    info = json.loads(run(prefix + ['info', '--format=json'], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout)
    require_effective_storage(info, args)
    host = info.get('host', {})
    common_require(host.get('os') == 'linux' and host.get('arch') == 'amd64'
                   and host.get('security', {}).get('rootless') is True, 'native engine rootless/platform mismatch')
    uid_maps = host.get('idMappings', {}).get('uidmap', [])
    common_require(any(mapping['container_id'] <= 32768 < mapping['container_id'] + mapping['size'] for mapping in uid_maps),
                   'native engine user mapping does not cover32768')
    version = run(prefix + ['version', '--format', '{{.Client.Version}}'], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout.strip()
    common_require(version == '6.1.3' and host.get('buildahVersion') == '1.45.1', 'reviewed Podman/Buildah version mismatch')
    require_podman_options(prefix, 'build', REQUIRED_BUILD_OPTIONS + ('--cap-drop', '--security-opt', '--isolation', '--hooks-dir', '--http-proxy'), capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
    require_podman_options(prefix, 'create', REQUIRED_CREATE_OPTIONS, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
    donor = inspect_image(prefix, COMMON_DONOR_REFERENCE, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
    common_require(donor.get('Os') == 'linux' and donor.get('Architecture') == 'amd64'
                   and 'sha256:' + donor.get('Id', '').removeprefix('sha256:') == COMMON_DONOR_CONFIG_DIGEST
                   and donor.get('Digest') == COMMON_DONOR_REFERENCE.split('@', 1)[1]
                   and COMMON_DONOR_REFERENCE in donor.get('RepoDigests', []), 'exact original donor reference/config/platform resolution mismatch')
    population = json.loads(run(prefix + ['ps', '--all', '--external', '--no-trunc', '--format=json'], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout)
    common_require(population == [], 'common native store has existing containers/unknown build effects')
    receipt = {'schema': 'fsgg.portable-workspace-image-preflight/1', 'profile': 'common-arch',
               'sourceRevision': checked['helperSourceRevision'], 'sourceTree': git_value(source, 'rev-parse', 'HEAD^{tree}', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES),
               'helperSourceRevision': checked['helperSourceRevision'], 'imageSourceRevision': checked['imageSourceRevision'],
               'imageSourceTree': git_value(Path(args.image_source), 'rev-parse', 'HEAD^{tree}', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES),
               'acquiredInputsSha256': checked['acquiredInputsSha256'], 'contextSha256': checked['contextSha256'],
               'containerfileSha256': COMMON_PINS['Containerfile'], 'imageSourceInputsSha256': COMMON_PINS['inputs.json'],
               'expectedInventorySha256': sha256_file_bytes(expected_inventory), 'donorReference': COMMON_DONOR_REFERENCE,
               'donorConfigDigest': COMMON_DONOR_CONFIG_DIGEST, 'engineConfigSha256': config_identities,
               'enginePrefix': prefix, 'podmanVersion': version, 'buildahVersion': host['buildahVersion'],
               'builderConfiguredCapabilities': list(COMMON_BUILD_CAPABILITIES), 'builderNoNewPrivilegesRequested': True,
               'builderNetworkRequested': 'none', 'builderIsolationRequested': 'rootless', 'rootless': True,
               'os': 'linux', 'architecture': 'amd64', 'containerUser': '32768:32768',
               'storage': {key: info['store'][key] for key in ('graphRoot', 'runRoot', 'graphDriverName')},
               'scope': 'native-prerequisites-observed; build/runtime-effective capabilities require native evidence'}
    checked['engineConfigSha256'] = config_identities
    if scratch_identity is not None:
        checked['engineScratchIdentity'] = scratch_identity
        receipt['engineScratchIdentity'] = scratch_identity
    path = state / 'preflight.json'
    with path.open('xb') as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(canonical_bytes(receipt))
    return path


def common_revalidate(args, checked):
    for checkout, expected in ((common_raw_path(args.source), checked['helperSourceRevision']),
                               (common_raw_path(args.image_source), checked['imageSourceRevision'])):
        common_require(not git_value(checkout, 'status', '--porcelain', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
                       and git_value(checkout, 'rev-parse', 'HEAD', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES) == expected, 'native source changed')
    identities = common_engine_environment(args)
    if 'engineScratchIdentity' in checked:
        common_require(common_scratch_identity(args) == checked['engineScratchIdentity'],
                       'engine scratch directory identity changed')
    if 'engineConfigSha256' in checked:
        common_require(identities == checked['engineConfigSha256'], 'native engine configuration changed')
    return common_context_inventory(checked)


def common_record(journal, record, primary=None):
    # Reporting must never replace an already observed operation/cleanup failure.
    try:
        append_journal(journal, record)
    except BaseException as error:
        if primary is None:
            raise
        primary.add_note('common journal reporting failed: ' + str(error)[-4096:])


def common_effect(args, checked, journal, name, command, timeout_seconds):
    common_revalidate(args, checked)
    common_record(journal, {'event': 'operation-start', 'name': name, 'argv': command})
    try:
        result = run(command, timeout_seconds=timeout_seconds, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
        common_record(journal, {'event': 'operation', 'name': name, 'exitCode': result.returncode,
                                'stdout': result.stdout[-4096:], 'stderr': result.stderr[-4096:]})
        return result
    except BaseException as error:
        common_record(journal, {'event': 'operation', 'name': name, 'outcome': 'failed', 'error': str(error)[-4096:],
                                'diagnostics': [note[-4096:] for note in getattr(error, '__notes__', [])]}, error)
        try:
            # Read the original population; never guess/remove a Buildah container.
            observed = run(podman_prefix(args) + ['ps', '--all', '--external', '--no-trunc', '--format=json'], check=False, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
            common_record(journal, {'event': 'cleanup', 'name': name, 'state': 'unconfirmed',
                                    'populationExitCode': observed.returncode, 'population': observed.stdout[-4096:],
                                    'reason': 'external build custody requires allocation owner teardown'}, error)
        except BaseException as observation_error:
            error.add_note('common external population observation failed: ' + str(observation_error)[-4096:])
        raise


def common_prepare(args, source, state, checked):
    build_dir = state / 'runs' / uuid.uuid4().hex
    build_dir.mkdir(parents=True, mode=0o700)
    build_journal = build_dir / 'journal.jsonl'
    built = common_effect(args, checked, build_journal, 'image-build', checked['buildCommands'][0], 600)
    common_require(json.loads(run(podman_prefix(args) + ['ps', '--all', '--external', '--no-trunc', '--format=json'], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout) == [],
                   'build left external containers; allocation owner teardown required')
    image = inspect_image(podman_prefix(args), COMMON_IMAGE_NAME, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
    verify_common_image(image)
    digest = common_text(image.get('Digest'), r'sha256:[0-9a-f]{64}')
    image_id = 'sha256:' + common_sha(image.get('Id', '').removeprefix('sha256:'))
    manifest = {'schema': 'fsgg.portable-workspace-local-image/1', 'profile': 'common-arch',
                'source': {'revision': checked['helperSourceRevision'], 'tree': git_value(source, 'rev-parse', 'HEAD^{tree}', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)},
                'imageSource': {'repository': 'FS-GG/.github', 'revision': checked['imageSourceRevision'],
                                'tree': git_value(Path(args.image_source), 'rev-parse', 'HEAD^{tree}', capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES),
                                'containerfileSha256': COMMON_PINS['Containerfile'], 'inputsSha256': COMMON_PINS['inputs.json']},
                'acquiredInputsSha256': checked['acquiredInputsSha256'], 'contextSha256': checked['contextSha256'],
                'image': {'name': COMMON_IMAGE_NAME, 'digest': digest, 'id': image_id,
                          'reference': COMMON_IMAGE_NAME + '@' + digest, 'os': 'linux', 'architecture': 'amd64',
                          'user': image['Config']['User']}, 'buildStdoutSha256': sha256_file_bytes(built.stdout.encode()),
                'podman': '6.1.3', 'buildJournal': str(build_journal),
                'buildJournalSha256': sha256_file(build_journal),
                'assertions': 'common native cases/export pending; no consumer activation'}
    return manifest['image']['reference'], manifest


def common_native_operations(expected_inventory, source_sha):
    inventory_script = ('import pathlib,subprocess; expected=pathlib.Path("/expected-inventory").read_bytes(); '
                        'retained=pathlib.Path("/opt/fsgg/package-inventory.txt").read_bytes(); '
                        'actual=subprocess.check_output(["/usr/bin/pacman","-Q"],env={"PATH":"/usr/bin:/bin","LC_ALL":"C"}); '
                        'actual=b"".join(sorted(actual.splitlines(keepends=True))); assert expected==retained==actual')
    filesystem_script = ('import os,pathlib; assert os.getuid()==32768 and os.getgid()==32768 and os.environ["HOME"]=="/home/fsgg"; '
                         'assert os.getcwd()=="/workspace"; '
                         'paths=["/var/lib/containers/storage","/run/containers/storage",'
                         '"/home/fsgg/.local/share/containers","/etc/pacman.d/gnupg/private-keys-v1.d",'
                         '"/etc/pacman.d/gnupg/secring.gpg","/run/podman/podman.sock","/run/docker.sock"]; '
                         'assert all(not os.path.lexists(path) for path in paths)')
    hash_script = ('import hashlib,pathlib; p=pathlib.Path("/source/eng/portable-workspace-image.py"); '
                   f'assert hashlib.sha256(p.read_bytes()).hexdigest()=="{source_sha}"; '
                   'q=pathlib.Path("/tmp/common-hash.txt"); q.write_bytes(b"offline-common-work\\n"); '
                   'assert hashlib.sha256(q.read_bytes()).hexdigest()==hashlib.sha256(b"offline-common-work\\n").hexdigest()')
    return [('complete-inventory', '/usr/bin/python', ['-c', inventory_script]),
            ('filesystem-profile', '/usr/bin/python', ['-c', filesystem_script]),
            ('offline-python-file-hash', '/usr/bin/python', ['-c', hash_script]),
            ('packaged-git-version', '/usr/bin/git', ['--version']),
            ('packaged-engine-version-passive', '/usr/bin/podman', ['--version'])]


def common_qualify(args, source, state, image_reference, checked):
    expected_inventory = common_revalidate(args, checked)
    prefix = podman_prefix(args)
    image = inspect_image(prefix, image_reference, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
    verify_common_image(image)
    expected_id = image['Id'].removeprefix('sha256:')
    run_id = uuid.uuid4().hex
    run_dir = state / 'runs' / run_id
    run_dir.mkdir(parents=True, mode=0o700)
    journal = run_dir / 'journal.jsonl'
    append_journal(journal, {'event': 'run-start', 'runId': run_id, 'profile': 'common-arch',
                            'sourceRevision': checked['helperSourceRevision'], 'imageSourceRevision': checked['imageSourceRevision'],
                            'acquiredInputsSha256': checked['acquiredInputsSha256'], 'contextSha256': checked['contextSha256'],
                            'image': image_reference, 'scope': 'image inventory/files/hash/passive tools; no nested engine/consumer activation'})
    operations = common_native_operations(expected_inventory, sha256_file(source / 'eng/portable-workspace-image.py'))
    try:
        for index, (name, executable, arguments) in enumerate(operations):
            container = 'fsgg-common-' + run_id[:12] + '-' + str(index)
            cid = None
            failure = None
            try:
                common_revalidate(args, checked)
                created = run(prefix + ['create', '--name', container, '--label=fsgg.portable-workspace.qualification=true',
                                       '--label=fsgg.portable-workspace.run=' + run_id, '--pull=never', '--read-only',
                                       '--network=none', '--cap-drop=all', '--security-opt=no-new-privileges',
                                       '--volume=' + str(source) + ':/source:ro',
                                       '--volume=' + str(Path(checked['context']) / 'acquired/package-inventory.expected') + ':/expected-inventory:ro',
                                       '--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=64m', '--workdir=/workspace',
                                       '--entrypoint=' + executable, '--unsetenv-all', '--env=HOME=/home/fsgg',
                                       '--env=PATH=/usr/bin:/bin', '--env=LANG=C.UTF-8', '--env=LC_ALL=C', image_reference, *arguments], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
                cid = common_text(created.stdout.strip(), r'[0-9a-f]{64}')
                started = run(prefix + ['start', '--attach', cid], check=False, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
                command_failure = RuntimeError('common-arch: native common case failed: ' + name) if started.returncode else None
                try:
                    observation = json.loads(run(prefix + ['container', 'inspect', cid], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout)[0]
                    common_require(observation.get('Id') == cid and observation.get('Image', '').removeprefix('sha256:') == expected_id
                                   and observation.get('Config', {}).get('Labels', {}).get('fsgg.portable-workspace.run') == run_id,
                                   'native case custody/image mismatch')
                except BaseException as observation_error:
                    if command_failure is not None:
                        command_failure.add_note('common case observation failed: ' + str(observation_error)[-4096:])
                        raise command_failure from observation_error
                    raise
                case_failure = command_failure
                if observation['State']['ExitCode'] != 0 or observation['State'].get('Running'):
                    case_failure = case_failure or RuntimeError('common-arch: native common case failed: ' + name)
                common_record(journal, {'event': 'operation', 'name': name, 'containerId': cid,
                                       'imageId': 'sha256:' + expected_id, 'exitCode': observation['State']['ExitCode'],
                                       'stdout': started.stdout[-4096:], 'stderr': started.stderr[-4096:]}, case_failure)
                if case_failure is not None:
                    raise case_failure
            except BaseException as error:
                failure = error
                raise
            finally:
                # Never remove by a proposed name after an uncertain create effect.
                if cid is None:
                    common_record(journal, {'event': 'cleanup', 'name': name, 'state': 'unconfirmed',
                                            'proposedName': container, 'reason': 'create identity unavailable; original effect must be observed'}, failure)
                    if failure is None:
                        raise RuntimeError('common-arch: missing native case identity; cleanup unconfirmed')
                else:
                    try:
                        actual = json.loads(run(prefix + ['container', 'inspect', cid], capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES).stdout)[0]
                        common_require(actual.get('Id') == cid and actual.get('Image', '').removeprefix('sha256:') == expected_id
                                       and actual.get('Config', {}).get('Labels', {}).get('fsgg.portable-workspace.run') == run_id,
                                       'cleanup identity mismatch')
                        removed = run(prefix + ['container', 'rm', '--force', cid], check=False, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
                        exists = run(prefix + ['container', 'exists', cid], check=False, capture_limit_bytes=COMMON_CAPTURE_LIMIT_BYTES)
                        common_require(removed.returncode == 0 and exists.returncode == 1, 'native case removal not positively observed')
                        append_journal(journal, {'event': 'cleanup', 'name': name, 'containerId': cid, 'state': 'observed'})
                    except BaseException as cleanup_error:
                        common_record(journal, {'event': 'cleanup', 'name': name, 'containerId': cid, 'state': 'unconfirmed',
                                                'error': str(cleanup_error)[-4096:]}, failure or cleanup_error)
                        if failure is None:
                            raise
                        failure.add_note('common native cleanup unconfirmed: ' + str(cleanup_error))
        append_journal(journal, {'event': 'run-complete', 'runId': run_id, 'outcome': 'passed', 'cases': [case[0] for case in operations]})
    except BaseException as error:
        common_record(journal, {'event': 'run-complete', 'runId': run_id, 'outcome': 'failed', 'error': str(error)[-4096:],
                                'cleanupUncertainty': list(getattr(error, '__notes__', []))}, error)
        raise
    return journal


def common_native_main(args):
    common_require(args.engine_allocation is not None, 'common native dispatch requires closed engine allocation')
    source = common_raw_path(args.source)
    state = common_raw_path(args.state_dir)
    checked = check_common_inputs(args)
    common_require(checked['buildCommands'] == common_build_commands(podman_prefix(args), Path(checked['context']), state, hooks_dir=common_engine_paths(args)['hooks']),
                   'native command/context correspondence mismatch')
    preflight_path = common_preflight(args, source, state, checked)
    if args.mode == 'preflight':
        return {'preflight': str(preflight_path), 'preflightSha256': sha256_file(preflight_path)}
    reference, manifest = common_prepare(args, source, state, checked)
    build_manifest = persist_manifest(state, manifest)
    journal = common_qualify(args, source, state, reference, checked)
    common_effect(args, checked, journal, 'image-export', checked['buildCommands'][1], 300)
    candidate = state / 'candidate.oci.tar'
    os.chmod(candidate, 0o600)
    archive_identity = inspect_oci_archive(candidate, profile='common-arch',
                                           expected_inventory=common_context_inventory(checked), built_image_id=manifest['image']['id'])
    manifest['assertions'] = 'local image cases/export passed; fresh reload/publication/consumer activation not established'
    manifest_path = write_manifest(state, manifest, archive_identity, image_name=COMMON_IMAGE_NAME)
    return {'candidate': str(candidate), 'candidateSha256': sha256_file(candidate), 'imageReference': manifest['image']['reference'],
            'buildImageReference': reference, 'buildManifest': str(build_manifest), 'buildManifestSha256': build_manifest.stem.removeprefix('sha256-'),
            'imageId': manifest['image']['id'], 'manifest': str(manifest_path), 'manifestSha256': manifest_path.stem.removeprefix('sha256-'),
            'journal': str(journal), 'journalSha256': sha256_file(journal)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("preflight", "qualify", "check-inputs"))
    parser.add_argument("--source", required=True)
    parser.add_argument("--expected-source-revision", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--podman", default="/usr/bin/podman")
    parser.add_argument("--root", required=True)
    parser.add_argument("--runroot", required=True)
    parser.add_argument("--profile", choices=("portable-python", "common-arch", "sc2-headless"), default="portable-python")
    parser.add_argument("--operation-input")
    parser.add_argument("--expected-operation-input-sha256")
    parser.add_argument("--engine-allocation")
    parser.add_argument("--engine-scratch-dir")
    parser.add_argument("--image-source")
    parser.add_argument("--expected-image-source-revision")
    parser.add_argument("--acquisition-dir")
    parser.add_argument("--expected-acquired-inputs-sha256")
    args = parser.parse_args()
    if args.profile == 'sc2-headless':
        if sys.platform != 'linux':
            parser.error('sc2-headless requires the selected Linux profile')
        import importlib.util
        import stat
        implementation = Path(__file__).with_name('portable_workspace_sc2.py')
        spec = importlib.util.spec_from_file_location('portable_workspace_sc2', implementation)
        profile = importlib.util.module_from_spec(spec)
        # Compile captured regular source, never read/write a sibling pyc before
        # the profile's independent source/cleanliness checks. This bootstrap
        # correspondence is not permission for an operation.
        descriptor = os.open(implementation, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            with os.fdopen(descriptor, 'rb') as stream:
                descriptor = None
                info = os.fstat(stream.fileno())
                if not stat.S_ISREG(info.st_mode) or info.st_size > 1048576:
                    raise RuntimeError('internal profile regular source byte bound exceeded')
                profile_source = stream.read(1048577)
                if len(profile_source) != info.st_size:
                    raise RuntimeError('internal profile source changed during capture')
            exec(compile(profile_source, str(implementation), 'exec'), profile.__dict__)
            profile.__dict__['_BOOTSTRAP_SOURCE_SHA256'] = hashlib.sha256(profile_source).hexdigest()
        finally:
            if descriptor is not None:
                os.close(descriptor)
        print(json.dumps(profile.main(args, globals()), sort_keys=True, separators=(',', ':')))
        return 0
    if args.operation_input is not None or args.expected_operation_input_sha256 is not None:
        parser.error('operation input requires the closed sc2-headless profile')
    common_flags = (args.image_source, args.expected_image_source_revision,
                    args.acquisition_dir, args.expected_acquired_inputs_sha256, args.engine_allocation, args.engine_scratch_dir)
    if args.profile == "common-arch":
        if args.mode != "check-inputs" and not COMMON_NATIVE_DISPATCH_ENABLED:
            parser.error("common-arch native preflight/qualification source dispatch disabled pending semantic review")
        if not all(common_flags[:4]):
            parser.error("common-arch requires image source and externally bound acquisition inputs")
        if (args.mode != "check-inputs" or args.engine_scratch_dir is not None) and args.engine_allocation is None:
            parser.error("common-arch native modes require --engine-allocation")
        result = check_common_inputs(args) if args.mode == "check-inputs" else common_native_main(args)
        print(json.dumps(result, sort_keys=True, separators=(",", ":")))
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
