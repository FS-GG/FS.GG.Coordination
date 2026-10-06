#!/usr/bin/env python3
"""Export only the canonical managed lease bytes; no compiler or native export."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import xml.etree.ElementTree as ET

PROJECT = "src/FS.GG.Coordination.Orchestration.Execution"
PATHS = tuple(f"{PROJECT}/CustodyProcessLease.{ext}" for ext in ("fsi", "fs"))
NAMESPACE = "FS.GG.Coordination.Orchestration.Execution"
MANIFEST = f"{PROJECT}/Custody/lease-source-manifest.json"
CAP = 16384


def encode(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":")) + "\n").encode("ascii")


def read_regular(root, relative):
    root = Path(root).absolute()
    if root.is_symlink():
        raise ValueError("symlink root")
    path = root / relative
    for parent in path.parents:
        if parent == root:
            break
        if parent.is_symlink():
            raise ValueError("symlink ancestor")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_mode & 0o111 or before.st_size > CAP:
            raise ValueError("source mode or size")
        data = os.read(fd, CAP + 1)
        after = os.fstat(fd)
        named = os.stat(path, follow_symlinks=False)
        identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns)
        if len(data) != before.st_size or identity(before) != identity(after) or identity(after) != identity(named):
            raise ValueError("source changed")
        return data
    finally:
        os.close(fd)


def manifest(root):
    root = Path(root).absolute()
    files = []
    for name in PATHS:
        data = read_regular(root, name)
        if not data.decode("utf-8").startswith(f"namespace {NAMESPACE}\n"):
            raise ValueError("source namespace")
        files.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    if sum(row["bytes"] for row in files) > CAP:
        raise ValueError("aggregate size")
    project = ET.fromstring(read_regular(root, f"{PROJECT}/FS.GG.Coordination.Orchestration.Execution.fsproj"))
    if project.findtext(".//TargetFramework") != "net10.0":
        raise ValueError("target framework")
    return {"schema": "fsgg.custody-lease-source/1", "producerRepository": "FS-GG/FS.GG.Coordination", "namespace": NAMESPACE, "targetFramework": "net10.0", "files": files}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        data = encode(manifest(args.root))
        output = args.root / MANIFEST
        if args.check:
            if read_regular(args.root, MANIFEST) != data:
                raise ValueError("manifest differs")
        else:
            if output.is_symlink():
                raise ValueError("symlink manifest")
            temporary = output.with_suffix(".json.tmp")
            fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o644)
            try:
                with os.fdopen(fd, "wb") as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, output)
            finally:
                temporary.unlink(missing_ok=True)
    except (OSError, ValueError, UnicodeError, ET.ParseError) as error:
        parser.exit(1, f"lease-source-manifest refused: {error}\n")


if __name__ == "__main__":
    main()
