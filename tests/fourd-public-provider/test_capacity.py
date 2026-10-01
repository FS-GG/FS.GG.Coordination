#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "fourd_public_capacity", ROOT / "eng/fourd-public-provider/capacity.py")
capacity = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(capacity)
WORKFLOW = ROOT / ".github/workflows/fourd-public-provider-qualification.yml"


class CapacityFactsTests(unittest.TestCase):
    def test_memory_floor_keeps_private_refusal_and_does_not_credit_swap(self):
        self.assertEqual(7_516_192_768, capacity.MEMORY_FLOOR)
        meminfo = capacity.read_meminfo(
            "MemTotal:       8128876 kB\nMemAvailable:    7016936 kB\n"
            "SwapTotal:      1048576 kB\nSwapFree:       1048576 kB\n")
        self.assertEqual(7_185_342_464, meminfo["MemAvailable"])
        unlimited = {"minimumFiniteHeadroomBytes": None}
        self.assertLess(capacity.effective_memory(meminfo, unlimited), capacity.MEMORY_FLOOR)
        threshold = dict(meminfo, MemAvailable=capacity.MEMORY_FLOOR)
        self.assertEqual(capacity.MEMORY_FLOOR, capacity.effective_memory(threshold, unlimited))
        constrained = {"minimumFiniteHeadroomBytes": capacity.MEMORY_FLOOR - 1}
        self.assertEqual(capacity.MEMORY_FLOOR - 1,
                         capacity.effective_memory(threshold, constrained))

    def test_cgroup_v2_ancestor_accounting_is_complete_and_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            mount = pathlib.Path(temporary) / "cgroup"
            leaf = mount / "actions" / "job"
            leaf.mkdir(parents=True)
            for directory, maximum, current in (
                (mount, "max", "100"),
                (mount / "actions", "8589934592", "1073741824"),
                (leaf, "8053063680", "268435456"),
            ):
                (directory / "memory.max").write_text(maximum)
                (directory / "memory.current").write_text(current)
            result = capacity.cgroup_accounting(mount, leaf)
            self.assertEqual((3, 2, 7_516_192_768, True),
                             (result["ancestorCount"], result["finiteLimitCount"],
                              result["minimumFiniteHeadroomBytes"], result["accountingKnown"]))
            (leaf / "memory.current").write_text("unknown")
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, leaf)
            (leaf / "memory.current").write_text("900")
            (leaf / "memory.max").write_text("800")
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_accounting(mount, leaf)

    def test_cgroup_mount_membership_requires_one_v2_route(self):
        mountinfo = "36 25 0:32 / /sys/fs/cgroup rw - cgroup2 cgroup rw\n"
        self.assertEqual((pathlib.Path("/sys/fs/cgroup"), pathlib.Path("/sys/fs/cgroup/actions/job")),
                         capacity.cgroup_location(mountinfo, "0::/actions/job\n"))
        for bad_mounts, bad_membership in (("", "0::/actions/job\n"),
                                            (mountinfo, "1:name=/bad\n"),
                                            (mountinfo + mountinfo, "0::/actions/job\n")):
            with self.assertRaisesRegex(capacity.Refusal, "capacity-cgroup-refused"):
                capacity.cgroup_location(bad_mounts, bad_membership)


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

    def test_screen_passes_only_capacity_and_removes_owned_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            output, state = root / "output/result.json", root / "state"
            meminfo = {"MemTotal": 16 * 1024**3, "MemAvailable": 12 * 1024**3,
                       "SwapTotal": 4 * 1024**3, "SwapFree": 4 * 1024**3}
            cgroup = {"version": 2, "ancestorCount": 2, "finiteLimitCount": 1,
                      "minimumFiniteHeadroomBytes": 10 * 1024**3, "accountingKnown": True}
            filesystem = {"mountDevice": "0:1", "filesystemType": "ext4", "statDevice": 1,
                          "freeBytes": 10 * 1024**3, "freeInodes": 1000}
            with mock.patch.dict(os.environ, self.environment(), clear=True), \
                 mock.patch.object(capacity.platform, "machine", return_value="x86_64"), \
                 mock.patch.object(capacity, "read_meminfo", return_value=meminfo), \
                 mock.patch.object(capacity, "cgroup_location", return_value=(root, root)), \
                 mock.patch.object(capacity, "cgroup_accounting", return_value=cgroup), \
                 mock.patch.object(capacity, "filesystem_fact", return_value=filesystem), \
                 mock.patch.object(capacity, "podman_fact", return_value={
                     "version": "4.9.3", "rootless": True, "graphDriver": "vfs",
                     "containers": 0, "images": 0, "ownedState": True}), \
                 mock.patch.object(pathlib.Path, "read_text", return_value="fixture"):
                self.assertTrue(capacity.screen(output, state))
            result = json.loads(output.read_text())
            self.assertTrue(result["capacityScreenPassed"])
            self.assertFalse(result["qualified"])
            self.assertEqual(0, result["facts"]["memory"]["swapCreditedBytes"])
            self.assertTrue(result["facts"]["cleanup"]["ownedStateCreated"])
            self.assertTrue(result["facts"]["cleanup"]["ownedStateRemoved"])
            self.assertFalse(state.exists())
            self.assertLessEqual(output.stat().st_size, capacity.MAX_OUTPUT)
            self.assertEqual(0o600, output.stat().st_mode & 0o777)

    def test_memory_refusal_prevents_filesystem_and_podman_effects(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            output, state = root / "output/result.json", root / "state"
            meminfo = {"MemTotal": 8_323_969_024, "MemAvailable": 7_185_342_464,
                       "SwapTotal": 1, "SwapFree": 1}
            cgroup = {"version": 2, "ancestorCount": 1, "finiteLimitCount": 0,
                      "minimumFiniteHeadroomBytes": None, "accountingKnown": True}
            with mock.patch.dict(os.environ, self.environment(), clear=True), \
                 mock.patch.object(capacity.platform, "machine", return_value="x86_64"), \
                 mock.patch.object(capacity, "read_meminfo", return_value=meminfo), \
                 mock.patch.object(capacity, "cgroup_location", return_value=(root, root)), \
                 mock.patch.object(capacity, "cgroup_accounting", return_value=cgroup), \
                 mock.patch.object(capacity, "filesystem_fact") as filesystem, \
                 mock.patch.object(capacity, "podman_fact") as podman, \
                 mock.patch.object(pathlib.Path, "read_text", return_value="fixture"):
                self.assertFalse(capacity.screen(output, state))
            result = json.loads(output.read_text())
            self.assertEqual("capacity-memory-refused", result["failureCode"])
            self.assertFalse(result["capacityScreenPassed"])
            self.assertFalse(result["qualified"])
            filesystem.assert_not_called()
            podman.assert_not_called()
            self.assertFalse(state.exists())

    def test_context_refuses_credentials_before_any_capacity_work(self):
        environment = self.environment()
        environment[capacity.FORBIDDEN_CREDENTIALS[1]] = "unexpected-private-key"
        with mock.patch.dict(os.environ, environment, clear=True):
            with self.assertRaisesRegex(capacity.Refusal, "capacity-credential-refused"):
                capacity.context_fact()
        self.assertNotIn("unexpected-private-key", str(capacity.FORBIDDEN_CREDENTIALS))

    def test_unimplemented_qualification_is_typed_and_never_qualified(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "result.json"
            result = capacity.base_result("qualification")
            result["failureCode"] = "qualification-phase-unimplemented"
            capacity.write_result(output, result)
            saved = json.loads(output.read_text())
            self.assertEqual("qualification-phase-unimplemented", saved["failureCode"])
            self.assertFalse(saved["capacityScreenPassed"])
            self.assertFalse(saved["qualified"])


class WorkflowSourceTests(unittest.TestCase):
    def test_workflow_is_manual_fixed_public_capacity_without_credentials_or_effects(self):
        text = WORKFLOW.read_text()
        self.assertIn("workflow_dispatch:", text)
        self.assertNotIn("pull_request:", text)
        self.assertNotIn("push:", text)
        self.assertIn("runs-on: ubuntu-24.04", text)
        self.assertIn("environment: fourd-native-private-source", text)
        self.assertIn("refs/heads/qualification/fourd-native-20261001", text)
        self.assertIn("github.repository == 'FS-GG/FS.GG.Coordination'", text)
        self.assertNotIn("secrets.", text)
        self.assertNotIn("qualify.py", text)
        self.assertNotIn("custody.py", text)
        self.assertNotIn("git clone", text.lower())
        self.assertNotIn("ssh-agent", text.lower())
        self.assertNotIn("podman pull", text)
        self.assertNotIn("podman build", text)
        for absent in ("dotnet", "npm ", "native-image", "image save", "image load", "p2 result"):
            self.assertNotIn(absent, text.lower())
        self.assertNotIn("workflow_call", text)
        self.assertEqual(1, text.count("path: ${{ env.RESULT_ROOT }}/result.json"))
        self.assertIn("qualification-phase-unimplemented", text)
        self.assertIn("capacityScreenPassed'] is True", text)
        self.assertIn("qualified'] is False", text)

    def test_workflow_has_only_closed_phase_input_and_valid_shell(self):
        text = WORKFLOW.read_text()
        self.assertEqual(1, text.count("inputs:"))
        self.assertNotIn("source_key:", text)
        self.assertNotIn("command:", text)
        try:
            import yaml
        except ImportError as error:
            self.fail(f"PyYAML required for this source check: {error}")
        value = yaml.safe_load(text)
        dispatch = value.get("on", value.get(True))["workflow_dispatch"]
        self.assertEqual({"phase"}, set(dispatch["inputs"]))
        for step in value["jobs"]["fixed-public-provider"]["steps"]:
            if "run" in step:
                checked = __import__("subprocess").run(
                    ["bash", "-n"], input=step["run"], text=True,
                    stdout=__import__("subprocess").PIPE,
                    stderr=__import__("subprocess").PIPE)
                self.assertEqual(0, checked.returncode, checked.stderr)


if __name__ == "__main__":
    unittest.main()
