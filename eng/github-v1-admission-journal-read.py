#!/usr/bin/env python3
"""Read the installed v1 admission Operation journal as exact, bounded Git objects.

This collector has no writer credential, repair path, or absent-as-empty behavior.
The typed decoder independently verifies every object and replays the registry.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import importlib.util
import json
import os
import pathlib
import sys
import tempfile

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-git-read.py")
spec = importlib.util.spec_from_file_location("v1_admission_genesis_git", SOURCE)
git_read = importlib.util.module_from_spec(spec)
spec.loader.exec_module(git_read)

SCHEMA = "fsgg.v1-admission-journal-git-read/1"
MAX_COMMITS = 4096
MAX_BYTES = 32_000_000
MAX_OBJECT_BYTES = 8192


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise git_read.Refused(reason)


def encoded(value: bytes) -> str:
    require(0 < len(value) <= MAX_OBJECT_BYTES, "admission-journal-object-size")
    return base64.b64encode(value).decode("ascii")


def collect(remote: str = git_read.REMOTE, read_repository_id=git_read.repository_id,
            read_refs=git_read.refs, observed_at: str | None = None) -> dict:
    require(read_repository_id() == git_read.REPOSITORY_ID, "admission-journal-repository-id")
    before = read_refs(remote)
    head = before.get(git_read.OPERATION_REF)
    require(isinstance(head, str) and git_read.OID.fullmatch(head) is not None,
            "admission-journal-not-installed")
    require(git_read.CUTOVER_REF in before, "admission-journal-cutover-missing")
    commits = []
    total = 0
    with tempfile.TemporaryDirectory(prefix="fsgg-v1-admission-history-") as directory:
        store = pathlib.Path(directory) / "journal.git"
        git_read.git(["init", "--bare", str(store)])
        git_read.git([f"--git-dir={store}", "fetch", "--no-tags", remote,
                      git_read.OPERATION_REF])
        fetched = git_read.git([f"--git-dir={store}", "rev-parse", "FETCH_HEAD"]).decode().strip()
        require(fetched == head, "admission-journal-fetch-moved")
        current = head
        seen = set()
        while current is not None:
            require(current not in seen and len(commits) < MAX_COMMITS,
                    "admission-journal-history-bound")
            seen.add(current)
            commit = git_read.object_bytes(store, "commit", current)
            tree_oid, parent = git_read.parse_commit(commit)
            tree = git_read.object_bytes(store, "tree", tree_oid)
            entries = git_read.parse_tree(tree)
            event = git_read.object_bytes(store, "blob", entries["event.json"])
            journal_head = git_read.object_bytes(store, "blob", entries["head.json"])
            total += sum(map(len, [commit, tree, event, journal_head]))
            require(total <= MAX_BYTES, "admission-journal-history-size")
            commits.append({
                "commitOid": current, "commitBytesBase64": encoded(commit),
                "treeOid": tree_oid, "treeBytesBase64": encoded(tree),
                "eventBytesBase64": encoded(event),
                "headBytesBase64": encoded(journal_head),
            })
            current = parent
    after = read_refs(remote)
    require(after == before, "admission-journal-refs-moved")
    if observed_at is None:
        observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schema": SCHEMA, "observedAt": observed_at,
        "repository": git_read.REPOSITORY,
        "repositoryId": git_read.REPOSITORY_ID,
        "ref": git_read.OPERATION_REF,
        "firstHead": head, "secondHead": after[git_read.OPERATION_REF],
        "commits": list(reversed(commits)),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    try:
        result = collect()
        require(not args.output.exists() and not args.output.is_symlink(),
                "admission-journal-output-exists")
        fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as output:
            output.write(json.dumps(result, sort_keys=True, separators=(",", ":")).encode() + b"\n")
        print(json.dumps({"schema": "fsgg.v1-admission-journal-read-result/1",
                          "head": result["firstHead"], "commits": len(result["commits"])},
                         sort_keys=True, separators=(",", ":")))
        return 0
    except (git_read.Refused, OSError, KeyError, TypeError, ValueError) as error:
        print(f"v1 admission journal read refused: {error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
