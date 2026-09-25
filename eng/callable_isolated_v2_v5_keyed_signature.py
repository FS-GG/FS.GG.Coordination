"""Closed post-registry signature byte join through an injected fake verifier.

Passing registry-selected public key bytes to a fake verifier does not prove a
real Ed25519 signature, protected key custody, revocation, or issuer identity.
"""

from __future__ import annotations

import base64
import binascii
import copy
import dataclasses
import datetime as dt
import hashlib
import json

import callable_isolated_v2_v5_issuer_envelope as envelope
import callable_isolated_v2_v5_key_registry as registry
import callable_isolated_v2_v5_no_grant_selection as candidate


class Refused(ValueError):
    """Fixed refusal without signed bytes or credential contents."""


@dataclasses.dataclass(frozen=True)
class KeyedWitness:
    key_id: str
    envelope_sha256: str
    verifier_reader_principal: str
    verifier_credential_id: str
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


def qualify(checked: envelope.EnvelopeWitness,
            key: registry.KeyWitness, raw: bytes, verifier_port,
            now: dt.datetime) -> KeyedWitness:
    """Send exact registry key and signed payload bytes to one fake verifier."""
    try:
        if (type(checked) is not envelope.EnvelopeWitness
                or type(key) is not registry.KeyWitness):
            raise Refused("v5-keyed-prior")
        originals = (checked, key, raw)
        checked, key, raw = copy.deepcopy(originals)
        if (any(result.authorized is not False
                or result.can_dispatch is not False
                or type(result.live_effects) is not int
                or result.live_effects != 0 for result in (checked, key))
                or type(raw) is not bytes or not 0 < len(raw) <= 8192
                or verifier_port is None
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)
                or not candidate._hex(key.key_id, candidate.HEX64)
                or key.key_id != checked.key_id
                or key.public_key_sha256 != key.key_id
                or type(key.public_key_bytes) is not bytes
                or len(key.public_key_bytes) != 32
                or key.public_key_bytes == bytes(32)
                or hashlib.sha256(key.public_key_bytes).hexdigest()
                   != key.key_id
                or not candidate._hex(checked.envelope_sha256,
                                      candidate.HEX64)
                or hashlib.sha256(raw).hexdigest()
                   != checked.envelope_sha256
                or not candidate._hex(checked.nonce, candidate.HEX64)
                or type(key.registry_event_id) is not int
                or key.registry_event_id <= 0
                or type(key.reviewer_actor_id) is not int
                or key.reviewer_actor_id <= 0
                or type(key.repository_id) is not int
                or key.repository_id <= 0
                or type(checked.issuer_event_id) is not int
                or checked.issuer_event_id <= 0):
            raise Refused("v5-keyed-selection")
        observed = candidate._time(key.observed_at)
        expires = candidate._time(key.expires_at)
        if not (now - dt.timedelta(minutes=30) <= observed <= now < expires
                <= observed + dt.timedelta(minutes=30)):
            raise Refused("v5-keyed-time")
        try:
            value = json.loads(raw.decode("utf-8"),
                               object_pairs_hook=envelope._unique,
                               parse_constant=envelope._nonfinite)
        except (UnicodeError, ValueError, TypeError):
            raise Refused("v5-keyed-json") from None
        envelope._shape(value, envelope.ENVELOPE)
        if (value["schema"] != envelope.ENVELOPE_SCHEMA
                or raw != envelope._canonical(value)):
            raise Refused("v5-keyed-canonical")
        payload = value["payload"]
        envelope._shape(payload, envelope.PAYLOAD)
        if (payload["schema"] != envelope.PAYLOAD_SCHEMA
                or type(payload["keyId"]) is not str
                or payload["keyId"] != key.key_id
                or type(payload["nonce"]) is not str
                or payload["nonce"] != checked.nonce
                or type(payload["issuerEventId"]) is not int
                or payload["issuerEventId"] != checked.issuer_event_id
                or payload["algorithm"] != "Ed25519"
                or payload["audience"] != envelope.AUDIENCE):
            raise Refused("v5-keyed-claims")
        signature_text = value["signature"]
        if type(signature_text) is not str:
            raise Refused("v5-keyed-signature")
        try:
            signature = base64.b64decode(signature_text, validate=True)
        except (binascii.Error, ValueError):
            raise Refused("v5-keyed-signature") from None
        if (len(signature) != 64
                or base64.b64encode(signature).decode("ascii")
                   != signature_text):
            raise Refused("v5-keyed-signature")
        scope = copy.deepcopy(verifier_port.scope())
        envelope._shape(scope, envelope.SCOPE)
        principals = (checked.verifier_reader_principal,
                      key.registry_reader_principal, scope["principalId"])
        credentials = (checked.verifier_credential_id,
                       key.registry_credential_id, scope["credentialId"])
        if (any(type(item) is not str or not item for item in principals)
                or any(not candidate._hex(item, candidate.HEX64)
                       for item in credentials)
                or len(set(principals)) != len(principals)
                or len(set(credentials)) != len(credentials)
                or scope["repository"] != candidate.REPOSITORY
                or type(scope["repositoryId"]) is not int
                or scope["repositoryId"] != key.repository_id
                or type(scope["permissions"]) is not list
                or scope["permissions"] != ["verify-signature"]
                or scope["keyId"] != key.key_id
                or scope["algorithm"] != "Ed25519"
                or not now < candidate._time(scope["expiresAt"])
                       <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-keyed-verifier-scope")
        verified = verifier_port.verify(key.public_key_bytes, "Ed25519",
                                        envelope._canonical(payload),
                                        signature)
        if verified is not True:
            raise Refused("v5-keyed-unverified")
        if (verifier_port.scope() != scope
                or originals != (checked, key, raw)):
            raise Refused("v5-keyed-drift")
        return KeyedWitness(key.key_id, checked.envelope_sha256,
                            scope["principalId"], scope["credentialId"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-keyed-unavailable") from None
