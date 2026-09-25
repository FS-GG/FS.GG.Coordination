"""Independent fake runner and audit observations never confer authority."""

import copy
import datetime as dt
import hashlib
import json
import os
import pathlib
import socket
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_effect_runner_readback as readback
import callable_isolated_v2_effect_approval_identity as approval
import callable_isolated_v2_effect_release_preflight as release

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)
ARCHIVE = b"synthetic-closed-archive"
SHA = lambda raw: hashlib.sha256(raw).hexdigest()
PATH = "/opt/fsgg/closed-v2/closed-effect.pyz"
REFUSAL = (json.dumps({"authorized": False, "canDispatch": False,
    "liveEffects": 0, "reason": "no-grant",
    "schema": "fsgg.coordination.callable-isolated-v2-effect-scaffold-refusal/1"},
    sort_keys=True, separators=(",", ":")) + "\n").encode()


class Port:
    def __init__(self, scope, record):
        self.scopes = [copy.deepcopy(scope), copy.deepcopy(scope)]
        self.record = copy.deepcopy(record)
        self.reads = []

    def scope(self):
        return self.scopes.pop(0)

    def read_probe(self, run_id, attempt):
        self.reads.append((run_id, attempt))
        return copy.deepcopy(self.record)

    def read_audit(self, event_id):
        self.reads.append(event_id)
        return copy.deepcopy(self.record)


def fixture():
    result = release.PreflightResult("a" * 40, "b" * 40,
        "c" * 64, 404, SHA(ARCHIVE), 202, 1, 303, 606, 505, 101)
    selected = {"runId": 707, "runAttempt": 1, "runnerActorId": 808,
        "auditActorId": 1001,
        "auditEventId": 909, "repositoryId": 77,
        "identityEventId": 808, "approvalEventId": 505,
        "imageDigest": "d" * 64,
        "attestationDigest": "e" * 64, "interpreterSha256": "f" * 64,
        "runtimeClosureSha256": "1" * 64, "installPath": PATH}
    runner_scope = {"principalId": "runner-reader", "credentialId": "2" * 64,
        "repository": release.REPOSITORY, "permissions": ["read-installed-probe"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    audit_scope = {"principalId": "audit-reader", "credentialId": "3" * 64,
        "repository": release.REPOSITORY, "permissions": ["read-effect-audit"],
        "expiresAt": "2026-09-25T12:10:00Z"}
    identity = {"runId": 707, "runAttempt": 1,
        "coordinationRevision": "a" * 40, "sourceTree": "b" * 40,
        "artifactId": 404, "manifestSha256": "c" * 64,
        "archiveSha256": SHA(ARCHIVE), "installPath": PATH,
        "repositoryId": 77, "identityEventId": 808,
        "sourceRecordId": 101, "approvalEventId": 505}
    obj = {"device": 11, "inode": 12, "size": len(ARCHIVE),
        "sha256": SHA(ARCHIVE)}
    probe = {"schema": readback.PROBE_SCHEMA, "complete": True,
        "principalId": "runner-reader", "credentialId": "2" * 64,
        "repository": release.REPOSITORY, **identity,
        "runnerActorId": 808, "imageDigest": "d" * 64,
        "attestationDigest": "e" * 64, "interpreterSha256": "f" * 64,
        "runtimeClosureSha256": "1" * 64, "realPath": PATH,
        "symlink": False, "before": copy.deepcopy(obj),
        "after": copy.deepcopy(obj), "archiveBytes": ARCHIVE,
        "argv": ["python3", "-I", "-S", PATH, "execute-native-pull"],
        "exitCode": 78, "stdout": REFUSAL, "stderr": b"",
        "observedAt": "2026-09-25T11:58:00Z"}
    audit = {"schema": readback.AUDIT_SCHEMA, "complete": True,
        "principalId": "audit-reader", "credentialId": "3" * 64,
        "repository": release.REPOSITORY, **identity,
        "eventId": 909, "auditActorId": 1001, "runnerActorId": 808,
        "object": copy.deepcopy(obj), "counts": {
            "tokenReads": 0, "journalReads": 0, "journalWrites": 0,
            "casWrites": 0, "providerPosts": 0, "cleanupAttempts": 0},
        "observedAt": "2026-09-25T11:59:00Z"}
    return result, selected, Port(runner_scope, probe), Port(audit_scope, audit)


def reviewed(preflight):
    return approval.ApprovalWitnessResult(
        preflight.coordination_revision, preflight.source_tree,
        preflight.producer_run_id, preflight.producer_run_attempt,
        preflight.artifact_id, preflight.reviewer_actor_id,
        preflight.approval_event_id, "a" * 64,
        preflight.manifest_sha256, "b" * 64,
        preflight.source_record_id, preflight.producer_actor_id, 77, 808,
        "2026-09-25T11:57:30Z", "2026-09-25T12:10:00Z")


class RunnerReadbackTests(unittest.TestCase):
    def observe(self, change=None):
        result, selection, runner, audit = fixture()
        if change:
            change(result, selection, runner, audit)
        return readback.qualify(result, runner, audit, selection, NOW,
                                approval_witness=reviewed(result))

    def refuses(self, change):
        with self.assertRaises(readback.Refused):
            self.observe(change)

    def test_matching_fake_ports_stay_closed_without_live_ports(self):
        with (mock.patch("builtins.open", side_effect=AssertionError("file")),
              mock.patch.object(os, "getenv", side_effect=AssertionError("token")),
              mock.patch.object(socket.socket, "connect", side_effect=AssertionError("post")),
              mock.patch("sqlite3.connect", side_effect=AssertionError("journal"))):
            result = self.observe()
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)
        self.assertEqual(result.audit_actor_id, 1001)
        self.assertEqual(result.approval_event_id, 505)
        self.assertEqual(result.approved_at, "2026-09-25T11:57:30Z")
        self.assertFalse(hasattr(readback, "dispatch"))

    def test_installed_refusal_without_immutable_review_witness_refuses(self):
        preflight, selection, runner, audit = fixture()
        with self.assertRaises(readback.Refused):
            readback.qualify(preflight, runner, audit, selection, NOW)
        self.assertEqual(runner.reads, [])
        self.assertEqual(audit.reads, [])

    def test_foreign_review_or_installed_approval_event_refuses(self):
        preflight, selection, runner, audit = fixture()
        checked = reviewed(preflight)
        object.__setattr__(checked, "source_record_id", 102)
        with self.assertRaises(readback.Refused):
            readback.qualify(preflight, runner, audit, selection, NOW,
                             approval_witness=checked)
        self.assertEqual(runner.reads, [])
        preflight, selection, runner, audit = fixture()
        runner.record["approvalEventId"] = 506
        with self.assertRaises(readback.Refused):
            readback.qualify(preflight, runner, audit, selection, NOW,
                             approval_witness=reviewed(preflight))

    def test_probe_before_review_or_after_expiry_refuses(self):
        for field, value in (("approved_at", "2026-09-25T11:58:30Z"),
                             ("expires_at", "2026-09-25T11:59:30Z")):
            with self.subTest(field=field):
                preflight, selection, runner, audit = fixture()
                checked = reviewed(preflight)
                object.__setattr__(checked, field, value)
                with self.assertRaises(readback.Refused):
                    readback.qualify(preflight, runner, audit, selection, NOW,
                                     approval_witness=checked)

    def test_selected_source_runtime_and_object_drift_refuse(self):
        for key, value in (("runId", 708), ("runAttempt", 2),
                           ("imageDigest", "0" * 64),
                           ("interpreterSha256", "0" * 64),
                           ("runtimeClosureSha256", "0" * 64),
                           ("installPath", "/tmp/closed-effect.pyz")):
            with self.subTest(key=key):
                self.refuses(lambda _p, s, _r, _a: s.__setitem__(key, value))
        for key, value in (("artifactId", 405), ("sourceTree", "0" * 40),
                           ("archiveBytes", b"changed"), ("symlink", True),
                           ("realPath", "/tmp/foreign.pyz")):
            with self.subTest(key=key):
                self.refuses(lambda _p, _s, r, _a: r.record.__setitem__(key, value))
        self.refuses(lambda _p, _s, r, _a: r.record["after"].__setitem__("inode", 13))

    def test_refusal_audit_and_reader_custody_negatives(self):
        self.refuses(lambda _p, _s, r, _a: r.record.__setitem__("exitCode", 0))
        self.refuses(lambda _p, _s, r, _a: r.record.__setitem__("stdout", b"{}\n"))
        self.refuses(lambda _p, _s, r, _a: r.record.__setitem__("stderr", b"warning"))
        self.refuses(lambda _p, _s, _r, a: a.record["counts"].__setitem__("tokenReads", 1))
        self.refuses(lambda _p, _s, _r, a: a.record["counts"].__setitem__("providerPosts", True))
        self.refuses(lambda _p, _s, _r, a: a.record.__setitem__("artifactId", 405))
        self.refuses(lambda _p, _s, _r, a: a.record.__setitem__("observedAt", "2026-09-25T11:00:00Z"))
        self.refuses(lambda _p, _s, _r, a: a.scopes[0].__setitem__("principalId", "runner-reader"))
        self.refuses(lambda _p, _s, _r, a: a.scopes[1].__setitem__("credentialId", "4" * 64))
        self.refuses(lambda _p, s, _r, _a: s.__setitem__("installPath", "/opt/fsgg/../closed-effect.pyz"))
        self.refuses(lambda _p, _s, r, _a: r.record["after"].__setitem__("sha256", "0" * 64))
        self.refuses(lambda _p, _s, _r, a: a.record["object"].__setitem__("inode", 13))
        self.refuses(lambda _p, _s, _r, a: a.record["counts"].pop("casWrites"))
        self.refuses(lambda _p, _s, r, _a: r.record.__setitem__("imageDigest", "0" * 64))
        self.refuses(lambda _p, _s, r, _a: r.record.__setitem__("argv", ["python3", PATH]))
        self.refuses(lambda _p, _s, _r, a: a.record.__setitem__("auditActorId", 808))
        self.refuses(lambda _p, _s, _r, a: a.record.__setitem__("auditActorId", 1002))
        self.refuses(lambda _p, s, _r, _a: s.__setitem__("auditActorId", 1002))
        self.refuses(lambda _p, _s, _r, a: a.record.__setitem__("eventId", 910))

    def test_foreign_positive_audit_actor_was_a_false_green(self):
        result, selection, runner, audit = fixture()
        selection.pop("auditActorId")  # The old selector omitted the actor.
        audit.record["auditActorId"] = 1002
        with self.assertRaises(readback.Refused):
            readback.qualify(result, runner, audit, selection, NOW,
                             approval_witness=reviewed(result))

    def test_unavailable_or_drifting_independent_port_refuses_without_leak(self):
        result, selection, runner, audit = fixture()
        audit.scopes[1]["credentialId"] = "4" * 64
        with self.assertRaisesRegex(readback.Refused, "readback-scope-drift"):
            readback.qualify(result, runner, audit, selection, NOW,
                             approval_witness=reviewed(result))
        result, selection, runner, audit = fixture()
        class Broken(Port):
            def read_audit(self, event_id):
                raise OSError("SYNTHETIC_SECRET_SENTINEL")
        audit = Broken(audit.scopes[0], audit.record)
        with self.assertRaises(readback.Refused) as caught:
            readback.qualify(result, runner, audit, selection, NOW,
                             approval_witness=reviewed(result))
        self.assertNotIn("SYNTHETIC_SECRET_SENTINEL", repr(caught.exception))
        self.refuses(lambda p, _s, _r, _a: object.__setattr__(p, "live_effects", False))

    def test_reused_runner_or_audit_scope_mutated_at_final_read_refuses(self):
        for changed_port in ("runner", "audit"):
            with self.subTest(changed_port=changed_port):
                preflight, selection, runner, audit = fixture()
                port = runner if changed_port == "runner" else audit
                shared = copy.deepcopy(port.scopes[0])
                reads = [0]
                def scope():
                    reads[0] += 1
                    if reads[0] == 2:
                        shared["credentialId"] = "4" * 64
                    return shared
                port.scope = scope
                with self.assertRaises(readback.Refused):
                    readback.qualify(preflight, runner, audit, selection, NOW,
                                     approval_witness=reviewed(preflight))


if __name__ == "__main__":
    unittest.main()
