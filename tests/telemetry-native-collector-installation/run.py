#!/usr/bin/env python3
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

    def command(self, credential="collector"):
        return [
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

    def run(self, credential="collector"):
        return subprocess.run(self.command(credential), text=True, capture_output=True, check=False)


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


if __name__ == "__main__":
    unittest.main(verbosity=2)
