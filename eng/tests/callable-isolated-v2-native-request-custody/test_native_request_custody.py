"""Closed v5 native request and journal result byte-join controls."""

import dataclasses
import datetime as dt
import hashlib
import json
import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
import callable_isolated_v2_effect_candidate as candidate
import callable_isolated_v2_effect_v5_ports as v5
import callable_isolated_v2_journal_intent_readback as journal
import callable_isolated_v2_operation_plan_read_adapter as plan
import callable_isolated_v2_plan_seal_event_witness as seal
import callable_isolated_v2_native_request_custody as custody

NOW = dt.datetime(2026, 9, 25, 12, tzinfo=dt.timezone.utc)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def sha(value):
    return hashlib.sha256(value).hexdigest()


def selected_target():
    return {"repository": "FS-GG/v2-synthetic", "repositoryId": 300,
            "nodeId": "R_300", "installationId": 301,
            "sourceRef": "refs/heads/source", "sourceSha": "2" * 40,
            "baseRef": "refs/heads/base", "baseSha": "3" * 40,
            "prestateSha256": "4" * 64}


def fixture():
    core = {"operation_identity": candidate.IDENTITY, "write_attempts": 1,
            "repository_id": 300, "repository": "FS-GG/v2-synthetic",
            "source_ref": "refs/heads/source", "source_sha": "2" * 40,
            "base_ref": "refs/heads/base", "base_sha": "3" * 40}
    marker = sha(canonical({"effect": "create-pull", "intent": core}))
    request = canonical({"title": plan.TITLE, "head": "source",
                         "base": "base", "body": f"FS-GG-Effect: {marker}"})
    verified = plan.VerifiedRequest(500, "1" * 64, "5" * 64,
                                    sha(request), request)
    event = seal.WitnessResult(600, "e" * 64, verified.plan_sha256,
                               verified.request_sha256,
                               "2026-09-25T11:58:00Z")
    intent = journal.IntentReadback(verified.operation_id,
        verified.request_sha256, "6" * 64, sha(canonical(selected_target())),
        "FS-GG/coordination-journal", "refs/heads/gs2-09-9-intent",
        "v2/intent.json", 3, "8" * 40, 4, "9" * 40)
    native = v5.NativeCoordinates(
        operation_identity=candidate.IDENTITY, operation_id=verified.operation_id,
        canonical_request=request, request_sha256=verified.request_sha256,
        method="POST", path="repos/FS-GG/v2-synthetic/pulls",
        max_provider_writes=1, repository="FS-GG/v2-synthetic",
        repository_id=300, repository_node_id="R_300", installation_id=301,
        source_ref="refs/heads/source", source_sha="2" * 40,
        base_ref="refs/heads/base", base_sha="3" * 40,
        prestate_sha256="4" * 64, setup_receipt_sha256="a" * 64,
        run_id=200, run_attempt=1, environment_id=210,
        dispatch_actor_id=202, reviewer_actor_id=203,
        approval_event_id=204, issuer_actor_id=205,
        grant_sha256="6" * 64, grant_expires_at="2026-09-25T12:15:00Z",
        journal_repository="FS-GG/coordination-journal",
        journal_ref="refs/heads/gs2-09-9-intent",
        journal_path="v2/intent.json", journal_prior_generation=3,
        journal_prior_head="8" * 40)
    return verified, event, intent, native


class NativeRequestCustodyTests(unittest.TestCase):
    def qualify(self, verified=None, event=None, intent=None, native=None,
                target=None):
        v, e, i, n = fixture()
        return custody.qualify(v if verified is None else verified,
            e if event is None else event, i if intent is None else intent,
            n if native is None else native,
            selected_target() if target is None else target, 600, "e" * 64,
            4, "9" * 40, NOW)

    def test_matching_fake_join_is_closed(self):
        result = self.qualify()
        self.assertEqual(result.operation_id, "5" * 64)
        self.assertEqual(result.request_sha256, fixture()[0].request_sha256)
        self.assertEqual(result.state, "request-consistent-but-unadmitted")
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_native_request_bytes_and_post_shape_must_match(self):
        v, e, i, n = fixture()
        for field, value in (("canonical_request", b"{}"),
                             ("request_sha256", "c" * 64),
                             ("method", "PUT"),
                             ("path", "repos/FS-GG/foreign/pulls"),
                             ("max_provider_writes", 2),
                             ("operation_id", "c" * 64),
                             ("operation_identity", "foreign")):
            with self.subTest(field=field), self.assertRaises(custody.Refused):
                self.qualify(v, e, i, dataclasses.replace(n, **{field: value}))

    def test_target_mutation_cannot_reseal_native_coordinates(self):
        v, e, i, n = fixture()
        for field, value in (("repository", "FS-GG/foreign"),
                             ("repository_id", 301),
                             ("source_ref", "refs/heads/foreign"),
                             ("source_sha", "c" * 40),
                             ("base_ref", "refs/heads/foreign"),
                             ("base_sha", "c" * 40)):
            with self.subTest(field=field), self.assertRaises(custody.Refused):
                self.qualify(v, e, i, dataclasses.replace(n, **{field: value}))
        target = selected_target()
        target["sourceSha"] = "c" * 40
        with self.assertRaises(custody.Refused):
            self.qualify(v, e, i, n, target)

    def test_seal_and_journal_custody_must_match_plan(self):
        v, e, i, n = fixture()
        cases = (("event", dataclasses.replace(e, event_id=601)),
                 ("digest", dataclasses.replace(e, event_sha256="c" * 64)),
                 ("plan", dataclasses.replace(e, plan_sha256="c" * 64)),
                 ("request", dataclasses.replace(e, request_sha256="c" * 64)),
                 ("journal-operation", dataclasses.replace(i,
                     operation_id="c" * 64)),
                 ("journal-generation", dataclasses.replace(i,
                     committed_generation=5)),
                 ("journal-head", dataclasses.replace(i,
                     committed_head="c" * 40)))
        for name, changed in cases:
            with self.subTest(name=name), self.assertRaises(custody.Refused):
                if name.startswith("journal"):
                    self.qualify(v, e, changed, n)
                else:
                    self.qualify(v, changed, i, n)

    def test_wrong_run_actor_or_prior_generation_refuses(self):
        v, e, i, n = fixture()
        for field, value in (("run_id", 0), ("run_attempt", 0),
                             ("dispatch_actor_id", 203),
                             ("reviewer_actor_id", 202),
                             ("issuer_actor_id", 202),
                             ("journal_prior_generation", 4),
                             ("journal_prior_head", "0" * 40),
                             ("grant_sha256", "0" * 64)):
            with self.subTest(field=field), self.assertRaises(custody.Refused):
                self.qualify(v, e, i, dataclasses.replace(n, **{field: value}))

    def test_independent_committed_pin_is_required(self):
        v, e, i, n = fixture()
        for generation, head in ((5, "9" * 40), (4, "c" * 40)):
            with self.subTest(generation=generation, head=head):
                with self.assertRaises(custody.Refused):
                    custody.qualify(v, e, i, n, selected_target(),
                                    600, "e" * 64, generation, head, NOW)

    def test_no_file_token_socket_or_sqlite_access(self):
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result = self.qualify()
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
