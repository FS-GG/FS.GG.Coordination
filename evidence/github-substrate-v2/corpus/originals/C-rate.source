#!/usr/bin/env python3
"""Case 40's rate-limited board, served over HTTP for the compiled engine (#418).

A budget's exhaustion is a DISTINCT outcome from an empty queue, a lost race, or an unreadable board: it
is a BACK-OFF signal. The corpus certifies it (case 40): a `take` whose scan hits the budget exits
`EX_RATE` (75), the code `/pnext-item` teaches a worker to key on ("if take exits 75, back off until the
reset").

Bootstrap (projectsV2) and the fields read succeed; the BOARD ITEMS read — the one that actually spends
the budget — returns HTTP 403 with GitHub's rate-limit body. The engine's transport does NOT retry a
rate limit (retrying spends more calls confirming the same 403 and delays the back-off), classifies it
as `RateLimited`, and `take` fails with exit 75 — NOT a protocol error, NOT a lost race, NOT an empty
queue. This serves that exact shape so the engine can be held to #418's certified exit code over HTTP.

BOTH BUDGETS DIE HERE, AND THEY SAY SO DIFFERENTLY. This docstring used to open "The GraphQL budget is
the first to die under fan-out", and that premise — true enough in #418 — is what let the engine call
EVERY rate limit a GraphQL one. It is not reliably true: measured live on 2026-07-16, REST core sat at
0/5000 and 403'd every read while GraphQL had 3,639 points to spare, and `/pnext-item`'s own "read issues
over REST, it's free" doctrine is what drained it. So the POST leg 403s as `x-ratelimit-resource:
graphql` and the GET leg as `core`, and the corpus asserts the engine tells them APART — one fixture, two
legs, two different sentences out of one binary.
"""

import json
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

RATE = {"cost": 1, "remaining": 0}
RL_BODY = {"message": "API rate limit exceeded for installation",
           "documentation_url": "https://docs.github.com/rest#rate-limiting"}

# `X-RateLimit-Resource` — the header GitHub really sends, and the one that names WHICH budget died.
#
# This fixture used to omit it, and the omission is exactly why the corpus could not catch the bug it now
# certifies against: with no resource header there is nothing to name, so "GraphQL budget EXHAUSTED" and
# "REST budget EXHAUSTED" were indistinguishable to case 40 — which only ever grepped for the word
# "budget". The engine hardcoded "GraphQL" for BOTH and the corpus stayed green through it. A fixture that
# under-models the API cannot certify the code that reads it.
#
# `X-RateLimit-Reset` is epoch seconds and rides on the same 403. Nothing read it either, so a REST limit
# could only ever report "the reset time could not be read".
#
# Computed at import, ~30m out: a real GitHub window is at most an hour, and the engine RENDERS this as
# "resets in ~30m". A fixed far-future constant would be stabler still, and it would print "resets in
# ~38637009m" — a number no real limit can produce, which is how a fixture teaches a reader to distrust
# the very output it certifies. The corpus greps for "resets in ~", so the drifting minute is not load-
# bearing; the plausibility is.
RESET_EPOCH = str(int(time.time()) + 1800)


def graphql(q):
    if "projectsV2" in q:
        return 200, {"data": {"organization": {"projectsV2": {"nodes": [
            {"number": 12, "title": "Coordination", "id": "PVT_coord"}]}}, "rateLimit": RATE}}
    if "fields(first" in q:
        return 200, {"data": {"organization": {"projectV2": {"fields": {"nodes": [
            {"id": "PVTSSF_status", "name": "Status", "dataType": "SINGLE_SELECT",
             "options": [{"id": "opt_ready", "name": "Ready"}]}]}}}, "rateLimit": RATE}}
    if "items(first" in q:
        # The read that spends the budget: 403 rate-limit. This is what `take`'s scan hits.
        return 403, RL_BODY
    return 200, {"errors": [{"message": "unhandled"}]}


class H(BaseHTTPRequestHandler):
    # Keep-alive, so the server does not close after every response: HTTP/1.0's close-per-response
    # races the engine's pooling HttpClient and RSTs away a written response (#761). Pairs with
    # ThreadingHTTPServer below — a kept-alive connection would pin a single-threaded server.
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def _send(self, code, payload, headers=None):
        b = json.dumps(payload).encode()
        self.send_response(code)
        if headers is None:
            self.send_header("X-RateLimit-Resource", "core")
            self.send_header("X-RateLimit-Limit", "5000")
            self.send_header("X-RateLimit-Remaining", "4800")
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        try:
            q = json.loads(self.rfile.read(n).decode()).get("query", "")
        except json.JSONDecodeError:
            return self._send(500, {"errors": [{"message": "bad body"}]})
        code, payload = graphql(q)
        # A GraphQL 403 names `graphql` as its resource — this is the POST path, so that is what died.
        hdr = ({"x-ratelimit-remaining": "0", "retry-after": "60",
                "x-ratelimit-resource": "graphql", "x-ratelimit-reset": RESET_EPOCH}
               if code == 403 else None)
        self._send(code, payload, hdr)

    def do_GET(self):
        p = self.path.split("?", 1)[0]
        if p.rstrip("/") == "/rate_limit":
            return self._send(200, {"resources": {"graphql": {"remaining": 0, "limit": 5000}}})
        # Any REST read is also rate-limited on this board — and REST bills against `core`, NOT `graphql`.
        self._send(403, RL_BODY, {"x-ratelimit-remaining": "0",
                                  "x-ratelimit-resource": "core",
                                  "x-ratelimit-reset": RESET_EPOCH})


def main():
    s = ThreadingHTTPServer(("127.0.0.1", 0), H)
    print(s.server_address[1], flush=True)
    s.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
