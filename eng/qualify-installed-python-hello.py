#!/usr/bin/env python3
"""Prepare exact installed CLI custody and invoke its fixed production route."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import signal
import threading
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


SCHEMA = "fsgg.portable-workspace-installed-python-candidate/1"
INVOCATION_SCHEMA = "fsgg.portable-workspace-installed-python-invocation/1"
PACKAGE_ID = "FS.GG.Coordination.Cli"
CLI_DLL = "FS.GG.Coordination.Cli.dll"
TOOL_PREFIX = PurePosixPath("tools/net10.0/any")
IMAGE_NAME = "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2"
GRANT_PATH = Path("/etc/fsgg/portable-workspaces/python-hello-v1.json")
MAX_PACKAGE_ENTRIES = 4096
MAX_PACKAGE_BYTES = 128 * 1024 * 1024
MAX_FILE_BYTES = 16 * 1024 * 1024
MAX_OUTPUT_BYTES = 1024 * 1024


def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def exact_sha(value: str) -> str:
    if len(value) != 64 or any(character not in "0123456789abcdef" for character in value):
        raise ValueError("expected digest must be lowercase SHA-256")
    return value


def exact_commit(value: str) -> str:
    if len(value) != 40 or any(character not in "0123456789abcdef" for character in value):
        raise ValueError("source identity must be an exact lowercase Git commit")
    return value


def safe_name(value: str) -> PurePosixPath:
    if "\\" in value or "\n" in value or "\r" in value:
        raise ValueError("package contains a noncanonical path")
    path = PurePosixPath(value)
    if not value or path.is_absolute() or ".." in path.parts or "." in path.parts:
        raise ValueError("package contains an unsafe path")
    return path


def payload_digest(root: Path) -> tuple[str, list[dict[str, object]]]:
    entries: list[dict[str, object]] = []
    total = 0
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        info = path.lstat()
        if stat.S_ISDIR(info.st_mode):
            if path.is_symlink():
                raise ValueError("installed payload contains a linked directory")
            continue
        if not stat.S_ISREG(info.st_mode) or path.is_symlink():
            raise ValueError("installed payload contains a non-regular file")
        if len(relative) > 512 or "\n" in relative or "\r" in relative:
            raise ValueError("installed payload path is outside bounds")
        if info.st_size > MAX_FILE_BYTES:
            raise ValueError("installed payload file exceeds its bound")
        total += info.st_size
        if total > MAX_PACKAGE_BYTES or len(entries) >= MAX_PACKAGE_ENTRIES:
            raise ValueError("installed payload exceeds its closure bound")
        entries.append({"path": relative, "sha256": sha256_file(path), "size": info.st_size})
    if not entries:
        raise ValueError("installed payload is empty")
    lines = "".join(f"{item['sha256']} {item['size']} {item['path']}\n" for item in entries)
    return sha256_bytes(lines.encode()), entries


def package_identity(package: Path, expected_version: str) -> tuple[str, str]:
    with zipfile.ZipFile(package) as archive:
        nuspecs = [item for item in archive.infolist() if PurePosixPath(item.filename).suffix == ".nuspec"]
        if len(nuspecs) != 1:
            raise ValueError("package must contain exactly one nuspec")
        root = ET.fromstring(archive.read(nuspecs[0]))
        values = {item.tag.rsplit("}", 1)[-1]: (item.text or "") for item in root.iter()}
        if values.get("id") != PACKAGE_ID or values.get("version") != expected_version:
            raise ValueError("package identity differs from the candidate")
        return values["id"], values["version"]


def extract_package(package: Path, install_root: Path) -> list[dict[str, object]]:
    if install_root.exists() and (not install_root.is_dir() or install_root.is_symlink() or any(install_root.iterdir())):
        raise ValueError("install root must be absent or an empty real directory")
    install_root.mkdir(parents=True, mode=0o755, exist_ok=True)
    seen: set[str] = set()
    folded: set[str] = set()
    entries: list[dict[str, object]] = []
    total = 0
    with zipfile.ZipFile(package) as archive:
        infos = archive.infolist()
        if not infos or len(infos) > MAX_PACKAGE_ENTRIES:
            raise ValueError("package entry count is outside bounds")
        for info in infos:
            name = safe_name(info.filename.rstrip("/"))
            canonical_name = name.as_posix()
            if canonical_name in seen or canonical_name.casefold() in folded:
                raise ValueError("package repeats or case-collides a path")
            seen.add(canonical_name)
            folded.add(canonical_name.casefold())
            mode = info.external_attr >> 16
            if stat.S_ISLNK(mode) or info.flag_bits & 1:
                raise ValueError("package contains a link or encrypted entry")
            target = install_root.joinpath(*name.parts)
            if info.is_dir():
                target.mkdir(parents=True, mode=0o755, exist_ok=True)
                if target.is_symlink() or not target.is_dir():
                    raise ValueError("package directory is not a real directory")
                continue
            if info.file_size < 0 or info.file_size > MAX_FILE_BYTES:
                raise ValueError("package file exceeds its bound")
            total += info.file_size
            if total > MAX_PACKAGE_BYTES:
                raise ValueError("package payload exceeds its bound")
            target.parent.mkdir(parents=True, mode=0o755, exist_ok=True)
            payload = archive.read(info)
            if len(payload) != info.file_size:
                raise ValueError("package entry changed while reading")
            with target.open("xb") as output:
                output.write(payload)
            os.chmod(target, 0o444)
            entries.append({
                "path": canonical_name,
                "sha256": sha256_bytes(payload),
                "size": len(payload),
            })
    return entries


def archive_identity(path: Path) -> dict[str, str]:
    with tarfile.open(path, "r") as archive:
        members = archive.getmembers()
        names = [member.name for member in members]
        if len(names) != len(set(names)):
            raise ValueError("OCI archive repeats a member")
        for member in members:
            if member.issym() or member.islnk() or member.name.startswith("/") or ".." in PurePosixPath(member.name).parts:
                raise ValueError("OCI archive contains an unsafe member")

        def read(name: str) -> bytes:
            try:
                member = archive.getmember(name)
            except KeyError as error:
                raise ValueError(f"OCI archive lacks {name}") from error
            if not member.isfile() or member.size > MAX_FILE_BYTES:
                raise ValueError(f"OCI archive member is not a bounded regular file: {name}")
            stream = archive.extractfile(member)
            if stream is None:
                raise ValueError(f"OCI archive member cannot be read: {name}")
            return stream.read()

        index = json.loads(read("index.json"))
        descriptors = index.get("manifests")
        if index.get("schemaVersion") != 2 or not isinstance(descriptors, list) or len(descriptors) != 1:
            raise ValueError("OCI index must select exactly one image manifest")
        descriptor = descriptors[0]
        digest = descriptor.get("digest", "")
        if not digest.startswith("sha256:"):
            raise ValueError("OCI image manifest digest is absent")
        manifest_digest = exact_sha(digest.removeprefix("sha256:"))
        manifest_bytes = read(f"blobs/sha256/{manifest_digest}")
        if sha256_bytes(manifest_bytes) != manifest_digest:
            raise ValueError("OCI image manifest digest changed")
        manifest = json.loads(manifest_bytes)
        config_descriptor = manifest.get("config", {})
        config_value = config_descriptor.get("digest", "")
        if manifest.get("schemaVersion") != 2 or not config_value.startswith("sha256:"):
            raise ValueError("OCI image config digest is absent")
        config_digest = exact_sha(config_value.removeprefix("sha256:"))
        config_bytes = read(f"blobs/sha256/{config_digest}")
        if sha256_bytes(config_bytes) != config_digest:
            raise ValueError("OCI image config digest changed")
        config = json.loads(config_bytes)
        if config.get("os") != "linux" or config.get("architecture") != "amd64":
            raise ValueError("OCI image platform changed")
        if config.get("config", {}).get("User") not in ("32768", "32768:32768"):
            raise ValueError("OCI image user changed")
        return {
            "manifestDigest": manifest_digest,
            "configDigest": config_digest,
            "qualifiedImage": f"{IMAGE_NAME}@sha256:{manifest_digest}",
        }


def prepare(args: argparse.Namespace) -> None:
    package = args.package.resolve()
    archive = args.image_archive.resolve()
    manifest_path = args.image_manifest.resolve()
    for path in (package, archive, manifest_path):
        if not path.is_file() or path.is_symlink():
            raise ValueError("candidate input must be a real regular file")
    package_sha = exact_sha(args.expected_package_sha256)
    archive_sha = exact_sha(args.expected_image_archive_sha256)
    manifest_sha = exact_sha(args.expected_image_manifest_sha256)
    if sha256_file(package) != package_sha:
        raise ValueError("package digest changed")
    if sha256_file(archive) != archive_sha:
        raise ValueError("image archive digest changed")
    if sha256_file(manifest_path) != manifest_sha:
        raise ValueError("image manifest receipt digest changed")
    package_id, package_version = package_identity(package, args.expected_version)
    package_entries = extract_package(package, args.install_root.resolve())
    tool_root = args.install_root.resolve() / TOOL_PREFIX
    required = [
        tool_root / CLI_DLL,
        tool_root / "FS.GG.Coordination.Cli.deps.json",
        tool_root / "FS.GG.Coordination.Cli.runtimeconfig.json",
    ]
    if not tool_root.is_dir() or any(not path.is_file() for path in required):
        raise ValueError("package lacks the complete installed CLI launcher closure")
    closure_sha, closure = payload_digest(tool_root)
    oci = archive_identity(archive)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    image = manifest.get("image", {})
    source = manifest.get("source", {})
    if manifest.get("schema") != "fsgg.portable-workspace-local-image/1":
        raise ValueError("image manifest receipt schema changed")
    if image.get("reference") != oci["qualifiedImage"]:
        raise ValueError("image manifest receipt reference differs from the OCI archive")
    if image.get("archiveConfigDigest") != f"sha256:{oci['configDigest']}":
        raise ValueError("image manifest receipt config differs from the OCI archive")
    if image.get("id") != f"sha256:{oci['configDigest']}":
        raise ValueError("image load identity differs from the OCI config")
    receipt = {
        "schema": SCHEMA,
        "authorizationState": "required-external",
        "grantWritten": False,
        "nativeExecutionAuthorized": False,
        "package": {
            "id": package_id,
            "version": package_version,
            "sha256": package_sha,
            "entries": package_entries,
        },
        "installedCli": {
            "root": str(tool_root),
            "entryAssembly": str(tool_root / CLI_DLL),
            "entryAssemblySha256": sha256_file(tool_root / CLI_DLL),
            "payloadSha256": closure_sha,
            "files": closure,
        },
        "producer": {
            "sourceRevision": exact_commit(args.producer_source),
            "sourceTree": exact_commit(args.producer_tree),
        },
        "image": {
            "archiveSha256": archive_sha,
            "manifestReceiptSha256": manifest_sha,
            "sourceRevision": source.get("revision"),
            "sourceTree": source.get("tree"),
            **oci,
        },
    }
    args.receipt.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
    if args.receipt.exists():
        raise ValueError("candidate receipt already exists")
    with args.receipt.open("xb") as output:
        os.fchmod(output.fileno(), 0o600)
        output.write(canonical(receipt))


def invoke(args: argparse.Namespace) -> int:
    receipt = json.loads(args.candidate_receipt.read_text(encoding="utf-8"))
    if set(receipt) != {
        "schema", "authorizationState", "grantWritten", "nativeExecutionAuthorized",
        "package", "installedCli", "producer", "image",
    } or receipt["schema"] != SCHEMA:
        raise ValueError("candidate receipt schema changed")
    if receipt["authorizationState"] != "required-external" or receipt["grantWritten"] is not False or receipt["nativeExecutionAuthorized"] is not False:
        raise ValueError("candidate facts falsely claim authorization")
    if args.grant_path != GRANT_PATH or not args.grant_path.is_file() or args.grant_path.is_symlink():
        raise ValueError("the fixed externally authorized grant is absent")
    tool_root = Path(receipt["installedCli"]["root"])
    closure_sha, closure = payload_digest(tool_root)
    if closure_sha != receipt["installedCli"]["payloadSha256"] or closure != receipt["installedCli"]["files"]:
        raise ValueError("installed CLI payload changed")
    entry = Path(receipt["installedCli"]["entryAssembly"])
    if entry != tool_root / CLI_DLL or sha256_file(entry) != receipt["installedCli"]["entryAssemblySha256"]:
        raise ValueError("installed CLI entry assembly changed")
    dotnet = args.dotnet.resolve()
    if not dotnet.is_file() or dotnet.is_symlink():
        raise ValueError("runtime host is not a real regular file")
    command = [
        str(dotnet), str(entry), "portable-workspace", args.mode,
        "--enrollment", "local-python-hello-v1",
        "--operation-id", "p4-python-hello-test-v1",
        "--profile", str(args.profile.resolve()),
        "--command", str(args.command.resolve()),
    ]
    environment = {
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "HOME": os.environ.get("HOME", "/nonexistent"),
            "LANG": "C.UTF-8",
            "PATH": "/usr/local/bin:/usr/bin:/bin",
        }
    child = subprocess.Popen(command, cwd=args.workspace.resolve(), env=environment,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
    captured = {"stdout": bytearray(), "stderr": bytearray()}
    exceeded = threading.Event()
    def read(name: str, stream: object) -> None:
        while True:
            chunk = stream.read(65536)  # type: ignore[attr-defined]
            if not chunk: break
            if len(captured[name]) + len(chunk) > MAX_OUTPUT_BYTES:
                exceeded.set()
                try: os.killpg(child.pid, signal.SIGKILL)
                except ProcessLookupError: pass
                break
            captured[name].extend(chunk)
    readers = [threading.Thread(target=read, args=(name, stream), daemon=True)
               for name, stream in (("stdout", child.stdout), ("stderr", child.stderr))]
    for reader in readers: reader.start()
    try:
        child.wait(timeout=args.timeout_seconds)
    except subprocess.TimeoutExpired as error:
        os.killpg(child.pid, signal.SIGTERM)
        try: child.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid, signal.SIGKILL); child.wait(timeout=5)
        raise ValueError("installed CLI did not terminate within its bound") from error
    if exceeded.is_set():
        os.killpg(child.pid, signal.SIGKILL) if child.poll() is None else None
        child.wait(timeout=5)
    for reader in readers: reader.join(timeout=5)
    if any(reader.is_alive() for reader in readers): raise ValueError("installed CLI output readers did not settle")
    stdout, stderr = bytes(captured["stdout"]), bytes(captured["stderr"])
    if exceeded.is_set(): raise ValueError("installed CLI output exceeds its bound")
    try: production = json.loads(stdout)
    except json.JSONDecodeError as error:
        refusal = stderr.decode("utf-8", errors="replace")[:512].replace("\n", " ")
        raise ValueError(f"installed CLI result is not JSON (exit={child.returncode}, refusal={refusal})") from error
    command_value = json.loads(args.command.read_text(encoding="utf-8"))
    profile_value = json.loads(args.profile.read_text(encoding="utf-8"))
    result_value = production.get("result", {})
    if (child.returncode != 0 or production.get("schema") != "fsgg.portable-workspace-runtime-result/1"
            or production.get("outcome") != args.expected_outcome
            or production.get("commandId") != command_value.get("commandId")
            or production.get("operation") != "test"
            or production.get("entryPoint") != "python-test"
            or production.get("workspaceScope") != "fs-gg/p4-python-receiver"
            or production.get("sourceRevision") != command_value.get("sourceRevision")
            or production.get("qualifiedImage") != receipt["image"]["qualifiedImage"]
            or profile_value.get("qualifiedImage") != receipt["image"]["qualifiedImage"]
            or production.get("cleanupCompleted") is not True
            or result_value.get("workflowRevision") != command_value.get("expectedWorkflowRevision")
            or result_value.get("fenceGeneration") != command_value.get("fenceGeneration")
            or result_value.get("exitCode") != {"state": "known", "value": 0}
            or result_value.get("error") is not None):
        raise ValueError("installed CLI production result did not prove the expected settled outcome")
    invocation = {
        "schema": INVOCATION_SCHEMA,
        "candidateReceiptSha256": sha256_file(args.candidate_receipt),
        "mode": args.mode,
        "exitCode": child.returncode,
        "outcome": production["outcome"],
        "commandId": production["commandId"],
        "executionStarted": production["executionStarted"],
        "cleanupCompleted": production["cleanupCompleted"],
        "operationOutputSha256": production["outputSha256"],
        "productionResultSha256": sha256_bytes(stdout),
        "standardErrorSha256": sha256_bytes(stderr),
        "authorizationConsumedFromFixedGrant": True,
        "grantWritten": False,
    }
    args.receipt.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
    with args.receipt.open("xb") as output:
        os.fchmod(output.fileno(), 0o600)
        output.write(canonical(invocation))
    return child.returncode


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser()
    commands = result.add_subparsers(dest="operation", required=True)
    prepare_command = commands.add_parser("prepare")
    prepare_command.add_argument("--package", required=True, type=Path)
    prepare_command.add_argument("--expected-package-sha256", required=True)
    prepare_command.add_argument("--expected-version", required=True)
    prepare_command.add_argument("--install-root", required=True, type=Path)
    prepare_command.add_argument("--producer-source", required=True)
    prepare_command.add_argument("--producer-tree", required=True)
    prepare_command.add_argument("--image-archive", required=True, type=Path)
    prepare_command.add_argument("--expected-image-archive-sha256", required=True)
    prepare_command.add_argument("--image-manifest", required=True, type=Path)
    prepare_command.add_argument("--expected-image-manifest-sha256", required=True)
    prepare_command.add_argument("--receipt", required=True, type=Path)
    invoke_command = commands.add_parser("invoke")
    invoke_command.add_argument("--candidate-receipt", required=True, type=Path)
    invoke_command.add_argument("--dotnet", required=True, type=Path)
    invoke_command.add_argument("--workspace", required=True, type=Path)
    invoke_command.add_argument("--profile", required=True, type=Path)
    invoke_command.add_argument("--command", required=True, type=Path)
    invoke_command.add_argument("--mode", choices=("execute", "recover"), required=True)
    invoke_command.add_argument("--expected-outcome", choices=("completed", "duplicate"), required=True)
    invoke_command.add_argument("--grant-path", type=Path, default=GRANT_PATH)
    invoke_command.add_argument("--timeout-seconds", type=int, default=180)
    invoke_command.add_argument("--receipt", required=True, type=Path)
    return result


def main() -> int:
    args = parser().parse_args()
    try:
        if args.operation == "prepare":
            prepare(args)
            return 0
        return invoke(args)
    except (OSError, ValueError, KeyError, json.JSONDecodeError, zipfile.BadZipFile, tarfile.TarError, ET.ParseError, subprocess.TimeoutExpired) as error:
        print(f"INSTALLED_PYTHON_HELLO_REFUSED {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
