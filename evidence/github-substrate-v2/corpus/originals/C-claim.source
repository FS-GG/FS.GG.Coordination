#!/usr/bin/env python3
"""Case 24's LOCK ADVERSARIAL INTERLEAVINGS — the lock fails closed, served over HTTP for the engine.

Case 24's certified answers include the interleavings in which two workers could end up believing they
hold ONE item — the failure the whole ADR-0027 protocol exists to prevent. This fixture proves the
engine's already-implemented fail-closed behaviour against the corpus's certified answers, over HTTP,
with no bash in the pipeline. Each leg below is the corpus's; the letter is its label in
`tests/fsgg-coord/cases/24-issue-boundary-adversarial.sh`.

The world is ONE repo (FS.GG.SDD). Each issue carries the marker state its leg needs:

    (c) #86  ghost-222 STALE (id 812) + heron-b71 FRESH (id 813)   heartbeat: an expired worker cannot
                                                                    resurrect its claim UNDER A NEW HOLDER
    (d) #87  ghost-333 STALE (id 814)                              heartbeat: an expired lease cannot be
                                                                    renewed in place — re-claim
    (e) #88  a fsgg:msg comment that QUOTES a claim marker in prose (id 820), and NO real marker —
             so the forged marker does not hold the item and #88 is still CLAIMABLE
    (f) #89  a MALFORMED marker, `worker=` absent (id 815, fresh)  claim: fails CLOSED — a half-written
                                                                    lock BLOCKS the item, never vanishes
    (g) #90  EMPTY; the CAS RE-READ FAULTS (502 after the honest marker scan and M4's complete route
             ledger read) — claim withdraws its own just-posted marker (a
             failed re-read is a LOSS, never an orphan)
    (i) #92  EMPTY; the CAS RE-READ returns [] (our marker VANISHED) — claim treats "we cannot tell" as a
             LOSS and does not announce a lock it cannot show
    (a) #84  ghost-111 STALE (id 810)   claim COLLECTS the stale marker it claims over (DELETEs it), TELLS
                                        the evicted worker (a fsgg:msg to=ghost-111), one marker survives
    (b) #85  otter-b55 STALE (id 811)   a worker whose OWN marker went stale renews to ONE marker, not two
    (l) #95  ghost-444 STALE (id 818)   the collect DELETE 404s (a peer collected it first) — not fatal
    (j) #93  WID93 FRESH (id 817, session=SESS93)  a re-claim under a SHARED session id renews the marker in
                                        place (a PATCH, not a second POST) and WARNS ("adopted ITS lock",
                                        "may not be unique to this worker") — the id is not proof of ownership

Legs (a)/(b)/(l) MUTATE this process too: `claim` POSTs its fresh marker, wins the CAS over the stale one,
and then COLLECTS the stale marker (a DELETE, recorded in `/_deletes`). A GET on an issue's /comments
reflects the deletes — so `workers_on` sees exactly the survivor(s). Issue 95's DELETE returns 404 (the
concurrent-GC race: the marker is present for our read and re-verify, gone by the time our DELETE lands),
and the engine treats "already gone" as success. Leg (a)'s notify is an ordinary POST, so it shows up on
#84's /comments as a `fsgg:msg` addressed `to=ghost-111`.

Legs (g)/(i) MUTATE this process: `claim` POSTs its marker, the re-read fails or comes back empty, and the
withdraw DELETEs the marker it posted. `/_deletes` records the comment ids DELETEd, so the harness can
prove the failed CAS removed our own marker rather than orphaning it. `/_patches` records comment PATCHes,
so the harness can prove a refused heartbeat (leg c) patched NOTHING.

Disposed on the record (ADR-0040 §5), where the engine's wording differs from bash's literal strings — the
PROPERTY is asserted, not the spelling:
  (c) engine says `held by heron-b71`, bash `worker 'heron-b71' does`  — both NAME the new holder.
  (d) engine and bash BOTH point at a plain re-claim — no disposition needed (.github#1620 retired it:
      the engine used to say `claim --force`, which was wrong twice over. An expired lease needs no
      force — a plain `claim` COLLECTS the stale marker — and once `--force` became a real steal, the
      line was advertising a live-claim eviction as the remedy for one's own lapsed lease).
  (f) engine says `unparseable lock`, bash `unparsed-marker`           — both BLOCK the item, fail closed.
  (g) engine says `could not take … a LOSS`, bash `removed our marker` / `nothing was claimed` — both
      DELETE the just-posted marker (proven at `/_deletes`) and claim NOTHING (a non-zero exit).
"""

import hashlib
import json
import re
import sys
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

OWNER = "FS-GG"
REPO = "FS.GG.SDD"
RATE = {"cost": 1, "remaining": 4977}

# (j) THE SHARED-ID RE-CLAIM WARNING. A marker bearing our worker id is not proof it is ours — rules 4/5
# (#419) can hand one id to several workers — and the re-claim (heartbeat) path bypasses the CAS entirely,
# so it is exactly where a same-id sibling silently adopts another worker's lock. The worker id here is
# DERIVED from a shared claude-code session id (`Identity.nameFromSeed`, replicated below so the fixture
# seeds a marker carrying the SAME id + session the engine derives), so the re-claim must WARN.
_WORDS = ["finch", "heron", "wren", "swift", "kite", "tern", "rook", "crake", "snipe", "plover",
          "merlin", "godwit", "curlew", "dunlin", "teal", "smew", "osprey", "shrike", "avocet", "brant"]


def name_from_seed(seed):
    head = hashlib.sha256(seed.encode()).hexdigest()[:8]
    return f"{_WORDS[int(head[:2], 16) % len(_WORDS)]}-{head[2:5]}"


SESS93 = "309bd638-8a1c-42b7-952b-898efb8d1064"  # the corpus's shared claude-code session id (case 24 leg j)
WID93 = name_from_seed(SESS93)                    # the worker id BOTH the engine and this fixture derive from it

LOCK = threading.Lock()
_DELETES = []          # comment ids this process was asked to DELETE (the CAS withdraw / stale collect).
_DELETED = set()       # comment ids actually gone now — a GET on /comments reflects them (legs a/b/l).
_DELETE_404 = {818}    # comment ids whose DELETE 404s (a peer collected it first, leg l) — still GONE.
_PATCHES = []          # comment ids this process was asked to PATCH (a heartbeat renew).
_POSTED = {}           # marker comments `claim` POSTs, keyed by issue number (leg e's write-through).
_NEXT_ID = [9000]
_COMMENT_READS = {}    # per-issue count of GETs on its /comments, so legs g/i can fault the RE-READ.


def _now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _ago(hours):
    return (datetime.now(timezone.utc) - timedelta(hours=hours)).strftime("%Y-%m-%dT%H:%M:%SZ")


def _marker(cid, worker, ts, lease=120):
    return {"id": cid, "body": f"<!-- fsgg:claim worker={worker} lease={lease} -->\nheld",
            "user": {"login": "EHotwagner"}, "created_at": ts, "updated_at": ts}


# Static seeded comments, per issue. STALE markers are 3h old (past the 120m lease); FRESH are now.
def _seeded(n):
    if n == 86:  # (c) a stale holder AND a live one — the resurrection scenario
        return [_marker(812, "ghost-222", _ago(3)), _marker(813, "heron-b71", _now())]
    if n == 87:  # (d) one stale holder, nobody else
        return [_marker(814, "ghost-333", _ago(3))]
    if n == 88:  # (e) a MESSAGE quoting a claim marker in prose — NOT a marker (anchored at body start)
        ts = _now()
        return [{"id": 820,
                 "body": ("<!-- fsgg:msg from=wren-c22 to=vole-c88 -->\n**wren-c22 → vole-c88**\n\n"
                          "Careful with <!-- fsgg:claim worker=ghost-666 lease=120 --> in prose."),
                 "user": {"login": "EHotwagner"}, "created_at": ts, "updated_at": ts}]
    if n == 89:  # (f) a malformed marker — worker= absent, so it parses to nobody and fails closed
        ts = _now()
        return [{"id": 815, "body": "<!-- fsgg:claim lease=120 -->\nhalf-written",
                 "user": {"login": "EHotwagner"}, "created_at": ts, "updated_at": ts}]
    if n == 84:  # (a) a STALE claim by ghost-111 — the next claimant COLLECTS it and TELLS ghost-111
        return [_marker(810, "ghost-111", _ago(3))]
    if n == 85:  # (b) our OWN stale marker — a renew ends with one marker, not two
        return [_marker(811, "otter-b55", _ago(3))]
    if n == 95:  # (l) a stale claim whose DELETE 404s (a peer collected it first)
        return [_marker(818, "ghost-444", _ago(3))]
    if n == 93:  # (j) a FRESH marker carrying OUR shared-session id — a re-claim renews it (PATCH), warns
        ts = _now()
        return [{"id": 817,
                 "body": f"<!-- fsgg:claim worker={WID93} lease=120 session={SESS93} -->\nheld",
                 "user": {"login": "EHotwagner"}, "created_at": ts, "updated_at": ts}]
    return []  # #90, #92 start EMPTY


ISSUE_BODY = {
    84: "Paths: src/A/**", 85: "Paths: src/B/**",
    86: "Paths: src/C/**", 87: "Paths: src/D/**", 88: "Paths: src/E/**",
    89: "Paths: src/F/**", 90: "Paths: src/G/**", 92: "Paths: src/I/**",
    93: "Paths: src/J/**", 95: "Paths: src/L/**",
}


def graphql(query):
    # The board bootstrap `claim`'s #516 pre-hold scan needs. The board is EMPTY (no in-flight items),
    # and every claim subject is OFF the board (projectItems empty → readPreviousStatus is None).
    if "projectsV2" in query:
        return {"data": {"organization": {"projectsV2": {"nodes": [
            {"number": 12, "title": "Coordination", "id": "PVT_coord"}]}}, "rateLimit": RATE}}
    if "fields(first" in query:
        return {"data": {"organization": {"projectV2": {"fields": {"nodes": [
            {"id": "PVTSSF_status", "name": "Status", "dataType": "SINGLE_SELECT",
             "options": [{"id": "opt_ready", "name": "Ready"}, {"id": "opt_wip", "name": "In progress"}]},
            {"id": "PVTF_blocked", "name": "Blocked by", "dataType": "TEXT"}]}}}, "rateLimit": RATE}}
    if "projectItems" in query:
        return {"data": {"repository": {"issue": {"projectItems": {"nodes": []}}}}, "rateLimit": RATE}
    if "items(first" in query:
        return {"data": {"organization": {"projectV2": {"items": {
            "pageInfo": {"hasNextPage": False, "endCursor": None}, "nodes": []}}}, "rateLimit": RATE}}
    return None


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
        if code < 400:
            self.send_header("X-RateLimit-Resource", "core")
            self.send_header("X-RateLimit-Limit", "5000")
            self.send_header("X-RateLimit-Remaining", "4800")
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
        p = self.path.split("?", 1)[0]
        if p.rstrip("/") == "/graphql":
            try:
                q = json.loads(raw).get("query", "")
            except json.JSONDecodeError:
                return self._send(500, {"errors": [{"message": "bad body"}]})
            a = graphql(q)
            return self._send(200, a if a is not None else {"errors": [{"message": f"unhandled {q[:60]}"}]})

        # `claim` POSTs its marker here. Store it (leg e reads it back on the WINNING re-read); #92's marker
        # is stored too but its re-read returns [] regardless — the "vanished" model.
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/comments$", p)
        if m:
            n = int(m.group(1))
            try:
                body = json.loads(raw).get("body", "")
            except json.JSONDecodeError:
                return self._send(400, {"message": "bad body"})
            with LOCK:
                cid = _NEXT_ID[0]
                _NEXT_ID[0] += 1
                ts = _now()
                _POSTED.setdefault(n, []).append(
                    {"id": cid, "body": body, "user": {"login": "EHotwagner"},
                     "created_at": ts, "updated_at": ts})
            return self._send(201, {"id": cid})

        self._send(500, {"message": f"unhandled POST {p}"})

    def do_DELETE(self):
        p = self.path.split("?", 1)[0]
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/comments/(\d+)$", p)
        if m:
            cid = int(m.group(1))
            with LOCK:
                _DELETES.append(cid)
                # The marker is GONE regardless of what we answer — for leg l, a peer's delete landed
                # first, so it is already absent even though our own DELETE 404s. Either way a subsequent
                # read must not serve it back.
                _DELETED.add(cid)
                is404 = cid in _DELETE_404
            if is404:
                return self._send(404, {"message": "Not Found"})  # (l) already gone — not fatal
            self.send_response(204)
            self.end_headers()
            return
        self._send(500, {"message": f"unhandled DELETE {p}"})

    def do_PATCH(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        p = self.path.split("?", 1)[0]
        m = re.match(r"^/repos/[^/]+/[^/]+/issues/comments/(\d+)$", p)
        if m:
            with LOCK:
                _PATCHES.append(int(m.group(1)))
            return self._send(200, {"id": int(m.group(1))})
        self._send(500, {"message": f"unhandled PATCH {p}"})

    def do_GET(self):
        p = self.path.split("?", 1)[0]

        if p == "/_deletes":
            with LOCK:
                return self._send(200, list(_DELETES))
        if p == "/_patches":
            with LOCK:
                return self._send(200, list(_PATCHES))

        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)/comments$", p)
        if m:
            n = int(m.group(1))
            with LOCK:
                seen = _COMMENT_READS.get(n, 0)
                _COMMENT_READS[n] = seen + 1
                # (g) #90: the claim gets an honest marker scan and an honest complete decision-ledger
                # read. The third read is the CAS re-read and faults, so `claim` withdraws the marker it
                # posted rather than orphaning it.
                if n == 90 and seen >= 2:
                    return self._send(502, {"message": "upstream read failed (leg g)"})
                # (i) #92: the marker VANISHES — the re-read never serves what was POSTed, so `winner` is
                # None and "we cannot tell who holds this" is treated as a LOSS.
                if n == 92:
                    return self._send(200, [])
                # A read reflects the DELETEs (legs a/b/l): a collected stale marker is gone, so
                # `workers_on` sees exactly the survivor.
                live = [c for c in _seeded(n) + _POSTED.get(n, []) if c["id"] not in _DELETED]
                return self._send(200, live)

        if re.match(r"^/repos/[^/]+/[^/]+/issues/?$", p):
            return self._send(200, [{"number": n, "title": ISSUE_BODY[n].split(":")[0], "state": "open",
                                     "body": ISSUE_BODY[n]} for n in sorted(ISSUE_BODY)])

        m = re.match(r"^/repos/[^/]+/[^/]+/issues/(\d+)$", p)
        if m:
            n = int(m.group(1))
            if n in ISSUE_BODY:
                return self._send(200, {"number": n, "body": ISSUE_BODY[n]})
            return self._send(404, {"message": "Not Found"})

        if re.match(r"^/repos/[^/]+/[^/]+/pulls/?$", p):
            return self._send(200, [])

        if p.rstrip("/") == "/rate_limit":
            return self._send(200, {"resources": {"graphql": {"remaining": 4980, "limit": 5000}}})

        self._send(500, {"message": f"unhandled GET {p}"})


def main():
    s = ThreadingHTTPServer(("127.0.0.1", 0), H)
    print(s.server_address[1], flush=True)
    s.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
