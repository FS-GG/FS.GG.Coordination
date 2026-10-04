#!/usr/bin/env python3
"""Assemble the closed public P4 input from verified producer artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import zipfile
import xml.etree.ElementTree as ET

SHA = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
VERSION = "0.2.1"
IMAGE_NAME = "localhost/fsgg-portable-workspace:python-3.14.0-node-24.8.0-ts-5.9.2"
IMAGE_DIGEST = "sha256:40085dd0a7c3c16af6b24e247cec47707bc957d6453f7e15d82636fcbf6f0755"
IMAGE_CONFIG_DIGEST = "sha256:371d2b5db7c9708812ca8c3d752376e38aa81432a8bcbe7d99146414636dd872"
IMAGE_REFERENCE = f"{IMAGE_NAME}@{IMAGE_DIGEST}"
RUNTIME_SHA256 = "8458f4cef855fcebd139d9853e47fb0a5d86ab65d4aa101ea158a11e036c0fa4"
RUNTIME_SHA512 = "58388fdde4f13bd703c7a6f7defb3300b17e42ba5fe8bca50066f80f64ac7406620dcdfb4acc1eff7992750c8cc5cff8369dd12d09730c8a9e8760d6032f7f6e"
RUNTIME_URL = "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz"
# Current receiver identities are fixed; exact genuine Actions custody is a root-bound
# required input. Missing run/archive digests cannot silently select historical bytes.
SDD = {"repository": "FS-GG/FS.GG.SDD", "packageId": "FS.GG.SDD.Cli", "version": "2.1.0", "package": "FS.GG.SDD.Cli.2.1.0.nupkg"}
TEMPLATES = {"repository": "FS-GG/FS.GG.Templates", "packageId": "FS.GG.Workspace.Template", "version": "0.18.0", "package": "FS.GG.Workspace.Template.0.18.0.nupkg"}
DESCRIPTOR = {"repository": "FS-GG/FS.GG.Templates", "revision": "96b9d01475935ea3f474cfd4ed4dd93b4755f726", "tree": "5cf467fd0aa1b078738fcb31bf1128f8aa06be3c", "path": "providers/python.providers.yml", "sha256": "f817b5c42cce22dae53ddc16cec075ed1387d1dfc7226eeaae85b3db2cb475f0"}

def receiver_custody(value):
    if not isinstance(value, dict) or set(value) != {"schema", "sdd", "templates"} or value["schema"] != "fsgg.portable-current-receiver-custody/1":
        raise ValueError("required current receiver custody schema")
    records = []
    fields = {"runId", "runAttempt", "workflow", "headSha", "artifactId", "artifactName", "artifactDigest", "packageSha256", "event"}
    for role, identity in (("sdd", SDD), ("templates", TEMPLATES)):
        identity = {key: identity[key] for key in ("repository", "packageId", "version", "package")}
        row = value[role]
        if not isinstance(row, dict) or set(row) != set(identity) | fields or any(row[k] != v for k, v in identity.items()):
            raise ValueError("exact current receiver identity required")
        if any(type(row[k]) is not int or row[k] <= 0 for k in ("runId", "runAttempt", "artifactId")):
            raise ValueError("actual receiver run/artifact identities required")
        if (not isinstance(row["headSha"], str) or not COMMIT.fullmatch(row["headSha"])
                or not isinstance(row["packageSha256"], str) or not SHA.fullmatch(row["packageSha256"])
                or not isinstance(row["artifactDigest"], str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", row["artifactDigest"])
                or not isinstance(row["workflow"], str) or not re.fullmatch(r"\.github/workflows/[A-Za-z0-9._-]+\.ya?ml", row["workflow"])
                or row["event"] not in ("workflow_dispatch", "pull_request", "push")
                or not isinstance(row["artifactName"], str) or not re.fullmatch(r"[A-Za-z0-9._-]{1,160}", row["artifactName"])):
            raise ValueError("closed genuine receiver custody required")
        records.append(dict(row))
    return tuple(records)


def parse_receiver_custody(raw):
    if not isinstance(raw, str) or not 0 < len(raw.encode()) <= 8192:
        raise ValueError("required bounded current receiver custody")
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value: raise ValueError("duplicate receiver custody field")
            value[key] = item
        return value
    value = json.loads(raw, object_pairs_hook=unique)
    receiver_custody(value)
    return value


def receiver_custody_file(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 8192:
        raise ValueError("regular bounded receiver custody required")
    return receiver_custody(parse_receiver_custody(path.read_text(encoding="utf-8")))



def canonical(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(path: Path, algorithm: str = "sha256") -> str:
    result = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def load(path: Path) -> dict[str, object]:
    if path.is_symlink() or path.stat().st_size > 2 * 1024 * 1024:
        raise ValueError("JSON input custody refused")
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("JSON object required")
    return value


def validate_custody(run: dict[str, object], artifact: dict[str, object], expected: dict[str, object]) -> dict[str, object]:
    repository = run.get("repository", {})
    workflow_run = artifact.get("workflow_run", {})
    if (run.get("id") != expected["runId"] or run.get("run_attempt") != expected["runAttempt"]
            or run.get("head_sha") != expected["headSha"] or run.get("path") != expected["workflow"]
            or run.get("event") != expected["event"] or run.get("conclusion") != "success"
            or not isinstance(repository, dict) or repository.get("full_name") != expected["repository"]):
        raise ValueError("producer run custody differs")
    if (artifact.get("id") != expected["artifactId"] or artifact.get("name") != expected["artifactName"]
            or artifact.get("digest") != expected["artifactDigest"] or artifact.get("expired") is not False
            or not isinstance(workflow_run, dict) or workflow_run.get("id") != expected["runId"]
            or workflow_run.get("head_sha") != expected["headSha"]):
        raise ValueError("producer artifact custody differs")
    return {key: expected[key] for key in ("repository", "runId", "runAttempt", "workflow", "headSha", "artifactId", "artifactDigest")}


def validate_archive(path: Path, expected_digest: object) -> None:
    metadata = path.lstat()
    if (not stat.S_ISREG(metadata.st_mode) or metadata.st_size <= 0
            or metadata.st_size > 640 * 1024 * 1024
            or not isinstance(expected_digest, str)
            or not re.fullmatch(r"sha256:[0-9a-f]{64}", expected_digest)
            or "sha256:" + digest(path) != expected_digest):
        raise ValueError("producer archive digest differs")


def safe_extract(archive: Path, target: Path, *, total_limit: int = 640 * 1024 * 1024) -> None:
    metadata = archive.lstat()
    if (target.exists() or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_size > 640 * 1024 * 1024):
        raise ValueError("artifact target or archive size refused")
    target.mkdir(mode=0o700)
    try:
        total = 0
        with zipfile.ZipFile(archive) as source:
            infos = source.infolist()
            if not infos or len(infos) > 1024:
                raise ValueError("artifact entry count refused")
            seen: set[str] = set()
            for info in infos:
                raw = info.filename.rstrip("/")
                path = PurePosixPath(raw)
                folded = raw.casefold()
                mode = info.external_attr >> 16
                if (not raw or "\\" in raw or path.is_absolute() or ".." in path.parts or "." in path.parts
                        or folded in seen or stat.S_ISLNK(mode) or info.flag_bits & 1):
                    raise ValueError("artifact entry is unsafe or repeated")
                seen.add(folded)
                destination = target.joinpath(*path.parts)
                if info.is_dir():
                    destination.mkdir(parents=True, exist_ok=True)
                    continue
                total += info.file_size
                if info.file_size > 300 * 1024 * 1024 or total > total_limit:
                    raise ValueError("artifact payload exceeds bounds")
                destination.parent.mkdir(parents=True, exist_ok=True)
                with destination.open("xb") as output:
                    os.fchmod(output.fileno(), 0o600)
                    with source.open(info) as payload:
                        shutil.copyfileobj(payload, output, 1024 * 1024)
    except Exception:
        shutil.rmtree(target)
        raise


def unique(root: Path, name: str) -> Path:
    matches = [path for path in root.rglob(name) if path.is_file() and not path.is_symlink()]
    if len(matches) != 1:
        raise ValueError(f"expected one artifact member: {name}")
    return matches[0]


def package_identity(path: Path, package_id: str, version: str) -> None:
    with zipfile.ZipFile(path) as package:
        infos = package.infolist(); total = 0; seen: set[str] = set()
        if not infos or len(infos) > 4096: raise ValueError("package entry count refused")
        for item in infos:
            raw = item.filename.rstrip("/"); member = PurePosixPath(raw); mode = item.external_attr >> 16
            if (not raw or "\\" in raw or member.is_absolute() or ".." in member.parts or "." in member.parts
                    or raw.casefold() in seen or stat.S_ISLNK(mode) or item.flag_bits & 1):
                raise ValueError("package entry is unsafe or repeated")
            seen.add(raw.casefold()); total += item.file_size
            if item.file_size > 16 * 1024 * 1024 or total > 128 * 1024 * 1024: raise ValueError("package payload exceeds bounds")
        nuspecs = [item for item in infos if PurePosixPath(item.filename).suffix == ".nuspec"]
        if len(nuspecs) != 1 or nuspecs[0].file_size > 1024 * 1024:
            raise ValueError("package identity document is ambiguous")
        metadata = {node.tag.rsplit("}", 1)[-1]: (node.text or "") for node in ET.fromstring(package.read(nuspecs[0])).iter()}
    if metadata.get("id") != package_id or metadata.get("version") != version:
        raise ValueError("package identity differs")


def copy_member(source: Path, target: Path, expected: str | None = None) -> str:
    actual = digest(source)
    if expected is not None and actual != expected:
        raise ValueError("artifact member digest differs")
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as output, source.open("rb") as payload:
        os.fchmod(output.fileno(), 0o600)
        shutil.copyfileobj(payload, output, 1024 * 1024)
    return actual


def preparation(expected: dict[str, object], run: dict[str, object], artifact: dict[str, object], root: Path) -> tuple[dict[str, object], dict[str, Path]]:
    custody = validate_custody(run, artifact, expected)
    source = expected["headSha"]
    tree = expected["headTree"]
    if not isinstance(source, str) or not COMMIT.fullmatch(source) or not isinstance(tree, str) or not COMMIT.fullmatch(tree):
        raise ValueError("candidate source identity malformed")
    package_name = f"FS.GG.Coordination.Cli.{VERSION}.nupkg"
    image_name = f"portable-workspace-linux-amd64-{VERSION}.oci.tar"
    members = {
        "package": unique(root, package_name), "callable": unique(root, "callable-cli-release-manifest.json"),
        "image": unique(root, image_name), "imageManifest": unique(root, "portable-workspace-image-manifest.json"),
        "release": unique(root, "portable-workspace-release-manifest.json"),
        "packaged": unique(root, "portable-workspace-packaged-qualification.json"),
        "cleanup": unique(root, "cleanup-state.json"),
    }
    package_sha = digest(members["package"]); image_sha = digest(members["image"]); image_manifest_sha = digest(members["imageManifest"])
    callable_manifest = load(members["callable"]); release = load(members["release"]); image_manifest = load(members["imageManifest"]); packaged = load(members["packaged"]); cleanup = load(members["cleanup"])
    if (callable_manifest.get("schema") != "fsgg.coordination.callable-cli-release-preparation/1"
            or callable_manifest.get("version") != VERSION or callable_manifest.get("sourceCommit") != source
            or callable_manifest.get("sourceTree") != tree or callable_manifest.get("packageSha256") != package_sha
            or callable_manifest.get("publicationAuthorized") is not False or callable_manifest.get("tagAuthorized") is not False):
        raise ValueError("callable package provenance differs")
    if (release.get("schema") != "fsgg.portable-workspace-release/1" or release.get("version") != VERSION
            or release.get("sourceCommit") != source or release.get("sourceTree") != tree
            or release.get("packageName") != package_name or release.get("packageSha256") != package_sha
            or release.get("imageArchiveName") != image_name or release.get("imageArchiveSha256") != image_sha
            or release.get("imageManifestSha256") != image_manifest_sha or release.get("imageDigest") != IMAGE_DIGEST
            or release.get("imageReference") != IMAGE_REFERENCE
            or any(release.get(key) is not False for key in ("publicationAuthorized", "tagAuthorized", "activationAuthorized"))):
        raise ValueError("coherent release provenance differs")
    image_source = image_manifest.get("source", {}); image = image_manifest.get("image", {})
    if (image_manifest.get("schema") != "fsgg.portable-workspace-local-image/1"
            or not isinstance(image_source, dict) or image_source.get("revision") != source or image_source.get("tree") != tree
            or not isinstance(image, dict) or image.get("name") != IMAGE_NAME or image.get("digest") != IMAGE_DIGEST
            or image.get("archiveConfigDigest") != IMAGE_CONFIG_DIGEST or image.get("reference") != IMAGE_REFERENCE):
        raise ValueError("image source or compiled digest differs")
    if (packaged.get("schema") != "fsgg.portable-workspace-packaged-qualification/1"
            or packaged.get("packageSha256") != package_sha or packaged.get("qualificationExitCode") != 0
            or packaged.get("publicationAuthorized") is not False or packaged.get("activationAuthorized") is not False):
        raise ValueError("packaged qualification differs")
    cleanup_values = [value for key, value in cleanup.items() if key != "schema"]
    if cleanup.get("schema") != "fsgg.portable-workspace-release-cleanup/1" or not cleanup_values or any(value is not True for value in cleanup_values):
        raise ValueError("preparation cleanup differs")
    package_identity(members["package"], "FS.GG.Coordination.Cli", VERSION)
    custody["headTree"] = tree
    return custody, members


def assemble(args: argparse.Namespace) -> None:
    SDD, TEMPLATES = receiver_custody_file(args.receiver_custody)
    if (args.output.exists() or args.runtime.is_symlink() or args.runtime.stat().st_size > 128 * 1024 * 1024
            or digest(args.runtime) != RUNTIME_SHA256 or digest(args.runtime, "sha512") != RUNTIME_SHA512):
        raise ValueError("output or official runtime custody differs")
    descriptor_commit = load(args.descriptor_commit)
    if (descriptor_commit.get("sha") != DESCRIPTOR["revision"]
            or not isinstance(descriptor_commit.get("tree"), dict)
            or descriptor_commit["tree"].get("sha") != DESCRIPTOR["tree"]
            or args.descriptor.is_symlink() or args.descriptor.stat().st_size > 64 * 1024
            or digest(args.descriptor) != DESCRIPTOR["sha256"]):
        raise ValueError("protected descriptor digest differs")
    p_expected = {
        "repository": "FS-GG/FS.GG.Coordination", "runId": args.preparation_run_id,
        "runAttempt": args.preparation_run_attempt, "workflow": ".github/workflows/callable-cli-release-prepare.yml",
        "headSha": args.preparation_source, "headTree": args.preparation_tree,
        "artifactId": args.preparation_artifact_id, "artifactName": f"callable-cli-{args.preparation_source}",
        "artifactDigest": args.preparation_artifact_digest, "event": "workflow_dispatch",
    }
    roots: list[Path] = []
    try:
        p_run, p_artifact = load(args.preparation_run), load(args.preparation_artifact)
        sdd_run, sdd_artifact = load(args.sdd_run), load(args.sdd_artifact)
        templates_run, templates_artifact = load(args.templates_run), load(args.templates_artifact)
        validate_custody(p_run, p_artifact, p_expected)
        validate_custody(sdd_run, sdd_artifact, SDD)
        validate_custody(templates_run, templates_artifact, TEMPLATES)
        validate_archive(args.preparation_archive, p_artifact.get("digest"))
        validate_archive(args.sdd_archive, sdd_artifact.get("digest"))
        validate_archive(args.templates_archive, templates_artifact.get("digest"))
        for archive in (args.preparation_archive, args.sdd_archive, args.templates_archive):
            root = args.output.parent / f".{args.output.name}-{len(roots)}"
            safe_extract(archive, root); roots.append(root)
        p_custody, p_members = preparation(p_expected, p_run, p_artifact, roots[0])
        sdd_custody = validate_custody(sdd_run, sdd_artifact, SDD)
        templates_custody = validate_custody(templates_run, templates_artifact, TEMPLATES)
        sdd_package = unique(roots[1], SDD["package"]); templates_package = unique(roots[2], TEMPLATES["package"])
        if digest(sdd_package) != SDD["packageSha256"] or digest(templates_package) != TEMPLATES["packageSha256"]:
            raise ValueError("receiver package digest differs")
        package_identity(sdd_package, SDD["packageId"], SDD["version"]); package_identity(templates_package, TEMPLATES["packageId"], TEMPLATES["version"])
        descriptor = args.descriptor.read_text(encoding="utf-8")
        marker = "source: FS.GG.Workspace.Template::0.18.0"
        if (descriptor.count(marker) != 1 or "::<pin>" in descriptor
                or '    contractVersion: "2.0.0"' not in descriptor
                or not re.search(r'(?m)^    minimumFsggSdd:\n      version: "2\.1\.0"$', descriptor)):
            raise ValueError("protected descriptor transform refused")
        transformed = descriptor.replace(marker, "source: FS.GG.Workspace.Template::<pin>").encode()
        args.output.mkdir(mode=0o700)
        paths = {
            "package": args.output / "coordination" / p_members["package"].name,
            "sdd": args.output / "sdd" / SDD["package"], "templates": args.output / "templates" / TEMPLATES["package"],
            "descriptor": args.output / "templates/python.providers.yml", "runtime": args.output / "runtime" / args.runtime.name,
            "image": args.output / "image" / p_members["image"].name,
            "imageManifest": args.output / "image/portable-workspace-image-manifest.json",
        }
        package_sha = copy_member(p_members["package"], paths["package"])
        sdd_sha = copy_member(sdd_package, paths["sdd"], SDD["packageSha256"])
        templates_sha = copy_member(templates_package, paths["templates"], TEMPLATES["packageSha256"])
        paths["descriptor"].parent.mkdir(parents=True, exist_ok=True); paths["descriptor"].write_bytes(transformed); paths["descriptor"].chmod(0o600)
        runtime_sha = copy_member(args.runtime, paths["runtime"], RUNTIME_SHA256)
        image_sha = copy_member(p_members["image"], paths["image"])
        image_manifest_sha = copy_member(p_members["imageManifest"], paths["imageManifest"])
        join = {
            "schema": "fsgg.portable-workspace-python-provider-input/1",
            "coordination": {"package": str(paths["package"].relative_to(args.output)), "sha256": package_sha, "version": VERSION, "sourceRevision": args.preparation_source, "sourceTree": args.preparation_tree},
            "sdd": {"package": str(paths["sdd"].relative_to(args.output)), "sha256": sdd_sha, "version": SDD["version"]},
            "templates": {"package": str(paths["templates"].relative_to(args.output)), "sha256": templates_sha, "version": TEMPLATES["version"], "descriptor": str(paths["descriptor"].relative_to(args.output)), "descriptorSha256": hashlib.sha256(transformed).hexdigest()},
            "runtime": {"archive": str(paths["runtime"].relative_to(args.output)), "sha256": runtime_sha, "version": "10.0.12"},
            "image": {"archive": str(paths["image"].relative_to(args.output)), "archiveSha256": image_sha, "manifest": str(paths["imageManifest"].relative_to(args.output)), "manifestSha256": image_manifest_sha},
        }
        provenance = {
            "schema": "fsgg.portable-python-provider-staging-provenance/1", "authorizationState": "none",
            "nativeExecutionAuthorized": False, "staging": {"sourceRevision": args.staging_source, "sourceTree": args.staging_tree, "workflow": ".github/workflows/portable-workspace-python-provider-qualification.yml"},
            "preparation": p_custody, "sdd": sdd_custody, "templates": templates_custody,
            "descriptor": {**DESCRIPTOR, "transformedSha256": join["templates"]["descriptorSha256"]},
            "runtime": {"url": RUNTIME_URL, "version": "10.0.12", "sha256": RUNTIME_SHA256, "sha512": RUNTIME_SHA512},
        }
        (args.output / "provider-input.json").write_bytes(canonical(join)); (args.output / "provider-input.json").chmod(0o600)
        (args.output / "staging-provenance.json").write_bytes(canonical(provenance)); (args.output / "staging-provenance.json").chmod(0o600)
    except Exception:
        if args.output.exists(): shutil.rmtree(args.output)
        raise
    finally:
        for root in roots:
            if root.exists(): shutil.rmtree(root)


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(); result.add_argument("--preparation-archive", type=Path, required=True)
    for name in ("receiver-custody", "preparation-run", "preparation-artifact", "sdd-archive", "sdd-run", "sdd-artifact", "templates-archive", "templates-run", "templates-artifact", "runtime", "descriptor", "descriptor-commit", "output"):
        result.add_argument(f"--{name}", type=Path, required=True)
    result.add_argument("--preparation-run-id", type=int, required=True); result.add_argument("--preparation-run-attempt", type=int, required=True)
    result.add_argument("--preparation-artifact-id", type=int, required=True); result.add_argument("--preparation-artifact-digest", required=True)
    result.add_argument("--preparation-source", required=True); result.add_argument("--preparation-tree", required=True)
    result.add_argument("--staging-source", required=True); result.add_argument("--staging-tree", required=True)
    return result


def main() -> int:
    try:
        args = parser().parse_args()
        for value in (args.preparation_source, args.preparation_tree, args.staging_source, args.staging_tree):
            if not COMMIT.fullmatch(value): raise ValueError("source identity malformed")
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", args.preparation_artifact_digest): raise ValueError("artifact digest malformed")
        assemble(args); return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError, zipfile.BadZipFile, ET.ParseError) as error:
        print(f"PORTABLE_PROVIDER_STAGING_REFUSED {error}", file=__import__("sys").stderr); return 2


if __name__ == "__main__": raise SystemExit(main())
