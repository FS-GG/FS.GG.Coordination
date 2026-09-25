import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = ROOT / "eng/validate-github-sandbox-mint-proof.py"
LIVE = ROOT / "eng/execute-github-sandbox-live.sh"
QUALIFY = ROOT / "eng/qualify-github-sandbox-closure.sh"
TOKEN = "fake-sandbox-token-secret-sentinel-123456789"


def proof():
    return {
        "schema": "fsgg.github-substrate-v2.sandbox-mint-grants/1",
        "appId": 4166418,
        "appSlug": "fs-gg-cross-repo-dispatch",
        "actor": {"login": "fs-gg-cross-repo-dispatch[bot]", "databaseId": 297630107},
        "installationId": 160000000,
        "repositorySelection": "selected",
        "repository": {"id": 1353050537, "nodeId": "R_kgDOUKXpqQ",
                       "fullName": "FS-GG/FS.GG.GitHub.Substrate.Sandbox"},
        "permissions": {"administration": "write", "contents": "write", "issues": "write",
                        "pull_requests": "write", "organization_projects": "write",
                        "metadata": "read"},
        "expiresAt": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1))
            .isoformat(timespec="seconds").replace("+00:00", "Z"),
        "tokenSha256": hashlib.sha256(TOKEN.encode()).hexdigest(),
        "mintResponseSha256": "a" * 64,
        "viewerResponseSha256": "b" * 64,
    }


class MintProofHandoffTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.path = self.directory / "mint-grants.json"
        self.calls = self.directory / "gh-called"
        stub = self.directory / "gh"
        stub.write_text("#!/bin/sh\nprintf '%s' \"$GH_HOST\" > \"$FSGG_TEST_GH_CALLS\"\nexit 91\n")
        stub.chmod(0o700)
        self.environment = dict(os.environ)
        self.environment.update({
            "FSGG_SANDBOX_MINT_PROOF": str(self.path),
            "FSGG_SANDBOX_TOKEN": TOKEN,
            "FSGG_SANDBOX_OWNER": "FS-GG",
            "FSGG_SANDBOX_REPOSITORY": "FS.GG.GitHub.Substrate.Sandbox",
            "FSGG_SANDBOX_REPOSITORY_NODE_ID": "R_kgDOUKXpqQ",
            "FSGG_SANDBOX_PROJECT_NODE_ID": "PVT_kwDOEYAWY84BiESo",
            "FSGG_SANDBOX_PURPOSE": "fsgg-sandbox-gs2-04-9",
            "FSGG_SANDBOX_ACTOR": "fs-gg-cross-repo-dispatch[bot]",
            "FSGG_SANDBOX_ACTOR_ID": "297630107",
            "FSGG_CANDIDATE_SHA": "a" * 40,
            "FSGG_SANDBOX_RUN_NONCE": "offline-mint-handoff-control",
            "FSGG_SANDBOX_EVIDENCE_DIR": str(self.directory / "evidence"),
            "FSGG_SANDBOX_MODE": "live",
            "FSGG_TEST_GH_CALLS": str(self.calls),
            "PATH": str(self.directory) + os.pathsep + os.environ["PATH"],
        })
        self.write(proof())

    def write(self, value):
        self.path.write_text(json.dumps(value, separators=(",", ":")) + "\n")

    def run_validator(self):
        return subprocess.run([sys.executable, str(VALIDATOR)], env=self.environment,
                              capture_output=True, text=True, check=False)

    def run_live(self):
        return subprocess.run(["bash", str(LIVE), "execute"], env=self.environment,
                              capture_output=True, text=True, check=False)

    def assert_refused_before_gh(self, result):
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.calls.exists(), "provider command was reached")
        self.assertNotIn(TOKEN, result.stdout + result.stderr)

    def test_matching_protected_schema_is_consistent_offline(self):
        result = self.run_validator()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("handoff consistent", result.stdout)
        self.assertNotIn(TOKEN, result.stdout + result.stderr)

    def test_valid_proof_pins_provider_host_before_first_call(self):
        self.environment["GH_HOST"] = "foreign.example.invalid"
        result = self.run_live()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("github.com", self.calls.read_text())
        self.assertNotIn(TOKEN, result.stdout + result.stderr)

    def test_missing_proof_refuses_live_route_before_provider(self):
        self.path.unlink()
        result = subprocess.run(["bash", str(QUALIFY), str(ROOT)],
                                env=self.environment, capture_output=True,
                                text=True, check=False)
        self.assert_refused_before_gh(result)
        self.assertIn("GSQ-MINT-PROOF: refused", result.stderr)

    def test_token_digest_mismatch_refuses_before_provider(self):
        changed = proof()
        changed["tokenSha256"] = "0" * 64
        self.write(changed)
        self.assert_refused_before_gh(self.run_live())

    def test_missing_token_refuses_before_provider_without_secret_output(self):
        self.environment.pop("FSGG_SANDBOX_TOKEN")
        result = self.run_live()
        self.assert_refused_before_gh(result)
        self.assertNotIn(str(self.path), result.stdout + result.stderr)

    def test_unexpected_proof_field_and_invalid_extra_level_refuse(self):
        changed = proof()
        changed["token"] = TOKEN
        self.write(changed)
        self.assert_refused_before_gh(self.run_live())
        changed = proof()
        changed["permissions"]["metadata"] = "admin"
        self.write(changed)
        self.assert_refused_before_gh(self.run_live())

    def test_installation_selection_and_hash_shape_refuse(self):
        for key, value in (("installationId", 0),
                           ("repositorySelection", "all"),
                           ("mintResponseSha256", "bad"),
                           ("viewerResponseSha256", "bad")):
            with self.subTest(key=key):
                changed = proof()
                changed[key] = value
                self.write(changed)
                self.assert_refused_before_gh(self.run_live())

    def test_foreign_app_or_actor_refuses_before_provider(self):
        for key, value in (("appId", 4166419), ("appSlug", "other-app"),
                           ("actor", {"login": "EHotwagner", "databaseId": 1645484})):
            with self.subTest(key=key):
                changed = proof()
                changed[key] = value
                self.write(changed)
                self.assert_refused_before_gh(self.run_live())

    def test_foreign_repo_or_broad_grant_refuses_before_provider(self):
        changed = proof()
        changed["repository"]["id"] = 1
        self.write(changed)
        self.assert_refused_before_gh(self.run_live())
        changed = proof()
        changed["permissions"]["members"] = "write"
        self.write(changed)
        self.assert_refused_before_gh(self.run_live())

    def test_expired_or_unbounded_proof_refuses_before_provider(self):
        for hours in (-1, 3):
            with self.subTest(hours=hours):
                changed = proof()
                changed["expiresAt"] = (dt.datetime.now(dt.timezone.utc)
                                        + dt.timedelta(hours=hours)).isoformat().replace("+00:00", "Z")
                self.write(changed)
                self.assert_refused_before_gh(self.run_live())

    def test_duplicate_and_nonfinite_json_refuse(self):
        self.path.write_text('{"schema":"x","schema":"y"}')
        self.assert_refused_before_gh(self.run_live())
        self.path.write_text('{"value":NaN}')
        self.assert_refused_before_gh(self.run_live())
        self.path.write_text("[" * 1500 + "0" + "]" * 1500)
        self.assert_refused_before_gh(self.run_live())

    def test_symlink_and_oversize_refuse(self):
        other = self.directory / "other.json"
        other.write_text(json.dumps(proof()))
        self.path.unlink()
        self.path.symlink_to(other)
        self.assert_refused_before_gh(self.run_live())
        self.path.unlink()
        self.path.write_text("x" * (64 * 1024 + 1))
        self.assert_refused_before_gh(self.run_live())


if __name__ == "__main__":
    unittest.main()
