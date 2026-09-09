#!/usr/bin/env python3
"""One-shot, fail-closed GS2-08.2 observer. It never calls GitHub or repairs state."""
import argparse, gzip, hashlib, json, os, pathlib, sqlite3, stat, sys, time, uuid
from datetime import datetime, timezone, timedelta

SCHEMA = "fsgg.github-ledger-protection-monitor/1"
DB_VERSION = 1

def digest(data): return hashlib.sha256(data).hexdigest()
def utc(value): return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)
def private_dir(path):
    path = pathlib.Path(path)
    if path.is_symlink(): raise ValueError("store-symlink")
    path = path.resolve(strict=False)
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    mode = stat.S_IMODE(path.stat().st_mode)
    if mode & 0o077: raise ValueError("store-not-private")
    return path
def connect(root):
    db = root / "monitor.sqlite3"
    if db.is_symlink(): raise ValueError("database-symlink")
    for attempt in range(100):
        cx = sqlite3.connect(db, timeout=30, isolation_level=None)
        try:
            cx.execute("pragma busy_timeout=30000")
            cx.execute("pragma journal_mode=WAL")
            cx.execute("pragma synchronous=FULL")
            cx.execute("pragma foreign_keys=ON")
            cx.executescript("""
            create table if not exists schema_info(version integer not null);
            insert into schema_info select 1 where not exists(select 1 from schema_info);
            create table if not exists runs(id text primary key, started_at text not null, finished_at text, outcome text not null, payload_sha256 text, finding text);
            create table if not exists observations(payload_sha256 text primary key, captured_at text not null, raw_set_sha256 text not null, normalized_set_sha256 text not null, continuity text not null, path text not null);
            create table if not exists checkpoints(id integer primary key check(id=1), last_success_at text, last_payload_sha256 text, last_normalized_sha256 text);
            create table if not exists incidents(id text primary key, opened_at text not null, kind text not null, payload_sha256 text, detail text not null, resolved_at text);
            create table if not exists outbox(id text primary key, created_at text not null, incident_id text not null references incidents(id), delivered_at text, attempts integer not null default 0);
            create table if not exists heartbeat(id integer primary key check(id=1), observed_at text not null, outcome text not null, run_id text not null);
            """)
            if cx.execute("select version from schema_info").fetchone() != (DB_VERSION,): raise ValueError("unsupported-migration")
            os.chmod(db, 0o600)
            return cx
        except sqlite3.OperationalError as error:
            cx.close()
            if "locked" not in str(error).lower() or attempt == 99: raise
            time.sleep(0.01 * (attempt + 1))
def incident(cx, now, kind, payload, detail):
    key=digest(f"{kind}\0{payload or ''}\0{detail}".encode())
    cx.execute("insert or ignore into incidents values(?,?,?,?,?,null)",(key,now,kind,payload,detail))
    cx.execute("insert or ignore into outbox(id,created_at,incident_id) values(?,?,?)",(digest(("outbox\0"+key).encode()),now,key))
def ingest(store, capture_path, now, max_age, retention_days, quota):
    root=private_dir(store); raw=pathlib.Path(capture_path).read_bytes(); payload=digest(raw); run=digest((now+payload).encode())
    cx=connect(root); compressed=gzip.compress(raw, mtime=0); payload_dir=root/"payloads"/payload[:2]; payload_dir.mkdir(parents=True,exist_ok=True,mode=0o700)
    target=payload_dir/(payload+".json.gz")
    if target.exists() and target.is_symlink(): raise ValueError("payload-symlink")
    if not target.exists():
        tmp=target.with_name(target.name+f".{os.getpid()}.{uuid.uuid4().hex}.tmp")
        try: tmp.write_bytes(compressed); os.chmod(tmp,0o600); os.replace(tmp,target)
        finally: tmp.unlink(missing_ok=True)
    outcome="red"; finding="unknown"
    try:
        value=json.loads(raw); captured=utc(value["capturedAt"]); gaps=value.get("gaps",[])
        continuity=value.get("continuity"); raw_set=value["rawSetSha256"]; normalized=value["normalizedSetSha256"]
        resources=value["resources"]
        bad=[r.get("id","?") for r in resources if not r.get("pagesComplete") or r.get("state") not in ("observed","proven-absent")]
        age=utc(now)-captured
        if gaps: finding="incomplete:"+",".join(gaps)
        elif bad: finding="unknown:"+",".join(bad)
        elif age<timedelta(0) or age>timedelta(seconds=max_age): finding="stale"
        elif continuity!="matched": finding="continuity"
        else:
            prior=cx.execute("select last_normalized_sha256 from checkpoints where id=1").fetchone()
            if prior and prior[0] and prior[0]!=normalized: finding="drift"
            else: outcome="green"; finding="ok"
        cx.execute("begin immediate")
        cx.execute("insert or replace into runs values(?,?,?,?,?,?)",(run,now,now,outcome,payload,finding))
        cx.execute("insert or ignore into observations values(?,?,?,?,?,?)",(payload,value["capturedAt"],raw_set,normalized,continuity,str(target)))
        if outcome=="green": cx.execute("insert into checkpoints values(1,?,?,?) on conflict(id) do update set last_success_at=excluded.last_success_at,last_payload_sha256=excluded.last_payload_sha256,last_normalized_sha256=excluded.last_normalized_sha256",(now,payload,normalized))
        else: incident(cx,now,finding.split(':')[0],payload,finding)
        cx.execute("insert into heartbeat values(1,?,?,?) on conflict(id) do update set observed_at=excluded.observed_at,outcome=excluded.outcome,run_id=excluded.run_id",(now,outcome,run)); cx.execute("commit")
    except Exception as error:
        cx.execute("rollback") if cx.in_transaction else None
        cx.execute("begin immediate"); cx.execute("insert or replace into runs values(?,?,?,?,?,?)",(run,now,now,"red",payload,"unreadable:"+type(error).__name__)); incident(cx,now,"unreadable",payload,type(error).__name__); cx.execute("insert into heartbeat values(1,?,?,?) on conflict(id) do update set observed_at=excluded.observed_at,outcome=excluded.outcome,run_id=excluded.run_id",(now,"red",run)); cx.execute("commit")
        outcome="red"; finding="unreadable:"+type(error).__name__
    cutoff=(utc(now)-timedelta(days=retention_days)).isoformat()
    referenced={r[0] for r in cx.execute("select payload_sha256 from observations where captured_at>=? union select payload_sha256 from incidents where resolved_at is null",(cutoff,)) if r[0]}
    for file in (root/"payloads").glob("*/*.json.gz"):
        if file.stem.split('.')[0] not in referenced: file.unlink()
    size=sum(p.stat().st_size for p in root.rglob('*') if p.is_file())
    if size>quota:
        outcome="red"; finding="quota"; cx.execute("begin immediate"); incident(cx,now,"quota",payload,str(size)); cx.execute("update runs set outcome='red',finding='quota' where id=?",(run,)); cx.execute("update heartbeat set outcome='red' where id=1 and run_id=?",(run,)); cx.execute("commit")
        size=sum(p.stat().st_size for p in root.rglob('*') if p.is_file())
    pending=cx.execute("select count(*) from outbox where delivered_at is null").fetchone()[0]; cx.close()
    print(json.dumps({"schema":SCHEMA,"runId":run,"outcome":outcome,"finding":finding,"payloadSha256":payload,"pendingAlerts":pending,"storeBytes":size},sort_keys=True,separators=(',',':')))
    return 0 if outcome=="green" else 3
def main():
    p=argparse.ArgumentParser(); p.add_argument('--store',required=True); p.add_argument('--capture-file',required=True); p.add_argument('--now',required=True); p.add_argument('--max-age-seconds',type=int,default=900); p.add_argument('--retention-days',type=int,default=30); p.add_argument('--quota-bytes',type=int,default=1073741824)
    a=p.parse_args()
    try: return ingest(a.store,a.capture_file,a.now,a.max_age_seconds,a.retention_days,a.quota_bytes)
    except Exception as error: print(json.dumps({"schema":SCHEMA,"outcome":"red","finding":str(error)},sort_keys=True),file=sys.stderr); return 4
if __name__=='__main__': raise SystemExit(main())
