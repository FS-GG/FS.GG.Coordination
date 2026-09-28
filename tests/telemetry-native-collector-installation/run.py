#!/usr/bin/env python3
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
MANAGER = ROOT / "eng/telemetry-host-manager/bin/Debug/net10.0/TelemetryHostManager.dll"


class Fixture:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory(prefix="native-collector-install-")
        self.root = pathlib.Path(self.temp.name)
        self.root.chmod(0o700)
        self.codex_home = self.root / "codex-home"
        self.evidence_parent = self.root / "custody"
        for path in (self.codex_home, self.evidence_parent):
            path.mkdir()
            path.chmod(0o700)
        self.evidence = self.evidence_parent / "evidence"
        self.executable = self.root / "codex"
        self.executable.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        self.executable.chmod(0o700)
        self.secret = self.root / "collector.secret"
        self.secret.write_text("s" * 32, encoding="utf-8")
        self.secret.chmod(0o600)
        self.config = self.root / "host.json"
        self.write_config("fsgg.telemetry.host-config/2")

    def close(self):
        self.temp.cleanup()

    def write_config(self, schema, credentials=None):
        if credentials is None:
            credentials = [
                {
                    "Reference": "collector",
                    "SecretFile": str(self.secret),
                    "WorkspaceId": "workspace",
                    "ProducerId": "protected-collector",
                    "StreamId": "native-inventory",
                    "Role": "native-collector",
                    "GrantId": "learn-native-collector",
                    "GrantGeneration": 1,
                    "Revoked": False,
                }
            ]
        self.config.write_text(
            json.dumps({"Schema": schema, "Credentials": credentials}, separators=(",", ":")),
            encoding="utf-8",
        )
        self.config.chmod(0o600)

    def executable_sha256(self):
        return hashlib.sha256(self.executable.read_bytes()).hexdigest()

    def command(self, credential="collector", installation_version=1, executable_sha256=None):
        command = [
            "dotnet",
            str(MANAGER),
            "install-native-collector",
            "--host-config",
            str(self.config),
            "--credential-reference",
            credential,
            "--executable",
            str(self.executable),
            "--codex-home",
            str(self.codex_home),
            "--evidence-root",
            str(self.evidence),
            "--provider",
            "openai",
            "--model",
            "gpt-6-sol",
            "--effort",
            "medium",
        ]

        if installation_version != 1:
            command.extend(["--installation-version", str(installation_version)])
        if executable_sha256 is not None:
            command.extend(["--executable-sha256", executable_sha256])
        return command

    def run(self, credential="collector", installation_version=1, executable_sha256=None):
        return subprocess.run(
            self.command(credential, installation_version, executable_sha256),
            text=True,
            capture_output=True,
            check=False,
        )


class NativeCollectorInstallationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = Fixture()

    def tearDown(self):
        self.fixture.close()

    def test_installs_exact_private_sidecar_and_unknown_receipt_idempotently(self):
        first = self.fixture.run()
        self.assertEqual(0, first.returncode, first.stderr)
        result = json.loads(first.stdout)
        self.assertEqual("installed", result["status"])
        self.assertEqual("unknown", result["sourceVerification"])
        self.assertEqual("unknown", result["snapshotOrigin"])
        self.assertEqual("unknown", result["sharedCostCompleteness"])
        self.assertFalse(result["activationAuthorized"])
        self.assertNotIn("s" * 32, first.stdout)

        sidecar = pathlib.Path(str(self.fixture.config) + ".native-collector.json")
        receipt = pathlib.Path(str(self.fixture.config) + ".native-collector.receipt.json")
        self.assertEqual(0o600, stat.S_IMODE(sidecar.stat().st_mode))
        self.assertEqual(0o600, stat.S_IMODE(receipt.stat().st_mode))
        installed = json.loads(sidecar.read_text(encoding="utf-8"))
        self.assertEqual("fsgg.telemetry.native-collector-installation/1", installed["Schema"])
        self.assertEqual(str(self.fixture.executable), installed["ExecutablePath"])
        self.assertNotIn("Secret", installed)
        self.assertEqual(0o700, stat.S_IMODE(self.fixture.evidence.stat().st_mode))

        second = self.fixture.run()
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual(first.stdout, second.stdout)

    def test_refuses_v1_missing_or_duplicate_collector_authority(self):
        self.fixture.write_config("fsgg.telemetry.host-config/1")
        self.assertEqual(3, self.fixture.run().returncode)
        self.fixture.write_config("fsgg.telemetry.host-config/2", [])
        self.assertEqual(3, self.fixture.run().returncode)
        credential = {
            "Reference": "collector",
            "SecretFile": str(self.fixture.secret),
            "WorkspaceId": "workspace",
            "ProducerId": "protected-collector",
            "StreamId": "native-inventory",
            "Role": "native-collector",
            "GrantId": "learn-native-collector",
            "GrantGeneration": 1,
            "Revoked": False,
        }
        self.fixture.write_config("fsgg.telemetry.host-config/2", [credential, credential])
        self.assertEqual(3, self.fixture.run().returncode)

    def test_refuses_insecure_secret_symlinked_executable_and_executable_drift(self):
        self.fixture.secret.chmod(0o640)
        self.assertEqual(3, self.fixture.run().returncode)
        self.fixture.secret.chmod(0o600)
        target = self.fixture.executable
        linked = self.fixture.root / "linked-codex"
        linked.symlink_to(target)
        command = self.fixture.command()
        command[command.index("--executable") + 1] = str(linked)
        self.assertEqual(3, subprocess.run(command, text=True, capture_output=True).returncode)

        self.assertEqual(0, self.fixture.run().returncode)
        target.write_text("#!/bin/sh\nexit 7\n", encoding="utf-8")
        target.chmod(0o700)
        drift = self.fixture.run()
        self.assertEqual(3, drift.returncode)
        self.assertIn("installed native collector custody differs", drift.stderr)

    def test_installs_exact_v2_pin_and_unknown_receipt_idempotently(self):
        digest = self.fixture.executable_sha256()
        first = self.fixture.run(installation_version=2, executable_sha256=digest)
        self.assertEqual(0, first.returncode, first.stderr)
        result = json.loads(first.stdout)
        self.assertEqual("fsgg.telemetry.native-collector-installation-receipt/2", result["schema"])
        self.assertEqual(digest, result["executableSha256"])
        self.assertEqual("unknown", result["sourceVerification"])
        self.assertEqual("unknown", result["snapshotOrigin"])
        self.assertEqual("unknown", result["sharedCostCompleteness"])
        self.assertFalse(result["activationAuthorized"])

        sidecar = pathlib.Path(str(self.fixture.config) + ".native-collector.json")
        installed = json.loads(sidecar.read_text(encoding="utf-8"))
        self.assertEqual(
            {
                "Schema",
                "CredentialReference",
                "ExecutablePath",
                "CodexHome",
                "EvidenceRoot",
                "Provider",
                "Model",
                "Effort",
                "ExecutableSha256",
            },
            set(installed),
        )
        self.assertEqual("fsgg.telemetry.native-collector-installation/2", installed["Schema"])
        self.assertEqual(digest, installed["ExecutableSha256"])

        second = self.fixture.run(installation_version=2, executable_sha256=digest)
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual(first.stdout, second.stdout)

    def test_v2_refuses_bad_pin_and_changed_executable(self):
        digest = self.fixture.executable_sha256()
        missing = self.fixture.run(installation_version=2)
        self.assertEqual(3, missing.returncode)
        self.assertIn("missing --executable-sha256", missing.stderr)

        invalid_version = self.fixture.run(installation_version=3, executable_sha256=digest)
        self.assertEqual(3, invalid_version.returncode)
        self.assertIn("installation version must be 1 or 2", invalid_version.stderr)

        bad = self.fixture.run(installation_version=2, executable_sha256="0" * 64)
        self.assertEqual(3, bad.returncode)
        self.assertIn("SHA-256 differs", bad.stderr)

        self.assertEqual(
            0,
            self.fixture.run(installation_version=2, executable_sha256=digest).returncode,
        )
        self.fixture.executable.write_text("#!/bin/sh\nexit 9\n", encoding="utf-8")
        self.fixture.executable.chmod(0o700)
        changed = self.fixture.run(installation_version=2, executable_sha256=digest)
        self.assertEqual(3, changed.returncode)
        self.assertIn("SHA-256 differs", changed.stderr)

    def test_v2_refuses_external_or_unsafe_private_roots(self):
        digest = self.fixture.executable_sha256()
        with tempfile.TemporaryDirectory(prefix="native-collector-external-") as external:
            external_home = pathlib.Path(external)
            external_home.chmod(0o700)
            command = self.fixture.command(installation_version=2, executable_sha256=digest)
            command[command.index("--codex-home") + 1] = str(external_home)
            refused = subprocess.run(command, text=True, capture_output=True, check=False)
            self.assertEqual(3, refused.returncode)
            self.assertIn("private descendant", refused.stderr)

        unsafe = self.fixture.root / "unsafe"
        unsafe.mkdir(mode=0o755)
        nested = unsafe / "codex-home"
        nested.mkdir(mode=0o700)
        command = self.fixture.command(installation_version=2, executable_sha256=digest)
        command[command.index("--codex-home") + 1] = str(nested)
        refused = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertEqual(3, refused.returncode)
        self.assertIn("private descendant", refused.stderr)

    def test_v1_remains_default_and_cannot_be_promoted_in_place(self):
        first = self.fixture.run()
        self.assertEqual(0, first.returncode, first.stderr)
        sidecar = pathlib.Path(str(self.fixture.config) + ".native-collector.json")
        self.assertEqual(
            "fsgg.telemetry.native-collector-installation/1",
            json.loads(sidecar.read_text(encoding="utf-8"))["Schema"],
        )

        promoted = self.fixture.run(
            installation_version=2,
            executable_sha256=self.fixture.executable_sha256(),
        )
        self.assertEqual(3, promoted.returncode)
        self.assertIn("cannot be promoted in place", promoted.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
