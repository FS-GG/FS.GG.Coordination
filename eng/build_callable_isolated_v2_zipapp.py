#!/usr/bin/env python3
"""Build the exact inspect-only isolated v2 zipapp with fixed ZIP metadata."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
ARCHIVE_NAME = "fsgg-callable-isolated-v2.pyz"
MEMBERS = {
    "__main__.py": "eng/callable_isolated_v2_entry.py",
    "callable_isolated_v2_grant.py": "eng/callable_isolated_v2_grant.py",
}
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _source(relative: str) -> bytes:
    path = ROOT / relative
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 128_000:
        raise ValueError("zipapp-source-unavailable")
    return path.read_bytes()


def build(output: pathlib.Path) -> dict:
    if output.name != ARCHIVE_NAME:
        raise ValueError("zipapp-output-name")
    sources = {name: _source(relative) for name, relative in MEMBERS.items()}
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, mode="w", compression=zipfile.ZIP_STORED,
                         allowZip64=False) as archive:
        archive.comment = b""
        for name in sorted(sources):
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, sources[name])
    artifact = output.read_bytes()
    return {
        "schema": "fsgg.coordination.callable-isolated-v2-zipapp-manifest/1",
        "artifact": ARCHIVE_NAME,
        "archiveSha256": _sha(artifact),
        "archiveSize": len(artifact),
        "builderSourceSha256": _sha(pathlib.Path(__file__).read_bytes()),
        "buildContract": "python3-stdlib-zipfile-stored-fixed-metadata",
        "entry": ["python3", "-I", ARCHIVE_NAME, "inspect-grant"],
        "members": [
            {"path": name, "source": MEMBERS[name], "sha256": _sha(sources[name]),
             "size": len(sources[name])}
            for name in sorted(sources)
        ],
        "state": "inspect-only-not-installed",
    }


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    args = parser.parse_args()
    manifest = build(args.output)
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
