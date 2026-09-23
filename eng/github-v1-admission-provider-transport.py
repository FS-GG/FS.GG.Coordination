#!/usr/bin/env python3
"""Narrow ordinary-App provider operations for the protected v1 admission port.

This is an importable transport, not a standalone writer. The caller must complete
the typed installer's signed-approval and fresh-authority checks before using it.
It accepts a short-lived App JWT supplied by the Main host, never an App PEM.
"""

from __future__ import annotations

import base64
import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request


API = "https://api.github.com"
REPOSITORY = "FS-GG/FS.GG.Coordination.Authority"
REPOSITORY_ID = 1351660651
APP_ID = 4882140
APP_SLUG = "fs-gg-ordinary-journal-writer"
INSTALLATION_ID = 160261608
OPERATION_REF = "refs/heads/fsgg/v2/journal/operation/79"
OID = re.compile(r"[0-9a-f]{40}\Z")
WRITER_RULESET_ID = 21872113
INTEGRITY_RULESET_ID = 21872115
JOURNAL_PATTERN = "refs/heads/fsgg/v2/journal/**/*"
CUTOVER_REF = "refs/heads/fsgg/v2/journal/cutover/d5"


class Refused(RuntimeError):
    """A bounded refusal without provider response bodies or credentials."""


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def git_oid(kind: str, raw: bytes) -> str:
    require(kind in {"blob", "tree", "commit"}, "admission-object-kind")
    return hashlib.sha1(f"{kind} {len(raw)}\0".encode() + raw).hexdigest()


def read_app_jwt_fd(fd: int) -> str:
    """Receive only a short-lived signed JWT over an inherited descriptor, not a PEM."""
    require(type(fd) is int and fd >= 3, "admission-app-jwt-fd")
    try:
        chunks = []
        size = 0
        while True:
            chunk = os.read(fd, 8193 - size)
            if not chunk:
                break
            chunks.append(chunk)
            size += len(chunk)
            require(size <= 8192, "admission-app-jwt-fd")
        raw = b"".join(chunks)
    except OSError as error:
        raise Refused("admission-app-jwt-fd") from error
    require(0 < len(raw) <= 8192 and b"\n" not in raw and b"\0" not in raw,
            "admission-app-jwt-fd")
    try:
        return raw.decode("ascii")
    except UnicodeError as error:
        raise Refused("admission-app-jwt-fd") from error


def _jwt_payload(jwt: str, now: int) -> None:
    require(isinstance(jwt, str) and 0 < len(jwt) <= 8192 and jwt.isascii()
            and re.fullmatch(r"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", jwt) is not None,
            "admission-app-jwt-shape")
    try:
        header, payload, _ = jwt.split(".")
        header_value = json.loads(base64.urlsafe_b64decode(header + "=" * (-len(header) % 4)))
        claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    except (ValueError, UnicodeError) as error:
        raise Refused("admission-app-jwt-shape") from error
    require(header_value == {"alg": "RS256", "typ": "JWT"}
            and isinstance(claims, dict) and set(claims) == {"iat", "exp", "iss"}
            and type(claims["iat"]) is int and type(claims["exp"]) is int
            and claims["iss"] in (APP_ID, str(APP_ID))
            and now - 120 <= claims["iat"] <= now + 60
            and now < claims["exp"] <= now + 600
            and claims["exp"] - claims["iat"] <= 600,
            "admission-app-jwt-claims")


def request(path: str, token: str, method: str = "GET", body: dict | None = None):
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "fsgg-v1-admission-provider-transport",
        "Authorization": "Bearer " + token,
    }
    raw = json.dumps(body, sort_keys=True, separators=(",", ":")).encode() if body is not None else None
    call = urllib.request.Request(API + path, data=raw, headers=headers, method=method)
    try:
        with urllib.request.urlopen(call, timeout=30) as response:
            content = response.read(2_000_001)
            require(len(content) <= 2_000_000, "admission-provider-response-size")
            return response.status, json.loads(content) if content else None
    except urllib.error.HTTPError as error:
        error.read(2_000_001)  # Drain a bounded body; never include it in diagnostics.
        return error.code, None
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
        raise Refused("admission-provider-indeterminate") from error


def read_rules_api(path: str):
    """Use a read-only administrative observation; this token is never writer authority."""
    require(path.startswith(f"repos/{REPOSITORY}/"), "admission-rules-path")
    try:
        result = subprocess.run(["gh", "api", "--method", "GET", path],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=30, check=False)
    except subprocess.TimeoutExpired as error:
        raise Refused("admission-rules-read-timeout") from error
    require(result.returncode == 0 and len(result.stdout) <= 2_000_000,
            "admission-rules-read-unavailable")
    try:
        return json.loads(result.stdout)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise Refused("admission-rules-read-json") from error


def _person(value: str) -> dict:
    match = re.fullmatch(r"(.+) <([^<>]+)> ([0-9]+) ([+-][0-9]{4})", value)
    require(match is not None, "admission-commit-person")
    zone = match.group(4)
    delta = dt.timedelta(hours=int(zone[1:3]), minutes=int(zone[3:5]))
    if zone[0] == "-":
        delta = -delta
    stamp = dt.datetime.fromtimestamp(int(match.group(3)), dt.timezone.utc)
    return {"name": match.group(1), "email": match.group(2),
            "date": stamp.astimezone(dt.timezone(delta)).isoformat()}


def _commit_body(raw: bytes) -> dict:
    header, separator, message = raw.partition(b"\n\n")
    require(bool(separator), "admission-commit-shape")
    fields = {}
    for line in header.decode("utf-8").split("\n"):
        key, separator, value = line.partition(" ")
        require(bool(separator) and key in {"tree", "author", "committer"} and key not in fields,
                "admission-commit-header")
        fields[key] = value
    require(set(fields) == {"tree", "author", "committer"} and OID.fullmatch(fields["tree"]) is not None
            and message.endswith(b"\n") and b"\n" not in message[:-1], "admission-commit-root")
    return {"tree": fields["tree"], "parents": [], "author": _person(fields["author"]),
            "committer": _person(fields["committer"]), "message": message.decode("utf-8")}


def _tree_body(raw: bytes) -> list[dict]:
    entries = []
    offset = 0
    while offset < len(raw):
        nul = raw.find(b"\0", offset)
        require(nul > offset and nul + 21 <= len(raw), "admission-tree-shape")
        mode, separator, name = raw[offset:nul].partition(b" ")
        require(bool(separator) and mode == b"100644" and name in (b"event.json", b"head.json"),
                "admission-tree-entry")
        entries.append({"path": name.decode("ascii"), "mode": "100644", "type": "blob",
                        "sha": raw[nul + 1:nul + 21].hex()})
        offset = nul + 21
    require([entry["path"] for entry in entries] == ["event.json", "head.json"],
            "admission-tree-entries")
    return entries


class OrdinaryAdmissionTransport:
    """Provider calls restricted to the one App, installation, repository and ref."""

    def __init__(self, app_jwt: str, send=request, now: int | None = None):
        current = int(time.time()) if now is None else now
        _jwt_payload(app_jwt, current)
        self._send = send
        status, app = send("/app", app_jwt)
        require(status == 200 and isinstance(app, dict) and app.get("id") == APP_ID
                and app.get("slug") == APP_SLUG, "admission-app-identity")
        status, installation = send(f"/app/installations/{INSTALLATION_ID}", app_jwt)
        permissions = installation.get("permissions") if isinstance(installation, dict) else None
        require(status == 200 and isinstance(installation, dict)
                and installation.get("id") == INSTALLATION_ID
                and installation.get("app_id") == APP_ID
                and (installation.get("account") or {}).get("login") == "FS-GG"
                and installation.get("suspended_at") is None
                and installation.get("repository_selection") == "selected"
                and isinstance(permissions, dict)
                and permissions.get("contents") == "write"
                and all(level in {"read", "none"} for name, level in permissions.items()
                        if name != "contents"),
                "admission-installation-identity")
        status, minted = send(f"/app/installations/{INSTALLATION_ID}/access_tokens", app_jwt,
                              "POST", {"repository_ids": [REPOSITORY_ID],
                                       "permissions": {"contents": "write"}})
        token = minted.get("token") if status == 201 and isinstance(minted, dict) else None
        token_permissions = minted.get("permissions") if isinstance(minted, dict) else None
        require(isinstance(token, str) and bool(token)
                and isinstance(token_permissions, dict)
                and token_permissions.get("contents") == "write"
                and all(level in {"read", "none"} for name, level in token_permissions.items()
                        if name != "contents")
                and minted.get("repository_selection") == "selected", "admission-scoped-token")
        self._token = token
        status, repositories = send("/installation/repositories?per_page=100", token)
        require(status == 200 and isinstance(repositories, dict)
                and repositories.get("total_count") == 1
                and [(item.get("id"), item.get("full_name"))
                     for item in repositories.get("repositories", [])] == [(REPOSITORY_ID, REPOSITORY)],
                "admission-token-repository-scope")

    def protection_snapshot(self, read_rules=read_rules_api, observed_at: str | None = None) -> dict:
        """Read exact effective rules twice; never infer an omitted bypass list is empty."""
        prefix = f"repos/{REPOSITORY}"
        repo = read_rules(prefix + "/rulesets/" + str(WRITER_RULESET_ID))
        integrity = read_rules(prefix + "/rulesets/" + str(INTEGRITY_RULESET_ID))
        branch = "fsgg%2Fv2%2Fjournal%2Foperation%2F79"
        effective = read_rules(prefix + "/rules/branches/" + branch)
        repo_again = read_rules(prefix + "/rulesets/" + str(WRITER_RULESET_ID))
        integrity_again = read_rules(prefix + "/rulesets/" + str(INTEGRITY_RULESET_ID))
        effective_again = read_rules(prefix + "/rules/branches/" + branch)
        require(repo == repo_again and integrity == integrity_again and effective == effective_again,
                "admission-rules-moved")

        def ruleset(value, identifier, name, exclusions, types, actors):
            require(isinstance(value, dict) and value.get("id") == identifier
                    and value.get("name") == name and value.get("target") == "branch"
                    and value.get("enforcement") == "active"
                    and value.get("source_type") == "Repository"
                    and value.get("source") == REPOSITORY
                    and isinstance(value.get("conditions"), dict)
                    and value["conditions"].get("ref_name") == {
                        "include": [JOURNAL_PATTERN], "exclude": exclusions}
                    and isinstance(value.get("rules"), list)
                    and all(isinstance(item, dict) and isinstance(item.get("type"), str)
                            for item in value["rules"])
                    and sorted(item["type"] for item in value["rules"]) == sorted(types)
                    and len(value["rules"]) == len(types)
                    and "bypass_actors" in value
                    and value["bypass_actors"] == actors,
                    "admission-ruleset-binding")

        ruleset(repo, WRITER_RULESET_ID, "v2-journal-writer", [CUTOVER_REF],
                ["creation", "update"],
                [{"actor_id": APP_ID, "actor_type": "Integration", "bypass_mode": "always"}])
        ruleset(integrity, INTEGRITY_RULESET_ID, "v2-journal-integrity", [],
                ["deletion", "non_fast_forward"], [])
        require(isinstance(effective, list)
                and all(isinstance(item, dict)
                        and item.get("ruleset_source_type") == "Repository"
                        and item.get("ruleset_source") == REPOSITORY
                        and type(item.get("ruleset_id")) is int
                        and isinstance(item.get("type"), str) for item in effective)
                and sorted((item["ruleset_id"], item["type"]) for item in effective)
                == sorted([(WRITER_RULESET_ID, "creation"), (WRITER_RULESET_ID, "update"),
                           (INTEGRITY_RULESET_ID, "deletion"),
                           (INTEGRITY_RULESET_ID, "non_fast_forward")])
                and len(effective) == 4, "admission-effective-rules")
        if observed_at is None:
            observed_at = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        return {
            "schema": "fsgg.v1-admission-genesis-protection-read/1",
            "observedAt": observed_at,
            "repositoryId": REPOSITORY_ID,
            "writerRulesetId": WRITER_RULESET_ID,
            "writerRulesetActive": True,
            "writerRulesetMatchesRef": True,
            "writerBypassAppIds": [APP_ID],
            "integrityRulesetId": INTEGRITY_RULESET_ID,
            "integrityRulesetActive": True,
            "integrityRulesetMatchesRef": True,
            "integrityRejectsDeletion": True,
            "integrityRejectsNonFastForward": True,
            "integrityBypassAppIds": [],
            "credentialAppId": APP_ID,
            "credentialInstallationId": INSTALLATION_ID,
            "credentialRepositoryIds": [REPOSITORY_ID],
            "credentialContentsWrite": True,
            "credentialHasOtherWritePermissions": False,
        }

    def read_ref(self, name: str) -> str | None:
        require(name == OPERATION_REF, "admission-ref-scope")
        suffix = urllib.parse.quote(name.removeprefix("refs/"), safe="/")
        status, value = self._send(f"/repos/{REPOSITORY}/git/ref/{suffix}", self._token)
        if status == 404:
            return None
        oid = (value.get("object") or {}).get("sha") if status == 200 and isinstance(value, dict) else None
        require(isinstance(oid, str) and OID.fullmatch(oid) is not None, "admission-ref-read")
        return oid

    def put_object(self, kind: str, expected_oid: str, raw: bytes) -> str:
        require(isinstance(raw, bytes) and len(raw) <= 8192 and git_oid(kind, raw) == expected_oid,
                "admission-object-binding")
        if kind == "blob":
            suffix = "blobs"
            body = {"content": base64.b64encode(raw).decode("ascii"), "encoding": "base64"}
        elif kind == "tree":
            suffix = "trees"
            body = {"tree": _tree_body(raw)}
        else:
            suffix = "commits"
            body = _commit_body(raw)
        status, value = self._send(f"/repos/{REPOSITORY}/git/{suffix}", self._token, "POST", body)
        require(status == 201 and isinstance(value, dict) and value.get("sha") == expected_oid,
                "admission-object-write-indeterminate")
        return expected_oid

    def read_object(self, kind: str, expected_oid: str) -> bytes:
        require(kind in {"blob", "tree", "commit"} and OID.fullmatch(expected_oid) is not None,
                "admission-object-scope")
        suffix = {"blob": "blobs", "tree": "trees", "commit": "commits"}[kind]
        status, value = self._send(f"/repos/{REPOSITORY}/git/{suffix}/{expected_oid}", self._token)
        require(status == 200 and isinstance(value, dict) and value.get("sha") == expected_oid,
                "admission-object-read")
        if kind == "blob":
            require(value.get("encoding") == "base64" and isinstance(value.get("content"), str),
                    "admission-blob-read")
            try:
                raw = base64.b64decode(value["content"].replace("\n", ""), validate=True)
            except ValueError as error:
                raise Refused("admission-blob-read") from error
        elif kind == "tree":
            entries = value.get("tree")
            require(value.get("truncated") is False and isinstance(entries, list)
                    and [(item.get("path"), item.get("mode"), item.get("type")) for item in entries]
                    == [("event.json", "100644", "blob"), ("head.json", "100644", "blob")],
                    "admission-tree-read")
            require(all(isinstance(item.get("sha"), str) and OID.fullmatch(item["sha"]) is not None
                        for item in entries), "admission-tree-read")
            raw = b"".join(b"100644 " + item["path"].encode("ascii") + b"\0"
                           + bytes.fromhex(item["sha"]) for item in entries)
        else:
            parents = value.get("parents")
            tree = (value.get("tree") or {}).get("sha")
            author, committer = value.get("author"), value.get("committer")
            message = value.get("message")
            require(parents == [] and isinstance(tree, str) and OID.fullmatch(tree) is not None
                    and isinstance(author, dict) and isinstance(committer, dict)
                    and isinstance(message, str), "admission-commit-read")
            def person(name: str, entry: dict) -> bytes:
                require(entry.get("name") == "FS.GG Coordination"
                        and entry.get("email") == "coordination@fs.gg"
                        and isinstance(entry.get("date"), str), "admission-commit-person-read")
                try:
                    stamp = dt.datetime.fromisoformat(entry["date"].replace("Z", "+00:00"))
                except ValueError as error:
                    raise Refused("admission-commit-person-read") from error
                require(stamp.tzinfo is not None and stamp.utcoffset() == dt.timedelta(),
                        "admission-commit-person-read")
                return f"{name} FS.GG Coordination <coordination@fs.gg> {int(stamp.timestamp())} +0000\n".encode()
            raw = (f"tree {tree}\n".encode() + person("author", author) + person("committer", committer)
                   + b"\n" + message.rstrip("\n").encode("utf-8") + b"\n")
        require(len(raw) <= 8192 and git_oid(kind, raw) == expected_oid,
                "admission-object-readback-mismatch")
        return raw

    def create_ref_expected_absent(self, name: str, commit_oid: str) -> None:
        require(name == OPERATION_REF and OID.fullmatch(commit_oid) is not None,
                "admission-ref-create-scope")
        require(self.read_ref(name) is None, "admission-ref-not-absent")
        try:
            status, _ = self._send(f"/repos/{REPOSITORY}/git/refs", self._token, "POST",
                                   {"ref": name, "sha": commit_oid})
        except Refused:
            status = None
        observed = self.read_ref(name)
        if observed == commit_oid:
            return
        if observed is not None:
            raise Refused("admission-competing-ref")
        raise Refused("admission-ref-create-indeterminate" if status is None else "admission-ref-create-refused")
