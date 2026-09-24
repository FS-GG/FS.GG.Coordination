#!/usr/bin/env python3
"""Main-host-only, Secret Service-backed v1 admission credential handoff.

Run this on Main, not in fsharp-dev-2. Private PEM bytes remain in Main process
memory and an anonymous memfd; output is only a short-lived App JWT or a public
signature envelope. Never pass a PEM through argv, environment, files, or chat.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import time

sys.dont_write_bytecode = True

APP_ID = 4882140
SERVICE = "fsgg-ledger-protection"
AUTHORIZER_SERVICE = "fsgg-v1-admission"
# Never fall back to the legacy fsgg-ledger-protection/authorizer record.
AUTHORIZER_KEY_ID = "main-gs2-08-2-authorizer-2dc8d29f8d5a675d"
AUTHORIZER_SPKI_SHA256 = "2dc8d29f8d5a675d070701dacd6dacf2ca3e368ecf823fa9eaf560ddd22d77be"
TRUST_ANCHOR_SHA256 = "0a9f84f72ca10c01b5acc386a32ce6920fea15231f90f65a8f17df9e87d9a779"
SIGNATURE_SCHEMA = "fsgg.github-substrate.v1-admission-genesis-signature/1"
ENVELOPE_SCHEMA = "fsgg.github-substrate.v1-admission-genesis-signature-envelope/1"
HEX64 = re.compile(r"[0-9a-f]{64}\Z")


class Refused(RuntimeError):
    pass


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def canonical(value: dict) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def read_public(path: pathlib.Path, ceiling: int = 8192) -> bytes:
    require(not path.is_symlink() and path.is_file() and path.stat().st_size <= ceiling,
            "admission-public-input")
    return path.read_bytes()


def secret(role: str, run=subprocess.run) -> bytes:
    require(role in {"ordinary", "authorizer"}, "admission-custody-role")
    if role == "ordinary":
        attributes = ["service", SERVICE, "role", role, "app-id", str(APP_ID)]
    else:
        attributes = ["service", AUTHORIZER_SERVICE, "role", role,
                      "key-id", AUTHORIZER_KEY_ID,
                      "spki-sha256", AUTHORIZER_SPKI_SHA256,
                      "trust-anchor-sha256", TRUST_ANCHOR_SHA256]
    try:
        result = run(["secret-tool", "lookup", *attributes], stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, timeout=15, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Refused("admission-custody-unavailable") from error
    key = result.stdout
    require(result.returncode == 0 and isinstance(key, bytes) and 0 < len(key) <= 8192
            and b"-----BEGIN " in key and b"PRIVATE KEY-----" in key,
            "admission-custody-unavailable")
    return key


def openssl_with_key(key: bytes, arguments: list[str]) -> bytes:
    require(hasattr(os, "memfd_create"), "admission-anonymous-key-unavailable")
    fd = os.memfd_create("fsgg-admission-key", os.MFD_CLOEXEC)
    try:
        os.write(fd, key)
        os.lseek(fd, 0, os.SEEK_SET)
        command = ["openssl", *(f"/proc/self/fd/{fd}" if item == "@KEY@" else item
                               for item in arguments)]
        try:
            result = subprocess.run(command, stdout=subprocess.PIPE,
                                    stderr=subprocess.DEVNULL, pass_fds=(fd,), timeout=15,
                                    check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise Refused("admission-openssl-unavailable") from error
        require(result.returncode == 0 and 0 < len(result.stdout) <= 8192,
                "admission-openssl-refused")
        return result.stdout
    finally:
        os.close(fd)


def sign(key: bytes, payload: bytes, pss: bool) -> bytes:
    arguments = ["dgst", "-sha256", "-sign"]
    # openssl dgst expects -sign's path before -sigopt and reads payload on stdin.
    fd = os.memfd_create("fsgg-admission-signing-key", os.MFD_CLOEXEC)
    try:
        os.write(fd, key)
        os.lseek(fd, 0, os.SEEK_SET)
        arguments += [f"/proc/self/fd/{fd}"]
        if pss:
            arguments += ["-sigopt", "rsa_padding_mode:pss"]
        try:
            result = subprocess.run(["openssl", *arguments], input=payload,
                                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                    pass_fds=(fd,), timeout=15, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise Refused("admission-signing-unavailable") from error
        require(result.returncode == 0 and 256 <= len(result.stdout) <= 512,
                "admission-signing-refused")
        return result.stdout
    finally:
        os.close(fd)


def public_key(key: bytes) -> tuple[bytes, str]:
    pem = openssl_with_key(key, ["pkey", "-in", "@KEY@", "-pubout"])
    der = openssl_with_key(key, ["pkey", "-in", "@KEY@", "-pubout", "-outform", "DER"])
    return pem, hashlib.sha256(der).hexdigest()


def b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")


def native_approval(run_id: int) -> dict:
    source = pathlib.Path(__file__).with_name("github-v1-admission-protected-read.py")
    spec = importlib.util.spec_from_file_location("v1_admission_native_read", source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    try:
        return module.collect(run_id)
    except (module.Refused, OSError, ValueError) as error:
        raise Refused("admission-native-approval-unavailable") from error


def approved(run_id: int, read_native=native_approval, now: int | None = None) -> dict:
    require(type(run_id) is int and run_id > 0, "admission-native-run-id")
    try:
        native = read_native(run_id)
        artifact = json.loads(base64.b64decode(native["artifactBytesBase64"], validate=True))
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        raise Refused("admission-native-approval-invalid") from error
    require(native.get("runId") == run_id
            and native.get("schema") == "fsgg.v1-admission-genesis-native-read/2"
            and native.get("runRepositoryId") == 1269292704
            and native.get("runEvent") == "workflow_dispatch"
            and native.get("runPath") == ".github/workflows/gs2-v1-admission-protected-authorization.yml"
            and native.get("runRef") == "refs/heads/main"
            and native.get("runAttempt") == 1
            and native.get("runConclusion") == "success"
            and native.get("runActorId") == 1645484
            and native.get("environmentId") == 22582241959
            and native.get("environmentName") == "fleet-v1-admission-owner"
            and native.get("environmentBranchPolicy") == "custom-main"
            and native.get("environmentWaitMinutes") == 5
            and native.get("environmentPreventsSelfReview") is False
            and native.get("environmentReviewerIds") == [1645484]
            and native.get("approvals") == [{"reviewerId": 1645484, "state": "approved",
                                             "environmentIds": [22582241959]}]
            and artifact.get("schema") == "fsgg.v1-admission-genesis-protected-authorization/2"
            and artifact.get("operationId") == "fleet-v1-admission:fs-gg-production"
            and artifact.get("repository") == "FS-GG/.github"
            and artifact.get("environment") == "fleet-v1-admission-owner"
            and artifact.get("workflowRevision") == native.get("workflowReadRevision") == native.get("runHead")
            and artifact.get("runId") == run_id
            and artifact.get("conclusion") == "success",
            "admission-native-approval-invalid")
    try:
        approved_at = dt.datetime.fromisoformat(artifact["approvedAt"].replace("Z", "+00:00"))
        expires_at = dt.datetime.fromisoformat(artifact["expiresAt"].replace("Z", "+00:00"))
    except (KeyError, TypeError, ValueError) as error:
        raise Refused("admission-native-approval-time") from error
    current = dt.datetime.fromtimestamp(int(time.time()) if now is None else now, dt.timezone.utc)
    require(approved_at <= current < expires_at and expires_at - approved_at <= dt.timedelta(hours=2),
            "admission-native-approval-expired")
    return artifact


def issue_jwt(run_id: int, lookup=secret, read_native=native_approval,
              now: int | None = None) -> bytes:
    current = int(time.time()) if now is None else now
    approved(run_id, read_native, current)
    header = b64url(canonical({"alg": "RS256", "typ": "JWT"}))
    claims = b64url(canonical({"iat": current - 30, "exp": current + 540, "iss": APP_ID}))
    body = (header + "." + claims).encode("ascii")
    signature = sign(lookup("ordinary"), body, pss=False)
    return (header + "." + claims + "." + b64url(signature)).encode("ascii")


def parse_payload(raw: bytes) -> dict:
    require(0 < len(raw) <= 2048, "admission-signing-payload-size")
    try:
        value = json.loads(raw)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise Refused("admission-signing-payload-json") from error
    require(isinstance(value, dict) and set(value) == {
        "schema", "intentSha256", "keyId", "protectedRunId", "authorizedAt", "expiresAt"}
        and raw == canonical(value) and value["schema"] == SIGNATURE_SCHEMA
        and isinstance(value["intentSha256"], str)
        and HEX64.fullmatch(value["intentSha256"]) is not None
        and isinstance(value["keyId"], str) and 0 < len(value["keyId"]) <= 128
        and type(value["protectedRunId"]) is int and value["protectedRunId"] > 0,
        "admission-signing-payload-binding")
    try:
        start = dt.datetime.fromisoformat(value["authorizedAt"])
        end = dt.datetime.fromisoformat(value["expiresAt"])
    except (TypeError, ValueError) as error:
        raise Refused("admission-signing-payload-time") from error
    require(start.tzinfo is not None and end.tzinfo is not None
            and start.utcoffset() == dt.timedelta() and end.utcoffset() == dt.timedelta()
            and start < end <= start + dt.timedelta(hours=2),
            "admission-signing-payload-time")
    return value


def sign_envelope(payload: bytes, trust: bytes, lookup=secret,
                  read_native=native_approval, now: int | None = None,
                  expected_trust_sha256: str = TRUST_ANCHOR_SHA256) -> bytes:
    value = parse_payload(payload)
    artifact = approved(value["protectedRunId"], read_native, now)
    require(artifact.get("genesisIntentSha256") == value["intentSha256"]
            and dt.datetime.fromisoformat(artifact["approvedAt"].replace("Z", "+00:00"))
                == dt.datetime.fromisoformat(value["authorizedAt"])
            and dt.datetime.fromisoformat(artifact["expiresAt"].replace("Z", "+00:00"))
                == dt.datetime.fromisoformat(value["expiresAt"]),
            "admission-native-signature-binding")
    require(isinstance(expected_trust_sha256, str)
            and HEX64.fullmatch(expected_trust_sha256) is not None
            and hashlib.sha256(trust).hexdigest() == expected_trust_sha256,
            "admission-trust-anchor-digest")
    try:
        anchor = json.loads(trust)
        authorizer = anchor["authorizer"]
    except (UnicodeError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise Refused("admission-trust-anchor") from error
    require(anchor.get("schema") == "fsgg.github-ledger-initial-trust/1"
            and isinstance(authorizer, dict)
            and authorizer.get("algorithm") == "RSA-PSS-SHA256"
            and authorizer.get("keyId") == value["keyId"]
            and isinstance(authorizer.get("publicKeySpkiSha256"), str)
            and HEX64.fullmatch(authorizer["publicKeySpkiSha256"]) is not None,
            "admission-trust-anchor")
    key = lookup("authorizer")
    pem, spki = public_key(key)
    require(spki == authorizer["publicKeySpkiSha256"], "admission-authorizer-key-mismatch")
    signature = sign(key, payload, pss=True)
    envelope = dict(value)
    envelope["schema"] = ENVELOPE_SCHEMA
    envelope["publicKeyPem"] = pem.decode("ascii")
    envelope["signatureBase64"] = base64.b64encode(signature).decode("ascii")
    return canonical(envelope) + b"\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    issuer = commands.add_parser("issue-jwt")
    issuer.add_argument("--run-id", type=int, required=True)
    signer = commands.add_parser("sign-intent")
    signer.add_argument("--payload", type=pathlib.Path, required=True)
    signer.add_argument("--trust-anchor", type=pathlib.Path, required=True)
    signer.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "issue-jwt":
            require(stat.S_ISFIFO(os.fstat(sys.stdout.fileno()).st_mode),
                    "admission-jwt-output-not-pipe")
            sys.stdout.buffer.write(issue_jwt(args.run_id))
        else:
            require(not args.output.exists() and not args.output.is_symlink(),
                    "admission-signature-output-exists")
            result = sign_envelope(read_public(args.payload, 2048),
                                   read_public(args.trust_anchor))
            fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "wb") as output:
                output.write(result)
            print(json.dumps({"schema": "fsgg.v1-admission-main-signature-result/1",
                              "envelopeSha256": hashlib.sha256(result).hexdigest()},
                             sort_keys=True, separators=(",", ":")))
        return 0
    except (Refused, OSError, ValueError) as error:
        print(f"v1 admission Main custody refused: {error}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
