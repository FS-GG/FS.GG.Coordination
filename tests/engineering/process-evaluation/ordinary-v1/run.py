"""Outer owner for serial ordinary observations. Requires a root-scheduled slot.
Kills only its own newly created evaluator session; fixture children also self-expire.
No cgroup, privileged controller, foreign PID signaling or containment acceptance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--sink-failure-only', action='store_true')
parser.add_argument('--dotnet', type=Path, default=Path('/usr/share/dotnet/dotnet'))
args = parser.parse_args()
base = Path(__file__).resolve().parent
pins = json.loads((base / 'inputs.json').read_text())
python = Path(sys.executable).resolve()
assert hashlib.sha256(python.read_bytes()).hexdigest() == pins['fixtureRuntime']['executable']['sha256']
assert hashlib.sha256(args.dotnet.read_bytes()).hexdigest() == pins['runtime']['host']['sha256']
dll = base / 'bin/Debug/net10.0/OrdinaryEvaluation.dll'
assert dll.is_file(), 'Schedule and build evaluator first'
manifest = json.loads((base / 'build-manifest.json').read_text())
for artifact in manifest['artifacts']:
    assert hashlib.sha256((dll.parent / artifact['name']).read_bytes()).hexdigest() == artifact['sha256']
for source in manifest['fixtureSources']:
    assert hashlib.sha256((base / source['name']).read_bytes()).hexdigest() == source['sha256']
for runtime in pins['runtime']['files']:
    assert hashlib.sha256(Path(runtime['path']).read_bytes()).hexdigest() == runtime['sha256']
args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
results = []
cases = [case for case in pins['cases'] if not args.sink_failure_only or case['id'] == 'ordinary-dual']
mode = 'sink-failure' if args.sink_failure_only else 'normal'
collection_started = time.monotonic()
for backend in ('baseline', 'cliwrap', 'processkit'):
    for case in cases:
        if args.sink_failure_only and time.monotonic() - collection_started >= 15:
            raise RuntimeError('sink observation collection budget exhausted; no next launch')
        started = time.monotonic()
        registry = args.output / (backend + '-' + case['id'] + '-owned')
        registry.mkdir(mode=0o700)
        environment = os.environ.copy()
        environment['FSGG_PROC_REGISTRY'] = str(registry)
        process = subprocess.Popen([str(args.dotnet), str(dll), backend, case['id'],
                                    str(python), str(base / 'fixture.py'), mode],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   start_new_session=True, cwd=base, env=environment)
        selector = selectors.DefaultSelector()
        selector.register(process.stdout, selectors.EVENT_READ, 'stdout')
        selector.register(process.stderr, selectors.EVENT_READ, 'stderr')
        retained = {'stdout': bytearray(), 'stderr': bytearray()}
        guard = 'none'
        try:
            while selector.get_map():
                if time.monotonic() - started >= 3:
                    guard = 'outer-deadline'
                    break
                for key, _ in selector.select(timeout=0.05):
                    block = os.read(key.fileobj.fileno(), 4096)
                    if not block:
                        selector.unregister(key.fileobj)
                        continue
                    room = 16384 - sum(map(len, retained.values()))
                    retained[key.data].extend(block[:max(0, room)])
                    if len(block) > room:
                        guard = 'outer-output-overflow'
                        break
                if guard != 'none':
                    break
        finally:
            selector.close()
            # The session is created by this launch; no preexisting process is a target.
            if process.poll() is None or guard != 'none':
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.wait(timeout=2)
            process.stdout.close()
            process.stderr.close()
        try:
            observation = json.loads(retained['stdout'])
        except (UnicodeError, ValueError):
            observation = None
        report = {'backend': backend, 'case': case['id'], 'evaluationMode': mode, 'outerGuard': guard,
                  'evaluatorExitCode': process.returncode,
                  'elapsedMs': int((time.monotonic() - started) * 1000),
                  'observation': observation,
                  'stderr': retained['stderr'].decode(errors='replace'),
                  'qualification': 'not-established'}
        # One bounded natural-expiry window prevents the held writer from overlapping next case.
        remaining = 2 - (time.monotonic() - started)
        if remaining > 0:
            time.sleep(remaining)
        known = []
        for identity_file in sorted(registry.glob('*.json')):
            identity = json.loads(identity_file.read_text())
            try:
                fields = Path('/proc', str(identity['pid']), 'stat').read_text().rsplit(')', 1)[1].split()
                status = 'different-generation' if fields[19] != identity['startTicks'] else \
                    'zombie-unreaped' if fields[0] == 'Z' else 'still-live'
            except FileNotFoundError:
                status = 'absent'
            except (PermissionError, OSError, IndexError):
                status = 'unknown'
            known.append(dict(identity, observed=status))
        report['knownFixtureGenerations'] = known
        report['descendantCoverage'] = 'registered fixtures only; library helpers and complete descendant/reaping coverage unknown'
        path = args.output / (backend + '-' + case['id'] + '.json')
        path.write_text(json.dumps(report, indent=2) + '\n')
        path.chmod(0o600)
        results.append(report)
        if guard != 'none' or any(row['observed'] != 'absent' for row in known) or \
                (observation is None or (observation.get('launch') in ('started', 'submitted') and not known)):
            raise RuntimeError('outer guard or registered-generation uncertainty; affected collection stopped')
(args.output / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps({'observations': len(results), 'output': str(args.output),
                  'qualification': 'not-established'}))
