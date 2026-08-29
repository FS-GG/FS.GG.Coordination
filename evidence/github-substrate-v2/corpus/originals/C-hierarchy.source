#!/usr/bin/env python3
"""Case 15's `child` world, served over HTTP for the compiled engine (#320).

`child` links an issue as a native SUB-ISSUE of a parent, so `done --flip`'s epic rollup can see it. The
corpus certifies four properties, and every one is a fail-closed rule wearing a different coat:

    link          POST the child's REST INTEGER ID (#266: a NUMBER, not the quoted string form a naive
                  `-f` sent, which 422s; and the id, never the issue number — two repos can each have #7).
    idempotent    re-linking a child already attached is SUCCESS, not a 422 — a worker re-running its
                  close-out never has to reason about whether the edge exists. Keyed BY ID.
    fail closed   the existing-links read must FAIL CLOSED (#320): an unreachable read is NOT an absent
                  edge. Swallowing it would make `child` POST, collect a 422, and blame the token.
    surface       a failed POST reports the API's OWN diagnosis (422 vs 403), never a guessed cause.

Bash carries all four (case 15); the engine's `child` POSTed BLINDLY — no existing-links read at all, so
no idempotency and no #320 fail-closed. This fixture is case 15's world one transport over. The `-F`
(typed-number) assertion the corpus counts on the `gh` stub is re-expressed here at the HTTP layer: the
POST body is recorded and served back on `/_posts`, so the harness asserts `sub_issue_id` arrives as a
JSON NUMBER (`1047`), not the quoted string that 422s.

Env toggles (each parity leg spawns a FRESH server, since a POST mutates the edge set):
    FSGG_PARITY_LINKED=1047[,...]   the parent already has these sub-issue ids (the idempotency leg)
    FSGG_PARITY_SUBISSUES_FAIL=1    the existing-links GET 500s (the #320 unreachable-read leg)
    FSGG_PARITY_POST_FAIL=1         the link POST 422s (the surface-the-API's-error leg)

The child's REST id is its number + 1000 (case 15 seeds `id:($n + 1000)`), so #47 -> 1047, #302 -> 1302.
"""

import json
import os
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LINKED = [int(x) for x in os.environ.get("FSGG_PARITY_LINKED", "").split(",") if x.strip()]
SUBISSUES_FAIL = os.environ.get("FSGG_PARITY_SUBISSUES_FAIL", "") != ""
POST_FAIL = os.environ.get("FSGG_PARITY_POST_FAIL", "") != ""

LOCK = threading.Lock()
_POSTS = []  # every sub_issue POST body this process received — the harness reads it back on /_posts.


def rest_id(number):
    return number + 1000


class H(BaseHTTPRequestHandler):
    # Keep-alive, so the server does not close after every response: HTTP/1.0's close-per-response
    # races the engine's pooling HttpClient and RSTs away a written response (#761). Pairs with
    # ThreadingHTTPServer below — a kept-alive connection would pin a single-threaded server.
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def _send(self, code, payload):
        b = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        p = self.path.split("?", 1)[0]
        if p == "/_posts":
            with LOCK:
                return self._send(200, list(_POSTS))
        # The existing-links read. #320: on the fail toggle this 500s, and the engine must REFUSE, never
        # read it as an empty edge set.
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/sub_issues$", p)
        if m:
            if SUBISSUES_FAIL:
                return self._send(500, {"message": "sub-issues read unavailable"})
            with LOCK:
                return self._send(200, [{"id": i} for i in LINKED])
        # The child's REST id lookup (`Reads.restId`): id = number + 1000.
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)$", p)
        if m:
            n = int(m.group(1))
            return self._send(200, {"number": n, "id": rest_id(n)})
        if p.rstrip("/") == "/rate_limit":
            return self._send(200, {"resources": {"graphql": {"remaining": 4960, "limit": 5000}}})
        self._send(500, {"message": f"unhandled GET {p}"})

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
        p = self.path.split("?", 1)[0]
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/sub_issues$", p)
        if m:
            try:
                body = json.loads(raw)
            except json.JSONDecodeError:
                body = raw
            with LOCK:
                _POSTS.append(body)
            if POST_FAIL:
                # The API's own diagnosis — a 422 the engine must surface rather than guess a cause for.
                return self._send(422, {"message": "Unprocessable Entity: sub_issue could not be added"})
            cid = m.group(1)
            return self._send(201, {"id": int(cid), "sub_issues_summary": {"total": 1}})
        self._send(500, {"message": f"unhandled POST {p}"})


def main():
    s = ThreadingHTTPServer(("127.0.0.1", 0), H)
    print(s.server_address[1], flush=True)
    s.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
