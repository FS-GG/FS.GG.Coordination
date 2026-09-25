#!/usr/bin/env python3
"""Controlled poststate proof for a prospective isolated-operation revision.

This is a new, inactive operator identity. Its injected runtime can exercise
one controlled request but has no credential or native HTTP implementation.
The CLI uses a local transcript only. The original V2-CALL-01.4b operator and
accepted GS2-09.9 evidence remain immutable. A future governed contract,
durable attempt fence, and complete native reader must bind these predicates
before any effect is authorized.
"""

from __future__ import annotations

import argparse
import dataclasses
import hashlib
import http.client
import json
import math
import os
import pathlib
import re
import sqlite3
import stat
import sys
import urllib.parse
from typing import Callable

OPERATION_IDENTITY = "v2-call-01-4b-isolated-native-v2-provisional"
HISTORICAL_IDENTITY = "v2-call-01-4b-isolated-native-v1"
HISTORICAL_PREFLIGHT = "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
CONTROLS = "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
SOURCE = "eng/callable-cli-isolated-operation-v2.py"
ROOT = pathlib.Path(__file__).resolve().parents[1]
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
BRANCH = re.compile(r"[A-Za-z0-9._/-]+\Z")


class Refused(Exception):
    """A fixed, public refusal reason with no provider exception attached."""


class OfflineMismatch(Exception):
    """A controlled transcript did not match the requested operation."""


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise Refused("json-duplicate-member")
        result[key] = value
    return result


def _finite_constant(_value: str):
    raise Refused("json-nonfinite")


def _finite_float(raw: str) -> float:
    value = float(raw)
    if not math.isfinite(value):
        raise Refused("json-nonfinite")
    return value


def _strict_json(raw: bytes):
    return json.loads(raw, object_pairs_hook=_unique_object,
                      parse_constant=_finite_constant, parse_float=_finite_float)


@dataclasses.dataclass(frozen=True)
class ExpectedPull:
    operation_identity: str
    write_attempts: int
    repository_id: int
    repository: str
    source_ref: str
    source_sha: str
    base_ref: str
    base_sha: str


@dataclasses.dataclass(frozen=True)
class PullCensus:
    complete: bool
    repository_id: int
    source_branch_sha: str
    base_branch_sha: str
    pulls: tuple[dict, ...]
    transcript_sha256: str


@dataclasses.dataclass(frozen=True)
class ExactPull:
    number: int
    node_id: str
    census_sha256: str


@dataclasses.dataclass(frozen=True)
class ExpectedProtection:
    operation_identity: str
    write_attempts: int
    repository_id: int
    repository: str
    branch: str
    branch_sha: str
    check_context: str
    check_app_id: int


@dataclasses.dataclass(frozen=True)
class ProtectionReadback:
    complete: bool
    repository_id: int
    branch: str
    branch_sha: str
    protected: bool
    policy: dict
    transcript_sha256: str


@dataclasses.dataclass(frozen=True)
class ExactProtection:
    policy_sha256: str


@dataclasses.dataclass(frozen=True)
class Unknown:
    reason: str


@dataclasses.dataclass(frozen=True)
class HttpResponse:
    status: int
    headers: tuple[tuple[str, str], ...]
    body: bytes


class LoopbackHttpTransport:
    """Concrete HTTP parser limited to a literal IPv4 loopback endpoint.

    There is no token source, redirect handling, proxy support, or live-host
    fallback. A future installed provider adapter needs its own authority.
    """

    def __init__(self, base_url: str):
        if type(base_url) is not str:
            raise Refused("loopback-origin-invalid")
        try:
            parsed = urllib.parse.urlsplit(base_url)
            port = parsed.port
        except ValueError:
            raise Refused("loopback-origin-invalid") from None
        if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1"
                or type(port) is not int or not 1 <= port <= 65535
                or parsed.netloc != f"127.0.0.1:{port}"
                or parsed.path or parsed.query or parsed.fragment):
            raise Refused("loopback-origin-invalid")
        self.port = port

    def request(self, method: str, path: str, body: object = None) -> HttpResponse:
        if (type(method) is not str or method not in {"GET", "POST", "PUT"}
                or type(path) is not str
                or not path.startswith("repos/") or "#" in path
                or any(segment in {"", ".", ".."} for segment in path.split("?")[0].split("/"))
                or (method == "GET" and body is not None)
                or (method != "GET" and type(body) is not dict)):
            raise Refused("loopback-request-invalid")
        try:
            payload = None if body is None else json.dumps(
                body, sort_keys=True, separators=(",", ":"),
                ensure_ascii=True, allow_nan=False).encode("ascii")
            headers = {"Accept": "application/vnd.github+json",
                       "User-Agent": "fsgg-isolated-v2-loopback"}
            if payload is not None:
                headers["Content-Type"] = "application/json"
            connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
            try:
                connection.request(method, "/" + path, body=payload, headers=headers)
                response = connection.getresponse()
                raw = response.read(4_000_001)
                if len(raw) > 4_000_000:
                    raise Refused("loopback-response-too-large")
                return HttpResponse(response.status, tuple(response.getheaders()), raw)
            finally:
                connection.close()
        except Refused:
            raise
        except Exception:
            raise Refused("loopback-transport-unavailable") from None


class NativeReadAdapter:
    """Derive complete readbacks from injected raw REST responses.

    The injected transport is the only I/O surface. This class contains no
    HTTP client, token, credential lookup, or automatic retry.
    """

    def __init__(self, transport: object):
        self.transport = transport
        self.transcript: list[dict] = []

    def _get(self, path: str, paginated: bool = False) -> tuple[int, str, object]:
        response = self.transport.request("GET", path, None)
        if (type(response) is not HttpResponse or type(response.status) is not int
                or type(response.headers) is not tuple or type(response.body) is not bytes
                or len(response.body) > 4_000_000):
            raise Refused("native-response-shape")
        if any(type(pair) is not tuple or len(pair) != 2
               or any(type(item) is not str for item in pair)
               for pair in response.headers):
            raise Refused("native-header-shape")
        links = [(key, value) for key, value in response.headers
                 if key.lower() == "link"]
        if len(links) > 1:
            raise Refused("native-link-shape")
        link = links[0][1] if links else ""
        if link and not paginated:
            raise Refused("native-singleton-pagination")
        try:
            body = _strict_json(response.body)
        except (UnicodeError, ValueError):
            raise Refused("native-json-invalid") from None
        self.transcript.append({"path": path, "status": response.status,
                                "link": link, "bodySha256": _sha(response.body)})
        return response.status, link, body

    def _repo(self, repository: str, repository_id: int) -> str | None:
        if not _safe_repository(repository):
            raise Refused("native-repository-mismatch")
        owner, name = repository.split("/")
        status, _, body = self._get(f"repos/{repository}")
        if (status != 200 or type(body) is not dict
                or type(body.get("id")) is not int
                or body["id"] != repository_id
                or body.get("full_name") != repository
                or ("name" in body and body["name"] != name)
                or ("owner" in body and
                    (type(body["owner"]) is not dict
                     or body["owner"].get("login") != owner))
                or ("url" in body and
                    (type(body["url"]) is not str or body["url"] !=
                     f"https://api.github.com/repos/{repository}"))):
            raise Refused("native-repository-mismatch")
        node_id = body.get("node_id")
        if "node_id" in body and (type(node_id) is not str or not node_id):
            raise Refused("native-repository-node-invalid")
        return node_id

    def _ref(self, repository: str, ref: str) -> str:
        branch = ref.removeprefix("refs/heads/")
        encoded = urllib.parse.quote(branch, safe="/")
        path = f"repos/{repository}/git/ref/heads/{encoded}"
        status, _, body = self._get(path)
        obj = body.get("object") if type(body) is dict else None
        sha = obj.get("sha") if type(obj) is dict else None
        if (status != 200 or type(body) is not dict or body.get("ref") != ref
                or not _oid(sha)
                or body.get("url") !=
                f"https://api.github.com/repos/{repository}/git/refs/heads/{encoded}"
                or obj.get("type") != "commit"
                or obj.get("url") !=
                f"https://api.github.com/repos/{repository}/git/commits/{sha}"):
            raise Refused("native-ref-mismatch")
        return sha

    @staticmethod
    def _links(header: str, path_prefix: str) -> dict[str, int]:
        if not header:
            return {}
        result: dict[str, int] = {}
        for part in header.split(","):
            match = re.fullmatch(r'\s*<([^<>]+)>;\s*rel="(next|last|prev|first)"\s*', part)
            if match is None or match.group(2) in result:
                raise Refused("native-link-invalid")
            parsed = urllib.parse.urlparse(match.group(1))
            query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)
            if (parsed.scheme != "https" or parsed.netloc != "api.github.com"
                    or parsed.path != "/" + path_prefix or parsed.fragment
                    or set(query) != {"state", "per_page", "page"}
                    or query["state"] != ["open"] or query["per_page"] != ["100"]
                    or len(query["page"]) != 1 or not query["page"][0].isdigit()):
                raise Refused("native-link-target")
            page = int(query["page"][0])
            if page < 1 or page > 51:
                raise Refused("native-link-page")
            result[match.group(2)] = page
        return result

    def _open_pulls(self, repository: str) -> list[dict]:
        prefix = f"repos/{repository}/pulls"
        collected: list[dict] = []
        page = 1
        advertised_last: int | None = None
        while page <= 50:
            path = f"{prefix}?state=open&per_page=100&page={page}"
            status, header, body = self._get(path, paginated=True)
            if status != 200 or type(body) is not list or len(body) > 100:
                raise Refused("native-pull-page-invalid")
            links = self._links(header, prefix)
            if (("first" in links and links["first"] != 1)
                    or ("prev" in links and
                        (page == 1 or links["prev"] != page - 1))):
                raise Refused("native-pull-link-direction")
            if "last" in links:
                if advertised_last is not None and links["last"] != advertised_last:
                    raise Refused("native-pull-last-drift")
                advertised_last = links["last"]
            if links.get("next") is not None:
                if (links["next"] != page + 1
                        or ("last" in links and links["last"] < page + 1)
                        or not body):
                    raise Refused("native-pull-continuation-invalid")
                collected.extend(body)
                page += 1
                continue
            if (advertised_last is not None and advertised_last != page) or (page > 1 and not body):
                raise Refused("native-pull-terminal-contradiction")
            collected.extend(body)
            terminal = f"{prefix}?state=open&per_page=100&page={page + 1}"
            terminal_status, terminal_header, terminal_body = self._get(
                terminal, paginated=True)
            terminal_links = self._links(terminal_header, prefix)
            if (terminal_status != 200 or terminal_body != []
                    or "next" in terminal_links
                    or ("first" in terminal_links and terminal_links["first"] != 1)
                    or ("last" in terminal_links
                        and terminal_links["last"] != page)
                    or ("prev" in terminal_links
                        and terminal_links["prev"] != page)):
                raise Refused("native-pull-terminal-unproved")
            return collected
        raise Refused("native-pull-page-limit")

    def read_pull_census(self, expected: ExpectedPull) -> PullCensus:
        if not _valid_pull(expected):
            raise Refused("native-pull-identity-invalid")
        expected = dataclasses.replace(expected)
        self.transcript = []
        repository_node = self._repo(expected.repository, expected.repository_id)
        source_sha = self._ref(expected.repository, expected.source_ref)
        base_sha = self._ref(expected.repository, expected.base_ref)
        listed = self._open_pulls(expected.repository)
        selected: list[dict] = []
        seen: set[int] = set()
        for item in listed:
            number = item.get("number") if type(item) is dict else None
            if type(number) is not int or number <= 0 or number in seen:
                raise Refused("native-pull-list-identity")
            seen.add(number)
            status, _, detail = self._get(f"repos/{expected.repository}/pulls/{number}")
            if (status != 200 or type(detail) is not dict
                    or detail.get("number") != number):
                raise Refused("native-pull-detail-identity")
            for row in (item, detail):
                if (row.get("state") != "open"
                        or type(row.get("draft")) is not bool
                        or ("merged" in row and row["merged"] is not False)
                        or ("merged_at" in row and row["merged_at"] is not None)):
                    raise Refused("native-open-pull-row-state")
            if (("url" in item) != ("url" in detail)
                    or ("url" in detail and
                        (type(detail["url"]) is not str
                         or item["url"] != detail["url"]
                         or detail["url"] != _pull_url(expected.repository, number)))):
                raise Refused("native-pull-list-detail-url-drift")
            if (type(item.get("node_id")) is not str
                    or item["node_id"] != detail.get("node_id")):
                raise Refused("native-pull-list-detail-node-drift")
            for field in ("state", "draft", "title", "body"):
                if (field not in item or field not in detail
                        or _digest(item[field]) != _digest(detail[field])):
                    raise Refused("native-pull-list-detail-field-drift")
            for side in ("head", "base"):
                listed_side = item.get(side)
                detail_side = detail.get(side)
                if (type(listed_side) is not dict or type(detail_side) is not dict
                        or listed_side.get("sha") != detail_side.get("sha")
                        or listed_side.get("ref") != detail_side.get("ref")):
                    raise Refused("native-pull-list-detail-drift")
                listed_repo = listed_side.get("repo")
                detail_repo = detail_side.get("repo")
                if (not _repo_object_consistent(listed_repo)
                        or not _repo_object_consistent(detail_repo)
                        or listed_repo.get("id") != detail_repo.get("id")
                        or listed_repo.get("full_name") != detail_repo.get("full_name")
                        or ("url" in listed_repo) != ("url" in detail_repo)
                        or ("url" in listed_repo and
                            (type(listed_repo["url"]) is not str
                             or listed_repo["url"] != detail_repo["url"]))):
                    raise Refused("native-pull-list-detail-repo-drift")
            if detail.get("body") == pull_request_body(expected)["body"]:
                selected.append(detail)
        if self._repo(expected.repository, expected.repository_id) != repository_node:
            raise Refused("native-repository-terminal-node-drift")
        if (self._ref(expected.repository, expected.source_ref) != source_sha
                or self._ref(expected.repository, expected.base_ref) != base_sha):
            raise Refused("native-pull-terminal-ref-drift")
        return PullCensus(True, expected.repository_id, source_sha, base_sha,
                          tuple(selected), _digest(self.transcript))

    def read_protection(self, expected: ExpectedProtection) -> ProtectionReadback:
        if not _valid_protection(expected):
            raise Refused("native-protection-identity-invalid")
        expected = dataclasses.replace(expected)
        self.transcript = []
        repository_node = self._repo(expected.repository, expected.repository_id)
        branch = expected.branch
        sha = self._ref(expected.repository, f"refs/heads/{branch}")
        branch_path = f"repos/{expected.repository}/branches/{urllib.parse.quote(branch, safe='/')}"
        status, _, branch_body = self._get(branch_path)
        commit = branch_body.get("commit") if type(branch_body) is dict else None
        if (status != 200 or type(branch_body) is not dict
                or branch_body.get("name") != branch
                or type(commit) is not dict or commit.get("sha") != sha
                or not _branch_commit_url_matches(commit, expected.repository, sha)
                or type(branch_body.get("protected")) is not bool
                or not _branch_protection_url_matches(branch_body, expected)):
            raise Refused("native-branch-identity")
        status, _, policy = self._get(f"{branch_path}/protection")
        protected = branch_body["protected"]
        if ((protected and (status != 200 or type(policy) is not dict))
                or (not protected and status != 404)):
            raise Refused("native-protection-status")
        if protected and not _protection_urls_match(policy, expected):
            raise Refused("native-protection-target")
        if self._repo(expected.repository, expected.repository_id) != repository_node:
            raise Refused("native-repository-terminal-node-drift")
        if self._ref(expected.repository, f"refs/heads/{branch}") != sha:
            raise Refused("native-protection-terminal-ref-drift")
        terminal_status, _, terminal_branch = self._get(branch_path)
        terminal_commit = terminal_branch.get("commit") if type(terminal_branch) is dict else None
        if (terminal_status != 200 or type(terminal_branch) is not dict
                or terminal_branch.get("name") != branch
                or type(terminal_commit) is not dict
                or terminal_commit.get("sha") != sha
                or not _branch_commit_url_matches(
                    terminal_commit, expected.repository, sha)
                or terminal_branch.get("protected") is not protected
                or not _branch_protection_url_matches(terminal_branch, expected)):
            raise Refused("native-protection-terminal-branch-drift")
        terminal_policy_status, _, terminal_policy = self._get(f"{branch_path}/protection")
        if (terminal_policy_status != status
                or _digest(terminal_policy) != _digest(policy)):
            raise Refused("native-protection-terminal-policy-drift")
        return ProtectionReadback(True, expected.repository_id, branch, sha,
                                  protected, policy if protected else {},
                                  _digest(self.transcript))


PULL_TITLE = "V2-CALL-01.4b synthetic delivery v2"


def _valid_pull(expected: object) -> bool:
    return (type(expected) is ExpectedPull and _shape(expected)
            and _safe_repository(expected.repository)
            and type(expected.source_ref) is str
            and expected.source_ref.startswith("refs/heads/")
            and _safe_branch(expected.source_ref.removeprefix("refs/heads/"))
            and type(expected.base_ref) is str
            and expected.base_ref.startswith("refs/heads/")
            and _safe_branch(expected.base_ref.removeprefix("refs/heads/"))
            and expected.source_ref != expected.base_ref
            and _oid(expected.source_sha) and _oid(expected.base_sha))


def _valid_protection(expected: object) -> bool:
    return (type(expected) is ExpectedProtection and _shape(expected)
            and _safe_repository(expected.repository)
            and _safe_branch(expected.branch)
            and _oid(expected.branch_sha)
            and type(expected.check_context) is str and bool(expected.check_context)
            and type(expected.check_app_id) is int and expected.check_app_id > 0)


def pull_request_body(expected: ExpectedPull) -> dict:
    """A deterministic request marker binds an exact poststate to this intent."""
    core = dataclasses.asdict(expected)
    marker = _digest({"effect": "create-pull", "intent": core})
    return {"title": PULL_TITLE,
            "head": expected.source_ref.removeprefix("refs/heads/"),
            "base": expected.base_ref.removeprefix("refs/heads/"),
            "body": f"FS-GG-Effect: {marker}"}


def protection_body(expected: ExpectedProtection) -> dict:
    return {
        "required_status_checks": {"strict": True, "checks": [
            {"context": expected.check_context, "app_id": expected.check_app_id}]},
        "enforce_admins": False,
        "required_pull_request_reviews": None,
        "restrictions": None,
        "allow_force_pushes": False,
        "allow_deletions": False,
    }


def _shape(expected) -> bool:
    return (expected.operation_identity == OPERATION_IDENTITY
            and type(expected.write_attempts) is int
            and expected.write_attempts == 1
            and type(expected.repository_id) is int
            and expected.repository_id > 0)


def _safe_branch(value: object) -> bool:
    return (type(value) is str and BRANCH.fullmatch(value) is not None
            and all(segment not in {"", ".", ".."} for segment in value.split("/")))


def _safe_repository(value: object) -> bool:
    return (type(value) is str and REPOSITORY.fullmatch(value) is not None
            and all(segment not in {".", ".."} for segment in value.split("/")))


def _oid(value: object) -> bool:
    return type(value) is str and HEX40.fullmatch(value) is not None


def _digest(value: object) -> str:
    raw = json.dumps(value, sort_keys=True, separators=(",", ":"),
                     ensure_ascii=True, allow_nan=False).encode("ascii")
    return hashlib.sha256(raw).hexdigest()


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _read_object(path: str) -> tuple[dict, bytes]:
    try:
        raw = pathlib.Path(path).read_bytes()
        value = _strict_json(raw)
    except (OSError, UnicodeError, ValueError):
        raise Refused("inspect-input-unavailable") from None
    if type(value) is not dict:
        raise Refused("inspect-input-shape")
    return value, raw


def _sealed(value: dict, key: str) -> bool:
    supplied = value.get(key)
    return (_complete_digest(supplied)
            and supplied == _digest({name: part for name, part in value.items()
                                     if name != key}))


def inspect(contract_path: str, proposal_path: str, preflight_path: str) -> dict:
    """Inspect fixed historical evidence; this cannot authorize an effect."""
    contract, _ = _read_object(contract_path)
    proposal, _ = _read_object(proposal_path)
    preflight, preflight_raw = _read_object(preflight_path)
    if (contract.get("schema") != "fsgg.coordination.callable-isolated-operation-contract/5"
            or contract.get("identity") != OPERATION_IDENTITY
            or contract.get("state") != "prepared-not-authorized"
            or contract.get("authorized") is not False
            or not _sealed(contract, "contractSha256")):
        raise Refused("inspect-contract-invalid")
    source = contract.get("source")
    controls = contract.get("qualificationControls")
    if (type(source) is not dict or source.get("operationSource") != SOURCE
            or not _complete_digest(source.get("operationSourceSha256"))
            or type(controls) is not dict or controls.get("path") != CONTROLS
            or not _complete_digest(controls.get("sha256"))):
        raise Refused("inspect-source-binding")
    try:
        source_sha = _sha((ROOT / SOURCE).read_bytes())
        controls_sha = _sha((ROOT / CONTROLS).read_bytes())
    except OSError:
        raise Refused("inspect-source-unavailable") from None
    if (source_sha != source["operationSourceSha256"]
            or controls_sha != controls["sha256"]):
        raise Refused("inspect-source-drift")
    historical = contract.get("historicalPreflight")
    if (type(historical) is not dict
            or historical.get("path") != HISTORICAL_PREFLIGHT
            or not _complete_digest(historical.get("sha256"))
            or historical.get("operationIdentity") != HISTORICAL_IDENTITY
            or historical.get("authority") != "historical-observation-only"
            or historical["sha256"] != _sha(preflight_raw)
            or preflight.get("schema") != "fsgg.coordination.callable-isolated-operation-preflight/1"
            or preflight.get("operationIdentity") != HISTORICAL_IDENTITY
            or preflight.get("authorized") is not False
            or preflight.get("disposition") != "refused-no-compatible-admitted-target"
            or not _sealed(preflight, "evidenceSha256")):
        raise Refused("inspect-historical-preflight-invalid")
    if (proposal.get("schema") != "fsgg.coordination.callable-isolated-operation-proposal/5"
            or proposal.get("identity") != OPERATION_IDENTITY
            or proposal.get("state") != "prepared-not-authorized"
            or proposal.get("authorized") is not False
            or not _sealed(proposal, "proposalSha256")
            or proposal.get("historicalPreflight") != historical):
        raise Refused("inspect-proposal-invalid")
    binding = proposal.get("contract")
    if (type(binding) is not dict
            or binding.get("sha256") != contract["contractSha256"]
            or binding.get("operationSourceSha256") != source["operationSourceSha256"]):
        raise Refused("inspect-proposal-binding")
    return {
        "schema": "fsgg.coordination.callable-isolated-operation-inspection/2",
        "operationIdentity": OPERATION_IDENTITY,
        "contractSha256": contract["contractSha256"],
        "state": "prepared-not-authorized",
        "authorized": False,
        "disposition": "refused-no-compatible-admitted-target",
        "historicalObservationOnly": True,
        "liveEffects": 0,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--contract", required=True)
    parser.add_argument("--proposal", required=True)
    parser.add_argument("--preflight", required=True)
    parser.add_argument("--scenario")
    parser.add_argument("--journal")
    parser.add_argument("action", choices=["inspect", "exercise-offline"])
    args = parser.parse_args(argv)
    try:
        inspection = inspect(args.contract, args.proposal, args.preflight)
        if args.action == "inspect":
            result = inspection
        else:
            if not args.scenario or not args.journal:
                raise Refused("offline-scenario-and-journal-required")
            scenario, _ = _read_object(args.scenario)
            result = exercise_offline(scenario, SqliteAttemptFence(args.journal).reserve_once)
    except Refused as error:
        print(str(error), file=sys.stderr)
        return 2
    except Exception:
        print("inspect-unavailable", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


def _complete_digest(value: object) -> bool:
    return type(value) is str and HEX64.fullmatch(value) is not None


def _same_repo(value: object, expected: ExpectedPull) -> bool:
    return (_repo_object_consistent(value)
            and value["id"] == expected.repository_id
            and value["full_name"] == expected.repository)


def _repo_object_consistent(value: object) -> bool:
    if type(value) is not dict or type(value.get("id")) is not int or value["id"] <= 0:
        return False
    repository = value.get("full_name")
    if not _safe_repository(repository):
        return False
    owner, name = repository.split("/")
    return (("name" not in value or value["name"] == name)
            and ("owner" not in value or
                 (type(value["owner"]) is dict
                  and value["owner"].get("login") == owner))
            and ("url" not in value or
                 (type(value["url"]) is str and value["url"] ==
                  f"https://api.github.com/repos/{repository}")))


def _pull_url(repository: str, number: int) -> str:
    return f"https://api.github.com/repos/{repository}/pulls/{number}"


def _two(read: Callable[[], object]):
    """An injected native reader supplies complete evidence; no write occurs."""
    try:
        first = read()
        first_snapshot = _digest(dataclasses.asdict(first))
        second = read()
        second_snapshot = _digest(dataclasses.asdict(second))
        if first_snapshot != second_snapshot:
            return None
        return second
    except Exception:
        # Never attach raw provider exceptions, response bodies, or credentials.
        return None


def _response_allows_readback(value: object) -> bool:
    """Only a 2xx, ambiguous 5xx/loss, or absent response may be reconciled."""
    if value is None:
        return True
    if type(value) is HttpResponse:
        status = value.status
    elif type(value) is dict:
        status = value.get("status")
    else:
        return False
    return ((type(status) is int and
             (200 <= status < 300 or 500 <= status < 600))
            or (type(status) is str and status in {"unknown", "lost"}))


def _pull_success_response_matches(value: object, expected: ExpectedPull,
                                   pull: dict) -> bool:
    """A successful POST must identify the same PR as the native readback."""
    if value is None:
        return True
    if type(value) is HttpResponse:
        if not 200 <= value.status < 300:
            return True
        headers = value.headers
        try:
            body = _strict_json(value.body)
        except (UnicodeError, ValueError, Refused):
            return False
    elif type(value) is dict:
        if type(value.get("status")) is not int or not 200 <= value["status"] < 300:
            return True
        headers = value.get("headers", ())
        body = value.get("body")
    else:
        return False
    if type(headers) is not tuple or any(
            type(pair) is not tuple or len(pair) != 2
            or type(pair[0]) is not str or type(pair[1]) is not str
            for pair in headers):
        return False
    locations = [part for name, part in headers if name.lower() == "location"]
    if len(locations) > 1 or (locations and locations[0] !=
                              _pull_url(expected.repository, pull["number"])):
        return False
    if type(body) is not dict:
        return False
    head = body.get("head")
    base = body.get("base")
    return (type(body.get("number")) is int
            and body["number"] == pull["number"]
            and type(body.get("node_id")) is str
            and body["node_id"] == pull["node_id"]
            and type(body.get("url")) is str
            and body["url"] == _pull_url(expected.repository, pull["number"])
            and body.get("state") == "open"
            and body.get("draft") is False
            and body.get("title") == PULL_TITLE
            and body.get("body") == pull_request_body(expected)["body"]
            and type(head) is dict and type(base) is dict
            and head.get("ref") == expected.source_ref.removeprefix("refs/heads/")
            and head.get("sha") == expected.source_sha
            and _same_repo(head.get("repo"), expected)
            and base.get("ref") == expected.base_ref.removeprefix("refs/heads/")
            and base.get("sha") == expected.base_sha
            and _same_repo(base.get("repo"), expected))


def classify_pull_after_one_attempt(
        expected: ExpectedPull,
        read: Callable[[], PullCensus],
        provider_response: object = None) -> ExactPull | Unknown:
    """Require one complete, coherent and exact PR poststate after attempt one.

    A caller must durably prove its original one-attempt count and supply a
    qualified complete native reader. Explicit refusals cannot be reconciled.
    """
    try:
        if not _response_allows_readback(provider_response):
            return Unknown("pull-request-provider-response-ineligible")
        if not _valid_pull(expected):
            return Unknown("pull-request-identity-invalid")
        expected = dataclasses.replace(expected)
        observed = _two(read)
        if (type(observed) is not PullCensus or observed.complete is not True
                or type(observed.repository_id) is not int
                or observed.repository_id != expected.repository_id
                or observed.source_branch_sha != expected.source_sha
                or observed.base_branch_sha != expected.base_sha
                or type(observed.pulls) is not tuple
                or len(observed.pulls) != 1
                or not _complete_digest(observed.transcript_sha256)):
            return Unknown("pull-request-readback-incomplete")
        pull = observed.pulls[0]
        head = pull.get("head") if type(pull) is dict else None
        base = pull.get("base") if type(pull) is dict else None
        if (type(head) is not dict or type(base) is not dict
                or type(pull.get("number")) is not int or pull["number"] <= 0
                or type(pull.get("node_id")) is not str or not pull["node_id"]
                or ("url" in pull and
                    (type(pull["url"]) is not str or pull["url"] !=
                     _pull_url(expected.repository, pull["number"])))
                or pull.get("state") != "open" or pull.get("draft") is not False
                or pull.get("merged") is not False
                or pull.get("title") != PULL_TITLE
                or pull.get("body") != pull_request_body(expected)["body"]
                or head.get("ref") != expected.source_ref.removeprefix("refs/heads/")
                or head.get("sha") != expected.source_sha
                or not _same_repo(head.get("repo"), expected)
                or base.get("ref") != expected.base_ref.removeprefix("refs/heads/")
                or base.get("sha") != expected.base_sha
                or not _same_repo(base.get("repo"), expected)):
            return Unknown("pull-request-readback-mismatch")
        if not _pull_success_response_matches(provider_response, expected, pull):
            return Unknown("pull-request-provider-response-mismatch")
        return ExactPull(pull["number"], pull["node_id"], observed.transcript_sha256)
    except Exception:
        return Unknown("pull-request-readback-unavailable")


def _disabled(value: object) -> bool:
    return value is False or (type(value) is dict and value == {"enabled": False})


def _protection_url(expected: ExpectedProtection) -> str:
    branch = urllib.parse.quote(expected.branch, safe="/")
    return (f"https://api.github.com/repos/{expected.repository}/"
            f"branches/{branch}/protection")


def _branch_commit_url_matches(commit: dict, repository: str, sha: str) -> bool:
    return ("url" not in commit or
            (type(commit["url"]) is str and commit["url"] ==
             f"https://api.github.com/repos/{repository}/commits/{sha}"))


def _branch_protection_url_matches(branch: object, expected: ExpectedProtection) -> bool:
    return (type(branch) is dict and
            ("protection_url" not in branch or
             (type(branch["protection_url"]) is str and
              branch["protection_url"] == _protection_url(expected))))


def _protection_urls_match(policy: object, expected: ExpectedProtection) -> bool:
    if type(policy) is not dict:
        return False
    root = _protection_url(expected)
    checks = policy.get("required_status_checks")
    if ("url" in policy and
            (type(policy["url"]) is not str or policy["url"] != root)):
        return False
    if type(checks) is dict:
        for key, suffix in (("url", "/required_status_checks"),
                            ("contexts_url", "/required_status_checks/contexts")):
            if key in checks and (type(checks[key]) is not str
                                  or checks[key] != root + suffix):
                return False
    return True


UNSELECTED_PROTECTION_FLAGS = (
    "required_signatures", "required_linear_history", "block_creations",
    "required_conversation_resolution", "lock_branch", "allow_fork_syncing",
)


def classify_protection_after_one_attempt(
        expected: ExpectedProtection,
        read: Callable[[], ProtectionReadback],
        provider_response: object = None) -> ExactProtection | Unknown:
    """Require full protection readback, including disabled force pushes."""
    try:
        if not _response_allows_readback(provider_response):
            return Unknown("branch-protection-provider-response-ineligible")
        if not _valid_protection(expected):
            return Unknown("branch-protection-identity-invalid")
        expected = dataclasses.replace(expected)
        observed = _two(read)
        if (type(observed) is not ProtectionReadback
                or observed.complete is not True
                or observed.protected is not True
                or type(observed.repository_id) is not int
                or observed.repository_id != expected.repository_id
                or observed.branch != expected.branch
                or observed.branch_sha != expected.branch_sha
                or type(observed.policy) is not dict
                or not _protection_urls_match(observed.policy, expected)
                or not _complete_digest(observed.transcript_sha256)):
            return Unknown("branch-protection-readback-incomplete")
        policy = observed.policy
        checks = policy.get("required_status_checks")
        entries = checks.get("checks") if type(checks) is dict else None
        contexts = checks.get("contexts") if type(checks) is dict else None
        if (type(checks) is not dict or checks.get("strict") is not True
                or type(entries) is not list or len(entries) != 1
                or type(entries[0]) is not dict
                or entries[0].get("context") != expected.check_context
                or type(entries[0].get("app_id")) is not int
                or entries[0]["app_id"] != expected.check_app_id
                or ("contexts" in checks and
                    (type(contexts) is not list or contexts not in
                     ([], [expected.check_context])))
                or not _disabled(policy.get("enforce_admins"))
                or "required_pull_request_reviews" not in policy
                or policy.get("required_pull_request_reviews") is not None
                or "restrictions" not in policy
                or policy.get("restrictions") is not None
                or not _disabled(policy.get("allow_force_pushes"))
                or not _disabled(policy.get("allow_deletions"))
                or any(key in policy and not _disabled(policy[key])
                       for key in UNSELECTED_PROTECTION_FLAGS)):
            return Unknown("branch-protection-readback-mismatch")
        return ExactProtection(_digest({
            "operation_identity": expected.operation_identity,
            "repository_id": expected.repository_id,
            "branch": observed.branch,
            "policy": policy,
            "transcript_sha256": observed.transcript_sha256,
        }))
    except Exception:
        return Unknown("branch-protection-readback-unavailable")


def run_pull_once(expected: ExpectedPull, transport: object,
                  reserve_once: Callable[[str], bool]) -> ExactPull | Unknown:
    """Exercise one injected POST after complete absent prestate, then reread.

    `reserve_once` must be a durable before-send fence in any future installed
    caller. This module provides no native transport or durable fence.
    """
    try:
        if not _valid_pull(expected):
            return Unknown("pull-request-identity-invalid")
        expected = dataclasses.replace(expected)
        reader = NativeReadAdapter(transport)
        before = _two(lambda: reader.read_pull_census(expected))
        if (type(before) is not PullCensus or before.complete is not True
                or before.repository_id != expected.repository_id
                or before.source_branch_sha != expected.source_sha
                or before.base_branch_sha != expected.base_sha
                or type(before.pulls) is not tuple or before.pulls
                or not _complete_digest(before.transcript_sha256)):
            return Unknown("pull-request-prestate-not-absent")
        body = pull_request_body(expected)
        key = _digest({"operation": OPERATION_IDENTITY,
                       "effect": "create-pull", "repository": expected.repository,
                       "body": body, "sourceSha": expected.source_sha,
                       "baseSha": expected.base_sha})
        if reserve_once(key) is not True:
            return Unknown("pull-request-attempt-not-reserved")
        response = None
        try:
            response = transport.request("POST", f"repos/{expected.repository}/pulls", body)
            if (type(response) is not HttpResponse or type(response.status) is not int
                    or type(response.headers) is not tuple
                    or type(response.body) is not bytes):
                return Unknown("pull-request-provider-response-invalid")
            if not (200 <= response.status < 300 or 500 <= response.status < 600):
                return Unknown("pull-request-provider-explicit-refusal")
        except OfflineMismatch:
            return Unknown("pull-request-controlled-request-mismatch")
        except Exception:
            # A lost response may conceal an applied write. Never send again.
            pass
        return classify_pull_after_one_attempt(
            expected, lambda: reader.read_pull_census(expected), response)
    except Exception:
        return Unknown("pull-request-runtime-unavailable")


def run_protection_once(expected: ExpectedProtection, transport: object,
                        reserve_once: Callable[[str], bool]) -> ExactProtection | Unknown:
    """Exercise one injected PUT after complete absent prestate, then reread."""
    try:
        if not _valid_protection(expected):
            return Unknown("branch-protection-identity-invalid")
        expected = dataclasses.replace(expected)
        reader = NativeReadAdapter(transport)
        before = _two(lambda: reader.read_protection(expected))
        if (type(before) is not ProtectionReadback or before.complete is not True
                or before.repository_id != expected.repository_id
                or before.branch != expected.branch
                or before.branch_sha != expected.branch_sha
                or before.protected is not False
                or not _complete_digest(before.transcript_sha256)):
            return Unknown("branch-protection-prestate-not-absent")
        body = protection_body(expected)
        key = _digest({"operation": OPERATION_IDENTITY,
                       "effect": "set-protection", "repositoryId": expected.repository_id,
                       "repository": expected.repository,
                       "branch": expected.branch, "body": body})
        if reserve_once(key) is not True:
            return Unknown("branch-protection-attempt-not-reserved")
        try:
            branch = urllib.parse.quote(expected.branch, safe="/")
            response = transport.request("PUT", f"repos/{expected.repository}/branches/{branch}/protection", body)
            if (type(response) is not HttpResponse or type(response.status) is not int
                    or type(response.headers) is not tuple
                    or type(response.body) is not bytes):
                return Unknown("branch-protection-provider-response-invalid")
            if not (200 <= response.status < 300 or 500 <= response.status < 600):
                return Unknown("branch-protection-provider-explicit-refusal")
        except OfflineMismatch:
            return Unknown("branch-protection-controlled-request-mismatch")
        except Exception:
            pass
        return classify_protection_after_one_attempt(
            expected, lambda: reader.read_protection(expected))
    except Exception:
        return Unknown("branch-protection-runtime-unavailable")


class OfflineTranscriptTransport:
    """Local JSON event playback; incapable of contacting a provider."""

    def __init__(self, events: object):
        if type(events) is not list:
            raise Refused("offline-events-invalid")
        self.events = iter(events)
        self.writes = 0

    def request(self, method: str, path: str, body: object = None) -> HttpResponse:
        try:
            event = next(self.events)
        except StopIteration:
            raise OfflineMismatch() from None
        if (type(event) is not dict or event.get("method") != method
                or event.get("path") != path or event.get("body") != body):
            raise OfflineMismatch()
        if method in {"POST", "PUT"}:
            self.writes += 1
        if "raise" in event:
            raise OSError(str(event["raise"])) from None
        response = event.get("response")
        if (type(response) is not dict or type(response.get("status")) is not int
                or type(response.get("headers")) is not list):
            raise OfflineMismatch()
        pairs = response["headers"]
        if any(type(pair) is not list or len(pair) != 2
               or any(type(item) is not str for item in pair)
               for pair in pairs):
            raise OfflineMismatch()
        raw = response.get("rawBody")
        if raw is None:
            raw = json.dumps(response.get("json"), sort_keys=True,
                             separators=(",", ":"))
        if type(raw) is not str:
            raise OfflineMismatch()
        return HttpResponse(response["status"], tuple(tuple(pair) for pair in pairs),
                            raw.encode())


class SqliteAttemptFence:
    """Offline persistent attempt demonstration, not an installed issuer fence."""

    def __init__(self, path: str):
        self.path = pathlib.Path(path)
        try:
            parent = self.path.parent
            parent_mode = parent.stat().st_mode
            if not stat.S_ISDIR(parent_mode) or parent_mode & 0o077:
                raise Refused("offline-journal-parent-custody")
            if self.path.is_symlink():
                raise Refused("offline-journal-symlink")
            if not self.path.exists():
                flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
                if hasattr(os, "O_NOFOLLOW"):
                    flags |= os.O_NOFOLLOW
                fd = os.open(self.path, flags, 0o600)
                os.close(fd)
                directory = os.open(parent, os.O_RDONLY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
            mode = self.path.stat().st_mode
            if not stat.S_ISREG(mode) or mode & 0o077:
                raise Refused("offline-journal-file-custody")
            with sqlite3.connect(self.path) as db:
                db.execute("PRAGMA synchronous=FULL")
                db.execute("CREATE TABLE IF NOT EXISTS attempts (request_digest TEXT PRIMARY KEY)")
        except Refused:
            raise
        except (OSError, sqlite3.Error):
            raise Refused("offline-journal-unavailable") from None

    def reserve_once(self, digest: str) -> bool:
        if not _complete_digest(digest):
            return False
        try:
            with sqlite3.connect(self.path) as db:
                db.execute("PRAGMA synchronous=FULL")
                db.execute("BEGIN IMMEDIATE")
                cursor = db.execute(
                    "INSERT OR IGNORE INTO attempts (request_digest) VALUES (?)", (digest,))
                db.commit()
                return cursor.rowcount == 1
        except (OSError, sqlite3.Error):
            return False


def exercise_offline(scenario: dict, reserve_once: Callable[[str], bool]) -> dict:
    """Run the real predicates against a controlled event transcript only."""
    if scenario.get("schema") != "fsgg.coordination.isolated-operation-offline-scenario/1":
        raise Refused("offline-scenario-schema")
    transport = OfflineTranscriptTransport(scenario.get("events"))
    try:
        if scenario.get("effect") == "create-pull":
            expected = ExpectedPull(**scenario["expected"])
            result = run_pull_once(expected, transport, reserve_once)
        elif scenario.get("effect") == "set-protection":
            expected = ExpectedProtection(**scenario["expected"])
            result = run_protection_once(expected, transport, reserve_once)
        else:
            raise Refused("offline-effect-invalid")
    except (KeyError, TypeError):
        raise Refused("offline-expected-invalid") from None
    return {
        "schema": "fsgg.coordination.isolated-operation-offline-result/1",
        "simulationOnly": True,
        "authorized": False,
        "classification": type(result).__name__,
        "reason": result.reason if type(result) is Unknown else "exact-poststate",
        "writeAttempts": transport.writes,
        "liveEffects": 0,
    }


if __name__ == "__main__":
    raise SystemExit(main())
