#!/usr/bin/env python3
"""Prepare and qualify the pinned portable workspace OCI image locally."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import uuid


IMAGE_NAME = "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2"


def run(argv: list[str], *, cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(argv, cwd=cwd, check=check, text=True, capture_output=True)


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


def require_exact_source(source: Path) -> tuple[str, str, str]:
    if git_value(source, "status", "--porcelain"):
        raise RuntimeError("qualification source must be a clean committed Git worktree")
    revision = git_value(source, "rev-parse", "HEAD")
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
    if config.get("User") not in ("65532", "65532:65532"):
        raise RuntimeError("image does not select the fixed non-root user")
    if entrypoint not in (None, []) or command not in (None, []) or volumes not in (None, {}):
        raise RuntimeError("image declares an entrypoint, command, or volume hook")


def clear_image_hooks(prefix: list[str], image: str, state: Path) -> str:
    """Clear inherited process hooks in an OCI archive, then load the amended image."""
    original = state / "prepared-with-hooks.oci.tar"
    amended = state / "prepared.oci.tar"
    run(prefix + ["save", "--format=oci-archive", "--output", str(original), image])
    with tempfile.TemporaryDirectory(prefix="portable-oci-", dir=state) as temporary:
        layout = Path(temporary)
        with tarfile.open(original, "r") as archive:
            archive.extractall(layout, filter="data")
        index_path = layout / "index.json"
        index = json.loads(index_path.read_text())
        descriptor = index["manifests"][0]
        manifest_path = layout / "blobs" / "sha256" / descriptor["digest"].removeprefix("sha256:")
        manifest = json.loads(manifest_path.read_text())
        config_descriptor = manifest["config"]
        config_path = layout / "blobs" / "sha256" / config_descriptor["digest"].removeprefix("sha256:")
        config = json.loads(config_path.read_text())
        config["config"]["Entrypoint"] = []
        config["config"]["Cmd"] = []
        config_payload = canonical_bytes(config)
        config_digest = hashlib.sha256(config_payload).hexdigest()
        (layout / "blobs" / "sha256" / config_digest).write_bytes(config_payload)
        manifest["config"] = {**config_descriptor, "digest": f"sha256:{config_digest}", "size": len(config_payload)}
        manifest_payload = canonical_bytes(manifest)
        manifest_digest = hashlib.sha256(manifest_payload).hexdigest()
        (layout / "blobs" / "sha256" / manifest_digest).write_bytes(manifest_payload)
        index["manifests"][0] = {**descriptor, "digest": f"sha256:{manifest_digest}", "size": len(manifest_payload)}
        index_path.write_bytes(canonical_bytes(index))
        with tarfile.open(amended, "w") as archive:
            for path in sorted(layout.rglob("*")):
                info = archive.gettarinfo(path, arcname=str(path.relative_to(layout)))
                info.mtime = 0
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                if path.is_file():
                    with path.open("rb") as stream:
                        archive.addfile(info, stream)
                else:
                    archive.addfile(info)
    return run(prefix + ["load", "--input", str(amended)]).stdout


def prepare(args: argparse.Namespace, source: Path, state: Path) -> tuple[str, Path, dict]:
    revision, tree, fixture_sha256 = require_exact_source(source)
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
    if args.nested_host_namespace_workaround:
        container = f"fsgg-portable-preparation-{uuid.uuid4().hex[:12]}"
        base = inputs["base"]["name"].split(":", 1)[0] + "@" + inputs["base"]["ociDigest"]
        install = (
            "set -eu; "
            "tar -xzf /tmp/node.tar.gz -C /usr/local --strip-components=1; "
            "mkdir -p /opt/typescript; "
            "tar -xzf /tmp/typescript.tgz -C /opt/typescript --strip-components=1; "
            "chmod 0555 /opt/typescript/bin/tsc /opt/typescript/bin/tsserver; "
            "rm /tmp/node.tar.gz /tmp/typescript.tgz; "
            "test \"$(/usr/local/bin/python3 --version)\" = \"Python 3.14.0\"; "
            "test \"$(/usr/local/bin/node --version)\" = \"v24.8.0\"; "
            "test \"$(/opt/typescript/bin/tsc --version)\" = \"Version 5.9.2\""
        )
        created = run(prefix + ["create", "--name", container, "--network=none", "--pid=host", "--uts=host", "--entrypoint=/bin/sh", base, "-c", install])
        try:
            run(prefix + ["cp", str(cache / inputs["node"]["archive"]), f"{container}:/tmp/node.tar.gz"])
            run(prefix + ["cp", str(cache / inputs["typescript"]["archive"]), f"{container}:/tmp/typescript.tgz"])
            try:
                started = run(prefix + ["start", "--attach", container])
            except subprocess.CalledProcessError as error:
                raise RuntimeError(f"Podman preparation container failed: {error.stderr.strip()}") from error
            committed = run(
                prefix
                + [
                    "commit",
                    "--format=oci",
                    "--change=USER 65532:65532",
                    "--change=WORKDIR /source",
                    "--change=ENTRYPOINT []",
                    "--change=CMD []",
                    "--change=LABEL org.opencontainers.image.title=FS.GG-portable-workspace-qualification-toolchain",
                    "--change=LABEL org.opencontainers.image.licenses=PSF-2.0-AND-MIT-AND-Apache-2.0",
                    container,
                    IMAGE_NAME,
                ]
            )
            sanitized = clear_image_hooks(prefix, IMAGE_NAME, state)
            build_output = created.stdout + started.stdout + committed.stdout + sanitized
        finally:
            run(prefix + ["container", "rm", container], check=False)
    else:
        try:
            built = run(
                prefix
                + [
                    "build",
                    "--pull=never",
                    "--platform=linux/amd64",
                    "--format=oci",
                    "--tag",
                    IMAGE_NAME,
                    "--file",
                    str(context / "Containerfile"),
                    str(context),
                ]
            )
        except subprocess.CalledProcessError as error:
            raise RuntimeError(f"Podman image build failed: {error.stderr.strip()}") from error
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
        "nestedHostNamespaceWorkaround": args.nested_host_namespace_workaround,
        "podman": run(prefix + ["version", "--format", "{{.Client.Version}}"]).stdout.strip(),
    }
    payload = canonical_bytes(manifest)
    manifest_digest = hashlib.sha256(payload).hexdigest()
    manifests = state / "manifests"
    manifests.mkdir(parents=True, exist_ok=True, mode=0o700)
    path = manifests / f"sha256-{manifest_digest}.json"
    with path.open("xb") as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return f"{IMAGE_NAME}@{digest}", path, manifest


def append_journal(path: Path, record: dict) -> None:
    with path.open("ab") as stream:
        stream.write(canonical_bytes(record))
        stream.flush()
        os.fsync(stream.fileno())


def qualify(args: argparse.Namespace, source: Path, state: Path, image_reference: str) -> Path:
    revision, tree, fixture_sha256 = require_exact_source(source)
    prefix = podman_prefix(args)
    run_id = uuid.uuid4().hex
    run_dir = state / "runs" / run_id
    output = run_dir / "output"
    output.mkdir(parents=True, mode=0o755)
    journal = run_dir / "journal.jsonl"
    os.chmod(run_dir, 0o700)
    run(prefix + ["unshare", "chown", "65532:65532", str(output)])

    common_env = [
        "--unsetenv-all",
        "--env=HOME=/tmp",
        "--env=LANG=C.UTF-8",
        "--env=PATH=/usr/local/bin:/opt/typescript/bin:/usr/bin:/bin",
        "--env=PYTHONDONTWRITEBYTECODE=1",
        "--env=PYTHONPYCACHEPREFIX=/output/pycache",
    ]
    fixture = "tests/portable-workspace/image/fixture"
    operations = [
        ("versions", "/source", "/usr/local/bin/python3", ["-c", "import subprocess,sys; assert sys.version_info[:3] == (3,14,0); subprocess.run(['/usr/local/bin/node','--version'],check=True); subprocess.run(['/opt/typescript/bin/tsc','--version'],check=True)"]),
        ("python-build", f"/source/{fixture}/python", "/usr/local/bin/python3", ["-m", "py_compile", "app.py"]),
        ("python-test", f"/source/{fixture}/python", "/usr/local/bin/python3", ["test.py"]),
        ("frontend-build", f"/source/{fixture}/composed/frontend", "/opt/typescript/bin/tsc", ["--project", "tsconfig.json", "--outDir", "/output/frontend"]),
        ("backend-build", f"/source/{fixture}/composed/backend", "/usr/local/bin/python3", ["-m", "py_compile", "service.py"]),
        ("backend-test", f"/source/{fixture}/composed/backend", "/usr/local/bin/python3", ["test.py"]),
        ("composed-journey", f"/source/{fixture}/composed/product", "/usr/local/bin/python3", ["journey.py", "/output/frontend/app.js", "/usr/local/bin/node"]),
    ]
    append_journal(journal, {"event": "run-start", "runId": run_id, "sourceRevision": revision, "sourceTree": tree, "fixtureArchiveSha256": fixture_sha256, "image": image_reference, "nestedHostNamespaceWorkaround": args.nested_host_namespace_workaround})
    try:
        for index, (name, workdir, executable, arguments) in enumerate(operations):
            container = f"fsgg-portable-{run_id[:12]}-{index}"
            namespace_workaround = ["--pid=host", "--uts=host"] if args.nested_host_namespace_workaround else []
            create = run(
                prefix
                + [
                    "create",
                    "--name",
                    container,
                    "--pull=never",
                    "--read-only",
                    "--network=none",
                    "--cap-drop=all",
                    "--security-opt=no-new-privileges",
                    *namespace_workaround,
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
            append_journal(
                journal,
                {
                    "event": "operation",
                    "name": name,
                    "containerId": container_id,
                    "imageId": inspected["Image"],
                    "exitCode": inspected["State"]["ExitCode"],
                    "stdout": started.stdout,
                    "stderr": started.stderr,
                },
            )
            run(prefix + ["container", "rm", container])
            if started.returncode != 0 or inspected["State"]["ExitCode"] != 0:
                raise RuntimeError(f"qualification operation failed: {name}")
        append_journal(journal, {"event": "run-complete", "runId": run_id, "outcome": "passed"})
    except BaseException as error:
        append_journal(journal, {"event": "run-complete", "runId": run_id, "outcome": "failed", "error": str(error)})
        raise
    return journal


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--podman", default="/usr/bin/podman")
    parser.add_argument("--root", required=True)
    parser.add_argument("--runroot", required=True)
    parser.add_argument(
        "--nested-host-namespace-workaround",
        action="store_true",
        help="Use host PID/UTS namespaces only to diagnose nested environments that prohibit proc/UTS setup; this does not qualify strict isolation",
    )
    args = parser.parse_args()
    source = Path(args.source).resolve()
    state = Path(args.state_dir).resolve()
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(state, 0o700)
    if args.nested_host_namespace_workaround:
        containers_conf = state / "containers.conf"
        containers_conf.write_text("[containers]\ndefault_sysctls = []\n")
        os.chmod(containers_conf, 0o600)
        os.environ["CONTAINERS_CONF"] = str(containers_conf)
    image_reference, manifest_path, manifest = prepare(args, source, state)
    journal = qualify(args, source, state, image_reference)
    result = {
        "imageReference": image_reference,
        "imageId": manifest["image"]["id"],
        "manifest": str(manifest_path),
        "manifestSha256": manifest_path.stem.removeprefix("sha256-"),
        "journal": str(journal),
        "journalSha256": sha256_file(journal),
    }
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
