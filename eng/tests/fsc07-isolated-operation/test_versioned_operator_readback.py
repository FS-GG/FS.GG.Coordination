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
import urllib.parse


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


def ref_value(ref, sha):
    repository = "FS-GG/disposable"
    branch = urllib.parse.quote(ref.removeprefix("refs/heads/"), safe="/")
    return {"ref": ref,
            "url": f"https://api.github.com/repos/{repository}/git/refs/heads/{branch}",
            "object": {"type": "commit", "sha": sha,
                       "url": f"https://api.github.com/repos/{repository}/git/commits/{sha}"}}


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
              value=ref_value("refs/heads/source", source_sha)),
        event("GET", f"{prefix}/git/ref/heads/main",
              value=ref_value("refs/heads/main", base_sha)),
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
              value=ref_value("refs/heads/source", source_sha)),
        event("GET", f"{prefix}/git/ref/heads/main",
              value=ref_value("refs/heads/main", base_sha)),
    ])
    return events


def protection_read_events(protected=False, policy=None, branch_sha=SHA_B):
    repo = "FS-GG/disposable"
    prefix = f"repos/{repo}"
    first = [
        event("GET", prefix, value={"id": 44, "full_name": repo}),
        event("GET", f"{prefix}/git/ref/heads/main",
              value=ref_value("refs/heads/main", branch_sha)),
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

    def test_lost_response_read_cannot_rewrite_selected_pull_sha(self):
        expected_pull = pull_expected()
        pull = copy.deepcopy(pull_observed().pulls[0])
        pull["head"]["sha"] = SHA_C
        observed_pull = dataclasses.replace(
            pull_observed(), source_branch_sha=SHA_C, pulls=(pull,))

        def read_pull():
            object.__setattr__(expected_pull, "source_sha", SHA_C)
            pull["body"] = operator.pull_request_body(expected_pull)["body"]
            return observed_pull

        self.assert_unknown(operator.classify_pull_after_one_attempt(
            expected_pull, read_pull))

    def test_lost_response_read_cannot_rewrite_selected_protection_sha(self):
        expected_protection = protection_expected()
        observed_protection = dataclasses.replace(
            protection_observed(), branch_sha=SHA_C)

        def read_protection():
            object.__setattr__(expected_protection, "branch_sha", SHA_C)
            return observed_protection

        self.assert_unknown(operator.classify_protection_after_one_attempt(
            expected_protection, read_protection))

    def test_reservation_callback_cannot_redirect_selected_native_write(self):
        for kind in ("pull", "protection"):
            with self.subTest(kind=kind):
                expected = pull_expected() if kind == "pull" else protection_expected()
                events = (pull_read_events() if kind == "pull"
                          else protection_read_events()) * 2
                playback = operator.OfflineTranscriptTransport(events)
                posts = []

                class TracingTransport:
                    def request(self, method, path, body=None):
                        if method in {"POST", "PUT"}:
                            posts.append((method, path))
                            raise OSError("synthetic-lost-response")
                        return playback.request(method, path, body)

                def reserve(_key):
                    if kind == "pull":
                        object.__setattr__(expected, "repository", "FS-GG/foreign")
                    else:
                        object.__setattr__(expected, "branch", "foreign")
                    return True

                runner = (operator.run_pull_once if kind == "pull"
                          else operator.run_protection_once)
                self.assert_unknown(runner(expected, TracingTransport(), reserve))
                self.assertEqual(posts, [
                    ("POST", "repos/FS-GG/disposable/pulls") if kind == "pull"
                    else ("PUT", "repos/FS-GG/disposable/branches/main/protection")])

    def test_native_reader_cannot_return_mutated_selected_repository_id(self):
        for kind in ("pull", "protection"):
            with self.subTest(kind=kind):
                expected = pull_expected() if kind == "pull" else protection_expected()
                events = (pull_read_events() if kind == "pull"
                          else protection_read_events())
                playback = operator.OfflineTranscriptTransport(events)
                count = [0]

                class MutatingTransport:
                    def request(self, method, path, body=None):
                        response = playback.request(method, path, body)
                        count[0] += 1
                        if count[0] == len(events):
                            object.__setattr__(expected, "repository_id", 45)
                        return response

                reader = operator.NativeReadAdapter(MutatingTransport())
                observed = (reader.read_pull_census(expected) if kind == "pull"
                            else reader.read_protection(expected))
                self.assertEqual(observed.repository_id, 44)

    def test_native_reader_refuses_foreign_repository_url(self):
        root = "https://api.github.com/repos/FS-GG/disposable"
        for kind in ("pull", "protection"):
            for url in ("https://api.github.com/repos/FS-GG/foreign",
                        root + "?alias=1", root.replace(".com/", ".com:443/"),
                        None, root):
                with self.subTest(kind=kind, url=url):
                    expected = pull_expected() if kind == "pull" else protection_expected()
                    events = (pull_read_events() if kind == "pull"
                              else protection_read_events())
                    for item in events:
                        if (item["method"] == "GET"
                                and item["path"] == "repos/FS-GG/disposable"):
                            item["response"]["json"]["url"] = url
                    reader = operator.NativeReadAdapter(
                        operator.OfflineTranscriptTransport(events))
                    if url == root:
                        observed = (reader.read_pull_census(expected) if kind == "pull"
                                    else reader.read_protection(expected))
                        self.assertEqual(observed.repository_id, 44)
                    else:
                        with self.assertRaisesRegex(
                                operator.Refused, "native-repository-mismatch"):
                            if kind == "pull":
                                reader.read_pull_census(expected)
                            else:
                                reader.read_protection(expected)

    def test_exact_pull_refuses_foreign_nested_repository_url(self):
        root = "https://api.github.com/repos/FS-GG/disposable"
        for side in ("head", "base"):
            for url in ("https://api.github.com/repos/FS-GG/foreign",
                        root + "?alias=1", None, root):
                with self.subTest(side=side, url=url):
                    pull = copy.deepcopy(pull_observed().pulls[0])
                    pull[side]["repo"]["url"] = url
                    observed = dataclasses.replace(pull_observed(), pulls=(pull,))
                    result = operator.classify_pull_after_one_attempt(
                        pull_expected(), lambda: observed)
                    if url == root:
                        self.assertIsInstance(result, operator.ExactPull)
                    else:
                        self.assert_unknown(result)

    def test_native_pull_list_detail_refuses_nested_repository_url_drift(self):
        pull = copy.deepcopy(pull_observed().pulls[0])
        for side in ("head", "base"):
            with self.subTest(side=side):
                events = pull_read_events((pull,))
                events[3]["response"]["json"][0][side]["repo"]["url"] = (
                    "https://api.github.com/repos/FS-GG/foreign")
                events[5]["response"]["json"][side]["repo"]["url"] = (
                    "https://api.github.com/repos/FS-GG/disposable")
                reader = operator.NativeReadAdapter(
                    operator.OfflineTranscriptTransport(events))
                with self.assertRaisesRegex(
                        operator.Refused, "native-pull-list-detail-repo-drift"):
                    reader.read_pull_census(pull_expected())

    def test_lost_response_foreign_nested_repository_url_stays_unknown(self):
        expected = pull_expected()
        pull = copy.deepcopy(pull_observed().pulls[0])
        pull["base"]["repo"]["url"] = (
            "https://api.github.com/repos/FS-GG/foreign")
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         error=SENTINEL)] +
                  pull_read_events((pull,)) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_exact_pull_refuses_foreign_pull_object_url(self):
        root = "https://api.github.com/repos/FS-GG/disposable/pulls/8"
        for url in ("https://api.github.com/repos/FS-GG/foreign/pulls/8",
                    "https://api.github.com/repos/FS-GG/disposable/pulls/9",
                    root + "?alias=1", None, root):
            with self.subTest(url=url):
                pull = copy.deepcopy(pull_observed().pulls[0])
                pull["url"] = url
                observed = dataclasses.replace(pull_observed(), pulls=(pull,))
                result = operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: observed)
                if url == root:
                    self.assertIsInstance(result, operator.ExactPull)
                else:
                    self.assert_unknown(result)

    def test_native_pull_list_detail_refuses_object_url_drift(self):
        pull = copy.deepcopy(pull_observed().pulls[0])
        events = pull_read_events((pull,))
        events[3]["response"]["json"][0]["url"] = (
            "https://api.github.com/repos/FS-GG/foreign/pulls/8")
        events[5]["response"]["json"]["url"] = (
            "https://api.github.com/repos/FS-GG/disposable/pulls/8")
        reader = operator.NativeReadAdapter(
            operator.OfflineTranscriptTransport(events))
        with self.assertRaisesRegex(operator.Refused, "native-pull-list-detail-url-drift"):
            reader.read_pull_census(pull_expected())
        exact = "https://api.github.com/repos/FS-GG/disposable/pulls/8"
        events = pull_read_events((pull,))
        events[3]["response"]["json"][0]["url"] = exact
        events[5]["response"]["json"]["url"] = exact
        reader = operator.NativeReadAdapter(
            operator.OfflineTranscriptTransport(events))
        self.assertEqual(reader.read_pull_census(pull_expected()).pulls[0]["url"], exact)

    def test_lost_response_foreign_pull_object_url_stays_unknown(self):
        expected = pull_expected()
        pull = copy.deepcopy(pull_observed().pulls[0])
        pull["url"] = "https://api.github.com/repos/FS-GG/foreign/pulls/8"
        post = pull_read_events((pull,))
        post[3]["response"]["json"][0]["url"] = pull["url"]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_native_protection_refuses_foreign_branch_commit_url(self):
        selected = f"https://api.github.com/repos/FS-GG/disposable/commits/{SHA_B}"
        foreign = f"https://api.github.com/repos/FS-GG/foreign/commits/{SHA_B}"
        for probe in (2, 6):
            for url in (foreign, selected + "?alias=1", None, selected):
                with self.subTest(probe=probe, url=url):
                    events = protection_read_events(
                        protected=True, policy=protection_observed().policy)
                    events[probe]["response"]["json"]["commit"]["url"] = url
                    reader = operator.NativeReadAdapter(
                        operator.OfflineTranscriptTransport(events))
                    if url == selected:
                        self.assertTrue(reader.read_protection(
                            protection_expected()).protected)
                    else:
                        with self.assertRaisesRegex(
                                operator.Refused,
                                ("native-branch-identity" if probe == 2
                                 else "native-protection-terminal-branch-drift")):
                            reader.read_protection(protection_expected())

    def test_lost_response_foreign_branch_commit_url_stays_unknown(self):
        expected = protection_expected()
        post = protection_read_events(
            protected=True, policy=protection_observed().policy)
        foreign = f"https://api.github.com/repos/FS-GG/foreign/commits/{SHA_B}"
        for index in (2, 6):
            post[index]["response"]["json"]["commit"]["url"] = foreign
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_native_protection_refuses_terminal_policy_type_alias(self):
        for field in ("strict", "admin-enabled", "check-app-id"):
            with self.subTest(field=field):
                events = protection_read_events(
                    protected=True, policy=protection_observed().policy)
                terminal = events[7]["response"]["json"]
                if field == "strict":
                    terminal["required_status_checks"]["strict"] = 1
                elif field == "admin-enabled":
                    terminal["enforce_admins"]["enabled"] = 0
                else:
                    terminal["required_status_checks"]["checks"][0]["app_id"] = 17.0
                reader = operator.NativeReadAdapter(
                    operator.OfflineTranscriptTransport(events * 2))
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(),
                    lambda: reader.read_protection(protection_expected())))

    def test_lost_response_terminal_policy_type_alias_stays_unknown(self):
        expected = protection_expected()
        post = protection_read_events(
            protected=True, policy=protection_observed().policy)
        post[7]["response"]["json"]["required_status_checks"]["strict"] = 1
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_native_pull_refuses_list_detail_draft_type_alias(self):
        pull = copy.deepcopy(pull_observed().pulls[0])
        for listed_draft in (0, 0.0):
            with self.subTest(listed_draft=listed_draft):
                events = pull_read_events((pull,))
                events[3]["response"]["json"][0]["draft"] = listed_draft
                reader = operator.NativeReadAdapter(
                    operator.OfflineTranscriptTransport(events * 2))
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(),
                    lambda: reader.read_pull_census(pull_expected())))

    def test_lost_response_pull_draft_type_alias_stays_unknown(self):
        expected = pull_expected()
        pull = copy.deepcopy(pull_observed().pulls[0])
        post = pull_read_events((pull,))
        post[3]["response"]["json"][0]["draft"] = 0
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_native_json_refuses_exponent_overflow(self):
        for raw in (b'{"value":1e999}', b'{"value":-1e999}'):
            with self.subTest(raw=raw):
                with self.assertRaisesRegex(operator.Refused, "json-nonfinite"):
                    operator._strict_json(raw)
        self.assertEqual(operator._strict_json(b'{"value":1.5}'), {"value": 1.5})

    def test_lost_response_nonfinite_repository_extra_stays_unknown(self):
        expected = protection_expected()
        post = protection_read_events(
            protected=True, policy=protection_observed().policy)
        raw = '{"id":44,"full_name":"FS-GG/disposable","extra":1e999}'
        for index in (0, 4):
            post[index]["response"]["rawBody"] = raw
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_repository_dot_segments_refuse_before_native_callbacks(self):
        for repository in ("../disposable", "./disposable",
                           "FS-GG/..", "FS-GG/."):
            for kind in ("pull", "protection"):
                with self.subTest(repository=repository, kind=kind):
                    original = pull_expected() if kind == "pull" else protection_expected()
                    expected = dataclasses.replace(original, repository=repository)
                    calls = []

                    class Transport:
                        def request(self, method, path, body=None):
                            calls.append((method, path))
                            raise OSError("no-native-request-allowed")

                    def reserve(_key):
                        calls.append(("reserve", ""))
                        return True

                    runner = operator.run_pull_once if kind == "pull" else operator.run_protection_once
                    self.assert_unknown(runner(expected, Transport(), reserve))
                    self.assertEqual(calls, [])

    def test_native_reader_refuses_repository_owner_name_conflict(self):
        for kind in ("pull", "protection"):
            for field, value in (("owner", {"login": "Foreign"}),
                                 ("name", "foreign"),
                                 ("owner", None)):
                with self.subTest(kind=kind, field=field, value=value):
                    expected = pull_expected() if kind == "pull" else protection_expected()
                    events = (pull_read_events() if kind == "pull"
                              else protection_read_events())
                    for item in events:
                        if (item["method"] == "GET"
                                and item["path"] == "repos/FS-GG/disposable"):
                            item["response"]["json"][field] = value
                    reader = operator.NativeReadAdapter(
                        operator.OfflineTranscriptTransport(events))
                    with self.assertRaisesRegex(
                            operator.Refused, "native-repository-mismatch"):
                        if kind == "pull":
                            reader.read_pull_census(expected)
                        else:
                            reader.read_protection(expected)
            events = (pull_read_events() if kind == "pull"
                      else protection_read_events())
            for item in events:
                if (item["method"] == "GET"
                        and item["path"] == "repos/FS-GG/disposable"):
                    item["response"]["json"].update({
                        "owner": {"login": "FS-GG"}, "name": "disposable"})
            reader = operator.NativeReadAdapter(
                operator.OfflineTranscriptTransport(events))
            observed = (reader.read_pull_census(pull_expected()) if kind == "pull"
                        else reader.read_protection(protection_expected()))
            self.assertEqual(observed.repository_id, 44)

    def test_lost_response_foreign_repository_owner_stays_unknown(self):
        expected = protection_expected()
        post = protection_read_events(
            protected=True, policy=protection_observed().policy)
        for index in (0, 4):
            post[index]["response"]["json"]["owner"] = {"login": "Foreign"}
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

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

    def test_pull_list_omitted_fields_refuse_after_lost_response(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        for missing in ("state", "draft", "title", "body"):
            with self.subTest(missing=missing):
                post = pull_read_events((pull,))
                del post[3]["response"]["json"][0][missing]
                events = (pull_read_events() * 2 +
                          [event("POST", "repos/FS-GG/disposable/pulls",
                                 body=operator.pull_request_body(expected),
                                 error=SENTINEL)] + post * 2)
                transport = operator.OfflineTranscriptTransport(events)
                self.assert_unknown(operator.run_pull_once(
                    expected, transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)

    def test_pull_ref_must_name_selected_commit_object_after_lost_response(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        for location, key, value in (
                ("object", "type", "tag"),
                ("object", "url",
                 "https://api.github.com/repos/FS-GG/foreign/git/commits/" + SHA_A),
                ("ref", "url",
                 "https://api.github.com/repos/FS-GG/disposable/git/refs/heads/source?alias=1"),
                ("object", "type", None)):
            with self.subTest(location=location, key=key, value=value):
                post = pull_read_events((pull,))
                for index in (1, 7):
                    body = post[index]["response"]["json"]
                    target = body["object"] if location == "object" else body
                    if value is None:
                        del target[key]
                    else:
                        target[key] = value
                events = (pull_read_events() * 2 +
                          [event("POST", "repos/FS-GG/disposable/pulls",
                                 body=operator.pull_request_body(expected),
                                 error=SENTINEL)] + post * 2)
                transport = operator.OfflineTranscriptTransport(events)
                self.assert_unknown(operator.run_pull_once(
                    expected, transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)

    def test_pull_pagination_direction_must_be_coherent_after_lost_response(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        prefix = "https://api.github.com/repos/FS-GG/disposable/pulls?state=open&per_page=100&page="
        for response_index, relation, linked_page in (
                (3, "prev", 50),
                (3, "first", 2),
                (4, "first", 2)):
            with self.subTest(response_index=response_index, relation=relation):
                post = pull_read_events((pull,))
                post[response_index]["response"]["headers"] = [[
                    "Link", f'<{prefix}{linked_page}>; rel="{relation}"']]
                events = (pull_read_events() * 2 +
                          [event("POST", "repos/FS-GG/disposable/pulls",
                                 body=operator.pull_request_body(expected),
                                 error=SENTINEL)] + post * 2)
                transport = operator.OfflineTranscriptTransport(events)
                self.assert_unknown(operator.run_pull_once(
                    expected, transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)
        post = pull_read_events((pull,))
        post[3]["response"]["headers"] = [[
            "Link", f'<{prefix}1>; rel="first"']]
        post[4]["response"]["headers"] = [[
            "Link", f'<{prefix}1>; rel="first", <{prefix}1>; rel="prev"']]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assertIsInstance(operator.run_pull_once(
            expected, transport, reserve_once_factory()), operator.ExactPull)
        self.assertEqual(transport.writes, 1)

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

    def test_protection_rejects_unselected_enabled_policy_flag(self):
        observed = protection_observed()
        for key in operator.UNSELECTED_PROTECTION_FLAGS:
            for value in ({"enabled": True}, True, None, 1.0):
                with self.subTest(key=key, value=value):
                    policy = {**observed.policy, key: value}
                    wrong = dataclasses.replace(observed, policy=policy)
                    self.assert_unknown(operator.classify_protection_after_one_attempt(
                        protection_expected(), lambda: wrong,
                        provider_response={"status": "lost", "body": SENTINEL}))
            for value in ({"enabled": False}, False):
                with self.subTest(key=key, disabled=value):
                    policy = {**observed.policy, key: value}
                    compatible = dataclasses.replace(observed, policy=policy)
                    self.assertIsInstance(operator.classify_protection_after_one_attempt(
                        protection_expected(), lambda: compatible),
                        operator.ExactProtection)

    def test_protection_requires_explicit_null_review_and_actor_restrictions(self):
        observed = protection_observed()
        for key in ("required_pull_request_reviews", "restrictions"):
            with self.subTest(missing=key):
                policy = copy.deepcopy(observed.policy)
                del policy[key]
                incomplete = dataclasses.replace(observed, policy=policy)
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: incomplete,
                    provider_response={"status": 500, "body": SENTINEL}))

    def test_protection_foreign_response_url_refuses_after_lost_response(self):
        expected = protection_expected()
        foreign = "https://api.github.com/repos/FS-GG/foreign/branches/main/protection"
        for section, key, url in (
                ("policy", "url", foreign),
                ("checks", "url", foreign + "/required_status_checks"),
                ("checks", "contexts_url", foreign + "/required_status_checks/contexts"),
                ("policy", "url", 17)):
            with self.subTest(section=section, key=key, url=url):
                policy = copy.deepcopy(protection_observed().policy)
                destination = (policy if section == "policy" else
                               policy["required_status_checks"])
                destination[key] = url
                observed = dataclasses.replace(protection_observed(), policy=policy)
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    expected, lambda: observed, provider_response={"body": SENTINEL}))
                events = (protection_read_events() * 2 +
                          [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                                 body=operator.protection_body(expected), error=SENTINEL)] +
                          protection_read_events(True, policy) * 2)
                transport = operator.OfflineTranscriptTransport(events)
                self.assert_unknown(operator.run_protection_once(
                    expected, transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)
        policy = copy.deepcopy(protection_observed().policy)
        root = "https://api.github.com/repos/FS-GG/disposable/branches/main/protection"
        policy["url"] = root
        policy["required_status_checks"]["url"] = root + "/required_status_checks"
        policy["required_status_checks"]["contexts_url"] = (
            root + "/required_status_checks/contexts")
        observed = dataclasses.replace(protection_observed(), policy=policy)
        self.assertIsInstance(operator.classify_protection_after_one_attempt(
            expected, lambda: observed), operator.ExactProtection)

    def test_branch_protection_url_must_name_selected_target(self):
        expected = protection_expected()
        policy = protection_observed().policy
        root = "https://api.github.com/repos/FS-GG/disposable/branches/main/protection"
        variants = (
            "https://api.github.com/repos/FS-GG/foreign/branches/main/protection",
            root.replace("api.github.com", "API.github.com"),
            root.replace("api.github.com", "api.github.com:443"),
            root + "?alias=1",
            root.replace("/main/", "/%6dain/"),
            17,
        )
        for wrong in variants:
            with self.subTest(wrong=wrong):
                post = protection_read_events(True, policy)
                post[2]["response"]["json"]["protection_url"] = wrong
                post[6]["response"]["json"]["protection_url"] = wrong
                events = (protection_read_events() * 2 +
                          [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                                 body=operator.protection_body(expected), error=SENTINEL)] +
                          post * 2)
                transport = operator.OfflineTranscriptTransport(events)
                self.assert_unknown(operator.run_protection_once(
                    expected, transport, reserve_once_factory()))
                self.assertEqual(transport.writes, 1)
        post = protection_read_events(True, policy)
        post[2]["response"]["json"]["protection_url"] = root
        post[6]["response"]["json"]["protection_url"] = root
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(expected), error=SENTINEL)] +
                  post * 2)
        result = operator.run_protection_once(
            expected, operator.OfflineTranscriptTransport(events), reserve_once_factory())
        self.assertIsInstance(result, operator.ExactProtection)

    def test_pull_accepts_only_two_complete_exact_reads(self):
        reads = iter((pull_observed(), pull_observed()))
        result = operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: next(reads),
            provider_response={"status": "unknown", "body": SENTINEL})
        self.assertEqual(result, operator.ExactPull(8, "PR_8", DIGEST))

    def test_direct_pull_classifier_keeps_explicit_refusal_unknown(self):
        # A future installed caller may pass its counted response directly to
        # the classifier. Exact poststate cannot override an explicit refusal.
        for response in ({"status": 302, "body": SENTINEL},
                         operator.HttpResponse(401, (), b"redacted"),
                         {"status": True}, {"status": 302.0},
                         {"status": "302"}):
            reads = iter((pull_observed(), pull_observed()))
            with self.subTest(response=type(response).__name__):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: next(reads),
                    provider_response=response))
        for response in ({"status": 500},
                         {"status": "lost"}):
            reads = iter((pull_observed(), pull_observed()))
            self.assertIsInstance(operator.classify_pull_after_one_attempt(
                pull_expected(), lambda: next(reads),
                provider_response=response), operator.ExactPull)

    def test_direct_pull_success_response_must_match_native_identity(self):
        complete = dict(pull_observed().pulls[0],
                        url="https://api.github.com/repos/FS-GG/disposable/pulls/8")
        for response in ({"status": 201, "body": complete},
                         operator.HttpResponse(201, (), json.dumps(complete).encode()),
                         operator.HttpResponse(201, (("Location", complete["url"]),),
                                               json.dumps(complete).encode())):
            with self.subTest(positive=type(response).__name__):
                self.assertIsInstance(operator.classify_pull_after_one_attempt(
                    pull_expected(), pull_observed,
                    provider_response=response), operator.ExactPull)
        wrong_head = copy.deepcopy(complete)
        wrong_head["head"]["sha"] = SHA_C
        wrong_number = dict(complete, number=9)
        for response in ({"status": 201},
                         {"status": 201, "body": wrong_head},
                         {"status": 201, "body": wrong_number},
                         operator.HttpResponse(201, (("Location", complete["url"] + "?q=1"),),
                                               json.dumps(complete).encode()),
                         operator.HttpResponse(201, (("Location", complete["url"]),
                                                     ("location", complete["url"])),
                                               json.dumps(complete).encode()),
                         {"status": 201, "body": complete,
                          "headers": {"Location": complete["url"] + "/9"}},
                         operator.HttpResponse(201, (), b'{"number":8,"number":9}')):
            with self.subTest(negative=repr(response)[:80]):
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), pull_observed,
                    provider_response=response))

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

    def test_direct_protection_classifier_keeps_explicit_refusal_unknown(self):
        for response in ({"status": 302, "body": SENTINEL},
                         operator.HttpResponse(401, (), b"redacted")):
            reads = iter((protection_observed(), protection_observed()))
            with self.subTest(response=type(response).__name__):
                self.assert_unknown(operator.classify_protection_after_one_attempt(
                    protection_expected(), lambda: next(reads),
                    provider_response=response))

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

    def test_q3_success_response_foreign_pull_identity_stays_unknown(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         value={"number": 9, "node_id": "PR_9"}, status=201)] +
                  pull_read_events((pull,)) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            expected, transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_q3_success_response_exact_pull_identity_stays_exact(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        response_pull = dict(pull, url=
                             "https://api.github.com/repos/FS-GG/disposable/pulls/8")
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         value=response_pull, status=201)] +
                  pull_read_events((pull,)) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assertIsInstance(operator.run_pull_once(
            expected, transport, reserve_once_factory()), operator.ExactPull)
        self.assertEqual(transport.writes, 1)

    def test_q3_success_response_foreign_location_stays_unknown(self):
        expected = pull_expected()
        pull = pull_observed().pulls[0]
        response_pull = dict(pull, url=
                             "https://api.github.com/repos/FS-GG/disposable/pulls/8")
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(expected),
                         value=response_pull, status=201,
                         headers={"Location":
                                  "https://api.github.com/repos/FS-GG/disposable/pulls/9"})] +
                  pull_read_events((pull,)) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            expected, transport, reserve_once_factory()))
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

    def test_lost_response_foreign_nested_repository_owner_stays_unknown(self):
        pull = copy.deepcopy(pull_observed().pulls[0])
        pull["head"]["repo"]["owner"] = {"login": "Foreign"}
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] +
                  pull_read_events((pull,)) * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_nested_repository_owner_and_name_bind_selected_full_name(self):
        for side, key, value in (("head", "owner", {"login": "Foreign"}),
                                 ("base", "owner", {"login": "Foreign"}),
                                 ("head", "name", "foreign"),
                                 ("base", "name", "foreign")):
            with self.subTest(side=side, key=key):
                pull = copy.deepcopy(pull_observed().pulls[0])
                pull[side]["repo"][key] = value
                observed = dataclasses.replace(pull_observed(), pulls=(pull,))
                self.assert_unknown(operator.classify_pull_after_one_attempt(
                    pull_expected(), lambda: observed))
        pull = copy.deepcopy(pull_observed().pulls[0])
        for side in ("head", "base"):
            pull[side]["repo"]["owner"] = {"login": "FS-GG"}
            pull[side]["repo"]["name"] = "disposable"
        observed = dataclasses.replace(pull_observed(), pulls=(pull,))
        self.assertIsInstance(operator.classify_pull_after_one_attempt(
            pull_expected(), lambda: observed), operator.ExactPull)

    def test_unselected_pull_nested_repository_identity_is_still_checked(self):
        unrelated = copy.deepcopy(pull_observed().pulls[0])
        unrelated["body"] = "unrelated"
        unrelated["base"]["repo"]["name"] = "foreign"
        reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
            pull_read_events((unrelated,))))
        with self.assertRaisesRegex(operator.Refused,
                                    "native-pull-list-detail-repo-drift"):
            reader.read_pull_census(pull_expected())

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

    def test_q6_terminal_repository_node_identity_drift_is_unknown(self):
        pull = pull_observed().pulls[0]
        post = pull_read_events((pull,))
        post[0]["response"]["json"]["node_id"] = "R_44_A"
        post[6]["response"]["json"]["node_id"] = "R_44_B"
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_native_repository_node_identity_requires_stable_shape(self):
        pull = pull_observed().pulls[0]
        for first, terminal, exact in (("R_44", "R_44", True),
                                       ("R_44", None, False),
                                       (True, True, False)):
            with self.subTest(first=first, terminal=terminal):
                events = pull_read_events((pull,))
                events[0]["response"]["json"]["node_id"] = first
                if terminal is not None:
                    events[6]["response"]["json"]["node_id"] = terminal
                reader = operator.NativeReadAdapter(
                    operator.OfflineTranscriptTransport(events))
                if exact:
                    self.assertTrue(reader.read_pull_census(pull_expected()).complete)
                else:
                    with self.assertRaises(operator.Refused):
                        reader.read_pull_census(pull_expected())

    def test_q6_terminal_protection_repository_node_drift_is_unknown(self):
        policy = protection_observed().policy
        post = protection_read_events(True, policy)
        post[0]["response"]["json"]["node_id"] = "R_44_A"
        post[4]["response"]["json"]["node_id"] = "R_44_B"
        events = (protection_read_events() * 2 +
                  [event("PUT", "repos/FS-GG/disposable/branches/main/protection",
                         body=operator.protection_body(protection_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_protection_once(
            protection_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_q6_open_census_foreign_closed_row_stays_unknown(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, node_id="PR_9", body="unrelated", state="closed")
        post = pull_read_events((selected, unrelated))
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_unrelated_open_census_rows_must_have_coherent_lifecycle(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, node_id="PR_9", body="unrelated")
        observed = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
            pull_read_events((selected, unrelated)))).read_pull_census(pull_expected())
        self.assertEqual(len(observed.pulls), 1)
        for field, value in (("state", "closed"), ("draft", 0),
                             ("merged", True), ("merged_at", "2026-09-25T00:00:00Z")):
            with self.subTest(field=field):
                wrong = copy.deepcopy(unrelated)
                wrong[field] = value
                reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
                    pull_read_events((selected, wrong))))
                with self.assertRaisesRegex(operator.Refused,
                                            "native-open-pull-row-state"):
                    reader.read_pull_census(pull_expected())

    def test_q6_open_census_foreign_base_target_stays_unknown(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, node_id="PR_9", body="unrelated")
        unrelated["base"]["repo"] = {"id": 45, "full_name": "FS-GG/foreign"}
        post = pull_read_events((selected, unrelated))
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_open_census_base_target_is_selected_for_every_row(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, node_id="PR_9", body="unrelated")
        unrelated["head"]["repo"] = {"id": 45, "full_name": "FS-GG/fork"}
        reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
            pull_read_events((selected, unrelated))))
        self.assertEqual(len(reader.read_pull_census(pull_expected()).pulls), 1)
        for foreign in ({"id": 45, "full_name": "FS-GG/disposable"},
                        {"id": 44, "full_name": "FS-GG/foreign"}):
            with self.subTest(foreign=foreign):
                wrong = copy.deepcopy(unrelated)
                wrong["base"]["repo"] = foreign
                reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
                    pull_read_events((selected, wrong))))
                with self.assertRaisesRegex(operator.Refused,
                                            "native-pull-list-base-target"):
                    reader.read_pull_census(pull_expected())

    def test_q6_open_census_duplicate_node_id_stays_unknown(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, body="unrelated")
        post = pull_read_events((selected, unrelated))
        events = (pull_read_events() * 2 +
                  [event("POST", "repos/FS-GG/disposable/pulls",
                         body=operator.pull_request_body(pull_expected()),
                         error=SENTINEL)] + post * 2)
        transport = operator.OfflineTranscriptTransport(events)
        self.assert_unknown(operator.run_pull_once(
            pull_expected(), transport, reserve_once_factory()))
        self.assertEqual(transport.writes, 1)

    def test_open_census_requires_unique_nonempty_node_ids(self):
        selected = pull_observed().pulls[0]
        unrelated = copy.deepcopy(selected)
        unrelated.update(number=9, node_id="PR_9", body="unrelated")
        reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
            pull_read_events((selected, unrelated))))
        self.assertEqual(len(reader.read_pull_census(pull_expected()).pulls), 1)
        for node_id in ("PR_8", ""):
            with self.subTest(node_id=node_id):
                wrong = copy.deepcopy(unrelated)
                wrong["node_id"] = node_id
                reader = operator.NativeReadAdapter(operator.OfflineTranscriptTransport(
                    pull_read_events((selected, wrong))))
                with self.assertRaisesRegex(operator.Refused,
                                            "native-pull-list-identity"):
                    reader.read_pull_census(pull_expected())

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
