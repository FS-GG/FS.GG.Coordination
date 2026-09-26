#!/usr/bin/env python3
"""Seal and verify one host-isolated canonical Quint shard receipt."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile


class Refused(RuntimeError):
    pass


HEX40 = re.compile(r"[0-9a-f]{40}")
HEX64 = re.compile(r"[0-9a-f]{64}")


def canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_regular(path: str | pathlib.Path) -> bytes:
    target = pathlib.Path(path)
    if target.is_symlink() or not target.is_file():
        raise Refused(f"regular-file-required:{target}")
    return target.read_bytes()


def read_json(path: str | pathlib.Path) -> dict:
    try:
        value = json.loads(read_regular(path))
    except json.JSONDecodeError as error:
        raise Refused(f"invalid-json:{path}") from error
    if not isinstance(value, dict):
        raise Refused(f"json-object-required:{path}")
    return value


def git(root: pathlib.Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(root), *arguments],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        check=False,
    )
    if completed.returncode != 0:
        raise Refused("git-binding")
    return completed.stdout.strip()


def utc(value: str) -> dt.datetime:
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (TypeError, ValueError) as error:
        raise Refused("invalid-time") from error
    if parsed.tzinfo is None:
        raise Refused("time-zone-required")
    return parsed.astimezone(dt.timezone.utc)


def spki_sha(public_key: pathlib.Path) -> str:
    read_regular(public_key)
    completed = subprocess.run(
        ["openssl", "pkey", "-pubin", "-in", str(public_key), "-outform", "DER"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if completed.returncode != 0 or not completed.stdout:
        raise Refused("public-key-invalid")
    return digest(completed.stdout)


def tracked_digests(root: pathlib.Path, policy: dict, revision: str) -> dict[str, str]:
    result = {}
    paths = policy.get("boundPaths")
    if not isinstance(paths, list) or not paths or paths != sorted(set(paths)):
        raise Refused("bound-path-inventory")
    for relative in paths:
        if not isinstance(relative, str) or relative.startswith("/") or ".." in pathlib.PurePosixPath(relative).parts:
            raise Refused("bound-path-invalid")
        completed = subprocess.run(
            ["git", "-C", str(root), "show", f"{revision}:{relative}"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        if completed.returncode != 0:
            raise Refused(f"bound-path-missing:{relative}")
        result[relative] = digest(completed.stdout)
    return result


def validate_policy(policy: dict) -> None:
    if policy.get("schema") != "fsgg.coordination.offline-formal-pilot-policy/1":
        raise Refused("policy-schema")
    if policy.get("mode") not in ("shadow", "active"):
        raise Refused("policy-mode")
    if policy.get("repository") != "FS-GG/FS.GG.Coordination":
        raise Refused("policy-repository")
    maximum_age = policy.get("maximumAgeSeconds")
    if not isinstance(maximum_age, int) or not 60 <= maximum_age <= 21600:
        raise Refused("policy-maximum-age")
    for name in ("imageSha256", "toolchainArchiveSha256"):
        if not isinstance(policy.get(name), str) or not HEX64.fullmatch(policy[name]):
            raise Refused(f"policy-{name}")
    signer = policy.get("signer")
    if not isinstance(signer, dict) or not isinstance(signer.get("keyId"), str):
        raise Refused("policy-signer")
    anchor = signer.get("publicKeySpkiSha256")
    if anchor is not None and not (isinstance(anchor, str) and HEX64.fullmatch(anchor)):
        raise Refused("policy-signer-anchor")


def validate_receipt(receipt: dict, shard: str) -> None:
    if receipt.get("schema") != "fsgg.coordination.canonical-quint-formal-shard/1":
        raise Refused("receipt-schema")
    if receipt.get("id") != shard or receipt.get("outcome") != "passed":
        raise Refused("receipt-shard-outcome")
    if receipt.get("accountingMethod") != "logical-formal-contribution-and-observed-execution/v1":
        raise Refused("receipt-accounting")
    if receipt.get("negativeControlCount") != 5 or receipt.get("processCounts") != {
        "external": 7,
        "quintCli": 7,
        "apalacheVerify": 3,
    }:
        raise Refused("receipt-process-counts")
    retries = receipt.get("startupRetries")
    executed = receipt.get("executedProcessCounts")
    if not isinstance(retries, dict) or set(retries) != {"total", "verify", "reflectionDeadline", "earlyLifecycleExit"}:
        raise Refused("receipt-retries")
    if any(not isinstance(retries[name], int) or retries[name] < 0 for name in retries):
        raise Refused("receipt-retries")
    if retries["total"] != retries["reflectionDeadline"] + retries["earlyLifecycleExit"] or retries["verify"] > retries["total"]:
        raise Refused("receipt-retries")
    if executed != {
        "external": 10 + retries["total"],
        "quintCli": 10 + retries["total"],
        "apalacheVerify": 3 + retries["verify"],
    }:
        raise Refused("receipt-executed-counts")
    if not isinstance(receipt.get("q2DurationMs"), int) or receipt["q2DurationMs"] <= 0:
        raise Refused("receipt-duration")
    for name in ("toolchainSha256", "quintSha256", "apalacheJarSha256", "sourceSha256", "contractSha256", "preparationSha256", "manifestSha256", "traceSha256", "itfSha256"):
        if not isinstance(receipt.get(name), str) or not HEX64.fullmatch(receipt[name]):
            raise Refused(f"receipt-digest:{name}")


def sign_payload(payload: bytes, key_fd: int) -> bytes:
    completed = subprocess.run(
        [
            "openssl", "dgst", "-sha256", "-sign", f"/proc/self/fd/{key_fd}",
            "-sigopt", "rsa_padding_mode:pss",
        ],
        input=payload,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        pass_fds=(key_fd,),
        check=False,
    )
    if completed.returncode != 0 or not completed.stdout:
        raise Refused("signing-refused")
    return completed.stdout


def verify_signature(public_key: pathlib.Path, payload: bytes, signature: bytes) -> None:
    with tempfile.NamedTemporaryFile() as signature_file:
        signature_file.write(signature)
        signature_file.flush()
        completed = subprocess.run(
            [
                "openssl", "dgst", "-sha256", "-verify", str(public_key),
                "-signature", signature_file.name, "-sigopt", "rsa_padding_mode:pss",
            ],
            input=payload,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    if completed.returncode != 0:
        raise Refused("signature-invalid")


def seal(args: argparse.Namespace) -> None:
    root = pathlib.Path(args.root).resolve()
    policy_bytes = read_regular(args.policy)
    policy = json.loads(policy_bytes)
    validate_policy(policy)
    receipt = read_json(args.receipt)
    shard = policy["pilotShard"]
    validate_receipt(receipt, shard)
    if not HEX40.fullmatch(args.head_sha) or git(root, "rev-parse", "HEAD") != args.head_sha:
        raise Refused("head-binding")
    if not HEX40.fullmatch(args.base_sha):
        raise Refused("base-binding")
    git(root, "merge-base", "--is-ancestor", args.base_sha, args.head_sha)
    tree_sha = git(root, "rev-parse", f"{args.head_sha}^{{tree}}")
    created = utc(args.created_at)
    expires = utc(args.expires_at)
    if expires <= created or int((expires - created).total_seconds()) > policy["maximumAgeSeconds"]:
        raise Refused("evidence-lifetime")
    public_key = pathlib.Path(args.public_key)
    key_sha = spki_sha(public_key)
    configured_sha = policy["signer"]["publicKeySpkiSha256"]
    if configured_sha is not None and configured_sha != key_sha:
        raise Refused("signer-anchor")
    payload = {
        "schema": "fsgg.coordination.offline-formal-evidence/1",
        "repository": policy["repository"],
        "headSha": args.head_sha,
        "baseSha": args.base_sha,
        "treeSha": tree_sha,
        "shard": shard,
        "createdAt": created.isoformat(timespec="seconds").replace("+00:00", "Z"),
        "expiresAt": expires.isoformat(timespec="seconds").replace("+00:00", "Z"),
        "container": {"imageDigest": args.image_digest, "network": "none", "exitCode": 0},
        "toolchainArchiveSha256": digest(read_regular(args.toolchain_archive)),
        "policySha256": digest(policy_bytes),
        "boundPathSha256": tracked_digests(root, policy, args.head_sha),
        "receipt": receipt,
        "receiptSha256": digest(canonical(receipt) + b"\n"),
    }
    if not re.fullmatch(r"(?:[^@\s]+@)?sha256:[0-9a-f]{64}", args.image_digest):
        raise Refused("container-image-must-be-digest-pinned")
    if not args.image_digest.endswith("sha256:" + policy["imageSha256"]):
        raise Refused("container-image-policy-pin")
    if payload["toolchainArchiveSha256"] != policy["toolchainArchiveSha256"]:
        raise Refused("toolchain-policy-pin")
    payload_bytes = canonical(payload)
    signature = sign_payload(payload_bytes, args.private_key_fd)
    envelope = {
        "schema": "fsgg.coordination.offline-formal-envelope/1",
        "payload": payload,
        "signature": {
            "algorithm": "RSA-PSS-SHA256",
            "keyId": policy["signer"]["keyId"],
            "publicKeySpkiSha256": key_sha,
            "valueBase64": base64.b64encode(signature).decode(),
        },
    }
    output = pathlib.Path(args.output)
    if output.exists() and output.is_symlink():
        raise Refused("output-symlink")
    output.write_bytes(canonical(envelope) + b"\n")
    print(canonical({"outcome": "sealed", "headSha": args.head_sha, "shard": shard, "evidenceSha256": digest(canonical(envelope) + b"\n")}).decode())


def verify(args: argparse.Namespace) -> None:
    root = pathlib.Path(args.root).resolve()
    policy_bytes = read_regular(args.policy)
    policy = json.loads(policy_bytes)
    validate_policy(policy)
    if policy["mode"] == "active" and policy["signer"]["publicKeySpkiSha256"] is None:
        raise Refused("active-policy-requires-signer-anchor")
    envelope = read_json(args.evidence)
    if set(envelope) != {"schema", "payload", "signature"} or envelope.get("schema") != "fsgg.coordination.offline-formal-envelope/1":
        raise Refused("envelope-shape")
    payload = envelope.get("payload")
    signature = envelope.get("signature")
    if not isinstance(payload, dict) or not isinstance(signature, dict):
        raise Refused("envelope-shape")
    expected_payload = {"schema", "repository", "headSha", "baseSha", "treeSha", "shard", "createdAt", "expiresAt", "container", "toolchainArchiveSha256", "policySha256", "boundPathSha256", "receipt", "receiptSha256"}
    if set(payload) != expected_payload:
        raise Refused("payload-shape")
    if payload["repository"] != policy["repository"] or payload["headSha"] != args.expected_head or payload["baseSha"] != args.expected_base:
        raise Refused("exact-head-binding")
    if payload["shard"] != policy["pilotShard"]:
        raise Refused("pilot-shard-binding")
    if payload["policySha256"] != digest(policy_bytes) or payload["boundPathSha256"] != tracked_digests(root, policy, args.expected_head):
        raise Refused("tracked-input-binding")
    if git(root, "rev-parse", "HEAD") != args.expected_head or git(root, "rev-parse", f"{args.expected_head}^{{tree}}") != payload["treeSha"]:
        raise Refused("checkout-binding")
    git(root, "merge-base", "--is-ancestor", args.expected_base, args.expected_head)
    if payload["toolchainArchiveSha256"] != digest(read_regular(args.toolchain_archive)):
        raise Refused("toolchain-binding")
    if payload["toolchainArchiveSha256"] != policy["toolchainArchiveSha256"]:
        raise Refused("toolchain-policy-pin")
    container = payload["container"]
    if not isinstance(container, dict) or container.get("network") != "none" or container.get("exitCode") != 0 or not re.fullmatch(r"(?:[^@\s]+@)?sha256:[0-9a-f]{64}", str(container.get("imageDigest", ""))):
        raise Refused("container-binding")
    if not container["imageDigest"].endswith("sha256:" + policy["imageSha256"]):
        raise Refused("container-image-policy-pin")
    receipt = payload["receipt"]
    if not isinstance(receipt, dict) or payload["receiptSha256"] != digest(canonical(receipt) + b"\n"):
        raise Refused("receipt-binding")
    validate_receipt(receipt, policy["pilotShard"])
    now, created, expires = utc(args.now), utc(payload["createdAt"]), utc(payload["expiresAt"])
    if not created <= now < expires or int((expires - created).total_seconds()) > policy["maximumAgeSeconds"]:
        raise Refused("evidence-stale")
    public_key = pathlib.Path(args.public_key)
    key_sha = spki_sha(public_key)
    if signature.get("algorithm") != "RSA-PSS-SHA256":
        raise Refused("signature-algorithm")
    if set(signature) != {"algorithm", "keyId", "publicKeySpkiSha256", "valueBase64"} or signature["keyId"] != policy["signer"]["keyId"] or signature["publicKeySpkiSha256"] != key_sha:
        raise Refused("signature-identity")
    anchor = policy["signer"]["publicKeySpkiSha256"]
    if anchor is not None and anchor != key_sha:
        raise Refused("signature-anchor")
    try:
        signature_bytes = base64.b64decode(signature["valueBase64"], validate=True)
    except (KeyError, ValueError) as error:
        raise Refused("signature-encoding") from error
    verify_signature(public_key, canonical(payload), signature_bytes)
    if args.receipt_output is not None:
        output = pathlib.Path(args.receipt_output)
        if output.is_symlink() or output.exists():
            raise Refused("receipt-output-exists")
        output.write_bytes(canonical(receipt) + b"\n")
    print(canonical({"outcome": "verified", "headSha": args.expected_head, "shard": policy["pilotShard"], "receiptSha256": payload["receiptSha256"]}).decode())


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser()
    commands = root.add_subparsers(dest="command", required=True)
    seal_parser = commands.add_parser("seal")
    for name in ("root", "policy", "receipt", "head-sha", "base-sha", "toolchain-archive", "image-digest", "created-at", "expires-at", "public-key", "output"):
        seal_parser.add_argument("--" + name, required=True)
    seal_parser.add_argument("--private-key-fd", required=True, type=int)
    seal_parser.set_defaults(run=seal)
    verify_parser = commands.add_parser("verify")
    for name in ("root", "policy", "evidence", "expected-head", "expected-base", "toolchain-archive", "public-key", "now"):
        verify_parser.add_argument("--" + name, required=True)
    verify_parser.add_argument("--receipt-output")
    verify_parser.set_defaults(run=verify)
    return root


def main() -> int:
    try:
        args = parser().parse_args()
        args.run(args)
        return 0
    except (Refused, OSError, json.JSONDecodeError) as error:
        print(f"OFFLINE_FORMAL_EVIDENCE_REFUSED reason={error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
