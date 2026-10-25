#!/usr/bin/env python3
"""Source-only retirement controls; no CLR, native image, capsule or credential effects."""
import importlib.util
import os
from pathlib import Path
import stat
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("fourd_native_cleanup_io", ROOT / "eng/fourd-public-provider/native_io.py")
io = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = io
spec.loader.exec_module(io)


class Clock:
    value = 10.0

    def __call__(self):
        return self.value


class NativeDirectoryRetirementTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="native-retire-")
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "owned"
        self.root.mkdir(mode=0o700)
        child = self.root / "nested"
        child.mkdir(mode=0o700)
        (child / "data").write_text("owned plaintext fixture")
        (child / "data").chmod(0o400)
        child.chmod(0o500)
        self.root.chmod(0o500)
        self.holder = io.OwnedDirectory(self.root)
        self.addCleanup(self.holder.close)

    def test_frozen_private_tree_is_retired_via_held_descriptors(self):
        sentinel = self.base / "outside"
        sentinel.write_text("preserve outside")
        self.root.chmod(0o700)
        (self.root / "link").symlink_to(sentinel)
        self.root.chmod(0o500)
        captured = []

        def record(holder):
            captured.append(holder.path)
            self.assertNotEqual(holder.path, self.root)
            self.assertEqual(stat.S_IMODE(holder.path.stat().st_mode), 0o500)
            self.assertEqual((holder.path.stat().st_dev, holder.path.stat().st_ino), holder.original)
            self.assertEqual((holder.path / "nested/data").read_text(), "owned plaintext fixture")

        self.holder.retire(deadline=time.monotonic() + 5, on_captured=record)
        self.assertEqual(len(captured), 1)
        self.assertFalse(captured[0].exists())
        self.assertFalse(list(self.base.glob(".fsgg-retire-*")))
        self.assertFalse(self.root.exists())
        self.assertEqual(sentinel.read_text(), "preserve outside")

    def test_private_direct_tmp_child_retires_under_sticky_parent(self):
        temporary = tempfile.TemporaryDirectory(prefix="native-retire-direct-", dir="/tmp")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / "data").write_text("private direct child")
        holder = io.OwnedDirectory(root)
        self.addCleanup(holder.close)
        holder.retire(deadline=time.monotonic() + 5)
        self.assertFalse(root.exists())
        self.assertFalse(holder.path.exists())

    def test_nonsticky_writable_parent_stays_refused(self):
        self.base.chmod(0o777)
        with self.assertRaisesRegex(io.NativeIOError, "parent-boundary-refused"):
            self.holder.retire(deadline=time.monotonic() + 5)
        self.assertEqual((self.root / "nested/data").read_text(), "owned plaintext fixture")

    def test_target_must_already_be_private_before_capture(self):
        self.root.chmod(0o755)
        with self.assertRaisesRegex(io.NativeIOError, "private-capture-refused"):
            self.holder.retire(deadline=time.monotonic() + 5)
        self.assertEqual((self.root / "nested/data").read_text(), "owned plaintext fixture")

    def replace_after_check(self):
        original = self.holder.revalidate
        displaced = self.base / "displaced-owned"

        def successful_check_then_replace():
            original()
            self.root.rename(displaced)
            self.root.mkdir(mode=0o700)
            (self.root / "sentinel").write_text("foreign replacement")

        return displaced, successful_check_then_replace

    def test_replacement_after_successful_check_preserves_both_trees(self):
        displaced, replacement = self.replace_after_check()
        callback = mock.Mock()
        with mock.patch.object(self.holder, "revalidate", replacement), mock.patch.object(os, "fchmod", wraps=os.fchmod) as chmod:
            with self.assertRaisesRegex(io.NativeIOError, "capture-foreign"):
                self.holder.retire(deadline=time.monotonic() + 5, on_captured=callback)
        callback.assert_not_called()
        chmod.assert_not_called()
        self.assertEqual((self.root / "sentinel").read_text(), "foreign replacement")
        self.assertEqual((displaced / "nested/data").read_text(), "owned plaintext fixture")
        self.assertEqual(stat.S_IMODE(displaced.stat().st_mode), 0o500)

    def test_restore_collision_preserves_captured_and_new_foreign_roots(self):
        displaced, replacement = self.replace_after_check()
        rename = self.holder._rename_noreplace
        calls = []

        def interfere(source_fd, source, target_fd, target):
            calls.append((source, target))
            if len(calls) == 2:
                self.root.mkdir(mode=0o700)
                (self.root / "other").write_text("later replacement")
            return rename(source_fd, source, target_fd, target)

        with mock.patch.object(self.holder, "revalidate", replacement), mock.patch.object(self.holder, "_rename_noreplace", interfere):
            with self.assertRaisesRegex(io.NativeIOError, "foreign-restore-unknown"):
                self.holder.retire(deadline=time.monotonic() + 5)
        self.assertEqual((self.root / "other").read_text(), "later replacement")
        self.assertEqual((self.holder.retirement_quarantine / "sentinel").read_text(), "foreign replacement")
        self.assertEqual((displaced / "nested/data").read_text(), "owned plaintext fixture")

    def test_callback_failure_preserves_frozen_captured_location(self):
        def fail(_):
            raise RuntimeError("record unavailable")

        with self.assertRaisesRegex(io.NativeIOError, "relocation-record-unknown"):
            self.holder.retire(deadline=time.monotonic() + 5, on_captured=fail)
        self.assertEqual((self.holder.path / "nested/data").read_text(), "owned plaintext fixture")
        self.assertEqual(stat.S_IMODE(self.holder.path.stat().st_mode), 0o500)
        self.assertFalse(self.root.exists())

    def test_expired_original_deadline_changes_nothing(self):
        clock = Clock()
        with self.assertRaisesRegex(io.NativeIOError, "retirement-deadline"):
            self.holder.retire(deadline=clock.value, clock=clock)
        self.assertEqual((self.root / "nested/data").read_text(), "owned plaintext fixture")
        self.assertEqual(list(self.base.iterdir()), [self.root])

    def test_deadline_after_capture_preserves_recorded_frozen_tree(self):
        clock = Clock()
        recorded = []

        def expire(holder):
            recorded.append(holder.path)
            clock.value = 20.0

        with self.assertRaisesRegex(io.NativeIOError, "retirement-deadline"):
            self.holder.retire(deadline=20.0, clock=clock, on_captured=expire)
        self.assertEqual(recorded, [self.holder.path])
        self.assertEqual(stat.S_IMODE(self.holder.path.stat().st_mode), 0o500)
        self.assertEqual((self.holder.path / "nested/data").read_text(), "owned plaintext fixture")

    def test_deadline_between_entry_observation_and_unlink_preserves_data(self):
        clock = Clock()
        original_stat = os.stat

        def expire(path, *args, **kwargs):
            result = original_stat(path, *args, **kwargs)
            if path == "data":
                clock.value = 20.0
            return result

        with mock.patch.object(os, "stat", expire):
            with self.assertRaisesRegex(io.NativeIOError, "retirement-deadline"):
                self.holder.retire(deadline=20.0, clock=clock)
        self.assertEqual((self.holder.path / "nested/data").read_text(), "owned plaintext fixture")

    def test_old_path_replacement_during_retirement_is_preserved_and_unknown(self):
        def replace(_):
            self.root.mkdir(mode=0o700)
            (self.root / "sentinel").write_text("created after capture")

        with self.assertRaisesRegex(io.NativeIOError, "original-replaced"):
            self.holder.retire(deadline=time.monotonic() + 5, on_captured=replace)
        self.assertEqual((self.root / "sentinel").read_text(), "created after capture")
        self.assertFalse(self.holder.path.exists())


if __name__ == "__main__":
    unittest.main()
