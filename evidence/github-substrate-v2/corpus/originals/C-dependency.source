#!/usr/bin/env python3
"""Case 13's `Blocked by` write gate, served over HTTP for the compiled engine.

`Blocked by` is a TYPED dependency edge, but Projects v2 has no dependency field — so it is TEXT, and in
bash it drifted back into a resolution LOG ("RESOLVED: #8 closed, shipped @d80a8ae") that `.blocked` — which
reads the field back as refs — could not parse. `set-field <issue> 'Blocked by' <value>` is the gate on the
WRITE: every accepted form (owner/repo#n, repo#n, a bare #n, an issue URL) reduces to one canonical
`owner/repo#n`, refs that canonicalize alike are de-duped, and prose is REFUSED before it can be stored.

The corpus (case 13, lines 153-243) counts `gh` invocations: it asserts the exact `--text` the field
mutation would carry, that an empty value clears via `--clear` (never `--text ''`, which the API treats as a
no-op), that a REFUSED write spends ZERO GraphQL (validation precedes item resolution — the budget that dies
first), and that the gate is SCOPED to `Blocked by` (Contract and every other TEXT field stay free-form).

An F# tool calling HTTPS directly is invisible to that `gh` stub, so this fixture moves the checks one
transport over: it records every FIELD mutation the engine POSTs — the field it targets (mapped from the
`fieldId` variable), whether it SET a value or CLEARED it, and the text it carried — and counts the GraphQL
requests so "a refused write spends no GraphQL" is a request count of ZERO. The property is unchanged; it is
checked at the HTTP layer, ADR-0040 C1's whole resolution.

The board:
    Status     SINGLE_SELECT  (Ready / In progress / Done) — present so bootstrap resolves a real board
    Blocked by TEXT           (the gated field)
    Contract   TEXT           (an ungated TEXT field — the scope control)
and FS.GG.SDD#42 is item PVTI_item42 on project #12 ("Coordination").
"""

import json
import re
import sys
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

RATE = {"cost": 1, "remaining": 4960}
ITEM = 42
ITEM_ID = "PVTI_item42"

FIELDS = [
    {"id": "PVTSSF_status", "name": "Status", "dataType": "SINGLE_SELECT",
     "options": [{"id": "opt_ready", "name": "Ready"}, {"id": "opt_wip", "name": "In progress"},
                 {"id": "opt_done", "name": "Done"}]},
    {"id": "PVTF_blocked", "name": "Blocked by", "dataType": "TEXT"},
    {"id": "PVTF_contract", "name": "Contract", "dataType": "TEXT"},
]
FIELD_NAME = {f["id"]: f["name"] for f in FIELDS}

LOCK = threading.Lock()
GQL_COUNT = 0           # every POST to /graphql — the `gcount` the corpus reads, one transport over.
WRITES = []             # every field mutation, in order: {field, op: set|clear, text}
COMMENTS = []           # ephemeral REST-backed Blocked-by mutation leases
NEXT_COMMENT_ID = 1000


def _is_field_mutation(doc):
    return "updateProjectV2ItemFieldValue" in doc or "clearProjectV2ItemFieldValue" in doc


def graphql(q, variables):
    if "projectsV2" in q:
        return {"data": {"organization": {"projectsV2": {"nodes": [
            {"number": 12, "title": "Coordination", "id": "PVT_coord"}]}}, "rateLimit": RATE}}
    if "fields(first" in q:
        return {"data": {"organization": {"projectV2": {"fields": {"nodes": FIELDS}}}}, "rateLimit": RATE}
    if "projectItems" in q:
        return {"data": {"repository": {"issue": {"projectItems": {"nodes": [
            {"id": ITEM_ID, "project": {"number": 12}}]}}}}, "rateLimit": RATE}
    if _is_field_mutation(q):
        field = FIELD_NAME.get(variables.get("fieldId", ""), variables.get("fieldId", "?"))
        if "clearProjectV2ItemFieldValue" in q:
            entry = {"field": field, "op": "clear", "text": None}
        else:
            entry = {"field": field, "op": "set", "text": variables.get("text")}
        with LOCK:
            WRITES.append(entry)
        return {"data": {"clearProjectV2ItemFieldValue": {"clientMutationId": None}}
                if entry["op"] == "clear"
                else {"updateProjectV2ItemFieldValue": {"clientMutationId": None}},
                "rateLimit": RATE}
    return None


class H(BaseHTTPRequestHandler):
    # Keep-alive, so the server does not close after every response: HTTP/1.0's close-per-response
    # races the engine's pooling HttpClient and RSTs away a written response (#761). Pairs with
    # ThreadingHTTPServer below — a kept-alive connection would pin a single-threaded server.
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def _send(self, code, payload):
        if code == 204:
            self.send_response(code)
            self.end_headers()
            return
        b = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
        p = self.path.split("?", 1)[0]
        if p.rstrip("/") == "/graphql":
            global GQL_COUNT
            with LOCK:
                GQL_COUNT += 1
            try:
                body = json.loads(raw)
            except json.JSONDecodeError:
                return self._send(500, {"errors": [{"message": "bad body"}]})
            q = body.get("query", "")
            a = graphql(q, body.get("variables", {}) or {})
            return self._send(200, a if a is not None else {"errors": [{"message": f"unhandled {q[:60]}"}]})
        if re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/comments$", p):
            global NEXT_COMMENT_ID
            try:
                body = json.loads(raw).get("body")
            except json.JSONDecodeError:
                body = None
            if not isinstance(body, str):
                return self._send(422, {"message": "body required"})
            with LOCK:
                comment = {
                    "id": NEXT_COMMENT_ID,
                    "body": body,
                    "updated_at": datetime.now(timezone.utc).isoformat(),
                    "html_url": f"https://fixture/comments/{NEXT_COMMENT_ID}",
                }
                NEXT_COMMENT_ID += 1
                COMMENTS.append(comment)
            return self._send(201, comment)
        self._send(500, {"message": f"unhandled POST {p}"})

    def do_GET(self):
        p = self.path.split("?", 1)[0]
        # The GraphQL request count (the corpus's `gcount`) and the recorded writes (the `--text`/`--clear`
        # the corpus greps out of the `gh` log), one transport over.
        if p.rstrip("/") == "/_gqlcount":
            with LOCK:
                return self._send(200, {"count": GQL_COUNT})
        if p.rstrip("/") == "/_writes":
            with LOCK:
                return self._send(200, {"writes": WRITES, "last": WRITES[-1] if WRITES else None})
        if re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/comments$", p):
            with LOCK:
                return self._send(200, list(COMMENTS))
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)$", p)
        if m:
            n = int(m.group(1))
            return self._send(200, {"number": n}) if n == ITEM else self._send(404, {"message": "Not Found"})
        if p.rstrip("/") == "/rate_limit":
            return self._send(200, {"resources": {"graphql": {"remaining": 4960, "limit": 5000}}})
        self._send(500, {"message": f"unhandled GET {p}"})

    def do_DELETE(self):
        p = self.path.split("?", 1)[0]
        match = re.match(r"^/repos/[^/]+/[^/]+/issues/comments/(\d+)$", p)
        if match:
            comment_id = int(match.group(1))
            with LOCK:
                before = len(COMMENTS)
                COMMENTS[:] = [comment for comment in COMMENTS if comment["id"] != comment_id]
            return self._send(204, {}) if len(COMMENTS) < before else self._send(404, {"message": "Not Found"})
        self._send(500, {"message": f"unhandled DELETE {p}"})


def main():
    s = ThreadingHTTPServer(("127.0.0.1", 0), H)
    print(s.server_address[1], flush=True)
    s.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
