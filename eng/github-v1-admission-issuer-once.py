#!/usr/bin/env python3
"""Import-only durable one-shot gate for a future Main-host v1 issuer.

The future fixed issuer must verify signed job OIDC, native job provenance, and
the exact sealed public plan before calling consume_once. A successful consume
is not permission to mint by itself. This module has no key access, token
output, provider call, network listener, reset, or deletion path.
"""

from __future__ import annotations

import os
import pathlib
import re
import sqlite3
import stat

HEX64 = re.compile(r"[0-9a-f]{64}\Z")
HEX32 = re.compile(r"[0-9a-f]{32}\Z")
MAX_RECORDS = 100_000
TABLE_SQL = (
    "CREATE TABLE consumed ("
    "plan_sha256 TEXT PRIMARY KEY, nonce TEXT NOT NULL UNIQUE,"
    "token_id TEXT NOT NULL UNIQUE, run_id INTEGER NOT NULL,"
    "run_attempt INTEGER NOT NULL, check_run_id INTEGER NOT NULL,"
    "consumed_at INTEGER NOT NULL)"
)


class Refused(RuntimeError):
    pass


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def _private_directory(directory: pathlib.Path) -> None:
    require(directory.is_absolute() and not directory.is_symlink(),
            "issuer-once-directory")
    try:
        info = directory.stat(follow_symlinks=False)
    except OSError as error:
        raise Refused("issuer-once-directory") from error
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid()
            and stat.S_IMODE(info.st_mode) == 0o700,
            "issuer-once-directory")


def _private_database(path: pathlib.Path) -> None:
    try:
        info = path.stat(follow_symlinks=False)
    except OSError as error:
        raise Refused("issuer-once-database") from error
    require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
            and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1,
            "issuer-once-database")


def _ensure_database(path: pathlib.Path) -> None:
    _private_directory(path.parent)
    if not path.exists() and not path.is_symlink():
        try:
            descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        except FileExistsError:
            pass
        except OSError as error:
            raise Refused("issuer-once-database-create") from error
        else:
            os.close(descriptor)
            # The first committed consume must survive loss of the directory
            # entry as well as loss of the database contents.
            try:
                directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
            except OSError as error:
                raise Refused("issuer-once-directory-sync") from error
    _private_database(path)


def consume_once(
    database: pathlib.Path,
    plan_sha256: str,
    nonce: str,
    token_id: str,
    run_id: int,
    run_attempt: int,
    check_run_id: int,
    now: int,
) -> None:
    """Atomically and durably consume plan, nonce and OIDC jti before mint.

    The database path comes from Main's protected configuration, never a
    request. Any error, including busy/corrupt/full state, refuses. A consumed
    plan is never made eligible again, even if a later mint or CAS response is
    lost. Recovery must inspect the durable operation journal instead.
    """
    require(isinstance(database, pathlib.Path)
            and database.is_absolute() and database.name == "operating-v1-issuer.sqlite3",
            "issuer-once-path")
    require(isinstance(plan_sha256, str) and HEX64.fullmatch(plan_sha256) is not None
            and isinstance(nonce, str) and HEX32.fullmatch(nonce) is not None,
            "issuer-once-plan")
    require(isinstance(token_id, str) and 0 < len(token_id) <= 128
            and all(32 <= ord(char) < 127 for char in token_id),
            "issuer-once-token-id")
    require(all(type(value) is int and value > 0
                for value in (run_id, run_attempt, check_run_id, now))
            and run_attempt == 1, "issuer-once-job")
    _ensure_database(database)
    connection = None
    try:
        connection = sqlite3.connect(str(database), timeout=5, isolation_level=None)
        connection.execute("PRAGMA journal_mode=DELETE")
        connection.execute("PRAGMA synchronous=FULL")
        connection.execute("PRAGMA trusted_schema=OFF")
        connection.execute("BEGIN IMMEDIATE")
        connection.execute(TABLE_SQL.replace("CREATE TABLE consumed", "CREATE TABLE IF NOT EXISTS consumed", 1))
        schema = connection.execute(
            "SELECT type, sql FROM sqlite_master WHERE name = 'consumed'"
        ).fetchone()
        require(schema == ("table", TABLE_SQL), "issuer-once-schema")
        require(connection.execute("PRAGMA quick_check(1)").fetchone() == ("ok",),
                "issuer-once-integrity")
        count = connection.execute("SELECT COUNT(*) FROM consumed").fetchone()[0]
        require(type(count) is int and count < MAX_RECORDS, "issuer-once-capacity")
        connection.execute(
            "INSERT INTO consumed "
            "(plan_sha256,nonce,token_id,run_id,run_attempt,check_run_id,consumed_at) "
            "VALUES (?,?,?,?,?,?,?)",
            (plan_sha256, nonce, token_id, run_id, run_attempt, check_run_id, now),
        )
        connection.execute("COMMIT")
    except sqlite3.IntegrityError as error:
        if connection is not None and connection.in_transaction:
            connection.execute("ROLLBACK")
        raise Refused("issuer-once-replay") from error
    except Refused:
        if connection is not None and connection.in_transaction:
            connection.execute("ROLLBACK")
        raise
    except (sqlite3.Error, OSError) as error:
        if connection is not None and connection.in_transaction:
            try:
                connection.execute("ROLLBACK")
            except sqlite3.Error:
                pass
        raise Refused("issuer-once-unavailable") from error
    finally:
        if connection is not None:
            connection.close()
