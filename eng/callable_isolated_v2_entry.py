"""Inspect-only entry for the prospective isolated v2 zipapp.

No command in this artifact can issue a grant, read a token, contact a provider,
write a journal, or dispatch an effect. A live entry requires a new artifact.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import stat
import sys

import callable_isolated_v2_grant as grant

USAGE = "usage: inspect-grant --grant FILE --digest SHA256 --expected FILE --replay FILE"
OPTIONS = {"--grant", "--digest", "--expected", "--replay"}


def _options(arguments: list[str]) -> dict[str, str]:
    if len(arguments) != 8:
        raise grant.Refused("inspect-arguments")
    values: dict[str, str] = {}
    for index in range(0, len(arguments), 2):
        key, value = arguments[index:index + 2]
        if key not in OPTIONS or key in values or not value or value.startswith("--"):
            raise grant.Refused("inspect-arguments")
        values[key] = value
    if set(values) != OPTIONS:
        raise grant.Refused("inspect-arguments")
    return values


def _read_regular(path: str) -> bytes:
    try:
        flags = os.O_RDONLY
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        descriptor = os.open(path, flags)
        try:
            status = os.fstat(descriptor)
            if not stat.S_ISREG(status.st_mode) or status.st_size > 16_384:
                raise grant.Refused("inspect-input-shape")
            raw = os.read(descriptor, 16_385)
            if len(raw) > 16_384:
                raise grant.Refused("inspect-input-shape")
            return raw
        finally:
            os.close(descriptor)
    except grant.Refused:
        raise
    except OSError:
        raise grant.Refused("inspect-input-unavailable") from None


def _object(raw: bytes) -> dict:
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=grant._unique,
                           parse_constant=grant._no_constant)
    except grant.Refused:
        raise
    except (UnicodeError, ValueError):
        raise grant.Refused("inspect-input-json") from None
    if type(value) is not dict:
        raise grant.Refused("inspect-input-json")
    return value


def main(argv: list[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments == ["--help"]:
        print(USAGE)
        return 0
    if not arguments or arguments[0] != "inspect-grant":
        print("inspect-only", file=sys.stderr)
        return 2
    try:
        values = _options(arguments[1:])
        raw = _read_regular(values["--grant"])
        expected = _object(_read_regular(values["--expected"]))
        replay = _object(_read_regular(values["--replay"]))
        parsed = grant.parse_grant(raw, values["--digest"], expected, replay,
                                   dt.datetime.now(dt.timezone.utc))
    except grant.Refused as error:
        print(f"inspect-refused:{error}", file=sys.stderr)
        return 2
    except Exception:
        print("inspect-refused:unavailable", file=sys.stderr)
        return 2
    result = {
        "schema": "fsgg.coordination.callable-isolated-v2-inspection/1",
        "state": "parsed-not-authorized",
        "payloadSha256": parsed.payload_sha256,
        "authorized": False,
        "canDispatch": False,
        "liveEffects": 0,
    }
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
