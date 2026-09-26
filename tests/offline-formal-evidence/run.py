#!/usr/bin/env python3
from __future__ import annotations

import base64
import datetime as dt
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
TOOL = ROOT / "eng/offline-formal-evidence.py"
SOURCE_POLICY = ROOT / "eng/offline-formal-pilot.json"


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def run(arguments, *, pass_fds=(), expected=0):
    completed = subprocess.run(
        ["python3", str(TOOL), *arguments],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        pass_fds=pass_fds,
        check=False,
    )
    if completed.returncode != expected:
        raise AssertionError(
            f"expected exit {expected}, got {completed.returncode}\nstdout={completed.stdout}\nstderr={completed.stderr}"
        )
    return completed


def spki_sha(public_key):
    der = subprocess.run(
        ["openssl", "pkey", "-pubin", "-in", str(public_key), "-outform", "DER"],
        stdout=subprocess.PIPE,
        check=True,
    ).stdout
    return hashlib.sha256(der).hexdigest()


def receipt(shard):
    value = {
        "schema": "fsgg.coordination.canonical-quint-formal-shard/1",
        "id": shard,
        "outcome": "passed",
        "accountingMethod": "logical-formal-contribution-and-observed-execution/v1",
        "negativeControlCount": 5,
        "processCounts": {"external": 7, "quintCli": 7, "apalacheVerify": 3},
        "executedProcessCounts": {"external": 10, "quintCli": 10, "apalacheVerify": 3},
        "startupRetries": {"total": 0, "verify": 0, "reflectionDeadline": 0, "earlyLifecycleExit": 0},
        "q2DurationMs": 1,
    }
    for name in (
        "toolchainSha256", "quintSha256", "apalacheJarSha256", "sourceSha256",
        "contractSha256", "preparationSha256", "manifestSha256", "traceSha256", "itfSha256",
    ):
        value[name] = "a" * 64
    return value


def main():
    with tempfile.TemporaryDirectory(prefix="fsgg-offline-evidence-test-") as scratch_text:
        scratch = pathlib.Path(scratch_text)
        private_key = scratch / "private.pem"
        public_key = scratch / "public.pem"
        subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(private_key)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        subprocess.run(["openssl", "pkey", "-in", str(private_key), "-pubout", "-out", str(public_key)], stdout=subprocess.DEVNULL, check=True)
        policy = json.loads(SOURCE_POLICY.read_text())
        policy["signer"]["publicKeySpkiSha256"] = spki_sha(public_key)
        policy_path = scratch / "policy.json"
        policy_path.write_bytes(canonical(policy) + b"\n")
        receipt_path = scratch / "receipt.json"
        receipt_path.write_bytes(canonical(receipt(policy["pilotShard"])) + b"\n")
        toolchain = scratch / "toolchain.tar.gz"
        toolchain.write_bytes(b"pinned-toolchain-fixture")
        policy["toolchainArchiveSha256"] = hashlib.sha256(toolchain.read_bytes()).hexdigest()
        policy["imageSha256"] = "b" * 64
        policy_path.write_bytes(canonical(policy) + b"\n")
        evidence = scratch / "evidence.json"
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, stdout=subprocess.PIPE, check=True).stdout.strip()
        created = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
        expires = created + dt.timedelta(minutes=30)
        common = [
            "--root", str(ROOT), "--policy", str(policy_path), "--receipt", str(receipt_path),
            "--head-sha", head, "--base-sha", head, "--toolchain-archive", str(toolchain),
            "--image-digest", "sha256:" + "b" * 64,
            "--created-at", created.isoformat().replace("+00:00", "Z"),
            "--expires-at", expires.isoformat().replace("+00:00", "Z"),
            "--public-key", str(public_key), "--output", str(evidence),
        ]

        def with_option(arguments, name, value):
            changed = list(arguments)
            changed[changed.index(name) + 1] = value
            return changed

        with private_key.open("rb") as key:
            run(
                ["seal", *with_option(common, "--image-digest", "sha256:" + "c" * 64), "--private-key-fd", str(key.fileno())],
                pass_fds=(key.fileno(),), expected=1,
            )
            wrong_toolchain = scratch / "wrong-toolchain.tar.gz"
            wrong_toolchain.write_bytes(b"caller-selected-toolchain")
            run(
                ["seal", *with_option(common, "--toolchain-archive", str(wrong_toolchain)), "--private-key-fd", str(key.fileno())],
                pass_fds=(key.fileno(),), expected=1,
            )
            run(["seal", *common, "--private-key-fd", str(key.fileno())], pass_fds=(key.fileno(),))

        def verify_arguments(evidence_path=evidence, expected_head=head, now=created):
            return [
                "verify", "--root", str(ROOT), "--policy", str(policy_path), "--evidence", str(evidence_path),
                "--expected-head", expected_head, "--expected-base", head, "--toolchain-archive", str(toolchain),
                "--public-key", str(public_key), "--now", now.isoformat().replace("+00:00", "Z"),
            ]

        run(verify_arguments())
        joined_receipt = scratch / "joined-receipt.json"
        run([*verify_arguments(), "--receipt-output", str(joined_receipt)])
        assert joined_receipt.read_bytes() == receipt_path.read_bytes()
        run(verify_arguments(expected_head="0" * 40), expected=1)

        original = json.loads(evidence.read_text())
        wrong_shard = json.loads(json.dumps(original))
        wrong_shard["payload"]["shard"] = "claim-election"
        wrong_shard_path = scratch / "wrong-shard.json"
        wrong_shard_path.write_bytes(canonical(wrong_shard) + b"\n")
        run(verify_arguments(evidence_path=wrong_shard_path), expected=1)

        tampered = json.loads(json.dumps(original))
        raw_signature = bytearray(base64.b64decode(tampered["signature"]["valueBase64"]))
        raw_signature[0] ^= 1
        tampered["signature"]["valueBase64"] = base64.b64encode(raw_signature).decode()
        tampered_path = scratch / "tampered.json"
        tampered_path.write_bytes(canonical(tampered) + b"\n")
        run(verify_arguments(evidence_path=tampered_path), expected=1)
        rejected_receipt = scratch / "rejected-receipt.json"
        run([*verify_arguments(evidence_path=tampered_path), "--receipt-output", str(rejected_receipt)], expected=1)
        assert not rejected_receipt.exists()

        run(verify_arguments(now=expires + dt.timedelta(seconds=1)), expected=1)

        exported = scratch / "exported"
        subprocess.run(
            [str(ROOT / "eng/prepare-offline-formal-source.sh"), str(ROOT), head, str(exported)],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            text=True,
            check=True,
        )
        exported_tree = subprocess.run(
            ["git", "write-tree"], cwd=exported, text=True, stdout=subprocess.PIPE, check=True
        ).stdout.strip()
        source_tree = subprocess.run(
            ["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True, stdout=subprocess.PIPE, check=True
        ).stdout.strip()
        assert exported_tree == source_tree
        assert subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=exported, text=True, stdout=subprocess.PIPE, check=True
        ).stdout.strip() == head
        tracked_qnt = subprocess.run(
            ["git", "ls-files", "*.qnt"], cwd=exported, text=True, stdout=subprocess.PIPE, check=True
        ).stdout.strip()
        assert tracked_qnt == ""

        trusted = scratch / "trusted"
        candidate = scratch / "candidate"
        for target in (trusted, candidate):
            subprocess.run(["git", "clone", "--quiet", "--no-hardlinks", str(ROOT), str(target)], check=True)
        trusted_key = trusted / "pilot-public.pem"
        candidate_key = candidate / "candidate-public.pem"
        shutil.copyfile(public_key, trusted_key)
        shutil.copyfile(public_key, candidate_key)
        wrapper = ROOT / "eng/offline-formal-verify.sh"

        def wrapper_refuses(trusted_root, selected_key, reason):
            environment = os.environ.copy()
            environment.update({
                "FSGG_TRUSTED_ROOT": str(trusted_root),
                "FSGG_CANDIDATE_ROOT": str(candidate),
                "FSGG_OFFLINE_EVIDENCE": str(scratch / "absent-evidence.json"),
                "FSGG_OFFLINE_PUBLIC_KEY": str(selected_key),
                "FSGG_OFFLINE_TOOLCHAIN_ARCHIVE": str(toolchain),
                "FSGG_CANDIDATE_SHA": head,
                "FSGG_BASE_SHA": head,
            })
            completed = subprocess.run(
                [str(wrapper)], cwd=ROOT, env=environment, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
            )
            assert completed.returncode != 0
            assert reason in completed.stderr, completed.stderr

        wrapper_refuses(trusted, candidate_key, "public-key-outside-protected-root")
        candidate_policy = candidate / "eng/offline-formal-pilot.json"
        candidate_policy.write_text("{}\n")
        wrapper_refuses(candidate, candidate_key, "protected-base-drift")
        subprocess.run(["git", "checkout", "--quiet", "--", "eng/offline-formal-pilot.json"], cwd=candidate, check=True)
        candidate_verifier = candidate / "eng/offline-formal-evidence.py"
        candidate_verifier.write_text("#!/usr/bin/env python3\nraise SystemExit(0)\n")
        wrapper_refuses(candidate, candidate_key, "protected-base-drift")

    print("OFFLINE_FORMAL_EVIDENCE_TESTS_OK envelopeCases=7 policyPinCases=2 trustBoundaryCases=3 sourceExportCases=1")


if __name__ == "__main__":
    main()
