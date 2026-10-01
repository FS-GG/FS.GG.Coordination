#!/usr/bin/env python3
"""Validate closed provider joins, runtime archives, descriptors, and authorization."""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import tarfile


SHA = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def contained(root: Path, value: str) -> Path:
    relative = PurePosixPath(value)
    if relative.is_absolute() or ".." in relative.parts or "." in relative.parts:
        raise ValueError("provider input path escapes its artifact")
    path = root.joinpath(*relative.parts)
    if not path.is_file() or path.is_symlink():
        raise ValueError("provider input is not a real regular file")
    return path


def validate_join(root: Path, output: Path) -> None:
    joins = list(root.rglob("provider-input.json"))
    if len(joins) != 1:
        raise ValueError("expected one provider input join")
    value = json.loads(joins[0].read_text(encoding="utf-8"))
    if set(value) != {"schema", "coordination", "sdd", "templates", "runtime", "image"}:
        raise ValueError("provider input top-level fields changed")
    if value["schema"] != "fsgg.portable-workspace-python-provider-input/1":
        raise ValueError("provider input schema changed")
    shapes = {
        "coordination": {"package", "sha256", "version", "sourceRevision", "sourceTree"},
        "sdd": {"package", "sha256", "version"},
        "templates": {"package", "sha256", "version", "descriptor", "descriptorSha256"},
        "runtime": {"archive", "sha256", "version"},
        "image": {"archive", "archiveSha256", "manifest", "manifestSha256"},
    }
    for section, fields in shapes.items():
        item = value[section]
        if set(item) != fields:
            raise ValueError(f"{section} join fields changed")
        for name, field in item.items():
            if name.lower().endswith("sha256") and not SHA.fullmatch(field):
                raise ValueError(f"{section} contains a malformed digest")
            if name in ("sourceRevision", "sourceTree") and not COMMIT.fullmatch(field):
                raise ValueError("producer identity is malformed")
        pairs = {
            "package": "sha256",
            "descriptor": "descriptorSha256",
            "archive": "archiveSha256" if section == "image" else "sha256",
            "manifest": "manifestSha256",
        }
        for name, digest_name in pairs.items():
            if name in item:
                path = contained(root, item[name])
                if sha256(path) != item[digest_name]:
                    raise ValueError(f"{section} artifact digest changed")
    output.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def extract_runtime(archive_path: Path, target: Path) -> None:
    if target.exists() and (not target.is_dir() or target.is_symlink() or any(target.iterdir())):
        raise ValueError("runtime target must be empty")
    target.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive_path) as archive:
        members = archive.getmembers()
        if not members or len(members) > 4096:
            raise ValueError("runtime archive entry count is outside bounds")
        total = 0
        for member in members:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or member.issym() or member.islnk():
                raise ValueError("runtime archive contains an unsafe entry")
            if not (member.isdir() or member.isfile()):
                raise ValueError("runtime archive contains a special entry")
            total += member.size
            if member.size > 64 * 1024 * 1024 or total > 256 * 1024 * 1024:
                raise ValueError("runtime archive exceeds its bound")
        archive.extractall(target, filter="data")


def bind_descriptor(path: Path, package: Path) -> None:
    text = path.read_text(encoding="utf-8")
    marker = "source: FS.GG.Workspace.Template::<pin>"
    if text.count(marker) != 1:
        raise ValueError("provider descriptor candidate marker changed")
    path.write_text(text.replace(marker, f"source: {package.resolve()}"), encoding="utf-8")


def validate_authorization(facts_root: Path, authorization_root: Path, facts_output: Path, grant_output: Path) -> None:
    facts = list(facts_root.rglob("provider-facts.json"))
    grants = list(authorization_root.rglob("python-hello-v1.json"))
    authorities = list(authorization_root.rglob("authorization.json"))
    if len(facts) != 1 or len(grants) != 1 or len(authorities) != 1:
        raise ValueError("authorization artifact is incomplete or ambiguous")
    authority = json.loads(authorities[0].read_text(encoding="utf-8"))
    if set(authority) != {
        "schema", "providerFactsSha256", "grantSha256",
        "authorizedBy", "authorizedAt", "nativeExecutionAuthorized",
    }:
        raise ValueError("authorization record fields changed")
    if authority["schema"] != "fsgg.portable-workspace-python-provider-authorization/1":
        raise ValueError("authorization schema changed")
    if authority["nativeExecutionAuthorized"] is not True:
        raise ValueError("native execution is not authorized")
    if not isinstance(authority["authorizedBy"], str) or not authority["authorizedBy"].strip():
        raise ValueError("authorization actor is absent")
    if not isinstance(authority["authorizedAt"], str):
        raise ValueError("authorization time is absent")
    try:
        observed = datetime.strptime(authority["authorizedAt"], "%Y-%m-%dT%H:%M:%S.%fZ")
    except ValueError as error:
        raise ValueError("authorization time is malformed") from error
    if observed.year < 2026:
        raise ValueError("authorization time is outside the provider horizon")
    if not SHA.fullmatch(authority["providerFactsSha256"]) or not SHA.fullmatch(authority["grantSha256"]):
        raise ValueError("authorization digest is malformed")
    if sha256(facts[0]) != authority["providerFactsSha256"] or sha256(grants[0]) != authority["grantSha256"]:
        raise ValueError("authorization does not bind the supplied facts and grant")
    facts_value = json.loads(facts[0].read_text(encoding="utf-8"))
    if facts_value["authorizationState"] != "required-external" or facts_value["nativeExecutionAuthorized"] is not False:
        raise ValueError("provider facts contain an authorization claim")
    grant = json.loads(grants[0].read_text(encoding="utf-8"))
    if set(grant) != {
        "schema", "enrollmentId", "grantId", "allowedUid", "cli", "provider", "receiver",
        "profileSha256", "workspaceScope", "workflowRevision", "fenceGeneration", "observedAt",
        "stateRoot", "executables", "image",
    } or grant["schema"] != "fsgg.portable-workspace-python-enrollment/v1":
        raise ValueError("grant schema changed")
    if set(grant["cli"]) != {"version", "packageSha256", "payloadSha256"}:
        raise ValueError("grant CLI shape changed")
    if set(grant["provider"]) != {"version", "packageSha256", "producerSourceRevision"}:
        raise ValueError("grant provider shape changed")
    if set(grant["receiver"]) != {"repositoryPath", "commit", "tree", "projectedPayload"}:
        raise ValueError("grant receiver shape changed")
    if set(grant["executables"]) != {"git", "tar", "podman"} or set(grant["image"]) != {
        "qualifiedImage", "archiveSha256", "manifestDigest", "configDigest", "recipeSha256",
    }:
        raise ValueError("grant runtime shape changed")
    if (not isinstance(grant["grantId"], str) or not grant["grantId"]
            or not isinstance(grant["workflowRevision"], int) or grant["workflowRevision"] <= 0
            or not isinstance(grant["fenceGeneration"], int) or grant["fenceGeneration"] <= 0):
        raise ValueError("grant authority fields are malformed")
    try:
        datetime.strptime(grant["observedAt"], "%Y-%m-%dT%H:%M:%S.%fZ")
    except (TypeError, ValueError) as error:
        raise ValueError("grant observation time is malformed") from error
    if grant["enrollmentId"] != "local-python-hello-v1" or grant["allowedUid"] != facts_value["allowedUid"]:
        raise ValueError("grant selected another enrollment or user")
    if grant["cli"] != {
        "version": facts_value["package"]["version"],
        "packageSha256": facts_value["package"]["sha256"],
        "payloadSha256": facts_value["installedCli"]["payloadSha256"],
    }:
        raise ValueError("grant does not consume the exact installed CLI facts")
    if grant["provider"] != {
        "version": facts_value["provider"]["templateVersion"],
        "packageSha256": facts_value["provider"]["templatePackageSha256"],
        "producerSourceRevision": facts_value["producerSourceRevision"],
    }:
        raise ValueError("grant does not consume the exact provider facts")
    if grant["receiver"] != {
        "repositoryPath": "/srv/p4-receiver",
        "commit": facts_value["receiver"]["commit"],
        "tree": facts_value["receiver"]["tree"],
        "projectedPayload": facts_value["receiver"]["projectedPayload"],
    }:
        raise ValueError("grant does not consume the exact receiver facts")
    if grant["profileSha256"] != facts_value["profileSha256"] or grant["workspaceScope"] != "fs-gg/p4-python-receiver":
        raise ValueError("grant does not bind the exact profile")
    if grant["stateRoot"] != "/p4" or grant["executables"] != facts_value["executables"]:
        raise ValueError("grant does not bind the exact provider runtime")
    expected_image = {
        "qualifiedImage": facts_value["image"]["qualifiedImage"],
        "archiveSha256": facts_value["image"]["archiveSha256"],
        "manifestDigest": facts_value["image"]["manifestDigest"],
        "configDigest": facts_value["image"]["configDigest"],
        "recipeSha256": facts_value["image"]["manifestReceiptSha256"],
    }
    if grant["image"] != expected_image:
        raise ValueError("grant does not consume the exact image custody facts")
    facts_output.write_bytes(facts[0].read_bytes())
    grant_output.write_bytes(grants[0].read_bytes())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--extract-runtime", type=Path)
    parser.add_argument("--descriptor", type=Path)
    parser.add_argument("--template-package", type=Path)
    parser.add_argument("--facts-root", type=Path)
    parser.add_argument("--authorization-root", type=Path)
    parser.add_argument("--validated-facts", type=Path)
    parser.add_argument("--validated-grant", type=Path)
    args = parser.parse_args()
    try:
        if args.root and args.output:
            validate_join(args.root.resolve(), args.output.resolve())
        elif args.runtime and args.extract_runtime:
            extract_runtime(args.runtime.resolve(), args.extract_runtime.resolve())
        elif args.descriptor and args.template_package:
            bind_descriptor(args.descriptor.resolve(), args.template_package.resolve())
        elif all((args.facts_root, args.authorization_root, args.validated_facts, args.validated_grant)):
            validate_authorization(
                args.facts_root.resolve(), args.authorization_root.resolve(),
                args.validated_facts.resolve(), args.validated_grant.resolve(),
            )
        else:
            raise ValueError("exactly one provider input validation mode is required")
        return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError, tarfile.TarError) as error:
        print(f"PORTABLE_PROVIDER_INPUT_REFUSED {error}")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
