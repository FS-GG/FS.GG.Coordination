#!/usr/bin/env python3
"""Build deterministic, non-dispatchable native-v2 effect scaffold archive."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
ARCHIVE_NAME = "fsgg-callable-isolated-v2-effect-scaffold.pyz"
MANIFEST = "work/fsc07-isolated-operator-v2/effect-scaffold-manifest.json"
WORKFLOW = ".github/workflows/callable-isolated-v2-execute.yml"
NATIVE_SOURCE = "eng/callable-cli-isolated-operation-v2.py"
NATIVE_SOURCE_SHA256 = "7f275d4f6e28808a6d30b9427dc26d7a867fdddbd65ae146d5b28872c1a72fcd"
MEMBERS = {
    "__main__.py": "eng/callable_isolated_v2_effect_entry.py",
    "callable_isolated_v2_effect_closed.py":
        "eng/callable_isolated_v2_effect_closed.py",
    "callable_isolated_v2_effect_candidate.py":
        "eng/callable_isolated_v2_effect_candidate.py",
}
PINNED_MEMBERS = {
    "__main__.py": "4b3ef835e4b998e374cfe61b886e07981aeab94cb0ff4262ee549c3ce693e0e7",
    "callable_isolated_v2_effect_candidate.py":
        "71fbe3e75c3e4185f74fa7284f26047dbfb5719fa309d4c2c0f5d5eb61558b87",
    "callable_isolated_v2_effect_closed.py":
        "fc32d5525b0af8dcfe84e38a28eff26dcbf62f6586c6ea19a4525c21c58b5c42",
}
PINNED_WORKFLOW_SHA256 = "7406efeabb5aca0504e8ae39a514038d7473b6fd97a66d2e4efcc5b2c94799db"
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _source(relative: str) -> bytes:
    path = ROOT / relative
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 256_000:
        raise ValueError("effect-source-unavailable")
    return path.read_bytes()


def build(output: pathlib.Path) -> dict:
    if output.name != ARCHIVE_NAME or output.is_symlink():
        raise ValueError("effect-output-name")
    sources = {name: _source(relative) for name, relative in MEMBERS.items()}
    native_source = _source(NATIVE_SOURCE)
    workflow = _source(WORKFLOW)
    if (set(sources) != set(PINNED_MEMBERS)
            or any(_sha(raw) != PINNED_MEMBERS[name]
                   for name, raw in sources.items())
            or _sha(native_source) != NATIVE_SOURCE_SHA256
            or _sha(workflow) != PINNED_WORKFLOW_SHA256):
        raise ValueError("effect-source-or-workflow-drift")
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
    return {"schema": "fsgg.coordination.callable-isolated-v2-effect-scaffold/1",
            "state": "closed-source-not-installed",
            "artifact": ARCHIVE_NAME,
            "archiveSha256": _sha(artifact), "archiveSize": len(artifact),
            "builderSourceSha256": _sha(pathlib.Path(__file__).read_bytes()),
            "nativeSource": {"path": NATIVE_SOURCE,
                             "sha256": _sha(native_source),
                             "size": len(native_source)},
            "workflowPath": WORKFLOW, "workflowSha256": _sha(workflow),
            "entry": ["python3", "-I", "-S", ARCHIVE_NAME,
                      "execute-native-pull"],
            "members": [{"path": name, "source": MEMBERS[name],
                         "sha256": _sha(sources[name]),
                         "size": len(sources[name])}
                        for name in sorted(sources)]}


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
