#!/usr/bin/env python3
"""Build and optionally bind the exact callable CLI release readback receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys


def refuse(message: str) -> None:
    print(f"CALLABLE_CLI_RELEASE_READBACK_REFUSED {message}", file=sys.stderr)
    raise SystemExit(2)


def sha(value: str, size: int) -> str:
    if len(value) != size or any(character not in "0123456789abcdef" for character in value):
        refuse(f"identity must be lowercase hexadecimal with length {size}")
    return value


def feed(path: Path, name: str, package_sha256: str) -> dict[str, object]:
    if not path.is_file():
        refuse(f"verified {name} feed result is absent")
    try:
        value = json.loads(path.read_bytes())
    except (OSError, json.JSONDecodeError):
        refuse(f"verified {name} feed result is invalid")
    expected_keys = {"feed", "candidateSha256", "servedArchiveSha256", "payloadMatch"}
    if not isinstance(value, dict) or set(value) != expected_keys:
        refuse(f"verified {name} feed result shape changed")
    if value["feed"] != name or value["candidateSha256"] != package_sha256 or value["payloadMatch"] is not True:
        refuse(f"verified {name} feed result identity changed")
    sha(value["servedArchiveSha256"], 64)
    return value



def pair_readback(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser()
    for name in ("publisher", "source", "tree", "merge", "manifest-sha256"):
        parser.add_argument("--" + name, required=True)
    for name in ("manifest", "feed-root", "installed", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    args = parser.parse_args(arguments)
    for identity in (args.publisher, args.source, args.tree, args.merge): sha(identity, 40)
    sha(args.manifest_sha256, 64)
    raw = args.manifest.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.manifest_sha256: refuse("pair manifest digest changed")
    manifest = json.loads(raw)
    ids = {"FS.GG.Execution.Contracts", "FS.GG.Execution.Processes"}
    if (manifest["schema"] != "fsgg.coordination.execution-pair-release-preparation/1" or manifest["profile"] != "execution-pair"
        or manifest["version"] != "0.2.0" or manifest["tag"] != "execution/v0.2.0"
        or manifest["sourceCommit"] != args.source or manifest["sourceTree"] != args.tree
        or manifest["publicationAuthorized"] is not False or manifest["tagAuthorized"] is not False
        or len(manifest["packages"]) != 2 or {row["packageId"] for row in manifest["packages"]} != ids):
        refuse("pair manifest source/member identity changed")
    installed = json.loads(args.installed.read_bytes())
    if (installed["schema"] != "fsgg.coordination.execution-pair-installed-load/1" or installed["mode"] != "public"
        or installed["publicOnly"] is not True or installed["producerProjectReferenced"] is not False
        or installed["packageInputsOnly"] is not True or installed["physicalSourceExclusionEstablished"] is not False
        or installed["privateCachesUnavailable"] is not True or installed["effectiveProjectIsolationGuard"] is not True
        or installed["effectiveNuGetFoldersVerified"] is not True or installed["actualCompilerReferencesVerified"] is not True
        or installed["nativeFixtureInvoked"] is not False
        or installed["consumerAdoptionAccepted"] is not False or len(installed["packages"]) != 2
        or {row["packageId"] for row in installed["packages"]} != ids): refuse("pair public installed-load proof missing")
    members = []
    for row in sorted(manifest["packages"], key=lambda row: row["packageId"]):
        sha(row["sha256"], 64); sha(row["assemblySha256"], 64)
        actual = [item for item in installed["packages"] if item["packageId"] == row["packageId"]]
        if actual != [{"packageId": row["packageId"], "version": "0.2.0", "runtimeSha256": row["assemblySha256"]}]: refuse("pair installed runtime join changed")
        github = feed(args.feed_root / (row["packageId"] + ".github.json"), "github-packages", row["sha256"])
        if github["servedArchiveSha256"] != row["sha256"]: refuse("pair GitHub raw archive identity changed")
        members.append({"packageId": row["packageId"], "candidateSha256": row["sha256"], "assemblySha256": row["assemblySha256"],
                        "githubPackages": github,
                        "nugetOrg": feed(args.feed_root / (row["packageId"] + ".nuget.json"), "nuget-org", row["sha256"])})
    value = {"schema": "fsgg.coordination.execution-pair-release-readback/1", "profile": "execution-pair", "publisherSource": args.publisher,
             "source": args.source, "sourceTree": args.tree, "protectedMerge": args.merge, "version": "0.2.0", "tag": "execution/v0.2.0",
             "manifestSha256": args.manifest_sha256, "packages": members, "installedLoad": installed,
             "consumerAdoptionAccepted": False}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode())
    return 0

def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--execution-pair":
        return pair_readback(sys.argv[2:])
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli-only", action="store_true")
    parser.add_argument("--publisher", help="Reviewed publisher B for CLI-only source A")
    parser.add_argument("--source", required=True)
    parser.add_argument("--tree", required=True)
    parser.add_argument("--merge", required=True)
    parser.add_argument("--package-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--package-sha256", required=True)
    parser.add_argument("--bundle-sha256")
    parser.add_argument("--image-sha256")
    parser.add_argument("--manifest-sha256")
    parser.add_argument("--github-feed", required=True, type=Path)
    parser.add_argument("--nuget-feed", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--existing", type=Path)
    parser.add_argument("--original-publication", type=Path, help="Verified original failed attempt retained by readback recovery")
    args = parser.parse_args()

    sha(args.source, 40)
    sha(args.tree, 40)
    sha(args.merge, 40)
    sha(args.package_sha256, 64)
    if args.cli_only:
        if args.publisher is None or any((args.bundle_sha256, args.image_sha256, args.manifest_sha256)):
            refuse("CLI-only requires publisher identity and excludes portable assets")
        sha(args.publisher, 40)
    else:
        if args.publisher is not None or any(value is None for value in (args.bundle_sha256, args.image_sha256, args.manifest_sha256)):
            refuse("portable readback requires every original asset")
        for value in (args.bundle_sha256, args.image_sha256, args.manifest_sha256):
            sha(value, 64)
    if args.package_id != "FS.GG.Coordination.Cli" or args.version != ("0.3.0" if args.cli_only else "0.2.0"):
        refuse("package identity changed")
    value = {
        "schema": "fsgg.coordination.callable-cli-release-readback/2",
        "source": args.source,
        "sourceTree": args.tree,
        "protectedMerge": args.merge,
        "packageId": args.package_id,
        "version": args.version,
        "packageSha256": args.package_sha256,
        "portableAssets": {
            "bundleSha256": args.bundle_sha256,
            "imageArchiveSha256": args.image_sha256,
            "manifestSha256": args.manifest_sha256,
        },
        "feeds": {
            "githubPackages": feed(args.github_feed, "github-packages", args.package_sha256),
            "nugetOrg": feed(args.nuget_feed, "nuget-org", args.package_sha256),
        },
    }
    if args.cli_only:
        value["schema"] = "fsgg.coordination.callable-cli-release-readback/3"
        value["publisherSource"] = args.publisher
        del value["portableAssets"]
    if args.original_publication is not None:
        if not args.cli_only:
            refuse("original publication applies only to CLI-only recovery")
        try:
            original = json.loads(args.original_publication.read_bytes())
            keys = {"publisherSource", "runId", "runAttempt", "jobId", "observationArtifactId", "observationArchiveSha256", "conclusion", "publicReadback"}
            if not isinstance(original, dict) or set(original) != keys:
                refuse("original publication shape changed")
            sha(original["publisherSource"], 40)
            sha(original["observationArchiveSha256"], 64)
            if any(type(original[key]) is not int or original[key] <= 0 for key in ("runId", "jobId", "observationArtifactId")):
                refuse("original publication native identity changed")
            if type(original["runAttempt"]) is not int or original["runAttempt"] != 1 or original["conclusion"] != "failure" or original["publicReadback"] != "unresolved-after-404":
                refuse("original publication outcome changed")
        except (OSError, ValueError, TypeError, KeyError):
            refuse("original publication evidence unavailable")
        value["originalPublication"] = original
    expected = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if args.existing is not None:
        if not args.existing.is_file():
            refuse("existing release readback is absent")
        if args.existing.read_bytes() != expected:
            refuse("existing release readback differs from the complete expected receipt")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(expected)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
