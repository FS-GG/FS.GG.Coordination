#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import signal
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "fourd_public_capacity", ROOT / "eng/fourd-public-provider/capacity.py")
capacity = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(capacity)
WORKFLOW = ROOT / "tests/fixtures/retired-workflows/fourd-public-provider-qualification.yml"
HELPER = ROOT / "eng/fourd-public-provider/capacity.py"


class CapacityFactsTests(unittest.TestCase):
    def test_memory_floor_exact_threshold_one_byte_short_and_no_swap_credit(self):
        self.assertEqual(7_516_192_768, capacity.MEMORY_FLOOR)
        meminfo = capacity.read_meminfo(
            "MemTotal:       16777216 kB\nMemAvailable:    7340032 kB\n"
            "SwapTotal:      1048576 kB\nSwapFree:       1048576 kB\n")
        self.assertEqual(capacity.MEMORY_FLOOR, meminfo["MemAvailable"])
        unlimited = {"minimumFiniteHeadroomBytes": None}
        self.assertEqual(capacity.MEMORY_FLOOR, capacity.effective_memory(meminfo, unlimited))
        constrained = {"minimumFiniteHeadroomBytes": capacity.MEMORY_FLOOR - 1}
        self.assertEqual(capacity.MEMORY_FLOOR - 1,
                         capacity.effective_memory(meminfo, constrained))
        self.assertGreater(meminfo["SwapFree"], 0)

    def test_true_hierarchy_root_may_omit_files_and_ancestor_high_limits_headroom(self):
        with tempfile.TemporaryDirectory() as temporary:
            mount = pathlib.Path(temporary) / "cgroup"
            parent = mount / "actions"
            leaf = parent / "job"
            leaf.mkdir(parents=True)
            for directory, maximum, high, current in (
                (parent, "max", str(capacity.MEMORY_FLOOR + 100), "100"),
                (leaf, str(12 * 1024**3), "max", str(1024**3)),
            ):
                (directory / "memory.max").write_text(maximum)
                (directory / "memory.high").write_text(high)
                (directory / "memory.current").write_text(current)
            result = capacity.cgroup_accounting(mount, leaf, True)
            self.assertEqual(3, result["ancestorCount"])
            self.assertEqual(2, result["finiteLimitCount"])
            self.assertEqual(capacity.MEMORY_FLOOR, result["minimumFiniteHeadroomBytes"])
            self.assertTrue(result["ancestors"][-1]["hierarchyRoot"])
            self.assertIsNone(result["ancestors"][-1]["currentBytes"])

    def test_mounted_subtree_root_requires_complete_accounting(self):
        with tempfile.TemporaryDirectory() as temporary:
            mount = pathlib.Path(temporary) / "cgroup"
            mount.mkdir()
            for name, value in (("memory.max", "max"), ("memory.high", "8053063680"),
                                ("memory.current", "536870912")):
                (mount / name).write_text(value)
            result = capacity.cgroup_accounting(mount, mount, False)
            self.assertEqual(7_516_192_768, result["minimumFiniteHeadroomBytes"])
            (mount / "memory.current").unlink()
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, mount, False)

    def test_missing_partial_and_unknown_accounting_refuse(self):
        with tempfile.TemporaryDirectory() as temporary:
            mount = pathlib.Path(temporary) / "cgroup"
            leaf = mount / "job"
            leaf.mkdir(parents=True)
            (leaf / "memory.max").write_text("max")
            (leaf / "memory.high").write_text("max")
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, leaf, True)
            (leaf / "memory.current").write_text("unknown")
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, leaf, True)
            (leaf / "memory.current").write_text("101")
            (leaf / "memory.max").write_text("100")
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, leaf, True)

    def test_mount_identity_rejects_traversal_ambiguity_and_outside_membership(self):
        mountinfo = "36 25 0:32 / /sys/fs/cgroup rw - cgroup2 cgroup rw\n"
        self.assertEqual(
            (pathlib.Path("/sys/fs/cgroup"), pathlib.Path("/sys/fs/cgroup/actions/job"), True),
            capacity.cgroup_location(mountinfo, "0::/actions/job\n"))
        bad = (
            (mountinfo, "0::/../../outside\n"),
            ("36 25 0:32 /tenant /sys/fs/cgroup rw - cgroup2 cgroup rw\n", "0::/other/job\n"),
            (mountinfo + mountinfo, "0::/actions/job\n"),
            (mountinfo, "0::/actions/job\n0::/other\n"),
        )
        for mounts, membership in bad:
            with self.subTest(membership=membership):
                with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                    capacity.cgroup_location(mounts, membership)


class ScreenTests(unittest.TestCase):
    def environment(self):
        return {
            "GITHUB_REPOSITORY": capacity.EXPECTED_REPOSITORY,
            "GITHUB_REF": capacity.EXPECTED_REF,
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_SHA": "a" * 40,
            "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "1",
            "RUNNER_TEMP": "/tmp",
            "RUNNER_OS": "Linux",
            "RUNNER_ARCH": "X64",
            "RUNNER_ENVIRONMENT": "github-hosted",
            "ImageOS": "ubuntu24",
            "ImageVersion": "20261001.1",
        }

    def common_facts(self):
        meminfo = {"MemTotal": 16 * 1024**3, "MemAvailable": 12 * 1024**3,
                   "SwapTotal": 4 * 1024**3, "SwapFree": 4 * 1024**3}
        cgroup = {"version": 2, "ancestorCount": 2, "finiteLimitCount": 1,
                  "minimumFiniteHeadroomBytes": 10 * 1024**3, "accountingKnown": True}
        filesystem = {"mountDevice": "0:1", "filesystemType": "ext4", "statDevice": 1,
                      "freeBytes": 10 * 1024**3, "freeInodes": 1000}
        return meminfo, cgroup, filesystem

    def test_screen_is_read_only_and_reports_rootless_capability_unmeasured(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            output = root / "result.json"
            meminfo, cgroup, filesystem = self.common_facts()
            with mock.patch.dict(os.environ, self.environment(), clear=True), \
                 mock.patch.object(capacity.platform, "machine", return_value="x86_64"), \
                 mock.patch.object(capacity, "read_meminfo", return_value=meminfo), \
                 mock.patch.object(capacity, "cgroup_location", return_value=(root, root, True)), \
                 mock.patch.object(capacity, "cgroup_accounting", return_value=cgroup), \
                 mock.patch.object(capacity, "filesystem_fact", return_value=filesystem), \
                 mock.patch.object(capacity.shutil, "which", return_value="/usr/bin/podman"), \
                 mock.patch.object(pathlib.Path, "read_text", return_value="fixture"):
                self.assertTrue(capacity.screen(output))
            result = json.loads(output.read_text())
            self.assertTrue(result["capacityScreenPassed"])
            self.assertFalse(result["qualified"])
            self.assertEqual(0, result["facts"]["memory"]["swapCreditedBytes"])
            self.assertEqual(10 * 1024**3, result["facts"]["filesystems"]["runnerTemp"]["freeBytes"])
            self.assertEqual(1000, result["facts"]["filesystems"]["runnerTemp"]["freeInodes"])
            self.assertEqual({"binaryAvailable": True, "version": None,
                              "rootlessCapability": "unmeasured", "probeExecuted": False},
                             result["facts"]["podman"])
            self.assertLessEqual(output.stat().st_size, capacity.MAX_OUTPUT)
            self.assertEqual(0o600, output.stat().st_mode & 0o777)
            self.assertEqual(["result.json"], sorted(item.name for item in root.iterdir()))

    def test_one_byte_memory_refusal_prevents_later_measurements(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            output = root / "result.json"
            meminfo = {"MemTotal": 16 * 1024**3,
                       "MemAvailable": capacity.MEMORY_FLOOR - 1,
                       "SwapTotal": 8 * 1024**3, "SwapFree": 8 * 1024**3}
            cgroup = {"minimumFiniteHeadroomBytes": None, "accountingKnown": True}
            with mock.patch.dict(os.environ, self.environment(), clear=True), \
                 mock.patch.object(capacity.platform, "machine", return_value="x86_64"), \
                 mock.patch.object(capacity, "read_meminfo", return_value=meminfo), \
                 mock.patch.object(capacity, "cgroup_location", return_value=(root, root, True)), \
                 mock.patch.object(capacity, "cgroup_accounting", return_value=cgroup), \
                 mock.patch.object(capacity, "filesystem_fact") as filesystem, \
                 mock.patch.object(capacity, "podman_static_fact") as podman, \
                 mock.patch.object(pathlib.Path, "read_text", return_value="fixture"):
                self.assertFalse(capacity.screen(output))
            result = json.loads(output.read_text())
            self.assertEqual("capacity-memory-refused", result["failureCode"])
            filesystem.assert_not_called()
            podman.assert_not_called()

    def test_sigterm_retains_bounded_typed_cancellation(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "result.json"

            def cancel_read(*_args, **_kwargs):
                os.kill(os.getpid(), signal.SIGTERM)
                return "unreachable"

            with mock.patch.dict(os.environ, self.environment(), clear=True), \
                 mock.patch.object(capacity.platform, "machine", return_value="x86_64"), \
                 mock.patch.object(pathlib.Path, "read_text", side_effect=cancel_read):
                self.assertFalse(capacity.screen(output))
            result = json.loads(output.read_text())
            self.assertEqual("capacity-cancelled", result["failureCode"])
            self.assertFalse(result["capacityScreenPassed"])
            self.assertFalse(result["qualified"])
            self.assertLessEqual(output.stat().st_size, capacity.MAX_OUTPUT)

    def test_preexisting_output_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            output = root / "result.json"
            output.write_bytes(b"owner-data\n")
            before = output.stat()
            with self.assertRaises(FileExistsError):
                capacity.write_result(output, capacity.base_result("capacity"))
            after = output.stat()
            self.assertEqual(b"owner-data\n", output.read_bytes())
            self.assertEqual((before.st_ino, before.st_mode), (after.st_ino, after.st_mode))
            self.assertEqual(["result.json"], sorted(item.name for item in root.iterdir()))

    def test_context_refuses_credentials_before_capacity_work(self):
        environment = self.environment()
        environment[capacity.FORBIDDEN_CREDENTIALS[1]] = "unexpected-private-key"
        with mock.patch.dict(os.environ, environment, clear=True):
            with self.assertRaisesRegex(capacity.Refusal, "capacity-credential-refused"):
                capacity.context_fact()
        self.assertNotIn("unexpected-private-key", str(capacity.FORBIDDEN_CREDENTIALS))

    def test_capacity_result_is_never_a_qualification(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "result.json"
            result = capacity.base_result("capacity")
            result["capacityScreenPassed"] = True
            capacity.write_result(output, result)
            saved = json.loads(output.read_text())
            self.assertEqual("capacity", saved["phase"])
            self.assertTrue(saved["capacityScreenPassed"])
            self.assertFalse(saved["qualified"])


class WorkflowSourceTests(unittest.TestCase):
    def test_retired_workflow_is_fixture_only_with_original_bytes(self):
        import hashlib
        self.assertFalse((ROOT / ".github/workflows/fourd-public-provider-qualification.yml").exists())
        archived = ROOT / "tests/fixtures/retired-workflows/fourd-public-provider-qualification.yml"
        self.assertEqual("03e2c515ca58c045892135c2dc53fa8826fb2c0489af68ff49bd9a53dfc59d80", hashlib.sha256(archived.read_bytes()).hexdigest())

    def test_capacity_job_is_manual_fixed_and_has_no_credentials_or_private_effects(self):
        text = WORKFLOW.read_text()
        helper = HELPER.read_text()
        capacity_job = text.split("  capacity:\n", 1)[1].split("\n  qualification:\n", 1)[0]
        self.assertIn("workflow_dispatch:", text)
        self.assertNotIn("pull_request:", text)
        self.assertNotIn("push:", text)
        self.assertIn("runs-on: ubuntu-24.04", text)
        self.assertNotIn("environment:", capacity_job)
        self.assertIn("refs/heads/qualification/fourd-native-20261001", text)
        self.assertIn("github.repository == 'FS-GG/FS.GG.Coordination'", text)
        self.assertNotIn("secrets.", capacity_job)
        self.assertNotIn("FSGG_FOURD_PUBLIC_PROVIDER_ADMISSION_JSON_B64", capacity_job)
        self.assertNotIn("FSGG_FOURD_READONLY_DEPLOY_KEY_B64", capacity_job)
        self.assertNotIn("qualify.py", capacity_job)
        self.assertNotIn("git clone", capacity_job.lower())
        self.assertNotIn("ssh-agent", capacity_job.lower())
        for absent in ("podman version", "podman info", "podman pull", "podman build",
                       "subprocess", "--state", "state_root", "native-image", "image save",
                       "image load", "p2 result"):
            self.assertNotIn(absent, (capacity_job + helper).lower())
        self.assertNotIn("workflow_call", text)
        self.assertEqual(1, text.count("path: ${{ env.RESULT_ROOT }}/result.json"))
        self.assertIn("capacityScreenPassed'] is True", text)
        self.assertIn("qualified'] is False", text)
        self.assertIn("inputs.phase == 'capacity'", capacity_job)

    def test_workflow_has_only_closed_phase_input_and_valid_shell(self):
        text = WORKFLOW.read_text()
        self.assertEqual(1, text.count("inputs:"))
        self.assertNotIn("source_key:", text)
        self.assertNotIn("command:", text)
        self.assertEqual(1, text.count("    inputs:\n"))
        self.assertEqual(1, text.count("      phase:\n"))
        self.assertEqual(1, text.count("  capacity:\n"))
        self.assertEqual(1, text.count("  qualification:\n"))
        lines = text.splitlines()
        scripts = []
        for index, line in enumerate(lines):
            if line.strip() != "run: |":
                continue
            indent = len(line) - len(line.lstrip())
            script = []
            for candidate in lines[index + 1:]:
                current = len(candidate) - len(candidate.lstrip())
                if candidate and current <= indent:
                    break
                script.append(candidate[indent + 2:] if candidate else "")
            scripts.append("\n".join(script))
        self.assertGreaterEqual(len(scripts), 5)
        for script in scripts:
            checked = __import__("subprocess").run(
                ["bash", "-n"], input=script, text=True,
                stdout=__import__("subprocess").PIPE,
                stderr=__import__("subprocess").PIPE)
            self.assertEqual(0, checked.returncode, checked.stderr)


if __name__ == "__main__":
    unittest.main()
