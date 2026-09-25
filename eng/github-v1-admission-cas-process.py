#!/usr/bin/env python3
"""Import-only post-genesis CAS handoff for an already planned admission.

The public typed CAS plan is supplied by the typed service. A short-lived
ordinary-App JWT may arrive only through an inherited pipe or socket FD. This module
cannot invent a MutationContext or prove admission: every provider response,
including a success or failure, remains unknown until the typed service reads
the complete journal again. No private key, JWT, or installation token is
accepted through arguments, environment, or a file. It has no standalone writer
entry point; a separately qualified service must call `execute`.
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import selectors
import stat
import sys
import time

sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-journal-cas.py")
spec = importlib.util.spec_from_file_location("v1_admission_cas_process_core", SOURCE)
cas = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cas)

MAX_PLAN = 65536


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise cas.Refused(reason)


def unique_pairs(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, "admission-cas-process-duplicate-field")
        result[name] = value
    return result


def decode_public_plan(raw: bytes) -> dict:
    require(isinstance(raw, bytes) and 0 < len(raw) <= MAX_PLAN,
            "admission-cas-process-plan-size")
    try:
        value = json.loads(raw, object_pairs_hook=unique_pairs)
    except (UnicodeError, ValueError, TypeError) as error:
        raise cas.Refused("admission-cas-process-plan-json") from error
    cas.decode_plan(value)
    return value


def read_jwt_pipe(fd: int = 3, timeout_seconds: float = 30.0) -> str:
    require(type(fd) is int and fd >= 3, "admission-cas-process-jwt-fd")
    try:
        mode = os.fstat(fd).st_mode
    except OSError as error:
        raise cas.Refused("admission-cas-process-jwt-fd") from error
    require(stat.S_ISFIFO(mode) or stat.S_ISSOCK(mode),
            "admission-cas-process-jwt-not-pipe")
    chunks = []
    total = 0
    deadline = time.monotonic() + timeout_seconds
    with selectors.DefaultSelector() as selector:
        selector.register(fd, selectors.EVENT_READ)
        while True:
            remaining = deadline - time.monotonic()
            require(remaining > 0 and bool(selector.select(remaining)),
                    "admission-cas-process-jwt-timeout")
            chunk = os.read(fd, 8193 - total)
            if not chunk:
                break
            total += len(chunk)
            require(total <= 8192, "admission-cas-process-jwt-size")
            chunks.append(chunk)
    raw = b"".join(chunks)
    require(raw and b"\n" not in raw and b"\0" not in raw and raw.isascii(),
            "admission-cas-process-jwt-shape")
    return raw.decode("ascii")


def execute(raw_plan: bytes, jwt_fd: int = 3,
            append=cas.append_with_ordinary_app) -> dict:
    """Validate the public plan before consuming the one-shot secret pipe."""
    checked = decode_public_plan(raw_plan)
    jwt = read_jwt_pipe(jwt_fd)
    try:
        append(checked, jwt)
    except Exception:
        # The exception may follow a durable push. Only fresh typed journal
        # readback can distinguish it from a pre-send refusal or lost response.
        pass
    return {"schema": "fsgg.v1-admission-cas-process-result/1",
            "outcome": "response-unknown"}
