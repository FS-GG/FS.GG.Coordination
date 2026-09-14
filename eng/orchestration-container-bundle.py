#!/usr/bin/env python3
"""Assemble and verify the immutable Host/runner orchestration application bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import tempfile
import zipfile


SCHEMA = "fsgg.coordination.orchestration-container-bundle/1"
HOST_PREPARED_SCHEMA = "fsgg.coordination.orchestration-host-prepared/1"
RUNNER_PREPARED_SCHEMA = "fsgg.coordination.orchestration-runner-client-prepared/1"
ROOT = "fsgg-coord-orchestration-linux-x64"
HOST_NAME = "fsgg-coord-orchestration-host"
RUNNER_NAME = "fsgg-coord-orchestration-runner"
HOST_PATH = f"{ROOT}/host/{HOST_NAME}"
RUNNER_PATH = f"{ROOT}/runner/{RUNNER_NAME}"
MANIFEST_PATH = f"{ROOT}/manifest.json"
FIXED_TIME = (2000, 1, 1, 0, 0, 0)
LIMIT = 200 * 1024 * 1024
MANIFEST_KEYS = {"schema", "repository", "sourceRevision", "sourceTree", "rid", "processModel", "inputs", "payloads"}
INPUT_KEYS = {"archive", "manifestSha256", "preparedSha256"}
ARCHIVE_KEYS = {"file", "bytes", "sha256"}


def require(value: bool, code: str, detail: str) -> None:
    if not value:
        raise SystemExit(f"{code} {detail}")


def canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode() + b"\n"


def digest_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def digest_file(path: Path) -> str:
    return digest_bytes(path.read_bytes())


def sha256(value: object) -> bool:
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def info(name: str, mode: int) -> zipfile.ZipInfo:
    value = zipfile.ZipInfo(name, FIXED_TIME)
    value.create_system = 3
    value.external_attr = mode << 16
    value.compress_type = zipfile.ZIP_DEFLATED
    return value


def read_component(directory: Path, expected_schema: str, role: str) -> tuple[dict, bytes, dict]:
    prepared_path = directory / "prepared.json"
    manifest_path = directory / "manifest.json"
    require(prepared_path.is_file() and manifest_path.is_file(), "OCB-INPUT", f"{role} receipts are absent")
    prepared_bytes = prepared_path.read_bytes()
    manifest_bytes = manifest_path.read_bytes()
    prepared = json.loads(prepared_bytes)
    manifest = json.loads(manifest_bytes)
    require(canonical(prepared) == prepared_bytes and canonical(manifest) == manifest_bytes, "OCB-INPUT", f"{role} receipts are not canonical")
    require(prepared.get("schema") == expected_schema, "OCB-INPUT", f"{role} prepared schema differs")
    archive = prepared.get("archive", {})
    archive_path = directory / archive.get("file", "")
    require(archive_path.parent == directory and archive_path.is_file(), "OCB-INPUT", f"{role} archive is absent")
    require(archive_path.stat().st_size == archive.get("bytes") and digest_file(archive_path) == archive.get("sha256"), "OCB-INPUT", f"{role} archive binding differs")
    require(digest_bytes(manifest_bytes) == prepared.get("manifestSha256"), "OCB-INPUT", f"{role} manifest binding differs")
    with zipfile.ZipFile(archive_path) as zipped:
        names = zipped.namelist()
        payload = manifest.get("payload", {})
        require(len(names) == 2 and names[0] == payload.get("path") and names[1].endswith("/manifest.json"), "OCB-INPUT", f"{role} archive layout differs")
        binary = zipped.read(names[0])
        embedded = zipped.read(names[1])
    require(embedded == manifest_bytes and prepared.get("payload") == payload, "OCB-INPUT", f"{role} embedded manifest differs")
    require(payload.get("bytes") == len(binary) and payload.get("sha256") == digest_bytes(binary), "OCB-INPUT", f"{role} payload binding differs")
    require(binary[:4] == b"\x7fELF" and binary[4:6] == b"\x02\x01" and binary[18:20] == b"\x3e\x00", "OCB-INPUT", f"{role} payload is not Linux x86-64 ELF")
    binding = {
        "archive": {"file": archive_path.name, "bytes": archive_path.stat().st_size, "sha256": digest_file(archive_path)},
        "manifestSha256": digest_bytes(manifest_bytes),
        "preparedSha256": digest_bytes(prepared_bytes),
    }
    return manifest, binary, binding


def write_bundle(path: Path, host: bytes, runner: bytes, manifest_bytes: bytes) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        zipped.writestr(info(HOST_PATH, stat.S_IFREG | 0o500), host, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        zipped.writestr(info(RUNNER_PATH, stat.S_IFREG | 0o500), runner, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        zipped.writestr(info(MANIFEST_PATH, stat.S_IFREG | 0o400), manifest_bytes, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def validate(path: Path, expected_manifest: bytes | None = None) -> dict:
    require(path.is_file() and path.stat().st_size <= LIMIT, "OCB-ARCHIVE", "bundle is absent or exceeds 200 MiB")
    with zipfile.ZipFile(path) as zipped:
        entries = zipped.infolist()
        require([entry.filename for entry in entries] == [HOST_PATH, RUNNER_PATH, MANIFEST_PATH], "OCB-LAYOUT", "bundle layout differs")
        require(all(entry.date_time == FIXED_TIME for entry in entries), "OCB-TIMESTAMP", "bundle timestamps differ")
        modes = [(entry.external_attr >> 16) & 0xFFFF for entry in entries]
        require(modes == [stat.S_IFREG | 0o500, stat.S_IFREG | 0o500, stat.S_IFREG | 0o400], "OCB-MODE", "bundle modes differ")
        host, runner, manifest_bytes = (zipped.read(entry.filename) for entry in entries)
    manifest = json.loads(manifest_bytes)
    require(canonical(manifest) == manifest_bytes and manifest.get("schema") == SCHEMA, "OCB-MANIFEST", "bundle manifest differs")
    require(set(manifest) == MANIFEST_KEYS, "OCB-MANIFEST", "bundle manifest shape differs")
    require(manifest["repository"] == "FS-GG/FS.GG.Coordination", "OCB-MANIFEST", "repository binding differs")
    require(manifest["rid"] == "linux-x64", "OCB-MANIFEST", "runtime binding differs")
    require(manifest["processModel"] == "host-supervises-local-runner-child", "OCB-MANIFEST", "process model differs")
    require(re.fullmatch(r"[0-9a-f]{40}", manifest["sourceRevision"]) is not None and re.fullmatch(r"[0-9a-f]{40}", manifest["sourceTree"]) is not None, "OCB-MANIFEST", "source identity differs")
    inputs = manifest.get("inputs")
    require(isinstance(inputs, dict) and set(inputs) == {"host", "runner"}, "OCB-INPUT", "component bindings differ")
    for role, prefix in (("host", "fsgg-coord-orchestration-host-linux-x64-"), ("runner", "fsgg-coord-orchestration-runner-linux-x64-")):
        binding = inputs[role]
        require(isinstance(binding, dict) and set(binding) == INPUT_KEYS, "OCB-INPUT", f"{role} component shape differs")
        archive = binding.get("archive")
        require(isinstance(archive, dict) and set(archive) == ARCHIVE_KEYS, "OCB-INPUT", f"{role} archive shape differs")
        require(archive["file"] == f"{prefix}{manifest['sourceRevision']}.zip" and isinstance(archive["bytes"], int) and archive["bytes"] > 0 and sha256(archive["sha256"]), "OCB-INPUT", f"{role} archive binding differs")
        require(sha256(binding["manifestSha256"]) and sha256(binding["preparedSha256"]), "OCB-INPUT", f"{role} receipt binding differs")
    if expected_manifest is not None:
        require(manifest_bytes == expected_manifest, "OCB-MANIFEST", "bundle manifest bytes differ")
    for role, binary, expected_path in (("host", host, HOST_PATH), ("runner", runner, RUNNER_PATH)):
        payload = manifest.get("payloads", {}).get(role, {})
        require(payload == {"bytes": len(binary), "path": expected_path, "sha256": digest_bytes(binary)}, "OCB-PAYLOAD", f"{role} payload binding differs")
    return manifest


def assemble(host_directory: Path, runner_directory: Path, output: Path) -> Path:
    host_manifest, host, host_binding = read_component(host_directory.resolve(), HOST_PREPARED_SCHEMA, "host")
    runner_manifest, runner, runner_binding = read_component(runner_directory.resolve(), RUNNER_PREPARED_SCHEMA, "runner")
    candidate = host_manifest.get("sourceRevision")
    tree = host_manifest.get("sourceTree")
    require(isinstance(candidate, str) and re.fullmatch(r"[0-9a-f]{40}", candidate) is not None, "OCB-SOURCE", "source revision differs")
    require(isinstance(tree, str) and re.fullmatch(r"[0-9a-f]{40}", tree) is not None, "OCB-SOURCE", "source tree differs")
    require(candidate == runner_manifest.get("sourceRevision") and tree == runner_manifest.get("sourceTree"), "OCB-SOURCE", "Host and runner source bindings differ")
    manifest = {
        "schema": SCHEMA,
        "repository": "FS-GG/FS.GG.Coordination",
        "sourceRevision": candidate,
        "sourceTree": tree,
        "rid": "linux-x64",
        "processModel": "host-supervises-local-runner-child",
        "inputs": {"host": host_binding, "runner": runner_binding},
        "payloads": {
            "host": {"path": HOST_PATH, "bytes": len(host), "sha256": digest_bytes(host)},
            "runner": {"path": RUNNER_PATH, "bytes": len(runner), "sha256": digest_bytes(runner)},
        },
    }
    manifest_bytes = canonical(manifest)
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"fsgg-coord-orchestration-linux-x64-{candidate}.zip"
    write_bundle(archive, host, runner, manifest_bytes)
    validate(archive, manifest_bytes)
    (output / "manifest.json").write_bytes(manifest_bytes)
    receipt = {"schema": "fsgg.coordination.orchestration-container-bundle-prepared/1", "archive": {"file": archive.name, "bytes": archive.stat().st_size, "sha256": digest_file(archive)}, "manifestSha256": digest_bytes(manifest_bytes), "sourceRevision": candidate, "sourceTree": tree}
    (output / "prepared.json").write_bytes(canonical(receipt))
    return archive


def verify(prepared_path: Path, archive_override: Path | None = None) -> None:
    receipt_bytes = prepared_path.read_bytes()
    receipt = json.loads(receipt_bytes)
    require(canonical(receipt) == receipt_bytes and set(receipt) == {"schema", "archive", "manifestSha256", "sourceRevision", "sourceTree"} and receipt.get("schema") == "fsgg.coordination.orchestration-container-bundle-prepared/1", "OCB-PREPARED", "prepared receipt differs")
    archive = archive_override or prepared_path.parent / receipt["archive"]["file"]
    binding = receipt["archive"]
    require(archive.name == binding["file"] and archive.stat().st_size == binding["bytes"] and digest_file(archive) == binding["sha256"], "OCB-PREPARED", "bundle bytes differ")
    manifest_path = prepared_path.parent / "manifest.json"
    require(digest_file(manifest_path) == receipt["manifestSha256"], "OCB-PREPARED", "manifest digest differs")
    manifest = validate(archive, manifest_path.read_bytes())
    require(manifest["sourceRevision"] == receipt["sourceRevision"] and manifest["sourceTree"] == receipt["sourceTree"], "OCB-PREPARED", "source binding differs")


def fixture(directory: Path, role: str, schema: str, payload_name: str, source: str, tree: str) -> None:
    directory.mkdir()
    binary = bytearray(64)
    binary[:6] = b"\x7fELF\x02\x01"
    binary[18:20] = b"\x3e\x00"
    binary.extend(role.encode())
    root = f"fixture-{role}"
    manifest = {"schema": f"fixture/{role}", "sourceRevision": source, "sourceTree": tree, "payload": {"path": f"{root}/{payload_name}", "bytes": len(binary), "sha256": digest_bytes(binary)}}
    manifest_bytes = canonical(manifest)
    archive = directory / f"fsgg-coord-orchestration-{role}-linux-x64-{source}.zip"
    with zipfile.ZipFile(archive, "w") as zipped:
        zipped.writestr(f"{root}/{payload_name}", binary)
        zipped.writestr(f"{root}/manifest.json", manifest_bytes)
    prepared = {"schema": schema, "candidate": source, "sourceTree": tree, "archive": {"file": archive.name, "bytes": archive.stat().st_size, "sha256": digest_file(archive)}, "manifestSha256": digest_bytes(manifest_bytes), "payload": manifest["payload"]}
    (directory / "manifest.json").write_bytes(manifest_bytes)
    (directory / "prepared.json").write_bytes(canonical(prepared))


def self_test() -> None:
    source, tree = "a" * 40, "b" * 40
    with tempfile.TemporaryDirectory(prefix="orchestration-container-bundle-") as temporary:
        root = Path(temporary)
        fixture(root / "host", "host", HOST_PREPARED_SCHEMA, HOST_NAME, source, tree)
        fixture(root / "runner", "runner", RUNNER_PREPARED_SCHEMA, RUNNER_NAME, source, tree)
        first = assemble(root / "host", root / "runner", root / "first")
        second = assemble(root / "host", root / "runner", root / "second")
        require(first.read_bytes() == second.read_bytes(), "OCB-SELFTEST", "repeated assembly differs")
        verify(root / "first/prepared.json")
        with zipfile.ZipFile(first) as zipped:
            original = {entry.filename: zipped.read(entry.filename) for entry in zipped.infolist()}
            modes = {entry.filename: (entry.external_attr >> 16) & 0xFFFF for entry in zipped.infolist()}
        base_manifest = json.loads(original[MANIFEST_PATH])
        mutations = {
            "top-level": lambda value: value.update({"unexpected": True}),
            "repository": lambda value: value.update({"repository": "FS-GG/other"}),
            "rid": lambda value: value.update({"rid": "linux-arm64"}),
            "process-model": lambda value: value.update({"processModel": "relay"}),
            "input-roles": lambda value: value["inputs"].pop("runner"),
            "input-binding": lambda value: value["inputs"]["host"]["archive"].update({"file": "host.zip"}),
        }
        for name, mutate in mutations.items():
            changed = json.loads(json.dumps(base_manifest))
            mutate(changed)
            target = root / f"mutated-{name}.zip"
            with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
                for path in (HOST_PATH, RUNNER_PATH, MANIFEST_PATH):
                    payload = canonical(changed) if path == MANIFEST_PATH else original[path]
                    zipped.writestr(info(path, modes[path]), payload, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
            try:
                validate(target)
            except SystemExit:
                pass
            else:
                require(False, "OCB-SELFTEST", f"{name} manifest mutation was accepted")
        damaged = json.loads((root / "first/prepared.json").read_text())
        damaged["archive"]["sha256"] = "0" * 64
        (root / "first/prepared.json").write_bytes(canonical(damaged))
        try:
            verify(root / "first/prepared.json")
        except SystemExit:
            pass
        else:
            require(False, "OCB-SELFTEST", "changed archive binding was accepted")
    print("ORCHESTRATION_CONTAINER_BUNDLE_SELF_TEST_OK")


def main() -> None:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("assemble")
    build.add_argument("--host", required=True)
    build.add_argument("--runner", required=True)
    build.add_argument("--output", required=True)
    check = commands.add_parser("verify")
    check.add_argument("--prepared", required=True)
    check.add_argument("--archive")
    commands.add_parser("self-test")
    args = parser.parse_args()
    if args.command == "assemble":
        archive = assemble(Path(args.host), Path(args.runner), Path(args.output))
        print(f"ORCHESTRATION_CONTAINER_BUNDLE_PREPARED archive={archive.name} sha256={digest_file(archive)}")
    elif args.command == "verify":
        verify(Path(args.prepared), Path(args.archive) if args.archive else None)
        print("ORCHESTRATION_CONTAINER_BUNDLE_PREPARED_OK")
    else:
        self_test()


if __name__ == "__main__":
    main()
