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
                     denial_observations=None):
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
        return []
    except PermissionError as error:
        return terminal_after_denial(error, 'directory')
    if len(entries) > 256: raise ValueError('server FD cap')
    targets = []
    for entry in entries:
        try: targets.append(os.readlink(entry))
        except FileNotFoundError: continue
        except PermissionError as error: return terminal_after_denial(error, 'readlink')
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
                                first = encoded[:FILE_BYTES]
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
    for attempt in (1,2):
        observation = {'event':event, 'attempt':attempt, 'disposition':'pending',
                       'beforeMembers':None, 'afterMembers':None, 'beforeInodes':None,
                       'afterInodes':None, 'matchedInodes':None, 'failure':None}
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
                for target in owned_fd_targets(row, owner.proc, additional,
                                              terminal_probe=probe, denial_observations=fd_denials):
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
            if matched != inodes: raise ValueError('foreign endpoint listener')
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
    if len(sys.argv) < 9 or sys.argv[5] != '--': sys.exit(64)
    sys.exit(execute_command(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[6:]))
elif __name__ == '__main__':
    try:
        capture(sys.argv[1], sys.argv[2], int(sys.argv[3]),
                max_output=int(sys.argv[4]) if len(sys.argv) == 5 else MAX_OUTPUT)
    except Exception:
        # Never echo errors, paths, input bytes or credentials into public logs.
        sys.exit(2)
