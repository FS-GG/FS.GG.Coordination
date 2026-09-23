#!/usr/bin/env python3
"""Bounded local stdio bridge for the typed protected admission installer.

The first input line is a short-lived App JWT supplied over a pipe by the runner.
Neither that JWT nor the minted installation token is returned or logged. This
bridge is a transport capability, not an approval or standalone install command.
"""

from __future__ import annotations

import base64
import importlib.util
import json
import pathlib
import sys


sys.dont_write_bytecode = True
SOURCE = pathlib.Path(__file__).with_name("github-v1-admission-provider-transport.py")
spec = importlib.util.spec_from_file_location("v1_admission_provider_transport", SOURCE)
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise transport.Refused(reason)


def line(stream, ceiling: int) -> bytes:
    raw = stream.readline(ceiling + 2)
    require(0 < len(raw) <= ceiling + 1 and raw.endswith(b"\n"),
            "admission-bridge-line")
    return raw[:-1]


def exact(value, fields):
    require(isinstance(value, dict) and set(value) == set(fields), "admission-bridge-shape")


class Bridge:
    def __init__(self, provider):
        self.provider = provider
        self.bound_commit = None
        self.objects = {}

    def handle(self, value):
        require(isinstance(value, dict) and isinstance(value.get("op"), str),
                "admission-bridge-command")
        operation = value["op"]
        if operation == "bind":
            exact(value, {"op", "ref", "commit", "objects"})
            require(self.bound_commit is None and value["ref"] == transport.OPERATION_REF
                    and isinstance(value["commit"], str)
                    and transport.OID.fullmatch(value["commit"]) is not None
                    and isinstance(value["objects"], list) and len(value["objects"]) == 4,
                    "admission-bridge-plan")
            objects = {}
            for item in value["objects"]:
                exact(item, {"kind", "oid", "bytesBase64"})
                kind, oid = item["kind"], item["oid"]
                require(kind in {"blob", "tree", "commit"} and isinstance(oid, str)
                        and transport.OID.fullmatch(oid) is not None
                        and isinstance(item["bytesBase64"], str), "admission-bridge-object")
                try:
                    raw = base64.b64decode(item["bytesBase64"], validate=True)
                except ValueError as error:
                    raise transport.Refused("admission-bridge-object") from error
                require(len(raw) <= 8192 and transport.git_oid(kind, raw) == oid
                        and (kind, oid) not in objects, "admission-bridge-object")
                objects[(kind, oid)] = raw
            require(sorted(kind for kind, _ in objects) == ["blob", "blob", "commit", "tree"]
                    and ("commit", value["commit"]) in objects, "admission-bridge-plan")
            self.bound_commit = value["commit"]
            self.objects = objects
            return {"bound": True}
        require(self.bound_commit is not None, "admission-bridge-unbound")
        if operation == "read_ref":
            exact(value, {"op", "ref"})
            return {"oid": self.provider.read_ref(value["ref"])}
        if operation == "protection":
            exact(value, {"op"})
            return {"snapshot": self.provider.protection_snapshot()}
        if operation == "put_object":
            exact(value, {"op", "kind", "oid"})
            key = value["kind"], value["oid"]
            require(key in self.objects, "admission-bridge-object-not-planned")
            return {"oid": self.provider.put_object(*key, self.objects[key])}
        if operation == "read_object":
            exact(value, {"op", "kind", "oid"})
            key = value["kind"], value["oid"]
            require(key in self.objects, "admission-bridge-object-not-planned")
            raw = self.provider.read_object(*key)
            return {"bytesBase64": base64.b64encode(raw).decode("ascii")}
        if operation == "create_ref":
            exact(value, {"op", "ref", "commit"})
            require(value["ref"] == transport.OPERATION_REF
                    and value["commit"] == self.bound_commit,
                    "admission-bridge-ref-not-planned")
            self.provider.create_ref_expected_absent(value["ref"], value["commit"])
            return {"createdOrPresent": True}
        raise transport.Refused("admission-bridge-command")


def serve(stdin=sys.stdin.buffer, stdout=sys.stdout.buffer, factory=transport.OrdinaryAdmissionTransport):
    jwt = line(stdin, 8192)
    require(b"\0" not in jwt and jwt.isascii(), "admission-bridge-jwt")
    provider = factory(jwt.decode("ascii"))
    bridge = Bridge(provider)
    stdout.write(b'{"ready":true}\n')
    stdout.flush()
    while True:
        raw = stdin.readline(32770)
        if not raw:
            return
        require(len(raw) <= 32769 and raw.endswith(b"\n"), "admission-bridge-line")
        try:
            value = json.loads(raw)
            result = {"ok": bridge.handle(value)}
        except (transport.Refused, ValueError, TypeError, KeyError, AttributeError) as error:
            result = {"error": str(error) if isinstance(error, transport.Refused)
                      else "admission-bridge-invalid"}
        stdout.write(json.dumps(result, sort_keys=True, separators=(",", ":")).encode() + b"\n")
        stdout.flush()


if __name__ == "__main__":
    try:
        serve()
    except (transport.Refused, OSError) as error:
        print(f"v1 admission provider bridge refused: {error}", file=sys.stderr)
        sys.exit(3)
