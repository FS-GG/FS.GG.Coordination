#!/usr/bin/env python3
"""Bounded filesystem/process adapters for the qualification-owned native reducer.

This module dispatches typed allowances. It does not decide admission or infer a
successful operation from a command exit or cleanup result.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tempfile
import time
from typing import Callable

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent


def _module(name, file):
    spec = importlib.util.spec_from_file_location(name, HERE / file)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


# Reuse only the maintained bounded mechanisms, never its retired admission or runner.
mechanisms = _module("fourd_native_owned_mechanisms", "qualify.py")
capsules = _module("fourd_native_capsule_mechanisms", "source_capsule.py")
HeldFile, Effects = mechanisms.HeldFile, mechanisms.Effects


class NativeIOError(RuntimeError):
    pass


def write_new(path: Path, value: bytes | object):
    raw = value if isinstance(value, bytes) else capsules.canonical(value)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(raw)
        output.flush()
        os.fsync(output.fileno())


def private_directory(path: Path):
    path.mkdir(mode=0o700, parents=False, exist_ok=True)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
        raise NativeIOError("native-private-directory-refused")


def safe_environment(home: Path, host: Path | None = None):
    result = {"PATH": "/usr/bin:/bin", "HOME": str(home), "LANG": "C.UTF-8",
              "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_TERMINAL_PROMPT": "0",
              "DOTNET_PROCESSOR_COUNT":"2"}
    if host is not None:
        result["DOTNET_ROOT"] = str(host.parent)
    return result


class NativeClient:
    """One local qualified journal, measured against the original workflow clock."""
    def __init__(self, executable: Path, work: Path, *, mode: str, started: float,
                 started_unix: int, host: Path, clock: Callable[[], float] = time.monotonic):
        self.executable, self.work, self.host = executable, work, host
        self.mode, self.started, self.started_unix = mode, started, started_unix
        self.clock, self.history, self.response, self.ordinal = clock, [], None, 0
        private_directory(work)
        self.cancelled = False
        self.session_path = work / "session.json"
        self.config = {}
        self.transient_evidence = []
        self.owned_directories = {}
        self.active_scopes = []

    @classmethod
    def load(cls, path: Path):
        value = capsules.closed_json(path.read_bytes(), 1024*1024)
        client = cls(Path(value["executable"]), path.parent, mode=value["mode"], started=value["started"],
                     started_unix=value["startedUnix"], host=Path(value["host"]))
        client.history,client.response,client.config=value["history"],value["response"],value["config"]
        return client

    def save(self):
        value={"executable":str(self.executable),"mode":self.mode,"started":self.started,
               "startedUnix":self.started_unix,"host":str(self.host),"history":self.history,
               "response":self.response,"config":self.config}
        descriptor, name = tempfile.mkstemp(prefix="session-",suffix=".new",dir=self.work)
        with os.fdopen(descriptor,"wb") as output:
            output.write(capsules.canonical(value));output.flush();os.fsync(output.fileno())
        os.replace(name,self.session_path)

    def own_directory(self,role,holder):
        self.owned_directories[role]=holder
        self.config.setdefault("ownedDirectories",{})[str(holder.path)]={"identity":holder.identity,"role":role}
        self.save()

    def retire_directory(self,holder,*,deadline):
        record=self.config.get("ownedDirectories",{}).get(str(holder.path))
        if record is None or record["identity"]!=holder.identity:
            raise NativeIOError("native-directory-retirement-ownership-refused")
        def captured(value):
            self.config["ownedDirectories"][str(value.path)]=dict(record)
            self.save()
        holder.retire(deadline=deadline,on_captured=captured)

    def elapsed(self):
        now = self.clock()
        if not math.isfinite(now) or now < self.started:
            raise NativeIOError("native-original-clock-refused")
        return math.ceil(now - self.started)

    def call(self, command: str, request: dict):
        directory=Path(tempfile.mkdtemp(prefix="call-",dir=self.work))
        input_path,output_path=directory/"request.json",directory/"response.json"
        write_new(input_path, request)
        # A separate short-lived mechanism scope treats any already-running effect
        # child as baseline; a reducer call cannot adopt or kill that sibling.
        scope = Effects()
        result = scope.run([str(self.executable), command, str(input_path), str(output_path)],
                           cwd=self.work, env=safe_environment(self.work, self.host), timeout=15,
                           total_timeout=35, capture=None, allow_cancelled=True)
        if not scope.settle() or result.returncode not in (0, 2) or not output_path.exists():
            raise NativeIOError("native-producer-response-unknown")
        value = capsules.closed_json(output_path.read_bytes(), 1024 * 1024)
        if not isinstance(value, dict):
            raise NativeIOError("native-producer-response-refused")
        return value

    def observe(self, kind: str, **facts):
        # The parent waits while an owned stage child performs its consumption
        # check. Reload its latest typed journal before appending another event.
        if self.session_path.exists():
            latest=capsules.closed_json(self.session_path.read_bytes(),1024*1024)
            self.history,self.response,self.config=latest["history"],latest["response"],latest["config"]
        elapsed=self.elapsed()
        if kind=="packet":
            facts={**facts,"evidence":{**facts["evidence"],"nowUnixSeconds":self.started_unix+elapsed}}
        observation = {"kind": kind, "elapsed": elapsed, **facts}
        value = self.call("native-transition", {
            "schema": "fsgg.fourd.native-transition-request/1", "mode": self.mode,
            "startedUnixSeconds": self.started_unix, "history": self.history, "observation": observation})
        if value.get("schema") == "fsgg.fourd.native-transition/1":
            self.history, self.response = value["history"], value
        else:
            self.response = value
        for held in self.transient_evidence:
            if not held.wipe_close():
                raise NativeIOError("native-transient-evidence-retirement-unknown")
        self.transient_evidence=[]
        self.save()
        return value

    def require_action(self, name: str):
        if self.response is None or name not in self.response.get("actions", []):
            raise NativeIOError("native-action-not-permitted")
        return self.response["state"]

    def signal(self, *_):
        self.cancelled = True
        for scope in self.active_scopes:scope.request_cancel()


class OwnedDirectory:
    """Held directory identity. A replacement path never supplies owned cleanup."""
    def __init__(self, path: Path):
        self.path = path
        self.fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        info = os.fstat(self.fd)
        if info.st_uid != os.getuid():
            os.close(self.fd)
            raise NativeIOError("native-directory-owner-refused")
        self.identity = f"{info.st_dev}:{info.st_ino}"
        self.original = (info.st_dev, info.st_ino)

    def revalidate(self):
        value = self.path.lstat()
        if not stat.S_ISDIR(value.st_mode) or (value.st_dev, value.st_ino) != self.original:
            raise NativeIOError("native-directory-replaced")
        current = os.fstat(self.fd)
        if (current.st_dev, current.st_ino) != self.original:
            raise NativeIOError("native-directory-held-identity-refused")

    @staticmethod
    def _rename_noreplace(source_fd, source, target_fd, target):
        # No fallback to rename(): a competing entry must never be overwritten.
        import ctypes
        library = ctypes.CDLL(None, use_errno=True)
        try:
            rename = library.renameat2
        except AttributeError as error:
            raise NativeIOError("native-directory-atomic-capture-unavailable") from error
        rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        rename.restype = ctypes.c_int
        if rename(source_fd, os.fsencode(source), target_fd, os.fsencode(target), 1) != 0:
            number = ctypes.get_errno()
            raise OSError(number, os.strerror(number))

    @staticmethod
    def _open_parent(path):
        descriptor = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        try:
            for component in path.parent.parts[1:]:
                child = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                dir_fd=descriptor)
                os.close(descriptor)
                descriptor = child
            return descriptor
        except BaseException:
            os.close(descriptor)
            raise

    def retire(self, *, deadline: float, clock: Callable[[], float] = time.monotonic,
               on_captured: Callable[[OwnedDirectory], None] | None = None):
        """Retire a frozen tree only after the caller has settled every writer/reader.

        Capture the pathname atomically under a new private quarantine name, then verify the
        captured inode against the held descriptor before any chmod or descent. The
        callback can persist ``holder.path`` for recovery before destructive work. A
        callback failure or expired original deadline retains the captured location.
        An old-path replacement is never traversed and keeps the result Unknown.
        """
        def within_deadline():
            now = clock()
            if not math.isfinite(deadline) or not math.isfinite(now) or now >= deadline:
                raise NativeIOError("native-directory-retirement-deadline")

        def same(info, identity):
            return stat.S_ISDIR(info.st_mode) and (info.st_dev, info.st_ino) == identity

        def empty(directory, depth=0):
            within_deadline()
            if depth > 128:
                raise NativeIOError("native-directory-retirement-depth")
            os.fchmod(directory, 0o700)
            with os.scandir(directory) as entries:
                for entry in entries:
                    within_deadline()
                    observed = os.stat(entry.name, dir_fd=directory, follow_symlinks=False)
                    if stat.S_ISDIR(observed.st_mode):
                        child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                        dir_fd=directory)
                        try:
                            held = os.fstat(child)
                            identity = held.st_dev, held.st_ino
                            if not same(observed, identity) or held.st_dev != self.original[0]:
                                raise NativeIOError("native-directory-descendant-replaced")
                            empty(child, depth + 1)
                            within_deadline()
                            if not same(os.stat(entry.name, dir_fd=directory, follow_symlinks=False), identity):
                                raise NativeIOError("native-directory-descendant-replaced")
                            within_deadline()
                            os.rmdir(entry.name, dir_fd=directory)
                        finally:
                            os.close(child)
                    else:
                        # Symlinks and special entries are removed, never followed/opened.
                        within_deadline()
                        os.unlink(entry.name, dir_fd=directory)

        parent = None
        quarantine_name = None
        original_path = Path(os.path.abspath(self.path))
        try:
            within_deadline()
            if self.fd is None or original_path.name in ("", ".", ".."):
                raise NativeIOError("native-directory-retirement-identity-refused")
            self.revalidate()
            parent = self._open_parent(original_path)
            if not same(os.fstat(self.fd), self.original):
                raise NativeIOError("native-directory-held-identity-refused")
            parent_info = os.fstat(parent)
            root_info = os.fstat(self.fd)
            if root_info.st_uid != os.getuid() or stat.S_IMODE(root_info.st_mode) & 0o077:
                raise NativeIOError("native-directory-private-capture-refused")
            # Direct /tmp and /dev/shm children retain private captured roots. A
            # writable shared parent is admissible only under its sticky protection.
            shared_writable = bool(stat.S_IMODE(parent_info.st_mode) & 0o022)
            if (parent_info.st_uid not in (0, os.getuid())
                    or shared_writable and not parent_info.st_mode & stat.S_ISVTX):
                raise NativeIOError("native-directory-parent-boundary-refused")
            # Capture under a fresh private sibling name. Moving a frozen directory
            # across parents requires changing its '..' and would need an unsafe
            # pre-capture chmod. NOREPLACE reserves the random name atomically.
            for _ in range(16):
                within_deadline()
                candidate = ".fsgg-retire-" + os.urandom(16).hex()
                try:
                    within_deadline()
                    self._rename_noreplace(parent, original_path.name, parent, candidate)
                    quarantine_name = candidate
                    break
                except FileExistsError:
                    continue
            if quarantine_name is None:
                raise NativeIOError("native-directory-quarantine-unavailable")
            self.retirement_quarantine = original_path.parent / quarantine_name
            observed = os.stat(quarantine_name, dir_fd=parent, follow_symlinks=False)
            if not same(observed, self.original):
                # Restore without overwriting a further replacement. A collision
                # retains both foreign entries without chmod or traversal.
                try:
                    self._rename_noreplace(parent, quarantine_name, parent, original_path.name)
                except OSError as error:
                    raise NativeIOError("native-directory-foreign-restore-unknown") from error
                raise NativeIOError("native-directory-capture-foreign")
            self.path = self.retirement_quarantine
            if not same(self.path.lstat(), self.original):
                raise NativeIOError("native-directory-capture-location-unknown")
            if on_captured is not None:
                try:
                    on_captured(self)
                except BaseException as error:
                    raise NativeIOError("native-directory-relocation-record-unknown") from error
            within_deadline()
            empty(self.fd)
            within_deadline()
            if not same(os.stat(quarantine_name, dir_fd=parent, follow_symlinks=False), self.original):
                raise NativeIOError("native-directory-capture-replaced")
            within_deadline()
            os.rmdir(quarantine_name, dir_fd=parent)
            try:
                os.stat(original_path.name, dir_fd=parent, follow_symlinks=False)
            except FileNotFoundError:
                return
            raise NativeIOError("native-directory-original-replaced")
        except NativeIOError:
            raise
        except OSError as error:
            raise NativeIOError("native-directory-retirement-unknown") from error
        finally:
            # Never pathname-clean an uncertain capture in a finally block.
            if parent is not None:
                os.close(parent)

    def close(self):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None


def run_gated(effects: Effects, argv: list[str], *, client: NativeClient | None,
              operation: str | None, cwd: Path, env: dict, timeout: float,
              total_timeout: float, pass_fds=(), on_started=None):
    """Intent must already be recorded. Real child birth is acknowledged before exec."""
    gate_read, gate_write = os.pipe2(os.O_CLOEXEC)
    def born(identity):
        if client is not None:
            if client.cancelled and operation not in ("cleanup","collection"):
                client.observe("cancel")
                raise NativeIOError("native-cancelled-before-child-gate")
            value = client.observe("ack", operation=operation, pid=identity.pid,
                                   startIdentity=str(identity.start_ticks))
            if not value.get("eventAccepted",False):
                raise NativeIOError("native-child-ack-refused")
        if on_started is not None:
            on_started(identity)
        if client is not None and client.cancelled and operation not in ("cleanup","collection"):
            client.observe("cancel")
            raise NativeIOError("native-cancelled-before-child-gate")
        os.write(gate_write, b"1")
    try:
        return effects.run(["/usr/bin/python3", "-I", "-B", str(HERE / "native_child.py"),
                            str(gate_read), *argv], cwd=cwd, env=env, timeout=timeout,
                           total_timeout=total_timeout, capture=None, pass_fds=(gate_read, *pass_fds),
                           allow_cancelled=operation in ("cleanup", "collection"), on_started=born)
    finally:
        os.close(gate_read); os.close(gate_write)


def git_observation(source: Path, target: Path, effects: Effects, env: dict, remaining: Callable[[], float]):
    def git(*args):
        result = effects.run(["/usr/bin/git", *args], cwd=source, env=env,
                             timeout=min(15, remaining()), total_timeout=remaining(), capture=None)
        if result.returncode:
            raise NativeIOError("native-git-observation-refused")
        return result.stdout
    revision = git("rev-parse", "HEAD").decode().strip()
    tree = git("rev-parse", "HEAD^{tree}").decode().strip()
    if git("status", "--porcelain", "--untracked-files=all"):
        raise NativeIOError("native-git-dirty-refused")
    commit = git("cat-file", "commit", revision)
    pending, objects, seen = [tree], [], set()
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        seen.add(current)
        raw = git("cat-file", "tree", current)
        objects.append({"sha": current, "base64": base64.b64encode(raw).decode()})
        listing = git("ls-tree", "-z", current)
        for item in listing.split(b"\0"):
            if item:
                metadata, _ = item.split(b"\t", 1)
                mode, kind, oid = metadata.split()
                if kind == b"tree":
                    pending.append(oid.decode())
        if len(seen) > 512:
            raise NativeIOError("native-git-tree-bound-refused")
    held=HeldFile.create(target,capsules.canonical({"revision": revision, "tree": tree,
                       "rawCommitBase64": base64.b64encode(commit).decode(), "treeObjects": objects}))
    return revision, tree, held


def acquire_capsule(packet: dict, key: bytes, private: Path, effects: Effects, *,
                    deadline: float, client: NativeClient, read: Callable):
    """Actual capsule/raw-commit IO; expected identities originate in the typed root join."""
    client.require_action("acquire-capsule")
    credentials, source = private / "credentials", private / "source"
    private_directory(credentials)
    credential_owner=OwnedDirectory(credentials)
    client.own_directory("credentials",credential_owner)
    capsule = packet["sourceCapsule"]
    held = []
    plaintext_path = credentials / "source.snapshot.json"
    plaintext_output=Path(f"/proc/self/fd/{credential_owner.fd}/source.snapshot.json")
    original = None
    cleanup_complete = False
    env = safe_environment(private / "home")
    def create_credential(name,data):
        item=HeldFile.create(Path(f"/proc/self/fd/{credential_owner.fd}/{name}"),data)
        item.path=credentials/name
        client.config.setdefault("ownedFiles",{})[str(item.path)]={"device":item.device,"inode":item.inode}
        client.save()
        return item
    def open_plaintext():
        descriptor=os.open("source.snapshot.json",os.O_RDWR|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=credential_owner.fd)
        info=os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or info.st_nlink!=1 or stat.S_IMODE(info.st_mode)!=0o600:
            os.close(descriptor);raise NativeIOError("native-held-plaintext-refused")
        return HeldFile(plaintext_path,descriptor,info.st_dev,info.st_ino)
    def plaintext_exists():
        try:os.stat("source.snapshot.json",dir_fd=credential_owner.fd,follow_symlinks=False);return True
        except FileNotFoundError:return False
    def remaining():
        value = deadline-time.monotonic()
        if value <= 36 or effects.cancelled:
            raise NativeIOError("native-source-deadline-or-cancellation")
        return value
    try:
        metadata = read("release", capsule["releaseId"], 256*1024, deadline)
        metadata_path = private / "capsule-release.json"
        write_new(metadata_path, metadata)
        raw = read("asset", capsule["assetId"], 24*1024*1024, deadline)
        encrypted = create_credential("source.capsule.json", raw); held.append(encrypted)
        result = client.observe("capsule", evidence={"releasePath":str(metadata_path),"assetPath":str(encrypted.path),
                               "sealerPath":str(HERE/"seal_native_custody.mjs")})
        if result.get("refusal"):
            raise NativeIOError("native-capsule-join-refused")
        client.require_action("expose-recipient-key")
        credential_owner.revalidate()
        recipient = create_credential("recipient.pem", key); held.append(recipient)
        public = effects.run(["/usr/bin/openssl", "pkey", "-in", f"/proc/self/fd/{recipient.fd}", "-pubout"],
                             cwd=private, env=env, timeout=min(15, remaining()), total_timeout=remaining(),
                             capture=None, pass_fds=(recipient.fd,))
        if public.returncode:
            raise NativeIOError("native-key-readback-unknown")
        identity = f"{recipient.device}:{recipient.inode}"
        result = client.observe("key-exposed", resource="recipient-key", identity=identity,
                                publicKeySha256=hashlib.sha256(public.stdout).hexdigest())
        if result.get("refusal"):
            raise NativeIOError("native-key-binding-refused")
        client.require_action("acquire-source")
        result = client.observe("intent", operation="source", resource="source-scope", identity="source-scope-1")
        if result.get("refusal"):
            raise NativeIOError("native-source-intent-refused")
        capsules.validate_outer_capsule(raw, packet["runNonce"]+"-source", packet["product"]["revision"], capsule["descriptorSha256"])
        if hashlib.sha256((HERE / "seal_native_custody.mjs").read_bytes()).hexdigest() != capsule["descriptor"]["sealerSha256"]:
            raise NativeIOError("native-sealer-bytes-refused")
        result = run_gated(effects, ["/usr/bin/node", str(HERE / "seal_native_custody.mjs"), "unseal",
            f"/proc/self/fd/{encrypted.fd}", f"/proc/self/fd/{recipient.fd}", str(plaintext_output),
            packet["runNonce"]+"-source", packet["product"]["revision"], capsule["descriptorSha256"]],
            client=client, operation="source", cwd=private, env=env, timeout=min(120, remaining()-36),
            total_timeout=remaining(), pass_fds=(encrypted.fd,recipient.fd,credential_owner.fd))
        if result.returncode:
            raise NativeIOError("native-unseal-child-failed")
        plaintext = open_plaintext(); held.append(plaintext)
        client.config.setdefault("ownedFiles",{})[str(plaintext.path)]={"device":plaintext.device,"inode":plaintext.inode}
        client.save()
        raw_plaintext = plaintext.read(capsules.MAX_PLAINTEXT)
        descriptor = capsule["descriptor"]
        if len(raw_plaintext) != descriptor["plaintextBytes"] or hashlib.sha256(raw_plaintext).hexdigest() != descriptor["plaintextSha256"]:
            raise NativeIOError("native-plaintext-bytes-refused")
        expected = {"sourceSha": packet["product"]["revision"], "sourceTree": packet["product"]["tree"],
                    "inventorySha256": packet["product"]["inventorySha256"]}
        def runner(argv, cwd, timeout):
            response = effects.run(argv, cwd=cwd, env=env, timeout=min(timeout, remaining()-36),
                                   total_timeout=remaining(), capture=None)
            if response.returncode:
                raise NativeIOError("native-git-reconstruction-child-failed")
            return response.stdout
        previous = os.umask(0o077)
        try:
            capsules.reconstruct(raw_plaintext, source, expected, runner=runner, deadline_monotonic=deadline,
                                 on_created=lambda path:client.own_directory("source",OwnedDirectory(path)))
        finally:
            os.umask(previous)
        return source
    except BaseException as error:
        original = error
        raise
    finally:
        # Child-created plaintext may exist even when unseal never acknowledged success.
        try: settled = effects.settle(deadline=deadline)
        except BaseException: settled = False
        if plaintext_exists() and all(item.path != plaintext_path for item in held):
            try:
                item=open_plaintext();held.append(item)
                client.config.setdefault("ownedFiles",{})[str(item.path)]={"device":item.device,"inode":item.inode}
                client.save()
            except BaseException: settled = False
        cleanup_complete = settled
        if settled:
            for item in reversed(held):
                cleanup_complete = item.wipe_close() and cleanup_complete
            credential_owner.close()
        # Unknown child settlement never authorizes wiping plaintext underneath readers.
        if not cleanup_complete and original is None:
            raise NativeIOError("native-source-retirement-unknown")
