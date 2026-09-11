#!/usr/bin/env python3
"""Prepare and verify the immutable Linux-x64 orchestration-runner-client candidate."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit
import zipfile


SCHEMA = "fsgg.coordination.orchestration-runner-client-candidate/1"
PREPARED_SCHEMA = "fsgg.coordination.orchestration-runner-client-prepared/1"
VERIFICATION_SCHEMA = "fsgg.coordination.orchestration-runner-client-verification/1"
PROVENANCE_TYPE = "https://slsa.dev/provenance/v1"
REPOSITORY = "FS-GG/FS.GG.Coordination"
PROJECT = "src/FS.GG.Coordination.Orchestration.Runner.Client/FS.GG.Coordination.Orchestration.Runner.Client.fsproj"
LOCK = "src/FS.GG.Coordination.Orchestration.Runner.Client/packages.lock.json"
WORKFLOW = ".github/workflows/orchestration-runner-client-candidate.yml"
SDK = "10.0.400"
RUNTIME = "10.0.11"
FSC_SHA256 = "3e82a7fb4fb386f645b538dd56b73e02eb77d12f453915c5ae029421343d5d18"
RID = "linux-x64"
PAYLOAD = "fsgg-coord-orchestration-runner"
ROOT = "fsgg-coord-orchestration-runner-linux-x64"
ARCHIVE_PREFIX = "fsgg-coord-orchestration-runner-linux-x64-"
LIMIT = 100 * 1024 * 1024
FIXED_TIME = (2000, 1, 1, 0, 0, 0)
SHA = re.compile(r"^[0-9a-f]{40}$")


def refuse(code: str, detail: str) -> "NoReturn":
    raise SystemExit(f"{code} {detail}")


def require(value: bool, code: str, detail: str) -> None:
    if not value:
        refuse(code, detail)


def digest_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def digest_file(path: Path) -> str:
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_bytes(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode() + b"\n"


def write_json(path: Path, value: object) -> None:
    path.write_bytes(canonical_bytes(value))


def run(cwd: Path, arguments: list[str], env: dict[str, str] | None = None, expected: int = 0) -> str:
    process = subprocess.run(arguments, cwd=cwd, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    require(process.returncode == expected, "ORC-COMMAND", f"exit={process.returncode} command={arguments!r}\n{process.stdout}")
    return process.stdout.strip()


def exact_candidate(repo: Path, candidate: str, protected_ref: str) -> tuple[str, str]:
    require(bool(SHA.fullmatch(candidate)), "ORC-CANDIDATE", "expected lowercase 40-character SHA")
    actual = run(repo, ["git", "rev-parse", candidate])
    require(actual == candidate, "ORC-CANDIDATE", "revision did not resolve byte-identically")
    run(repo, ["git", "merge-base", "--is-ancestor", candidate, protected_ref])
    tree = run(repo, ["git", "rev-parse", candidate + "^{tree}"])
    require(bool(SHA.fullmatch(tree)), "ORC-TREE", "source tree is invalid")
    commit_time = run(repo, ["git", "show", "-s", "--format=%cI", candidate])
    return tree, commit_time


def toolchain(repo: Path) -> dict[str, str]:
    sdk = run(repo, ["dotnet", "--version"])
    require(sdk == SDK, "ORC-SDK", f"expected {SDK}, observed {sdk}")
    runtimes = run(repo, ["dotnet", "--list-runtimes"])
    require(f"Microsoft.NETCore.App {RUNTIME} " in runtimes, "ORC-RUNTIME", f"runtime {RUNTIME} is absent")
    rows = [line for line in run(repo, ["dotnet", "--list-sdks"]).splitlines() if line.startswith(SDK + " [")]
    require(len(rows) == 1 and rows[0].endswith("]"), "ORC-SDK", "pinned SDK location is ambiguous")
    sdk_root = Path(rows[0][len(SDK) + 2 : -1]) / SDK
    compiler = sdk_root / "FSharp" / "fsc.dll"
    require(compiler.is_file(), "ORC-FSC", "pinned F# compiler is absent")
    observed = digest_file(compiler)
    require(observed == FSC_SHA256, "ORC-FSC", "pinned F# compiler digest differs")
    return {"dotnetSdk": sdk, "dotnetRuntime": RUNTIME, "fsharpCompilerSha256": observed, "python": sys.version.split()[0]}


def project_contract(source: Path) -> str:
    project = (source / PROJECT).read_text()
    for exact in (
        "<IsPackable>false</IsPackable>",
        "<RuntimeIdentifier>linux-x64</RuntimeIdentifier>",
        "<SelfContained>true</SelfContained>",
        "<PublishSingleFile>true</PublishSingleFile>",
    ):
        require(project.count(exact) == 1, "ORC-PROJECT", f"missing or ambiguous {exact}")
    lock = source / LOCK
    lock_value = json.loads(lock.read_text())
    require("net10.0/linux-x64" in lock_value.get("dependencies", {}), "ORC-LOCK", "runner client lock omits net10.0/linux-x64")
    return digest_file(lock)


def safe_project(repo: Path, candidate: str, source: Path) -> str:
    archive = source.parent / "tracked.zip"
    run(repo, ["git", "archive", "--format=zip", "--output", str(archive), candidate])
    source_archive_sha = digest_file(archive)
    with zipfile.ZipFile(archive) as zipped:
        entries = zipped.infolist()
        require(bool(entries), "ORC-SOURCE", "tracked projection is empty")
        for entry in entries:
            path = Path(entry.filename)
            require(not path.is_absolute() and ".." not in path.parts, "ORC-SOURCE", f"unsafe path {entry.filename}")
        zipped.extractall(source)
    archive.unlink()
    require((source / PROJECT).is_file(), "ORC-SOURCE", "tracked projection omits runner client project")
    return source_archive_sha


def zip_info(name: str, mode: int) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, FIXED_TIME)
    info.create_system = 3
    info.external_attr = mode << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    return info


def make_archive(path: Path, binary: Path, manifest: bytes) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        zipped.writestr(zip_info(f"{ROOT}/{PAYLOAD}", stat.S_IFREG | 0o500), binary.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        zipped.writestr(zip_info(f"{ROOT}/manifest.json", stat.S_IFREG | 0o400), manifest, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def validate_archive(path: Path, expected_manifest: bytes | None = None) -> tuple[dict, bytes]:
    require(path.is_file() and path.stat().st_size <= LIMIT, "ORC-ARCHIVE-SIZE", "archive is absent or exceeds 100 MiB")
    with zipfile.ZipFile(path) as zipped:
        infos = zipped.infolist()
        names = [info.filename for info in infos]
        expected = [f"{ROOT}/{PAYLOAD}", f"{ROOT}/manifest.json"]
        require(names == expected, "ORC-LAYOUT", f"archive layout differs: {names!r}")
        require(all(info.date_time == FIXED_TIME for info in infos), "ORC-TIMESTAMP", "archive timestamp differs")
        modes = [(info.external_attr >> 16) & 0xFFFF for info in infos]
        require(modes == [stat.S_IFREG | 0o500, stat.S_IFREG | 0o400], "ORC-MODE", f"archive modes differ: {modes!r}")
        binary = zipped.read(expected[0])
        manifest_bytes = zipped.read(expected[1])
    require(len(binary) <= LIMIT, "ORC-PAYLOAD-SIZE", "payload exceeds 100 MiB")
    require(binary[:4] == b"\x7fELF" and binary[4:6] == b"\x02\x01" and binary[18:20] == b"\x3e\x00", "ORC-ELF", "payload is not Linux x86-64 ELF")
    manifest = json.loads(manifest_bytes)
    require(canonical_bytes(manifest) == manifest_bytes, "ORC-MANIFEST", "manifest is not canonical")
    require(manifest.get("schema") == SCHEMA, "ORC-MANIFEST", "manifest schema differs")
    require(manifest.get("rid") == RID and manifest.get("selfContained") is True and manifest.get("singleFile") is True, "ORC-MANIFEST", "runtime binding differs")
    payload = manifest.get("payload", {})
    require(payload.get("path") == expected[0] and payload.get("bytes") == len(binary) and payload.get("sha256") == digest_bytes(binary), "ORC-PAYLOAD", "payload binding differs")
    if expected_manifest is not None:
        require(manifest_bytes == expected_manifest, "ORC-MANIFEST", "embedded manifest differs")
    return manifest, binary


def usage_readback(binary: bytes) -> str:
    with tempfile.TemporaryDirectory(prefix="o2-runner-client-usage-") as temporary:
        path = Path(temporary) / PAYLOAD
        path.write_bytes(binary)
        path.chmod(0o500)
        process = subprocess.run([str(path)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        require(process.returncode == 2, "ORC-USAGE", f"expected usage exit 2, observed {process.returncode}")
        require(process.stdout.startswith("usage: fsgg-coord-orchestration-runner post"), "ORC-USAGE", "native usage readback differs")
        for binding in ("https://orchestration.main.internal:18080/", "--client-cert-file", "--client-key-file", "--ca-file"):
            require(binding in process.stdout, "ORC-USAGE", f"native usage omits bridge binding {binding}")
        require("--token-file" not in process.stdout, "ORC-USAGE", "runner usage exposes forbidden bearer-token input")
        oversized = Path(temporary) / "oversized-request.json"
        oversized.write_bytes(b"x" * 8193)
        refused = subprocess.run(
            [str(path), "post", "--endpoint", "https://orchestration.main.internal:18080/",
             "--client-cert-file", str(Path(temporary) / "missing-cert"),
             "--client-key-file", str(Path(temporary) / "missing-key"),
             "--ca-file", str(Path(temporary) / "missing-ca"),
             "--path", "/v1/runner/assignment", "--request-file", str(oversized)],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        )
        require(refused.returncode == 2 and refused.stdout.strip() == "runner-request-size-refused", "ORC-USAGE", "oversized request was not refused before credential or network access")
        return digest_bytes(process.stdout.encode())


def build_once(repo: Path, candidate: str, tree: str, commit_time: str, identity: dict[str, str], root: Path) -> tuple[Path, bytes, dict]:
    require(not root.exists(), "ORC-BUILD-ROOT", f"fixed build root already exists: {root}")
    source = root / "source"
    output = root / "output"
    source.mkdir(parents=True)
    source_archive_sha = safe_project(repo, candidate, source)
    lock_sha = project_contract(source)
    env = os.environ.copy()
    env.update({"DOTNET_CLI_HOME": str(root / "dotnet-home"), "NUGET_PACKAGES": str(root / "nuget"), "DOTNET_NOLOGO": "1", "DOTNET_ROLL_FORWARD": "Disable"})
    properties = [
        "-p:ContinuousIntegrationBuild=true", "-p:Deterministic=true", "-p:DeterministicSourcePaths=true",
        "-p:UseSharedCompilation=false", "-p:DebugType=None", "-p:DebugSymbols=false", f"-p:PathMap={source}=/_/",
    ]
    run(source, ["dotnet", "restore", PROJECT, "--locked-mode", "--disable-build-servers", *properties], env)
    output.mkdir()
    run(source, ["dotnet", "publish", PROJECT, "--configuration", "Release", "--no-restore", "--disable-build-servers", "--output", str(output), *properties], env)
    files = sorted(path.name for path in output.iterdir())
    require(files == [PAYLOAD], "ORC-SINGLE-FILE", f"publish output differs: {files!r}")
    binary = output / PAYLOAD
    payload_sha = digest_file(binary)
    manifest = {
        "schema": SCHEMA, "repository": REPOSITORY, "sourceRevision": candidate, "sourceTree": tree,
        "sourceArchiveSha256": source_archive_sha, "commitTime": commit_time, "rid": RID,
        "selfContained": True, "singleFile": True, "configuration": "Release", "publishInvocationsPerBuild": 1,
        "reproductionBuilds": 2, "debugType": "None", "debugSymbols": False, "sharedCompilation": False,
        "lock": {"path": LOCK, "sha256": lock_sha}, "toolchain": identity,
        "payload": {"path": f"{ROOT}/{PAYLOAD}", "bytes": binary.stat().st_size, "sha256": payload_sha},
    }
    manifest_bytes = canonical_bytes(manifest)
    archive = root / f"{ARCHIVE_PREFIX}{candidate}.zip"
    make_archive(archive, binary, manifest_bytes)
    validated, archived_binary = validate_archive(archive, manifest_bytes)
    require(validated == manifest and archived_binary == binary.read_bytes(), "ORC-ARCHIVE", "archive readback differs")
    usage_sha = usage_readback(archived_binary)
    return archive, manifest_bytes, {"manifest": manifest, "usageSha256": usage_sha}


def prepare(args: argparse.Namespace) -> None:
    repo = Path(args.repo).resolve()
    output = Path(args.output).resolve()
    require(not output.exists() or not any(output.iterdir()), "ORC-OUTPUT", "output must be absent or empty")
    output.mkdir(parents=True, exist_ok=True)
    tree, commit_time = exact_candidate(repo, args.candidate, args.protected_ref)
    identity = toolchain(repo)
    fixed = Path(tempfile.gettempdir()) / f"fsgg-orchestration-runner-client-candidate-{args.candidate}"
    products: list[tuple[bytes, bytes, dict]] = []
    try:
        for _ in range(2):
            archive, manifest, metadata = build_once(repo, args.candidate, tree, commit_time, identity, fixed)
            products.append((archive.read_bytes(), manifest, metadata))
            shutil.rmtree(fixed)
    finally:
        if fixed.exists():
            shutil.rmtree(fixed)
    require(products[0][0] == products[1][0], "ORC-REPRODUCIBILITY", "double-build archives differ")
    require(products[0][1] == products[1][1], "ORC-REPRODUCIBILITY", "double-build manifests differ")
    require(products[0][2] == products[1][2], "ORC-REPRODUCIBILITY", "double-build readback differs")
    archive_name = f"{ARCHIVE_PREFIX}{args.candidate}.zip"
    archive_path = output / archive_name
    archive_path.write_bytes(products[0][0])
    manifest_path = output / "manifest.json"
    manifest_path.write_bytes(products[0][1])
    manifest = products[0][2]["manifest"]
    provenance = {
        "_type": "https://in-toto.io/Statement/v1", "subject": [{"name": archive_name, "digest": {"sha256": digest_file(archive_path)}}],
        "predicateType": PROVENANCE_TYPE,
        "predicate": {"buildDefinition": {"buildType": f"https://github.com/{REPOSITORY}/orchestration-runner-client-candidate/v1", "externalParameters": {"candidate": args.candidate, "sourceTree": tree, "rid": RID}, "resolvedDependencies": [{"uri": LOCK, "digest": {"sha256": manifest["lock"]["sha256"]}}]}, "runDetails": {"builder": {"id": f"https://github.com/{REPOSITORY}/{WORKFLOW}"}}},
    }
    write_json(output / "provenance.intoto.json", provenance)
    prepared = {
        "schema": PREPARED_SCHEMA, "candidate": args.candidate, "sourceTree": tree,
        "archive": {"file": archive_name, "bytes": archive_path.stat().st_size, "sha256": digest_file(archive_path)},
        "manifestSha256": digest_file(manifest_path), "provenanceSha256": digest_file(output / "provenance.intoto.json"),
        "payload": manifest["payload"], "usageSha256": products[0][2]["usageSha256"],
        "stages": ["protected-main-bound", "tracked-source-projected", "locked-rid-restored", "published-twice", "archives-byte-identical", "prepared-verified"],
    }
    write_json(output / "prepared.json", prepared)
    verify_prepared(output / "prepared.json")
    print(f"ORCHESTRATION_RUNNER_CLIENT_PREPARED candidate={args.candidate} archiveSha256={prepared['archive']['sha256']} bytes={prepared['archive']['bytes']}")


def verify_prepared(path: Path) -> dict:
    receipt = json.loads(path.read_text())
    require(receipt.get("schema") == PREPARED_SCHEMA, "ORC-PREPARED", "prepared schema differs")
    candidate = receipt.get("candidate", "")
    require(bool(SHA.fullmatch(candidate)), "ORC-PREPARED", "prepared candidate differs")
    root = path.parent
    archive = receipt.get("archive", {})
    archive_path = root / archive.get("file", "")
    require(archive_path.name == f"{ARCHIVE_PREFIX}{candidate}.zip", "ORC-PREPARED", "archive name differs")
    require(archive_path.stat().st_size == archive.get("bytes") and digest_file(archive_path) == archive.get("sha256"), "ORC-PREPARED", "archive bytes differ")
    manifest_path = root / "manifest.json"
    require(digest_file(manifest_path) == receipt.get("manifestSha256"), "ORC-PREPARED", "manifest digest differs")
    manifest, binary = validate_archive(archive_path, manifest_path.read_bytes())
    require(manifest.get("sourceRevision") == candidate and manifest.get("sourceTree") == receipt.get("sourceTree"), "ORC-PREPARED", "source binding differs")
    require(manifest.get("payload") == receipt.get("payload"), "ORC-PREPARED", "payload binding differs")
    require(usage_readback(binary) == receipt.get("usageSha256"), "ORC-PREPARED", "usage binding differs")
    provenance_path = root / "provenance.intoto.json"
    require(digest_file(provenance_path) == receipt.get("provenanceSha256"), "ORC-PREPARED", "provenance digest differs")
    provenance = json.loads(provenance_path.read_text())
    require(canonical_bytes(provenance) == provenance_path.read_bytes(), "ORC-PROVENANCE", "provenance is not canonical")
    require(provenance.get("_type") == "https://in-toto.io/Statement/v1" and provenance.get("predicateType") == PROVENANCE_TYPE, "ORC-PROVENANCE", "provenance type differs")
    expected_subject = [{"name": archive["file"], "digest": {"sha256": archive["sha256"]}}]
    require(provenance.get("subject") == expected_subject, "ORC-PROVENANCE", "provenance subject differs")
    definition = provenance.get("predicate", {}).get("buildDefinition", {})
    require(definition.get("externalParameters") == {"candidate": candidate, "sourceTree": receipt["sourceTree"], "rid": RID}, "ORC-PROVENANCE", "provenance source binding differs")
    dependencies = definition.get("resolvedDependencies", [])
    require(dependencies == [{"uri": LOCK, "digest": {"sha256": manifest["lock"]["sha256"]}}], "ORC-PROVENANCE", "provenance lock binding differs")
    require(receipt.get("stages") == ["protected-main-bound", "tracked-source-projected", "locked-rid-restored", "published-twice", "archives-byte-identical", "prepared-verified"], "ORC-PREPARED", "prepared stages differ")
    return receipt


def verify_served(args: argparse.Namespace) -> None:
    prepared_path = Path(args.prepared).resolve()
    receipt = verify_prepared(prepared_path)
    served = Path(args.served).resolve()
    archive = receipt["archive"]
    require(served.name == archive["file"], "ORC-SERVED", "served filename differs")
    require(served.stat().st_size == archive["bytes"] and digest_file(served) == archive["sha256"], "ORC-SERVED", "served archive bytes differ")
    manifest, binary = validate_archive(served, (prepared_path.parent / "manifest.json").read_bytes())
    usage_sha = usage_readback(binary)
    require(usage_sha == receipt["usageSha256"], "ORC-SERVED", "served usage differs")
    artifact = validate_artifact_identity(receipt["candidate"], args.artifact_id, args.artifact_name, args.artifact_url, args.artifact_digest)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    verification = {
        "schema": VERIFICATION_SCHEMA, "candidate": receipt["candidate"], "sourceTree": receipt["sourceTree"],
        "artifact": artifact, "archive": archive, "payload": manifest["payload"], "usageSha256": usage_sha,
        "stages": ["uploaded-once", "downloaded-fresh", "archive-byte-identical", "payload-byte-identical", "native-usage-readback"],
    }
    write_json(output / "verification.json", verification)
    print(f"ORCHESTRATION_RUNNER_CLIENT_SERVED candidate={receipt['candidate']} artifactId={artifact['id']} archiveSha256={archive['sha256']}")


def validate_artifact_identity(candidate: str, artifact_id_value: str, artifact_name: str, artifact_url: str, artifact_digest: str) -> dict:
    artifact_id = str(artifact_id_value)
    require(artifact_id.isdigit() and int(artifact_id) > 0, "ORC-ARTIFACT", "artifact id is invalid")
    expected_name = f"orchestration-runner-client-linux-x64-{candidate}"
    require(artifact_name == expected_name, "ORC-ARTIFACT", "artifact name differs")
    require(bool(re.fullmatch(r"[0-9a-f]{64}", artifact_digest)), "ORC-ARTIFACT", "outer artifact digest is invalid")
    parsed_url = urlsplit(artifact_url)
    parts = parsed_url.path.split("/")
    route_ok = len(parts) == 8 and parts[1:5] == ["FS-GG", "FS.GG.Coordination", "actions", "runs"] and parts[5].isdigit() and int(parts[5]) > 0 and parts[6:] == ["artifacts", artifact_id]
    require(parsed_url.scheme == "https" and parsed_url.netloc == "github.com" and route_ok and not parsed_url.query and not parsed_url.fragment, "ORC-ARTIFACT", "artifact URL differs")
    return {"id": int(artifact_id), "name": artifact_name, "url": artifact_url, "digest": artifact_digest, "retentionDays": 90}


def validate_workflow(repo: Path) -> None:
    text = (repo / WORKFLOW).read_text().replace("\r\n", "\n")
    require(text.count("  workflow_dispatch:\n") == 1, "ORC-WORKFLOW", "manual trigger differs")
    for trigger in ("push", "pull_request", "schedule", "repository_dispatch"):
        require(not re.search(rf"^  {trigger}:\s*$", text, re.MULTILINE), "ORC-WORKFLOW", f"forbidden trigger {trigger}")
    for exact in (
        "permissions:\n  contents: read", "dotnet-version: 10.0.400",
        "if: github.ref == 'refs/heads/main'",
        "ref: ${{ github.sha }}",
        "EXPECTED_SHA: ${{ inputs.expected_sha }}",
        'test "$GITHUB_REF" = "refs/heads/main"',
        'test "$EXPECTED_SHA" = "$GITHUB_SHA"',
        'test "$(git rev-parse HEAD)" = "$GITHUB_SHA"',
        "git fetch --no-tags origin +refs/heads/main:refs/remotes/origin/main",
        'git merge-base --is-ancestor "$GITHUB_SHA" refs/remotes/origin/main',
        '--candidate "${{ github.sha }}"',
        "--protected-ref refs/remotes/origin/main",
        "name: orchestration-runner-client-verification-${{ github.sha }}",
    ):
        require(text.count(exact) == 1, "ORC-WORKFLOW", f"missing or ambiguous workflow binding: {exact}")
    require(text.count("name: orchestration-runner-client-linux-x64-${{ github.sha }}") == 2, "ORC-WORKFLOW", "candidate upload/download identity differs")
    require(text.count("${{ inputs.expected_sha }}") == 1, "ORC-WORKFLOW", "dispatch input escapes inert comparison")
    require(text.count("${{ github.sha }}") == 9, "ORC-WORKFLOW", "trusted revision use differs")
    require(text.count("retention-days: 90") == 2, "ORC-WORKFLOW", "retention binding differs")
    require(text.count("uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a") == 2, "ORC-WORKFLOW", "upload action count differs")
    require(text.count("uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c") == 1, "ORC-WORKFLOW", "download action count differs")
    for forbidden in ("gh release", "git tag", "nuget push", "packages: write", "contents: write", "continue-on-error"):
        require(forbidden not in text, "ORC-WORKFLOW", f"forbidden workflow capability: {forbidden}")


def self_test(repo: Path) -> None:
    validate_workflow(repo)
    with tempfile.TemporaryDirectory(prefix="o2-runner-client-candidate-test-") as temporary:
        root = Path(temporary)
        binary = root / PAYLOAD
        # Minimal structural ELF64/x86-64 fixture; native execution is covered by real prepare/readback.
        value = bytearray(64)
        value[:6] = b"\x7fELF\x02\x01"
        value[18:20] = b"\x3e\x00"
        binary.write_bytes(value)
        manifest = {"schema": SCHEMA, "rid": RID, "selfContained": True, "singleFile": True, "payload": {"path": f"{ROOT}/{PAYLOAD}", "bytes": len(value), "sha256": digest_bytes(value)}}
        manifest_bytes = canonical_bytes(manifest)
        first, second = root / "first.zip", root / "second.zip"
        make_archive(first, binary, manifest_bytes)
        make_archive(second, binary, manifest_bytes)
        require(first.read_bytes() == second.read_bytes(), "ORC-SELF-TEST", "canonical ZIP is not repeatable")
        validate_archive(first, manifest_bytes)
        mutations = 0
        for kind in ("payload", "manifest", "extra", "timestamp", "mode"):
            target = root / f"{kind}.zip"
            entries = [(f"{ROOT}/{PAYLOAD}", bytes(value), stat.S_IFREG | 0o500, FIXED_TIME), (f"{ROOT}/manifest.json", manifest_bytes, stat.S_IFREG | 0o400, FIXED_TIME)]
            if kind == "payload": entries[0] = (entries[0][0], bytes(value) + b"x", entries[0][2], entries[0][3])
            if kind == "manifest": entries[1] = (entries[1][0], manifest_bytes.replace(b"linux-x64", b"linux-arm"), entries[1][2], entries[1][3])
            if kind == "extra": entries.append(("extra", b"x", stat.S_IFREG | 0o400, FIXED_TIME))
            if kind == "timestamp": entries[0] = (entries[0][0], entries[0][1], entries[0][2], (2001, 1, 1, 0, 0, 0))
            if kind == "mode": entries[0] = (entries[0][0], entries[0][1], stat.S_IFREG | 0o777, entries[0][3])
            with zipfile.ZipFile(target, "w") as zipped:
                for name, data, mode, timestamp in entries:
                    info = zip_info(name, mode); info.date_time = timestamp
                    zipped.writestr(info, data)
            try:
                validate_archive(target, manifest_bytes)
            except SystemExit:
                mutations += 1
        oversized = root / "oversized.zip"
        with oversized.open("wb") as stream:
            stream.seek(LIMIT)
            stream.write(b"x")
        try:
            validate_archive(oversized)
        except SystemExit:
            mutations += 1
        require(mutations == 6, "ORC-SELF-TEST", f"archive mutation refusals differ: {mutations}")
        candidate = "a" * 40
        artifact_name = f"orchestration-runner-client-linux-x64-{candidate}"
        artifact_url = "https://github.com/FS-GG/FS.GG.Coordination/actions/runs/1/artifacts/12345"
        artifact_digest = "b" * 64
        validate_artifact_identity(candidate, "12345", artifact_name, artifact_url, artifact_digest)
        artifact_mutations = [
            ("0", artifact_name, artifact_url, artifact_digest),
            ("12345", artifact_name + "x", artifact_url, artifact_digest),
            ("12345", artifact_name, artifact_url + "?download=1", artifact_digest),
            ("12345", artifact_name, artifact_url.replace("FS-GG", "other", 1), artifact_digest),
            ("12345", artifact_name, artifact_url, "sha256:" + artifact_digest),
        ]
        for artifact_id, name, url, digest in artifact_mutations:
            try:
                validate_artifact_identity(candidate, artifact_id, name, url, digest)
            except SystemExit:
                mutations += 1
        require(mutations == 11, "ORC-SELF-TEST", f"all mutation refusals differ: {mutations}")
    print("ORCHESTRATION_RUNNER_CLIENT_CANDIDATE_SELF_TEST_OK controls=workflow,layout,timestamp,mode,rid,limit,reproducibility,artifact-route mutations=11")


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    prepare_parser = sub.add_parser("prepare")
    prepare_parser.add_argument("--repo", required=True)
    prepare_parser.add_argument("--candidate", required=True)
    prepare_parser.add_argument("--protected-ref", required=True)
    prepare_parser.add_argument("--output", required=True)
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--prepared", required=True)
    served_parser = sub.add_parser("verify-served")
    served_parser.add_argument("--prepared", required=True)
    served_parser.add_argument("--served", required=True)
    served_parser.add_argument("--artifact-id", required=True)
    served_parser.add_argument("--artifact-name", required=True)
    served_parser.add_argument("--artifact-url", required=True)
    served_parser.add_argument("--artifact-digest", required=True)
    served_parser.add_argument("--output", required=True)
    self_parser = sub.add_parser("self-test")
    self_parser.add_argument("--repo", required=True)
    args = parser.parse_args()
    if args.command == "prepare": prepare(args)
    elif args.command == "verify": verify_prepared(Path(args.prepared).resolve()); print("ORCHESTRATION_RUNNER_CLIENT_PREPARED_OK")
    elif args.command == "verify-served": verify_served(args)
    else: self_test(Path(args.repo).resolve())


if __name__ == "__main__":
    main()
