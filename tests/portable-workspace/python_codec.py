#!/usr/bin/env python3
"""Independent standard-library conformance check for portable workspace v1."""

import hashlib
import json
import re
from datetime import datetime

SCHEMA = "fsgg.workspace.command/1"
MAX = 2**64 - 1
REQUIRED = ["schema", "commandId", "idempotencyId", "workspaceScope", "profileId", "profileRevision", "sourceRevision", "expectedWorkflowRevision", "fenceGeneration", "deadline", "operation"]
OPTIONAL = ["causationId", "componentId"]
ORDER = REQUIRED[:9] + ["causationId"] + REQUIRED[9:] + ["componentId"]


def pairs(values):
    result = {}
    for key, value in values:
        if key in result:
            raise ValueError("duplicate")
        result[key] = value
    return result


def validate(text):
    value = json.loads(text, object_pairs_hook=pairs)
    if value.get("schema") != SCHEMA:
        raise ValueError("unsupported-schema")
    if set(value) - set(REQUIRED + OPTIONAL) or set(REQUIRED) - set(value):
        raise ValueError("shape")
    if any(value.get(key) is None for key in OPTIONAL if key in value):
        raise ValueError("null")
    for key in ("profileRevision", "expectedWorkflowRevision", "fenceGeneration"):
        counter = value[key]
        if not isinstance(counter, str) or not re.fullmatch(r"0|[1-9][0-9]{0,19}", counter) or int(counter) > MAX:
            raise ValueError("counter")
    deadline = value["deadline"]
    if not isinstance(deadline, str) or not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z", deadline):
        raise ValueError("timestamp")
    datetime.strptime(deadline, "%Y-%m-%dT%H:%M:%S.%fZ")
    return value


def canonical(value):
    return json.dumps({key: value[key] for key in ORDER if key in value}, separators=(",", ":"), ensure_ascii=False).encode()


def expect_refused(text):
    try:
        validate(text)
    except (ValueError, json.JSONDecodeError):
        return
    raise AssertionError("document unexpectedly accepted")


command = {
    "schema": SCHEMA,
    "commandId": "11111111-2222-3333-4444-555555555555",
    "idempotencyId": "max-counter",
    "workspaceScope": "fs-gg/conformance",
    "profileId": "portable-v1",
    "profileRevision": str(MAX),
    "sourceRevision": "0123456789abcdef0123456789abcdef01234567",
    "expectedWorkflowRevision": str(MAX),
    "fenceGeneration": str(MAX),
    "deadline": "2026-09-30T12:34:56.789123Z",
    "operation": "build",
}
encoded = canonical(validate(json.dumps(command)))
assert b":null" not in encoded
expect_refused(encoded.decode()[:-1] + ',"extra":"x"}')
expect_refused(encoded.decode().replace('"schema":', '"schema":"fsgg.workspace.command/1","schema":', 1))
expect_refused(encoded.decode().replace('"operation":"build"', '"causationId":null,"operation":"build"'))
expect_refused(encoded.decode().replace(SCHEMA, "fsgg.workspace.command/2"))

for evidence in ({"state": "known", "value": 0}, {"state": "missing", "reason": "not-produced"}, {"state": "unknown", "reason": "readback-lost"}):
    assert set(evidence) == ({"state", "value"} if evidence["state"] == "known" else {"state", "reason"})

print(hashlib.sha256(encoded).hexdigest())
