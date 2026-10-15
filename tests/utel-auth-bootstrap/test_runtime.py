#!/usr/bin/env python3
"""Hosted fake-only ELF and FD fixture qualification; no credential input."""

from __future__ import annotations

import argparse
import copy
import fcntl
import hashlib
import json
import os
import pathlib
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE = b"FSGG-UTEL-INERT-V1\n"
FRAME = len(FIXTURE).to_bytes(4, "big") + FIXTURE
FIELDS = ("productionAvailable", "credentialIngressAvailable", "sameTokenCleanupAvailable",
          "nativeConstructorAvailable", "authorized")
CLEANUP_FAILURES: list[str] = []
ACTIVE_CHILDREN: set[subprocess.Popen[bytes]] = set()
INPUTS = (
    "src/FS.GG.Coordination.Orchestration.Execution/Custody/utel-auth-bootstrap.c",
    "src/FS.GG.Coordination.Orchestration.Execution/Custody/build-utel-auth.py",
    "tests/utel-auth-bootstrap/test_static.py",
    "tests/utel-auth-bootstrap/test_runtime.py",
    ".github/workflows/utel-auth-bootstrap-qualification.yml",
)

# Hosted harness only: close_fds precedes a subprocess preexec_fn on some Python
# runtimes, so keep source FDs through a Python child and remap immediately before exec.
FD_LAUNCHER = """
import os, sys
source, extra, binary = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
os.dup2(source, 3)
if extra >= 0:
    os.dup2(extra, 4)
for fd in {source, extra}:
    if fd >= 4 and not (fd == 4 and extra >= 0):
        os.close(fd)
os.execv(binary, [binary, '--inert-fixture'])
"""


def spawn_fd(binary: pathlib.Path, source: int, extra: int = -1) -> subprocess.Popen[bytes]:
    passed = (source, extra) if extra >= 0 else (source,)
    # SIG_IGN survives exec into the launcher. The C fixture installs its own
    # handler before input reads, so an early signal cannot kill Python startup.
    previous = signal.signal(signal.SIGUSR1, signal.SIG_IGN)
    try:
        child = subprocess.Popen([sys.executable, "-c", FD_LAUNCHER, str(source), str(extra),
                                  str(binary)], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, pass_fds=passed)
    finally:
        signal.signal(signal.SIGUSR1, previous)
    ACTIVE_CHILDREN.add(child)
    return child


def reap(child: subprocess.Popen[bytes]) -> None:
    if child.poll() is None:
        try:
            child.kill()
        except ProcessLookupError:
            pass
    try:
        child.communicate(timeout=1)
    except (subprocess.TimeoutExpired, OSError) as error:
        CLEANUP_FAILURES.append(type(error).__name__)
    if child.poll() is None:
        CLEANUP_FAILURES.append("child-not-terminal")
    else:
        ACTIVE_CHILDREN.discard(child)


def finish(child: subprocess.Popen[bytes], timeout: float = 3) -> tuple[bytes, bytes]:
    try:
        output = child.communicate(timeout=timeout)
        ACTIVE_CHILDREN.discard(child)
        return output
    except subprocess.TimeoutExpired as error:
        reap(child)
        raise AssertionError("owned child timed out; kill/reap attempted") from error
    except BaseException:
        reap(child)
        raise


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_manifest(body: dict, directory: pathlib.Path, head: str, run: int, attempt: int) -> None:
    assert set(body) == {"schema", "profile", "referenceOperation", "source", "inputs",
                         "compiler", "elf", "binary", "entrypoints", "capabilities"}
    assert body["schema"] == "fsgg.utel.auth-bootstrap.inert-artifact/1"
    assert body["profile"] == "linux-x64-utel-auth-inert-source/1"
    assert body["referenceOperation"] == "utel-rel-21/original-tag-authentication-only/2"
    assert set(body["source"]) == {"repository", "head", "runId", "attempt"}
    assert body["source"]["repository"] == "FS-GG/FS.GG.Coordination"
    assert body["source"]["head"] == head
    assert type(body["source"]["runId"]) is int and body["source"]["runId"] == run
    assert type(body["source"]["attempt"]) is int and body["source"]["attempt"] == attempt
    assert set(body["inputs"]) == set(INPUTS)
    assert body["inputs"] == {item: sha(ROOT / item) for item in INPUTS}
    compiler = body["compiler"]
    assert set(compiler) == {"path", "sha256", "version", "flags"}
    compiler_path = pathlib.Path(compiler["path"])
    assert compiler_path.is_absolute() and compiler_path.is_file()
    assert compiler["sha256"] == sha(compiler_path)
    assert compiler["version"] == subprocess.check_output(
        [str(compiler_path), "--version"], text=True, timeout=5).splitlines()[0]
    assert compiler["flags"] == ["-static", "-Os", "-s", "-fstack-protector-strong",
                                 "-D_FORTIFY_SOURCE=3", "-Wl,--build-id=none", "-Wall",
                                 "-Wextra", "-Werror"]
    assert body["elf"] == {"class": "ELF64", "machine": "Advanced Micro Devices X86-64",
                            "interpreter": False, "neededLibraries": []}
    assert body["entrypoints"] == ["--describe", "--inert-fixture"]
    assert set(body["capabilities"]) == set(FIELDS)
    for field in FIELDS:
        assert body["capabilities"][field] is False
    binary = body["binary"]
    assert set(binary) == {"path", "bytes", "sha256"}
    assert binary["path"] == "utel-auth-bootstrap.linux-x64.bin"
    path = directory / binary["path"]
    assert path.is_file() and not path.is_symlink()
    assert 0 < binary["bytes"] <= 2 * 1024 * 1024
    assert binary["bytes"] == path.stat().st_size and binary["sha256"] == sha(path)
    header = subprocess.check_output(["readelf", "-h", str(path)], text=True, timeout=5)
    segments = subprocess.check_output(["readelf", "-l", str(path)], text=True, timeout=5)
    dynamic = subprocess.check_output(["readelf", "-d", str(path)], text=True, timeout=5)
    assert "Class:                             ELF64" in header
    assert "Machine:                           Advanced Micro Devices X86-64" in header
    assert "INTERP" not in segments and "NEEDED" not in dynamic


class HostedRuntime(unittest.TestCase):
    binary: pathlib.Path
    manifest: dict
    output: pathlib.Path
    head: str
    run: int
    attempt: int

    def test_manifest_and_mutants(self) -> None:
        check_manifest(self.manifest, self.output, self.head, self.run, self.attempt)
        for field in FIELDS:
            mutant = copy.deepcopy(self.manifest)
            mutant["capabilities"][field] = True
            with self.subTest(field=field), self.assertRaises(AssertionError):
                check_manifest(mutant, self.output, self.head, self.run, self.attempt)
        for edit in (
            lambda m: m["entrypoints"].append("--run"),
            lambda m: m["binary"].update(sha256="0" * 64),
            lambda m: m["inputs"].update({INPUTS[0]: "0" * 64}),
            lambda m: m.update(unexpected=True),
            lambda m: m.pop("elf"),
            lambda m: m["source"].update(runId=self.run + 1),
            lambda m: m["source"].update(attempt=True),
            lambda m: m["capabilities"].update(authorized=0),
        ):
            mutant = copy.deepcopy(self.manifest)
            edit(mutant)
            with self.assertRaises(AssertionError):
                check_manifest(mutant, self.output, self.head, self.run, self.attempt)

    def test_describe_and_unsupported_mode(self) -> None:
        result = subprocess.run([str(self.binary), "--describe"], capture_output=True, timeout=3)
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["status"], "source-only")
        self.assertEqual(data["architecture"], "linux-x64")
        self.assertEqual(data["fdRoles"], [0, 1, 2, 3])
        for field in FIELDS:
            self.assertIs(data[field], False)
        for argv in (("--run",), ("--inert-fixture", "extra"), ()):
            result = subprocess.run([str(self.binary), *argv], capture_output=True, timeout=3)
            self.assertEqual(result.returncode, 125)

    def fixture(self, payload: bytes, *, keep_open: bool = False, extra_fd: bool = False,
                interrupt: bool = False) -> subprocess.CompletedProcess[bytes]:
        read_fd, write_fd = os.pipe()
        added_fd = os.open("/dev/null", os.O_RDONLY) if extra_fd else -1
        child: subprocess.Popen[bytes] | None = None
        try:
            child = spawn_fd(self.binary, read_fd, added_fd)
            if payload:
                os.write(write_fd, payload)
            if not keep_open:
                os.close(write_fd)
                write_fd = -1
            if interrupt:
                time.sleep(0.15)
                for _ in range(10):
                    if child.poll() is not None:
                        break
                    try:
                        os.kill(child.pid, signal.SIGUSR1)
                    except ProcessLookupError:
                        break
                    time.sleep(0.05)
            started = time.monotonic()
            stdout, stderr = finish(child)
            self.assertLess(time.monotonic() - started, 2.5)
            return subprocess.CompletedProcess(child.args, child.returncode, stdout, stderr)
        finally:
            if child is not None and child in ACTIVE_CHILDREN:
                reap(child)
            os.close(read_fd)
            if write_fd >= 0:
                os.close(write_fd)
            if added_fd >= 0:
                os.close(added_fd)

    def test_exact_pipe_fixture_and_refusals(self) -> None:
        good = self.fixture(FRAME)
        self.assertEqual(good.returncode, 0, good.stderr)
        self.assertNotIn(FIXTURE, good.stdout)
        data = json.loads(good.stdout)
        self.assertEqual(data["status"], "inert")
        self.assertEqual(data["architecture"], "linux-x64")
        for field in FIELDS:
            self.assertIs(data[field], False)
        bad = (FRAME[:-1], FRAME + b"X", b"\0\0\0\0" + FIXTURE,
               b"\0\0\x10\0" + FIXTURE, FRAME[:4] + b"X" + FRAME[5:],
               b"\0\0\0", FRAME[:4], b"")
        for payload in bad:
            with self.subTest(payload=payload):
                self.assertEqual(self.fixture(payload).returncode, 125)
        self.assertEqual(self.fixture(FRAME, extra_fd=True).returncode, 125)
        self.assertEqual(self.fixture(FRAME, keep_open=True).returncode, 125)
        self.assertEqual(self.fixture(b"", keep_open=True, interrupt=True).returncode, 125)
        occupied = os.open("/dev/null", os.O_RDONLY)
        try:
            self.assertEqual(self.fixture(FRAME).returncode, 0)
            self.assertEqual(self.fixture(FRAME, extra_fd=True).returncode, 125)
        finally:
            os.close(occupied)

    def test_wrong_fd_type(self) -> None:
        missing = subprocess.run([str(self.binary), "--inert-fixture"],
                                 stdin=subprocess.DEVNULL, capture_output=True, timeout=3)
        self.assertEqual(missing.returncode, 125)
        with tempfile.TemporaryFile() as source:
            source.write(FRAME)
            source.seek(0)
            child = spawn_fd(self.binary, source.fileno())
            finish(child)
            self.assertEqual(child.returncode, 125)
        source, peer = socket.socketpair()
        with source, peer:
            peer.sendall(FRAME)
            peer.shutdown(socket.SHUT_WR)
            child = spawn_fd(self.binary, source.fileno())
            finish(child)
            self.assertEqual(child.returncode, 125)

    def test_stalled_output_refused_by_original_deadline(self) -> None:
        reader, writer = os.pipe()
        try:
            fcntl.fcntl(writer, fcntl.F_SETFL, fcntl.fcntl(writer, fcntl.F_GETFL) | os.O_NONBLOCK)
            try:
                while True:
                    os.write(writer, b"x" * 4096)
            except BlockingIOError:
                pass
            started = time.monotonic()
            child = subprocess.Popen([str(self.binary), "--describe"], stdin=subprocess.DEVNULL,
                                     stdout=writer, stderr=subprocess.PIPE)
            ACTIVE_CHILDREN.add(child)
            _, stderr = finish(child)
            self.assertEqual(child.returncode, 125, stderr)
            self.assertLess(time.monotonic() - started, 2.5)
        finally:
            if 'child' in locals() and child in ACTIVE_CHILDREN:
                reap(child)
            os.close(reader)
            os.close(writer)

    def test_regular_file_output_refused(self) -> None:
        with tempfile.TemporaryFile() as sink:
            result = subprocess.run([str(self.binary), "--describe"],
                                    stdin=subprocess.DEVNULL, stdout=sink, stderr=subprocess.PIPE,
                                    timeout=3)
            self.assertEqual(result.returncode, 125)


class RecordingResult(unittest.TextTestResult):
    first_problem: tuple[str, str] | None = None

    def addFailure(self, test: unittest.case.TestCase, err: tuple) -> None:
        super().addFailure(test, err)
        if self.first_problem is None:
            self.first_problem = (str(test), self.failures[-1][1][-2000:])

    def addError(self, test: unittest.case.TestCase, err: tuple) -> None:
        super().addError(test, err)
        if self.first_problem is None:
            self.first_problem = (str(test), self.errors[-1][1][-2000:])


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("head")
    parser.add_argument("run", type=int)
    parser.add_argument("attempt", type=int)
    args = parser.parse_args()
    HostedRuntime.output = args.output
    HostedRuntime.head = args.head
    HostedRuntime.run = args.run
    HostedRuntime.attempt = args.attempt
    HostedRuntime.manifest = json.loads((args.output / "manifest.json").read_text())
    HostedRuntime.binary = args.output / "utel-auth-bootstrap.linux-x64.bin"
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(HostedRuntime)
    result = unittest.TextTestRunner(verbosity=2, resultclass=RecordingResult).run(suite)
    first_failure = result.first_problem
    for child in tuple(ACTIVE_CHILDREN):
        reap(child)
    receipt = {
        "schema": "fsgg.utel.auth-bootstrap.inert-test-receipt/1",
        "head": args.head, "runId": args.run, "attempt": args.attempt,
        "testsRun": result.testsRun, "failures": len(result.failures), "errors": len(result.errors),
        "firstFailure": {"test": first_failure[0], "traceback": first_failure[1]}
        if first_failure else None,
        "cleanup": {"ownedChildrenReaped": not ACTIVE_CHILDREN and not CLEANUP_FAILURES,
                    "remaining": len(ACTIVE_CHILDREN), "failures": CLEANUP_FAILURES},
        "binarySha256": sha(HostedRuntime.binary),
    }
    (args.output / "test-receipt.json").write_text(
        json.dumps(receipt, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    if not result.wasSuccessful() or CLEANUP_FAILURES:
        raise SystemExit(1)
