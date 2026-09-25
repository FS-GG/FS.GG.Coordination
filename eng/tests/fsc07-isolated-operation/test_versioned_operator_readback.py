#!/usr/bin/env python3
"""Adversarial offline controls for the inactive versioned readback proof."""

import dataclasses
import copy
import contextlib
import hashlib
import http.server
import importlib.util
import json
import pathlib
import socket
import subprocess
import tempfile
import threading
import unittest
import urllib.error


SOURCE = pathlib.Path(__file__).resolve().parents[2] / "callable-cli-isolated-operation-v2.py"
SPEC = importlib.util.spec_from_file_location("isolated_operation_v2", SOURCE)
operator = importlib.util.module_from_spec(SPEC)
import sys
sys.modules[SPEC.name] = operator
SPEC.loader.exec_module(operator)

SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40
DIGEST = "d" * 64
SENTINEL = "FSC07_OFFLINE_CREDENTIAL_SENTINEL"


def pull_expected():
    return operator.ExpectedPull(operator.OPERATION_IDENTITY, 1, 44,
                                 "FS-GG/disposable", "refs/heads/source", SHA_A,
                                 "refs/heads/main", SHA_B)


def pull_observed():
    return operator.PullCensus(True, 44, SHA_A, SHA_B, ({
        "number": 8, "node_id": "PR_8", "state": "open", "draft": False,
        "merged": False, "title": operator.PULL_TITLE,
        "body": operator.pull_request_body(pull_expected())["body"],
        "head": {"ref": "source", "sha": SHA_A,
                 "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
        "base": {"ref": "main", "sha": SHA_B,
                 "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
    },), DIGEST)


def protection_expected():
    return operator.ExpectedProtection(operator.OPERATION_IDENTITY, 1, 44,
                                       "FS-GG/disposable", "main", SHA_B,
                                       "required-check", 17)


def protection_observed():
    return operator.ProtectionReadback(True, 44, "main", SHA_B, True, {
        "required_status_checks": {
            "strict": True,
            "checks": [{"context": "required-check", "app_id": 17}],
        },
        "enforce_admins": {"enabled": False},
        "required_pull_request_reviews": None,
        "restrictions": None,
        "allow_force_pushes": {"enabled": False},
        "allow_deletions": {"enabled": False},
    }, DIGEST)


def event(method, path, body=None, value=None, status=200, headers=None, error=None):
    item = {"method": method, "path": path, "body": body}
    if error is not None:
        item["raise"] = error
    else:
        item["response"] = {"status": status,
                            "headers": [list(pair) for pair in (headers or {}).items()],
                            "json": value}
    return item


def pull_read_events(pulls=(), source_sha=SHA_A, base_sha=SHA_B,
                     page_header=None, terminal=(), detail=True):
    repo = "FS-GG/disposable"
    prefix = f"repos/{repo}"
    listed = [{"number": pull["number"], "node_id": pull["node_id"],
               "state": pull["state"], "draft": pull["draft"],
               "title": pull["title"], "body": pull["body"],
               "head": copy.deepcopy(pull["head"]),
               "base": copy.deepcopy(pull["base"])} for pull in pulls]
    events = [
        event("GET", prefix, value={"id": 44, "full_name": repo,
                                      "transcript_sha256": "0" * 64}),
        event("GET", f"{prefix}/git/ref/heads/source",
              value={"ref": "refs/heads/source", "object": {"sha": source_sha}}),
        event("GET", f"{prefix}/git/ref/heads/main",
              value={"ref": "refs/heads/main", "object": {"sha": base_sha}}),
        event("GET", f"{prefix}/pulls?state=open&per_page=100&page=1",
              value=listed, headers={"Link": page_header} if page_header else {}),
        event("GET", f"{prefix}/pulls?state=open&per_page=100&page=2",
              value=list(terminal)),
    ]
    if detail:
        events.extend(event("GET", f"{prefix}/pulls/{pull['number']}", value=pull)
                      for pull in pulls)
    events.extend([
        event("GET", prefix, value={"id": 44, "full_name": repo,
                                      "transcript_sha256": "0" * 64}),
        event("GET", f"{prefix}/git/ref/heads/source",
              value={"ref": "refs/heads/source", "object": {"sha": source_sha}}),
        event("GET", f"{prefix}/git/ref/heads/main",
              value={"ref": "refs/heads/main", "object": {"sha": base_sha}}),
    ])
    return events


def protection_read_events(protected=False, policy=None, branch_sha=SHA_B):
    repo = "FS-GG/disposable"
    prefix = f"repos/{repo}"
    first = [
        event("GET", prefix, value={"id": 44, "full_name": repo}),
        event("GET", f"{prefix}/git/ref/heads/main",
              value={"ref": "refs/heads/main", "object": {"sha": branch_sha}}),
        event("GET", f"{prefix}/branches/main",
              value={"name": "main", "commit": {"sha": branch_sha},
                     "protected": protected}),
        event("GET", f"{prefix}/branches/main/protection", value=policy or {},
              status=200 if protected else 404),
    ]
    return first + copy.deepcopy(first)


def reserve_once_factory():
    seen = set()
    def reserve(key):
        if key in seen:
            return False
        seen.add(key)
        return True
    return reserve


@contextlib.contextmanager
def loopback_server(events):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            self.handle_event()

        def do_POST(self):
            self.handle_event()

        def do_PUT(self):
            self.handle_event()

        def handle_event(self):
            try:
                scripted = self.server.events.pop(0)
            except IndexError:
                self.server.errors.append("unexpected-request")
                self.send_error(500)
                return
            length = int(self.headers.get("Content-Length", "0"))
            raw_body = self.rfile.read(length)
            request_body = json.loads(raw_body) if raw_body else None
            if (scripted["method"] != self.command
                    or scripted["path"] != self.path.lstrip("/")
                    or scripted["body"] != request_body):
                self.server.errors.append("request-mismatch")
                self.send_error(500)
                return
            self.server.observed.append({
                "method": self.command, "path": self.path,
                "authorization": self.headers.get("Authorization"),
            })
            if "raise" in scripted:
                self.connection.shutdown(socket.SHUT_RDWR)
                self.connection.close()
                return
            response = scripted["response"]
            payload = response.get("rawBody")
            if payload is None:
                payload = json.dumps(response["json"], sort_keys=True,
                                     separators=(",", ":"))
            data = payload.encode()
            self.send_response(response["status"])
            for name, value in response["headers"]:
                self.send_header(name, value)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.events = copy.deepcopy(events)
    server.errors = []
    server.observed = []
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


class VersionedReadbackTests(unittest.TestCase):
    def assert_unknown(self, value):
        self.assertIsInstance(value, operator.Unknown)
        self.assertNotIn(SENTINEL, repr(value))

    def test_pull_repository_id_must_not_accept_boolean_alias(self):
        expected = dataclasses.replace(pull_expected(), repository_id=1)
        for side in ("head", "base"):
            with self.subTest(side=side):
                pull = copy.deepcopy(pull_observed().pulls[0])
                pull["body"] = operator.pull_request_body(expected)["body"]
                pull["head"]["repo"]["id"] = 1
                pull["base"]["repo"]["id"] = 1
                pull[side]["repo"]["id"] = True
                observed = dataclasses.replace(
                    pull_observed(), repository_id=1, pulls=(pull,))
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    expected, lambda: observed))

        transport = operator.OfflineTranscriptTransport([
            event("GET", "repos/FS-GG/disposable",
                  value={"id": True, "full_name": "FS-GG/disposable"})])
        with self.assertRaisesRegex(operator.Refused, "native-repository-mismatch"):
            operator.NativeReadAdapter(transport)._repo("FS-GG/disposable", 1)

        pull = copy.deepcopy(pull_observed().pulls[0])
        pull["body"] = operator.pull_request_body(expected)["body"]
        pull["head"]["repo"]["id"] = 1
        pull["base"]["repo"]["id"] = 1
        events = pull_read_events((pull,))
        events[0]["response"]["json"]["id"] = 1
        events[6]["response"]["json"]["id"] = 1
        events[5]["response"]["json"]["head"]["repo"]["id"] = True
        with self.assertRaisesRegex(operator.Refused, "native-pull-list-detail-repo-drift"):
            operator.NativeReadAdapter(
                operator.OfflineTranscriptTransport(events)).read_pull_census(expected)

    def test_protection_rejects_extra_effective_required_context(self):
        observed = protection_observed()
        for contexts in (["required-check", "foreign-check"],
                         ["required-check", "required-check"],
                         ["foreign-check"], "required-check", True, 1.0):
            with self.subTest(contexts=contexts):
                policy = copy.deepcopy(observed.policy)
                policy["required_status_checks"]["contexts"] = contexts
                wrong = dataclasses.replace(observed, policy=policy)
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: wrong,
                    provider_response={"status": 500, "body": SENTINEL}))
        for contexts in ([], ["required-check"]):
            with self.subTest(compatible=contexts):
                policy = copy.deepcopy(observed.policy)
                policy["required_status_checks"]["contexts"] = contexts
                compatible = dataclasses.replace(observed, policy=policy)
                self.assertIsInstance(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: compatible), operator.ExactProtection)

    def test_pull_accepts_only_two_complete_exact_reads(self):
        reads = iter((pull_observed(), pull_observed()))
        result = operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: next(reads),
            provider_response={"status": "unknown", "body": SENTINEL})
        self.assertEqual(result, operator.ExactPull(8, "PR_8", DIGEST))

    def test_pull_rejects_wrong_head_base_repo_and_identity(self):
        observed = pull_observed()
        for wrong in (
            dataclasses.replace(observed, source_branch_sha=SHA_C),
            dataclasses.replace(observed, base_branch_sha=SHA_C),
            dataclasses.replace(observed, repository_id=45),
        ):
            with self.subTest(wrong=wrong):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: wrong))
        for side, field, value in (
            ("head", "ref", "foreign"), ("head", "sha", SHA_C),
            ("head", "repo", {"id": 44, "full_name": "FS-GG/foreign"}),
            ("base", "ref", "foreign"), ("base", "sha", SHA_C),
            ("base", "repo", {"id": 45, "full_name": "FS-GG/disposable"}),
        ):
            pull = dict(observed.pulls[0])
            pull[side] = {**pull[side], field: value}
            wrong = dataclasses.replace(observed, pulls=(pull,))
            with self.subTest(side=side, field=field):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: wrong))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            dataclasses.replace(pull_expected(), operation_identity="old"),
            lambda: observed))

    def test_pull_rejects_duplicate_incomplete_drift_and_retry(self):
        observed = pull_observed()
        for wrong in (
            dataclasses.replace(observed, pulls=observed.pulls * 2),
            dataclasses.replace(observed, complete=False),
            dataclasses.replace(observed, transcript_sha256=""),
        ):
            self.assert_unknown(operator.classify_pull_after_one_attempt(
                pull_expected(), lambda: wrong))
        reads = iter((observed, dataclasses.replace(observed, base_branch_sha=SHA_C)))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: next(reads)))
        shared = pull_observed()
        count = 0
        def reused_mutating_read():
            nonlocal count
            count += 1
            if count == 2:
                shared.pulls[0]["base"]["sha"] = SHA_C
            return shared
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), reused_mutating_read))
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            dataclasses.replace(pull_expected(), write_attempts=2), lambda: observed))

    def test_protection_rejects_force_push_deletion_and_policy_drift(self):
        observed = protection_observed()
        self.assertIsInstance(operator.classify_protection_after_one_attempt(
            protection_expected(), lambda: observed), operator.ExactProtection)
        for key, value in (
            ("allow_force_pushes", {"enabled": True}),
            ("allow_deletions", {"enabled": True}),
            ("allow_force_pushes", None),
            ("required_pull_request_reviews", {"required_approving_review_count": 0}),
            ("required_status_checks", {"strict": True, "checks": [
                {"context": "required-check", "app_id": 18}]}),
        ):
            wrong = dataclasses.replace(observed, policy={**observed.policy, key: value})
            with self.subTest(key=key, value=value):
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: wrong,
                    provider_response={"status": 200}))
        reads = iter((observed, dataclasses.replace(observed, branch="other")))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            protection_expected(), lambda: next(reads)))

    def test_protection_rejects_wrong_branch_repo_incomplete_and_retry(self):
        observed = protection_observed()
        for wrong in (
            dataclasses.replace(observed, repository_id=45),
            dataclasses.replace(observed, branch="foreign"),
            dataclasses.replace(observed, protected=False),
            dataclasses.replace(observed, complete=False),
            dataclasses.replace(observed, transcript_sha256="bad"),
        ):
            self.assert_unknown(operator.classify_protection_after_one_attempt(
                protection_expected(), lambda: wrong))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            dataclasses.replace(protection_expected(), write_attempts=2),
            lambda: observed))

    def test_exception_text_never_escapes(self):
        def failed():
            raise urllib.error.URLError(SENTINEL)
        self.assert_unknown(operator.classify_pull_after_one_attempt(
            pull_expected(), failed))
        self.assert_unknown(operator.classify_protection_after_one_attempt(
            protection_expected(), failed))

    def test_q3_exact_native_pr_and_protection_runtime(self):
        pull = pull_observed().pulls[0]
        post = pull_read_events((pull,))
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        result = operator.run_pull_once(pull_expected(), transport,
                                        reserve_once_factory())
        self.assertIsInstance(result, operator.ExactPull)
        self.assertEqual(transport.writes, 1)
        self.assertNotEqual(result.census_sha256, "0" * 64)
        protection = protection_observed()
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         error=SENTINEL)] +
                  protection_read_events(True, protection.policy) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        result = operator.run_protection_once(protection_expected(), transport,
                                              reserve_once_factory())
        self.assertIsInstance(result, operator.ExactProtection)
        self.assertEqual(transport.writes, 1)

    def test_q3_loopback_http_pull_and_protection(self):
        pull = pull_observed().pulls[0]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + pull_read_events((pull,)) * 2)
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            result = operator.run_pull_once(pull_expected(), transport,
                                            reserve_once_factory())
            self.assertIsInstance(result, operator.ExactPull)
            self.assertEqual(server.errors, [])
            self.assertEqual(server.events, [])
            self.assertEqual(sum(item["method"] == "POST"
                                 for item in server.observed), 1)
            self.assertTrue(all(item["authorization"] is None
                                for item in server.observed))
        policy = protection_observed().policy
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         error=SENTINEL)] + protection_read_events(True, policy) * 2)
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            result = operator.run_protection_once(
                protection_expected(), transport, reserve_once_factory())
            self.assertIsInstance(result, operator.ExactProtection)
            self.assertEqual(server.errors, [])
            self.assertEqual(server.events, [])
            self.assertEqual(sum(item["method"] == "PUT"
                                 for item in server.observed), 1)
            self.assertTrue(all(item["authorization"] is None
                                for item in server.observed))

    def test_q6_loopback_origin_and_redirect_refuse_without_egress(self):
        for url in ("https://api.github.com", "http://localhost:1234",
                    "http://127.0.0.1:0", "http://127.0.0.1:80/path",
                    "http://user@127.0.0.1:80"):
            with self.subTest(url=url):
                with self.assertRaises(operator.Refused):
                    operator.LoopbackHttpTransport(url)
        events = [event("GET", "repos/FS-GG/disposable", value={}, status=302,
                        headers={"Location": "https://api.github.com/"})]
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            self.assert_unknown(operator.run_pull_once(
                pull_expected(), transport, reserve_once_factory()))
            self.assertEqual(server.errors, [])
            self.assertEqual(len(server.observed), 1)
            self.assertEqual(server.events, [])
        for wrong in (
            dataclasses.replace(pull_expected(), repository="FS-GG/disposable?foreign"),
            dataclasses.replace(pull_expected(), source_ref="refs/heads/../foreign"),
        ):
            with self.subTest(identity=wrong), loopback_server([]) as server:
                transport = operator.LoopbackHttpTransport(
                    f"http://127.0.0.1:{server.server_port}")
                self.assert_unknown(operator.run_pull_once(
                    wrong, transport, reserve_once_factory()))
                self.assertEqual(server.observed, [])
        with loopback_server([]) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            wrong = dataclasses.replace(protection_expected(), branch="../main")
            self.assert_unknown(operator.run_protection_once(
                wrong, transport, reserve_once_factory()))
            self.assertEqual(server.observed, [])

    def test_q6_loopback_lost_response_unknown_and_no_repeat(self):
        pull = dict(pull_observed().pulls[0])
        pull["base"] = {**pull["base"], "sha": SHA_C}
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] +
                  pull_read_events((pull,)) * 2 + pull_read_events() * 2)
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            reserve = reserve_once_factory()
            first = operator.run_pull_once(pull_expected(), transport, reserve)
            second = operator.run_pull_once(pull_expected(), transport, reserve)
            self.assert_unknown(first)
            self.assert_unknown(second)
            self.assertEqual(sum(item["method"] == "POST"
                                 for item in server.observed), 1)
            self.assertEqual(server.errors, [])
            self.assertEqual(server.events, [])
            self.assertNotIn(SENTINEL, repr(first) + repr(second))

    def test_q6_loopback_redirected_or_refused_write_is_unknown(self):
        pull = pull_observed().pulls[0]
        for status in (302, 401):
            events = (pull_read_events() * 2 +
                      [event("POST", "repos/FS-GG/disposable/pulls",
                             body=operator.pull_request_body(pull_expected()),
                             value={"message": "moved"}, status=status,
                             headers={"Location": "https://api.github.com/"})] +
                      pull_read_events((pull,)) * 2)
            with self.subTest(status=status), loopback_server(events) as server:
                transport = operator.LoopbackHttpTransport(
                    f"http://127.0.0.1:{server.server_port}")
                result = operator.run_pull_once(
                    pull_expected(), transport, reserve_once_factory())
                self.assert_unknown(result)
                self.assertEqual(result.reason, "pull-request-provider-explicit-refusal")
                self.assertEqual(sum(item["method"] == "POST"
                                     for item in server.observed), 1)
                self.assertEqual(server.errors, [])
        policy = protection_observed().policy
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         value={"message": "moved"}, status=302,
                         headers={"Location": "https://api.github.com/"})] +
                  protection_read_events(True, policy) * 2)
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            result = operator.run_protection_once(
                protection_expected(), transport, reserve_once_factory())
            self.assert_unknown(result)
            self.assertEqual(result.reason, "branch-protection-provider-explicit-refusal")
            self.assertEqual(sum(item["method"] == "PUT"
                                 for item in server.observed), 1)
            self.assertEqual(server.errors, [])

    def test_q6_loopback_500_is_readback_only(self):
        pull = pull_observed().pulls[0]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         value={"message": SENTINEL}, status=500)] +
                  pull_read_events((pull,)) * 2)
        with loopback_server(events) as server:
            transport = operator.LoopbackHttpTransport(
                f"http://127.0.0.1:{server.server_port}")
            result = operator.run_pull_once(
                pull_expected(), transport, reserve_once_factory())
            self.assertIsInstance(result, operator.ExactPull)
            self.assertEqual(sum(item["method"] == "POST"
                                 for item in server.observed), 1)
            self.assertEqual(server.errors, [])
            self.assertEqual(server.events, [])
            self.assertNotIn(SENTINEL, repr(result))

    def test_q6_malformed_injected_write_response_is_unknown(self):
        class MalformedTransport(operator.OfflineTranscriptTransport):
            def request(self, method, path, body=None):
                if method == "POST":
                    self.writes += 1
                    return operator.HttpResponse(201, {}, b"{}")
                return super().request(method, path, body)

        pull = pull_observed().pulls[0]
        transport = MalformedTransport(
            pull_read_events() * 2 + pull_read_events((pull,)) * 2)
        result = operator.run_pull_once(pull_expected(), transport,
                                        reserve_once_factory())
        self.assert_unknown(result)
        self.assertEqual(result.reason, "pull-request-provider-response-invalid")
        self.assertEqual(transport.writes, 1)

    def test_q6_lost_response_unknown_and_no_repeat(self):
        pull = dict(pull_observed().pulls[0])
        pull["head"] = {**pull["head"], "sha": SHA_C}
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] +
                  pull_read_events((pull,)) * 2 + pull_read_events() * 2)
        transport = operator.OfflineTranscriptTransport(events)
        reserve = reserve_once_factory()
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport, reserve))
        self.assertEqual(transport.writes, 1)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport, reserve))
        self.assertEqual(transport.writes, 1)

    def test_q3_runtime_refuses_force_push_after_put(self):
        policy = {**protection_observed().policy,
                  "allow_force_pushes": {"enabled": True}}
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         error=SENTINEL)] +
                  protection_read_events(True, policy) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            protection_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_q3_incomplete_page_and_false_terminal_refuse_before_write(self):
        pull = pull_observed().pulls[0]
        link_next = ('<https://api.github.com/repos/FS-GG/disposable/pulls?'
                     'state=open&per_page=100&page=2>; rel="next"')
        # A short first page with a next link is not a terminal census.
        events = pull_read_events((pull,), page_header=link_next)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)

    def test_q3_terminal_ref_and_singleton_link_refuse_before_write(self):
        events = pull_read_events()
        events[6]["response"]["json"]["object"]["sha"] = SHA_C
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)

    def test_q3_list_detail_foreign_identity_and_terminal_last_refuse(self):
        pull = pull_observed().pulls[0]
        for field in ("node_id", "head.repo"):
            post = pull_read_events((pull,))
            listed = post[3]["response"]["json"][0]
            if field == "node_id":
                listed["node_id"] = "PR_foreign"
            else:
                listed["head"]["repo"] = {"id": 999, "full_name": "FS-GG/foreign"}
            events = (pull_read_events() * 2 +
                      [event("POST", "repos/FS-GG/disposable/pulls",
                             body=operator.pull_request_body(pull_expected()),
                             error=SENTINEL)] + post)
            transport = operator.OfflineTranscriptTransport(events)
            with self.subTest(field=field):
                self.assert_unknown(operator.run_pull_once(
                    pull_expected(), transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)
        last_one = ('<https://api.github.com/repos/FS-GG/disposable/pulls?'
                    'state=open&per_page=100&page=1>; rel="last"')
        last_two = ('<https://api.github.com/repos/FS-GG/disposable/pulls?'
                    'state=open&per_page=100&page=2>; rel="last"')
        post = pull_read_events((pull,), page_header=last_one)
        post[4]["response"]["headers"] = [["Link", last_two]]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_q3_duplicate_and_nonfinite_native_json_refuse(self):
        policy = protection_observed().policy
        malformed = {**policy, "allow_force_pushes": {"enabled": True}}
        raw = json.dumps(malformed)[:-1] + ',"allow_force_pushes":{"enabled":false}}'
        post = protection_read_events(True, policy)
        post[3]["response"]["rawBody"] = raw
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         error=SENTINEL)] + post)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            protection_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)
        events = pull_read_events()
        events[0]["response"]["rawBody"] = (
            '{"id":44,"full_name":"FS-GG/disposable","extra":NaN}')
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 0)
        events = protection_read_events()
        events[6]["response"]["json"]["protected"] = True
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(protection_expected(),
                                                         transport,
                                                         reserve_once_factory()))
        self.assertEqual(transport.writes, 0)
        events = pull_read_events()
        events[0]["response"]["headers"] = [[
            "Link", "<https://api.github.com/repos/FS-GG/disposable?page=2>; rel=\"next\""]]
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)
        link_next = ('<https://api.github.com/repos/FS-GG/disposable/pulls?'
                     'state=open&per_page=100&page=2>; rel="next"')
        false_last = ('<https://api.github.com/repos/FS-GG/disposable/pulls?'
                      'state=open&per_page=100&page=3>; rel="last"')
        events = pull_read_events()
        events[3]["response"]["headers"] = [["Link", link_next], ["link", false_last]]
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)
        events = pull_read_events(page_header=false_last)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)
        events = pull_read_events()
        events[4]["response"]["headers"] = [["Link", false_last]]
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 0)

    def test_q6_controlled_exception_sentinel_not_surfaced(self):
        events = pull_read_events() * 2 + [
            event("POST", "repos/FS-GG/disposable/pulls",
                  body=operator.pull_request_body(pull_expected()), error=SENTINEL)]
        transport = operator.OfflineTranscriptTransport(events)
        result = operator.run_pull_once(pull_expected(), transport,
                                        reserve_once_factory())
        self.assert_unknown(result)
        self.assertEqual(transport.writes, 1)
        self.assertNotIn(SENTINEL, repr(result))

    def test_q6_final_poststate_ref_drift_is_unknown(self):
        pull = pull_observed().pulls[0]
        final_pass = pull_read_events((pull,))
        final_pass[7]["response"]["json"]["object"]["sha"] = SHA_C
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] +
                  pull_read_events((pull,)) + final_pass)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(pull_expected(), transport,
                                                   reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_q6_restart_replay_cli_and_v5_inspect_binding(self):
        root = SOURCE.parents[1]
        preflight_path = root / operator.HISTORICAL_PREFLIGHT
        historical = {
            "path": operator.HISTORICAL_PREFLIGHT,
            "sha256": hashlib.sha256(preflight_path.read_bytes()).hexdigest(),
            "operationIdentity": operator.HISTORICAL_IDENTITY,
            "authority": "historical-observation-only",
        }
        contract = {
            "schema": "fsgg.coordination.callable-isolated-operation-contract/5",
            "identity": operator.OPERATION_IDENTITY,
            "state": "prepared-not-authorized", "authorized": False,
            "source": {"operationSource": operator.SOURCE,
                       "operationSourceSha256": hashlib.sha256(SOURCE.read_bytes()).hexdigest()},
            "qualificationControls": {
                "path": operator.CONTROLS,
                "sha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
            },
            "historicalPreflight": historical,
        }
        contract["contractSha256"] = operator._digest(contract)
        proposal = {
            "schema": "fsgg.coordination.callable-isolated-operation-proposal/5",
            "identity": operator.OPERATION_IDENTITY,
            "state": "prepared-not-authorized", "authorized": False,
            "contract": {"sha256": contract["contractSha256"],
                         "operationSourceSha256": contract["source"]["operationSourceSha256"]},
            "historicalPreflight": historical,
        }
        proposal["proposalSha256"] = operator._digest(proposal)
        with tempfile.TemporaryDirectory() as temp:
            contract_path = pathlib.Path(temp) / "contract.json"
            proposal_path = pathlib.Path(temp) / "proposal.json"
            contract_path.write_text(json.dumps(contract))
            proposal_path.write_text(json.dumps(proposal))
            command = [sys.executable, str(SOURCE), "--contract", str(contract_path),
                       "--proposal", str(proposal_path), "--preflight",
                       str(preflight_path), "inspect"]
            positive = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertEqual(positive.returncode, 0, positive.stderr)
            result = json.loads(positive.stdout)
            self.assertIs(result["authorized"], False)
            self.assertEqual(result["liveEffects"], 0)
            self.assertIs(result["historicalObservationOnly"], True)
            pull = pull_observed().pulls[0]
            scenario = {
                "schema": "fsgg.coordination.isolated-operation-offline-scenario/1",
                "effect": "create-pull", "expected": dataclasses.asdict(pull_expected()),
                "events": (pull_read_events() * 2 +
                           [event("POST", "repos/FS-GG/disposable/pulls",
                                  body=operator.pull_request_body(pull_expected()),
                                  error=SENTINEL)] + pull_read_events((pull,)) * 2),
            }
            scenario_path = pathlib.Path(temp) / "scenario.json"
            scenario_path.write_text(json.dumps(scenario))
            journal_path = pathlib.Path(temp) / "attempts.sqlite"
            exercise_command = command[:-1] + ["--scenario", str(scenario_path),
                                               "--journal", str(journal_path),
                                               "exercise-offline"]
            exercise = subprocess.run(exercise_command,
                                      capture_output=True, text=True, check=False)
            self.assertEqual(exercise.returncode, 0, exercise.stderr)
            result = json.loads(exercise.stdout)
            self.assertEqual(result["classification"], "ExactPull")
            self.assertEqual(result["writeAttempts"], 1)
            self.assertIs(result["simulationOnly"], True)
            self.assertNotIn(SENTINEL, exercise.stdout + exercise.stderr)
            replay = subprocess.run(exercise_command, capture_output=True,
                                    text=True, check=False)
            self.assertEqual(replay.returncode, 0, replay.stderr)
            replay_result = json.loads(replay.stdout)
            self.assertEqual(replay_result["classification"], "Unknown")
            self.assertEqual(replay_result["reason"], "pull-request-attempt-not-reserved")
            self.assertEqual(replay_result["writeAttempts"], 0)
            proposal_path.write_text('{"schema":"x","schema":"y"}')
            duplicate = subprocess.run(command, capture_output=True,
                                       text=True, check=False)
            self.assertEqual(duplicate.returncode, 2)
            self.assertEqual(duplicate.stderr.strip(), "json-duplicate-member")
            proposal_path.write_text(json.dumps(proposal))
            contract["source"]["operationSourceSha256"] = "0" * 64
            contract["contractSha256"] = operator._digest({
                key: value for key, value in contract.items() if key != "contractSha256"})
            contract_path.write_text(json.dumps(contract))
            negative = subprocess.run(command, capture_output=True, text=True, check=False)
            self.assertEqual(negative.returncode, 2)
            self.assertEqual(negative.stderr.strip(), "inspect-source-drift")
            self.assertNotIn(SENTINEL, negative.stderr)


if __name__ == "__main__":
    unittest.main()
