"""Strict canonical Ed25519 grant verification before any effect capability lookup."""
from __future__ import annotations

import base64
import binascii
import datetime as dt
import json
import re
import subprocess
import tempfile
from pathlib import Path
from .contracts import Binding, digest

SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-grant/1"
AUDIENCE = "fsgg.coordination.callable-isolated-v2-native-runner/1"
HEX64 = re.compile(r"[0-9a-f]{64}\Z")

class Refused(ValueError):
    pass


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise Refused("grant-duplicate")
        value[key] = item
    return value


def _constant(_):
    raise Refused("grant-nonfinite")


def canonical(value) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":"),
                       ensure_ascii=True, allow_nan=False) + "\n").encode("ascii")


def _time(value):
    if type(value) is not str or not re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", value):
        raise Refused("grant-time")
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise Refused("grant-time") from None


def _ed25519_verify(public_key: bytes, payload: bytes, signature: bytes) -> bool:
    """OpenSSL's Ed25519 verifier; raw key is wrapped as RFC 8410 SPKI."""
    if type(public_key) is not bytes or len(public_key) != 32 or len(signature) != 64:
        return False
    with tempfile.TemporaryDirectory(prefix="fsgg-v2-verify-") as temp:
        root = Path(temp)
        (root / "key.der").write_bytes(bytes.fromhex("302a300506032b6570032100") + public_key)
        (root / "payload").write_bytes(payload)
        (root / "signature").write_bytes(signature)
        result = subprocess.run(["openssl", "pkeyutl", "-verify", "-pubin",
                                 "-inkey", str(root / "key.der"), "-keyform", "DER",
                                 "-rawin", "-in", str(root / "payload"),
                                 "-sigfile", str(root / "signature")],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                timeout=5, check=False)
        return result.returncode == 0


def verify(raw: bytes, binding: Binding, key_reader, now: dt.datetime,
           *, allow_expired: bool = False) -> str:
    """Return grant digest only for exact signed claims and trusted active key."""
    try:
        if (type(raw) is not bytes or not 0 < len(raw) <= 8192
                or type(binding) is not Binding or type(now) is not dt.datetime
                or now.tzinfo is None or now.utcoffset() != dt.timedelta(0)):
            raise Refused("grant-input")
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=_constant)
        if (type(value) is not dict or set(value) != {"schema", "payload", "signature"}
                or value["schema"] != SCHEMA or canonical(value) != raw):
            raise Refused("grant-canonical")
        payload = value["payload"]
        required = set(binding.claims()) | {"schema", "audience", "algorithm",
                                             "keyId", "issuedAt", "expiresAt"}
        if (type(payload) is not dict or set(payload) != required
                or payload["schema"] != SCHEMA or payload["audience"] != AUDIENCE
                or payload["algorithm"] != "Ed25519"
                or any(type(payload[k]) is not type(v) or payload[k] != v
                       for k, v in binding.claims().items())):
            raise Refused("grant-binding")
        key_id = payload["keyId"]
        if type(key_id) is not str or HEX64.fullmatch(key_id) is None:
            raise Refused("grant-key")
        issued, expires = _time(payload["issuedAt"]), _time(payload["expiresAt"])
        if not (issued <= now and issued < expires
                <= issued + dt.timedelta(minutes=30)
                and (allow_expired or now < expires)
                and (allow_expired or now - dt.timedelta(minutes=30) <= issued)):
            raise Refused("grant-expired")
        encoded = value["signature"]
        if type(encoded) is not str:
            raise Refused("grant-signature")
        signature = base64.b64decode(encoded, validate=True)
        if len(signature) != 64 or base64.b64encode(signature).decode("ascii") != encoded:
            raise Refused("grant-signature")
        # The reader is an internally wired protected-key adapter. It must fail closed
        # for missing, revoked, or non-approved keys; raw caller bytes cannot select it.
        public_key = key_reader.active_public_key(key_id, binding.issuer_actor_id, now)
        if (type(public_key) is not bytes or digest(public_key) != key_id
                or not _ed25519_verify(public_key, canonical(payload), signature)):
            raise Refused("grant-unverified")
        return digest(raw)
    except Refused:
        raise
    except (ValueError, TypeError, UnicodeError, binascii.Error, OSError,
            subprocess.SubprocessError):
        raise Refused("grant-unavailable") from None
