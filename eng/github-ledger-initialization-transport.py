#!/usr/bin/env python3
"""Apply or verify one sealed GS2-08.2 genesis plan without retaining credentials."""

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
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.github.com"
REPOSITORY = "FS-GG/FS.GG.Coordination.Authority"
REPOSITORY_ID = 1351660651
CUTOVER_APP_ID = 4882399
CUTOVER_INSTALLATION_ID = 160261436
PLAN_SCHEMA = "fsgg.github-ledger-initialization-plan/1"


class Refused(RuntimeError):
    pass


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def b64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def oid(kind, payload):
    return hashlib.sha1(f"{kind} {len(payload)}\0".encode() + payload).hexdigest()


def request(path, token=None, method="GET", body=None):
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "fsgg-ledger-initialization-transport",
    }
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    call = urllib.request.Request(
        API + path,
        data=canonical(body) if body is not None else None,
        method=method,
        headers=headers,
    )
    try:
        with urllib.request.urlopen(call, timeout=30) as response:
            raw = response.read()
        return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        try:
            value = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            value = None
        return error.code, value
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
        raise Refused("provider-indeterminate:" + type(error).__name__) from error


def mint(key_fd):
    now = int(time.time())
    header = b64url(canonical({"alg": "RS256", "typ": "JWT"}))
    payload = b64url(canonical({"iat": now - 60, "exp": now + 540, "iss": CUTOVER_APP_ID}))
    signing_input = (header + "." + payload).encode("ascii")
    signed = subprocess.run(
        ["openssl", "dgst", "-sha256", "-sign", f"/proc/self/fd/{key_fd}"],
        input=signing_input,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        pass_fds=(key_fd,),
        check=False,
    )
    if signed.returncode != 0 or not signed.stdout:
        raise Refused("credential-signing-refused")
    jwt = header + "." + payload + "." + b64url(signed.stdout)
    status, value = request(
        f"/app/installations/{CUTOVER_INSTALLATION_ID}/access_tokens",
        jwt,
        "POST",
        {"repository_ids": [REPOSITORY_ID], "permissions": {"contents": "write"}},
    )
    token = value.get("token") if status == 201 and isinstance(value, dict) else None
    if not isinstance(token, str) or not token:
        raise Refused("installation-token-refused")
    return token


def load_plan(path):
    plan_path = pathlib.Path(path)
    if plan_path.is_symlink() or not plan_path.is_file():
        raise Refused("plan-must-be-regular-nonsymlink")
    plan = json.loads(plan_path.read_bytes())
    if plan.get("schema") != PLAN_SCHEMA or plan.get("expectedRef") != "absent":
        raise Refused("plan-contract")
    if plan.get("ref") != "refs/heads/fsgg/v2/journal/cutover/d5":
        raise Refused("plan-ref")
    if not str(plan.get("tag", "")).startswith("refs/tags/fsgg/v2/fleet-cutover/operating-v1/"):
        raise Refused("plan-tag")
    if plan.get("operationOrder") != [
        "put-event-blob", "put-head-blob", "put-tree", "put-commit", "reread-objects",
        "create-ref-expected-absent", "reread-ref", "create-tag-expected-absent", "reread-tag",
    ]:
        raise Refused("plan-operation-order")
    objects = plan.get("objects")
    if not isinstance(objects, list) or [item.get("kind") for item in objects] != ["blob", "blob", "tree", "commit"]:
        raise Refused("plan-objects")
    decoded = []
    for item in objects:
        try:
            payload = base64.b64decode(item["bytesBase64"], validate=True)
        except (KeyError, ValueError) as error:
            raise Refused("plan-object-encoding") from error
        if oid(item["kind"], payload) != item.get("oid"):
            raise Refused("plan-object-oid")
        decoded.append({"kind": item["kind"], "oid": item["oid"], "bytes": payload})
    if decoded[-1]["oid"] != plan.get("commitOid"):
        raise Refused("plan-commit")
    return plan, decoded


def ref_path(name):
    return "/repos/" + REPOSITORY + "/git/ref/" + urllib.parse.quote(name.removeprefix("refs/"), safe="/")


def read_ref(name, token=None):
    status, value = request(ref_path(name), token)
    if status == 404:
        return None
    if status != 200 or not isinstance(value, dict):
        raise Refused("ref-read-indeterminate")
    result = (value.get("object") or {}).get("sha")
    if not isinstance(result, str) or not re.fullmatch(r"[0-9a-f]{40}", result):
        raise Refused("ref-read-invalid")
    return result


def put_blob(item, token):
    status, value = request(
        f"/repos/{REPOSITORY}/git/blobs", token, "POST",
        {"content": base64.b64encode(item["bytes"]).decode("ascii"), "encoding": "base64"},
    )
    if status != 201 or not isinstance(value, dict) or value.get("sha") != item["oid"]:
        raise Refused("blob-object-mismatch")


def parse_tree(payload):
    entries = []
    offset = 0
    while offset < len(payload):
        separator = payload.find(b"\0", offset)
        if separator < 0 or separator + 21 > len(payload):
            raise Refused("tree-object-invalid")
        prefix = payload[offset:separator].decode("utf-8")
        mode, path = prefix.split(" ", 1)
        object_id = payload[separator + 1:separator + 21].hex()
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": object_id})
        offset = separator + 21
    return entries


def put_tree(item, token):
    status, value = request(
        f"/repos/{REPOSITORY}/git/trees", token, "POST", {"tree": parse_tree(item["bytes"])},
    )
    if status != 201 or not isinstance(value, dict) or value.get("sha") != item["oid"]:
        raise Refused("tree-object-mismatch")


def parse_person(value):
    match = re.fullmatch(r"(.+) <([^<>]+)> ([0-9]+) ([+-][0-9]{4})", value)
    if match is None:
        raise Refused("commit-person-invalid")
    stamp = dt.datetime.fromtimestamp(int(match.group(3)), dt.timezone.utc)
    zone = match.group(4)
    offset = dt.timedelta(hours=int(zone[1:3]), minutes=int(zone[3:5]))
    if zone[0] == "-":
        offset = -offset
    stamp = stamp.astimezone(dt.timezone(offset)).isoformat()
    return {"name": match.group(1), "email": match.group(2), "date": stamp}


def utc_iso(value):
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise Refused("commit-person-time-zone")
    return parsed.astimezone(dt.timezone.utc).isoformat()


def parse_commit(payload):
    header, separator, message = payload.partition(b"\n\n")
    if not separator:
        raise Refused("commit-object-invalid")
    fields = {}
    for line in header.decode("utf-8").splitlines():
        key, value = line.split(" ", 1)
        if key in fields or key not in ("tree", "author", "committer"):
            raise Refused("commit-object-header")
        fields[key] = value
    if set(fields) != {"tree", "author", "committer"}:
        raise Refused("commit-object-header")
    return {
        "message": message.decode("utf-8").removesuffix("\n"),
        "tree": fields["tree"],
        "parents": [],
        "author": parse_person(fields["author"]),
        "committer": parse_person(fields["committer"]),
    }


def put_commit(item, token):
    status, value = request(f"/repos/{REPOSITORY}/git/commits", token, "POST", parse_commit(item["bytes"]))
    if status != 201 or not isinstance(value, dict) or value.get("sha") != item["oid"]:
        raise Refused("commit-object-mismatch")


def verify_objects(objects, token=None):
    for item in objects:
        suffix = {"blob": "blobs", "tree": "trees", "commit": "commits"}[item["kind"]]
        status, value = request(f"/repos/{REPOSITORY}/git/{suffix}/{item['oid']}", token)
        if status != 200 or not isinstance(value, dict) or value.get("sha") != item["oid"]:
            raise Refused("object-readback:" + item["oid"])
        if item["kind"] == "blob":
            content = str(value.get("content", "")).replace("\n", "")
            if base64.b64decode(content) != item["bytes"]:
                raise Refused("blob-readback:" + item["oid"])
        elif item["kind"] == "tree":
            observed = [
                {"path": entry.get("path"), "mode": entry.get("mode"), "type": entry.get("type"), "sha": entry.get("sha")}
                for entry in value.get("tree", [])
            ]
            if observed != parse_tree(item["bytes"]):
                raise Refused("tree-readback:" + item["oid"])
        else:
            expected = parse_commit(item["bytes"])
            observed = {
                "message": value.get("message"),
                "tree": (value.get("tree") or {}).get("sha"),
                "parents": [parent.get("sha") for parent in value.get("parents", [])],
                "author": value.get("author"),
                "committer": value.get("committer"),
            }
            for person in ("author", "committer"):
                if isinstance(observed[person], dict) and isinstance(observed[person].get("date"), str):
                    observed[person] = dict(observed[person])
                    observed[person]["date"] = utc_iso(observed[person]["date"])
                expected[person]["date"] = utc_iso(expected[person]["date"])
            if observed != expected:
                raise Refused("commit-readback:" + item["oid"])


def create_ref(name, commit, token):
    status, _ = request(f"/repos/{REPOSITORY}/git/refs", token, "POST", {"ref": name, "sha": commit})
    try:
        observed = read_ref(name, token)
    except Refused:
        if status == 201:
            raise
        raise Refused("ref-create-indeterminate")
    if observed == commit:
        return
    if observed is not None:
        raise Refused("expected-absence-conflict")
    raise Refused("ref-create-refused")


def verify(plan, objects, token=None):
    commit = plan["commitOid"]
    if read_ref(plan["ref"], token) != commit or read_ref(plan["tag"], token) != commit:
        raise Refused("ref-readback-mismatch")
    verify_objects(objects, token)


def apply(plan, objects, key_fd):
    token = mint(key_fd)
    commit = plan["commitOid"]
    branch = read_ref(plan["ref"], token)
    tag = read_ref(plan["tag"], token)
    if branch not in (None, commit) or tag not in (None, commit):
        raise Refused("expected-absence-conflict")
    if branch is None:
        put_blob(objects[0], token)
        put_blob(objects[1], token)
        put_tree(objects[2], token)
        put_commit(objects[3], token)
        verify_objects(objects, token)
        create_ref(plan["ref"], commit, token)
    if tag is None:
        if read_ref(plan["ref"], token) != commit:
            raise Refused("branch-readback-mismatch")
        create_ref(plan["tag"], commit, token)
    verify(plan, objects, token)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("apply", "verify"))
    parser.add_argument("--plan", required=True)
    parser.add_argument("--credential-fd", type=int)
    args = parser.parse_args()
    try:
        plan, objects = load_plan(args.plan)
        if args.mode == "apply":
            if args.credential_fd is None or args.credential_fd < 3:
                raise Refused("credential-fd-required")
            apply(plan, objects, args.credential_fd)
        else:
            if args.credential_fd is not None:
                raise Refused("verify-does-not-accept-credential")
            verify(plan, objects)
        print(canonical({
            "schema": "fsgg.github-ledger-initialization-transport-result/1",
            "mode": args.mode,
            "repository": REPOSITORY,
            "commitOid": plan["commitOid"],
            "ref": plan["ref"],
            "tag": plan["tag"],
            "outcome": "verified",
        }).decode())
        return 0
    except (Refused, OSError, ValueError, json.JSONDecodeError) as error:
        print("ledger initialization transport refused: " + str(error), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
