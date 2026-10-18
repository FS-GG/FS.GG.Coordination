#!/usr/bin/env python3
"""Storage preflight controls: no engine, network or storage allocation effects."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("image_storage", ROOT / "eng/portable-workspace-image.py")
IMAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMAGE)


class StoragePreflight(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.source = self.base / "source"
        self.state = self.base / "state"
        self.args = argparse.Namespace(root=str(self.base / "graphroot"),
                                       runroot=str(self.base / "runroot"), state_dir=str(self.state),
                                       podman="/inert/podman")
        self.engine = patch.object(IMAGE, "run", side_effect=AssertionError("engine execution forbidden"))
        self.engine.start()
        self.addCleanup(self.engine.stop)

    def validate(self):
        IMAGE.require_storage_paths(self.args, self.source, self.state)

    def test_disjoint_paths_need_no_existing_allocation(self):
        self.validate()
        self.assertEqual(list(self.base.iterdir()), [])

    def test_each_path_refuses_checkout_and_ancestor_overlap(self):
        for field in ("root", "runroot", "state_dir"):
            original = getattr(self.args, field)
            for path in (self.source, self.source / "store", self.base):
                with self.subTest(field=field, path=path):
                    setattr(self.args, field, str(path))
                    with self.assertRaisesRegex(RuntimeError, "overlaps the source checkout"):
                        self.validate()
            setattr(self.args, field, original)

    def test_writable_paths_cannot_contain_each_other(self):
        for left, right in (("root", "runroot"), ("root", "state_dir"), ("runroot", "state_dir")):
            original = getattr(self.args, left)
            for suffix in ("", "/child"):
                with self.subTest(left=left, right=right, suffix=suffix):
                    setattr(self.args, left, getattr(self.args, right) + suffix)
                    with self.assertRaisesRegex(RuntimeError, "overlaps"):
                        self.validate()
            setattr(self.args, left, original)

    def test_global_stores_and_configured_data_home_are_refused(self):
        data_home = self.base / "custom-data"
        stores = (Path.home() / ".local/share/containers", Path("/var/lib/containers/storage"),
                  Path(f"/run/user/{os.getuid()}/containers"), Path("/run/containers/storage"),
                  data_home / "containers")
        with patch.dict(os.environ, {"XDG_DATA_HOME": str(data_home)}):
            for store in stores:
                for selected in (store, store / "a-run"):
                    with self.subTest(selected=selected):
                        self.args.root = str(selected)
                        with self.assertRaisesRegex(RuntimeError, "global container store"):
                            self.validate()

    def test_symlink_ancestor_and_leaf_are_refused(self):
        target = self.base / "target"
        target.mkdir()
        alias = self.base / "alias"
        alias.symlink_to(target, target_is_directory=True)
        for selected in (alias, alias / "graphroot"):
            self.args.root = str(selected)
            with self.assertRaisesRegex(RuntimeError, "symlink component"):
                self.validate()

    def test_known_global_scope_refuses_before_inaccessible_path_observation(self):
        for selected in ('/var/lib/containers/storage', '/var/lib/containers/storage/a-run',
                         '/run/containers/storage', '/run/containers/storage/a-run'):
            with self.subTest(selected=selected):
                self.args.root = selected
                with patch.object(Path, 'is_symlink', side_effect=PermissionError('declared inaccessible global store')) as observed:
                    with self.assertRaisesRegex(RuntimeError, 'global container store'):
                        self.validate()
                    observed.assert_not_called()

    def test_parent_traversal_is_refused_before_normalization(self):
        self.args.root = str(self.base / "alias/../graphroot")
        with self.assertRaisesRegex(RuntimeError, "parent traversal"):
            self.validate()

    def test_effective_paths_and_driver_must_match_exactly(self):
        expected = {"graphRoot": self.args.root, "runRoot": self.args.runroot, "graphDriverName": "vfs"}
        IMAGE.require_effective_storage({"store": expected}, self.args)
        for field in expected:
            for altered in (None, "", "overlay", str(self.base / "another-run")):
                with self.subTest(field=field, altered=altered):
                    with self.assertRaisesRegex(RuntimeError, field):
                        IMAGE.require_effective_storage({"store": {**expected, field: altered}}, self.args)
        for info in ({}, {"store": None}, {"store": []}):
            with self.assertRaisesRegex(RuntimeError, "observation is missing"):
                IMAGE.require_effective_storage(info, self.args)

    def test_preflight_rejects_before_source_or_engine_commands(self):
        self.args.root = str(self.source / "graphroot")
        with self.assertRaisesRegex(RuntimeError, "overlaps the source checkout"):
            IMAGE.preflight(self.args, self.source, self.state)

    def test_effective_mismatch_blocks_remaining_preflight_and_receipt(self):
        self.args.expected_source_revision = "inert"
        observed = {"store": {"graphRoot": self.args.root, "runRoot": self.args.runroot,
                              "graphDriverName": "overlay"}}
        result = argparse.Namespace(stdout=json.dumps(observed))
        with patch.object(IMAGE, "require_exact_source", return_value=("inert", "tree", "fixture")), \
             patch.object(IMAGE, "run", return_value=result) as engine:
            with self.assertRaisesRegex(RuntimeError, "graphDriverName"):
                IMAGE.preflight(self.args, ROOT, self.state)
        self.assertEqual(engine.call_count, 1)
        self.assertEqual(engine.call_args.args[0][-2:], ["info", "--format=json"])
        self.assertFalse(self.state.exists())

    def test_main_refuses_before_state_directory_creation(self):
        argv = ["image", "preflight", "--source", str(self.source), "--expected-source-revision", "inert",
                "--state-dir", str(self.source / "state"), "--root", self.args.root,
                "--runroot", self.args.runroot]
        with patch.object(IMAGE.sys, "argv", argv):
            with self.assertRaisesRegex(RuntimeError, "overlaps the source checkout"):
                IMAGE.main()
        self.assertFalse(self.source.exists())

    def test_receipt_binds_effective_storage_and_refuses_changed_store_reuse(self):
        self.args.expected_source_revision = "inert"
        self.state.mkdir()

        def observed(argv):
            if argv[-2:] == ["info", "--format=json"]:
                value = {"store": {"graphRoot": self.args.root, "runRoot": self.args.runroot,
                                   "graphDriverName": "vfs"},
                         "host": {"os": "linux", "arch": "amd64", "security": {"rootless": True},
                                  "idMappings": {"uidmap": [{"container_id": 0, "size": 65536}]}}}
                return argparse.Namespace(stdout=json.dumps(value))
            if argv[-1] == "--help":
                return argparse.Namespace(stdout="\n".join(IMAGE.REQUIRED_BUILD_OPTIONS + IMAGE.REQUIRED_CREATE_OPTIONS))
            return argparse.Namespace(stdout="inert-version")

        with patch.object(IMAGE, "require_exact_source", return_value=("inert", "tree", "fixture")), \
             patch.object(IMAGE, "run", side_effect=observed):
            receipt = IMAGE.preflight(self.args, ROOT, self.state)
            original = receipt.read_bytes()
            self.assertEqual(json.loads(original)["storage"],
                             {"graphRoot": self.args.root, "runRoot": self.args.runroot, "graphDriverName": "vfs"})
            self.assertEqual(IMAGE.preflight(self.args, ROOT, self.state).read_bytes(), original)
            self.args.root = str(self.base / "successor-store")
            with self.assertRaisesRegex(RuntimeError, "receipt conflicts"):
                IMAGE.preflight(self.args, ROOT, self.state)
            self.assertEqual(receipt.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
