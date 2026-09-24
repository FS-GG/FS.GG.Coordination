#!/usr/bin/env python3
"""Exact-parent admission journal append transport; import-only, no live CLI.

The production caller must supply a scoped ordinary-App Git transport after the
typed admission service has verified authority. This module never treats a push
failure as proof of absence or success: an independent journal reread decides.
"""

from __future__ import annotations

import base64
import hashlib
import importlib.util
import pathlib
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-git-read.py")
spec = importlib.util.spec_from_file_location("v1_admission_cas_git", SOURCE)
git_read = importlib.util.module_from_spec(spec)
spec.loader.exec_module(git_read)
PROVIDER_SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-provider-transport.py")
provider_spec = importlib.util.spec_from_file_location("v1_admission_cas_provider", PROVIDER_SOURCE)
provider_transport = importlib.util.module_from_spec(provider_spec)
provider_spec.loader.exec_module(provider_transport)

REF = git_read.OPERATION_REF
REPOSITORY_ID = git_read.REPOSITORY_ID
OID = git_read.OID
PERSON = "FS.GG Coordination <coordination@fs.gg> 0 +0000"
MAX_OBJECT_BYTES = 8192


class Refused(RuntimeError):
    """A definite local refusal before the provider write boundary."""


class ParentConflict(Refused):
    """The freshly fetched parent differs from the planned lease."""


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def exact(value, fields: set[str], reason: str) -> None:
    require(isinstance(value, dict) and set(value) == fields, reason)


def git_oid(kind: str, raw: bytes) -> str:
    return hashlib.sha1(f"{kind} {len(raw)}\0".encode() + raw).hexdigest()


def decode_plan(value: dict) -> dict:
    """Validate the complete public plan, including exact commit/tree bytes."""
    exact(value, {"schema", "repositoryId", "ref", "expectedParent", "proposedCommit",
                  "operationId", "objects"}, "admission-cas-plan-shape")
    require(value["schema"] == "fsgg.v1-admission-journal-cas/1"
            and type(value["repositoryId"]) is int
            and value["repositoryId"] == REPOSITORY_ID
            and value["ref"] == REF
            and isinstance(value["expectedParent"], str)
            and OID.fullmatch(value["expectedParent"]) is not None
            and isinstance(value["proposedCommit"], str)
            and OID.fullmatch(value["proposedCommit"]) is not None
            and isinstance(value["operationId"], str)
            and bool(value["operationId"])
            and "\n" not in value["operationId"]
            and "\r" not in value["operationId"]
            and isinstance(value["objects"], list)
            and len(value["objects"]) == 4, "admission-cas-plan-binding")
    objects = {}
    for item in value["objects"]:
        exact(item, {"kind", "oid", "bytesBase64"}, "admission-cas-object-shape")
        kind, oid, encoded = item["kind"], item["oid"], item["bytesBase64"]
        require(kind in {"blob", "tree", "commit"} and isinstance(oid, str)
                and OID.fullmatch(oid) is not None and isinstance(encoded, str),
                "admission-cas-object-binding")
        try:
            raw = base64.b64decode(encoded, validate=True)
        except (ValueError, UnicodeError) as error:
            raise Refused("admission-cas-object-encoding") from error
        require(0 < len(raw) <= MAX_OBJECT_BYTES
                and base64.b64encode(raw).decode("ascii") == encoded
                and git_oid(kind, raw) == oid and (kind, oid) not in objects,
                "admission-cas-object-binding")
        objects[(kind, oid)] = raw
    require(sorted(kind for kind, _ in objects) == ["blob", "blob", "commit", "tree"]
            and ("commit", value["proposedCommit"]) in objects,
            "admission-cas-object-set")
    tree_oid = next(oid for kind, oid in objects if kind == "tree")
    commit = objects[("commit", value["proposedCommit"])]
    expected_commit = (f"tree {tree_oid}\nparent {value['expectedParent']}\n"
                       f"author {PERSON}\ncommitter {PERSON}\n\n"
                       f"fsgg admission {value['operationId']}\n").encode("utf-8")
    require(commit == expected_commit, "admission-cas-commit-binding")
    blob_oids = {oid for kind, oid in objects if kind == "blob"}
    tree = objects[("tree", tree_oid)]
    try:
        entries = git_read.parse_tree(tree)
    except (git_read.Refused, UnicodeError) as error:
        raise Refused("admission-cas-tree-binding") from error
    require(set(entries.values()) == blob_oids and len(blob_oids) == 2,
            "admission-cas-tree-binding")
    return {**value, "decodedObjects": objects}


def git(store: pathlib.Path | None, arguments: list[str], data: bytes | None = None) -> bytes:
    """Local object/fetch operations only; never supplies a credential."""
    prefix = [f"--git-dir={store}"] if store is not None else []
    try:
        result = subprocess.run(["git", *prefix, *arguments], input=data,
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Refused("admission-cas-git-unavailable") from error
    require(result.returncode == 0 and len(result.stdout) <= 2_000_000,
            "admission-cas-git-unavailable")
    return result.stdout


def push_local(store: pathlib.Path, remote: str, expected: str, proposed: str) -> bool:
    """Test-only local receive-pack. A nonzero response is always unknown."""
    try:
        result = subprocess.run(
            ["git", f"--git-dir={store}", "push", "--porcelain",
             f"--force-with-lease={REF}:{expected}", remote,
             f"refs/heads/proposed:{REF}"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30, check=False)
        return result.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def append_local_fixture(plan: dict, remote: str, push=push_local) -> str:
    """Exercise CAS against a local bare repository; never contact a live remote."""
    checked = decode_plan(plan)
    require(isinstance(remote, str) and pathlib.Path(remote).is_absolute()
            and pathlib.Path(remote).is_dir() and not pathlib.Path(remote).is_symlink(),
            "admission-cas-local-remote")
    with tempfile.TemporaryDirectory(prefix="fsgg-v1-admission-cas-") as directory:
        store = pathlib.Path(directory) / "journal.git"
        git(None, ["init", "--bare", str(store)])
        git(store, ["fetch", "--no-tags", remote, REF])
        fetched = git(store, ["rev-parse", "FETCH_HEAD"]).decode("ascii").strip()
        if fetched != checked["expectedParent"]:
            raise ParentConflict("admission-cas-parent-moved")
        for (kind, oid), raw in checked["decodedObjects"].items():
            actual = git(store, ["hash-object", "-w", "-t", kind, "--stdin"], raw)
            require(actual.decode("ascii").strip() == oid, "admission-cas-local-object")
        git(store, ["update-ref", "refs/heads/proposed", checked["proposedCommit"]])
        try:
            pushed = push(store, remote, checked["expectedParent"], checked["proposedCommit"])
        except Exception:
            return "response-unknown"
        return "accepted" if pushed is True else "response-unknown"


def _append_checked_scoped(checked: dict, provider) -> str:
    """Network path: a raw push response never confirms durable admission."""
    with tempfile.TemporaryDirectory(prefix="fsgg-v1-admission-scoped-cas-") as directory:
        store = pathlib.Path(directory) / "journal.git"
        template = pathlib.Path(directory) / "empty-template"
        template.mkdir()
        git(None, ["init", "--bare", f"--template={template}", str(store)])
        provider.fetch_admission_ref(store)
        fetched = git(store, ["rev-parse", "FETCH_HEAD"]).decode("ascii").strip()
        if fetched != checked["expectedParent"]:
            raise ParentConflict("admission-cas-parent-moved")
        for (kind, oid), raw in checked["decodedObjects"].items():
            actual = git(store, ["hash-object", "-w", "-t", kind, "--stdin"], raw)
            require(actual.decode("ascii").strip() == oid, "admission-cas-local-object")
        git(store, ["update-ref", "refs/heads/proposed", checked["proposedCommit"]])
        try:
            provider.push_admission_ref(
                store, checked["expectedParent"], checked["proposedCommit"])
        except Exception:
            pass
        return "response-unknown"


def _append_scoped_for_qualification(plan: dict, provider) -> str:
    """Inject a fixture provider only for local qualification."""
    return _append_checked_scoped(decode_plan(plan), provider)


def append_with_ordinary_app(plan: dict, app_jwt: str) -> str:
    """Import-only production path; caller must supply a separately approved JWT."""
    checked = decode_plan(plan)
    provider = provider_transport.OrdinaryAdmissionTransport(app_jwt)
    provider.protection_snapshot()
    return _append_checked_scoped(checked, provider)
