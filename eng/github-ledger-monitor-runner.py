#!/usr/bin/env python3
"""Scheduler-independent one-shot capture, monitor, watchdog and alert handoff."""

from __future__ import annotations

import argparse
import datetime as dt
import gzip
import hashlib
import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import time


class Refused(RuntimeError):
    pass


CAPTURE_ATTEMPTS = 3
CAPTURE_RETRY_SECONDS = 2
CAPTURE_FAILURE_LIMIT = 64


def load_config(path):
    source = pathlib.Path(path)
    if source.is_symlink() or not source.is_file() or source.stat().st_mode & 0o077:
        raise Refused("config-must-be-private-regular-nonsymlink")
    value = json.loads(source.read_bytes())
    expected = {"schema", "runnerId", "sourceRoot", "store", "controlIssueNumber", "ordinaryCredentialCommand",
                "cutoverCredentialCommand", "alertCommand", "alertTarget"}
    if set(value) != expected or value["schema"] != "fsgg.github-ledger-external-runner-config/1":
        raise Refused("config-shape")
    if value["controlIssueNumber"] != 2 or not value["runnerId"] or not value["alertTarget"]:
        raise Refused("config-binding")
    for name in ("ordinaryCredentialCommand", "cutoverCredentialCommand", "alertCommand"):
        if not isinstance(value[name], list) or not value[name] or not all(isinstance(item, str) and item for item in value[name]):
            raise Refused("config-command:" + name)
    root, store = pathlib.Path(value["sourceRoot"]), pathlib.Path(value["store"])
    if not root.is_absolute() or not store.is_absolute() or root.is_symlink() or store.is_symlink():
        raise Refused("config-path")
    if not (root / "eng/capture-github-ledger-protection.py").is_file():
        raise Refused("source-root")
    store.mkdir(mode=0o700, parents=True, exist_ok=True)
    if store.stat().st_mode & 0o077:
        raise Refused("store-not-private")
    return value


def credential_fd(command):
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False)
    if completed.returncode != 0 or not completed.stdout:
        raise Refused("credential-command-refused")
    fd = os.memfd_create("fsgg-ledger-key", os.MFD_CLOEXEC)
    os.write(fd, completed.stdout)
    os.lseek(fd, 0, os.SEEK_SET)
    return fd


def invoke_capture(config, output, previous=None):
    ordinary = credential_fd(config["ordinaryCredentialCommand"])
    cutover = credential_fd(config["cutoverCredentialCommand"])
    try:
        command = [sys.executable, str(pathlib.Path(config["sourceRoot"]) / "eng/capture-github-ledger-protection.py"),
                   "--ordinary-app-key-fd", str(ordinary), "--cutover-app-key-fd", str(cutover),
                   "--control-issue-number", "2", "--output", str(output)]
        if previous is not None:
            command += ["--previous", str(previous)]
        completed = subprocess.run(command, pass_fds=(ordinary, cutover), stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, check=False)
        return completed.returncode
    finally:
        os.close(ordinary)
        os.close(cutover)


def retain_capture_failure(store, output):
    source = pathlib.Path(output)
    if not source.is_file() or source.is_symlink():
        return "missing-capture"
    raw = source.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    failures = pathlib.Path(store) / "capture-failures"
    if failures.is_symlink():
        raise Refused("capture-failure-store-symlink")
    failures.mkdir(mode=0o700, exist_ok=True)
    failures.chmod(0o700)
    target = failures / (digest + ".json.gz")
    compressed = gzip.compress(raw, compresslevel=9, mtime=0)
    if target.exists():
        if target.is_symlink():
            raise Refused("capture-failure-content-conflict")
        try:
            existing = gzip.decompress(target.read_bytes())
        except (OSError, EOFError):
            raise Refused("capture-failure-content-conflict")
        if existing != raw:
            raise Refused("capture-failure-content-conflict")
    else:
        with tempfile.NamedTemporaryFile(dir=failures, prefix="capture-failure-", delete=False) as temporary:
            temporary.write(compressed)
            temporary.flush()
            os.fsync(temporary.fileno())
            temporary_path = pathlib.Path(temporary.name)
        temporary_path.chmod(0o600)
        os.replace(temporary_path, target)
    retained = sorted((item for item in failures.glob("*.json.gz") if item.is_file() and not item.is_symlink()),
                      key=lambda item: (item.stat().st_mtime_ns, item.name), reverse=True)
    for expired in retained[CAPTURE_FAILURE_LIMIT:]:
        expired.unlink()
    try:
        gaps = json.loads(raw).get("gaps") or []
        summary = ",".join(str(item) for item in gaps[:8]) or "unspecified"
    except (json.JSONDecodeError, UnicodeDecodeError):
        summary = "unreadable"
    return digest + ":" + summary


def coherent_capture(config, store, private):
    last = "missing-capture"
    for attempt in range(1, CAPTURE_ATTEMPTS + 1):
        first = private / f"pass1-{attempt}.json"
        second = private / f"pass2-{attempt}.json"
        first_status = invoke_capture(config, first)
        if first_status == 0:
            second_status = invoke_capture(config, second, first)
            if second_status == 0:
                return second
            last = retain_capture_failure(store, second)
        else:
            last = retain_capture_failure(store, first)
        if attempt < CAPTURE_ATTEMPTS:
            time.sleep(CAPTURE_RETRY_SECONDS)
    raise Refused(f"capture-refused:attempts={CAPTURE_ATTEMPTS}:last={last}")


def capture_observed_at(path):
    value = json.loads(pathlib.Path(path).read_bytes())
    observed = value.get("capturedAt")
    if not isinstance(observed, str):
        raise Refused("capture-observed-at")
    parsed = dt.datetime.fromisoformat(observed.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise Refused("capture-observed-at")
    return parsed.astimezone(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def monitor_once(config, now):
    store = pathlib.Path(config["store"])
    with tempfile.TemporaryDirectory(prefix="fsgg-ledger-capture-", dir=store) as scratch:
        private = pathlib.Path(scratch)
        private.chmod(0o700)
        second = coherent_capture(config, store, private)
        observed_at = capture_observed_at(second)
        command = [sys.executable, str(pathlib.Path(config["sourceRoot"]) / "eng/monitor-github-ledger-protection.py"),
                   "--store", str(store), "--capture-file", str(second), "--now", observed_at]
        completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False)
    deliver(config, "monitor", observed_at)
    sys.stdout.buffer.write(completed.stdout)
    return completed.returncode


def db(config):
    path = pathlib.Path(config["store"]) / "monitor.sqlite3"
    if path.is_symlink() or not path.is_file():
        raise Refused("monitor-database")
    return sqlite3.connect(path, timeout=5, isolation_level=None)


def deliver(config, cause, observed_at):
    connection = db(config)
    try:
        connection.execute("PRAGMA busy_timeout=5000")
        rows = connection.execute(
            "SELECT o.id,i.kind,i.opened_at FROM outbox o JOIN incidents i ON i.id=o.incident_id WHERE o.delivered_at IS NULL ORDER BY o.created_at,o.id LIMIT 100"
        ).fetchall()
        for identifier, kind, opened in rows:
            payload = json.dumps({"schema": "fsgg.github-ledger-alert/1", "alertId": identifier, "kind": kind,
                                  "openedAt": opened, "observedAt": observed_at, "cause": cause,
                                  "runnerId": config["runnerId"], "target": config["alertTarget"]},
                                 sort_keys=True, separators=(",", ":")).encode()
            completed = subprocess.run(config["alertCommand"], input=payload, stdout=subprocess.DEVNULL,
                                       stderr=subprocess.DEVNULL, check=False)
            connection.execute("BEGIN IMMEDIATE")
            if completed.returncode == 0:
                connection.execute("UPDATE outbox SET attempts=attempts+1,delivered_at=? WHERE id=? AND delivered_at IS NULL", (observed_at, identifier))
            else:
                connection.execute("UPDATE outbox SET attempts=attempts+1 WHERE id=? AND delivered_at IS NULL", (identifier,))
            connection.commit()
    finally:
        connection.close()


def watchdog(config, now_text):
    now = dt.datetime.fromisoformat(now_text.replace("Z", "+00:00"))
    connection = db(config)
    try:
        row = connection.execute("SELECT observed_at FROM heartbeat WHERE id=1").fetchone()
    finally:
        connection.close()
    stale = row is None or now - dt.datetime.fromisoformat(row[0].replace("Z", "+00:00")) > dt.timedelta(minutes=15)
    if stale:
        completed = subprocess.run(config["alertCommand"], input=json.dumps({
            "schema": "fsgg.github-ledger-watchdog-alert/1", "runnerId": config["runnerId"],
            "target": config["alertTarget"], "observedAt": now_text, "finding": "heartbeat-stale",
        }, sort_keys=True, separators=(",", ":")).encode(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        return 3 if completed.returncode == 0 else 4
    deliver(config, "watchdog", now_text)
    print(json.dumps({"schema": "fsgg.github-ledger-watchdog/1", "outcome": "fresh", "observedAt": now_text}, sort_keys=True, separators=(",", ":")))
    return 0


def preview(config):
    print(json.dumps({"schema": "fsgg.github-ledger-external-runner-preview/1", "runnerId": config["runnerId"],
                      "store": config["store"], "alertTarget": config["alertTarget"], "intervalSeconds": 300,
                      "watchdogSeconds": 900, "commands": ["once --config <private-config> --now <UTC>",
                                                             "watchdog --config <private-config> --now <UTC>"],
                      "installed": False, "durable": False}, sort_keys=True, separators=(",", ":")))
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("preview", "once", "watchdog"))
    parser.add_argument("--config", required=True)
    parser.add_argument("--now")
    args = parser.parse_args()
    try:
        config = load_config(args.config)
        if args.mode == "preview":
            return preview(config)
        if not args.now:
            raise Refused("now-required")
        return monitor_once(config, args.now) if args.mode == "once" else watchdog(config, args.now)
    except (Refused, OSError, ValueError, json.JSONDecodeError, sqlite3.Error) as error:
        print("github ledger external runner refused: " + str(error), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
