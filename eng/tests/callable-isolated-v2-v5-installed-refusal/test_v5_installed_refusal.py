"""Independent fake installed-runner and audit refusals for the v5 ZIP."""

import copy
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_v5_no_grant_selection as candidate
import callable_isolated_v2_v5_installed_refusal as installed

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
PATH = "/opt/fsgg/v5/fsgg-callable-isolated-v2-v5-no-grant.pyz"


class Port:
    def __init__(self, scope, record):
        self.scope_data = scope
        self.record = record
        self.calls = []

    def scope(self):
        return self.scope_data

    def read(self, *key):
        self.calls.append(key)
        return copy.deepcopy(self.record)


def fixture():
    entry = (ENG / "callable_isolated_v2_v5_vault_no_grant_entry.py").read_bytes()
    import build_callable_isolated_v2_v5_no_grant as builder
    archive = builder._archive(entry)
    selection = candidate.Selection(
        candidate.PINNED_BYTES["archive"], candidate.PINNED_BYTES["manifest"],
        "1" * 40, "2" * 40, 300, 1, 305, 100, 302, 301, 304, 303,
        "2026-09-25T11:57:00Z", "2026-09-25T12:10:00Z",
        "source-reader", "a" * 64, "review-reader", "b" * 64)
    selected = {"repositoryId": 100, "artifactId": 302,
                "runId": 700, "runAttempt": 1, "runnerActorId": 701,
                "auditActorId": 703, "auditEventId": 702,
                "imageDigest": "3" * 64, "interpreterSha256": "4" * 64,
                "runtimeClosureSha256": "5" * 64, "installPath": PATH}
    common = {"repository": candidate.REPOSITORY,
              "repositoryId": selected["repositoryId"],
              "revision": selection.revision,
              "sourceTree": selection.source_tree,
              "producerRunId": selection.producer_run_id,
              "reviewEventId": selection.review_event_id,
              "artifactId": selected["artifactId"],
              "manifestSha256": selection.manifest_sha256,
              "archiveSha256": selection.archive_sha256,
              "runId": selected["runId"], "runAttempt": 1,
              "installPath": PATH}
    obj = {"device": 11, "inode": 12, "size": len(archive),
           "sha256": hashlib.sha256(archive).hexdigest()}
    refusal = {"schema":
               "fsgg.coordination.callable-isolated-v2-v5-vault-refusal/1",
               "reason": "no-grant", "authorized": False,
               "canDispatch": False, "liveEffects": 0}
    stdout = (json.dumps(refusal, sort_keys=True, separators=(",", ":"))
              + "\n").encode()
    probe = {"schema": installed.PROBE_SCHEMA, "complete": True,
             "principalId": "probe-reader", "credentialId": "6" * 64,
             **common, "runnerActorId": 701,
             "imageDigest": "3" * 64, "interpreterSha256": "4" * 64,
             "runtimeClosureSha256": "5" * 64, "realPath": PATH,
             "symlink": False, "before": copy.deepcopy(obj),
             "after": copy.deepcopy(obj), "archiveBytes": archive,
             "argv": ["python3", "-I", "-S", PATH, "execute-native-pull"],
             "exitCode": 78, "stdout": stdout, "stderr": b"",
             "startedAt": "2026-09-25T11:58:00Z",
             "completedAt": "2026-09-25T11:58:01Z",
             "observedAt": "2026-09-25T11:58:02Z"}
    audit = {"schema": installed.AUDIT_SCHEMA, "complete": True,
             "principalId": "audit-reader", "credentialId": "7" * 64,
             **common, "eventId": 702, "auditActorId": 703,
             "runnerActorId": 701, "object": copy.deepcopy(obj),
             "counts": {"tokenReads": 0, "journalReads": 0,
                        "journalWrites": 0, "casWrites": 0,
                        "providerPosts": 0, "providerPuts": 0,
                        "cleanupAttempts": 0},
             "startedAt": "2026-09-25T11:58:00Z",
             "completedAt": "2026-09-25T11:58:01Z",
             "observedAt": "2026-09-25T11:58:03Z"}
    probe_scope = {"principalId": "probe-reader", "credentialId": "6" * 64,
                   "repository": candidate.REPOSITORY, "repositoryId": 100,
                   "permissions": ["read-installed-probe"],
                   "expiresAt": "2026-09-25T12:10:00Z"}
    audit_scope = {"principalId": "audit-reader", "credentialId": "7" * 64,
                   "repository": candidate.REPOSITORY, "repositoryId": 100,
                   "permissions": ["read-no-effect-audit"],
                   "expiresAt": "2026-09-25T12:10:00Z"}
    return (selection, selected, archive, Port(probe_scope, probe),
            Port(audit_scope, audit))


class InstalledRefusalTests(unittest.TestCase):
    def observe(self, mutate=None):
        s, chosen, archive, probe, audit = fixture()
        if mutate:
            mutate(s, chosen, archive, probe, audit)
        return installed.qualify(s, chosen, probe, audit, NOW)

    def test_matching_fake_runner_stays_closed(self):
        result = self.observe()
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_wrong_runner_path_image_or_artifact_refuses(self):
        for key, value in (("realPath", "/tmp/foreign.pyz"),
                           ("imageDigest", "8" * 64),
                           ("artifactId", 999),
                           ("reviewEventId", 999),
                           ("archiveSha256", "9" * 64)):
            with self.subTest(key=key):
                def wrong(_s, _c, _b, probe, _audit):
                    probe.record[key] = value
                with self.assertRaises(installed.Refused):
                    self.observe(wrong)

    def test_replacement_or_wrong_stdout_refuses(self):
        for key, value in (("symlink", True), ("exitCode", 0),
                           ("stdout", b"success\n"),
                           ("archiveBytes", b"foreign")):
            with self.subTest(key=key):
                def wrong(_s, _c, _b, probe, _audit):
                    probe.record[key] = value
                with self.assertRaises(installed.Refused):
                    self.observe(wrong)
        with self.assertRaises(installed.Refused):
            self.observe(lambda _s, _c, _b, p, _a:
                         p.record["after"].update(inode=99))

    def test_self_audit_nonzero_effect_or_stale_refuses(self):
        with self.assertRaises(installed.Refused):
            self.observe(lambda _s, c, _b, _p, a:
                         a.record.update(auditActorId=c["runnerActorId"]))
        with self.assertRaises(installed.Refused):
            self.observe(lambda _s, _c, _b, _p, a:
                         a.record["counts"].update(providerPosts=1))
        with self.assertRaises(installed.Refused):
            self.observe(lambda _s, _c, _b, _p, a:
                         a.record.update(observedAt="2026-09-25T11:00:00Z"))

    def test_frozen_selection_mutated_by_fake_reader_refuses(self):
        s, chosen, _archive, probe, audit = fixture()
        original_read = probe.read

        def mutate_then_read(*key):
            object.__setattr__(s, "artifact_id", 999)
            probe.record["artifactId"] = 999
            audit.record["artifactId"] = 999
            return original_read(*key)

        probe.read = mutate_then_read
        with self.assertRaises(installed.Refused):
            installed.qualify(s, chosen, probe, audit, NOW)

    def test_probe_cannot_reuse_source_reader_identity(self):
        s, chosen, _archive, probe, audit = fixture()
        probe.scope_data["principalId"] = s.source_reader_principal
        probe.record["principalId"] = s.source_reader_principal
        with self.assertRaises(installed.Refused):
            installed.qualify(s, chosen, probe, audit, NOW)

    def test_no_file_token_socket_or_journal_access(self):
        s, chosen, _archive, probe, audit = fixture()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("journal")):
            result = installed.qualify(s, chosen, probe, audit, NOW)
        self.assertEqual(result.live_effects, 0)


if __name__ == "__main__":
    unittest.main()
