#!/usr/bin/env python3
"""Exercise the closed receiver/runtime inventory roles used by P4 facts."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest import mock


MODULE_PATH = Path(__file__).with_name("provider_facts.py")
SPEC = importlib.util.spec_from_file_location("provider_facts_runtime_bounds", MODULE_PATH)
assert SPEC and SPEC.loader
FACTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FACTS)

MIB = 1024 * 1024
OFFICIAL_SHA256 = "8458f4cef855fcebd139d9853e47fb0a5d86ab65d4aa101ea158a11e036c0fa4"


def sparse_tree(root: Path, sizes: list[int]) -> None:
    root.mkdir(parents=True, exist_ok=True)
    for index, size in enumerate(sizes):
        with (root / f"payload-{index:04d}.bin").open("wb") as stream:
            stream.truncate(size)


class RuntimeRoleBounds(unittest.TestCase):
    def test_receiver_keeps_sixty_four_mib_bound(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-receiver-bound-") as temporary:
            root = Path(temporary)
            sparse_tree(root, [16 * MIB] * 4 + [1])
            with self.assertRaisesRegex(ValueError, "receiver bounds"):
                FACTS.tree_digest(root, "receiver")

    def test_runtime_between_sixty_four_and_128_mib_collects_and_rechecks(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-runtime-middle-") as temporary:
            root = Path(temporary)
            sparse_tree(root, [16 * MIB] * 5)
            first = FACTS.tree_digest(root, "runtime")
            second = FACTS.tree_digest(root, "runtime")
            self.assertEqual(first, second)
            self.assertEqual(sum(item["size"] for item in first[1]), 80 * MIB)

    def test_runtime_exact_128_mib_passes_and_one_more_byte_refuses(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-runtime-edge-") as temporary:
            root = Path(temporary)
            sparse_tree(root, [16 * MIB] * 8)
            _, files = FACTS.tree_digest(root, "runtime")
            self.assertEqual(sum(item["size"] for item in files), 128 * MIB)
            (root / "payload-9999.bin").write_bytes(b"x")
            with self.assertRaisesRegex(ValueError, "runtime bounds"):
                FACTS.tree_digest(root, "runtime")

    def test_roles_are_closed_and_file_entry_link_bounds_remain(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-runtime-closed-") as temporary:
            root = Path(temporary)
            sparse_tree(root, [1])
            with self.assertRaisesRegex(ValueError, "role is not closed"):
                FACTS.tree_digest(root, "caller-selected")
            (root / "payload-0000.bin").unlink()
            sparse_tree(root, [16 * MIB + 1])
            with self.assertRaisesRegex(ValueError, "runtime bounds"):
                FACTS.tree_digest(root, "runtime")
            (root / "payload-0000.bin").unlink()
            for index in range(4097):
                (root / f"entry-{index:04d}").touch()
            with self.assertRaisesRegex(ValueError, "runtime bounds"):
                FACTS.tree_digest(root, "runtime")
            for path in root.iterdir():
                path.unlink()
            (root / "target").write_bytes(b"target")
            (root / "link").symlink_to("target")
            with self.assertRaisesRegex(ValueError, "contains a link"):
                FACTS.tree_digest(root, "runtime")

    def test_changed_runtime_bytes_refuse_the_retained_inventory(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-runtime-change-") as temporary:
            root = Path(temporary)
            (root / "runtime.dll").write_bytes(b"first")
            retained = FACTS.tree_digest(root, "runtime")
            (root / "runtime.dll").write_bytes(b"later")
            self.assertNotEqual(FACTS.tree_digest(root, "runtime"), retained)

    def test_collect_and_verify_select_receiver_and_runtime_roles(self) -> None:
        with tempfile.TemporaryDirectory(prefix="p4-facts-roles-") as temporary:
            root = Path(temporary)
            receiver, runtime = root / "receiver", root / "runtime"
            receiver.mkdir(); runtime.mkdir()
            (receiver / "README.md").write_text("receiver\n", encoding="utf-8")
            (runtime / "runtime.dll").write_bytes(b"runtime")
            subprocess.run(["git", "init", "-q", str(receiver)], check=True)
            subprocess.run(["git", "-C", str(receiver), "config", "user.name", "runtime-bound-test"], check=True)
            subprocess.run(["git", "-C", str(receiver), "config", "user.email", "runtime-bound@example.invalid"], check=True)
            subprocess.run(["git", "-C", str(receiver), "add", "README.md"], check=True)
            subprocess.run(["git", "-C", str(receiver), "commit", "-q", "-m", "receiver"], check=True)
            candidate_path, join_path = root / "candidate.json", root / "join.json"
            candidate = {"producer": {"sourceRevision": "a" * 40}, "package": {}, "installedCli": {}, "image": {}}
            join = {"runtime": {"version": "10.0.12"}, "sdd": {"version": "1", "sha256": "b" * 64}, "templates": {"version": "1", "sha256": "c" * 64}}
            candidate_path.write_text(json.dumps(candidate), encoding="utf-8")
            join_path.write_text(json.dumps(join), encoding="utf-8")
            archive, output, profile = root / "receiver.tar", root / "facts.json", root / "profile.json"
            archive.write_bytes(b"archive")
            collect_args = argparse.Namespace(join=join_path, candidate=candidate_path, receiver=receiver,
                                               receiver_archive=archive, runtime=runtime, output=output, profile=profile)
            roles: list[str] = []
            original = FACTS.tree_digest
            with mock.patch.object(FACTS, "tree_digest", side_effect=lambda path, role: (roles.append(role), original(path, role))[1]):
                FACTS.collect(collect_args)
                facts = json.loads(output.read_text(encoding="utf-8"))
                facts["executables"] = {}
                output.write_bytes(FACTS.canonical(facts))
                FACTS.verify(argparse.Namespace(facts=output, candidate=candidate_path, receiver=receiver, runtime=runtime))
            self.assertEqual(roles, ["receiver", "runtime", "receiver", "runtime"])


class OfficialRuntimeInventory(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("FSGG_P4_OFFICIAL_RUNTIME_ARCHIVE"),
                         "set FSGG_P4_OFFICIAL_RUNTIME_ARCHIVE for the admitted public archive check")
    def test_official_dotnet_runtime_fits_only_the_runtime_role(self) -> None:
        archive = Path(os.environ["FSGG_P4_OFFICIAL_RUNTIME_ARCHIVE"])
        self.assertEqual(FACTS.sha256(archive), OFFICIAL_SHA256)
        with tarfile.open(archive, "r:gz") as package:
            members = package.getmembers()
        files = [member for member in members if member.isfile()]
        self.assertTrue(all(member.isdir() or member.isfile() for member in members))
        self.assertEqual(len(files), 193)
        self.assertEqual(sum(member.size for member in files), 82_714_208)
        self.assertEqual(max(member.size for member in files), 15_576_872)
        self.assertGreater(sum(member.size for member in files), FACTS.ROLE_AGGREGATE_BYTES["receiver"])
        self.assertLessEqual(sum(member.size for member in files), FACTS.ROLE_AGGREGATE_BYTES["runtime"])
        self.assertLessEqual(max(member.size for member in files), FACTS.MAX_FILE_BYTES)


if __name__ == "__main__":
    unittest.main()
