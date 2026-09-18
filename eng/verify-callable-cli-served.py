#!/usr/bin/env python3
import hashlib
import json
import sys
import zipfile
from pathlib import Path


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def payload(path: Path) -> dict[str, str]:
    with zipfile.ZipFile(path) as archive:
        return {
            name: hashlib.sha256(archive.read(name)).hexdigest()
            for name in sorted(archive.namelist())
            if name != ".signature.p7s"
        }


if len(sys.argv) != 4:
    raise SystemExit("usage: verify-callable-cli-served.py CANDIDATE SERVED FEED")

candidate, served, feed = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
if feed not in {"github-packages", "nuget-org"}:
    raise SystemExit(f"unsupported feed: {feed}")
if not candidate.is_file() or not served.is_file():
    raise SystemExit("candidate and served package must exist")

candidate_digest = digest(candidate)
served_digest = digest(served)
if feed == "github-packages" and served_digest != candidate_digest:
    raise SystemExit("GitHub Packages archive differs from the prepared candidate")
if payload(served) != payload(candidate):
    raise SystemExit(f"{feed} package payload differs from the prepared candidate")

print(
    json.dumps(
        {
            "feed": feed,
            "candidateSha256": candidate_digest,
            "servedArchiveSha256": served_digest,
            "payloadMatch": True,
        },
        separators=(",", ":"),
        sort_keys=True,
    )
)
