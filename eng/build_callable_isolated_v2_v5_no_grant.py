#!/usr/bin/env python3
"""Build a deterministic local v5 no-grant refusal candidate only."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import pathlib
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
ARCHIVE_NAME = "fsgg-callable-isolated-v2-v5-no-grant.pyz"
MANIFEST = "work/fsc07-isolated-operator-v2/v5-no-grant-manifest.json"
ENTRY_SOURCE = "eng/callable_isolated_v2_v5_vault_no_grant_entry.py"
WORKFLOW = ".github/workflows/callable-isolated-v2-execute.yml"
PINNED_ENTRY_SHA256 = "d235e8e7b7ef3a3056fdd03443fc4b7736b78d7844428c66aa83e434bfe42dc8"
PINNED_WORKFLOW_SHA256 = "7406efeabb5aca0504e8ae39a514038d7473b6fd97a66d2e4efcc5b2c94799db"
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _source(relative: str) -> bytes:
    path = ROOT / relative
    if (path.is_symlink() or not path.is_file()
            or not 0 < path.stat().st_size <= 128_000):
        raise ValueError("v5-source-unavailable")
    return path.read_bytes()


def _archive(entry: bytes) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, mode="w", compression=zipfile.ZIP_STORED,
                         allowZip64=False) as archive:
        archive.comment = b""
        info = zipfile.ZipInfo("__main__.py", FIXED_TIME)
        info.compress_type = zipfile.ZIP_STORED
        info.create_system = 3
        info.external_attr = (stat.S_IFREG | 0o644) << 16
        archive.writestr(info, entry)
    return output.getvalue()


def build(output: pathlib.Path) -> dict:
    """Produce one inert entry only; a protected workflow cannot consume it."""
    if output.name != ARCHIVE_NAME or output.is_symlink():
        raise ValueError("v5-output-name")
    entry = _source(ENTRY_SOURCE)
    workflow = _source(WORKFLOW)
    if (_sha(entry) != PINNED_ENTRY_SHA256
            or _sha(workflow) != PINNED_WORKFLOW_SHA256
            or b"if: ${{ false }}" not in workflow
            or b"run: exit 78" not in workflow):
        raise ValueError("v5-source-or-workflow-drift")
    archive = _archive(entry)
    manifest = {
        "schema": "fsgg.coordination.callable-isolated-v2-v5-no-grant/1",
        "state": "closed-source-not-installed",
        "artifact": ARCHIVE_NAME,
        "archiveSha256": _sha(archive),
        "archiveSize": len(archive),
        "builderSourceSha256": _sha(_source(
            "eng/build_callable_isolated_v2_v5_no_grant.py")),
        "workflowPath": WORKFLOW,
        "workflowSha256": _sha(workflow),
        "entry": ["python3", "-I", "-S", ARCHIVE_NAME,
                  "execute-native-pull"],
        "members": [{"path": "__main__.py", "source": ENTRY_SOURCE,
                     "sha256": _sha(entry), "size": len(entry)}]}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(archive)
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    args = parser.parse_args()
    manifest = build(args.output)
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(manifest, sort_keys=True,
                                        separators=(",", ":")) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
