#!/usr/bin/env python3
"""Read exact public Authority Git objects for one protected v1 admission genesis."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile


REMOTE = "https://github.com/FS-GG/FS.GG.Coordination.Authority.git"
REPOSITORY = "FS-GG/FS.GG.Coordination.Authority"
REPOSITORY_ID = 1351660651
CUTOVER_REF = "refs/heads/fsgg/v2/journal/cutover/d5"
OPERATION_REF = "refs/heads/fsgg/v2/journal/operation/79"
CLAIM_PREFIX = "refs/heads/fsgg/v2/journal/claim/"
OPERATION_PREFIX = "refs/heads/fsgg/v2/journal/operation/"
TAG_PREFIX = "refs/tags/fsgg/v2/fleet-cutover/operating-v1/genesis-"
OID = re.compile(r"[0-9a-f]{40}\Z")


class Refused(RuntimeError):
    pass


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def git(arguments: list[str], input_bytes: bytes | None = None) -> bytes:
    environment = dict(os.environ)
    environment.update({"GIT_TERMINAL_PROMPT": "0", "GIT_CONFIG_NOSYSTEM": "1",
                        "GIT_CONFIG_GLOBAL": os.devnull})
    command = ["git", "-c", "credential.helper=", *arguments]
    try:
        result = subprocess.run(command, input=input_bytes, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, env=environment, timeout=30, check=False)
    except subprocess.TimeoutExpired as error:
        raise Refused("git-read-timeout") from error
    require(result.returncode == 0 and len(result.stdout) <= 2_000_000, "git-read-unavailable")
    return result.stdout


def repository_id() -> int:
    try:
        result = subprocess.run(["gh", "api", f"repos/{REPOSITORY}", "--jq", ".id"],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=30, check=False)
    except subprocess.TimeoutExpired as error:
        raise Refused("authority-identity-timeout") from error
    require(result.returncode == 0 and result.stdout.strip() == str(REPOSITORY_ID).encode(),
            "authority-repository-id")
    return REPOSITORY_ID


def refs(remote: str) -> dict[str, str]:
    raw = git(["ls-remote", "--refs", remote])
    entries = {}
    for line in raw.decode("ascii").splitlines():
        parts = line.split("\t")
        require(len(parts) == 2 and OID.fullmatch(parts[0]) is not None
                and parts[1].startswith("refs/") and parts[1] not in entries,
                "authority-ref-census-shape")
        entries[parts[1]] = parts[0]
    require(entries, "authority-ref-census-empty")
    return entries


def object_bytes(store: pathlib.Path, kind: str, oid: str) -> bytes:
    require(OID.fullmatch(oid) is not None, "authority-object-id")
    prefix = [f"--git-dir={store}"]
    require(git([*prefix, "cat-file", "-t", oid]).strip() == kind.encode(), "authority-object-type")
    raw = git([*prefix, "cat-file", kind, oid])
    computed = hashlib.sha1(f"{kind} {len(raw)}\0".encode() + raw).hexdigest()
    require(computed == oid, "authority-object-hash")
    return raw


def parse_commit(raw: bytes) -> tuple[str, str | None]:
    header, separator, _ = raw.partition(b"\n\n")
    require(bool(separator), "authority-commit-shape")
    lines = header.decode("utf-8").split("\n")
    trees = [line[5:] for line in lines if line.startswith("tree ")]
    parents = [line[7:] for line in lines if line.startswith("parent ")]
    require(len(trees) == 1 and OID.fullmatch(trees[0]) is not None
            and len(parents) <= 1 and all(OID.fullmatch(parent) for parent in parents),
            "authority-commit-parents")
    return trees[0], parents[0] if parents else None


def parse_tree(raw: bytes) -> dict[str, str]:
    entries = {}
    offset = 0
    while offset < len(raw):
        nul = raw.find(b"\0", offset)
        require(nul > offset and nul + 21 <= len(raw), "authority-tree-shape")
        mode, separator, name = raw[offset:nul].partition(b" ")
        require(bool(separator) and mode == b"100644", "authority-tree-mode")
        decoded = name.decode("utf-8")
        require(decoded not in entries, "authority-tree-duplicate")
        entries[decoded] = raw[nul + 1:nul + 21].hex()
        offset = nul + 21
    require(set(entries) == {"event.json", "head.json"}, "authority-tree-entries")
    return entries


def no_duplicate_pairs(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "authority-event-duplicate")
        value[key] = item
    return value


def claim_history(store: pathlib.Path, remote: str, ref: str, head: str) -> dict:
    """Read every raw object of one claim ref before the final stable census."""
    git([f"--git-dir={store}", "fetch", "--no-tags", remote, ref])
    fetched = git([f"--git-dir={store}", "rev-parse", "FETCH_HEAD"]).decode().strip()
    require(fetched == head, "authority-claim-fetch-moved")
    commits = []
    seen = set()
    total = 0
    current = head
    while current is not None:
        require(current not in seen and len(commits) < 4096,
                "authority-claim-history-bound")
        seen.add(current)
        commit = object_bytes(store, "commit", current)
        tree_oid, parent = parse_commit(commit)
        tree = object_bytes(store, "tree", tree_oid)
        entries = parse_tree(tree)
        event = object_bytes(store, "blob", entries["event.json"])
        journal_head = object_bytes(store, "blob", entries["head.json"])
        total += sum(map(len, (commit, tree, event, journal_head)))
        require(total <= 32_000_000 and all(0 < len(raw) <= 8192
                                            for raw in (commit, tree, event, journal_head)),
                "authority-claim-object-bound")
        commits.append({"commitOid": current,
                        "commitBytesBase64": base64.b64encode(commit).decode(),
                        "treeOid": tree_oid,
                        "treeBytesBase64": base64.b64encode(tree).decode(),
                        "eventBytesBase64": base64.b64encode(event).decode(),
                        "headBytesBase64": base64.b64encode(journal_head).decode()})
        current = parent
    return {"ref": ref, "firstHead": head, "secondHead": head,
            "commits": list(reversed(commits))}


def _collect(remote: str, read_repository_id, read_refs,
             observed_at: str | None, installed: bool) -> dict:
    require(read_repository_id() == REPOSITORY_ID, "authority-repository-id")
    before = read_refs(remote)
    head = before.get(CUTOVER_REF)
    require(isinstance(head, str) and OID.fullmatch(head) is not None, "authority-cutover-ref")
    claim_refs = sorted(ref for ref in before if ref.startswith(CLAIM_PREFIX))
    if installed:
        require(len(claim_refs) <= 64
                and all(re.fullmatch(re.escape(CLAIM_PREFIX) + r"[0-9a-f]{2}", ref)
                        for ref in claim_refs), "authority-claim-census-shape")
    else:
        require(not claim_refs, "authority-claim-census-not-empty")
    operation_refs = {ref for ref in before if ref.startswith(OPERATION_PREFIX)}
    if installed:
        require(operation_refs == {OPERATION_REF}
                and OID.fullmatch(before[OPERATION_REF]) is not None,
                "authority-operation-census-not-installed")
    else:
        require(not operation_refs, "authority-operation-census-not-empty")
    tags = {ref: oid for ref, oid in before.items() if ref.startswith(TAG_PREFIX)}
    require(len(tags) == 1, "authority-genesis-tag-census")

    with tempfile.TemporaryDirectory(prefix="fsgg-v1-authority-read-") as directory:
        store = pathlib.Path(directory) / "authority.git"
        git(["init", "--bare", str(store)])
        git([f"--git-dir={store}", "fetch", "--no-tags", remote, CUTOVER_REF])
        fetched = git([f"--git-dir={store}", "rev-parse", "FETCH_HEAD"]).decode().strip()
        require(fetched == head, "authority-cutover-fetch-moved")

        ancestry = []
        current = head
        while True:
            require(current not in ancestry and len(ancestry) < 128, "authority-ancestry-bound")
            ancestry.append(current)
            raw_commit = object_bytes(store, "commit", current)
            _, parent = parse_commit(raw_commit)
            if parent is None:
                break
            current = parent

        commit_bytes = object_bytes(store, "commit", head)
        tree_oid, parent_oid = parse_commit(commit_bytes)
        tree_bytes = object_bytes(store, "tree", tree_oid)
        entries = parse_tree(tree_bytes)
        event_bytes = object_bytes(store, "blob", entries["event.json"])
        head_bytes = object_bytes(store, "blob", entries["head.json"])
        try:
            event = json.loads(event_bytes, object_pairs_hook=no_duplicate_pairs)
        except (UnicodeError, json.JSONDecodeError) as error:
            raise Refused("authority-event-json") from error
        require(isinstance(event, dict), "authority-event-shape")
        manifest = event.get("manifestSha256")
        trust = event.get("trustAnchorSha256")
        require(isinstance(manifest, str) and re.fullmatch(r"[0-9a-f]{64}", manifest) is not None
                and isinstance(trust, str) and re.fullmatch(r"[0-9a-f]{64}", trust) is not None,
                "authority-event-digests")
        expected_tag = TAG_PREFIX + manifest[:16]
        require(tags == {expected_tag: head}, "authority-genesis-tag-binding")

        claims = [claim_history(store, remote, ref, before[ref]) for ref in claim_refs]

    after = read_refs(remote)
    require(after == before, "authority-ref-census-moved")
    if observed_at is None:
        observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    result = {
        "schema": ("fsgg.v1-admission-operating-git-read/1" if installed
                   else "fsgg.v1-admission-genesis-git-read/1"),
        "observedAt": observed_at,
        "repository": REPOSITORY,
        "repositoryId": REPOSITORY_ID,
        "cutover": {
            "ref": CUTOVER_REF,
            "firstHead": head,
            "secondHead": after[CUTOVER_REF],
            "tagRef": expected_tag,
            "tagTarget": after[expected_tag],
            "commit": head,
            "parent": parent_oid,
            "genesisCommit": ancestry[-1],
            "ancestry": ancestry,
            "commitTree": tree_oid,
            "commitBytesBase64": base64.b64encode(commit_bytes).decode(),
            "treeBytesBase64": base64.b64encode(tree_bytes).decode(),
            "treeEntries": entries,
            "eventOid": entries["event.json"],
            "eventBytesBase64": base64.b64encode(event_bytes).decode(),
            "headOid": entries["head.json"],
            "headBytesBase64": base64.b64encode(head_bytes).decode(),
            "manifestSha256": manifest,
            "trustAnchorSha256": trust,
            "claimRefs": claim_refs,
        },
        "operation": {
            "ref": OPERATION_REF,
            "firstHead": before[OPERATION_REF] if installed else None,
            "secondHead": after[OPERATION_REF] if installed else None,
            "observation": "present" if installed else "deleted",
        },
    }
    if installed:
        result["claims"] = claims
    return result


def collect(remote: str = REMOTE, read_repository_id=repository_id, read_refs=refs,
            observed_at: str | None = None) -> dict:
    """Genesis preflight still requires both operation and claim journals absent."""
    return _collect(remote, read_repository_id, read_refs, observed_at, False)


def collect_operating(remote: str = REMOTE, read_repository_id=repository_id,
                      read_refs=refs, observed_at: str | None = None) -> dict:
    """Observe installed admission and complete claim histories without writing."""
    return _collect(remote, read_repository_id, read_refs, observed_at, True)


def collect_installed(expected_commit: str, remote: str = REMOTE,
                      read_repository_id=repository_id, read_refs=refs,
                      observed_at: str | None = None) -> dict:
    """Read back one exact genesis without treating an absent ref as installed."""
    require(OID.fullmatch(expected_commit) is not None, "admission-expected-commit")
    require(read_repository_id() == REPOSITORY_ID, "authority-repository-id")
    before = read_refs(remote)
    cutover = before.get(CUTOVER_REF)
    require(isinstance(cutover, str) and OID.fullmatch(cutover) is not None,
            "authority-cutover-ref")
    require(not any(ref.startswith(CLAIM_PREFIX) for ref in before),
            "authority-claim-census-not-empty")
    require({ref for ref in before if ref.startswith(OPERATION_PREFIX)} == {OPERATION_REF},
            "admission-operation-census")
    require(before[OPERATION_REF] == expected_commit, "admission-competing-ref")

    with tempfile.TemporaryDirectory(prefix="fsgg-v1-admission-installed-read-") as directory:
        store = pathlib.Path(directory) / "authority.git"
        git(["init", "--bare", str(store)])
        git([f"--git-dir={store}", "fetch", "--no-tags", remote, OPERATION_REF])
        fetched = git([f"--git-dir={store}", "rev-parse", "FETCH_HEAD"]).decode().strip()
        require(fetched == expected_commit, "admission-fetch-moved")
        commit_bytes = object_bytes(store, "commit", expected_commit)
        tree_oid, parent = parse_commit(commit_bytes)
        require(parent is None, "admission-genesis-has-parent")
        tree_bytes = object_bytes(store, "tree", tree_oid)
        entries = parse_tree(tree_bytes)
        event_bytes = object_bytes(store, "blob", entries["event.json"])
        head_bytes = object_bytes(store, "blob", entries["head.json"])

    after = read_refs(remote)
    require(after == before, "authority-ref-census-moved")
    if observed_at is None:
        observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schema": "fsgg.v1-admission-genesis-installed-read/1",
        "observedAt": observed_at,
        "repository": REPOSITORY,
        "repositoryId": REPOSITORY_ID,
        "cutoverFirstHead": cutover,
        "cutoverSecondHead": after[CUTOVER_REF],
        "operation": {
            "ref": OPERATION_REF,
            "firstHead": before[OPERATION_REF],
            "secondHead": after[OPERATION_REF],
            "commitOid": expected_commit,
            "commitBytesBase64": base64.b64encode(commit_bytes).decode(),
            "treeOid": tree_oid,
            "treeBytesBase64": base64.b64encode(tree_bytes).decode(),
            "eventOid": entries["event.json"],
            "eventBytesBase64": base64.b64encode(event_bytes).decode(),
            "headOid": entries["head.json"],
            "headBytesBase64": base64.b64encode(head_bytes).decode(),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--expect-installed-commit")
    mode.add_argument("--operating", action="store_true")
    args = parser.parse_args()
    try:
        value = (collect_installed(args.expect_installed_commit)
                 if args.expect_installed_commit else
                 collect_operating() if args.operating else collect())
        require(not args.output.is_symlink() and not args.output.exists(), "authority-output-exists")
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "wb") as output:
            output.write(json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n")
        if args.expect_installed_commit:
            print(json.dumps({"schema": "fsgg.v1-admission-genesis-installed-read-result/1",
                              "authorityHead": value["cutoverFirstHead"],
                              "operationRef": OPERATION_REF,
                              "operationCommit": value["operation"]["commitOid"]},
                             sort_keys=True, separators=(",", ":")))
        elif args.operating:
            print(json.dumps({"schema": "fsgg.v1-admission-operating-git-read-result/1",
                              "authorityHead": value["cutover"]["commit"],
                              "operationRef": OPERATION_REF,
                              "operationHead": value["operation"]["firstHead"]},
                             sort_keys=True, separators=(",", ":")))
        else:
            print(json.dumps({"schema": "fsgg.v1-admission-genesis-git-read-result/1",
                              "authorityHead": value["cutover"]["commit"],
                              "operationRef": OPERATION_REF, "operation": "absent"},
                             sort_keys=True, separators=(",", ":")))
        return 0
    except (Refused, OSError, UnicodeError, KeyError, TypeError, ValueError) as error:
        print(f"v1 admission git read refused: {error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
