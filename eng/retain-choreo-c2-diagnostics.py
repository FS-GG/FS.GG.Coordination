#!/usr/bin/env python3
"""Canonical owned-command adapter and bounded diagnostic reporting.

Diagnostic text and command exit never replace required model-checking evidence.
"""
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
import hashlib
import importlib.util
import signal
import math

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


def owned_fd_targets(row, observe_birth, additional_failures=None, *, terminal_probe=None,
                     denial_observations=None, scan_observation=None):
    """Denied access needs fresh terminal original-birth proof; live/unknown refuse."""
    def identity(value):
        return None if value is None else {k:value[k] for k in ('pid','start','pgid','state')}
    def terminal_after_denial(error, operation):
        observation = {'operation':operation, 'before':identity(row),
                       'freshBeforeTerminalPoll':None, 'terminalPollAttempted':False,
                       'terminalExit':None, 'freshAfterTerminalPoll':None,
                       'observationFailure':None}
        if denial_observations is not None:
            if len(denial_observations['entries']) < 6:
                denial_observations['entries'].append(observation)
            else:
                denial_observations['omitted'] += 1
        def fresh_birth(field):
            try:
                fresh = observe_birth(row['pid'])
                observation[field] = identity(fresh)
                return fresh
            except Exception as observation_error:
                observation['observationFailure'] = failure_record(observation_error)
                if additional_failures is not None:
                    additional_failures.append(failure_record(observation_error))
                raise error from observation_error
        def terminal(fresh):
            # A replacement only proves this original birth is absent. It never
            # authenticates or grants signal authority over the replacement.
            if fresh is None or fresh['start'] != row['start']: return True
            if fresh['pgid'] != row['pgid']:
                group_error = ValueError('server group changed during denied FD observation')
                observation['observationFailure'] = failure_record(group_error)
                if additional_failures is not None:
                    additional_failures.append(failure_record(group_error))
                raise error from group_error
            return fresh['state'] == 'Z'
        if terminal(fresh_birth('freshBeforeTerminalPoll')): return []
        # Only the caller-authenticated direct-child Popen may supply this probe.
        # One nonblocking poll plus a new birth read; no retries, sleeps or waiver
        # for a still-live or unreadable original birth.
        if terminal_probe is not None:
            observation['terminalPollAttempted'] = True
            try:
                code = terminal_probe()
                if code is not None and type(code) is not int: raise ValueError('original Popen terminal status type')
                observation['terminalExit'] = code
            except Exception as poll_error:
                observation['observationFailure'] = failure_record(poll_error)
                if additional_failures is not None:
                    additional_failures.append(failure_record(poll_error))
                raise error from poll_error
            if code is not None and terminal(fresh_birth('freshAfterTerminalPoll')): return []
        raise error
    try:
        entries = list((Path('/proc') / str(row['pid']) / 'fd').iterdir())
    except FileNotFoundError:
        if scan_observation is not None: scan_observation['disposition'] = 'directory-missing'
        return []
    except PermissionError as error:
        if scan_observation is not None: scan_observation['disposition'] = 'directory-denied'
        return terminal_after_denial(error, 'directory')
    if len(entries) > 256: raise ValueError('server FD cap')
    targets = []
    for entry in entries:
        try: targets.append(os.readlink(entry))
        except FileNotFoundError:
            if scan_observation is not None: scan_observation['missingReadlinks'] += 1
            continue
        except PermissionError as error:
            if scan_observation is not None: scan_observation['disposition'] = 'readlink-denied'
            return terminal_after_denial(error, 'readlink')
    if scan_observation is not None:
        sockets = sorted({target[8:-1] for target in targets
                          if target.startswith('socket:[') and target.endswith(']')
                          and target[8:-1].isascii() and target[8:-1].isdigit()
                          and len(target[8:-1]) <= 20})
        scan_observation['disposition'] = 'scanned'
        scan_observation['socketInodes'] = {'values':sockets[:6], 'omitted':max(0,len(sockets)-6)}
    return targets


def lifecycle_summary(value):
    """Bound identity-only lifecycle fields; never project arbitrary result/input data."""
    def identities(rows, keys):
        if rows is None: return None
        if not isinstance(rows, list): raise ValueError('lifecycle identity shape')
        selected = []
        for row in rows[:32]:
            if not isinstance(row, dict): raise ValueError('lifecycle identity row')
            item = {}
            for key in keys:
                entry = row.get(key)
                if key == 'state':
                    if type(entry) is not str or len(entry) != 1: raise ValueError('lifecycle state')
                elif type(entry) is not int or entry < 0:
                    raise ValueError('lifecycle identity')
                item[key] = entry
            selected.append(item)
        return {'members':selected, 'omittedMembers':max(0,len(rows)-32)}
    observation = (value.get('ownerReceipt') or {}).get('exitObservation')
    projected = None
    if observation is not None:
        if not isinstance(observation, dict): raise ValueError('exit observation shape')
        code = observation.get('code')
        if type(code) is not int: raise ValueError('exit observation code')
        omitted = observation.get('omittedMembers')
        if omitted is not None and (type(omitted) is not int or omitted < 0): raise ValueError('exit omission shape')
        projected = {'code':code, 'membership':identities(observation.get('members'), ('pid','start','pgid','state')),
                     'omittedMembers':omitted}
    stops = value.get('originalServerStopRequests')
    projected_stops = None
    if stops is not None:
        if not isinstance(stops,list): raise ValueError('stop requests shape')
        projected_stops = []
        for stop in stops[:6]:
            identity = identities([stop], ('pid','start','signal'))['members'][0]
            disposition = stop.get('disposition')
            if disposition not in ('sent','already-terminal'): raise ValueError('stop disposition')
            projected_stops.append(dict(identity, disposition=disposition))
    denials = value.get('fdDenialObservations')
    projected_denials = None
    if denials is not None:
        if (not isinstance(denials,dict) or not isinstance(denials.get('entries'),list)
                or type(denials.get('omitted')) is not int or denials['omitted'] < 0):
            raise ValueError('FD denial observation shape')
        projected_denials = {'entries':[], 'omitted':denials['omitted']+max(0,len(denials['entries'])-6)}
        for denial in denials['entries'][:6]:
            if denial.get('operation') not in ('directory','readlink') or type(denial.get('terminalPollAttempted')) is not bool:
                raise ValueError('FD denial operation')
            code = denial.get('terminalExit')
            if code is not None and type(code) is not int: raise ValueError('FD denial terminal exit')
            item = {k:denial.get(k) for k in ('operation','terminalPollAttempted','terminalExit')}
            for key in ('before','freshBeforeTerminalPoll','freshAfterTerminalPoll'):
                row = denial.get(key)
                item[key] = None if row is None else identities([row],('pid','start','pgid','state'))['members'][0]
            error = denial.get('observationFailure')
            if error is not None:
                if not isinstance(error,dict): raise ValueError('FD denial observation failure')
                error = {k:filtered(str(error.get(k,'')).encode()[:512],Path('/proc')) for k in ('type','message')}
            item['observationFailure'] = error
            projected_denials['entries'].append(item)
    endpoint = value.get('endpointObservations')
    projected_endpoint = None
    if endpoint is not None:
        if (not isinstance(endpoint,dict) or not isinstance(endpoint.get('entries'),list)
                or type(endpoint.get('omitted')) is not int or endpoint['omitted'] < 0):
            raise ValueError('endpoint observation shape')
        projected_endpoint = {'entries':[], 'omitted':endpoint['omitted']+max(0,len(endpoint['entries'])-2)}
        for entry in endpoint['entries'][-2:]:
            if (entry.get('event') not in ('after-birth','poll','child-exit-observed','before-success')
                    or type(entry.get('attempt')) is not int or entry['attempt'] not in (1,2)
                    or entry.get('disposition') not in ('pending','changed','proved','refused')):
                raise ValueError('endpoint observation event')
            item = {k:entry[k] for k in ('event','attempt','disposition')}
            for key in ('beforeInodes','afterInodes','matchedInodes'):
                snapshot = entry.get(key)
                if snapshot is None: item[key] = None; continue
                if (not isinstance(snapshot,dict) or not isinstance(snapshot.get('values'),list)
                        or type(snapshot.get('omitted')) is not int or snapshot['omitted'] < 0
                        or any(type(x) is not str or not x.isascii() or not x.isdigit() or len(x)>20 for x in snapshot['values'])):
                    raise ValueError('endpoint inode snapshot')
                item[key] = {'values':snapshot['values'][:6], 'omitted':snapshot['omitted']+max(0,len(snapshot['values'])-6)}
            for key in ('beforeMembers','afterMembers'):
                snapshot = entry.get(key)
                if snapshot is None: item[key] = None; continue
                if (not isinstance(snapshot,dict) or not isinstance(snapshot.get('members'),list)
                        or type(snapshot.get('omittedMembers')) is not int or snapshot['omittedMembers'] < 0):
                    raise ValueError('endpoint membership snapshot')
                item[key] = identities(snapshot['members'][:6], ('pid','start','pgid','state'))
                item[key]['omittedMembers'] = snapshot['omittedMembers']+max(0,len(snapshot['members'])-6)
            error = entry.get('failure')
            if error is not None:
                if not isinstance(error,dict): raise ValueError('endpoint failure shape')
                error = {k:filtered(str(error.get(k,'')).encode()[:512],Path('/proc')) for k in ('type','message')}
            item['failure'] = error
            scans = entry.get('fdScans', [])
            if not isinstance(scans,list) or len(scans)>6: raise ValueError('endpoint FD scan population')
            item['fdScans'] = []
            for scan in scans:
                if (scan.get('disposition') not in ('pending','scanned','directory-missing','directory-denied','readlink-denied')
                        or type(scan.get('missingReadlinks')) is not int or not 0<=scan['missingReadlinks']<=256
                        or type(scan.get('afterMismatchObserved')) is not bool):
                    raise ValueError('endpoint FD scan shape')
                selected = {k:scan[k] for k in ('disposition','missingReadlinks','afterMismatchObserved')}
                for key in ('before','afterMismatchBirth'):
                    row=scan.get(key)
                    selected[key]=None if row is None else identities([row],('pid','start','pgid','state'))['members'][0]
                sockets=scan.get('socketInodes')
                if (not isinstance(sockets,dict) or not isinstance(sockets.get('values'),list)
                        or len(sockets['values'])>6 or type(sockets.get('omitted')) is not int or sockets['omitted']<0
                        or any(type(x) is not str or not x.isascii() or not x.isdigit() or len(x)>20 for x in sockets['values'])):
                    raise ValueError('endpoint FD socket snapshot')
                selected['socketInodes']={'values':list(sockets['values']),'omitted':sockets['omitted']}
                item['fdScans'].append(selected)
            projected_endpoint['entries'].append(item)
    owner_leader = (value.get('ownerReceipt') or {}).get('leader')
    return {'ownerLeader':None if owner_leader is None else identities([owner_leader],('pid','start','pgid','state'))['members'][0],
            'fdDenials':projected_denials, 'endpointObservations':projected_endpoint,
            'serverBirths':identities(value.get('originalServerBirths'), ('pid','start')),
            'adapterExitMembership':identities(value.get('exitMembers'), ('pid','start')),
            'ownerExitObservation':projected, 'stopRequests':projected_stops,
            'omittedStopRequests':None if stops is None else max(0,len(stops)-6)}

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
                    # Only logs and this adapter's typed failure summary; never
                    # copy arbitrary JSON, command inputs, models or credentials.
                    command_report = bool(re.fullmatch(r'.+\.log\.attempt-[12]\.command\.json', entry.name))
                    if not stat.S_ISREG(mode) or not (command_report or entry.name.endswith('.log') or entry.name == 'log.txt' or re.fullmatch(r'.+\.log\.attempt-[12]', entry.name)):
                        continue
                    if len(records) >= MAX_FILES:
                        coverage.append('file-count-bound')
                        break
                    f = os.open(entry.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
                    try:
                        before = os.fstat(f)
                        if not stat.S_ISREG(before.st_mode):
                            raise ValueError('log is not regular')
                        if command_report:
                            if before.st_size > MAX_OUTPUT:
                                coverage.append('command-report-byte-bound'); continue
                            raw = os.read(f, MAX_OUTPUT + 1)
                            try:
                                value = json.loads(raw)
                                if value.get('schema') != RESULT_SCHEMA: raise ValueError('command report schema')
                                owned = value.get('ownerReceipt') or {}
                                summary = {key:value.get(key) for key in ('schema','commandExit','commandAccepted','firstCause','additionalFailures')}
                                summary['owner'] = {key:owned.get(key) for key in ('firstCause','additionalFailures','cleanup','custody','exit','birthIdentityUnavailable')}
                                try:
                                    summary['lifecycle'] = lifecycle_summary(value)
                                except (ValueError, TypeError, AttributeError) as projection_error:
                                    summary['lifecycle'] = {'projectionFailure':failure_record(projection_error)}
                                    coverage.append('lifecycle-fields-unavailable')
                                encoded = json.dumps(summary, ensure_ascii=True).encode()
                                if len(encoded) > FILE_BYTES:
                                    summary['lifecycle'] = {'projectionFailure':failure_record(ValueError('lifecycle summary byte cap'))}
                                    coverage.append('lifecycle-byte-bound')
                                    encoded = json.dumps(summary, ensure_ascii=True).encode()
                                if len(encoded) > FILE_BYTES:
                                    # Never slice typed JSON. Retain the primary types/messages
                                    # with explicit character omissions; omit secondary arrays.
                                    # Two (40+224)-character causes use at most6336 escaped
                                    # UTF-8 bytes, leaving room for fixed fields/counts below8192.
                                    def bounded_cause(value):
                                        if value is None: return None, None
                                        if not isinstance(value,dict): return None, {'unavailable':True}
                                        kind=str(value.get('type','')); message=str(value.get('message',''))
                                        return {'type':kind[:40], 'message':message[:224]}, {
                                            'typeCharacters':max(0,len(kind)-40),
                                            'messageCharacters':max(0,len(message)-224),
                                            'otherFields':len(set(value)-{'type','message'})}
                                    primary, primary_omitted = bounded_cause(summary['firstCause'])
                                    owner_primary, owner_omitted = bounded_cause(summary['owner']['firstCause'])
                                    secondary = summary.get('additionalFailures')
                                    owner_secondary = summary['owner'].get('additionalFailures')
                                    summary = {'schema':RESULT_SCHEMA,
                                        'commandExit':summary['commandExit'] if type(summary['commandExit']) is int and -255<=summary['commandExit']<=255 else None,
                                        'commandAccepted':summary['commandAccepted'] if type(summary['commandAccepted']) is bool else None,
                                        'firstCause':primary, 'additionalFailures':[],
                                        'owner':{'firstCause':owner_primary,'additionalFailures':[]},
                                        'lifecycle':{'projectionFailure':failure_record(ValueError('typed summary byte cap'))},
                                        'omittedProjection':{'firstCause':primary_omitted,'ownerFirstCause':owner_omitted,
                                            'additionalFailures':len(secondary) if isinstance(secondary,list) else None,
                                            'ownerAdditionalFailures':len(owner_secondary) if isinstance(owner_secondary,list) else None,
                                            'ownerFields':len(set(summary['owner'])-{'firstCause','additionalFailures'}), 'lifecycle':True}}
                                    coverage.append('typed-summary-byte-bound')
                                    encoded = json.dumps(summary, ensure_ascii=True).encode()
                                if len(encoded) > FILE_BYTES: raise ValueError('bounded typed summary exceeds byte cap')
                                first = encoded
                                last = b''
                            except (ValueError, TypeError, AttributeError):
                                coverage.append('command-report-unavailable'); continue
                        else:
                            first = os.read(f, FILE_BYTES // 2)
                        if not command_report and before.st_size > FILE_BYTES // 2:
                            os.lseek(f, max(FILE_BYTES // 2, before.st_size - FILE_BYTES // 2), os.SEEK_SET)
                            last = os.read(f, FILE_BYTES // 2)
                        elif not command_report:
                            last = b''
                        after = os.fstat(f)
                        stable = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) == (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
                    finally:
                        os.close(f)
                    records.append({'source': 'owned-command' if command_report else ('compiler-server' if relative.parts[0] == '_apalache-out' else 'command'),
                                    'ordinal': len(records), 'observedBytes': before.st_size,
                                    'truncated': before.st_size > FILE_BYTES,
                                    'stable': stable,
                                    'text': filtered(first + (b'\n[head/tail omitted]\n' if not command_report and before.st_size > FILE_BYTES else b'') + last, scratch)})
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


DISK_LIMIT = 1073741824

def require(ok, message):
    if not ok: raise RuntimeError(message)

# Same non-following disk inventory as the qualified single-controller route.
def census(root,deadline,limit=DISK_LIMIT,clock=time.monotonic):
    logical=allocated=objects=links=0;stack=[root]
    while stack:
        require(clock()<deadline,'disk-budget');path=stack.pop();s=path.lstat();objects+=1
        require(objects<=25000,'disk-population-cap');logical+=s.st_size;allocated+=s.st_blocks*512
        require(max(logical,allocated)<=limit,'disk-byte-cap')
        if stat.S_ISDIR(s.st_mode):stack.extend(path.iterdir())
        elif stat.S_ISLNK(s.st_mode):links+=1
        else:require(stat.S_ISREG(s.st_mode),'disk-special-file')
    return {'objects':objects,'logicalBytes':logical,'allocatedBytes':allocated,'fixtureSymlinks':links,'linkTraversal':False}

COMMAND_SCHEMA = 'fsgg.canonical.owned-command/1'
RESULT_SCHEMA = 'fsgg.canonical.command-result/1'
OWNER_SHA = 'c07c9a6afedc2f977a9611772d52266f298b9fd136378511fd3f4c6ba52e76db'
TOOLCHAIN_ID = '79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486'


def pin_file(path):
    path = Path(path).resolve(strict=True)
    mode = path.stat().st_mode
    if not stat.S_ISREG(mode):
        raise ValueError('command input is not regular')
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return {'path': str(path), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()}


def failure_record(error):
    if error is None:
        return None
    try: message = str(error)
    except BaseException: message = '<message unavailable>'
    return {'type': type(error).__name__[:96], 'message': filtered(message.encode(), Path('/nonexistent'))[:512]}


def command_role(entry, action):
    """An entry's role is data, never inferred from appended argv."""
    roles = {'dispatch-runtime-entry': {'dispatch'}, 'credential-entry': {'dispatch', 'watch'},
             'canonical-quint': {'verify'}}
    if action not in roles.get(entry, set()):
        raise ValueError('command role mismatch')
    return action


def validate_launch_grant(grant):
    """Validate original freshness fields only; this is not launch authority."""
    if set(grant) != {'startedAtEpoch', 'launchBeforeEpoch'}:
        raise ValueError('launch grant fields')
    start, end = grant['startedAtEpoch'], grant['launchBeforeEpoch']
    if (type(start) not in (float, int) or type(end) not in (float, int)
            or not math.isfinite(start) or not math.isfinite(end) or end != start + 60):
        raise ValueError('launch grant original sixty-second interval')


def command_preflight(spec, *, now):
    """Pure shape/role/budget joins; filesystem checks happen against actual pins."""
    if spec.get('profile', '').startswith('proc-pair-'):
        return proc_command_preflight(spec, now=now)
    required = {'schema', 'role', 'argv', 'pins', 'aliases', 'semanticToolchainId', 'archiveSha256',
                'started', 'workDeadline', 'totalDeadline', 'workSeconds', 'cleanupSeconds',
                'rssBytes', 'logBytes', 'maximumGenerations', 'endpoint', 'jvmArgs', 'tlcConfig', 'mutableScope'}
    if set(spec) != required or spec['schema'] != COMMAND_SCHEMA:
        raise ValueError('command schema')
    command_role('canonical-quint', spec['role'])
    if (type(spec['argv']) is not list or not 3 <= len(spec['argv']) <= 32
            or any(type(x) is not str or not x or len(x) > 2048 or '\x00' in x for x in spec['argv'])
            or spec['argv'][1] != 'verify'):
        raise ValueError('command argv')
    if spec['semanticToolchainId'] != TOOLCHAIN_ID:
        raise ValueError('semantic toolchain identity')
    archive = spec['archiveSha256']
    if archive is not None and not re.fullmatch('[a-f0-9]{64}', archive):
        raise ValueError('archive byte digest')
    if any(type(spec[k]) not in (float, int) or not math.isfinite(spec[k]) or spec[k] <= 0
           for k in ('started', 'workDeadline', 'totalDeadline')):
        raise ValueError('command clock')
    if (type(spec['workSeconds']) is not int or not 1 <= spec['workSeconds'] <= 150
            or spec['cleanupSeconds'] != 30 or spec['workDeadline'] != spec['started'] + spec['workSeconds']
            or spec['totalDeadline'] != spec['workDeadline'] + 30 or now >= spec['workDeadline']):
        raise ValueError('original command budget')
    if (spec['rssBytes'] != 6 * 1024**3 or spec['logBytes'] != 8 * 1024**2
            or spec['maximumGenerations'] != 6 or spec['jvmArgs'] != ['-Xmx1024m', '-Xms64m']
            or spec['tlcConfig'] != {'maxHeap': '-Xmx2G', 'stackSize': '-Xss64m', 'workers': 1}):
        raise ValueError('command resource joins')
    scope = spec['mutableScope']
    if (set(scope) != {'cwd', 'tmpdir', 'diskBytes', 'inventorySeconds', 'maximumEntries', 'sampleIntervalSeconds', 'javaToolOptions', 'denoDir', 'xdgCacheHome'}
            or type(scope['cwd']) is not str or not Path(scope['cwd']).is_absolute()
            or scope['tmpdir'] != str(Path(scope['cwd']) / 'tmp')
            or scope['diskBytes'] != DISK_LIMIT or scope['inventorySeconds'] != 1
            or scope['maximumEntries'] != 25000 or scope['sampleIntervalSeconds'] != .5
            or scope['javaToolOptions'] != '-Djava.io.tmpdir=' + scope['tmpdir']
            or scope['denoDir'] != str(Path(scope['cwd']) / 'cache/deno')
            or scope['xdgCacheHome'] != str(Path(scope['cwd']) / 'cache')):
        raise ValueError('mutable scope resource join')
    if type(spec['endpoint']) is not int or not 1024 <= spec['endpoint'] <= 65535:
        raise ValueError('command endpoint')
    if len(spec['pins']) < 4 or len(spec['pins']) > 8:
        raise ValueError('command pin count')
    for row in spec['pins']:
        if (set(row) != {'path', 'bytes', 'sha256'} or type(row['bytes']) is not int
                or row['bytes'] < 0 or not re.fullmatch('[a-f0-9]{64}', row['sha256'])):
            raise ValueError('command pin shape')
    if type(spec['aliases']) is not list or len(spec['aliases']) > 64:
        raise ValueError('command alias count')
    for row in spec['aliases']:
        if set(row) != {'path', 'target'} or not all(type(x) is str and x for x in row.values()):
            raise ValueError('command alias shape')
    if len(json.dumps(spec, ensure_ascii=True).encode()) > 64 * 1024:
        raise ValueError('command metadata cap')
    # Semantic identity intentionally has no equality requirement with byte digests.
    return {'schema': COMMAND_SCHEMA, 'passed': True, 'role': spec['role']}


PROC_PROFILES = {'proc-pair-policy-observation': 'observe-execution-pair-policy-21633398',
                 'proc-pair-recovery-dispatch': 'recover-execution-pair-030-b11fb121-original38495729817',
                 'proc-pair-native-watch': None}
PROC_ORIGINAL = 'publish-execution-pair-030-b11fb121-four38484632631'
PROC_REPOSITORY = 'FS-GG/FS.GG.Coordination'
PROC_WORKFLOW = '.github/workflows/callable-cli-release-publish.yml'


def proc_command_preflight(spec, *, now):
    """Closed operation contracts. A valid shape never supplies launch authority."""
    fields = {'schema','profile','role','campaign','item','attempt','originalOperation','operation',
              'publisher','repositoryId','workflowId','workflowPath','source','originalPublisher',
              'request','argv','pins','proofInputs','sourceReadAllowlist','environment','operationRoot',
              'started','workDeadline','totalDeadline','cleanupSeconds','rssBytes','addressSpaceBytes','logBytes','diskBytes','cpuAffinity'}
    if set(spec) != fields or spec['schema'] != COMMAND_SCHEMA or spec['profile'] not in PROC_PROFILES:
        raise ValueError('PROC command schema/profile')
    expected_role = 'watch' if spec['profile'] == 'proc-pair-native-watch' else 'dispatch'
    if spec['role'] != expected_role: raise ValueError('PROC profile role exchange')
    if (spec['campaign'] != 'unified-roadmap-20261003' or not all(type(spec[k]) is str and spec[k] for k in ('item','attempt'))
            or spec['originalOperation'] != PROC_ORIGINAL
            or spec['repositoryId'] != 1346720714 or spec['workflowId'] != 361038177 or spec['workflowPath'] != PROC_WORKFLOW
            or spec['source'] != 'b11fb121f046898e655b779dfc6809f35fc7aac6'
            or spec['originalPublisher'] != '0ccfb6d067d912bbd8a1e26c4b56449358443b2e'
            or not re.fullmatch('[0-9a-f]{40}',spec['publisher'])):
        raise ValueError('PROC original/source/native identity')
    if spec['profile'] != 'proc-pair-native-watch' and spec['operation'] != PROC_PROFILES[spec['profile']]:
        raise ValueError('PROC operation exchange')
    if spec['operation'] not in (PROC_PROFILES['proc-pair-policy-observation'],PROC_PROFILES['proc-pair-recovery-dispatch']):
        raise ValueError('PROC consumed-original operation cannot dispatch or watch as recovery')
    if (any(type(spec[k]) not in (float,int) or not math.isfinite(spec[k]) for k in ('started','workDeadline','totalDeadline'))
            or not 0 < spec['workDeadline']-spec['started'] <= 90 or spec['cleanupSeconds'] != 10
            or spec['totalDeadline'] != spec['workDeadline']+10 or not spec['started'] <= now < spec['workDeadline']
            or now-spec['started'] > 60):
        raise ValueError('PROC original launch/deadline budget')
    if (spec['rssBytes'] != 512*1024**2 or spec['logBytes'] != 1024**2 or spec['diskBytes'] != 16*1024**2
            or not isinstance(spec['cpuAffinity'],list) or len(spec['cpuAffinity']) != 1
            or type(spec['cpuAffinity'][0]) is not int
            or spec['addressSpaceBytes'] is not None and (type(spec['addressSpaceBytes']) is not int or spec['addressSpaceBytes']<spec['rssBytes'])):
        raise ValueError('PROC resource profile')
    root = Path(spec['operationRoot'])
    if not root.is_absolute() or str(root) != os.path.normpath(str(root)) or root.name != spec['operation']:
        raise ValueError('PROC fixed operation root')
    environment = spec['environment']
    if (set(environment) != {'HOME','TMPDIR','GH_CONFIG_DIR','PATH','LANG','LC_ALL','GH_PROMPT_DISABLED','GH_HOST'}
            or environment['HOME'] != str(root/'home') or environment['TMPDIR'] != str(root/'tmp')
            or environment['GH_CONFIG_DIR'] != str(root/'home/.config/gh')
            or environment['GH_PROMPT_DISABLED'] != '1' or environment['GH_HOST'] != 'github.com'
            or any(type(v) is not str or '\x00' in v for v in environment.values())):
        raise ValueError('PROC environment/config exclusions')
    if (not isinstance(spec['pins'],list) or not 3 <= len(spec['pins']) <= 512
            or not isinstance(spec['sourceReadAllowlist'],list) or len(spec['sourceReadAllowlist']) > 512):
        raise ValueError('PROC closure population')
    for row in spec['pins']:
        if (set(row) != {'path','bytes','sha256','mode'} or not Path(row['path']).is_absolute()
                or type(row['bytes']) is not int or row['bytes'] < 0
                or not re.fullmatch('[0-9a-f]{64}',row['sha256']) or type(row['mode']) is not int):
            raise ValueError('PROC closure pin')
    if (len({p['path'] for p in spec['pins']}) != len(spec['pins'])
            or set(spec['sourceReadAllowlist']) != {p['path'] for p in spec['pins']}):
        raise ValueError('PROC exact declared read closure')
    if set(spec['proofInputs']) != {'admission','originalRun','originalJobs','originalArtifact','originalArchive','verifiedAttestation','consumerRoot','sourceCorrespondence','runtimeQualification'}:
        raise ValueError('PROC proof input population')
    pinned = {p['path'] for p in spec['pins']}
    if any(path not in pinned for key,path in spec['proofInputs'].items() if key != 'consumerRoot'):
        raise ValueError('PROC proof input not pinned')
    request = spec['request']
    if spec['role'] == 'dispatch':
        inputs = {'execution_pair':True,'pair_recovery':spec['profile']=='proc-pair-recovery-dispatch',
                  'preflight_only':spec['profile']=='proc-pair-policy-observation','cli_only':False,'readback_only':False,
                  'publisher_source':spec['publisher'],'operation':spec['operation'],'source':spec['source'],
                  'protected_merge':spec['source'],'expected_sha256':'c8772596977e2eb172672fa393d78daab5867aefcf24aa9960a1c5627d4f7ca8'}
        expected = {'method':'POST','endpoint':'repos/'+PROC_REPOSITORY+'/actions/workflows/361038177/dispatches',
                    'apiVersion':'2026-03-10','body':{'ref':'main','inputs':inputs},'requestLimit':1}
        argv_tail = ['api','--method','POST','--include',expected['endpoint'],'-H','X-GitHub-Api-Version: 2026-03-10',
                     '--input',str(root/'dispatch-input.json')]
    else:
        if (set(request) != {'method','endpoint','apiVersion','body','requestLimit','runId'}
                or request['runId'] is not None and (type(request['runId']) is not int or request['runId'] <= 0)):
            raise ValueError('PROC watch native identity')
        endpoint = ('repos/'+PROC_REPOSITORY+'/actions/runs/'+str(request['runId']) if request['runId'] is not None
                    else 'repos/'+PROC_REPOSITORY+'/actions/runs?event=workflow_dispatch&head_sha='+spec['publisher']+'&per_page=100')
        expected = {'method':'GET','endpoint':endpoint,
                    'apiVersion':'2026-03-10','body':None,'requestLimit':5,'runId':request['runId']}
        argv_tail = ['api','--method','GET',expected['endpoint'],'-H','X-GitHub-Api-Version: 2026-03-10']
    if request != expected or not isinstance(spec['argv'],list) or spec['argv'][1:] != argv_tail:
        raise ValueError('PROC exact endpoint/method/argv')
    if spec['argv'][0] not in pinned: raise ValueError('PROC client outside runtime closure')
    if len(json.dumps(spec).encode()) > 256*1024: raise ValueError('PROC descriptor bound')
    return {'schema':COMMAND_SCHEMA,'passed':True,'role':spec['role'],'publicationAuthorized':False}


def proc_regular_pin(row):
    path = Path(row['path'])
    if any(p.is_symlink() for p in (path,*path.parents)) or not path.is_file():
        raise ValueError('PROC source/runtime alias or nonregular input')
    actual = pin_file(path)
    actual['mode'] = stat.S_IMODE(path.stat().st_mode)
    if actual != row: raise ValueError('PROC source/runtime input drift')


def proc_dispatch_response(raw):
    """Only documented 200+exact native identity settles the one consumed send."""
    if len(raw) > 1024**2: raise ValueError('PROC dispatch response bound')
    header,body = raw.replace(b'\r\n',b'\n').split(b'\n\n',1)
    if header.splitlines()[0].split()[1] != b'200': raise ValueError('PROC dispatch response is not HTTP200')
    value = json.loads(body)
    if set(value) != {'workflow_run_id','run_url','html_url'} or type(value['workflow_run_id']) is not int or value['workflow_run_id'] <= 0:
        raise ValueError('PROC dispatch native response identity')
    run = value['workflow_run_id']
    if (value['run_url'] != 'https://api.github.com/repos/'+PROC_REPOSITORY+'/actions/runs/'+str(run)
            or value['html_url'] != 'https://github.com/'+PROC_REPOSITORY+'/actions/runs/'+str(run)):
        raise ValueError('PROC dispatch native response origin')
    return value


def proc_native_correlation(spec, prior, payload):
    """A unique original-window match is observation, never a fresh send grant."""
    def identity(run):
        return (type(run.get('id')) is int and run['id']>0 and run.get('workflow_id')==spec['workflowId']
                and run.get('head_sha')==spec['publisher'] and run.get('event')=='workflow_dispatch'
                and type(run.get('run_attempt')) is int and run['run_attempt']==1
                and run.get('repository',{}).get('id')==spec['repositoryId'] and run.get('path')==PROC_WORKFLOW
                and run.get('head_branch')=='main' and run.get('display_title')=='PROC '+spec['operation']+' @ '+spec['publisher'])
    if spec['request']['runId'] is not None:
        if not identity(payload) or payload['id']!=spec['request']['runId']:
            raise ValueError('PROC watch native correlation changed')
        return payload
    rows=payload['workflow_runs']
    if (type(payload['total_count']) is not int or payload['total_count']!=len(rows) or len(rows)>=100
            or len({r['id'] for r in rows})!=len(rows)):
        raise ValueError('PROC uncertain-send run population incomplete')
    import datetime
    matches=[]
    for run in rows:
        if identity(run):
            created=datetime.datetime.strptime(run['created_at'],'%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=datetime.timezone.utc).timestamp()
            if prior['nativeCreatedAfterEpoch']-1<=created<=prior['nativeCreatedBeforeEpoch']:matches.append(run)
    if len(matches)!=1:raise ValueError('PROC uncertain-send run correlation absent or ambiguous; send remains consumed')
    return matches[0]


def proc_runtime_evidence(spec, authority, evidence):
    """Join root-selected canonical qualification to actual current support inputs."""
    owner=evidence.get('ownerReceipt') or {}
    runtime=evidence.get('runtimeEvidence') or {}
    expected_bounds={k:spec[k] for k in ('cpuAffinity','rssBytes','addressSpaceBytes','logBytes','diskBytes')}
    support=[pin for pin in spec['pins'] if pin['path'] not in spec['proofInputs'].values()]
    if (evidence.get('schema')!=RESULT_SCHEMA or evidence.get('commandAccepted') is not True
            or evidence!=authority.get('runtimeQualification') or owner.get('cleanup')!='observed-empty-group'
            or owner.get('custody')!='observed-owned-group' or owner.get('exit')!=0 or owner.get('firstCause') is not None
            or not isinstance(owner.get('leader'),dict) or not owner['leader'].get('start')
            or owner.get('residual')!=[] or owner.get('escapedResidual')!=[] or owner.get('escapedDescendants')!=[]
            or runtime.get('currentBootId')!=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
            or runtime.get('effectiveUid')!=os.geteuid() or runtime.get('actualInterpreter')!=os.path.realpath(sys.executable)
            or runtime.get('supportPins')!=support or runtime.get('bounds')!=expected_bounds
            or runtime.get('clientPath')!=spec['argv'][0] or runtime.get('postRetryExcluded') is not True
            or runtime.get('transitiveSourceReadFence')!='qualified-no-unobserved-source-reads'
            or runtime.get('sourceReadAllowlist')!=spec['sourceReadAllowlist']):
        raise ValueError('PROC actual canonical runtime/support/read/retry/custody evidence unavailable')


def proc_terminal_evidence(owner, spec, slot, env, check, native):
    """Bound independent terminal logs/artifacts; failures never reopen original send."""
    run=native['id']; collected={}; failures=[]
    def read(target,name,document=False):
        output=slot/(name+'.log')
        argv=[spec['argv'][0],'api','--method','GET',target,'-H','X-GitHub-Api-Version: 2026-03-10']
        if name=='terminal-job':argv.insert(2,'--allow-escape-sequences')
        try:
            owner.run(argv,str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],spec['rssBytes'],str(output),
                      spec['logBytes'],control=check,observe_child_exit=True)
            raw=output.read_bytes()
            if len(raw)>spec['logBytes']:raise ValueError('PROC terminal evidence bound')
            value=json.loads(raw) if document else None
            collected[name]={'state':'collected','sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)}
            return value
        except BaseException as error:
            failures.append({'stage':name,'cause':failure_record(error)})
            receipt_path=output.with_suffix('.log.result.json')
            if not receipt_path.is_file() or json.loads(receipt_path.read_bytes()).get('cleanup')!='observed-empty-group':
                raise ValueError('PROC terminal evidence child custody/cleanup unknown') from error
            collected[name]={'state':'failed'}
            return None
    jobs=read('repos/'+PROC_REPOSITORY+'/actions/runs/'+str(run)+'/attempts/1/jobs?per_page=100','terminal-jobs',True)
    artifacts=read('repos/'+PROC_REPOSITORY+'/actions/runs/'+str(run)+'/artifacts?per_page=100','terminal-artifacts',True)
    if jobs is not None:
        rows=jobs.get('jobs',[])
        expected='Publish the admitted original routine Execution pair' if spec['operation']==PROC_PROFILES['proc-pair-recovery-dispatch'] else 'Observe original Execution pair without publication authority'
        selected=[j for j in rows if j.get('name')==expected]
        if (jobs.get('total_count')==len(rows)<100 and len(selected)==1 and type(selected[0].get('id')) is int
                and selected[0].get('run_id')==run and selected[0].get('head_sha')==spec['publisher'] and selected[0].get('run_attempt')==1):
            read('repos/'+PROC_REPOSITORY+'/actions/jobs/'+str(selected[0]['id'])+'/logs','terminal-job')
        else:failures.append({'stage':'terminal-job','cause':{'message':'complete selected native job binding unavailable'}})
    if artifacts is not None:
        rows=artifacts.get('artifacts',[])
        prefix='execution-pair-publication-' if spec['operation']==PROC_PROFILES['proc-pair-recovery-dispatch'] else 'execution-pair-preflight-'
        selected=[a for a in rows if a.get('name')==prefix+str(run)+'-1']
        if (artifacts.get('total_count')==len(rows)<100 and len(selected)==1 and type(selected[0].get('id')) is int
                and selected[0].get('expired') is False and selected[0].get('workflow_run',{}).get('id')==run
                and selected[0].get('workflow_run',{}).get('head_sha')==spec['publisher']
                and type(selected[0].get('size_in_bytes')) is int and 0<selected[0]['size_in_bytes']<=spec['logBytes']):
            read('repos/'+PROC_REPOSITORY+'/actions/artifacts/'+str(selected[0]['id'])+'/zip','terminal-archive')
            archive=collected.get('terminal-archive',{})
            if archive.get('state')=='collected' and selected[0].get('digest')!='sha256:'+archive['sha256']:
                archive['state']='failed';failures.append({'stage':'terminal-archive','cause':{'message':'native artifact archive digest differs'}})
        else:failures.append({'stage':'terminal-archive','cause':{'message':'artifact unavailable, unsafe, incomplete or exceeds remaining selected output bound'}})
    return {'records':collected,'additionalFailures':failures,'publicationAccepted':False}


def execute_proc_command(descriptor, authority_path, authority_sha):
    """Existing canonical input/result slots, one shared owner, no send replay."""
    first=None; additional=[]; receipt=None; spec=None; result=None; claimed=False
    def read_regular(path,cap):
        p=Path(path)
        if any(x.is_symlink() for x in (p,*p.parents)) or not p.is_file() or not 0<p.stat().st_size<=cap:
            raise ValueError('PROC selected descriptor/authority unsafe')
        return p.read_bytes()
    def persist(path,value,exclusive=False):
        raw=json.dumps(value,sort_keys=True).encode()+b'\n'
        if exclusive:
            with path.open('xb') as out: os.chmod(path,0o600);out.write(raw);out.flush();os.fsync(out.fileno())
        else:
            temporary=path.with_suffix('.pending')
            with temporary.open('xb') as out:os.chmod(temporary,0o600);out.write(raw);out.flush();os.fsync(out.fileno())
            os.replace(temporary,path)
    try:
        raw=read_regular(descriptor,256*1024);spec=json.loads(raw)
        authority_raw=read_regular(authority_path,256*1024)
        if hashlib.sha256(authority_raw).hexdigest()!=authority_sha:raise ValueError('PROC root-selected authority drift')
        authority=json.loads(authority_raw)
        if (authority.get('schema')!=COMMAND_SCHEMA or authority.get('descriptorSha256')!=hashlib.sha256(raw).hexdigest()
                or authority.get('operation')!=spec['operation'] or authority.get('operationRoot')!=spec['operationRoot']
                or authority.get('profile')!=spec['profile'] or authority.get('approved') is not True
                or authority.get('runtimeQualified') is not True or authority.get('readFenceQualified') is not True
                or authority.get('clientAutomaticPostRetry') is not False or authority.get('independentCapacityQualified') is not True):
            raise ValueError('PROC current root authority/runtime/read/retry/capacity admission unavailable')
        proc_command_preflight(spec,now=time.monotonic())
        if set(os.sched_getaffinity(0))!=set(spec['cpuAffinity']):raise ValueError('PROC actual CPU affinity differs')
        import resource
        expected_address_space=resource.RLIM_INFINITY if spec['addressSpaceBytes'] is None else spec['addressSpaceBytes']
        if resource.getrlimit(resource.RLIMIT_AS)[0]!=expected_address_space:raise ValueError('PROC actual virtual address-space bound differs')
        for pin in spec['pins']:proc_regular_pin(pin)
        if os.path.realpath(sys.executable) not in spec['sourceReadAllowlist']:
            raise ValueError('PROC actual interpreter outside qualified runtime closure')
        if not sys.flags.isolated or not sys.flags.no_site or not sys.dont_write_bytecode:
            raise ValueError('PROC actual interpreter must use isolated/no-site/no-bytecode flags')
        required_sources=(Path(__file__),Path(__file__).with_name('shared_process_owner.py'),
                          Path(__file__).with_name('test-callable-cli-installed-harness.py'),
                          Path(__file__).with_name('write-callable-cli-release-readback.py'))
        if any(str(path.resolve()) not in spec['sourceReadAllowlist'] for path in required_sources):
            raise ValueError('PROC actual validator/runner/support closure absent')
        root=Path(spec['operationRoot']);parent=root.parent
        if any(p.is_symlink() for p in (parent,*parent.parents)) or stat.S_IMODE(parent.stat().st_mode)!=0o700:
            raise ValueError('PROC exclusive operation parent')
        owner_path=Path(__file__).with_name('shared_process_owner.py')
        if not any(pin['path']==str(owner_path) and pin['sha256']==OWNER_SHA for pin in spec['pins']):
            raise ValueError('PROC shared owner closure absent')
        validators=__import__('runpy').run_path(str(Path(__file__).with_name('test-callable-cli-installed-harness.py')))
        def document(key):return json.loads(read_regular(spec['proofInputs'][key],2*1024**2))
        admission=document('admission');selected=validators['pair_recovery_selection'](admission)
        qualification=document('runtimeQualification')
        proc_runtime_evidence(spec,authority,qualification)
        correspondence=document('sourceCorrespondence')
        if (correspondence.get('publisher')!=spec['publisher'] or correspondence.get('source')!=spec['source']
                or correspondence.get('originalPublisher')!=spec['originalPublisher'] or correspondence.get('protectedSourceVerified') is not True
                or correspondence.get('aToEExactThreePaths') is not True or correspondence.get('aToPublisherExactElevenPaths') is not True):
            raise ValueError('PROC current protected source correspondence absent')
        original_proof=validators['pair_original_failure'](admission,document('originalRun'),document('originalJobs'),document('originalArtifact'),
                                                          read_regular(spec['proofInputs']['originalArchive'],192*1024**2))
        attestation_proof=validators['pair_verified_attestation'](admission,document('verifiedAttestation'))
        __import__('runpy').run_path(str(Path(__file__).with_name('write-callable-cli-release-readback.py')))['pair_consumers'](admission,Path(spec['proofInputs']['consumerRoot']))
        consumer=Path(spec['proofInputs']['consumerRoot'])
        expected_consumer={'run.json','jobs.json',*(case+suffix for case in ('sdd-s1','fsquint-s1','sdd-rollback','fsquint-rollback')
                                                  for suffix in ('.artifact.json','.job.log','.zip'))}
        if consumer.is_symlink() or any(str(consumer/name) not in spec['sourceReadAllowlist'] for name in expected_consumer):
            raise ValueError('PROC authenticated consumer input closure absent')
        def imports_unchanged():
            for imported in list(sys.modules.values()):
                source=getattr(imported,'__file__',None)
                if source is not None and Path(source).is_file() and str(Path(source).resolve()) not in spec['sourceReadAllowlist']:
                    raise ValueError('PROC unexpected actual import/support source')
        imports_unchanged()
        if (spec['profile']=='proc-pair-recovery-dispatch' and selected['enabled'] is not True
                or spec['profile']=='proc-pair-policy-observation' and selected['policyObservationEnabled'] is not True):
            raise ValueError('PROC protected profile remains disabled')
        if spec['role']=='watch':
            prior=json.loads(read_regular(root/'command.json',MAX_OUTPUT))
            if (prior.get('schema')!=RESULT_SCHEMA or prior.get('operation')!=spec['operation']
                    or prior.get('sendState') not in ('send-started','consumed-accepted','consumed-unknown')
                    or spec['request']['runId'] is not None and (prior.get('nativeResponse') or {}).get('workflow_run_id')!=spec['request']['runId']):
                raise ValueError('PROC watch original native response binding unavailable')
            observation=root/('watch-'+hashlib.sha256(raw).hexdigest())
            observation.mkdir(mode=0o700);slot=observation
        else:
            # Fixed root key comes from root-selected authority. No alternate output path exists.
            root.mkdir(mode=0o700);claimed=True;slot=root
        for name in ('tmp','home'): (slot/name).mkdir(mode=0o700)
        persist(slot/'input.json',spec,exclusive=True)
        result={'schema':RESULT_SCHEMA,'operation':spec['operation'],'originalOperation':PROC_ORIGINAL,
                'commandAccepted':False,'sendState':'not-applicable' if spec['role']=='watch' else 'prepared-no-send',
                'firstCause':None,'additionalFailures':[],'ownerReceipt':None,'nativeResponse':None,
                'originalPublication':original_proof,'retainedAttestation':attestation_proof,
                'remoteTerminal':'unknown','reportingOutcome':'pending','publicationAccepted':False}
        if spec['role']=='dispatch':
            persist(root/'dispatch-input.json',spec['request']['body'],exclusive=True)
            persist(root/'command.json',result,exclusive=True)
        env=dict(spec['environment'])
        if 'GH_TOKEN' not in os.environ or not os.environ['GH_TOKEN']:raise ValueError('PROC approved credential binding unavailable')
        env['GH_TOKEN']=os.environ['GH_TOKEN']
        def check(event,child,leader,known):
            if event!='after-cleanup' and time.monotonic()>=spec['workDeadline']:raise TimeoutError('PROC original deadline')
            for pin in spec['pins']:proc_regular_pin(pin)
            imports_unchanged()
            observed=census(slot,min(spec['totalDeadline'],time.monotonic()+1),limit=spec['diskBytes'])
            if observed['fixtureSymlinks'] or observed['objects']>25000:raise ValueError('PROC operation scope changed')
            # This is observed-group read exclusion, not a claim about transient opens.
            # The separately root-qualified transitive read fence remains mandatory.
            if leader is not None and event!='after-cleanup':
                members=owner.census(leader['pgid'])
                if len(members)>32:raise ValueError('PROC observed group population bound')
                for member in members:
                    if known.get(member['pid'])!=member['start']:raise ValueError('PROC unknown observed group birth')
                    for target in owned_fd_targets(member,owner.proc,additional):
                        if target.startswith('/') and target not in spec['sourceReadAllowlist'] and target not in (
                                '/dev/null',str(slot/'runner.log'),str(slot/'client-preflight.log'),str(root/'dispatch-input.json'),
                                *(str(slot/(name+'.log')) for name in ('terminal-jobs','terminal-artifacts','terminal-job','terminal-archive'))):
                            raise ValueError('PROC unexpected observed source/config read')
        module_spec=importlib.util.spec_from_file_location('proc_shared_owner',owner_path)
        owner=importlib.util.module_from_spec(module_spec);module_spec.loader.exec_module(owner)
        # Benign actual-client startup is checked before send intent, under the same
        # original limits; Go virtual reservations are distinct from sampled RSS.
        startup=owner.run([spec['argv'][0],'--version'],str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],
                          spec['rssBytes'],str(slot/'client-preflight.log'),spec['logBytes'],control=check,observe_child_exit=True)
        version=read_regular(slot/'client-preflight.log',4096).decode().strip()
        if version!=qualification['runtimeEvidence'].get('clientVersion'):
            raise ValueError('PROC actual current client startup/version differs')
        result['clientPreflightReceipt']=startup
        if spec['role']=='dispatch':
            result['sendState']='send-started'
            result['nativeCreatedAfterEpoch']=time.time()
            result['nativeCreatedBeforeEpoch']=result['nativeCreatedAfterEpoch']+max(0,spec['workDeadline']-time.monotonic())+10
            # Durable intent precedes exactly one native POST. Never transition back.
            persist(root/'command.json',result)
        receipt=owner.run(spec['argv'],str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],spec['rssBytes'],
                          str(slot/'runner.log'),spec['logBytes'],control=check,observe_child_exit=True)
        response=read_regular(slot/'runner.log',spec['logBytes'])
        if spec['role']=='dispatch':
            result['nativeResponse']=proc_dispatch_response(response);result['sendState']='consumed-accepted'
        else:
            native=proc_native_correlation(spec,prior,json.loads(response))
            result['remoteTerminal']=native.get('status','unknown');result['nativeResponse']=native
            if native.get('status')=='completed':
                result['terminalEvidence']=proc_terminal_evidence(owner,spec,slot,env,check,native)
        result['commandAccepted']=True
    except BaseException as error:
        first=error
        if result is not None and spec['role']=='dispatch' and result['sendState']=='send-started':result['sendState']='consumed-unknown'
    finally:
        if result is not None:
            result['firstCause']=failure_record(first);result['additionalFailures']=additional
            result['ownerReceipt']=receipt
            if receipt is None and 'slot' in locals() and (slot/'runner.log.result.json').is_file():
                try:result['ownerReceipt']=json.loads((slot/'runner.log.result.json').read_bytes())
                except BaseException as error:additional.append(failure_record(error))
            result['reportingOutcome']='written'
            try:persist(slot/'command.json',result,exclusive=spec['role']=='watch')
            except BaseException as error:
                additional.append(failure_record(error));result['reportingOutcome']='failed'
                if first is None:first=error
        elif first is not None:
            print(json.dumps({'schema':RESULT_SCHEMA,'firstCause':failure_record(first),'commandAccepted':False}),file=sys.stderr)
    return 0 if first is None else 1


def extraction_receipt_join(receipt, manifest_sha):
    """Actual accepted extraction fields; no compiler/CI authority inferred."""
    if (type(manifest_sha) is not str or not re.fullmatch('[a-f0-9]{64}', manifest_sha)
            or receipt.get('accepted') is not True or receipt.get('selectedManifestSha256') != manifest_sha
            or receipt.get('allSelectedBytesAndModesVerified') is not True
            or receipt.get('extractedCodeExecuted') is not False
            or type(receipt.get('providerRequests')) is not int or receipt['providerRequests'] != 0):
        raise ValueError('extraction receipt scope or fields')
    return {'extractionAccepted': True, 'compilerAccepted': False, 'ciAccepted': False}


def expected_server_retirement(receipt, server_births, exit_members, stop_requests):
    """Only the explicitly observed original server may explain live-child retirement."""
    if receipt['firstCause'] != 'RuntimeError: child exited with live owned-group descendants':
        return receipt['firstCause'] is None
    observation = receipt.get('exitObservation')
    leader = receipt.get('leader')
    if not isinstance(observation, dict) or not isinstance(leader, dict): return False
    members = observation.get('members')
    if (type(observation.get('code')) is not int or observation['code'] != 0
            or type(observation.get('omittedMembers')) is not int or observation['omittedMembers'] != 0
            or not isinstance(members, list) or len(members) != 1): return False
    member = members[0]
    if (not isinstance(member, dict) or type(member.get('pid')) is not int
            or type(member.get('start')) is not int or type(member.get('pgid')) is not int
            or type(leader.get('pgid')) is not int or member['pgid'] != leader['pgid']
            or type(member.get('state')) is not str or len(member['state']) != 1 or member['state'] == 'Z'):
        return False
    if (not isinstance(stop_requests, list) or len(stop_requests) != 1
            or not isinstance(stop_requests[0], dict)): return False
    stop = stop_requests[0]
    if type(stop.get('pid')) is not int or type(stop.get('start')) is not int: return False
    refusing_births = {(member['pid'], member['start'])}
    return (refusing_births == set(server_births)
            and exit_members is not None and len(server_births) == 1 and receipt['exit'] == 0
            and set(exit_members) == set(server_births) and (stop['pid'], stop['start']) in server_births
            and type(stop.get('signal')) is int and stop['signal'] == signal.SIGTERM
            and stop.get('disposition') in ('sent', 'already-terminal'))


def retire_original_server(owner, child, leader, servers, handles, stop_requests, checkpoint, known, maximum_generations, membership_observation):
    """Fresh post-exit membership and held pidfd only; unknown/live status refuses."""
    if child.returncode is None: raise ValueError('server retirement without observed child exit')
    rows = owner.census(leader['pgid'])
    members = {(row['pid'], row['start']) for row in rows}
    membership_observation['members'] = members
    known.update(members)
    if len(known) + 1 > maximum_generations: raise ValueError('generation cap')
    # The role owns exactly its authenticated compiler server. A running leader
    # sampled before the owner poll cannot participate in this fresh census.
    if child.returncode == 0 and len(servers) == 1 and members == servers and not stop_requests:
        birth = next(iter(servers)); checkpoint()
        fresh = owner.proc(birth[0])
        if fresh is None: return members
        if (fresh['start'], fresh['pgid']) != (birth[1], leader['pgid']):
            raise ValueError('server retirement birth join')
        try:
            signal.pidfd_send_signal(handles[birth], signal.SIGTERM)
            disposition = 'sent'
        except ProcessLookupError:
            disposition = 'already-terminal'
        stop_requests.append({'pid':birth[0], 'start':birth[1], 'signal':15,
                              'disposition':disposition, 'at':time.monotonic()})
    return members


def observe_endpoint_ownership(owner, child, leader, servers, handles, known, checkpoint,
                               maximum_generations, listener_inodes, additional, fd_denials,
                               endpoint_observations=None, event='poll'):
    """Reobserve one known transition; a second complete sampled proof must be stable.

    Listeners or members may change after their final samples. This does not
    establish atomic exclusivity, continuous containment or descendant reaping.
    """
    def inode_summary(inodes):
        if inodes is None: return None
        return {'values':sorted(inodes)[:6], 'omitted':max(0,len(inodes)-6)}
    def member_summary(rows):
        if rows is None: return None
        return {'members':[{k:row[k] for k in ('pid','start','pgid','state')} for row in rows[:6]],
                'omittedMembers':max(0,len(rows)-6)}
    def membership(observation, field):
        checkpoint()
        rows = owner.census(leader['pgid'])
        observation[field] = member_summary(rows)
        checkpoint()
        known.update((row['pid'], row['start']) for row in rows)
        if len(known) + 1 > maximum_generations: raise ValueError('generation cap')
        return rows
    # Internal prior sampled proof may classify disappearance, never current authority.
    prior = (endpoint_observations['entries'][-1] if endpoint_observations is not None
             and endpoint_observations['entries'] else None)
    for attempt in (1,2):
        observation = {'event':event, 'attempt':attempt, 'disposition':'pending',
                       'beforeMembers':None, 'afterMembers':None, 'beforeInodes':None,
                       'afterInodes':None, 'matchedInodes':None, 'failure':None, 'fdScans':[]}
        if endpoint_observations is not None:
            if len(endpoint_observations['entries']) >= 2:
                endpoint_observations['entries'].pop(0); endpoint_observations['omitted'] += 1
            endpoint_observations['entries'].append(observation)
        try:
            rows = membership(observation,'beforeMembers')
            inodes = listener_inodes(); observation['beforeInodes'] = inode_summary(inodes)
            checkpoint(); matched = set(); proved = set()
            # Candidates only: every attempt needs new FD and birth/group evidence.
            rows.sort(key=lambda row: (row['pid'], row['start']) not in handles)
            for row in rows:
                if matched == inodes: break
                checkpoint()
                def terminal_probe():
                    checkpoint()
                    return child.poll()
                probe = terminal_probe if (child.pid == leader['pid'] == row['pid']
                        and row['start'] == leader['start'] and row['pgid'] == leader['pgid']) else None
                scan={'before':{k:row[k] for k in ('pid','start','pgid','state')},
                      'disposition':'pending','missingReadlinks':0,
                      'socketInodes':{'values':[],'omitted':0},
                      'afterMismatchObserved':False,'afterMismatchBirth':None}
                observation['fdScans'].append(scan)
                for target in owned_fd_targets(row, owner.proc, additional,
                                              terminal_probe=probe, denial_observations=fd_denials,
                                              scan_observation=scan):
                    if target.startswith('socket:[') and target[8:-1] in inodes:
                        fresh = owner.proc(row['pid'])
                        if fresh is None or (fresh['start'], fresh['pgid']) != (row['start'], leader['pgid']):
                            raise ValueError('server birth join')
                        birth = (row['pid'], row['start'])
                        matched.add(target[8:-1]); servers.add(birth); proved.add(birth)
                        if birth not in handles:
                            handle = os.pidfd_open(row['pid'])
                            after = owner.proc(row['pid'])
                            if after is None or (after['start'], after['pgid']) != (row['start'], leader['pgid']):
                                os.close(handle); raise ValueError('server pidfd birth join')
                            handles[birth] = handle
            observation['matchedInodes'] = inode_summary(matched)
            if matched != inodes:
                # Capture the exact mismatch before classifying a single fully known
                # disappearance. Empty later state alone never supplies ownership,
                # terminal birth, retirement or a substitute for a fresh full proof.
                primary = ValueError('foreign endpoint listener')
                try: membership(observation,'afterMembers')
                except BaseException as error: additional.append(failure_record(error))
                for scan in observation['fdScans']:
                    try:
                        checkpoint()
                        fresh = owner.proc(scan['before']['pid'])
                        scan['afterMismatchBirth'] = None if fresh is None else {k:fresh[k] for k in ('pid','start','pgid','state')}
                        scan['afterMismatchObserved'] = True
                    except BaseException as error: additional.append(failure_record(error))
                try:
                    checkpoint()
                    observation['afterInodes'] = inode_summary(listener_inodes())
                    checkpoint()
                except BaseException as error: additional.append(failure_record(error))
                before_members = observation['beforeMembers']
                after_members = observation['afterMembers']
                before_listeners = observation['beforeInodes']
                after_listeners = observation['afterInodes']
                def complete_births(value):
                    if value is None or value['omittedMembers']: return None
                    rows = value['members']
                    births = {(row['pid'],row['start']) for row in rows}
                    if len(births) != len(rows) or any(row['pgid'] != leader['pgid']
                            or row['state'] not in ('R','S','D','T','t','I') for row in rows): return None
                    return births
                def prior_owned_disappearance():
                    if prior is None or prior['disposition'] != 'proved' or prior['failure'] is not None:
                        return False
                    summaries = [prior[field] for field in ('beforeInodes','afterInodes','matchedInodes')]
                    if any(value is None or value['omitted'] for value in summaries): return False
                    prior_inodes = set(summaries[0]['values'])
                    if any(set(value['values']) != prior_inodes for value in summaries[1:]): return False
                    missing = inodes - matched
                    if not missing or not missing <= prior_inodes: return False
                    prior_before = complete_births(prior['beforeMembers'])
                    prior_after = complete_births(prior['afterMembers'])
                    if prior_before is None or prior_after is None or not servers <= prior_after:
                        return False
                    if prior_before != prior_after:
                        # The authenticated direct leader may exit after the prior FD
                        # proof. Permit only its exact original birth to disappear,
                        # leaving exactly the already proved server. This classifies
                        # history only; current birth/FD checks and the second complete
                        # stable observation below remain mandatory.
                        leader_birth = (leader['pid'], leader['start'])
                        if (child.pid != leader['pid'] or leader['pgid'] != leader['pid']
                                or leader_birth in servers or prior_after != servers
                                or prior_before != servers | {leader_birth}):
                            return False
                    # Handles are retained append-only until the outer owner retires.
                    # Exact original birth plus its still-retained pidfd and prior FD
                    # ownership classify only these inodes, never later listener sets.
                    prior_scans = [scan for scan in prior['fdScans']
                                   if (scan['before']['pid'],scan['before']['start']) in servers]
                    return len(prior_scans) == 1 and all(
                        scan['before']['pgid'] == leader['pgid']
                        and (scan['before']['pid'],scan['before']['start']) in handles
                        and scan['disposition'] == 'scanned' and not scan['missingReadlinks']
                        and not scan['socketInodes']['omitted']
                        and missing <= set(scan['socketInodes']['values']) for scan in prior_scans)
                # Only the already authenticated sole server can explain this known
                # temporal disappearance. Unknown/foreign/replaced birth, incomplete
                # FD evidence or any diagnostic error remains the original refusal.
                scans = observation['fdScans']
                disappeared = (attempt == 1 and not additional and len(servers) == 1
                    and prior_owned_disappearance()
                    and complete_births(before_members) == servers
                    and complete_births(after_members) == servers
                    and before_listeners is not None and not before_listeners['omitted']
                    and after_listeners is not None and not after_listeners['omitted']
                    and bool(scans) and all(scan['disposition'] == 'scanned'
                        and scan['missingReadlinks'] == 0 and not scan['socketInodes']['omitted']
                        and scan['afterMismatchObserved'] and scan['afterMismatchBirth'] is not None
                        and (scan['before']['pid'],scan['before']['start']) in handles
                        and scan['before'] == scan['afterMismatchBirth']
                        and scan['before']['state'] in ('R','S','D','T','t','I') for scan in scans)
                    and (inodes - matched).isdisjoint(set(after_listeners['values'])))
                if disappeared:
                    observation['disposition'] = 'changed'
                    observation['failure'] = failure_record(primary)
                    continue
                raise primary
            after_rows = membership(observation,'afterMembers')
            after_births = {(row['pid'],row['start']) for row in after_rows if row['pgid'] == leader['pgid']}
            if not proved <= after_births: raise ValueError('server membership changed during endpoint observation')
            after_inodes = listener_inodes(); observation['afterInodes'] = inode_summary(after_inodes)
            checkpoint()
            if after_inodes != inodes:
                observation['disposition'] = 'changed'
                if attempt == 1: continue
                raise ValueError('endpoint listeners changed during ownership observation')
            observation['disposition'] = 'proved'
            return
        except BaseException as error:
            observation['disposition'] = 'refused'; observation['failure'] = failure_record(error)
            raise


def execute_command(seconds, log_path, endpoint, argv):
    """Existing canonical adapter with one exact shared owner; no nested supervisor."""
    started = time.monotonic(); first = None; additional = []; receipt = None; returned = None
    output = Path(log_path).absolute(); owner_path = Path(__file__).with_name('shared_process_owner.py')
    spec = None; servers = set(); known = set(); original_exit = 1; exit_members = {'members':None}
    server_handles = {}; stop_requests = []; disk_samples = []; disk_omitted = 0; last_disk = None
    fd_denials = {'entries':[], 'omitted':0}
    endpoint_observations = {'entries':[], 'omitted':0}
    mutable_root = Path.cwd()
    def sample_disk(cutoff, force=False):
        nonlocal last_disk, disk_omitted
        now = time.monotonic()
        if spec is not None:
            current_tmp = Path(spec['mutableScope']['tmpdir'])
            require(current_tmp.resolve(strict=True) == current_tmp and current_tmp.is_dir(), 'tmp-scope-retargeted')
        if force or last_disk is None or now - last_disk >= .5:
            observed = census(mutable_root, min(cutoff, now + 1), limit=DISK_LIMIT - 384 * 1024)
            require(observed['objects'] < 25000, 'disk-report-entry-reserve')
            observed['reportReserveBytes'] = 384 * 1024
            observed['observedAt'] = time.monotonic()
            if len(disk_samples) == 64:
                disk_samples.pop(0); disk_omitted += 1
            disk_samples.append(observed); last_disk = observed['observedAt']
    def checkpoint():
        if time.monotonic() >= spec['workDeadline']:
            raise TimeoutError('original canonical work deadline')
    def listeners():
        found = set()
        for name in ('/proc/net/tcp', '/proc/net/tcp6'):
            lines = Path(name).read_text().splitlines()
            if len(lines) > 4096: raise ValueError('listener population')
            for line in lines[1:]:
                fields = line.split()
                if int(fields[1].split(':')[-1], 16) == endpoint and fields[3] == '0A':
                    found.add(fields[9])
        return found
    def control(event, child, leader, observed):
        if event == 'after-cleanup': return
        checkpoint()
        sample_disk(spec['workDeadline'], force=event in ('before-popen','after-birth','before-success'))
        if event == 'before-popen' and listeners(): raise ValueError('endpoint already listening')
        if leader is None: return
        observe_endpoint_ownership(owner, child, leader, servers, server_handles, known,
                                   checkpoint, spec['maximumGenerations'], listeners, additional, fd_denials,
                                   endpoint_observations, event)
        if event == 'child-exit-observed':
            retire_original_server(owner, child, leader, servers, server_handles, stop_requests, checkpoint, known, spec['maximumGenerations'], exit_members)
    try:
        seconds, endpoint = int(seconds), int(endpoint)
        argv = list(argv)
        tmpdir = mutable_root / 'tmp'
        if (output.parent != mutable_root or output.exists()
                or Path(os.environ.get('TMPDIR','')).absolute() != tmpdir
                or tmpdir.resolve(strict=True) != tmpdir or not tmpdir.is_dir()):
            raise ValueError('exclusive cwd/log/TMPDIR scope join')
        if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
            raise ValueError('original-server pidfd capability unavailable')
        model = Path(argv[2]); java = Path(os.environ['JAVA_HOME']) / 'bin/java'
        jar = Path(os.environ['QUINT_HOME']) / 'apalache-dist-0.56.1/apalache/lib/apalache.jar'
        pins = [pin_file(path) for path in (argv[0], model, java, jar, owner_path)]
        aliases = []
        for source in (Path(argv[0]), model, java, jar, owner_path):
            if not source.is_file(): raise ValueError('command input missing')
            for ancestor in [*reversed(source.absolute().parents), source.absolute()]:
                if ancestor.is_symlink():
                    row = {'path':str(ancestor), 'target':os.readlink(ancestor)}
                    if row not in aliases: aliases.append(row)
        if pins[-1]['sha256'] != OWNER_SHA: raise ValueError('shared owner pin')
        pins.append(pin_file(__file__))
        archive = os.environ.get('FSGG_QUINT_TOOLCHAIN_ARCHIVE')
        spec = dict(schema=COMMAND_SCHEMA, role='verify', argv=argv, pins=pins, aliases=aliases,
                    semanticToolchainId=TOOLCHAIN_ID, archiveSha256=pin_file(archive)['sha256'] if archive else None,
                    started=started, workDeadline=started+seconds, totalDeadline=started+seconds+30,
                    workSeconds=seconds, cleanupSeconds=30, rssBytes=6*1024**3, logBytes=8*1024**2,
                    maximumGenerations=6, endpoint=endpoint, jvmArgs=['-Xmx1024m', '-Xms64m'],
                    tlcConfig={'maxHeap':'-Xmx2G', 'stackSize':'-Xss64m', 'workers':1},
                    mutableScope={'cwd':str(mutable_root), 'tmpdir':str(tmpdir), 'diskBytes':DISK_LIMIT,
                                  'inventorySeconds':1, 'maximumEntries':25000, 'sampleIntervalSeconds':.5,
                                  'javaToolOptions':'-Djava.io.tmpdir='+str(tmpdir),
                                  'denoDir':str(mutable_root/'cache/deno'), 'xdgCacheHome':str(mutable_root/'cache')})
        command_preflight(spec, now=time.monotonic())
        for row in pins:
            if pin_file(row['path']) != row: raise ValueError('command input drift')
        for row in aliases:
            if os.readlink(row['path']) != row['target']: raise ValueError('command alias drift')
        config_path = Path(str(output)+'.tlc.json')
        with config_path.open('x') as stream: json.dump(spec['tlcConfig'], stream)
        if any(x.startswith('--tlc-config') or x.startswith('--server-endpoint') for x in argv):
            raise ValueError('duplicate resource or endpoint argument')
        actual_argv = argv + ['--tlc-config='+str(config_path), '--server-endpoint', 'localhost:'+str(endpoint)]
        spec['argv'] = actual_argv
        pins.append(pin_file(config_path))
        command_preflight(spec, now=time.monotonic())
        with Path(str(output)+'.input.json').open('x') as stream: json.dump(spec, stream, indent=2)
        module_spec = importlib.util.spec_from_file_location('shared_process_owner', owner_path)
        owner = importlib.util.module_from_spec(module_spec); module_spec.loader.exec_module(owner)
        env = {k:v for k,v in os.environ.items()
               if k in ('JAVA_HOME','QUINT_HOME','FSGG_QUINT_HOME','PATH','HOME','TMPDIR','LANG','LC_ALL','TZ')
               or k.startswith('FAKE_')}
        env['JVM_ARGS'] = ' '.join(spec['jvmArgs'])
        env['TMPDIR'] = spec['mutableScope']['tmpdir']
        env['JAVA_TOOL_OPTIONS'] = spec['mutableScope']['javaToolOptions']
        env['DENO_DIR'] = spec['mutableScope']['denoDir']
        env['XDG_CACHE_HOME'] = spec['mutableScope']['xdgCacheHome']
        stage_error = None
        try:
            # Count both retained log and its public copy, reserving metadata.
            log_cap = (spec['logBytes'] - 384 * 1024) // 2
            returned = owner.run(actual_argv, os.getcwd(), env, spec['workDeadline'], 30, spec['rssBytes'],
                                 str(output), log_cap, control=control, observe_child_exit=True)
        except BaseException as error: stage_error = error
        try:
            receipt = json.loads(Path(str(output)+'.result.json').read_text())
        except BaseException as error:
            if stage_error is not None:
                first = stage_error; additional.append(failure_record(error)); raise stage_error
            raise
        leader = receipt.get('leader')
        if (receipt.get('argv') != actual_argv or not isinstance(leader, dict)
                or leader.get('ppid') != os.getpid() or leader.get('pid') != leader.get('pgid')
                or leader.get('pid') != receipt.get('ownedPopenPid')
                or (returned is not None and returned != receipt)):
            if stage_error is not None:
                first = stage_error; additional.append(failure_record(ValueError('owner receipt identity')))
                raise stage_error
            raise ValueError('owner receipt identity')
        original_exit = receipt['exit'] if receipt['exit'] is not None else 1
        if stage_error is not None and not expected_server_retirement(receipt, servers, exit_members['members'], stop_requests): raise stage_error
        if (receipt['birthIdentityUnavailable'] or receipt['custody'] != 'observed-owned-group'
                or receipt['cleanup'] != 'observed-empty-group' or receipt['residual'] or receipt['escapedResidual']
                or receipt['additionalFailures'] or time.monotonic() >= spec['totalDeadline']):
            raise ValueError('command retirement unqualified')
        sample_disk(spec['totalDeadline'], force=True)
        for row in pins:
            if pin_file(row['path']) != row: raise ValueError('post command input drift')
        for row in aliases:
            if os.readlink(row['path']) != row['target']: raise ValueError('post command alias drift')
    except BaseException as error:
        if first is None: first = error
        elif error is not first: additional.append(failure_record(error))
    finally:
        for handle in server_handles.values():
            try: os.close(handle)
            except BaseException as error:
                if first is None: first = error
                else: additional.append(failure_record(error))
    try:
        if spec is not None and time.monotonic() >= spec['totalDeadline']:
            raise TimeoutError('original reporting deadline')
        if output.exists():
            with output.open() as stream:
                for line in stream: sys.stdout.write(line)
    except BaseException as error:
        if first is None: first = error
        else: additional.append(failure_record(error))
    result = dict(schema=RESULT_SCHEMA, commandExit=original_exit, commandAccepted=first is None,
                  firstCause=failure_record(first), additionalFailures=additional,
                  ownerReceipt=receipt, originalServerBirths=[{'pid':p, 'start':s} for p,s in sorted(servers)],
                  originalServerStopRequests=stop_requests, fdDenialObservations=fd_denials,
                  endpointObservations=endpoint_observations,
                  exitMembers=None if exit_members['members'] is None else [{'pid':p, 'start':s} for p,s in sorted(exit_members['members'])],
                  diskSamples=disk_samples, omittedDiskSamples=disk_omitted,
                  input=spec, cleanupObservedAt=time.monotonic(), modelCheckingRequired=True,
                  limitation='sampled current listener ownership and owned group; changes after the last observation are not covered; no hard containment or descendant reaping')
    try:
        if spec is not None and time.monotonic() >= spec['totalDeadline']:
            raise TimeoutError('original result deadline')
        if spec is not None: sample_disk(spec['totalDeadline'], force=True)
        result['omittedDiskSamples'] = disk_omitted
        body = json.dumps(result, ensure_ascii=True, indent=2)
        if len(body.encode()) > MAX_OUTPUT: raise ValueError('command result metadata cap')
        with Path(str(output)+'.command.json').open('x') as stream: stream.write(body+'\n')
    except BaseException as error:
        if first is None: first = error
        else: additional.append(failure_record(error))
        try:
            print(json.dumps({'firstCause':failure_record(first), 'additionalFailures':additional}), file=sys.stderr)
        except BaseException: pass
    if isinstance(first, TimeoutError): return 124
    return (original_exit if original_exit > 0 else 1) if first is not None else original_exit


if __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'command':
    if len(sys.argv)==6 and sys.argv[2]=='--proc':
        sys.exit(execute_proc_command(sys.argv[3],sys.argv[4],sys.argv[5]))
    if len(sys.argv) < 9 or sys.argv[5] != '--': sys.exit(64)
    sys.exit(execute_command(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[6:]))
elif __name__ == '__main__':
    try:
        capture(sys.argv[1], sys.argv[2], int(sys.argv[3]),
                max_output=int(sys.argv[4]) if len(sys.argv) == 5 else MAX_OUTPUT)
    except Exception:
        # Never echo errors, paths, input bytes or credentials into public logs.
        sys.exit(2)
