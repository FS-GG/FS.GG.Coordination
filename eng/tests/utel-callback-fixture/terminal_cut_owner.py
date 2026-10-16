#!/usr/bin/env python3
"""One inert managed/Python terminal-cut rendezvous; never a custody receipt."""
import base64
import hashlib
import importlib.util
import json
import os
import pathlib
import sys
import time

OBSERVER_SHA256 = 'fd1c6ab7a6d53fa248e2080e29800f273920484eb079bb141aa57288bb2604da'
PROFILE_SHA256 = 'bb689abf8a63673b8cedd743756f45187996ef67dbcfe192e1f385f450fd57f0'
PRODUCER_REVISION = 'c9525d5f4afaadcbb69f8f71c25aee0939b32b7c'
MAXIMUM_RECORD = 8192


def require(value, reason):
    if not value:
        raise ValueError(reason)


def private_record(path, row):
    raw = json.dumps(row, sort_keys=True, separators=(',', ':')).encode('ascii')
    require(len(raw) <= MAXIMUM_RECORD, 'cut record exceeds bound')
    require(not path.exists(), 'cut record already exists')
    temporary = path.with_name(path.name + '.partial')
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(descriptor, 'wb', closefd=False) as output:
            require(output.write(raw) == len(raw), 'cut record short write')
            output.flush()
            os.fsync(output.fileno())
    finally:
        os.close(descriptor)
    os.replace(temporary, path)


def closed_release(path, nonce, binding, request):
    raw = path.read_bytes()
    require(len(raw) <= MAXIMUM_RECORD, 'cut release exceeds bound')
    row = json.loads(raw)
    require(type(row) is dict and set(row) == {'nonce', 'binding', 'request', 'ordinal', 'stage'}
            and row == {'nonce': nonce, 'binding': binding, 'request': request,
                        'ordinal': 6, 'stage': 'readback'}, 'cut release differs')


class DigestingInput:
    def __init__(self, source):
        self.source = source
        self.digest = hashlib.sha256()

    def read(self, count):
        raw = self.source.read(count)
        self.digest.update(raw)
        return raw


def load_owner(source):
    folder = source / 'tests/release-successor-authentication'
    observer = folder / 'slot-copy-lifetime-tests.py'
    profile = folder / 'callback_pair_profile.py'
    require(hashlib.sha256(observer.read_bytes()).hexdigest() == OBSERVER_SHA256,
            'copy observer source differs')
    require(hashlib.sha256(profile.read_bytes()).hexdigest() == PROFILE_SHA256,
            'copy profile source differs')
    spec = importlib.util.spec_from_file_location('utel_slot_copy_observer', observer)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.pair.select(source, module.pair.SLOT_PROFILE)
    return module


def execute(source, binding, clock, result, ready, release, after, nonce, managed_pid):
    require(len(nonce) == 32 and all(char in '0123456789abcdef' for char in nonce),
            'test execution nonce differs')
    require(type(managed_pid) is int and managed_pid > 0, 'managed process identity differs')
    module = load_owner(source)
    entry = module.entry
    digest = entry.provider.validate_auth_binding(binding)
    input_stream = DigestingInput(sys.stdin.buffer)
    backend = entry.slots.ControlledSlotBackend()
    census = module.Census(backend)
    original_stage = entry.slots.ControlledSlotBackend._stage
    observed = False

    def stage(subject, name):
        nonlocal observed
        if name == 'readback' and 'phase-6' in subject.stores:
            require(not observed and subject is backend, 'duplicate terminal cut')
            observed = True
            census.stage(subject, name)
            first = census.cut
            require(first is not None and len(first) == 16, 'copy census incomplete')
            request_digest = input_stream.digest.hexdigest()
            header = {'nonce': nonce, 'binding': digest, 'request': request_digest,
                      'ordinal': 6, 'stage': 'readback', 'producerRevision': PRODUCER_REVISION,
                      'managedPid': managed_pid, 'pythonPid': os.getpid()}
            private_record(ready, dict(header, objects=first))
            deadline = time.monotonic() + 15
            while not release.exists() and time.monotonic() < deadline:
                time.sleep(0.005)
            require(release.is_file(), 'terminal cut release missing')
            closed_release(release, nonce, digest, request_digest)
            census.stage(subject, name)
            require(census.cut == first, 'Python objects changed while held')
            private_record(after, dict(header, objects=census.cut, unchanged=True))
        return original_stage(subject, name)

    entry.slots.ControlledSlotBackend._stage = stage
    try:
        evidence = entry.execute(binding, clock, input_stream, sys.stdout.buffer, backend)
        require(observed and after.is_file(), 'terminal cut was not completed')
        raw = entry.encode_evidence(evidence)
        with result.open('xb') as output:
            require(output.write(raw) == len(raw), 'slot result short write')
            output.flush()
    finally:
        entry.slots.ControlledSlotBackend._stage = original_stage


def main():
    try:
        require(len(sys.argv) == 10, 'terminal cut arguments differ')
        source, result, ready, release, after = map(pathlib.Path,
            (sys.argv[1], sys.argv[4], sys.argv[5], sys.argv[6], sys.argv[7]))
        require(all(path.is_absolute() for path in (source, result, ready, release, after))
                and len({result, ready, release, after}) == 4
                and all(not path.exists() for path in (result, ready, release, after)),
                'terminal cut paths differ')
        binding = json.loads(base64.b64decode(sys.argv[2], validate=True))
        clock = json.loads(base64.b64decode(sys.argv[3], validate=True))
        execute(source, binding, clock, result, ready, release, after, sys.argv[8], int(sys.argv[9]))
        return 0
    except Exception:
        print('terminal-cut-owner-refused', file=sys.stderr)
        return 3


if __name__ == '__main__':
    raise SystemExit(main())
