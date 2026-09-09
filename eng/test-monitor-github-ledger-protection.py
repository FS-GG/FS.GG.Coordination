#!/usr/bin/env python3
import importlib.util, json, os, pathlib, tempfile, threading, unittest
ROOT=pathlib.Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("monitor",ROOT/"monitor-github-ledger-protection.py"); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
class MonitorTests(unittest.TestCase):
  def capture(self,path,normalized="a"*64,gaps=None):
    path.write_text(json.dumps({"capturedAt":"2026-09-09T10:00:00Z","continuity":"matched","gaps":gaps or [],"rawSetSha256":"b"*64,"normalizedSetSha256":normalized,"resources":[{"id":"x","pagesComplete":True,"state":"observed"}]}))
  def test_green_then_drift_opens_outbox(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); store=root/"store"; capture=root/"capture.json"; self.capture(capture)
      self.assertEqual(0,m.ingest(store,capture,"2026-09-09T10:01:00Z",900,30,10**7))
      self.capture(capture,"c"*64); self.assertEqual(3,m.ingest(store,capture,"2026-09-09T10:02:00Z",900,30,10**7))
      cx=m.sqlite3.connect(store/"monitor.sqlite3"); self.assertEqual("wal",cx.execute("pragma journal_mode").fetchone()[0]); self.assertEqual(1,cx.execute("select count(*) from outbox").fetchone()[0]); cx.close()
  def test_stale_and_incomplete_are_red(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); capture=root/"capture.json"; self.capture(capture,gaps=["denied"])
      self.assertEqual(3,m.ingest(root/"store",capture,"2026-09-09T10:01:00Z",900,30,10**7))
      self.capture(capture)
      self.assertEqual(3,m.ingest(root/"store",capture,"2026-09-09T10:20:00Z",900,30,10**7))
  def test_public_store_refused(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); store=root/"store"; store.mkdir(mode=0o755); capture=root/"capture.json"; self.capture(capture)
      with self.assertRaisesRegex(ValueError,"store-not-private"): m.ingest(store,capture,"2026-09-09T10:01:00Z",900,30,10**7)
  def test_store_and_database_symlinks_are_refused(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); target=root/"target"; target.mkdir(mode=0o700); capture=root/"capture.json"; self.capture(capture)
      link=root/"store"; link.symlink_to(target, target_is_directory=True)
      with self.assertRaisesRegex(ValueError,"store-symlink"): m.ingest(link,capture,"2026-09-09T10:01:00Z",900,30,10**7)
      link.unlink(); link.mkdir(mode=0o700); (link/"monitor.sqlite3").symlink_to(root/"missing.sqlite3")
      with self.assertRaisesRegex(ValueError,"database-symlink"): m.ingest(link,capture,"2026-09-09T10:01:00Z",900,30,10**7)
  def test_concurrent_writers_serialize_without_losing_runs(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); store=root/"store"; capture=root/"capture.json"; self.capture(capture)
      errors=[]
      def write(second):
        try: m.ingest(store,capture,f"2026-09-09T10:00:{second:02d}Z",900,30,10**7)
        except Exception as error: errors.append(error)
      threads=[threading.Thread(target=write,args=(second,)) for second in range(1,5)]
      [thread.start() for thread in threads]; [thread.join() for thread in threads]
      self.assertEqual([],errors)
      cx=m.sqlite3.connect(store/"monitor.sqlite3"); self.assertEqual(4,cx.execute("select count(*) from runs").fetchone()[0]); cx.close()
  def test_quota_failure_is_persisted_in_run_and_heartbeat(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); store=root/"store"; capture=root/"capture.json"; self.capture(capture)
      self.assertEqual(3,m.ingest(store,capture,"2026-09-09T10:01:00Z",900,30,1))
      cx=m.sqlite3.connect(store/"monitor.sqlite3")
      self.assertEqual(("red","quota"),cx.execute("select outcome,finding from runs").fetchone())
      self.assertEqual("red",cx.execute("select outcome from heartbeat").fetchone()[0]); cx.close()
  def test_corrupt_database_is_a_storage_failure(self):
    with tempfile.TemporaryDirectory() as td:
      root=pathlib.Path(td); store=root/"store"; store.mkdir(mode=0o700); capture=root/"capture.json"; self.capture(capture)
      database=store/"monitor.sqlite3"; database.write_bytes(b"not-a-sqlite-database"); database.chmod(0o600)
      with self.assertRaises(m.sqlite3.DatabaseError): m.ingest(store,capture,"2026-09-09T10:01:00Z",900,30,10**7)
if __name__=="__main__": unittest.main()
