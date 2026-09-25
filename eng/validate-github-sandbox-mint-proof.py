#!/usr/bin/env python3
"""Check the protected sandbox mint handoff before candidate provider calls.

This checks consistency with the protected host's sanitized mint proof. It does
not grant authority to a Coordination candidate or qualify a migration run.
"""

import datetime as dt
import hashlib
import json
import os
import re
import stat
import sys


SCHEMA = "fsgg.github-substrate-v2.sandbox-mint-grants/1"
REPOSITORY = {"id": 1353050537, "nodeId": "R_kgDOUKXpqQ",
              "fullName": "FS-GG/FS.GG.GitHub.Substrate.Sandbox"}
GRANTS = {"administration": "write", "contents": "write", "issues": "write",
          "pull_requests": "write", "organization_projects": "write"}
HEX_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
MAX_BYTES = 64 * 1024


class Refused(Exception):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def unique_pairs(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate-member")
        result[key] = value
    return result


def reject_nonfinite(_value: str) -> object:
    raise Refused("nonfinite-number")


def read_proof(path: str) -> dict:
    require(bool(path), "missing-proof")
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    with os.fdopen(descriptor, "rb") as stream:
        require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), "nonregular-proof")
        raw = stream.read(MAX_BYTES + 1)
    require(0 < len(raw) <= MAX_BYTES, "proof-size")
    proof = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs,
                       parse_constant=reject_nonfinite)
    require(type(proof) is dict, "proof-shape")
    return proof


def verify(proof: dict, token: str, now: dt.datetime) -> None:
    require(type(token) is str and len(token) > 20 and token.isascii()
            and not any(character.isspace() for character in token), "missing-token")
    require(proof.get("schema") == SCHEMA, "schema")
    require(type(proof.get("appId")) is int and proof["appId"] == 4166418,
            "app-id")
    require(proof.get("appSlug") == "fs-gg-cross-repo-dispatch", "app-slug")
    require(proof.get("actor") == {"login": "fs-gg-cross-repo-dispatch[bot]",
                                   "databaseId": 297630107}, "actor")
    require(type(proof.get("installationId")) is int
            and proof["installationId"] > 0, "installation-id")
    require(proof.get("repositorySelection") == "selected", "selection")
    require(proof.get("repository") == REPOSITORY, "repository")
    grants = proof.get("permissions")
    require(type(grants) is dict and all(grants.get(name) == level
                                        for name, level in GRANTS.items()), "grants")
    require(all(type(name) is str and type(level) is str
                and (level != "write" or name in GRANTS)
                for name, level in grants.items()), "extra-grant")
    for name in ("mintResponseSha256", "viewerResponseSha256", "tokenSha256"):
        require(type(proof.get(name)) is str and HEX_SHA256.fullmatch(proof[name]),
                name)
    digest = hashlib.sha256(token.encode("ascii")).hexdigest()
    require(digest == proof["tokenSha256"], "token-digest")
    expiry_text = proof.get("expiresAt")
    require(type(expiry_text) is str and expiry_text.endswith("Z"), "expiry")
    try:
        expiry = dt.datetime.fromisoformat(expiry_text[:-1] + "+00:00")
    except ValueError:
        raise Refused("expiry") from None
    require(now < expiry <= now + dt.timedelta(hours=2), "expiry-window")


def main() -> int:
    try:
        proof = read_proof(os.environ.get("FSGG_SANDBOX_MINT_PROOF", ""))
        verify(proof, os.environ.get("FSGG_SANDBOX_TOKEN", ""),
               dt.datetime.now(dt.timezone.utc))
    except Refused as error:
        print(f"GSQ-MINT-PROOF: refused {error}", file=sys.stderr)
        return 1
    except (OSError, UnicodeError, ValueError, TypeError, OverflowError,
            RecursionError):
        print("GSQ-MINT-PROOF: refused unreadable-or-malformed", file=sys.stderr)
        return 1
    print("GSQ-MINT-PROOF: handoff consistent")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
