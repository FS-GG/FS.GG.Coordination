"""Pure verifier for a future protected GS2-09.7 run-binding envelope.

The protected workflow does not yet emit this signature or install its trust
anchor. This module is a source contract, not a live authorization route.
"""

import base64
import binascii
import datetime as dt
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path


SCHEMA = "fsgg.github-substrate-v2.sandbox-run-binding/1"
AUDIENCE = "FS-GG/FS.GG.Coordination:gs2-09-7"
WORKFLOW = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
ENVIRONMENT = "github-substrate-v2-sandbox"
REPOSITORY = "FS-GG/.github"
SANDBOX_ID = 1353050537
SANDBOX_NODE = "R_kgDOUKXpqQ"
PROJECT_NODE = "PVT_kwDOEYAWY84BiESo"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
BINDING_FIELDS = {
    "audience", "workflowRepository", "workflowPath", "environment",
    "workflowSha", "runId", "runAttempt", "candidateSha", "runNonce",
    "sandboxRepositoryId", "sandboxRepositoryNodeId", "projectNodeId",
    "proofSha256", "tokenSha256", "expiresAt",
}


class Refused(Exception):
    pass


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def unique_pairs(pairs: list[tuple[str, object]]) -> dict:
    value = {}
    for key, item in pairs:
        require(key not in value, "duplicate-member")
        value[key] = item
    return value


def reject_nonfinite(_value: str) -> object:
    raise Refused("nonfinite-number")


def strict_json(raw: bytes) -> dict:
    require(type(raw) is bytes and 0 < len(raw) <= 64 * 1024, "json-size")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs,
                           parse_constant=reject_nonfinite)
    except (UnicodeError, ValueError, TypeError, RecursionError) as error:
        raise Refused("malformed-json") from error
    require(type(value) is dict, "object-required")
    return value


def payload(binding: dict) -> bytes:
    return (SCHEMA + "\n").encode("ascii") + json.dumps(
        binding, sort_keys=True, separators=(",", ":"), ensure_ascii=True,
        allow_nan=False).encode("ascii")


def spki_sha256(public_key_pem: bytes) -> str:
    try:
        result = subprocess.run(
            ["openssl", "pkey", "-pubin", "-outform", "DER"],
            input=public_key_pem, capture_output=True, check=False)
    except OSError as error:
        raise Refused("signature-tool-unavailable") from error
    require(result.returncode == 0 and bool(result.stdout), "public-key")
    return hashlib.sha256(result.stdout).hexdigest()


def verify_signature(public_key_pem: bytes, signed: bytes, signature: bytes) -> None:
    with tempfile.TemporaryDirectory() as directory:
        key = Path(directory) / "key.pem"
        signature_file = Path(directory) / "signature.bin"
        key.write_bytes(public_key_pem)
        signature_file.write_bytes(signature)
        try:
            result = subprocess.run(
                ["openssl", "dgst", "-sha256", "-verify", str(key),
                 "-signature", str(signature_file), "-sigopt", "rsa_padding_mode:pss",
                 "-sigopt", "rsa_pss_saltlen:digest"],
                input=signed, capture_output=True, check=False)
        except OSError as error:
            raise Refused("signature-tool-unavailable") from error
    require(result.returncode == 0, "signature")


def verify(envelope_raw: bytes, proof_raw: bytes, token: str, public_key_pem: bytes,
           pinned_spki_sha256: str, expected: dict, now: dt.datetime) -> dict:
    """Verify a host signature against a separately pinned key and exact run facts.

    The caller must supply expected facts from a protected host, not from the
    envelope or a candidate-writable file. Mint-proof validation is separate.
    """
    require(type(token) is str and len(token) > 20 and token.isascii()
            and not any(character.isspace() for character in token), "token")
    require(type(public_key_pem) is bytes and bool(public_key_pem), "public-key")
    require(type(pinned_spki_sha256) is str and HEX64.fullmatch(pinned_spki_sha256),
            "trust-anchor")
    require(spki_sha256(public_key_pem) == pinned_spki_sha256, "trust-anchor")
    require(type(now) is dt.datetime and now.tzinfo is not None
            and now.utcoffset() is not None, "clock")
    require(type(expected) is dict and set(expected) == {
        "workflowSha", "runId", "runAttempt", "candidateSha", "runNonce",
    }, "expected-run")
    require(type(expected["workflowSha"]) is str and HEX40.fullmatch(expected["workflowSha"])
            and type(expected["candidateSha"]) is str and HEX40.fullmatch(expected["candidateSha"])
            and type(expected["runId"]) is int and expected["runId"] > 0
            and type(expected["runAttempt"]) is int and expected["runAttempt"] > 0,
            "expected-run")
    nonce = f'{expected["runId"]}-{expected["runAttempt"]}-{expected["candidateSha"]}'
    require(expected["runNonce"] == nonce, "expected-nonce")

    envelope = strict_json(envelope_raw)
    require(set(envelope) == {"schema", "binding", "signatureBase64"}
            and envelope["schema"] == SCHEMA, "envelope")
    binding = envelope["binding"]
    require(type(binding) is dict and set(binding) == BINDING_FIELDS, "binding-fields")
    fixed = {
        "audience": AUDIENCE, "workflowRepository": REPOSITORY,
        "workflowPath": WORKFLOW, "environment": ENVIRONMENT,
        "sandboxRepositoryId": SANDBOX_ID,
        "sandboxRepositoryNodeId": SANDBOX_NODE, "projectNodeId": PROJECT_NODE,
        **expected,
    }
    require(all(type(binding[key]) is type(value) and binding[key] == value
                for key, value in fixed.items()), "run-or-target")
    require(type(binding["proofSha256"]) is str
            and HEX64.fullmatch(binding["proofSha256"])
            and binding["proofSha256"] == hashlib.sha256(proof_raw).hexdigest(),
            "proof-digest")
    token_digest = hashlib.sha256(token.encode("ascii")).hexdigest()
    require(type(binding["tokenSha256"]) is str
            and HEX64.fullmatch(binding["tokenSha256"])
            and binding["tokenSha256"] == token_digest, "token-digest")
    proof = strict_json(proof_raw)
    require(proof.get("tokenSha256") == token_digest
            and type(proof.get("expiresAt")) is str
            and binding["expiresAt"] == proof["expiresAt"], "proof-binding")
    require(type(binding["expiresAt"]) is str, "expiry")
    try:
        require(binding["expiresAt"].endswith("Z"), "expiry")
        expiry = dt.datetime.fromisoformat(binding["expiresAt"][:-1] + "+00:00")
    except (ValueError, TypeError, OverflowError) as error:
        raise Refused("expiry") from error
    require(now < expiry <= now + dt.timedelta(hours=2), "expiry")
    encoded = envelope["signatureBase64"]
    require(type(encoded) is str and bool(encoded), "signature")
    try:
        signature = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as error:
        raise Refused("signature") from error
    require(bool(signature), "signature")
    verify_signature(public_key_pem, payload(binding), signature)
    return binding
