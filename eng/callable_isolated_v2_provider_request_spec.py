"""Pure held v5 provider request envelope; no HTTP client or token port.

The exact host and zero-retry policy are source proposal values only. This
module cannot send a request or authorize a protected provider attempt.
"""

from __future__ import annotations

import dataclasses
import hashlib
from typing import Any

import callable_isolated_v2_effect_candidate as candidate
from callable_isolated_v2_effect_v5_ports import NativeCoordinates
from callable_isolated_v2_native_request_custody import RequestCustody
from callable_isolated_v2_operation_plan_read_adapter import VerifiedRequest

SCHEMA = "fsgg.coordination.callable-isolated-v2-provider-request-spec/1"
ORIGIN = "https://api.github.com"


class Refused(ValueError):
    """Fixed refusal without canonical body or credential contents."""


@dataclasses.dataclass(frozen=True)
class RequestSpec:
    operation_id: str
    request_sha256: str
    url: str
    body: bytes
    method: str = dataclasses.field(init=False, default="POST")
    max_sends: int = dataclasses.field(init=False, default=1)
    automatic_retries: int = dataclasses.field(init=False, default=0)
    follow_redirects: bool = dataclasses.field(init=False, default=False)
    schema: str = dataclasses.field(init=False, default=SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)


def _closed(value: Any) -> bool:
    return (value.authorized is False and value.can_dispatch is False
            and type(value.live_effects) is int and value.live_effects == 0)


def prepare(joined: RequestCustody, verified: VerifiedRequest,
            native: NativeCoordinates, origin: str) -> RequestSpec:
    """Carry only the exact already-joined bytes into a non-dispatching spec."""
    if (type(joined) is not RequestCustody
            or type(verified) is not VerifiedRequest
            or type(native) is not NativeCoordinates
            or type(origin) is not str or origin != ORIGIN
            or not _closed(joined) or not _closed(verified)
            or joined.state != "request-consistent-but-unadmitted"
            or type(joined.seal_event_id) is not int
            or joined.seal_event_id <= 0
            or type(joined.committed_generation) is not int
            or joined.committed_generation <= 0
            or not candidate._hex(joined.operation_id, candidate.HEX64)
            or not candidate._hex(joined.request_sha256, candidate.HEX64)
            or joined.operation_id != verified.operation_id
            or joined.request_sha256 != verified.request_sha256
            or joined.operation_id != native.operation_id
            or joined.request_sha256 != native.request_sha256
            or native.operation_identity != candidate.IDENTITY
            or native.method != "POST"
            or type(native.max_provider_writes) is not int
            or native.max_provider_writes != 1
            or type(native.repository) is not str
            or candidate.REPOSITORY.fullmatch(native.repository) is None
            or native.repository == "FS-GG/.github"
            or native.path != f"repos/{native.repository}/pulls"
            or type(verified.canonical_request) is not bytes
            or not verified.canonical_request
            or type(native.canonical_request) is not bytes
            or native.canonical_request != verified.canonical_request
            or hashlib.sha256(verified.canonical_request).hexdigest()
               != joined.request_sha256):
        raise Refused("provider-request-binding")
    return RequestSpec(joined.operation_id, joined.request_sha256,
                       f"{ORIGIN}/{native.path}", verified.canonical_request)
