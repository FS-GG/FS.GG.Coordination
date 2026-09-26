#!/usr/bin/env python3
"""Exercise the dormant hosted join against local protected and evidence repositories."""

import datetime as dt
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
SHARD = "authority-reconciliation"


def call(args, *, cwd=None, env=None, expected=0, pass_fds=()):
    result = subprocess.run(args, cwd=cwd, env=env, pass_fds=pass_fds,
                            text=True, capture_output=True, check=False)
    if result.returncode != expected:
        raise AssertionError(f"exit={result.returncode} expected={expected}\n{result.stdout}\n{result.stderr}")
    return result


def git(root, *args):
    return call(["git", "-C", str(root), *args]).stdout.strip()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"


def main():
    with tempfile.TemporaryDirectory(prefix="offline-formal-join-") as directory:
        scratch = pathlib.Path(directory)
        private = scratch / "private.pem"
        call(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048",
              "-out", str(private)])
        trusted = scratch / "trusted"
        call(["git", "clone", "--quiet", "--no-hardlinks", str(ROOT), str(trusted)])
        git(trusted, "config", "user.name", "Offline join test")
        git(trusted, "config", "user.email", "offline-join@example.invalid")
        public = trusted / "eng/offline-formal-pilot.pub.pem"
        call(["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)])
        # OpenSSL DER is binary; capture it without text conversion.
        der = subprocess.run(["openssl", "pkey", "-pubin", "-in", str(public), "-outform", "DER"],
                             capture_output=True, check=True).stdout
        toolchain = scratch / "toolchain.tar.gz"
        toolchain.write_bytes(b"same-run-toolchain-fixture")
        policy_path = trusted / "eng/offline-formal-pilot.json"
        policy = json.loads(policy_path.read_text())
        policy["mode"] = "active"
        policy["signer"] = {"keyId": "local-join-fixture",
                            "publicKeySpkiSha256": hashlib.sha256(der).hexdigest()}
        policy["imageSha256"] = "b" * 64
        policy["toolchainArchiveSha256"] = hashlib.sha256(toolchain.read_bytes()).hexdigest()
        policy_path.write_bytes(canonical(policy))
        git(trusted, "add", "eng/offline-formal-pilot.json", "eng/offline-formal-pilot.pub.pem")
        git(trusted, "commit", "-q", "-m", "Fixture protected base")
        base = git(trusted, "rev-parse", "HEAD")

        candidate = scratch / "candidate"
        call(["git", "clone", "--quiet", "--no-hardlinks", str(trusted), str(candidate)])
        git(candidate, "config", "user.name", "Offline join test")
        git(candidate, "config", "user.email", "offline-join@example.invalid")
        (candidate / "pilot-candidate.txt").write_text("one exact candidate\n")
        git(candidate, "add", "pilot-candidate.txt")
        git(candidate, "commit", "-q", "-m", "Fixture candidate")
        head = git(candidate, "rev-parse", "HEAD")

        receipt = {
            "schema": "fsgg.coordination.canonical-quint-formal-shard/1",
            "id": SHARD, "outcome": "passed",
            "accountingMethod": "logical-formal-contribution-and-observed-execution/v1",
            "negativeControlCount": 5,
            "processCounts": {"external": 7, "quintCli": 7, "apalacheVerify": 3},
            "executedProcessCounts": {"external": 10, "quintCli": 10, "apalacheVerify": 3},
            "startupRetries": {"total": 0, "verify": 0, "reflectionDeadline": 0, "earlyLifecycleExit": 0},
            "q2DurationMs": 1,
        }
        for field in ("toolchainSha256", "quintSha256", "apalacheJarSha256", "sourceSha256",
                      "contractSha256", "preparationSha256", "manifestSha256", "traceSha256", "itfSha256"):
            receipt[field] = "a" * 64
        receipt_path = scratch / "source-receipt.json"
        receipt_path.write_bytes(canonical(receipt))
        evidence = scratch / "envelope.json"
        created = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
        expires = created + dt.timedelta(hours=1)
        with private.open("rb") as key:
            call(["python3", str(trusted / "eng/offline-formal-evidence.py"), "seal",
                  "--root", str(candidate), "--policy", str(policy_path), "--receipt", str(receipt_path),
                  "--head-sha", head, "--base-sha", base, "--toolchain-archive", str(toolchain),
                  "--image-digest", "sha256:" + "b" * 64,
                  "--created-at", created.isoformat().replace("+00:00", "Z"),
                  "--expires-at", expires.isoformat().replace("+00:00", "Z"),
                  "--public-key", str(public), "--output", str(evidence),
                  "--private-key-fd", str(key.fileno())], pass_fds=(key.fileno(),))

        obligation = scratch / "candidate-obligation.json"
        obligation.write_bytes(canonical({"candidate": head, "baseRevision": base,
                                          "obligationSha256": "c" * 64,
                                          "sourceSha256": "a" * 64,
                                          "compiledContractSha256": "a" * 64}))
        plan = scratch / "partition-plan.json"
        plan.write_bytes(canonical({"schema": "fsgg.coordination.coherent-partition-plan/1",
                                    "candidateObligationSha256": "c" * 64,
                                    "partitions": [{"index": 1, "obligations": ["formal"]}]}))
        environment = os.environ.copy()
        environment.update({"FSGG_TRUSTED_ROOT": str(trusted), "FSGG_CANDIDATE_ROOT": str(candidate),
                            "FSGG_CANDIDATE_SHA": head, "FSGG_BASE_SHA": base,
                            "FSGG_OFFLINE_EVIDENCE": str(evidence),
                            "FSGG_OFFLINE_PUBLIC_KEY": str(public),
                            "FSGG_CANDIDATE_OBLIGATION": str(obligation),
                            "FSGG_PARTITION_PLAN": str(plan),
                            "FSGG_OFFLINE_FRAGMENT_ROOT": str(scratch / "fragment")})
        call(["bash", str(trusted / "eng/offline-formal-join.sh")], env=environment)
        fragment = scratch / "fragment"
        assert sorted(path.name for path in fragment.iterdir()) == [
            "candidate-obligation.json", "partition-plan.json", "receipt.json"]
        assert (fragment / "receipt.json").read_bytes() == receipt_path.read_bytes()
        assert (fragment / "candidate-obligation.json").read_bytes() == obligation.read_bytes()
        assert (fragment / "partition-plan.json").read_bytes() == plan.read_bytes()

        wrong_head = dict(environment, FSGG_CANDIDATE_SHA="0" * 40,
                          FSGG_OFFLINE_FRAGMENT_ROOT=str(scratch / "wrong-head-fragment"))
        call(["bash", str(trusted / "eng/offline-formal-join.sh")],
             env=wrong_head, expected=1)
        assert not (scratch / "wrong-head-fragment").exists()
        tampered = json.loads(evidence.read_text())
        tampered["payload"]["receipt"]["q2DurationMs"] = 2
        tampered_path = scratch / "tampered-envelope.json"
        tampered_path.write_bytes(canonical(tampered))
        altered = dict(environment, FSGG_OFFLINE_EVIDENCE=str(tampered_path),
                       FSGG_OFFLINE_FRAGMENT_ROOT=str(scratch / "tampered-fragment"))
        call(["bash", str(trusted / "eng/offline-formal-join.sh")],
             env=altered, expected=1)
        assert not (scratch / "tampered-fragment/receipt.json").exists()

        remote = scratch / "evidence.git"
        call(["git", "init", "--bare", "-q", str(remote)])
        publisher = scratch / "publisher"
        call(["git", "clone", "--quiet", str(remote), str(publisher)])
        git(publisher, "config", "user.name", "Offline join test")
        git(publisher, "config", "user.email", "offline-join@example.invalid")
        shutil.copyfile(evidence, publisher / "envelope.json")
        git(publisher, "add", "envelope.json")
        git(publisher, "commit", "-q", "-m", "Fixture evidence")
        ref = f"refs/heads/evidence/coordination-offline-formal-pilot/{head}/{base}/{SHARD}"
        git(publisher, "push", "-q", "origin", f"HEAD:{ref}")
        fetched = scratch / "fetched-envelope.json"
        fetch_env = dict(environment, FSGG_OFFLINE_EVIDENCE=str(fetched), RUNNER_TEMP=str(scratch),
                         GIT_CONFIG_COUNT="1",
                         GIT_CONFIG_KEY_0="url.file://" + str(remote) + ".insteadOf",
                         GIT_CONFIG_VALUE_0="https://github.com/FS-GG/.github.git")
        call(["bash", str(trusted / "eng/offline-formal-fetch.sh")],
             env=fetch_env)
        assert fetched.read_bytes() == evidence.read_bytes()

    print("OFFLINE_FORMAL_HOSTED_JOIN_TESTS_OK good=2 refusal=2")


if __name__ == "__main__":
    main()
