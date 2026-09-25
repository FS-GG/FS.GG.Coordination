"""Focused refusal and restart controls for the inactive Main-host consume gate."""

import importlib.util
import pathlib
import sqlite3
import tempfile
import threading
import unittest
from unittest import mock


SOURCE = pathlib.Path(__file__).resolve().parents[1] / "eng/github-v1-admission-issuer-once.py"
SPEC = importlib.util.spec_from_file_location("issuer_once", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class IssuerOnceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        directory = pathlib.Path(self.temp.name) / "private"
        directory.mkdir(mode=0o700)
        self.database = directory / "operating-v1-issuer.sqlite3"
        self.plan = "a" * 64
        self.nonce = "b" * 32
        self.token = "signed-jti-one"

    def consume(self, plan=None, nonce=None, token=None, attempt=1):
        MODULE.consume_once(
            self.database, plan or self.plan, nonce or self.nonce,
            token or self.token, 123, attempt, 456, 1_800_000_000,
        )

    def test_restart_refuses_reused_plan_nonce_and_token_independently(self):
        self.consume()
        for plan, nonce, token in (
            (self.plan, "c" * 32, "signed-jti-two"),
            ("c" * 64, self.nonce, "signed-jti-two"),
            ("c" * 64, "c" * 32, self.token),
        ):
            with self.subTest(plan=plan, nonce=nonce, token=token):
                with self.assertRaisesRegex(MODULE.Refused, "issuer-once-replay"):
                    self.consume(plan, nonce, token)
        with sqlite3.connect(self.database) as connection:
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM consumed").fetchone(), (1,))

    def test_concurrent_consumption_has_one_winner(self):
        results = []
        lock = threading.Lock()

        def worker():
            try:
                self.consume()
                result = "consumed"
            except MODULE.Refused:
                result = "refused"
            with lock:
                results.append(result)

        threads = [threading.Thread(target=worker) for _ in range(8)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        self.assertEqual(sorted(results), ["consumed"] + ["refused"] * 7)

    def test_nonprivate_path_bad_generation_and_corrupt_schema_refuse(self):
        self.database.parent.chmod(0o755)
        with self.assertRaisesRegex(MODULE.Refused, "issuer-once-directory"):
            self.consume()
        self.database.parent.chmod(0o700)
        with self.assertRaisesRegex(MODULE.Refused, "issuer-once-job"):
            self.consume(attempt=2)
        with sqlite3.connect(self.database) as connection:
            connection.execute("CREATE TABLE consumed (plan_sha256 TEXT)")
        self.database.chmod(0o600)
        with self.assertRaisesRegex(MODULE.Refused, "issuer-once-schema"):
            self.consume()

    def test_symlink_database_refuses(self):
        other = self.database.parent / "other"
        other.touch(mode=0o600)
        self.database.symlink_to(other)
        with self.assertRaisesRegex(MODULE.Refused, "issuer-once-database"):
            self.consume()

    def test_first_create_directory_sync_failure_refuses_before_consume(self):
        with mock.patch.object(MODULE.os, "fsync", side_effect=OSError("unavailable")):
            with self.assertRaisesRegex(MODULE.Refused, "issuer-once-directory-sync"):
                self.consume()
        with sqlite3.connect(self.database) as connection:
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM sqlite_master WHERE name='consumed'").fetchone(), (0,))


if __name__ == "__main__":
    unittest.main()
