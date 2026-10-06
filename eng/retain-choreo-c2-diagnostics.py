#!/usr/bin/env python3
"""Retain filtered failure text only; never qualify or execute a model."""
import json
import os
from pathlib import Path
import re
import stat
import sys
import time

MAX_ENTRIES = 128
MAX_FILES = 16
FILE_BYTES = 8192
MAX_OUTPUT = 192 * 1024
SECONDS = 2
SENSITIVE = re.compile(r'(?i)authorization|bearer|password|secret|token|credential|private[-_ ]?key|https?://[^\s]*(?:@|\?)')
ANSI = re.compile(r'\x1b\[[0-?]*[ -/]*[@-~]')


def filtered(raw, scratch):
    text = ANSI.sub('', raw.decode('utf-8', errors='replace'))
    lines = []
    for line in text.splitlines():
        if SENSITIVE.search(line):
            lines.append('[sensitive line omitted]')
        else:
            line = line.replace(str(scratch), '<scratch>')
            lines.append(''.join(c if c.isprintable() or c == '\t' else '?' for c in line))
    return '\n'.join(lines)


def capture(scratch, destination, status, *, clock=time.monotonic, max_output=MAX_OUTPUT):
    """Bound enumeration, nofollow reads, head/tail retention and output bytes."""
    if not 1024 <= max_output <= MAX_OUTPUT:
        raise ValueError('diagnostic output profile')
    scratch = Path(scratch).absolute()
    destination = Path(destination).absolute()
    end = clock() + SECONDS
    if not stat.S_ISDIR(scratch.lstat().st_mode) or status == 0:
        raise ValueError('not a failed scratch directory')
    # Caller-created output belongs to the existing fragment, never scratch.
    if destination == scratch or scratch in destination.parents:
        raise ValueError('diagnostic destination overlaps scratch')
    parents = [destination.parent, *destination.parent.parents]
    if any(not stat.S_ISDIR(p.lstat().st_mode) for p in parents):
        raise ValueError('diagnostic parent is not a plain directory')
    records = []
    coverage = []
    pending = [(scratch, 0)]
    visited = 0
    while pending and visited < MAX_ENTRIES and len(records) < MAX_FILES and clock() < end:
        directory, depth = pending.pop(0)
        # Do not follow directory links, even after enumeration.
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            with os.scandir(fd) as entries:
                for entry in entries:
                    visited += 1
                    if visited > MAX_ENTRIES or clock() >= end:
                        coverage.append('enumeration-bound')
                        break
                    mode = entry.stat(follow_symlinks=False).st_mode
                    relative = (directory / entry.name).relative_to(scratch)
                    if stat.S_ISDIR(mode):
                        if depth < 6 and (relative.parts[0] == '_apalache-out'):
                            pending.append((directory / entry.name, depth + 1))
                        continue
                    # Only textual command logs and compiler/server logs; no
                    # model, environment dump, protobuf, JSON or credential files.
                    if not stat.S_ISREG(mode) or not (entry.name.endswith('.log') or entry.name == 'log.txt' or re.fullmatch(r'.+\.log\.attempt-[12]', entry.name)):
                        continue
                    if len(records) >= MAX_FILES:
                        coverage.append('file-count-bound')
                        break
                    f = os.open(entry.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
                    try:
                        before = os.fstat(f)
                        if not stat.S_ISREG(before.st_mode):
                            raise ValueError('log is not regular')
                        first = os.read(f, FILE_BYTES // 2)
                        if before.st_size > FILE_BYTES // 2:
                            os.lseek(f, max(FILE_BYTES // 2, before.st_size - FILE_BYTES // 2), os.SEEK_SET)
                            last = os.read(f, FILE_BYTES // 2)
                        else:
                            last = b''
                        after = os.fstat(f)
                        stable = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) == (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
                    finally:
                        os.close(f)
                    records.append({'source': 'compiler-server' if relative.parts[0] == '_apalache-out' else 'command',
                                    'ordinal': len(records), 'observedBytes': before.st_size,
                                    'truncated': before.st_size > FILE_BYTES,
                                    'stable': stable,
                                    'text': filtered(first + (b'\n[head/tail omitted]\n' if before.st_size > FILE_BYTES else b'') + last, scratch)})
        finally:
            os.close(fd)
    if not records:
        coverage.append('no-text-logs-observed')
    if pending or visited >= MAX_ENTRIES or len(records) >= MAX_FILES or clock() >= end:
        coverage.append('collection-bounded')
    document = {'schema': 'fsgg.choreo.failure-diagnostics/1', 'originalExitCode': status,
                'outcome': 'failed', 'logs': records, 'omittedCoverage': sorted(set(coverage)),
                'filter': 'sensitive-lines-omitted; textual-logs-only; no-environment-capture'}
    raw = (json.dumps(document, ensure_ascii=True, indent=2) + '\n').encode()
    while len(raw) > max_output and records:
        records.pop()
        document['omittedCoverage'] = sorted(set(document['omittedCoverage'] + ['output-byte-bound']))
        raw = (json.dumps(document, ensure_ascii=True, indent=2) + '\n').encode()
    if len(raw) > max_output:
        raise ValueError('diagnostic output bound')
    destination.mkdir(mode=0o700)
    f = os.open(destination / 'failure.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(f, 'wb') as output:
        output.write(raw)
    return document


if __name__ == '__main__':
    try:
        capture(sys.argv[1], sys.argv[2], int(sys.argv[3]),
                max_output=int(sys.argv[4]) if len(sys.argv) == 5 else MAX_OUTPUT)
    except Exception:
        # Never echo errors, paths, input bytes or credentials into public logs.
        sys.exit(2)
