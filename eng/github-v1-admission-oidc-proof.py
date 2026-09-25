#!/usr/bin/env python3
"""Import-only verifier for a GitHub Actions OperatingV1 service-job proof.

This verifies a signed job identity against an expected, separately native-read
identity. It has no issuer entry point, App key access, JWT minting, CAS writer,
or production authorization by itself. The future Main-host issuer must derive
the expected identity from fresh complete native run/job/workflow/environment
reads and validate the sealed admission plan before calling this module.
"""

from __future__ import annotations

import base64
import dataclasses
import hashlib
import hmac
import json
import re
import urllib.request

ISSUER = "https://token.actions.githubusercontent.com"
JWKS_URL = ISSUER + "/.well-known/jwks"
REPOSITORY = "FS-GG/.github"
REPOSITORY_ID = "1269292704"
REF = "refs/heads/main"
DER_SHA256 = bytes.fromhex("3031300d060960864801650304020105000420")
BASE64URL = re.compile(r"[A-Za-z0-9_-]+\Z")
HEX = re.compile(r"[0-9a-f]+\Z")


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "oidc-duplicate-field")
        result[key] = value
    return result


def json_object(raw: bytes, ceiling: int) -> dict:
    require(isinstance(raw, bytes) and 0 < len(raw) <= ceiling, "oidc-json-size")
    try:
        value = json.loads(
            raw,
            object_pairs_hook=unique_pairs,
            parse_constant=lambda _: (_ for _ in ()).throw(Refused("oidc-json-constant")),
        )
    except (UnicodeError, ValueError, TypeError) as error:
        raise Refused("oidc-json-invalid") from error
    require(isinstance(value, dict), "oidc-json-object")
    return value


def b64url(value: str, ceiling: int) -> bytes:
    require(isinstance(value, str) and 0 < len(value) <= ceiling
            and BASE64URL.fullmatch(value) is not None, "oidc-base64url-shape")
    try:
        raw = base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
    except (ValueError, TypeError) as error:
        raise Refused("oidc-base64url-invalid") from error
    require(base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=") == value,
            "oidc-base64url-canonical")
    return raw


def plan_audience(sealed_plan: bytes, nonce: str) -> str:
    """Bind one exact public sealed-plan byte string and a fresh 128-bit nonce."""
    require(isinstance(sealed_plan, bytes) and 0 < len(sealed_plan) <= 65536,
            "oidc-plan-size")
    require(isinstance(nonce, str) and len(nonce) == 32
            and HEX.fullmatch(nonce) is not None, "oidc-plan-nonce")
    return "urn:fsgg:v1-admission:" + hashlib.sha256(sealed_plan).hexdigest() + ":" + nonce


@dataclasses.dataclass(frozen=True)
class NativeJobIdentity:
    workflow_path: str
    workflow_head: str
    run_id: int
    run_attempt: int
    actor_id: int
    check_run_id: int
    environment_name: str
    environment_node_id: str


@dataclasses.dataclass(frozen=True)
class SignedJobProof:
    token_id: str
    issued_at: int
    expires_at: int
    audience: str
    payload_sha256: str


def read_github_jwks() -> bytes:
    """Fetch public keys only from GitHub's fixed OIDC JWKS endpoint."""
    request = urllib.request.Request(JWKS_URL, headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            require(response.geturl() == JWKS_URL and response.status == 200,
                    "oidc-jwks-origin")
            raw = response.read(65537)
    except (OSError, ValueError) as error:
        raise Refused("oidc-jwks-unavailable") from error
    require(len(raw) <= 65536, "oidc-jwks-size")
    return raw


def _verify_rs256(signing_input: bytes, signature: bytes, jwks: dict, kid: str) -> None:
    require(set(jwks) == {"keys"} and isinstance(jwks["keys"], list)
            and 0 < len(jwks["keys"]) <= 32, "oidc-jwks-shape")
    matches = [key for key in jwks["keys"]
               if isinstance(key, dict) and key.get("kid") == kid]
    require(len(matches) == 1, "oidc-jwks-kid")
    key = matches[0]
    require(key.get("kty") == "RSA" and key.get("alg") in (None, "RS256")
            and key.get("use") in (None, "sig"), "oidc-jwks-key")
    modulus = b64url(key.get("n"), 700)
    exponent = b64url(key.get("e"), 12)
    require(256 <= len(modulus) <= 512 and modulus[0] & 0x80
            and exponent == b"\x01\x00\x01", "oidc-jwks-rsa-size")
    n = int.from_bytes(modulus, "big")
    e = int.from_bytes(exponent, "big")
    require(len(signature) == len(modulus), "oidc-signature-size")
    signature_value = int.from_bytes(signature, "big")
    require(signature_value < n, "oidc-signature-range")
    encoded = pow(signature_value, e, n).to_bytes(len(modulus), "big")
    digest_info = DER_SHA256 + hashlib.sha256(signing_input).digest()
    padding = b"\xff" * (len(modulus) - len(digest_info) - 3)
    expected = b"\x00\x01" + padding + b"\x00" + digest_info
    require(len(padding) >= 8 and hmac.compare_digest(encoded, expected),
            "oidc-signature-invalid")


def verify_signed_job(
    compact: str,
    jwks_bytes: bytes,
    native: NativeJobIdentity,
    audience: str,
    now: int,
) -> SignedJobProof:
    """Verify one signed token; the caller must separately validate native evidence.

    NativeJobIdentity is an expected value supplied by the future fixed Main
    native reader, never by a request body. This function does not consume jti
    or plan nonce; durable one-shot consumption is a separate issuer gate.
    """
    require(isinstance(compact, str) and len(compact) <= 16384
            and compact.count(".") == 2, "oidc-token-shape")
    encoded_header, encoded_payload, encoded_signature = compact.split(".")
    header = json_object(b64url(encoded_header, 4096), 4096)
    payload_bytes = b64url(encoded_payload, 8192)
    payload = json_object(payload_bytes, 8192)
    signature = b64url(encoded_signature, 2048)
    require(header.get("alg") == "RS256" and header.get("typ") in (None, "JWT")
            and isinstance(header.get("kid"), str) and 0 < len(header["kid"]) <= 256,
            "oidc-header")
    _verify_rs256(
        (encoded_header + "." + encoded_payload).encode("ascii"),
        signature,
        json_object(jwks_bytes, 65536),
        header["kid"],
    )
    require(isinstance(native, NativeJobIdentity)
            and type(now) is int and now > 0
            and isinstance(audience, str)
            and re.fullmatch(r"urn:fsgg:v1-admission:[0-9a-f]{64}:[0-9a-f]{32}", audience) is not None,
            "oidc-expected-identity")
    require(isinstance(payload.get("iat"), int) and not isinstance(payload["iat"], bool)
            and isinstance(payload.get("nbf"), int) and not isinstance(payload["nbf"], bool)
            and isinstance(payload.get("exp"), int) and not isinstance(payload["exp"], bool)
            and payload["iat"] <= now + 30 and payload["nbf"] <= now + 30
            and now < payload["exp"] <= now + 600
            and 0 < payload["exp"] - payload["iat"] <= 900,
            "oidc-time")
    expected = {
        "iss": ISSUER,
        "aud": audience,
        "repository": REPOSITORY,
        "repository_id": REPOSITORY_ID,
        "ref": REF,
        "sha": native.workflow_head,
        "workflow_ref": f"{REPOSITORY}/{native.workflow_path}@{REF}",
        "workflow_sha": native.workflow_head,
        "run_id": str(native.run_id),
        "run_attempt": str(native.run_attempt),
        "actor_id": str(native.actor_id),
        "check_run_id": str(native.check_run_id),
        "environment": native.environment_name,
        "environment_node_id": native.environment_node_id,
        "event_name": "workflow_dispatch",
    }
    require(all(payload.get(name) == value for name, value in expected.items()),
            "oidc-job-binding")
    require(native.workflow_path.startswith(".github/workflows/")
            and native.workflow_path.endswith(".yml")
            and len(native.workflow_head) == 40
            and HEX.fullmatch(native.workflow_head) is not None
            and native.run_id > 0 and native.run_attempt == 1
            and native.actor_id > 0 and native.check_run_id > 0
            and bool(native.environment_name) and bool(native.environment_node_id),
            "oidc-native-identity-shape")
    jti = payload.get("jti")
    require(isinstance(jti, str) and 0 < len(jti) <= 128
            and all(32 <= ord(char) < 127 for char in jti), "oidc-token-id")
    require(isinstance(payload.get("sub"), str) and bool(payload["sub"]),
            "oidc-subject")
    return SignedJobProof(
        token_id=jti,
        issued_at=payload["iat"],
        expires_at=payload["exp"],
        audience=audience,
        payload_sha256=hashlib.sha256(payload_bytes).hexdigest(),
    )
