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
import base64

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

def public_command_evidence(scratch, status, end, clock, files_left, entries_left, known_names=()):
    """Optional exact public C2 metadata; never command or retry authority."""
    # Four command names, at most twelve absent-command presence probes, and
    # four counterpart/log reads. Previously
    # retained files count once; new files and all lookup operations are bounded.
    if files_left < 0 or entries_left < 20:
        raise ValueError('public-counterparts-population-bound')
    if clock() >= end: raise ValueError('public-counterparts-deadline')
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value: raise ValueError('public-counterparts-duplicate-key')
            value[key] = item
        return value
    def constant(_): raise ValueError('public-counterparts-nonfinite')
    def parsed(raw):
        return json.loads(raw.decode('utf-8'), object_pairs_hook=unique, parse_constant=constant)
    def read(directory, name, cap):
        nonlocal files_left,entries_left
        if clock() >= end: raise ValueError('public-counterparts-deadline')
        entries_left -= 1
        if name not in known_names: files_left -= 1
        if entries_left < 0 or files_left < 0:
            raise ValueError('public-counterparts-population-bound')
        fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        try:
            before = os.fstat(fd)
            if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= cap:
                raise ValueError('public-counterparts-byte-or-file-bound')
            body = os.read(fd, cap + 1)
            after = os.fstat(fd)
            identity = lambda row: (row.st_dev, row.st_ino, row.st_size, row.st_mtime_ns,
                                    row.st_ctime_ns, row.st_mode, row.st_uid, row.st_gid)
            if identity(before) != identity(after) or len(body) != before.st_size:
                raise ValueError('public-counterparts-changed')
            if clock() >= end: raise ValueError('public-counterparts-deadline')
            return body
        finally: os.close(fd)
    directory = os.open(scratch, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        latest = {}
        for lane, endpoint in (('O2HostedWriterChoreoProviderBounded',19820),
                               ('O2HostedWriterChoreoRunnerBounded',19822)):
            for attempt in (1,2):
                stem = f'{lane}.log.attempt-{attempt}'
                try: raw = read(directory, stem+'.command.json', 32768)
                except FileNotFoundError:
                    # Equal log bytes cannot identify a final attempt. Refuse
                    # partial later reports instead of substituting an older one.
                    for suffix in ('', '.input.json', '.result.json'):
                        if clock() >= end: raise ValueError('public-counterparts-deadline')
                        entries_left -= 1
                        if entries_left < 0: raise ValueError('public-counterparts-population-bound')
                        try: os.stat(stem+suffix, dir_fd=directory, follow_symlinks=False)
                        except FileNotFoundError: continue
                        raise ValueError('public-counterparts-incomplete-attempt')
                    continue
                value = parsed(raw)
                if not isinstance(value,dict) or value.get('schema') != RESULT_SCHEMA:
                    raise ValueError('public-counterparts-result-schema')
                if value.get('commandAccepted') is True:
                    latest[lane] = None
                    continue
                if value.get('commandAccepted') is not False or value.get('firstCause') is None:
                    raise ValueError('public-counterparts-result-shape')
                spec = value.get('input')
                # Reject private/PROC and arbitrary inputs before using the
                # existing pure public-profile validator. Recorded time is
                # shape data here, not a fresh launch or eligibility check.
                if (not isinstance(spec,dict) or 'profile' in spec
                        or spec.get('schema') != COMMAND_SCHEMA or spec.get('role') != 'verify'):
                    raise ValueError('public-counterparts-profile')
                command_preflight(spec, now=spec['started'])
                expected = [spec['argv'][0], 'verify', str(scratch/'choreo-c2-bounded.qnt'),
                            '--main='+lane, '--init=init', '--step=step', '--invariant=safety',
                            '--max-steps=20', '--backend=tlc', '--verbosity=3',
                            '--tlc-config='+str(scratch/(stem+'.tlc.json')),
                            '--server-endpoint', 'localhost:'+str(endpoint+attempt-1)]
                if (spec['argv'] != expected or spec['endpoint'] != endpoint+attempt-1
                        or spec['mutableScope']['cwd'] != str(scratch)
                        or type(value.get('commandExit')) is not int):
                    raise ValueError('public-counterparts-command-join')
                latest[lane] = (lane,stem,raw,value)
        candidates = [value for value in latest.values() if value is not None]
        if not candidates: raise ValueError('public-counterparts-unavailable')
        # Never silently choose an earlier failure that a later retry replaced.
        # One final failing tuple is required; ambiguity is an explicit omission.
        if len(candidates) != 1: raise ValueError('public-counterparts-ambiguous')
        lane,stem,raw,value = candidates[0]
        log = read(directory, lane+'.log', FILE_BYTES)
        if log != read(directory, stem, FILE_BYTES):
            raise ValueError('public-counterparts-final-log-join')
        input_raw = read(directory, stem+'.input.json', 8192)
        owner_raw = read(directory, stem+'.result.json', 8192)
        spec,owner = parsed(input_raw),parsed(owner_raw)
        if (spec != value['input'] or owner != value.get('ownerReceipt')
                or not isinstance(owner,dict) or owner.get('argv') != spec['argv']):
            raise ValueError('public-counterparts-cross-join')
        # Exact bytes are never redacted and then labelled exact. Reject the
        # whole optional tuple if it contains sensitive text or unknown fields.
        result_fields = {'schema','commandExit','commandAccepted','firstCause','additionalFailures',
            'ownerReceipt','originalServerBirths','originalServerStopRequests','fdDenialObservations',
            'endpointObservations','exitMembers','diskSamples','omittedDiskSamples','input',
            'cleanupObservedAt','modelCheckingRequired','limitation'}
        owner_fields = {'argv','leader','ownedPopenPid','birthIdentityUnavailable','exit','exitObservation',
            'firstCause','additionalFailures','cleanup','residual','escapedDescendants','escapedResidual',
            'custody','sampledPeakAggregateRss','accountingScope'}
        if (set(value) != result_fields or set(owner) != owner_fields
                or SENSITIVE.search(json.dumps(value,ensure_ascii=False))
                or any(SENSITIVE.search(body.decode('utf-8')) for body in (raw,input_raw,owner_raw))):
            raise ValueError('public-counterparts-private-or-unknown-fields')
        # Closed nested producer fields are a privacy check, not lifecycle or
        # retry eligibility. Scalars cannot conceal arbitrary nested records.
        scalar = None
        identity = dict.fromkeys(('pid','start','pgid','state'))
        process = dict.fromkeys(('pid','start','pgid','state','ppid','rss'))
        birth = dict.fromkeys(('pid','start'))
        failure = {'type':scalar,'message':scalar}
        inode = {'values':[scalar],'omitted':scalar}
        membership = {'members':[identity],'omittedMembers':scalar}
        scan = {'before':identity,'disposition':scalar,'missingReadlinks':scalar,
                'socketInodes':inode,'afterMismatchObserved':scalar,'afterMismatchBirth':identity}
        endpoint = dict.fromkeys(('event','attempt','disposition'))
        endpoint.update(beforeMembers=membership,afterMembers=membership,beforeInodes=inode,
                        afterInodes=inode,matchedInodes=inode,failure=failure,fdScans=[scan])
        denial = dict.fromkeys(('operation','terminalPollAttempted','terminalExit'))
        denial.update(before=identity,freshBeforeTerminalPoll=identity,
                      freshAfterTerminalPoll=identity,observationFailure=failure)
        def closed(item, shape):
            if item is None: return
            if isinstance(shape,dict):
                if not isinstance(item,dict) or set(item) != set(shape):
                    raise ValueError('public-counterparts-nested-fields')
                for key,child in shape.items(): closed(item[key],child)
            elif isinstance(shape,list):
                if not isinstance(item,list): raise ValueError('public-counterparts-nested-list')
                for child in item: closed(child,shape[0])
            elif type(item) not in (str,int,float,bool):
                raise ValueError('public-counterparts-nested-scalar')
        def closed_failure(item):
            if item is None: return
            if not isinstance(item,dict) or set(item) not in (set(failure),set(failure)|{'secondaryFailures'}):
                raise ValueError('public-counterparts-failure-fields')
            closed({k:item[k] for k in failure},failure)
            if 'secondaryFailures' in item: closed(item['secondaryFailures'],[failure])
        closed_failure(value['firstCause'])
        if not isinstance(value['additionalFailures'],list): raise ValueError('public-counterparts-failure-list')
        for item in value['additionalFailures']: closed_failure(item)
        for key,shape in {'originalServerBirths':[birth],
                'originalServerStopRequests':[dict.fromkeys(('pid','start','signal','disposition','at'))],
                'exitMembers':[birth],
                'fdDenialObservations':{'entries':[denial],'omitted':scalar},
                'endpointObservations':{'entries':[endpoint],'omitted':scalar}}.items():
            closed(value[key],shape)
        # Retain legacy exact samples; new mutable-temp samples explicitly report
        # incomplete observations without paths or arbitrary nested metadata.
        disk_shape=dict.fromkeys(('objects','logicalBytes','allocatedBytes','fixtureSymlinks',
                                 'linkTraversal','reportReserveBytes','observedAt'))
        if not isinstance(value['diskSamples'],list): raise ValueError('public-counterparts-disk-list')
        for sample in value['diskSamples']:
            shape=disk_shape
            if isinstance(sample,dict) and 'vanishedEntries' in sample:
                shape=dict(disk_shape,vanishedEntries=None,byteCoverage=None)
                if (type(sample['vanishedEntries']) is not int or not 0<=sample['vanishedEntries']<=25000
                        or sample.get('byteCoverage') != ('observed-only' if sample['vanishedEntries'] else 'sampled')):
                    raise ValueError('public-counterparts-disk-coverage')
            closed(sample,shape)
        for key in ('schema','commandExit','commandAccepted','omittedDiskSamples',
                    'cleanupObservedAt','modelCheckingRequired','limitation'): closed(value[key],scalar)
        owner_shape=dict.fromkeys(owner_fields)
        owner_shape.update(argv=[scalar],leader=process,additionalFailures=[scalar],
            exitObservation={'code':scalar,'members':[identity],'omittedMembers':scalar},
            residual=[process],escapedDescendants=[process],escapedResidual=[process])
        closed(owner,owner_shape)
        input_shape=dict.fromkeys(spec)
        input_shape.update(argv=[scalar],jvmArgs=[scalar],
            pins=[dict.fromkeys(('path','bytes','sha256'))],aliases=[dict.fromkeys(('path','target'))],
            mutableScope=dict.fromkeys(spec['mutableScope']),tlcConfig=dict.fromkeys(('maxHeap','stackSize','workers')))
        closed(spec,input_shape)
        # leader is the full /proc producer record; synthetic controls use it too.
        expected_status = 124 if value['firstCause']['type']=='TimeoutError' else max(1,value['commandExit'])
        if status != expected_status: raise ValueError('public-counterparts-final-status')
        files = [(stem+'.command.json',raw),(stem+'.input.json',input_raw),(stem+'.result.json',owner_raw)]
        evidence = {'scope':'public-canonical-verify-diagnostics-only','complete':True,
            'files':[{'name':name,'bytes':len(body),'sha256':hashlib.sha256(body).hexdigest(),
                      'base64':base64.b64encode(body).decode('ascii')} for name,body in files],
            'finalLog':{'name':lane+'.log','bytes':len(log),'sha256':hashlib.sha256(log).hexdigest()}}
        if len(json.dumps(evidence,ensure_ascii=True).encode()) > 69632:
            raise ValueError('public-counterparts-frame-bound')
        if clock() >= end: raise ValueError('public-counterparts-deadline')
        return evidence
    finally: os.close(directory)


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
    retained_names = set()
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
                    retained_names.add(str(relative))
        finally:
            os.close(fd)
    if not records:
        coverage.append('no-text-logs-observed')
    if pending or visited >= MAX_ENTRIES or len(records) >= MAX_FILES or clock() >= end:
        coverage.append('collection-bounded')
    document = {'schema': 'fsgg.choreo.failure-diagnostics/1', 'originalExitCode': status,
                'outcome': 'failed', 'logs': records, 'omittedCoverage': sorted(set(coverage)),
                'filter': 'sensitive-lines-omitted; closed-public-verify-metadata-only; no-environment-capture',
                'publicCommandEvidence': {'complete':False,'omitted':'original-report-budget'}}
    raw = (json.dumps(document, ensure_ascii=True, indent=2) + '\n').encode()
    while len(raw) > max_output and records:
        records.pop()
        document['omittedCoverage'] = sorted(set(document['omittedCoverage'] + ['output-byte-bound']))
        raw = (json.dumps(document, ensure_ascii=True, indent=2) + '\n').encode()
    if len(raw) > max_output:
        raise ValueError('diagnostic output bound')
    # Existing original cause/cleanup and client/server text take priority. The
    # optional complete tuple cannot evict them or renew the collection deadline.
    try:
        evidence = public_command_evidence(scratch, status, end, clock,
                                           MAX_FILES-len(retained_names), MAX_ENTRIES-visited,retained_names)
        candidate = dict(document, publicCommandEvidence=evidence)
        complete = (json.dumps(candidate,ensure_ascii=True,indent=2)+'\n').encode()
        if len(complete) > max_output: raise ValueError('public-counterparts-output-bound')
        if clock() >= end: raise ValueError('public-counterparts-deadline')
        document,raw = candidate,complete
    except (OSError,ValueError,TypeError,KeyError,IndexError,AttributeError,RecursionError) as error:
        omitted = dict(document, publicCommandEvidence={'complete':False,
            'omitted':str(error) if isinstance(error,ValueError) else 'public-counterparts-unavailable'})
        bounded = (json.dumps(omitted,ensure_ascii=True,indent=2)+'\n').encode()
        # Even omission reporting cannot displace the original retained evidence.
        if len(bounded) <= max_output: document,raw = omitted,bounded
    destination.mkdir(mode=0o700)
    f = os.open(destination / 'failure.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(f, 'wb') as output:
        output.write(raw)
    return document


DISK_LIMIT = 1073741824

def require(ok, message):
    if not ok: raise RuntimeError(message)

# Same non-following disk inventory as the qualified single-controller route.
def census(root,deadline,limit=DISK_LIMIT,clock=time.monotonic,*,future_fifos=None,mutable_tmp=None,expected_tmp_identity=None):
    # Only the public canonical adapter opts into advisory observation of its
    # declared temporary subtree. Strict held-namespace callers remain unchanged.
    logical=allocated=objects=links=vanished=attempts=0;stack=[root]
    def tmp_identity():
        require(mutable_tmp.resolve(strict=True)==mutable_tmp,'tmp-scope-retargeted')
        s=mutable_tmp.lstat()
        require(stat.S_ISDIR(s.st_mode),'tmp-scope-retargeted')
        return (s.st_dev,s.st_ino,s.st_mode,s.st_uid,s.st_gid)
    if mutable_tmp is not None:
        require(future_fifos is None and mutable_tmp.is_absolute()
                and mutable_tmp!=root and mutable_tmp.is_relative_to(root),'tmp-scope-join')
        original_tmp=tmp_identity()
        require(expected_tmp_identity is None or original_tmp==expected_tmp_identity,
                'tmp-scope-retargeted')
    def vanished_tmp(path):
        # Root/ancestor/pinned-source failures are never converted into samples.
        if mutable_tmp is None or path==mutable_tmp or not path.is_relative_to(mutable_tmp):return False
        require(tmp_identity()==original_tmp,'tmp-scope-retargeted')
        return True
    while stack:
        require(clock()<deadline,'disk-budget');path=stack.pop()
        if mutable_tmp is not None:
            attempts+=1;require(attempts<=25000,'disk-population-cap')
        try:s=path.lstat()
        except FileNotFoundError:
            if not vanished_tmp(path):raise
            vanished+=1;continue
        objects+=1
        require(objects<=25000,'disk-population-cap');logical+=s.st_size;allocated+=s.st_blocks*512
        require(max(logical,allocated)<=limit,'disk-byte-cap')
        if stat.S_ISDIR(s.st_mode):
            try:stack.extend(path.iterdir())
            except FileNotFoundError:
                if not vanished_tmp(path):raise
                vanished+=1
        elif stat.S_ISLNK(s.st_mode):links+=1
        elif stat.S_ISFIFO(s.st_mode) and future_fifos is not None:
            expected=future_fifos.get(str(path))
            observed=(s.st_dev,s.st_ino,s.st_uid,s.st_gid,s.st_mode,s.st_nlink,s.st_size)
            require(expected is not None and tuple(expected)==observed and s.st_uid==os.geteuid()
                    and stat.S_IMODE(s.st_mode)==0o600 and s.st_nlink==1,'disk-special-file')
        else:require(stat.S_ISREG(s.st_mode),'disk-special-file')
    result={'objects':objects,'logicalBytes':logical,'allocatedBytes':allocated,'fixtureSymlinks':links,'linkTraversal':False}
    if mutable_tmp is not None:
        require(tmp_identity()==original_tmp,'tmp-scope-retargeted')
        result.update(vanishedEntries=vanished,byteCoverage='observed-only' if vanished else 'sampled')
    return result

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
    record={'type': type(error).__name__[:96], 'message': filtered(message.encode(), Path('/nonexistent'))[:512]}
    # The private writer preserves the original write/fsync error and one close
    # error. Project that existing bounded carrier through every result caller.
    secondary=getattr(error,'proc_report_secondary',None)
    if secondary is not None:record['secondaryFailures']=[secondary]
    return record


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


PROC_PROFILES = {'proc-publisher-promotion': 'promote-execution-publisher-b11fb121-original38495729817',
                 'proc-pair-policy-observation': 'observe-execution-pair-policy-21633398',
                 'proc-pair-recovery-dispatch': 'recover-execution-pair-030-b11fb121-original38495729817',
                 'proc-pair-client-qualification': 'qualify-proc-pair-curl-8-22-0',
                 'proc-pair-native-watch': None}
PROC_CURL_SHA = '450474f8939a71e297282400fc83176a9532da9f8ab84524c2f2ed42e96748cc'
PROC_STRACE_SHA = '28ed5493ecf611b83ac0d0398cc1c4f09a228afaa7fce4737d14c8599cc796e2'
PROC_INSTRUMENTATION = {'boundarySha256':'995c2509d964896b20af74b1449fc1fa2fdd25f42867c09d8eb6703601865f4a',
    'packageSha256':'4e51a26cf0f69d959d96bb629b848892ab248f5c522981e391dfa302ac44312e',
    'sourceSha256':'4bde6246926890dcee824f6e6ac42a06752f47d77e5097d86e3c0d6d4b709fe5',
    'clientSha256':PROC_STRACE_SHA,'sourceProbeBound':2}
PROC_QUALIFICATION_CREDENTIAL='root-existing-GH_TOKEN/read-only-original-collector'
PROC_OPENSSL_CONFIG = b'openssl_conf = proc_init\n[proc_init]\n'
PROC_QUALIFICATION_CASES = ('valid','301','302','303','307','308','401','417','429','500','early-close','partial-response')
PROC_QUALIFICATION_ORIGIN = 'https://127.0.0.1:18443'
PROC_RUNTIME_DATA = {'contract':'closed-existing-owner-kernel-data/1',
    'bootId':'/proc/sys/kernel/random/boot_id','procfs':'/proc',
    'processStat':'existing-owner-current-pid-census-for-known-group-classification',
    'ownedFd':'actual-known-pid-birth-only','cgroup':'/sys/fs/cgroup',
    'accounting':['cpu.max','memory.max','memory.current'],'selfMembership':'/proc/self/cgroup',
    'mountInfo':'/proc/self/mountinfo',
    'actorMetadata':'birth-joined traced controller/descendant status, task, namespace and executable metadata only',
    'scope':'volatile metadata, never immutable source pins, membership adoption, signal authority or reservation release'}
PROC_ORIGINAL = 'publish-execution-pair-030-b11fb121-four38484632631'
PROC_REPOSITORY = 'FS-GG/FS.GG.Coordination'
PROC_PUBLISHER_REF = 'refs/tags/execution/v0.3.0-publisher-original38495729817'
PROC_WORKFLOW = '.github/workflows/callable-cli-release-publish.yml'
# Producer source pins this persistent namespace. No descriptor, authority path,
# output, cwd, HOME or environment value can select another operation namespace.
PROC_OPERATION_BASE = Path('/var/tmp/fsgg-canonical-owned-commands')


def proc_operation_root(operation,qualification_attempt=None):
    lineage=PROC_OPERATION_BASE / str(os.geteuid()) / operation
    if qualification_attempt is None:return lineage
    if operation!=PROC_PROFILES['proc-pair-client-qualification'] or type(qualification_attempt) is not int or not 1<=qualification_attempt<=4:
        raise ValueError('PROC fixed qualification lineage/ordinal')
    return lineage/('attempt-'+str(qualification_attempt))


def proc_support_root():
    # Explicit root-prepared immutable support shared across the closed roles.
    return PROC_OPERATION_BASE / str(os.geteuid()) / 'support'


def proc_read_identity(path):
    """Metadata identity, not file content or preventive containment."""
    observed=Path(path).lstat()
    return {'device':observed.st_dev,'inode':observed.st_ino,'uid':observed.st_uid,
            'mode':stat.S_IMODE(observed.st_mode),'kind':stat.S_IFMT(observed.st_mode)}


def proc_absence(path):
    """Nearest no-follow existing ancestor and the complete missing chain."""
    path=Path(path)
    if not path.is_absolute() or any(part in ('.','..') for part in path.parts):
        raise ValueError('PROC negative lookup path')
    missing=[];current=path
    while True:
        try:identity=proc_read_identity(current)
        except FileNotFoundError:
            missing.insert(0,current.name);current=current.parent;continue
        if current.is_symlink() or not current.is_dir() or not missing:
            raise ValueError('PROC negative lookup ancestor/absence changed')
        for ancestor in current.parents:
            if ancestor.is_symlink():raise ValueError('PROC negative lookup ancestor alias')
        return {'path':str(path),'errno':'ENOENT','ancestor':str(current),
                'ancestorIdentity':identity,'missingComponents':missing}


def proc_read_drift(spec):
    """Typed exact-profile file/directory/alias/negative observations, rechecked."""
    evidence=spec['readEvidence']
    if set(evidence)!={'contract','directories','aliases','absences','runtimeData'} or evidence['contract']!='ordinary-exact-profile-observation-and-drift':
        raise ValueError('PROC typed read contract')
    if evidence['runtimeData']!=PROC_RUNTIME_DATA:raise ValueError('PROC closed volatile kernel DATA contract')
    for category in ('directories','aliases','absences'):
        if not isinstance(evidence[category],list) or len(evidence[category])>512:raise ValueError('PROC read observation bound')
    for row in evidence['aliases']:
        if (set(row)!={'path','target','resolved','identity'} or not Path(row['path']).is_symlink()
                or os.readlink(row['path'])!=row['target'] or str(Path(row['path']).resolve(strict=True))!=row['resolved']
                or proc_read_identity(row['path'])!=row['identity']):raise ValueError('PROC alias resolution drift')
    for row in evidence['directories']:
        if set(row)!={'path','identity','relevantEntries','completePopulation'} or type(row['completePopulation']) is not bool or proc_read_identity(row['path'])!=row['identity'] or not Path(row['path']).is_dir():
            raise ValueError('PROC directory identity drift')
        if not isinstance(row['relevantEntries'],dict) or len(row['relevantEntries'])>4096:raise ValueError('PROC relevant directory population bound')
        if row['completePopulation'] and {entry.name for entry in Path(row['path']).iterdir()}!=set(row['relevantEntries']):
            raise ValueError('PROC complete relevant directory population drift')
        for name,identity in row['relevantEntries'].items():
            if not name or name in ('.','..') or '/' in name:raise ValueError('PROC relevant entry name')
            path=Path(row['path'])/name
            try:actual=proc_read_identity(path)
            except FileNotFoundError:actual=None
            if actual!=identity:raise ValueError('PROC relevant directory entry drift')
    for row in evidence['absences']:
        if set(row)!={'path','errno','ancestor','ancestorIdentity','missingComponents'} or proc_absence(row['path'])!=row:
            raise ValueError('PROC negative lookup ancestor/missing chain drift')
    declared={row['path']:row for row in evidence['aliases']}
    for pin in spec['pins']:
        if pin['path'].startswith(('/proc/','/sys/fs/cgroup/')):raise ValueError('PROC volatile kernel DATA cannot be immutable source pin')
        path=Path(pin['path'])
        for ancestor in (path,*path.parents):
            if ancestor.is_symlink() and str(ancestor) not in declared:raise ValueError('PROC undeclared source/runtime alias')
        actual=pin_file(path);actual['path']=pin['path'];actual['mode']=stat.S_IMODE(path.stat().st_mode)
        if actual!=pin:raise ValueError('PROC source/runtime input drift')


def proc_curl_argv(spec, slot, endpoint, method, *, body=None, authorization=True, fixture=False, remaining=None, name='response'):
    """One literal URL/transfer. No redirect, negotiation, retry or option tail."""
    if remaining is None:remaining=spec['workDeadline']-spec['started']
    seconds=max(1,min(30,int(remaining)))
    if not re.fullmatch('[a-z0-9.-]+',name):raise ValueError('PROC response name')
    if remaining<=0:raise TimeoutError('PROC curl original transfer budget exhausted')
    transport=spec['transport'];root=Path(spec['operationRoot']);slot=Path(slot)
    if fixture:
        if spec['profile']!='proc-pair-client-qualification' or endpoint not in PROC_QUALIFICATION_CASES:
            raise ValueError('PROC fixture transport cannot flow into production')
        url=PROC_QUALIFICATION_ORIGIN+'/proc-qualification/'+endpoint;ca=str(root/'fixture-ca.pem')
    else:
        if method not in ('GET','POST') or not (endpoint=='repos/'+PROC_REPOSITORY or endpoint.startswith('repos/'+PROC_REPOSITORY+'/')):
            raise ValueError('PROC literal production origin')
        url='https://api.github.com/'+endpoint;ca=transport['caBundle']
    argv=[transport['client'],'-q','--silent','--show-error','--http1.1','--proto','=https','--proto-redir','=https',
          '--max-redirs','0','--globoff','--proxy','','--noproxy','*','--request',method,
          '--header','Accept: application/vnd.github+json','--header','X-GitHub-Api-Version: 2026-03-10',
          '--header','Expect:','--cacert',ca,'--capath',str(proc_support_root()/'ca-empty'),
          '--connect-timeout',str(min(3,seconds)),'--max-time',str(seconds),'--max-filesize',str(spec['logBytes']),
          '--output',str(slot/(name+'.body')),'--write-out',
          '{"httpCode":"%{http_code}","effectiveUrl":"%{url_effective}","redirects":%{num_redirects},"sslVerifyResult":%{ssl_verify_result}}\n']
    if authorization:argv+=['--header','@'+str(slot/'authorization.header')]
    if body is not None:argv+=['--header','Content-Type: application/json','--data-binary','@'+str(body)]
    argv.append(url)
    return argv


def proc_sensitive_header(slot, value, *, deadline=None):
    """Ephemeral semantic input. Never hash or report credential bytes."""
    if not isinstance(value,str) or not 1<=len(value.encode())<=8192 or any(c in value for c in '\r\n\x00') or not value.isascii():
        raise ValueError('PROC approved binding has invalid header shape')
    return proc_sensitive_input(slot,'authorization.header',('Authorization: Bearer '+value+'\n').encode(),deadline=deadline)


def proc_sensitive_input(slot,name,raw,*,deadline=None):
    if name not in ('authorization.header','storage-url.conf') or not isinstance(raw,bytes) or not 0<len(raw)<=16448:
        raise ValueError('PROC closed ephemeral input shape')
    path=Path(slot)/name
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    identity=None;creation_error=None
    try:
        observed=os.fstat(fd)
        identity={'device':observed.st_dev,'inode':observed.st_ino,'uid':observed.st_uid,
                  'mode':stat.S_IMODE(observed.st_mode),'kind':stat.S_IFMT(observed.st_mode)}
        if identity['mode']!=0o600 or identity['uid']!=os.geteuid() or identity['kind']!=stat.S_IFREG or observed.st_nlink!=1:
            raise ValueError('PROC ephemeral creation owner/mode/type refusal')
        offset=0
        while offset<len(raw):
            if deadline is not None and time.monotonic()>=deadline:raise TimeoutError('PROC ephemeral header creation deadline')
            count=os.write(fd,raw[offset:])
            if count<=0:raise OSError('PROC ephemeral header zero write')
            offset+=count
        os.fsync(fd)
        if deadline is not None and time.monotonic()>=deadline:raise TimeoutError('PROC ephemeral creation original deadline')
        if proc_read_identity(path)!=identity:raise ValueError('PROC ephemeral header creation identity drift')
        return {'path':str(path),'identity':proc_read_identity(path),'disposition':'live-ephemeral','_expectedBytes':raw}
    except BaseException as error:
        creation_error=error
        # No child has received this path. Remove only the authenticated exclusive
        # object; otherwise retain a private hold. Preserve the write first cause.
        disposition='held-creation-failure'
        try:
            if identity is not None and proc_read_identity(path)==identity:
                path.unlink();disposition='removed-before-child'
        except OSError:pass
        error.proc_sensitive_creation={'disposition':disposition,'identity':identity}
        raise
    finally:
        try:os.close(fd)
        except BaseException as error:
            if creation_error is not None:
                creation_error.proc_sensitive_close_failure=failure_record(error)
            else:
                disposition='held-creation-failure'
                try:
                    if identity is not None and proc_read_identity(path)==identity:
                        path.unlink();disposition='removed-before-child'
                except OSError:pass
                error.proc_sensitive_creation={'disposition':disposition,'identity':identity}
                raise


def proc_sensitive_unchanged(header):
    path=Path(header['path'])
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW)
    try:
        actual=os.fstat(fd)
        identity={'device':actual.st_dev,'inode':actual.st_ino,'uid':actual.st_uid,
                  'mode':stat.S_IMODE(actual.st_mode),'kind':stat.S_IFMT(actual.st_mode)}
        if identity!=header['identity'] or actual.st_nlink!=1 or actual.st_size!=len(header['_expectedBytes']) or os.read(fd,16448+1)!=header['_expectedBytes']:
            raise ValueError('PROC ephemeral single-header input drift; preserve private hold')
    finally:os.close(fd)


def proc_sensitive_retire(header, receipt):
    if header is None:return {'disposition':'not-created'}
    if not receipt or receipt.get('cleanup')!='observed-empty-group' or receipt.get('custody')!='observed-owned-group':
        return {'disposition':'held-uncertain-retirement','identity':header['identity']}
    path=Path(header['path'])
    proc_sensitive_unchanged(header)
    path.unlink()
    return {'disposition':'removed-after-qualified-retirement','identity':header['identity']}


def proc_curl_response(slot,name,expected_url,cap,expected_status='200'):
    slot=Path(slot);meta=slot/(name+'.log');body=slot/(name+'.body')
    if meta.is_symlink() or body.is_symlink() or not 0<meta.stat().st_size<=8192 or not 0<=body.stat().st_size<=cap:
        raise ValueError('PROC curl response bound/alias')
    value=json.loads(meta.read_bytes())
    if (set(value)!={'httpCode','effectiveUrl','redirects','sslVerifyResult'} or value['httpCode']!=expected_status
            or type(value['httpCode']) is not str or value['effectiveUrl']!=expected_url or value['redirects']!=0
            or value['sslVerifyResult']!=0):raise ValueError('PROC curl HTTP/origin/redirect/trust fence')
    return body.read_bytes()


def proc_read_status(status,storage=False):
    if not isinstance(status,str) or not re.fullmatch('[0-9]{3}',status):return 'malformed-status'
    if status=='401':return 'access-authentication-refused'
    if status=='403':return 'storage-access-indeterminate' if storage else 'access-permission-refused'
    if status=='404':return 'native-evidence-unavailable'
    if status=='410':return 'native-evidence-expired'
    if status in ('301','302','303','307','308'):return 'second-redirect-refused' if storage else 'redirect-status-refused'
    return 'unexpected-status-'+status


def proc_read_failure(stage,status,message):
    error=ValueError(message)
    error.proc_read_status={'stage':stage,'httpStatus':status if isinstance(status,str) and re.fullmatch('[0-9]{3}',status) else None,
                           'disposition':proc_read_status(status,storage=stage=='storage')}
    return error


PROC_STORAGE_HOST='productionresultssa18.blob.core.windows.net'
PROC_STORAGE_KEYS={'rsct','se','sig','ske','skoid','sks','skt','sktid','skv','sp','spr','sr','st','sv'}


def proc_storage_location(raw,*,archive,api_date,now_epoch):
    """Closed observed SAS grammar. Never include this input in error/receipt/hash."""
    if (not isinstance(raw,str) or not 0<len(raw.encode())<=16384
            or any(ord(c)<=32 or ord(c)>=127 or c in '\\"#' for c in raw)
            or not raw.startswith('https://'+PROC_STORAGE_HOST+'/')):
        raise ValueError('PROC storage location origin/shape refusal')
    rest=raw[len('https://'+PROC_STORAGE_HOST):]
    if '?' not in rest or rest.count('?')!=1 or not re.fullmatch(r'/[A-Za-z0-9._~/%!$&()*+,;=:@-]+\?[^#]+',rest):
        raise ValueError('PROC storage location path/query refusal')
    path,query=rest.split('?',1)
    checked_path=re.sub('%2[eE]','.',path)
    if any(part in ('.','..') for part in checked_path.split('/')) or re.search(r'%(?![0-9A-Fa-f]{2})',rest):
        raise ValueError('PROC storage location encoding refusal')
    fields={}
    for part in query.split('&'):
        if '=' not in part:raise ValueError('PROC storage query shape refusal')
        key,value=part.split('=',1)
        if key in fields or not key or not value:raise ValueError('PROC storage query duplicate/empty refusal')
        fields[key]=value
    if set(fields)!=PROC_STORAGE_KEYS|({'rscd'} if archive else set()):raise ValueError('PROC storage query version/inventory refusal')
    if any(fields[k]!=v for k,v in {'sp':'r','spr':'https','sr':'b','sv':'2025-11-05'}.items()):
        raise ValueError('PROC storage signed permissions/protocol/version refusal')
    # Only safe timestamp fields are decoded. The signature is never inspected.
    from datetime import datetime,timezone
    def epoch(value):
        value=value.replace('%3A',':').replace('%3a',':')
        if not re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ',value):raise ValueError('PROC storage timestamp shape refusal')
        return datetime.strptime(value,'%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=timezone.utc).timestamp()
    try:date=datetime.strptime(api_date,'%a, %d %b %Y %H:%M:%S GMT').replace(tzinfo=timezone.utc).timestamp()
    except (TypeError,ValueError):raise ValueError('PROC storage API Date unavailable') from None
    expiry=epoch(fields['se']);start=epoch(fields['st'])
    if abs(date-now_epoch)>60 or start>date+300 or expiry<=max(date,now_epoch)+10 or expiry-date>900:
        raise ValueError('PROC storage signed lifetime/clock consistency refusal')
    return min(60,expiry-now_epoch-5)


def proc_redirect_headers(path):
    path=Path(path)
    if path.is_symlink() or not 0<path.stat().st_size<=65536 or stat.S_IMODE(path.stat().st_mode)!=0o600:
        raise ValueError('PROC private API redirect headers unavailable')
    raw=path.read_bytes()
    if b'\x00' in raw:raise ValueError('PROC API redirect header framing refusal')
    lines=raw.decode('ascii','strict').split('\r\n')
    if not lines[0].startswith('HTTP/1.1 302 ') or any(line.startswith((' ','\t')) for line in lines):
        raise ValueError('PROC API redirect status/folding refusal')
    fields={}
    for line in lines[1:]:
        if not line:continue
        if ':' not in line:raise ValueError('PROC API redirect header framing refusal')
        key,value=line.split(':',1);key=key.lower()
        fields.setdefault(key,[]).append(value.strip())
    if len(fields.get('location',[]))!=1 or len(fields.get('date',[]))!=1:
        raise ValueError('PROC API redirect Location/Date cardinality refusal')
    return fields['location'][0],fields['date'][0]


def proc_storage_argv(spec,slot,name,remaining):
    if name not in ('terminal-job','terminal-archive') or remaining<=0:raise ValueError('PROC storage role/deadline refusal')
    seconds=max(1,min(30,int(remaining)))
    return [spec['transport']['client'],'-q','--config',str(Path(slot)/'storage-url.conf'),
            '--silent','--stderr','/dev/null','--http1.1','--proto','=https','--proto-redir','=https',
            '--max-redirs','0','--globoff','--path-as-is','--proxy','','--noproxy','*','--request','GET','--header','Expect:',
            '--cacert',spec['transport']['caBundle'],'--capath',str(proc_support_root()/'ca-empty'),
            '--connect-timeout',str(min(3,seconds)),'--max-time',str(seconds),'--max-filesize',str(spec['logBytes']),
            '--output',str(Path(slot)/(name+'.body')),'--write-out',
            '{"httpCode":"%{http_code}","redirects":%{num_redirects},"sslVerifyResult":%{ssl_verify_result},"bytes":%{size_download}}\n']


def proc_archive_shape(raw,*,deadline=None):
    import io,zipfile
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        rows=archive.infolist()
        if not 0<len(rows)<=128 or sum(row.file_size for row in rows)>8*1024**2:
            raise ValueError('PROC archive population/expanded bound')
        names=set()
        for row in rows:
            path=Path(row.filename)
            if (row.filename in names or path.is_absolute() or '..' in path.parts or '\\' in row.filename
                    or '\x00' in row.filename or row.flag_bits&1 or stat.S_ISLNK(row.external_attr>>16)):
                raise ValueError('PROC archive member framing/path/type refusal')
            names.add(row.filename)
        expanded=0
        for row in rows:
            with archive.open(row) as source:
                while True:
                    if deadline is not None and time.monotonic()>=deadline:raise TimeoutError('PROC original archive inspection deadline')
                    chunk=source.read(65536)
                    if not chunk:break
                    expanded+=len(chunk)
                    if expanded>8*1024**2:raise ValueError('PROC actual archive expanded bound')


def proc_directory_identity(path):
    path=Path(path)
    if any(p.is_symlink() for p in (path,*path.parents)) or not path.is_dir():
        raise ValueError('PROC canonical state missing/aliased; reconciliation required')
    observed=path.stat()
    if observed.st_uid!=os.geteuid() or stat.S_IMODE(observed.st_mode)!=0o700:
        raise ValueError('PROC canonical state owner/mode differs; reconciliation required')
    return {'path':str(path),'device':observed.st_dev,'inode':observed.st_ino,'uid':observed.st_uid,'mode':0o700}


def proc_command_preflight(spec, *, now):
    """Closed operation contracts. A valid shape never supplies launch authority."""
    fields = {'schema','profile','role','campaign','item','attempt','originalOperation','operation',
              'publisher','repositoryId','workflowId','workflowPath','source','originalPublisher',
              'request','argv','transport','readEvidence','pins','proofInputs','sourceReadAllowlist','environment','operationRoot','qualificationAttempt',
              'started','workDeadline','totalDeadline','cleanupSeconds','rssBytes','addressSpaceBytes','logBytes','diskBytes','cpuAffinity'}
    if set(spec) != fields or spec['schema'] != COMMAND_SCHEMA or spec['profile'] not in PROC_PROFILES:
        raise ValueError('PROC command schema/profile')
    expected_role = 'qualification' if spec['profile']=='proc-pair-client-qualification' else ('watch' if spec['profile'] == 'proc-pair-native-watch' else 'dispatch')
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
    if spec['profile']=='proc-pair-native-watch' and spec['operation'] not in (PROC_PROFILES['proc-pair-policy-observation'],PROC_PROFILES['proc-pair-recovery-dispatch']):
        raise ValueError('PROC qualification operation cannot become native effect watch')
    if spec['operation'] not in tuple(value for value in PROC_PROFILES.values() if value is not None):
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
    ordinal=spec['qualificationAttempt']
    if (spec['role']=='qualification' and (type(ordinal) is not int or not 1<=ordinal<=4)
            or spec['role']!='qualification' and ordinal is not None):
        raise ValueError('PROC root-selected qualification ordinal cannot become effect retry')
    root = Path(spec['operationRoot'])
    if root!=proc_operation_root(spec['operation'],ordinal):
        raise ValueError('PROC fixed operation root')
    environment = spec['environment']
    if (set(environment)!={'HOME','TMPDIR','PATH','LANG','LC_ALL','TZ','OPENSSL_CONF'}
            or environment['HOME']!=str(root/'home') or environment['TMPDIR']!=str(root/'tmp')
            or environment['PATH']!='/usr/bin' or environment['LANG']!='C' or environment['LC_ALL']!='C'
            or environment['TZ']!='UTC' or environment['OPENSSL_CONF']!=str(proc_support_root()/'openssl.cnf')
            or any(type(v) is not str or '\x00' in v for v in environment.values())):
        raise ValueError('PROC closed curl environment/config exclusions')
    transport=spec['transport']
    if (set(transport)!={'client','caBundle','tracer'} or any(not isinstance(v,str) or not Path(v).is_absolute() for v in transport.values())):
        raise ValueError('PROC closed curl support selection')
    if spec['argv']!=[transport['client']]:raise ValueError('PROC caller option tails forbidden')
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
    proof_keys={'admission','originalRun','originalJobs','originalArtifact','originalArchive','verifiedAttestation','consumerRoot','sourceCorrespondence'}
    if spec['profile']=='proc-publisher-promotion':proof_keys.add('promotionEvidence')
    proof_keys.add('bootstrapEvidence' if spec['role']=='qualification' else 'runtimeQualification')
    if set(spec['proofInputs']) != proof_keys:
        raise ValueError('PROC proof input population')
    pinned = {p['path'] for p in spec['pins']}
    if any(path not in pinned for key,path in spec['proofInputs'].items() if key != 'consumerRoot'):
        raise ValueError('PROC proof input not pinned')
    if any(path not in pinned for path in (*transport.values(),environment['OPENSSL_CONF'])):
        raise ValueError('PROC transport/config outside input closure')
    if any(Path(path).name=='authorization.header' for path in pinned):raise ValueError('PROC credential excluded from immutable proof pins')
    request = spec['request']
    if spec['role']=='qualification':
        expected={'method':'GET','endpoint':'repos/'+PROC_REPOSITORY,'apiVersion':'2026-03-10','body':None,
                  'requestLimit':5,'nativeGet':request.get('nativeGet'),'credentialReference':PROC_QUALIFICATION_CREDENTIAL if request.get('nativeGet') else None,
                  'cases':list(PROC_QUALIFICATION_CASES)}
        if type(request.get('nativeGet')) is not bool:raise ValueError('PROC qualification native GET admission selection')
    elif spec['profile']=='proc-publisher-promotion':
        expected={'method':'POST','endpoint':'repos/'+PROC_REPOSITORY+'/git/refs','apiVersion':'2026-03-10',
                  'body':{'ref':PROC_PUBLISHER_REF,'sha':spec['publisher']},'requestLimit':1}
    elif spec['role'] == 'dispatch':
        inputs = {'execution_pair':True,'pair_recovery':spec['profile']=='proc-pair-recovery-dispatch',
                  'preflight_only':spec['profile']=='proc-pair-policy-observation','cli_only':False,'readback_only':False,
                  'publisher_source':spec['publisher'],'operation':spec['operation'],'source':spec['source'],
                  'protected_merge':spec['source'],'expected_sha256':'c8772596977e2eb172672fa393d78daab5867aefcf24aa9960a1c5627d4f7ca8'}
        expected = {'method':'POST','endpoint':'repos/'+PROC_REPOSITORY+'/actions/workflows/361038177/dispatches',
                    'apiVersion':'2026-03-10','body':{'ref':PROC_PUBLISHER_REF[10:],'inputs':inputs},'requestLimit':1}
    else:
        if (set(request) != {'method','endpoint','apiVersion','body','requestLimit','runId'}
                or request['runId'] is not None and (type(request['runId']) is not int or request['runId'] <= 0)):
            raise ValueError('PROC watch native identity')
        endpoint = ('repos/'+PROC_REPOSITORY+'/actions/runs/'+str(request['runId']) if request['runId'] is not None
                    else 'repos/'+PROC_REPOSITORY+'/actions/runs?event=workflow_dispatch&head_sha='+spec['publisher']+'&per_page=100')
        expected = {'method':'GET','endpoint':endpoint,
                    'apiVersion':'2026-03-10','body':None,'requestLimit':7,'runId':request['runId']}
    if request != expected:
        raise ValueError('PROC exact endpoint/method/argv')
    if spec['argv'][0] not in pinned: raise ValueError('PROC client outside runtime closure')
    if len(json.dumps(spec).encode()) > 256*1024: raise ValueError('PROC descriptor bound')
    return {'schema':COMMAND_SCHEMA,'passed':True,'role':spec['role'],'publicationAuthorized':False}


PROC_PUBLISHER_PATHS = frozenset((
    '.github/workflows/callable-cli-release-prepare.yml', '.github/workflows/callable-cli-release-publish.yml',
    'eng/test-callable-cli-installed-harness.py', 'eng/execution-pair-release-admission.json',
    'eng/write-callable-cli-release-readback.py', 'eng/repository-settings/desired.json',
    'eng/repository-settings/release-tag-ruleset.json', 'tests/portable-workspace/release/test_execution_pair_release.py',
    'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json',
    'eng/retain-choreo-c2-diagnostics.py', 'eng/tests/choreo-c2-tlc-retry/test_diagnostics.py'))


def proc_source_correspondence(spec, correspondence):
    if (correspondence.get('publisher')!=spec['publisher'] or correspondence.get('source')!=spec['source']
            or correspondence.get('publisherRef')!=PROC_PUBLISHER_REF or correspondence.get('workflowPath')!=PROC_WORKFLOW
            or correspondence.get('immutablePublisherVerified') is not True
            or correspondence.get('originalPublisher')!=spec['originalPublisher'] or correspondence.get('protectedSourceVerified') is not True
            or correspondence.get('aToEExactThreePaths') is not True or correspondence.get('aToPublisherExactElevenPaths') is not True):
        raise ValueError('PROC current protected source correspondence absent')
    paths=correspondence.get('publisherPathDigests')
    if (not isinstance(paths,dict) or not paths or not set(paths)<=PROC_PUBLISHER_PATHS
            or correspondence.get('publisherPaths')!=sorted(paths)
            or any(not isinstance(d,str) or re.fullmatch('[0-9a-f]{64}',d) is None for d in paths.values())):
        raise ValueError('PROC exact publisher path population/digests absent')
    for path,digest in paths.items():
        pins=[p for p in spec['pins'] if p['path'].endswith('/'+path)]
        if len(pins)!=1 or pins[0]['sha256']!=digest:
            raise ValueError('PROC publisher path digest differs from actual pinned source')


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
    value = json.loads(raw)
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
                and run.get('head_branch')==PROC_PUBLISHER_REF[10:] and run.get('display_title')=='PROC '+spec['operation']+' @ '+spec['publisher'])
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


def proc_join_qualification_birth(member,known,observe):
    current=observe(member['pid'])
    if current is None or current['start']!=member['start']:
        raise ValueError('PROC qualification PID birth changed during observation')
    existing=known.get(member['pid'])
    if existing is not None:
        if existing!=member['start']:raise ValueError('PROC qualification PID birth reuse')
        return
    parent=observe(member['ppid'])
    if (parent is None or known.get(parent['pid'])!=parent['start']
            or current is None or current['start']!=member['start']
            or current['ppid']!=parent['pid']):
        raise ValueError('PROC unknown qualification descendant parent/birth')
    known[member['pid']]=member['start']


def proc_capacity_evidence(spec,authority,capacity):
    cgroup=Path(capacity.get('directory','/nonexistent'))
    mounts=Path('/proc/self/mountinfo').read_text().splitlines()
    mounted=any(len(line.split())>6 and line.split()[4]=='/sys/fs/cgroup' and ' - cgroup2 ' in line for line in mounts)
    if (str(cgroup)!='/sys/fs/cgroup' or not mounted or Path('/proc/self/cgroup').read_text()!='0::/\n'
            or capacity.get('independentPrivateScope') is not True or capacity.get('heldStoresUsed') is not False
            or capacity.get('activeWorkers')!=authority.get('activeWorkers')
            or proc_read_identity(cgroup)!=capacity.get('identity')
            or Path('/proc/self/cgroup').read_text()!=capacity.get('membership')
            or (cgroup/'cpu.max').read_text().strip()!='1200000 100000'
            or (cgroup/'memory.max').read_text().strip()!='34359738368'
            or int((cgroup/'memory.current').read_text())+spec['rssBytes']>=34359738368):
        raise ValueError('PROC fresh enclosing capacity/bootstrap scope unavailable')


def proc_bootstrap_evidence(spec, authority, evidence):
    """Root-owned bootstrap inputs; output qualification is deliberately absent."""
    pinned={pin['path']:pin for pin in spec['pins']}
    transport=spec['transport']
    if (authority.get('nonEffectQualificationApproved') is not True or authority.get('publicationAuthorized') is not False
            or authority.get('nativeGetApproved') is not spec['request']['nativeGet']
            or authority.get('bootstrapEvidence')!=evidence
            or evidence.get('sourceAlternativesReviewSha256')!=authority.get('sourceAlternativesReviewSha256')
            or not re.fullmatch('[0-9a-f]{64}',str(evidence.get('sourceAlternativesReviewSha256')))
            or evidence.get('clientSha256')!=PROC_CURL_SHA or pinned[transport['client']]['sha256']!=PROC_CURL_SHA
            or evidence.get('tracerSha256')!=PROC_STRACE_SHA or pinned[transport['tracer']]['sha256']!=PROC_STRACE_SHA
            or evidence.get('candidateSupportPins')!=[pin for pin in spec['pins'] if pin['path'] not in spec['proofInputs'].values()]
            or evidence.get('readEvidence')!=spec['readEvidence']):
        raise ValueError('PROC authenticated source/tool/bootstrap admission unavailable')
    proc_capacity_evidence(spec,authority,evidence.get('capacity') or {})
    for name in ('fixture-ca.pem','fixture-cert.pem','fixture-key.pem'):
        if str(Path(spec['operationRoot'])/name) not in pinned:raise ValueError('PROC root-prepared inert TLS input not pinned')
    key=Path(spec['operationRoot'])/'fixture-key.pem'
    if key.stat().st_uid!=os.geteuid() or stat.S_IMODE(key.stat().st_mode)!=0o600:
        raise ValueError('PROC private inert TLS key ownership/mode')


def proc_observed_runtime(spec, observed, version):
    return {'currentBootId':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
            'effectiveUid':os.geteuid(),'actualInterpreter':os.path.realpath(sys.executable),
            'interpreterFlags':{'isolated':sys.flags.isolated,'noSite':sys.flags.no_site,
                                'noBytecode':sys.dont_write_bytecode,'optimize':sys.flags.optimize},
            'supportPins':[pin for pin in spec['pins'] if pin['path'] not in spec['proofInputs'].values()],
            'bounds':{k:spec[k] for k in ('cpuAffinity','rssBytes','addressSpaceBytes','logBytes','diskBytes')},
            'clientPath':spec['transport']['client'],'clientVersion':version,
            'postRetryExcluded':observed['noRepeatedFixtureRequests'],
            'readContract':'ordinary-exact-profile-observation-and-drift','readEvidence':spec['readEvidence'],
            'sourceReadAllowlist':spec['sourceReadAllowlist'],'traceComplete':False,
            'sourceAlternativesReviewSha256':observed['sourceAlternativesReviewSha256'],
            'canonicalBaseIdentity':proc_directory_identity(proc_operation_root(spec['operation']).parent),
            'qualifiedSourcePins':[pin for pin in spec['pins'] if pin['path'].endswith('/retain-choreo-c2-diagnostics.py')],
            'nativeGetObserved':observed['nativeGetObserved'],'nativeCollectorObserved':observed.get('nativeCollectorObserved',False),
            'controllerStartupTraceComplete':False,
            'controllerTraceGap':'Outer canonical supervisor/shared owner startup and metadata hooks are source-pinned and sampled, but outside this worker trace.',
            'limitation':'Executed fixed scenarios and reviewed source alternatives; sampled drift checks have intervals. No preventive filesystem containment or universal future-read guarantee.'}


def proc_trace_arguments(line):
    match=re.match(r'([a-z0-9_]+)\(',line)
    if match is None:return None,[],None
    name=match.group(1);end=line.rfind(') = ')
    if end<0:raise ValueError('PROC trace syscall result framing unknown')
    body=line[match.end():end];args=[];begin=0;depth=0;quoted=False;escaped=False
    for index,char in enumerate(body):
        if quoted:
            if escaped:escaped=False
            elif char=='\\':escaped=True
            elif char=='"':quoted=False
        elif char=='"':quoted=True
        elif char in '([{':depth+=1
        elif char in ')]}':depth-=1
        elif char==',' and depth==0:args.append(body[begin:index].strip());begin=index+1
    if quoted or depth:raise ValueError('PROC truncated trace argument framing')
    args.append(body[begin:].strip())
    return name,args,line[end+4:]


def proc_trace_string(value):
    if not value.startswith('"') or not value.endswith('"'):raise ValueError('PROC truncated/unknown trace pathname')
    decoded=json.loads(value)
    if not isinstance(decoded,str) or '\x00' in decoded:raise ValueError('PROC trace pathname shape')
    return decoded


def proc_generated_path(spec,path,authority_path):
    root=Path(spec['operationRoot']);candidate=Path(path)
    if candidate==Path(authority_path):return True
    directories={root,root/'home',root/'tmp',root/'control-controller',root/'control-session'}
    if candidate in directories:return True
    files={'input.json','dispatch-input.json','command.json','command.json.pending','measurement.subject.json',
           'measurement.subject.json.pending','measurement.ready.json','measurement.control.json','measurement.child.log',
           'measurement.child.log.result.json','measurement.tracer.log','measurement.apparatus.json','measurement.collector.json',
           'qualification.observed.json','qualification.native.log','qualification.native.body','fixture-request.json',
           'runner.log','runner.log.result.json','client-preflight.log','client-preflight.log.result.json',
           'authorization.header','storage-url.conf',
           *(name+suffix for name in ('terminal-job','terminal-archive','terminal-job-api','terminal-archive-api') for suffix in ('.log','.body','.log.result.json','.headers')),
           *(f'fixture-{case}{suffix}' for case in PROC_QUALIFICATION_CASES for suffix in ('.log','.body'))}
    return candidate.parent in directories and (candidate.name in files or re.fullmatch(r'qualification\.trace\.[1-9][0-9]*',candidate.name) is not None)


def proc_trace_evidence(spec, slot, authority_path, tracer_pid=None, *, killed=False, apparatus_pid=None, trace_store=None):
    """Whole fork graph and chronological cwd/dirfd evidence; unknowns refuse."""
    if trace_store is None:
        traces=sorted(Path(slot).glob('qualification.trace.*'))
        if not traces or len(traces)>32:raise ValueError('PROC trace process/thread population incomplete')
        def source_records():
            total=0
            for trace in traces:
                raw=trace.read_bytes();total+=len(raw)
                if trace.is_symlink() or total>8*1024**2 or not raw.endswith(b'\n'):
                    raise ValueError('PROC private trace bound/completeness')
                yield int(trace.name.rsplit('.',1)[1]),raw
        return proc_trace_semantics(spec,slot,authority_path,source_records(),tracer_pid,
                                    killed=killed,apparatus_pid=apparatus_pid)
    if type(trace_store) is not ProcFutureTraceStore or trace_store.spec is not spec:
        raise ValueError('PROC unselected future trace input')
    projected,physical=trace_store.project('normal')
    result=proc_trace_semantics(spec,slot,authority_path,projected,tracer_pid,killed=killed,
                                apparatus_pid=apparatus_pid,future_context=trace_store.context(retirement=True))
    trace_store._check(retirement=True)
    result['representation']={'encoding':'gzip-pid-stream/1','physicalArtifact':physical,
                              'processHashesAre':'derived-per-pid-projections','sourceOnly':True}
    return result


def proc_future_trace_lines(raw):
    """Bound one decoded line, rather than duplicate the expanded Unicode file."""
    offset=0
    while offset<len(raw):
        end=raw.find(b'\n',offset)
        if end<0 or end+1-offset>65536:raise ValueError('PROC future trace line bound')
        yield raw[offset:end].decode('utf-8','strict')
        offset=end+1


PROC_FUTURE_PARSER_RETAINED_BYTES=128*1024**2


def proc_trace_semantics(spec,slot,authority_path,source_records,tracer_pid=None,*,killed=False,apparatus_pid=None,future_context=None):
    """One semantic engine for original raw traces and future strict projections."""
    future_fifos=proc_future_source_context(spec,future_context)
    read=spec['readEvidence'];files=set(spec['sourceReadAllowlist'])
    directories={row['path'] for row in read['directories']};aliases={row['path'] for row in read['aliases']}
    absent={row['path'] for row in read['absences']};events=[];records={};total=0;clocked=True;births={};birth_times={};serial=0;retained=0
    for pid,raw in source_records:
        total+=len(raw)
        if total>(32*1024**2 if future_context is not None else 8*1024**2) or not raw.endswith(b'\n'):
            raise ValueError('PROC private trace bound/completeness')
        if pid in records or len(records)>=32:raise ValueError('PROC trace process/thread population incomplete')
        lines=[];pending=None
        for original in (proc_future_trace_lines(raw) if future_context is not None else raw.decode('utf-8','strict').splitlines()):
            stamp=re.match(r'([0-9]+\.[0-9]+) (.*)$',original)
            if stamp:when=float(stamp.group(1));line=stamp.group(2)
            else:clocked=False;when=float(serial);line=original
            serial+=1
            if future_context is not None:
                if time.monotonic()>=spec['totalDeadline']:raise TimeoutError('PROC future parser original total clock')
                if serial>131072:raise ValueError('PROC future trace record population bound')
            if '<unfinished ...>' in line:
                if pending is not None:raise ValueError('PROC ambiguous trace continuation')
                pending=(when,line.split('<unfinished ...>')[0],line.split('(',1)[0]);continue
            if line.startswith('<... ') and ' resumed>' in line:
                if pending is None:raise ValueError('PROC orphan trace continuation')
                resumed=re.match(r'<\.\.\. ([a-z0-9_]+) resumed>',line)
                when,prior,call=pending
                if resumed is None or resumed.group(1)!=call:raise ValueError('PROC mismatched resumed syscall name')
                line=prior+line.split(' resumed>',1)[1];pending=None
            if future_context is not None:
                # Charge retained text plus up to three derived semantic
                # copies (argv/path/FD provenance), and fixed event/index/read
                # object overhead. This is a refusal budget, not hard RSS.
                retained+=4*sys.getsizeof(line)+4096
                if retained>PROC_FUTURE_PARSER_RETAINED_BYTES:
                    raise ValueError('PROC future parser retained-object planning bound')
            lines.append(line);events.append((when,serial,pid,line))
            born=re.match(r'(?:clone3?|fork|vfork)\(.*\)\s+= ([0-9]+)$',line)
            if born:
                child=int(born.group(1))
                if child in births:raise ValueError('PROC repeated traced PID birth; reconciliation required')
                births[child]=pid
                birth_times[child]=when
        terminal=r'\+\+\+ killed by SIGKILL \+\+\+' if killed else r'\+\+\+ exited with (0|18|52) \+\+\+'
        if pending is not None or not lines or not re.fullmatch(terminal,lines[-1]):raise ValueError('PROC detached/lost/nonzero traced lifetime')
        records[pid]={'pid':pid,'sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw),'terminal':lines[-1],'observed':set()}
    roots=set(records)-set(births)
    if len(roots)!=1 or set(records)!=roots|set(births):raise ValueError('PROC complete traced process/thread birth join unavailable')
    root_pid=next(iter(roots));states={root_pid:{'cwd':[str(Path(spec['operationRoot']))],'fds':{0:'/dev/null',1:str(Path(slot)/'measurement.tracer.log'),2:str(Path(slot)/'measurement.tracer.log')},'cloexec':set(),'controller':False,'client':False}}
    bootstrap=json.loads(Path(spec['proofInputs']['bootstrapEvidence']).read_bytes());cgroup=Path(bootstrap['capacity']['directory'])
    kernel_inputs={str(cgroup/name) for name in ('cpu.max','memory.max','memory.current')}|{'/proc/self/cgroup','/proc/self/mountinfo','/proc/sys/kernel/random/boot_id'}
    actors=set(records)|({apparatus_pid} if apparatus_pid is not None else set());root_exec=False
    path_indices={'open':(0,),'openat':(1,),'openat2':(1,),'stat':(0,),'lstat':(0,),'stat64':(0,),'lstat64':(0,),
                  'newfstatat':(1,),'statx':(1,),'access':(0,),'faccessat':(1,),'faccessat2':(1,),
                  'readlink':(0,),'readlinkat':(1,),'execve':(0,),'execveat':(1,),'chdir':(0,),'getcwd':(0,),
                  'mkdir':(0,),'mkdirat':(1,),'rmdir':(0,),'unlink':(0,),'unlinkat':(1,),
                  'rename':(0,1),'renameat':(1,3),'renameat2':(1,3),'chmod':(0,),'fchmodat':(1,),
                  'chown':(0,),'lchown':(0,),'utime':(0,),'utimes':(0,),'statfs':(0,),
                  'truncate':(0,),'truncate64':(0,),'getxattr':(0,),'lgetxattr':(0,),'listxattr':(0,),'llistxattr':(0,)}
    fd_metadata={'fstat','fstat64','fstatfs','fstatfs64','fgetxattr','flistxattr'}
    nonpath={'clone','clone3','fork','vfork','close','close_range','dup','dup2','dup3','fcntl','fcntl64',
             'mmap','mmap2','mprotect','munmap','mremap','brk','madvise','msync','mlock','munlock','mlockall','munlockall',
             'pkey_alloc','pkey_free','pkey_mprotect','mseal','fchdir','getdents64','read','pread64','readv',
             'exit','exit_group','wait4','waitid','kill','tkill','tgkill','pidfd_send_signal','setsid','setpgid',
             'getpid','getppid','gettid','getpgrp','getpgid','getsid','prctl','arch_prctl','set_tid_address','set_robust_list',
             'futex','rseq','rt_sigaction','rt_sigprocmask','rt_sigreturn','sigaltstack',
             'socket','accept','accept4','socketpair','pipe','pipe2','eventfd','eventfd2',
             'epoll_create','epoll_create1','timerfd_create','signalfd','signalfd4','inotify_init','inotify_init1','memfd_create','pidfd_open'}
    future_namespace=Path(spec['operationRoot'])/'lossless-traces'
    future_artifacts={str(future_namespace/(phase+suffix)) for phase in ('controller','session','normal') for suffix in ('.fifo','.gz')}|{str(future_namespace/'manifest.json')}
    def generated(path):
        if proc_generated_path(spec,path,authority_path):return True
        if future_context is None:return False
        metadata={'stat','lstat','stat64','lstat64','newfstatat','statx','access','faccessat','faccessat2',
                  'fstat','fstat64','fstatfs','fstatfs64','statfs','close'}
        if path in future_artifacts:return name in metadata
        if path==str(future_namespace):
            return (name in metadata|{'getdents64'} or name in ('open','openat','openat2') and 'O_DIRECTORY' in line
                    and not any(flag in line for flag in ('O_WRONLY','O_RDWR','O_TRUNC','O_CREAT')))
        return False
    semantic_reads=[]
    for _,_,pid,line in sorted(events):
        if future_context is not None and time.monotonic()>=spec['totalDeadline']:raise TimeoutError('PROC future parser original total clock')
        if pid not in states:raise ValueError('PROC child event precedes authenticated fork/cwd lineage')
        state=states[pid]
        if line.startswith(('+++','---')):continue
        if any(marker in line for marker in ('strace:','detached','CLONE_UNTRACED','setns(','unshare(','ptrace(','io_uring_','io_setup(','io_submit(','io_getevents(','io_pgetevents(','aio_','process_vm_','symlink(','symlinkat(')):
            raise ValueError('PROC unsupported tracing/asynchronous source access')
        name,args,result=proc_trace_arguments(line)
        if name is None:raise ValueError('PROC unknown trace record framing')
        if name not in set(path_indices)|fd_metadata|nonpath:raise ValueError('PROC unmodelled selected syscall grammar')
        success=not result.startswith('-1 ')
        paths=[]
        for index in path_indices.get(name,()):
            if index>=len(args):raise ValueError('PROC trace pathname argument missing')
            value=proc_trace_string(args[index])
            if not value:continue
            if value.startswith('/'):path=value
            else:
                base=state['cwd'][0]
                if index in (1,3):
                    dirarg=args[index-1]
                    if dirarg.startswith('AT_FDCWD<'):
                        annotated=re.fullmatch(r'AT_FDCWD<(/[^<>]+)>',dirarg)
                        if annotated is None or annotated.group(1)!=state['cwd'][0]:raise ValueError('PROC actual AT_FDCWD provenance/cwd drift')
                        base=annotated.group(1)
                    elif dirarg!='AT_FDCWD':
                        annotated=re.fullmatch(r'([0-9]+)<(/[^<>]+)>',dirarg)
                        if annotated:base=annotated.group(2)
                        elif dirarg.isdigit() and int(dirarg) in state['fds']:base=state['fds'][int(dirarg)]
                        else:raise ValueError('PROC unresolved relative dirfd pathname')
                path=os.path.normpath(str(Path(base)/value))
            paths.append(path)
        paths+=re.findall(r'<(/[^<>]+)>',line)
        if name in ('mmap','mmap2') and 'MAP_ANONYMOUS' not in line and success:
            if len(args)<5 or not re.fullmatch(r'[0-9]+</[^<>]+>',args[4]):raise ValueError('PROC executable/file mapping descriptor provenance unknown')
        for path in paths:
            if killed and Path(path).name in ('authorization.header','storage-url.conf'):
                raise ValueError('PROC token-free kernel control accessed semantic credential input')
            mutation=name in ('truncate','truncate64','chmod','fchmodat','chown','lchown','utime','utimes','unlink','unlinkat','rename','renameat','renameat2','mkdir','mkdirat','rmdir') or name in ('open','openat','openat2') and any(flag in line for flag in ('O_WRONLY','O_RDWR','O_TRUNC','O_CREAT'))
            if mutation and not generated(path):raise ValueError('PROC write/metadata mutation outside fixed generated artifact lifecycle')
            if '(deleted)' in path:raise ValueError('PROC deleted source/descriptor provenance unknown')
            kernel=proc_kernel_data_read(path,line,pid,actors,tracer_pid,state['controller'],kernel_inputs)
            if path not in files|directories|aliases|{'/dev/null'} and not generated(path) and not kernel:
                if 'ENOENT' not in result or path not in absent:raise ValueError('PROC observed source/mapping/negative lookup outside bootstrap inventory')
            records[pid]['observed'].add(path)
        if name in ('readlink','readlinkat') and success and paths:
            returned=re.findall(r'"((?:[^"\\]|\\.)*)"',line)
            if len(returned)>=2 and re.search(r'/proc/(?:self|thread-self|[0-9]+)/(?:(?:task/[0-9]+/)?fd/[0-9]+|exe)$',paths[0]):
                target=json.loads('"'+returned[1]+'"')
                if target.startswith('/') and target not in files|aliases|{'/dev/null'} and not generated(target):
                    raise ValueError('PROC ordinary executable/FD target outside source/lifecycle closure')
                if not target.startswith('/') and not re.fullmatch(r'(?:socket|pipe|anon_inode):\[[^\]]+\]',target):raise ValueError('PROC unknown kernel FD label')
        if name in ('execve','execveat') and success:
            state['fds']={fd:target for fd,target in state['fds'].items() if fd not in state['cloexec']};state['cloexec']=set()
            exe=paths[0]
            state['controller']=exe==os.path.realpath(sys.executable);state['client']=exe==spec['transport']['client']
            if pid==root_pid:
                if not state['controller']:raise ValueError('PROC trace root is not the canonical interpreter')
                root_exec=True
                argv=json.loads(args[1] if name=='execve' else args[2])
                if clocked:
                    if (len(argv)!=12 or argv[:6]!=[os.path.realpath(sys.executable),'-I','-S','-B',str(Path(__file__).resolve()),'command']
                            or argv[6]!='--proc-measured-controller' or argv[7]!=str(Path(spec['operationRoot'])/'input.json')
                            or argv[8]!=str(authority_path) or argv[9]!=hashlib.sha256(Path(authority_path).read_bytes()).hexdigest()
                            or not argv[10].isdigit() or argv[11] not in ('normal','controller','session')):
                        raise ValueError('PROC actual root exec argv outside closed measured role')
                    state['fds'][int(argv[10])]='pipe:measurement-handshake'
        if name in ('read','pread64','readv'):
            if (len(args)!=(4 if name=='pread64' else 3) or not all(re.fullmatch(r'(?:0x[0-9a-f]+|0)',arg) for arg in args)
                    or not re.fullmatch(r'(?:0x[0-9a-f]+|0|-[0-9]+(?: .*)?)',result)):
                raise ValueError('PROC read must use raw pointer/count metadata, never payload decoding')
            fd=int(args[0],16);target=state['fds'].get(fd)
            if target is None:raise ValueError('PROC raw read descriptor lifetime/provenance unknown')
            count=int(result,16) if result.startswith('0x') else int(result.split()[0])
            if (name!='readv' and count>int(args[2],16)) or (name=='readv' and int(args[2],16)==0 and count>0):raise ValueError('PROC impossible raw read byte/count result')
            if count>0 and Path(target).name in ('authorization.header','storage-url.conf'):
                semantic_reads.append({'pid':pid,'fd':fd,'path':target,'bytes':count})
        if name=='chdir' and success:state['cwd'][0]=paths[0]
        if name=='fchdir' and success:
            if not args or not args[0].split('<',1)[0].isdigit():raise ValueError('PROC unresolved fchdir')
            fd=int(args[0].split('<',1)[0]);annotated=re.search(r'<(/[^<>]+)>',args[0])
            if annotated:state['cwd'][0]=annotated.group(1)
            elif fd in state['fds']:state['cwd'][0]=state['fds'][fd]
            else:raise ValueError('PROC unresolved fchdir descriptor')
        born=re.match(r'(?:clone3?|fork|vfork)\(.*\)\s+= ([0-9]+)$',line)
        if born:
            child=int(born.group(1));states[child]={'cwd':state['cwd'] if 'CLONE_FS' in line else list(state['cwd']),
                'fds':state['fds'] if 'CLONE_FILES' in line else dict(state['fds']),
                'cloexec':state['cloexec'] if 'CLONE_FILES' in line else set(state['cloexec']),'controller':state['controller'],'client':state['client']}
        if name in ('open','openat','openat2') and success:
            opened=re.match(r'([0-9]+)(?:<[^>]+>)?$',result)
            if opened and paths:
                fd=int(opened.group(1));state['fds'][fd]=paths[0]
                if 'O_CLOEXEC' in line:state['cloexec'].add(fd)
                else:state['cloexec'].discard(fd)
        if name in ('socket','accept','accept4','eventfd','eventfd2','epoll_create','epoll_create1','timerfd_create','signalfd','signalfd4','inotify_init','inotify_init1','pidfd_open') and success:
            opened=re.match(r'([0-9]+)(?:<[^>]+>)?$',result)
            if opened:
                fd=int(opened.group(1));state['fds'][fd]='kernel:'+name
                if 'CLOEXEC' in line or name=='pidfd_open':state['cloexec'].add(fd)
                else:state['cloexec'].discard(fd)
            else:raise ValueError('PROC kernel descriptor creation result unknown')
        if name in ('pipe','pipe2','socketpair') and success:
            vector=args[3] if name=='socketpair' else args[0]
            descriptors=re.findall(r'(?:\[|,\s*)([0-9]+)(?:<[^>]+>)?',vector)
            if len(descriptors)!=2:raise ValueError('PROC kernel descriptor pair creation unknown')
            for fd in descriptors:
                state['fds'][int(fd)]='kernel:'+name
                if 'CLOEXEC' in line:state['cloexec'].add(int(fd))
                else:state['cloexec'].discard(int(fd))
        if name=='memfd_create':raise ValueError('PROC anonymous executable/source descriptor constructor unsupported')
        if name=='close' and success and args and args[0].split('<',1)[0].isdigit():
            fd=int(args[0].split('<',1)[0]);state['fds'].pop(fd,None);state['cloexec'].discard(fd)
        if name=='close_range' and success:
            if len(args)!=3 or args[2] not in ('0','CLOSE_RANGE_CLOEXEC','CLOSE_RANGE_UNSHARE','CLOSE_RANGE_UNSHARE|CLOSE_RANGE_CLOEXEC','CLOSE_RANGE_CLOEXEC|CLOSE_RANGE_UNSHARE') or not args[0].isdigit() or args[1] not in ('~0U','4294967295') and not args[1].isdigit():raise ValueError('PROC close_range descriptor bounds unknown')
            if 'CLOSE_RANGE_UNSHARE' in args[2]:
                state['fds']=dict(state['fds']);state['cloexec']=set(state['cloexec'])
            start=int(args[0]);end=4294967295 if args[1] in ('~0U','4294967295') else int(args[1])
            for fd in list(state['fds']):
                if start<=fd<=end:
                    if 'CLOSE_RANGE_CLOEXEC' in line:state['cloexec'].add(fd)
                    else:state['fds'].pop(fd,None);state['cloexec'].discard(fd)
        if name in ('dup','dup2','dup3') and success:
            source=args[0].split('<',1)[0];dest=result.split('<',1)[0]
            if source.isdigit() and dest.isdigit() and int(source) in state['fds']:
                state['fds'][int(dest)]=state['fds'][int(source)]
                if name=='dup3' and 'O_CLOEXEC' in line:state['cloexec'].add(int(dest))
                elif int(source)!=int(dest):state['cloexec'].discard(int(dest))
            else:raise ValueError('PROC descriptor duplication provenance unknown')
        if name in ('fcntl','fcntl64') and success:
            source=args[0].split('<',1)[0]
            if not source.isdigit():raise ValueError('PROC fcntl descriptor grammar unknown')
            fd=int(source);command=args[1]
            if command in ('F_DUPFD','F_DUPFD_CLOEXEC'):
                dest=result.split('<',1)[0]
                if fd not in state['fds'] or not dest.isdigit():raise ValueError('PROC fcntl duplicate provenance unknown')
                state['fds'][int(dest)]=state['fds'][fd]
                if command=='F_DUPFD_CLOEXEC':state['cloexec'].add(int(dest))
                else:state['cloexec'].discard(int(dest))
            elif command=='F_SETFD':
                if len(args)!=3 or args[2] not in ('0','FD_CLOEXEC'):raise ValueError('PROC fcntl close-on-exec value unknown')
                if args[2]=='FD_CLOEXEC':state['cloexec'].add(fd)
                else:state['cloexec'].discard(fd)
            elif command not in ('F_GETFD','F_GETFL','F_SETFL','F_GETPIPE_SZ','F_SETPIPE_SZ'):raise ValueError('PROC unsupported fcntl transition')
    if not root_exec:raise ValueError('PROC canonical root exec missing')
    union=set();rows=[]
    for pid,row in sorted(records.items()):
        if not killed and row['terminal']!='+++ exited with 0 +++' and not states[pid]['client']:raise ValueError('PROC unexpected traced child failure')
        observed=sorted(row.pop('observed'));union.update(observed)
        row.update(pathCount=len(observed),pathSetSha256=hashlib.sha256(json.dumps(observed).encode()).hexdigest());rows.append(row)
    evidence={'complete':True,'clocked':clocked,'rootPid':root_pid,'processes':rows,'observedPaths':sorted(union),
              'traceBytes':total,'semanticReads':semantic_reads,
              'forkProvenance':[{'pid':pid,'parentPid':parent,'timestamp':birth_times[pid]} for pid,parent in sorted(births.items())],
              'scope':'fresh canonical controller exec through terminal traced descendants/threads; metadata only'}
    if len(json.dumps(evidence).encode())>MAX_OUTPUT:raise ValueError('PROC trace result vocabulary byte bound')
    return evidence


def proc_kernel_data_read(path,line,pid,actors,tracer_pid,controller,kernel_inputs):
    """Observed purpose-limited kernel DATA, never arbitrary process authority."""
    if path in kernel_inputs:return controller
    if path=='/proc':return controller and line.startswith(('open','newfstatat','stat','getdents'))
    if re.fullmatch(r'/proc/[1-9][0-9]*/stat',path):
        return controller and line.startswith(('open','newfstatat','stat','access')) and not any(flag in line for flag in ('O_WRONLY','O_RDWR','O_CREAT'))
    path=path.replace('/proc/self/',f'/proc/{pid}/').replace('/proc/thread-self/',f'/proc/{pid}/task/{pid}/')
    match=re.fullmatch(r'/proc/([1-9][0-9]*)/(cgroup|mountinfo|status|exe|fd|fd/[0-9]+|ns/(?:pid|mnt|user)|task(?:/[0-9]+(?:/stat|/status)?)?)',path)
    if not match:return False
    target=int(match.group(1))
    if target not in actors and target!=tracer_pid:return False
    if not controller:return target==pid and match.group(2)=='fd/0'
    # Metadata readlink/lstat is allowed for the closed actor set. Dereferencing
    # arbitrary fd/exe/namespace contents is not a kernel-data read exception.
    if match.group(2).startswith(('fd/','ns/')) or match.group(2)=='exe':
        return line.startswith(('readlink','newfstatat','stat','access'))
    return not any(flag in line for flag in ('O_WRONLY','O_RDWR','O_CREAT'))


def proc_qualify(owner,spec,slot,env,check,authority,version,authority_path,authority_sha,work_deadline):
    # Only the authenticated measured controller reaches this worker; the outer
    # tracer already follows the unchanged owner's legitimate session boundary.
    argv=[os.path.realpath(sys.executable),'-I','-S','-B',str(Path(__file__).resolve()),'command','--proc-qualification-worker',
          str(slot/'input.json'),str(slot/'qualification.observed.json'),str(authority_path),authority_sha]
    receipt=owner.run(argv,str(slot),env,work_deadline,spec['cleanupSeconds'],spec['rssBytes'],
                      str(slot/'runner.log'),spec['logBytes'],control=check,observe_child_exit=True)
    output=slot/'qualification.observed.json'
    if output.is_symlink() or not 0<output.stat().st_size<=spec['logBytes']:raise ValueError('PROC observed qualification output bound')
    observed=json.loads(output.read_bytes())
    if (set(observed)!={'cases','noRepeatedFixtureRequests','nativeGetObserved'}
            or list(observed['cases'])!=list(PROC_QUALIFICATION_CASES)
            or observed['noRepeatedFixtureRequests'] is not True
            or observed['nativeGetObserved'] is not False):
        raise ValueError('PROC actual closed qualification sequence incomplete')
    observed['sourceAlternativesReviewSha256']=authority['sourceAlternativesReviewSha256']
    proc_read_drift(spec)
    return receipt,observed


def proc_qualification_worker(descriptor, output, authority_path, authority_sha):
    """Internal fixed scenario worker; root launch remains the outer owner only."""
    if not sys.flags.isolated or not sys.flags.no_site or not sys.dont_write_bytecode or sys.flags.optimize:
        raise ValueError('PROC qualification actual interpreter flags')
    spec=json.loads(Path(descriptor).read_bytes());proc_command_preflight(spec,now=time.monotonic())
    root=Path(spec['operationRoot'])
    if spec['profile']!='proc-pair-client-qualification' or Path(descriptor)!=root/'input.json' or Path(output)!=root/'qualification.observed.json':
        raise ValueError('PROC closed qualification worker route')
    authority_raw=Path(authority_path).read_bytes();authority=json.loads(authority_raw)
    if (hashlib.sha256(authority_raw).hexdigest()!=authority_sha
            or authority.get('descriptorSha256')!=hashlib.sha256(Path(descriptor).read_bytes()).hexdigest()
            or authority.get('approved') is not True or authority.get('operation')!=spec['operation']
            or authority.get('operationRoot')!=str(root) or authority.get('profile')!=spec['profile']):
        raise ValueError('PROC root-selected worker bootstrap authority unavailable')
    if (proc_directory_identity(PROC_OPERATION_BASE/str(os.geteuid()))!=authority.get('canonicalBaseIdentity')
            or proc_directory_identity(root.parent)!=authority.get('canonicalQualificationLineageIdentity')
            or proc_directory_identity(root)!=authority.get('canonicalOperationIdentity')):
        raise ValueError('PROC root-qualified worker canonical state differs')
    state=json.loads((root/'measurement.subject.json').read_bytes())
    if (state.get('schema')!=RESULT_SCHEMA or state.get('operation')!=spec['operation']
            or state.get('sendState')!='qualification-started' or not (root/'dispatch-input.json').is_file()):
        raise ValueError('PROC worker requires consumed outer qualification intent')
    parent=os.getppid()
    ready=json.loads((root/'measurement.ready.json').read_bytes());current_parent=proc_actor(parent)
    if (current_parent is None or current_parent['pid']!=ready['subject']['pid'] or current_parent['start']!=ready['subject']['start']
            or current_parent['tracerPid']!=ready['tracer']['pid'] or proc_actor(os.getpid())['tracerPid']!=ready['tracer']['pid']
            or os.path.realpath(f'/proc/{parent}/exe')!=os.path.realpath(sys.executable) or os.getpgrp()!=os.getpid()):
        raise ValueError('PROC worker lacks actual authenticated measured-controller/owned-session parent')
    # Keep the original input bytes unchanged; this local subject execution limit
    # reserves the original ten seconds for owner cleanup and ten for collection.
    spec=dict(spec);spec['workDeadline']=ready['subjectWorkDeadline']
    if set(os.sched_getaffinity(0))!=set(spec['cpuAffinity']):raise ValueError('PROC actual worker affinity differs')
    import resource
    expected=resource.RLIM_INFINITY if spec['addressSpaceBytes'] is None else spec['addressSpaceBytes']
    if resource.getrlimit(resource.RLIMIT_AS)[0]!=expected:raise ValueError('PROC actual worker address-space bound differs')
    if os.readlink('/proc/self/fd/0')!='/dev/null':raise ValueError('PROC qualification stdin must be root-selected dev-null')
    proc_bootstrap_evidence(spec,authority,json.loads(Path(spec['proofInputs']['bootstrapEvidence']).read_bytes()))
    proc_read_drift(spec)
    # Real protected-record validators are part of traced startup/import closure.
    validators=__import__('runpy').run_path(str(Path(__file__).with_name('test-callable-cli-installed-harness.py')))
    def document(key):return json.loads(Path(spec['proofInputs'][key]).read_bytes())
    admission=document('admission')
    validators['pair_recovery_selection'](admission)
    validators['pair_original_failure'](admission,document('originalRun'),document('originalJobs'),document('originalArtifact'),Path(spec['proofInputs']['originalArchive']).read_bytes())
    validators['pair_verified_attestation'](admission,document('verifiedAttestation'))
    __import__('runpy').run_path(str(Path(__file__).with_name('write-callable-cli-release-readback.py')))['pair_consumers'](admission,Path(spec['proofInputs']['consumerRoot']))
    import ssl, http.server, threading, subprocess
    counts={case:0 for case in PROC_QUALIFICATION_CASES};redirected=[]
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_POST(self):
            case=self.path.removeprefix('/proc-qualification/')
            if case not in counts:redirected.append(self.path);self.send_error(404);return
            counts[case]+=1
            size=int(self.headers.get('Content-Length','0'))
            if not 0<size<=4096:raise ValueError('PROC fixture fixed body bound')
            if (self.rfile.read(size)!=b'{"inert":"qualification-only"}' or self.headers.get('Expect') is not None
                    or self.headers.get('Authorization') is not None or self.request_version!='HTTP/1.1'):
                raise ValueError('PROC actual inert single-body/H1/no-Expect/no-credential request differs')
            if case=='early-close':self.close_connection=True;return
            code=int(case) if case.isdigit() else 200
            self.send_response(code)
            if 300<=code<400:self.send_header('Location',PROC_QUALIFICATION_ORIGIN+'/redirect-trap')
            payload=b'{"inert":true}'
            self.send_header('Content-Length',str(len(payload)+(100 if case=='partial-response' else 0)))
            self.end_headers();self.wfile.write(payload);self.close_connection=True
        def do_GET(self):redirected.append(self.path);self.send_error(404)
    server=http.server.HTTPServer(('127.0.0.1',18443),Handler)
    context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.load_cert_chain(root/'fixture-cert.pem',root/'fixture-key.pem')
    server.socket=context.wrap_socket(server.socket,server_side=True)
    thread=threading.Thread(target=server.serve_forever,kwargs={'poll_interval':.05});thread.start()
    body=root/'fixture-request.json'
    with body.open('xb') as stream:os.chmod(body,0o600);stream.write(b'{"inert":"qualification-only"}')
    cases={}
    try:
        for case in PROC_QUALIFICATION_CASES:
            argv=proc_curl_argv(spec,root,case,'POST',body=body,authorization=False,fixture=True,
                                remaining=spec['workDeadline']-time.monotonic(),name='fixture-'+case)
            with (root/('fixture-'+case+'.log')).open('xb') as stream:
                completed=subprocess.run(argv,cwd=root,env=spec['environment'],stdout=stream,stderr=subprocess.DEVNULL,
                                         timeout=max(.01,spec['workDeadline']-time.monotonic()))
            meta=json.loads((root/('fixture-'+case+'.log')).read_bytes())
            expected_code=int(case) if case.isdigit() else (0 if case=='early-close' else 200)
            if (counts[case]!=1 or redirected or meta['httpCode']!=f'{expected_code:03}' or meta['redirects']!=0
                    or meta['effectiveUrl']!=PROC_QUALIFICATION_ORIGIN+'/proc-qualification/'+case
                    or meta['sslVerifyResult']!=0
                    or completed.returncode!=(52 if case=='early-close' else 18 if case=='partial-response' else 0)):
                raise ValueError('PROC observed TLS fault request/response behavior differs')
            cases[case]={'requests':counts[case],'exit':completed.returncode,'metadata':meta}
        proc_read_drift(spec)
        value={'cases':cases,'noRepeatedFixtureRequests':not redirected and all(n==1 for n in counts.values()),
               'nativeGetObserved':False}
        with Path(output).open('xb') as stream:os.chmod(output,0o600);stream.write(json.dumps(value).encode()+b'\n')
    finally:
        server.shutdown();thread.join(timeout=max(0,spec['totalDeadline']-time.monotonic()));server.server_close()
        if thread.is_alive():raise ValueError('PROC inert TLS fixture retirement unknown')
    return 0


def proc_runtime_evidence(spec, authority, evidence):
    """Join root-selected canonical qualification to actual current support inputs."""
    owner=evidence.get('ownerReceipt') or {}
    runtime=evidence.get('runtimeEvidence') or {}
    expected_bounds={k:spec[k] for k in ('cpuAffinity','rssBytes','addressSpaceBytes','logBytes','diskBytes')}
    support=[pin for pin in spec['pins'] if pin['path'] not in spec['proofInputs'].values()]
    if (evidence.get('schema')!=RESULT_SCHEMA or evidence.get('commandAccepted') is not True
            or evidence.get('rootAccepted') is not True or evidence.get('publicationAuthorized') is not False
            or evidence.get('firstCause') is not None or evidence.get('additionalFailures')!=[]
            or evidence.get('reportingOutcome')!='written'
            or evidence!=authority.get('runtimeQualification') or owner.get('cleanup')!='observed-empty-group'
            or owner.get('custody')!='observed-owned-group' or owner.get('exit')!=0 or owner.get('firstCause') is not None
            or not isinstance(owner.get('leader'),dict) or not owner['leader'].get('start')
            or owner.get('residual')!=[] or owner.get('escapedResidual')!=[] or owner.get('escapedDescendants')!=[]
            or runtime.get('currentBootId')!=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
            or runtime.get('effectiveUid')!=os.geteuid() or runtime.get('actualInterpreter')!=os.path.realpath(sys.executable)
            or runtime.get('interpreterFlags')!={'isolated':1,'noSite':1,'noBytecode':True,'optimize':0}
            or runtime.get('supportPins')!=support or runtime.get('bounds')!=expected_bounds
            or runtime.get('clientPath')!=spec['argv'][0] or runtime.get('postRetryExcluded') is not True
            or runtime.get('qualifiedSourcePins')!=[pin for pin in spec['pins'] if pin['path'].endswith('/retain-choreo-c2-diagnostics.py')]
            or runtime.get('readContract')!='ordinary-exact-profile-observation-and-drift'
            or runtime.get('readEvidence')!=spec['readEvidence']
            or runtime.get('traceComplete') is not True
            or runtime.get('controllerStartupTraceComplete') is not True
            or evidence.get('qualificationAccepted') is not True
            or not re.fullmatch('[0-9a-f]{64}',str(authority.get('sourceAlternativesReviewSha256')))
            or runtime.get('sourceAlternativesReviewSha256')!=authority.get('sourceAlternativesReviewSha256')
            or runtime.get('sourceReadAllowlist')!=spec['sourceReadAllowlist']
            or runtime.get('canonicalBaseIdentity')!=proc_directory_identity(proc_operation_root(spec['operation']).parent)
            or runtime.get('nativeGetObserved') is not True or runtime.get('nativeCollectorObserved') is not True):
        raise ValueError('PROC actual canonical runtime/support/read/retry/custody evidence unavailable')


def proc_charge_read(retirement,kind,leg=None):
    counts=retirement.setdefault('readCounts',{'api':0,'storage':0,'total':0})
    qualification=retirement.get('profile')=='qualification'
    if qualification:
        expected={'api:repo-public','api:terminal-job','api:terminal-archive','storage:terminal-job','storage:terminal-archive'}
        key=kind+':'+str(leg);used=retirement.setdefault('consumedLegs',[])
        if key not in expected or key in used:raise ValueError('PROC qualification fixed native leg consumed/foreign')
    if kind not in ('api','storage') or counts[kind]>=(3 if qualification and kind=='api' else 5 if kind=='api' else 2) or counts['total']>=(5 if qualification else 7):
        raise ValueError('PROC finite API/storage request budget exhausted')
    if qualification:used.append(key)
    counts[kind]+=1;counts['total']+=1


def proc_storage_read(owner,spec,slot,env,check,target,name,retirement):
    """One explicitly consumed API redirect, then one credential-separated GET."""
    selected_path={'terminal-job':r'actions/jobs/[1-9][0-9]{0,19}/logs',
                   'terminal-archive':r'actions/artifacts/[1-9][0-9]{0,19}/zip'}
    if name not in selected_path or not re.fullmatch(re.escape('repos/'+PROC_REPOSITORY+'/')+selected_path[name],target):
        raise ValueError('PROC redirect transfer requires selected native job/artifact endpoint')
    headers=slot/(name+'.headers');api_body=slot/(name+'-api.body')
    storage_body=slot/(name+'.body')
    secret=None;header_identity=None;body_identity=None;storage_identity=None;receipt=None;api_receipt=None;api_started=False;storage_started=False;native_success=False
    started=time.monotonic();first=None
    try:
        fd=os.open(headers,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
        try:header_identity=proc_read_identity(headers)
        finally:os.close(fd)
        fd=os.open(api_body,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
        try:body_identity=proc_read_identity(api_body)
        finally:os.close(fd)
        fd=os.open(storage_body,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
        try:storage_identity=proc_read_identity(storage_body)
        finally:os.close(fd)
        api_result=slot/(name+'-api.log.result.json')
        if api_result.exists() or api_result.is_symlink():raise ValueError('PROC API child result occupied')
        argv=proc_curl_argv(spec,slot,target,'GET',name=name+'-api',remaining=spec['workDeadline']-time.monotonic())
        argv[-1:-1]=['--dump-header',str(headers)]
        proc_charge_read(retirement,'api',name);retirement['receipt']=None
        def api_check(event,child,leader,known):
            check(event,child,leader,known)
            if (proc_read_identity(headers)!=header_identity or proc_read_identity(api_body)!=body_identity
                    or headers.stat().st_size>65536 or api_body.stat().st_size>spec['logBytes']):
                raise ValueError('PROC private API redirect header/body identity/bound drift')
        try:
            api_started=True
            receipt=owner.run(argv,str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],spec['rssBytes'],
                              str(slot/(name+'-api.log')),spec['logBytes'],control=api_check,observe_child_exit=True)
        except BaseException:
            try:
                if api_result.is_file() and not api_result.is_symlink() and 0<api_result.stat().st_size<=spec['logBytes']:
                    api_receipt=json.loads(api_result.read_bytes());retirement['receipt']=api_receipt
            except BaseException as error:
                retirement.setdefault('storageRetirementFailures',[]).append(failure_record(error))
            raise
        api_receipt=receipt
        retirement.setdefault('apiReaders',[]).append({'stage':name,'receipt':receipt})
        retirement['receipt']=receipt
        meta=json.loads((slot/(name+'-api.log')).read_bytes())
        if (set(meta)!={'httpCode','effectiveUrl','redirects','sslVerifyResult'} or meta['httpCode']!='302'
                or meta['effectiveUrl']!='https://api.github.com/'+target or meta['redirects']!=0 or meta['sslVerifyResult']!=0
                or proc_read_identity(headers)!=header_identity or headers.stat().st_size>65536):
            raise proc_read_failure('api',meta.get('httpCode'),'PROC selected API redirect status/origin/trust refusal')
        location,date=proc_redirect_headers(headers)
        lifetime=proc_storage_location(location,archive=name=='terminal-archive',api_date=date,now_epoch=time.time())
        deadline=min(spec['workDeadline'],started+60,time.monotonic()+lifetime)
        secret=proc_sensitive_input(slot,'storage-url.conf',('url = "'+location+'"\n').encode(),deadline=deadline)
        # Never persist or hash the URL, API redirect headers or body.
        del location
        if receipt.get('cleanup')!='observed-empty-group' or receipt.get('custody')!='observed-owned-group':
            raise ValueError('PROC API redirect child retirement unknown')
        headers.unlink();header_identity=None
        if proc_read_identity(api_body)!=body_identity:raise ValueError('PROC private API body retirement identity drift')
        api_body.unlink();body_identity=None
        def transfer_check(event,child,leader,known):
            check(event,child,leader,known);proc_sensitive_unchanged(secret)
            if proc_read_identity(storage_body)!=storage_identity or storage_body.stat().st_size>spec['logBytes']:
                raise ValueError('PROC private storage body identity/bound drift')
            if event!='after-cleanup' and time.monotonic()>=deadline:raise TimeoutError('PROC consumed storage URL lifetime exhausted')
        proc_charge_read(retirement,'storage',name);retirement['receipt']=None;receipt=None
        argv=proc_storage_argv(spec,slot,name,deadline-time.monotonic())
        storage_result=slot/(name+'.log.result.json')
        if storage_result.exists() or storage_result.is_symlink():raise ValueError('PROC storage child result occupied')
        try:
            storage_started=True
            receipt=owner.run(argv,str(slot),env,deadline,spec['cleanupSeconds'],spec['rssBytes'],
                              str(slot/(name+'.log')),spec['logBytes'],control=transfer_check,observe_child_exit=True)
        except BaseException:
            try:
                if storage_result.is_file() and not storage_result.is_symlink() and 0<storage_result.stat().st_size<=spec['logBytes']:
                    receipt=json.loads(storage_result.read_bytes());retirement['receipt']=receipt
            except BaseException as error:
                retirement.setdefault('storageRetirementFailures',[]).append(failure_record(error))
            raise
        retirement['receipt']=receipt
        meta_path=slot/(name+'.log');body=slot/(name+'.body')
        if meta_path.is_symlink() or proc_read_identity(body)!=storage_identity or not 0<meta_path.stat().st_size<=8192 or not 0<body.stat().st_size<=spec['logBytes']:
            raise ValueError('PROC storage response private bound refusal')
        meta=json.loads(meta_path.read_bytes())
        if (set(meta)!={'httpCode','redirects','sslVerifyResult','bytes'} or meta['httpCode']!='200'
                or meta['redirects']!=0 or meta['sslVerifyResult']!=0 or type(meta['bytes']) not in (float,int)
                or meta['bytes']!=body.stat().st_size):
            raise proc_read_failure('storage',meta.get('httpCode'),'PROC storage '+proc_read_status(meta.get('httpCode'),storage=True)+' or metadata/trust refusal')
        raw=body.read_bytes()
        if name=='terminal-archive':proc_archive_shape(raw,deadline=spec['workDeadline'])
        native_success=True
        return raw
    except BaseException as error:
        first=error
        if hasattr(error,'proc_sensitive_creation'):
            retirement['storageCreationFailure']=error.proc_sensitive_creation
            if error.proc_sensitive_creation['disposition']!='removed-before-child':retirement['storagePrivateHold']=True
        raise
    finally:
        retirement_errors=[]
        private_outputs=[(headers,header_identity,'redirectHeadersDisposition',api_started,api_receipt),
                         (api_body,body_identity,'redirectBodyDisposition',api_started,api_receipt)]
        if not native_success:private_outputs.append((storage_body,storage_identity,'storageBodyDisposition',storage_started,receipt))
        for path,identity,label,started_child,child_receipt in private_outputs:
            if identity is None:continue
            try:
                if (not started_child or child_receipt is not None and child_receipt.get('cleanup')=='observed-empty-group' and child_receipt.get('custody')=='observed-owned-group') and proc_read_identity(path)==identity:
                    path.unlink();retirement[label]='removed-before-child' if not started_child else 'removed-after-qualified-child-retirement'
                else:
                    retirement[label]='held-uncertain-retirement';retirement['storagePrivateHold']=True
            except BaseException as error:
                retirement_errors.append(error);retirement[label]='held-uncertain-retirement';retirement['storagePrivateHold']=True
        if secret is not None:
            try:
                if storage_started:disposition=proc_sensitive_retire(secret,receipt)
                else:
                    proc_sensitive_unchanged(secret);Path(secret['path']).unlink()
                    disposition={'disposition':'removed-before-storage-child','identity':secret['identity']}
                retirement.setdefault('storageInputRetirements',[]).append(disposition)
                if disposition['disposition'] not in ('removed-after-qualified-retirement','removed-before-storage-child'):
                    retirement['storagePrivateHold']=True
            except BaseException as error:
                retirement_errors.append(error);retirement['storagePrivateHold']=True
        for error in retirement_errors:
            retirement.setdefault('storageRetirementFailures',[]).append(failure_record(error))
        if first is None and retirement_errors:raise retirement_errors[0]


def proc_terminal_evidence(owner, spec, slot, env, check, native, retirement=None):
    """Bound independent terminal logs/artifacts; failures never reopen original send."""
    run=native['id']; collected={}; failures=[]
    if retirement is None:raise ValueError('PROC watch read counter unavailable')
    def read(target,name,document=False):
        output=slot/(name+'.log')
        argv=proc_curl_argv(spec,slot,target,'GET',remaining=spec['workDeadline']-time.monotonic(),name=name)
        try:
            if name in ('terminal-job','terminal-archive'):
                raw=proc_storage_read(owner,spec,slot,env,check,target,name,retirement)
                collected[name]={'state':'collected','sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)}
                return None
            proc_charge_read(retirement,'api')
            if retirement is not None:retirement['receipt']=None
            observed=owner.run(argv,str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],spec['rssBytes'],str(output),
                      spec['logBytes'],control=check,observe_child_exit=True)
            if retirement is not None:retirement['receipt']=observed
            raw=proc_curl_response(slot,name,'https://api.github.com/'+target,spec['logBytes'])
            value=json.loads(raw) if document else None
            collected[name]={'state':'collected','sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)}
            return value
        except BaseException as error:
            failures.append({'stage':name,'cause':failure_record(error)})
            receipt_path=output.with_suffix('.log.result.json')
            if retirement is not None and receipt_path.is_file():retirement['receipt']=json.loads(receipt_path.read_bytes())
            latest=retirement.get('receipt')
            if (retirement.get('storagePrivateHold') or retirement.get('redirectHeadersDisposition')=='held-uncertain-retirement'
                    or not latest or latest.get('cleanup')!='observed-empty-group' or latest.get('custody')!='observed-owned-group'):
                raise ValueError('PROC terminal evidence child custody/cleanup unknown') from error
            collected[name]={'state':'failed'}
            if hasattr(error,'proc_read_status'):collected[name]['status']=error.proc_read_status
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
            if archive.get('state')=='collected' and (selected[0].get('digest')!='sha256:'+archive['sha256'] or selected[0]['size_in_bytes']!=archive['bytes']):
                archive['state']='failed';failures.append({'stage':'terminal-archive','cause':{'message':'native artifact archive digest differs'}})
        else:failures.append({'stage':'terminal-archive','cause':{'message':'artifact unavailable, unsafe, incomplete or exceeds remaining selected output bound'}})
    return {'records':collected,'additionalFailures':failures,'requests':retirement['readCounts'],
            'storageInputRetirements':retirement.get('storageInputRetirements',[]),'publicationAccepted':False}


def proc_stat(pid):
    try:
        text=Path('/proc',str(pid),'stat').read_text();fields=text[text.rindex(')')+2:].split()
        return {'pid':pid,'start':int(fields[19]),'ppid':int(fields[1]),'pgid':int(fields[2]),'sid':int(fields[3]),
                'state':fields[0],'rss':max(0,int(fields[21]))*os.sysconf('SC_PAGE_SIZE')}
    except FileNotFoundError:return None


def proc_actor(pid):
    """Fresh kernel DATA identity; a vanished task is not a fabricated lifetime."""
    try:
        row=proc_stat(pid)
        if row is None:return None
        status={}
        for line in Path('/proc',str(pid),'status').read_text().splitlines():
            if ':' in line:
                key,value=line.split(':',1);status[key]=value.strip()
        row.update(uid=int(status['Uid'].split()[0]),tgid=int(status['Tgid']),tracerPid=int(status['TracerPid']),
                   namespaces={name:os.readlink(Path('/proc',str(pid),'ns',name)) for name in ('pid','mnt','user')})
        fresh=proc_stat(pid)
        if fresh is None:return None
        if fresh['start']!=row['start']:raise ValueError('PROC apparatus PID changed during metadata observation')
        return row
    except FileNotFoundError:return None


def proc_private_record(path,value,deadline,*,exclusive=False,expected=None,cap=MAX_OUTPUT):
    path=Path(path);parts=[];size=0
    for part in json.JSONEncoder(sort_keys=True).iterencode(value):
        if time.monotonic()>=deadline:raise TimeoutError('PROC original private serialization deadline')
        raw=part.encode();size+=len(raw)
        if size+1>cap:raise ValueError('PROC finite measurement result byte budget')
        parts.append(raw)
    raw=b''.join(parts)+b'\n'
    if time.monotonic()>=deadline:raise TimeoutError('PROC original private serialization deadline')
    if expected is not None and (path.is_symlink() or path.stat().st_size!=len(expected) or path.read_bytes()!=expected):raise ValueError('PROC measurement collector state drift')
    destination=path if exclusive else path.with_name(path.name+'.pending')
    fd=os.open(destination,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600);first=None;identity=None;offset=0
    try:
        identity=proc_read_identity(destination)
        while offset<len(raw):
            if time.monotonic()>=deadline:raise TimeoutError('PROC original measurement report write deadline')
            count=os.write(fd,raw[offset:])
            if count<=0:raise OSError('PROC measurement report zero write')
            offset+=count
        os.fsync(fd)
        if time.monotonic()>=deadline:raise TimeoutError('PROC original report fsync deadline')
    except BaseException as error:first=error
    finally:
        try:os.close(fd)
        except BaseException as error:
            if first is None:first=error
            else:first.proc_report_secondary=failure_record(error)
    if first is None:
        try:
            if time.monotonic()>=deadline:raise TimeoutError('PROC original report close deadline')
            if proc_read_identity(destination)!=identity:raise ValueError('PROC private report destination identity drift')
            if not exclusive:
                if expected is not None and (path.is_symlink() or path.stat().st_size!=len(expected) or path.read_bytes()!=expected):raise ValueError('PROC measurement collector changed before replace')
                if time.monotonic()>=deadline:raise TimeoutError('PROC original report replace deadline')
                os.replace(destination,path)
                destination=path
                if time.monotonic()>=deadline:raise TimeoutError('PROC original report post-replace deadline')
        except BaseException as error:first=error
    if first is not None:
        first.proc_private_report={'path':str(destination),'disposition':'held-partial-or-completed-report','identity':identity,'bytesWritten':offset}
        raise first
    return raw



class ProcLosslessSourcePrototype:
    """Inert FD/codec feasibility only; never selected by a command profile.

    Existing traced command execution does not call this type. Selection here is
    source-control opt-in, not root admission or a source/read-closure assertion.
    The owner supplies exclusively held fresh output FDs and a nonblocking FIFO;
    this codec adds no process, source lookup, deadline or caller FD ownership.
    """
    STORED_CAP = 8 * 1024**2
    EXPANDED_CAP = 32 * 1024**2
    MANIFEST_RESERVE = 16384

    def __init__(self, selection, work_end, *, stored_cap=STORED_CAP, expanded_cap=EXPANDED_CAP):
        if selection != 'source-feasibility-only':
            raise ValueError('PROC lossless encoding is not selected')
        if (type(work_end) not in (int,float) or not math.isfinite(work_end)
                or work_end <= time.monotonic()):
            raise ValueError('PROC lossless original work deadline unavailable')
        if (type(stored_cap) is not int or not self.MANIFEST_RESERVE < stored_cap <= self.STORED_CAP
                or type(expanded_cap) is not int or not 0 < expanded_cap <= self.EXPANDED_CAP):
            raise ValueError('PROC lossless fixed resource bounds')
        self.work_end=work_end;self.stored_cap=stored_cap;self.expanded_cap=expanded_cap
        self.stored=0;self.captured=0;self.decoded=0;self.failed=False;self.rows=[];self.finished=False

    def _tick(self):
        if self.failed or self.finished:raise ValueError('PROC lossless consumed source prototype')
        if time.monotonic() >= self.work_end:raise TimeoutError('PROC lossless original work deadline')

    @staticmethod
    def _identity(fd):
        s=os.fstat(fd)
        return (s.st_dev,s.st_ino,s.st_uid,s.st_mode,s.st_nlink,s.st_size,s.st_mtime_ns,s.st_ctime_ns)

    def _fresh_output(self, fd):
        observed=self._identity(fd)
        if (not stat.S_ISREG(observed[3]) or observed[2]!=os.geteuid()
                or stat.S_IMODE(observed[3])!=0o600 or observed[4]!=1
                or observed[5]!=0 or os.lseek(fd,0,os.SEEK_CUR)!=0):
            raise ValueError('PROC lossless output must be exclusively held fresh private regular FD')
        return observed

    def _write(self, fd, data, *, manifest=False):
        limit=self.stored_cap if manifest else self.stored_cap-self.MANIFEST_RESERVE
        self._tick()
        if self.stored+len(data)>limit:raise ValueError('PROC lossless aggregate stored bound')
        offset=0
        while offset<len(data):
            self._tick();count=os.write(fd,data[offset:])
            if count<=0:raise OSError('PROC lossless short output write')
            offset+=count;self.stored+=count

    def capture(self, phase, source_fd, output_fd):
        """Drain a finite nonblocking source pipe through one lossless gzip member.

        Caller closes its FDs independently. On any failure this whole prototype
        is consumed; partial bytes remain held, and no retry/manifest follows.
        """
        import select
        import zlib
        import fcntl
        try:
            self._tick()
            if phase not in ('controller','session','normal') or phase in {r['phase'] for r in self.rows}:
                raise ValueError('PROC lossless phase population/replay')
            if not stat.S_ISFIFO(os.fstat(source_fd).st_mode) or not fcntl.fcntl(source_fd,fcntl.F_GETFL)&os.O_NONBLOCK:
                raise ValueError('PROC lossless source must be a nonblocking pipe')
            original=self._fresh_output(output_fd);codec=zlib.compressobj(6,zlib.DEFLATED,31)
            digest=hashlib.sha256();count=0;start_stored=self.stored
            while True:
                self._tick()
                readable,_,_=select.select([source_fd],[],[],min(.02,self.work_end-time.monotonic()))
                if not readable:continue
                # At saturation refuse conservatively without materializing an
                # extra byte. EOF already observed is the only completion proof.
                room=self.expanded_cap-self.captured
                if room<=0:raise ValueError('PROC lossless aggregate captured bound')
                try:raw=os.read(source_fd,min(65536,room))
                except BlockingIOError:continue
                if not raw:break
                self.captured+=len(raw);count+=len(raw);digest.update(raw)
                self._write(output_fd,codec.compress(raw))
            self._write(output_fd,codec.flush())
            current=self._identity(output_fd)
            if (current[:5]!=original[:5] or current[5]!=self.stored-start_stored
                    or os.lseek(output_fd,0,os.SEEK_CUR)!=current[5]):
                raise ValueError('PROC lossless output FD identity/size drift')
            row={'phase':phase,'bytes':count,'sha256':digest.hexdigest(),'storedBytes':current[5]}
            self.rows.append(row);return dict(row)
        except BaseException:
            self.failed=True;raise

    def read(self, source_fd, expected, consume):
        """Strict single-member bounded read; callbacks do not confer acceptance.

        All decoded bytes are delivered in order, including final fragments.
        Extra members/trailing bytes, CRC failure and missing EOF refuse. A
        callback failure consumes the prototype without another callback/retry.
        """
        import zlib
        try:
            self._tick();identity=self._identity(source_fd)
            if (not stat.S_ISREG(identity[3]) or identity[2]!=os.geteuid()
                    or stat.S_IMODE(identity[3])!=0o600 or identity[4]!=1
                    or not 0<identity[5]<=self.stored_cap-self.MANIFEST_RESERVE
                    or os.lseek(source_fd,0,os.SEEK_CUR)!=0):
                raise ValueError('PROC lossless encoded input custody/bound')
            codec=zlib.decompressobj(31);digest=hashlib.sha256();count=0;encoded=0
            while True:
                self._tick();chunk=os.read(source_fd,4096);encoded+=len(chunk)
                if encoded>identity[5]:raise ValueError('PROC lossless encoded input grew')
                if not chunk:break
                if codec.eof:raise ValueError('PROC lossless trailing data/member')
                tail=chunk
                while tail:
                    self._tick();room=self.expanded_cap-self.decoded
                    if room<=0:raise ValueError('PROC lossless aggregate expanded bound')
                    raw=codec.decompress(tail,min(65536,room));tail=codec.unconsumed_tail
                    self.decoded+=len(raw);count+=len(raw);digest.update(raw)
                    if raw:consume(raw)
                    if codec.unused_data:raise ValueError('PROC lossless trailing data/member')
            if not codec.eof or self._identity(source_fd)!=identity or encoded!=identity[5]:
                raise ValueError('PROC lossless truncated/drifting input')
            if count!=expected['bytes'] or digest.hexdigest()!=expected['sha256']:
                raise ValueError('PROC lossless complete decoded identity differs')
            return {'bytes':count,'sha256':digest.hexdigest()}
        except BaseException:
            self.failed=True;raise

    def finish(self, output_fd):
        """Bounded manifest reserves storage; never a native/complete-trace receipt."""
        try:
            self._tick();self._fresh_output(output_fd)
            if {r['phase'] for r in self.rows}!={'controller','session','normal'}:
                raise ValueError('PROC lossless incomplete phase census')
            body=json.dumps({'schema':'fsgg.proc.lossless-source-prototype/1',
                'nativeAccepted':False,'completeTraceQualified':False,'rows':self.rows,
                'storedBytesBeforeManifest':self.stored,'expandedBytes':self.captured},sort_keys=True).encode()+b'\n'
            if len(body)>self.MANIFEST_RESERVE:raise ValueError('PROC lossless manifest reserve')
            self._write(output_fd,body,manifest=True);self.finished=True;return body
        except BaseException:
            self.failed=True;raise



# A source-control profile is deliberately not a command descriptor/profile. No
# CLI or production command selects it; an eventual runtime profile needs its
# separately reviewed source/read closure and machine admission.
PROC_FUTURE_TRACE_SOURCE_PROFILE = {
    'schema':'fsgg.proc.future-trace-source-profile/1',
    'encoding':'gzip-pid-stream/1','sourceOnly':True,
    'storedBytes':8*1024**2,'expandedBytes':32*1024**2}

# Historical exact bytes already declared in ordinal 4's retained read closure.
# These are prospective SOURCE inputs, never a refresh of that consumed operation.
PROC_FUTURE_TRACE_RUNTIME = {
    'python': {'path':'/usr/bin/python3.14','bytes':14424,'mode':0o755,
               'sha256':'375532f83be620283ab780c14ca6bcc5907dc379d2b621de0c5d0a112cf5458b'},
    'libpython': {'path':'/usr/lib/libpython3.14.so.1.0','bytes':6344456,'mode':0o755,
                  'sha256':'0a5be42fd90e91b9d96010310ac288037a001fe7aa582345cda4e37d15a01500'},
    'zlibExtension': {'path':'/usr/lib/python3.14/lib-dynload/zlib.cpython-314-x86_64-linux-gnu.so',
                      'bytes':45624,'mode':0o755,
                      'sha256':'679b434b38b14fc48cdc7e09fad1110c7f01b0b0257aa9f76afd87e83d09c7b4'},
    'libz': {'path':'/usr/lib/libz.so.1.3.2','bytes':104464,'mode':0o755,
             'sha256':'9ba92a0b85dc9b659e8f5e596a69452cec801def6ece0883a8a2c1f032e52397'}}
PROC_FUTURE_TRACE_PRODUCER_SOURCE = {
    'archiveSha256':'4bde6246926890dcee824f6e6ac42a06752f47d77e5097d86e3c0d6d4b709fe5',
    'straceCSha256':'06525ee83048d477139312ad96e750585354e5f9f56663bdde0f959dce5b16dc',
    'argvContract':'fixed-named-fifo-follower-no-output-pipe/1'}


def proc_future_source_closure(spec):
    """Derive a finite prospective source join, without inspecting the live host.

    Existing descriptor/bootstrap authority and proc_read_drift still own all
    other pins and each actual per-use observation. A matching digest alone is
    neither authenticated bootstrap nor qualification/runtime admission.
    """
    pins=spec.get('pins');evidence=spec.get('readEvidence')
    if (type(pins) is not list or len(pins)>512 or type(evidence) is not dict
            or set(evidence)!={'contract','directories','aliases','absences','runtimeData'}
            or evidence['contract']!='ordinary-exact-profile-observation-and-drift'
            or any(type(evidence[key]) is not list for key in ('directories','aliases','absences'))):
        raise ValueError('PROC future source closure inventory unavailable')
    by_path={row.get('path'):row for row in pins if type(row) is dict}
    if len(by_path)!=len(pins):raise ValueError('PROC future source closure duplicate/malformed pin')
    runtime={key:dict(value) for key,value in PROC_FUTURE_TRACE_RUNTIME.items()}
    tracer_path=spec.get('transport',{}).get('tracer')
    runtime['tracer']={'path':tracer_path,'bytes':1706640,'mode':0o500,'sha256':PROC_STRACE_SHA}
    directories={row.get('path'):row for row in evidence['directories'] if type(row) is dict}
    if len(directories)!=len(evidence['directories']):raise ValueError('PROC future source closure duplicate directory')
    for role,pin in runtime.items():
        if type(pin['path']) is not str or not Path(pin['path']).is_absolute() or by_path.get(pin['path'])!=pin:
            raise ValueError('PROC future source closure exact '+role+' pin unavailable')
        if pin['path'] not in spec.get('sourceReadAllowlist',[]):
            raise ValueError('PROC future source closure undeclared '+role+' read')
        path=Path(pin['path']);directory=directories.get(str(path.parent),{})
        identity=directory.get('relevantEntries',{}).get(path.name,{})
        if (identity.get('kind')!=stat.S_IFREG or identity.get('mode')!=pin['mode']
                or identity.get('uid')!=(os.geteuid() if role=='tracer' else 0)
                or type(identity.get('device')) is not int or type(identity.get('inode')) is not int):
            raise ValueError('PROC future source closure '+role+' directory join unavailable')
    libz=Path(runtime['libz']['path']);alias=str(libz.parent/'libz.so.1')
    aliases=[row for row in evidence['aliases'] if type(row) is dict and row.get('path')==alias]
    if (len(aliases)!=1 or aliases[0].get('resolved')!=str(libz) or aliases[0].get('target')!=libz.name
            or aliases[0].get('identity',{}).get('kind')!=stat.S_IFLNK):
        raise ValueError('PROC future source closure libz alias unavailable')
    entry=directories.get(str(libz.parent),{}).get('relevantEntries',{}).get('libz.so.1')
    if entry!=aliases[0]['identity']:raise ValueError('PROC future source closure libz alias directory differs')
    for level in ('x86-64-v2','x86-64-v3','x86-64-v4'):
        path=str(libz.parent/'glibc-hwcaps'/level/'libz.so.1')
        rows=[row for row in evidence['absences'] if type(row) is dict and row.get('path')==path]
        if (len(rows)!=1 or rows[0].get('errno')!='ENOENT' or rows[0].get('ancestor')!=str(libz.parent)
                or rows[0].get('missingComponents')!=['glibc-hwcaps',level,'libz.so.1']
                or rows[0].get('ancestorIdentity')!=directories[str(libz.parent)].get('identity')):
            raise ValueError('PROC future source closure libz negative lookup unavailable')
    # A bounded canonical snapshot binds the COMPLETE declared inventory, rather
    # than dropping its other libraries, aliases or negative lookups to these roles.
    raw=json.dumps(evidence,sort_keys=True,separators=(',',':'),ensure_ascii=True).encode('ascii')
    if len(raw)>256*1024:raise ValueError('PROC future source closure inventory bound')
    return {'schema':'fsgg.proc.future-trace-source-closure/1','sourceOnly':True,
            'profile':dict(PROC_FUTURE_TRACE_SOURCE_PROFILE),'runtime':runtime,
            'producerSource':dict(PROC_FUTURE_TRACE_PRODUCER_SOURCE),
            'readEvidenceSha256':hashlib.sha256(raw).hexdigest()}


def proc_future_source_closure_digest(closure):
    raw=json.dumps(closure,sort_keys=True,separators=(',',':'),ensure_ascii=True).encode('ascii')
    return hashlib.sha256(b'fsgg.proc.future-trace-source-closure/1\0'+raw).hexdigest()


def proc_future_source_closure_binding(spec, bootstrap, authority):
    closure=proc_future_source_closure(spec)
    if (bootstrap.get('futureTraceSourceClosure')!=closure
            or authority.get('futureTraceSourceClosure')!=closure
            or bootstrap.get('readEvidence')!=spec['readEvidence']
            or any(pin not in bootstrap.get('candidateSupportPins',[]) for pin in closure['runtime'].values())):
        raise ValueError('PROC future source closure bootstrap/authority differ')
    return proc_future_source_closure_digest(closure)


def proc_future_source_profile(profile):
    if profile is None:return False
    if (type(profile) is not dict or set(profile)!=set(PROC_FUTURE_TRACE_SOURCE_PROFILE)
            or any(type(profile[key]) is not type(value) or profile[key]!=value
                   for key,value in PROC_FUTURE_TRACE_SOURCE_PROFILE.items())):
        raise ValueError('PROC future trace source profile not separately selected')
    return True


def proc_future_source_context(spec, context):
    """Validate the exact prospective capability, never infer one from a name."""
    if context is None:return None
    if (type(context) is not dict or set(context) not in ({'profile','namespaceIdentity','fifos'},
            {'profile','namespaceIdentity','fifos','sourceClosureSha256'})
            or not proc_future_source_profile(context['profile'])
            or spec.get('role')!='qualification' or spec.get('request',{}).get('nativeGet') is not False):
        raise ValueError('PROC future trace context outside selected source profile')
    if ('sourceClosureSha256' in context and context['sourceClosureSha256']!=
            proc_future_source_closure_digest(proc_future_source_closure(spec))):
        raise ValueError('PROC future trace source closure context drift')
    root=Path(spec['operationRoot'])/'lossless-traces'
    if proc_directory_identity(root)!=context['namespaceIdentity']:
        raise ValueError('PROC future trace namespace capability drift')
    fifos=context['fifos']
    if (type(fifos) is not dict or not 1<=len(fifos)<=3
            or any(path not in {str(root/(phase+'.fifo')) for phase in ('controller','session','normal')}
                   for path in fifos)):
        raise ValueError('PROC future trace fixed FIFO capability census')
    for path,row in fifos.items():
        if (type(row) not in (list,tuple) or len(row)!=7 or any(type(n) is not int for n in row)
                or row[2]!=os.geteuid() or row[4]!=(stat.S_IFIFO|0o600) or row[5:] not in ([1,0],(1,0))):
            raise ValueError('PROC future trace FIFO capability shape')
        observed=os.lstat(path)
        actual=(observed.st_dev,observed.st_ino,observed.st_uid,observed.st_gid,observed.st_mode,observed.st_nlink,observed.st_size)
        if actual!=tuple(row):raise ValueError('PROC future trace FIFO identity drift')
    return fifos


class ProcFutureTraceStore:
    """Fixed prospective trace namespace, held capabilities and monitor drain.

    The retained codec proof is reused without a shell or additional actor.
    This source-only route never confers normal/native qualification acceptance.
    """
    PHASES=('controller','session','normal')
    NAMESPACE='lossless-traces'

    def __init__(self, spec, profile, *, source_closure_sha=None):
        if (not proc_future_source_profile(profile) or spec.get('role')!='qualification'
                or spec.get('request',{}).get('nativeGet') is not False):
            raise ValueError('PROC future trace source profile not separately selected')
        if (source_closure_sha is not None and source_closure_sha!=
                proc_future_source_closure_digest(proc_future_source_closure(spec))):
            raise ValueError('PROC future trace source closure store differs')
        self.source_closure_sha=source_closure_sha
        self.spec=spec;self.root=Path(spec['operationRoot'])/self.NAMESPACE
        self.work_end=spec['workDeadline'];self.total_end=spec['totalDeadline']
        if (not all(type(x) in (int,float) and math.isfinite(x) for x in (self.work_end,self.total_end))
                or not time.monotonic()<self.work_end<self.total_end):
            raise ValueError('PROC future trace original clocks')
        self.root.mkdir(mode=0o700)
        self.root_identity=proc_directory_identity(self.root)
        self.directory=os.open(self.root,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try:
            self.codec=ProcLosslessSourcePrototype('source-feasibility-only',self.work_end)
            self.reader=ProcLosslessSourcePrototype('source-feasibility-only',self.work_end)
        except BaseException as first:
            try:os.close(self.directory)
            except BaseException as close_error:first.proc_measurement_additional=[failure_record(close_error)]
            raise
        self.objects={};self.phases={};self.failed=False;self.closed=False;self.manifest=None

    @staticmethod
    def identity(s):
        return (s.st_dev,s.st_ino,s.st_uid,s.st_gid,s.st_mode,s.st_nlink,s.st_size,s.st_mtime_ns,s.st_ctime_ns)

    def _check(self, *, retirement=False):
        if self.closed:raise ValueError('PROC future trace store already closed')
        if time.monotonic()>=(self.total_end if retirement else self.work_end):
            raise TimeoutError('PROC future trace original clock')
        if proc_directory_identity(self.root)!=self.root_identity:
            raise ValueError('PROC future trace namespace path binding drift')
        directory=os.fstat(self.directory)
        if (directory.st_dev,directory.st_ino)!=(self.root_identity['device'],self.root_identity['inode']):
            raise ValueError('PROC future trace retained directory differs')
        if set(os.listdir(self.directory))!=set(self.objects):
            raise ValueError('PROC future trace complete namespace census drift')
        total=0
        for name,row in self.objects.items():
            current=self.identity(os.fstat(row['fd']))
            named=self.identity(os.stat(name,dir_fd=self.directory,follow_symlinks=False))
            # FIFO timestamps reflect I/O, not a replacement. Every identity,
            # kind/mode/link/owner/size predicate is nevertheless observed anew.
            width=7 if row['fifo'] else 9
            if current[:width]!=row['identity'][:width] or named[:width]!=current[:width]:
                raise ValueError('PROC future trace artifact FD/path custody drift')
            total+=current[6]
        if total!=self.codec.stored or total>self.codec.stored_cap:
            raise ValueError('PROC future trace aggregate stored-byte census differs')
        return total

    def fifo_capabilities(self):
        self._check()
        return {str(self.root/name):row['identity'][:7] for name,row in self.objects.items() if row['fifo']}

    def context(self,*,retirement=False):
        self._check(retirement=retirement)
        context={'profile':dict(PROC_FUTURE_TRACE_SOURCE_PROFILE),'namespaceIdentity':dict(self.root_identity),
                'fifos':{str(self.root/name):row['identity'][:7] for name,row in self.objects.items() if row['fifo']}}
        if self.source_closure_sha is not None:
            if self.source_closure_sha!=proc_future_source_closure_digest(proc_future_source_closure(self.spec)):
                raise ValueError('PROC future trace source closure store drift')
            context['sourceClosureSha256']=self.source_closure_sha
        return context

    def _add(self,name,*,fifo=False):
        if name in self.objects:raise ValueError('PROC future trace occupied artifact')
        if fifo:
            os.mkfifo(name,0o600,dir_fd=self.directory)
            fd=os.open(name,os.O_RDONLY|os.O_NONBLOCK|os.O_NOFOLLOW,dir_fd=self.directory)
        else:
            fd=os.open(name,os.O_RDWR|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=self.directory)
        try:observed=self.identity(os.fstat(fd))
        except BaseException as first:
            try:os.close(fd)
            except BaseException as close_error:first.proc_measurement_additional=[failure_record(close_error)]
            raise
        self.objects[name]={'fd':fd,'fifo':fifo,'identity':observed}
        if (observed[2]!=os.geteuid() or stat.S_IMODE(observed[4])!=0o600 or observed[5]!=1
                or observed[6]!=0 or (stat.S_ISFIFO(observed[4]) if fifo else stat.S_ISREG(observed[4])) is not True):
            raise ValueError('PROC future trace fresh artifact custody')
        return fd

    def begin(self,phase):
        import zlib
        self._check()
        if self.failed or self.manifest is not None or len(self.phases)>=len(self.PHASES) or phase!=self.PHASES[len(self.phases)]:
            raise ValueError('PROC future trace phase order/consumption')
        source=self._add(phase+'.fifo',fifo=True);output=self._add(phase+'.gz')
        self.phases[phase]={'readFd':source,'outputFd':output,'codec':zlib.compressobj(6,zlib.DEFLATED,31),
            'digest':hashlib.sha256(),'encodedDigest':hashlib.sha256(),'bytes':0,'storedBytes':0,
            'seen':False,'eof':False,'sealed':False}
        self._check();return self.root/(phase+'.fifo')

    def _write(self,phase,data):
        state=self.phases[phase]
        self.codec._write(state['outputFd'],data)
        state['encodedDigest'].update(data);state['storedBytes']+=len(data)
        self.objects[phase+'.gz']['identity']=self.identity(os.fstat(state['outputFd']))

    def drain(self,phase,*,retirement=False):
        """At most four bounded chunks; caller retains its existing monitor tick."""
        self._check(retirement=retirement)
        state=self.phases[phase]
        if state['sealed'] or self.failed:raise ValueError('PROC future trace consumed phase')
        original_end=self.codec.work_end
        if retirement:self.codec.work_end=self.total_end
        try:
            for _ in range(4):
                self._check(retirement=retirement)
                room=self.codec.expanded_cap-self.codec.captured
                if room<=0:raise ValueError('PROC future trace aggregate expanded bound')
                try:raw=os.read(state['readFd'],min(65536,room))
                except BlockingIOError:return
                if not raw:
                    # Initial FIFO EOF is not evidence that a writer ever opened.
                    if state['seen']:state['eof']=True
                    return
                if state['eof']:raise ValueError('PROC future trace writer reopened after EOF')
                state['seen']=True;state['bytes']+=len(raw);self.codec.captured+=len(raw)
                state['digest'].update(raw);self._write(phase,state['codec'].compress(raw))
        except BaseException:
            self.failed=True;raise
        finally:self.codec.work_end=original_end

    def seal(self,phase,*,tracer_terminal):
        """Terminal-plus-drain under the original TOTAL retirement clock only."""
        if tracer_terminal is not True:raise ValueError('PROC future trace terminal unavailable')
        state=self.phases[phase];original_end=self.codec.work_end
        self.codec.work_end=self.total_end
        try:
            while not state['eof']:
                self.drain(phase,retirement=True)
                if not state['seen']:raise ValueError('PROC future trace no observed writer output')
            self._write(phase,state['codec'].flush());state['sealed']=True
            state['row']={'phase':phase,'bytes':state['bytes'],'sha256':state['digest'].hexdigest(),
                'storedBytes':state['storedBytes'],'encodedSha256':state['encodedDigest'].hexdigest(),
                'completeNormalTraceQualified':False}
            self.codec.rows.append(dict(state['row']));self._check(retirement=True)
            return dict(state['row'])
        except BaseException:
            self.failed=True;raise
        finally:self.codec.work_end=original_end

    def verify_hashes(self):
        self._check(retirement=True)
        for phase,state in self.phases.items():
            if not state['sealed']:raise ValueError('PROC future trace unsealed phase')
            fd=state['outputFd'];identity=self.identity(os.fstat(fd));os.lseek(fd,0,os.SEEK_SET);digest=hashlib.sha256();size=0
            while True:
                self._check(retirement=True);raw=os.read(fd,65536)
                if not raw:break
                size+=len(raw);digest.update(raw)
            if (self.identity(os.fstat(fd))!=identity or size!=state['storedBytes']
                    or digest.hexdigest()!=state['row']['encodedSha256']):
                raise ValueError('PROC future trace earlier artifact content drift')
        self._check(retirement=True)

    def project(self,phase,*,partial=False):
        """Strict PID framing to derived per-PID bytes, never physical-file claims."""
        self.verify_hashes();state=self.phases[phase];fd=state['outputFd'];os.lseek(fd,0,os.SEEK_SET)
        projections={};tail=bytearray();original_end=self.reader.work_end;self.reader.work_end=self.total_end
        def consume(raw):
            tail.extend(raw)
            if b'\n' not in tail and len(tail)>65536:raise ValueError('PROC future trace line bound')
            while b'\n' in tail:
                at=tail.index(10)
                if at+1>65536:raise ValueError('PROC future trace line bound')
                line=bytes(tail[:at+1]);del tail[:at+1]
                match=re.fullmatch(rb'([1-9][0-9]{0,9}) +([0-9]+\.[0-9]+ .*)\n',line)
                if match is None:raise ValueError('PROC future trace PID/timestamp framing')
                pid=int(match.group(1));projections.setdefault(pid,bytearray()).extend(match.group(2)+b'\n')
                if len(projections)>32:raise ValueError('PROC future trace PID population')
        try:
            self.reader.read(fd,state['row'],consume)
            if tail and not partial:raise ValueError('PROC future trace incomplete normal line')
            if not projections:raise ValueError('PROC future trace missing PID records')
            return ([(pid,bytes(raw)) for pid,raw in sorted(projections.items(),key=lambda row:'qualification.trace.'+str(row[0]))],
                    {'partialTailBytes':len(tail),'partialTailSha256':hashlib.sha256(tail).hexdigest(),
                     'physicalArtifact':str(self.root/(phase+'.gz')),'encodedSha256':state['row']['encodedSha256']})
        finally:self.reader.work_end=original_end

    def finish(self):
        self.verify_hashes()
        if self.failed or set(self.phases)!=set(self.PHASES) or self.manifest is not None:
            raise ValueError('PROC future trace incomplete/consumed manifest')
        fd=self._add('manifest.json');original_end=self.codec.work_end;self.codec.work_end=self.total_end
        try:
            body=self.codec.finish(fd)
            self.objects['manifest.json']['identity']=self.identity(os.fstat(fd))
            self.manifest={'path':str(self.root/'manifest.json'),'bytes':len(body),'sha256':hashlib.sha256(body).hexdigest()}
            self._check(retirement=True);return dict(self.manifest)
        finally:self.codec.work_end=original_end

    def close(self):
        """Close every owned sibling independently; preserve all close failures."""
        errors=[]
        if not self.closed:
            for row in self.objects.values():
                try:os.close(row['fd'])
                except BaseException as error:errors.append(error)
            try:os.close(self.directory)
            except BaseException as error:errors.append(error)
            self.closed=True
        if errors:
            errors[0].proc_measurement_additional=[failure_record(e) for e in errors[1:]]
            raise errors[0]


def proc_trace_argv(spec,slot,descriptor,authority_path,authority_sha,handshake_fd,phase,*,trace_store=None):
    if spec['role']!='qualification' or phase not in ('normal','controller','session') or type(handshake_fd) is not int or not 3<=handshake_fd<=1024:
        raise ValueError('PROC closed measurement role/handshake')
    calls='%file,%process,%memory,read,pread64,readv,socket,accept,accept4,socketpair,pipe,pipe2,eventfd,eventfd2,epoll_create,epoll_create1,timerfd_create,signalfd,signalfd4,inotify_init,inotify_init1,memfd_create,pidfd_open,close,close_range,dup,dup2,dup3,fcntl,fcntl64,getdents64,io_uring_setup,io_uring_enter,io_uring_register,io_setup,io_destroy,io_submit,io_cancel,io_getevents,io_pgetevents,process_vm_readv,process_vm_writev'
    argv=[spec['transport']['tracer'],'-ff','--kill-on-exit','--interruptible=2','-ttt','-yy','-s','4096','-e','trace='+calls,'-e','raw=read,pread64,readv',
            '-o',str(Path(slot)/'qualification.trace'),os.path.realpath(sys.executable),'-I','-S','-B',str(Path(__file__).resolve()),
            'command','--proc-measured-controller',str(descriptor),str(authority_path),authority_sha,str(handshake_fd),phase]

    if trace_store is not None:
        if type(trace_store) is not ProcFutureTraceStore or trace_store.spec is not spec or phase not in trace_store.phases:
            raise ValueError('PROC future trace producer not selected')
        trace_store._check();argv[1]='-f';argv[argv.index('-o')+1]=str(trace_store.root/(phase+'.fifo'))
    return argv


def proc_measured_handshake(spec,descriptor,authority_path,authority_sha,fd,phase):
    import select
    if spec['role']!='qualification' or phase not in ('normal','controller','session') or not stat.S_ISFIFO(os.fstat(fd).st_mode):
        raise ValueError('PROC forged measured role/handshake kind')
    import fcntl
    if fcntl.fcntl(fd,fcntl.F_GETFL)&os.O_ACCMODE!=os.O_RDONLY:raise ValueError('PROC handshake must be read-only')
    remaining=spec['workDeadline']-time.monotonic()
    if remaining<=0 or not select.select([fd],[],[],min(2,remaining))[0]:raise TimeoutError('PROC measured handshake original deadline')
    raw=os.read(fd,8193);os.close(fd)
    if not 0<len(raw)<=8192:raise ValueError('PROC handshake framing bound')
    proof=json.loads(raw);boot=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    expected_fields={'bootId','apparatus','tracer','descriptorSha256','authoritySha256','phase','slot','subjectWorkDeadline'}
    if 'futureTrace' in proof:
        future=proof['futureTrace'];authority_raw=Path(authority_path).read_bytes()
        if (future is None or hashlib.sha256(authority_raw).hexdigest()!=authority_sha
                or json.loads(authority_raw).get('futureTraceSourceProfile')!=future.get('profile')
                or future.get('sourceClosureSha256')!=proc_future_source_closure_digest(proc_future_source_closure(spec))
                or json.loads(authority_raw).get('futureTraceSourceClosure')!=proc_future_source_closure(spec)
                or proc_future_source_context(spec,future) is None):
            raise ValueError('PROC future trace handshake profile/capability unavailable')
        expected_fields.add('futureTrace')
    if (set(proof)!=expected_fields
            or proof['bootId']!=boot or proof['phase']!=phase or proof['authoritySha256']!=authority_sha
            or proof['descriptorSha256']!=hashlib.sha256(Path(descriptor).read_bytes()).hexdigest()
            or proof['subjectWorkDeadline']!=spec['workDeadline']-10):raise ValueError('PROC measured bootstrap binding unavailable')
    tracer=proc_actor(os.getppid());self_row=proc_actor(os.getpid());apparatus=proc_actor(proof['apparatus']['pid'])
    for row,expected in ((tracer,proof['tracer']),(apparatus,proof['apparatus'])):
        if row is None or any(row[k]!=expected[k] for k in ('pid','start','uid','pgid','sid')):raise ValueError('PROC stale apparatus/tracer birth')
    if (self_row is None or self_row['tracerPid']!=tracer['pid'] or tracer['ppid']!=apparatus['pid']
            or tracer['uid']!=os.geteuid() or apparatus['uid']!=os.geteuid()
            or pin_file(Path('/proc',str(tracer['pid']),'exe').resolve())['sha256']!=PROC_STRACE_SHA
            or str(Path('/proc',str(apparatus['pid']),'exe').resolve())!=os.path.realpath(sys.executable)):
        raise ValueError('PROC actual traced subject/parent executable relation unavailable')
    root=Path(spec['operationRoot']);slot=root if phase=='normal' else root/('control-'+phase)
    if proof['slot']!=str(slot) or proc_directory_identity(root) is None:raise ValueError('PROC measured fixed artifact slot')
    state=json.loads((root/'command.json').read_bytes())
    if state.get('sendState')!='measurement-started' or state.get('descriptorSha256')!=proof['descriptorSha256']:
        raise ValueError('PROC measured role lacks original exclusive measurement claim')
    # The actual closed entry consumes the ready artifact once. It is not another
    # operation or send identity and cannot initialize/retry the canonical slot.
    proc_private_record(slot/'measurement.ready.json',{'bootId':boot,'subject':self_row,'tracer':tracer,'subjectWorkDeadline':proof['subjectWorkDeadline'],
        'phase':phase,'descriptorSha256':proof['descriptorSha256']},spec['workDeadline']-10,exclusive=True)
    return proof


def proc_measurement_bounds(spec,authority,bootstrap,slot,known,tracer,subject_started,*,trace_store=None):
    proc_capacity_evidence(spec,authority,bootstrap['capacity']);proc_read_drift(spec)
    if set(os.sched_getaffinity(0))!=set(spec['cpuAffinity']):raise ValueError('PROC apparatus actual affinity drift')
    trace_files=list(Path(spec['operationRoot']).rglob('qualification.trace.*'))
    if len(trace_files)>128 or any(path.is_symlink() or not path.is_file() for path in trace_files) or sum(path.stat().st_size for path in trace_files)>8*1024**2:
        raise ValueError('PROC apparatus live 8MiB trace bound')
    if trace_store is None:
        census(Path(spec['operationRoot']),min(spec['totalDeadline'],time.monotonic()+1),limit=spec['diskBytes'])
    else:
        if type(trace_store) is not ProcFutureTraceStore or trace_store.spec is not spec:raise ValueError('PROC future trace bound capability')
        census(Path(spec['operationRoot']),min(spec['totalDeadline'],time.monotonic()+1),limit=spec['diskBytes'],future_fifos=trace_store.fifo_capabilities())
    rows={}
    for path in Path('/proc').iterdir():
        if path.name.isascii() and path.name.isdigit():
            row=proc_stat(int(path.name))
            if row is not None:rows[row['pid']]=row
    apparatus=rows.get(os.getpid())
    if apparatus is None:raise ValueError('PROC apparatus live census unavailable')
    candidates={apparatus['pid']:apparatus}
    if tracer is not None:
        current=rows.get(tracer['pid'])
        if current is not None and current['start']!=tracer['start']:raise ValueError('PROC tracer PID reused')
        if current is not None:candidates[current['pid']]=current
    changed=True
    while changed:
        changed=False
        for row in rows.values():
            if row['ppid'] in candidates and row['pid'] not in candidates:
                parent=candidates[row['ppid']]
                if parent['pid'] in known and known[parent['pid']]['start']!=parent['start']:raise ValueError('PROC apparatus parent birth reused')
                candidates[row['pid']]=row;changed=True
    for pid,birth in list(known.items()):
        current=rows.get(pid)
        if current is not None:
            if current['start']!=birth['start']:raise ValueError('PROC sampled measurement PID reuse')
            candidates[pid]=current
    pinned={os.path.realpath(sys.executable),str(Path(spec['transport']['tracer']).resolve()),str(Path(spec['transport']['client']).resolve())}
    samples=[];tgids={}
    for pid,row in candidates.items():
        row=proc_actor(pid)
        if row is None:continue
        if row['uid']!=os.geteuid() or row['namespaces']!=proc_actor(os.getpid())['namespaces']:
            raise ValueError('PROC foreign measurement UID/namespace')
        if row['state'] not in ('Z','X','x'):
            try:exe=str(Path('/proc',str(pid),'exe').resolve(strict=True))
            except FileNotFoundError:
                fresh=proc_actor(pid)
                if fresh is not None and fresh['start']==row['start'] and fresh['state'] not in ('Z','X','x'):raise
                continue
            if exe not in pinned:raise ValueError('PROC unclassified live measurement executable')
            if subject_started and pid not in (os.getpid(),tracer['pid']) and row['ppid']==tracer['pid'] and exe==str(Path(spec['transport']['tracer']).resolve()):
                raise ValueError('PROC unexpected live instrumentation after authenticated subject start')
        if pid not in known:
            if len(known)>=128:raise ValueError('PROC finite sampled measurement birth bound')
            known[pid]={'pid':pid,'start':row['start'],'tgid':row['tgid'],'instrumentationCandidate':tracer is not None and row['ppid']==tracer['pid']}
        tgids[row['tgid']]=max(tgids.get(row['tgid'],0),row['rss']);samples.append({k:row[k] for k in ('pid','start','tgid','state')})
    aggregate=sum(tgids.values())
    if aggregate>spec['rssBytes']:raise MemoryError('PROC aggregate sampled apparatus/tracer/subject RSS bound')
    return {'rssBytes':aggregate,'actors':samples}


def proc_private_artifact(path,cap):
    """Bounded private regular-file read with the existing no-follow FD identity pattern."""
    path=Path(path)
    if not path.is_absolute() or not 0<cap<=MAX_OUTPUT or any(p.is_symlink() for p in (path,*path.parents)):
        raise ValueError('PROC private artifact path/bound unavailable')
    before=path.lstat();identity=proc_read_identity(path)
    def snapshot(row):
        return (row.st_dev,row.st_ino,row.st_mode,row.st_uid,row.st_nlink,row.st_size,row.st_mtime_ns,row.st_ctime_ns)
    if (identity['kind']!=stat.S_IFREG or identity['uid']!=os.geteuid() or identity['mode']!=0o600
            or before.st_nlink!=1 or not 0<before.st_size<=cap):
        raise ValueError('PROC private artifact regular/owner/link/mode/size unavailable')
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    try:
        opened=os.fstat(fd)
        if snapshot(opened)!=snapshot(before):raise ValueError('PROC private artifact opened identity drift')
        raw=os.read(fd,cap+1);after=os.fstat(fd)
        if (snapshot(after)!=snapshot(before) or len(raw)!=before.st_size or len(raw)>cap
                or snapshot(path.lstat())!=snapshot(before) or proc_read_identity(path)!=identity
                or any(p.is_symlink() for p in (path,*path.parents))):
            raise ValueError('PROC private artifact identity/size drift')
        return raw
    finally:os.close(fd)


def proc_control_retirement_evidence(spec,slot,descriptor,authority_path,authority_sha,record,proof,child,*,trace_store=None):
    """Actual negative-control death and partial trace custody; never read closure."""
    phase=record['phase'];root=Path(spec['operationRoot']);slot=Path(slot)
    if (spec['profile']!='proc-pair-client-qualification' or spec['role']!='qualification'
            or phase not in ('controller','session') or slot!=root/('control-'+phase) or Path(descriptor)!=root/'input.json'
            or root!=proc_operation_root(spec['operation'],spec['qualificationAttempt'])
            or proof is None or child is None or record.get('measurementBinding')!=proof
            or proof['phase']!=phase or proof['slot']!=str(slot)
            or proof['bootId']!=Path('/proc/sys/kernel/random/boot_id').read_text().strip()
            or proof['descriptorSha256']!=hashlib.sha256(Path(descriptor).read_bytes()).hexdigest()
            or proof['authoritySha256']!=authority_sha
            or hashlib.sha256(Path(authority_path).read_bytes()).hexdigest()!=authority_sha
            or proof['subjectWorkDeadline']!=spec['workDeadline']-10):
        raise ValueError('PROC actual negative-control input/phase/origin binding unavailable')
    apparatus=proc_actor(os.getpid());tracer=record['tracer'];ready=record['subjectReadiness']
    if apparatus is None or any(apparatus[k]!=proof['apparatus'][k] for k in ('pid','start','uid','pgid','sid','namespaces')):
        raise ValueError('PROC actual negative-control apparatus birth unavailable')
    if (record['firstCause'] is not None or record['additionalFailures']!=[]
            or record['subjectStartAuthenticated'] is not True or ready is None
            or record['pidfdSignalSent'] is not True or record['tracerExit']!=-signal.SIGKILL
            or record['cleanup']!='observed-known-terminal' or record['residuals']!=[]
            or child.pid!=tracer['pid'] or child.returncode!=-signal.SIGKILL
            or tracer!=proof['tracer'] or tracer['ppid']!=apparatus['pid']
            or tracer['uid']!=os.geteuid() or tracer['namespaces']!=apparatus['namespaces']
            or tracer['pgid']!=tracer['pid'] or tracer['sid']!=tracer['pid']
            or ready['bootId']!=proof['bootId'] or ready['descriptorSha256']!=proof['descriptorSha256']
            or ready['phase']!=phase or ready['subjectWorkDeadline']!=proof['subjectWorkDeadline']
            or any(ready['tracer'][k]!=tracer[k] for k in ('pid','start','uid','ppid','pgid','sid','namespaces'))):
        raise ValueError('PROC negative-control authenticated signal/terminal/cleanup unavailable')
    ready_path=slot/'measurement.ready.json'
    if ready!=json.loads(proc_private_artifact(ready_path,8192)):
        raise ValueError('PROC negative-control original ready artifact differs')
    subject=ready['subject'];known={row['pid']:row for row in record['sampledBirths']}
    actors=[proof['apparatus'],tracer,subject]
    if (subject['ppid']!=tracer['pid'] or subject['tracerPid']!=tracer['pid']
            or subject['uid']!=os.geteuid() or subject['namespaces']!=apparatus['namespaces']
            or subject['pgid']!=tracer['pgid'] or subject['sid']!=tracer['sid']
            or not apparatus['start']<=tracer['start']<=subject['start']):
        raise ValueError('PROC negative-control authenticated subject relation unavailable')
    control=record['controlReadiness']
    if phase=='controller':
        if control is not None or (slot/'measurement.control.json').exists():
            raise ValueError('PROC controller control readiness must be authenticated subject only')
    else:
        if (control is None or control!=json.loads(proc_private_artifact(slot/'measurement.control.json',8192))
                or control['phase']!='session' or control['bootId']!=proof['bootId']
                or any(control['controller'][k]!=subject[k] for k in ('pid','start','uid','ppid','pgid','sid','namespaces'))):
            raise ValueError('PROC session child/controller readiness binding unavailable')
        nested=control['child'];actors.append(nested)
        if (nested['ppid']!=subject['pid'] or nested['tracerPid']!=tracer['pid']
                or nested['uid']!=os.geteuid() or nested['namespaces']!=apparatus['namespaces']
                or nested['sid']!=nested['pid'] or nested['pgid']!=nested['pid'] or nested['start']<subject['start']):
            raise ValueError('PROC session child actual birth/session relation unavailable')
    if len(known)!=len(record['sampledBirths']):raise ValueError('PROC negative-control duplicate sampled birth')
    if len({actor['pid'] for actor in actors})!=len(actors):raise ValueError('PROC negative-control actor identity exchange')
    for actor in actors:
        if (type(actor['pid']) is not int or type(actor['start']) is not int or actor['pid']<=0 or actor['start']<=0
                or actor['pid'] not in known or known[actor['pid']]['start']!=actor['start']):
            raise ValueError('PROC negative-control sampled birth join unavailable')
    for actor in known.values():
        if type(actor['pid']) is not int or type(actor['start']) is not int or actor['pid']<=0 or actor['start']<=0:
            raise ValueError('PROC negative-control sampled birth identity unavailable')
        current=proc_actor(actor['pid'])
        if actor['pid']!=apparatus['pid'] and current is not None and (current['start']!=actor['start'] or current['state'] not in ('Z','X','x')):
            raise ValueError('PROC negative-control current terminal birth unavailable')
    if trace_store is not None:
        if type(trace_store) is not ProcFutureTraceStore or trace_store.spec is not spec:
            raise ValueError('PROC future negative-control source unavailable')
        projected,physical=trace_store.project(phase,partial=True)
        expected={actor['pid'] for actor in actors[2:]}
        if {pid for pid,_ in projected}!=expected:
            raise ValueError('PROC future negative-control partial PID population unavailable')
        artifacts=[{'pid':pid,'bytes':len(raw),'sha256':hashlib.sha256(raw).hexdigest(),
                    'derivedProjection':True,'physicalArtifact':physical} for pid,raw in projected]
    else:
        traces=sorted(slot.glob('qualification.trace.*'));expected={actor['pid'] for actor in actors[2:]};artifacts=[];total=0
        for trace in traces:
            if not re.fullmatch(r'qualification\.trace\.[1-9][0-9]*',trace.name):raise ValueError('PROC negative-control trace artifact identity')
            observed=trace.lstat();identity=proc_read_identity(trace)
            if (any(p.is_symlink() for p in (trace,*trace.parents)) or not stat.S_ISREG(observed.st_mode)
                    or observed.st_uid!=os.geteuid() or observed.st_nlink!=1 or total+observed.st_size>8*1024**2):
                raise ValueError('PROC negative-control partial trace custody/bound unavailable')
            raw=trace.read_bytes();total+=len(raw)
            if proc_read_identity(trace)!=identity or len(raw)!=observed.st_size or total>8*1024**2:
                raise ValueError('PROC negative-control partial trace identity/byte drift')
            artifacts.append({'pid':int(trace.name.rsplit('.',1)[1]),'path':str(trace),'sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)})
        if not traces or len(traces)>32 or {row['pid'] for row in artifacts}!=expected:
            raise ValueError('PROC negative-control partial trace actor population unavailable')
    return {'contract':'actual-negative-control-retirement/1','phase':phase,'measurementBinding':proof,
            'subjectReadiness':ready,'controlReadiness':control,'knownTerminalBirths':[row for row in record['sampledBirths'] if row['pid']!=apparatus['pid']],
            'pidfdSignalSent':True,'tracerExit':-signal.SIGKILL,'cleanup':'observed-known-terminal',
            'traceArtifacts':artifacts,'traceComplete':False,'readClosureQualified':False,
            'publicationAuthorized':False,'limitation':'Tracer was intentionally aborted; retained traces are incomplete diagnostics, never complete lifetime/read proof.'}


def proc_measure_once(spec,authority,bootstrap,descriptor,authority_path,authority_sha,phase,*,trace_store=None):
    import subprocess
    root=Path(spec['operationRoot']);slot=root if phase=='normal' else root/('control-'+phase)
    if phase!='normal':slot.mkdir(mode=0o700)
    subject_deadline=spec['workDeadline']-10
    if time.monotonic()>=subject_deadline:raise TimeoutError('PROC original subject-work reserve exhausted')
    if trace_store is not None:
        if (type(trace_store) is not ProcFutureTraceStore or trace_store.spec is not spec
                or not proc_future_source_profile(authority.get('futureTraceSourceProfile'))):
            raise ValueError('PROC future trace measure profile unavailable')
    elif authority.get('futureTraceSourceProfile') is not None:
        raise ValueError('PROC future trace encoding unselected by caller')
    read_fd,write_fd=os.pipe();child=None;tracer=None;pidfd=None;first=None;known={};samples=[];peak=0;ready=None;control=None;signal_sent=False;code=None;additional=[];proof=None
    stream=None
    launch_started=time.monotonic()
    try:
        stream=(slot/'measurement.tracer.log').open('xb');os.chmod(slot/'measurement.tracer.log',0o600)
        if trace_store is not None:
            trace_store.begin(phase)
            argv=proc_trace_argv(spec,slot,root/'input.json',authority_path,authority_sha,read_fd,phase,trace_store=trace_store)
        else:argv=proc_trace_argv(spec,slot,root/'input.json',authority_path,authority_sha,read_fd,phase)
        child=subprocess.Popen(argv,cwd=str(root),env=dict(spec['environment']),stdin=subprocess.DEVNULL,stdout=stream,stderr=stream,
                               close_fds=True,pass_fds=(read_fd,),start_new_session=True)
        os.close(read_fd);read_fd=None
        tracer=proc_actor(child.pid)
        if tracer is None or tracer['ppid']!=os.getpid() or tracer['uid']!=os.geteuid() or tracer['pgid']!=tracer['pid'] or tracer['sid']!=tracer['pid']:
            raise ValueError('PROC actual direct tracer birth unavailable')
        # Popen can be sampled before exec; wait briefly within the same deadline.
        while str(Path('/proc',str(child.pid),'exe').resolve(strict=True))!=str(Path(spec['transport']['tracer']).resolve()):
            if child.poll() is not None or time.monotonic()>=min(subject_deadline,launch_started+2):raise ValueError('PROC tracer actual pinned executable startup unavailable')
            time.sleep(.01)
        pidfd=os.pidfd_open(tracer['pid']);fresh=proc_actor(tracer['pid'])
        if fresh is None or fresh['start']!=tracer['start']:raise ValueError('PROC tracer pidfd original birth drift')
        apparatus=proc_actor(os.getpid())
        proof={'bootId':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'apparatus':apparatus,'tracer':tracer,
               'descriptorSha256':hashlib.sha256(Path(descriptor).read_bytes()).hexdigest(),'authoritySha256':authority_sha,
               'phase':phase,'slot':str(slot),'subjectWorkDeadline':subject_deadline}
        if trace_store is not None:proof['futureTrace']=trace_store.context()
        wire=json.dumps(proof,sort_keys=True).encode()
        if len(wire)>8192 or os.write(write_fd,wire)!=len(wire):raise ValueError('PROC handshake one-use write failure')
        os.close(write_fd);write_fd=None
        while True:
            if trace_store is not None:trace_store.drain(phase)
            if (hashlib.sha256(Path(descriptor).read_bytes()).hexdigest()!=authority['descriptorSha256']
                    or hashlib.sha256(Path(authority_path).read_bytes()).hexdigest()!=authority_sha
                    or proc_directory_identity(root)!=authority['canonicalOperationIdentity']
                    or proc_directory_identity(root.parent)!=authority['canonicalQualificationLineageIdentity']
                    or proc_directory_identity(PROC_OPERATION_BASE/str(os.geteuid()))!=authority['canonicalBaseIdentity']):
                raise ValueError('PROC apparatus pinned input/canonical lineage drift')
            ready_path=slot/'measurement.ready.json'
            if ready is None and ready_path.is_file():
                ready=json.loads(proc_private_artifact(ready_path,8192));subject=proc_actor(ready['subject']['pid'])
                if (ready['bootId']!=proof['bootId'] or ready['phase']!=phase or ready['descriptorSha256']!=proof['descriptorSha256']
                        or subject is None or subject['start']!=ready['subject']['start'] or subject['tracerPid']!=tracer['pid']):
                    raise ValueError('PROC actual measured subject readiness unavailable')
            if trace_store is None:sample=proc_measurement_bounds(spec,authority,bootstrap,slot,known,tracer,ready is not None)
            else:sample=proc_measurement_bounds(spec,authority,bootstrap,slot,known,tracer,ready is not None,trace_store=trace_store)
            sample={'elapsedMilliseconds':int((time.monotonic()-spec['started'])*1000),'rssBytes':sample['rssBytes'],
                    'actorCount':len(sample['actors'])}
            if len(samples)>=1000:raise ValueError('PROC apparatus finite sample/report bound')
            # Bound reporting incrementally; retain first cause rather than silently
            # omitting birth records or emitting an unbounded successful receipt.
            if len(json.dumps(samples+[sample]).encode())>64*1024:raise ValueError('PROC apparatus sample receipt byte bound')
            samples.append(sample);peak=max(peak,sample['rssBytes'])
            if (slot/'measurement.tracer.log').stat().st_size>spec['logBytes']:raise ValueError('PROC apparatus tracer log bound')
            if phase!='normal' and ready is not None:
                control_ready=slot/'measurement.control.json'
                if phase=='controller' or control_ready.is_file():
                    if phase=='session':
                        control=json.loads(proc_private_artifact(control_ready,8192));current=proc_actor(control['child']['pid'])
                        parent=proc_actor(control['controller']['pid'])
                        if (control.get('phase')!='session' or control.get('bootId')!=proof['bootId'] or current is None or parent is None
                                or current['start']!=control['child']['start'] or current['ppid']!=ready['subject']['pid']
                                or current['tracerPid']!=tracer['pid'] or parent['start']!=ready['subject']['start']
                                or current['sid']!=current['pid'] or current['pgid']!=current['pid']):
                            raise ValueError('PROC actual kernel descendant/session readiness unavailable')
                        known[current['pid']]={'pid':current['pid'],'start':current['start'],'tgid':current['tgid'],'instrumentationCandidate':False}
                    signal.pidfd_send_signal(pidfd,signal.SIGKILL);signal_sent=True;break
            code=child.poll()
            if code is not None:break
            if time.monotonic()>=(spec['workDeadline'] if phase=='normal' else subject_deadline):raise TimeoutError('PROC original measured subject/cleanup deadline')
            time.sleep(.1)
    except BaseException as error:first=error
    finally:
        def cleanup(action):
            nonlocal first
            try:return action()
            except BaseException as error:
                if first is None:first=error
                else:additional.append(failure_record(error))
                additional.extend(getattr(error,'proc_measurement_additional',[]))
                return None
        for fd in (read_fd,write_fd):
            if fd is not None:cleanup(lambda fd=fd:os.close(fd))
        if child is not None:
            status=cleanup(child.poll)
            if status is None:
                if pidfd is not None and tracer is not None:
                    current=cleanup(lambda:proc_actor(tracer['pid']))
                    if current is not None and current['start']==tracer['start']:
                        sent=cleanup(lambda:(signal.pidfd_send_signal(pidfd,signal.SIGKILL),True)[1])
                        if sent is True:signal_sent=True
                code=cleanup(lambda:child.wait(timeout=max(.001,min(10,spec['workDeadline']-time.monotonic()))))
            else:code=status
        if trace_store is not None and phase in trace_store.phases:
            cleanup(lambda:trace_store.seal(phase,tracer_terminal=code is not None))
        if stream is not None:cleanup(stream.close)
        if pidfd is not None:cleanup(lambda:os.close(pidfd))
    residual=[]
    while True:
        residual=[]
        for pid,birth in known.items():
            if pid==os.getpid():continue
            try:current=proc_actor(pid)
            except (OSError,ValueError) as error:
                if first is None:first=error
                residual.append({'pid':pid,'start':birth['start'],'observation':'unreadable'});continue
            if current is not None and (current['start']!=birth['start'] or current['state'] not in ('Z','X','x')):residual.append(current)
        if not residual or time.monotonic()>=spec['workDeadline']:break
        time.sleep(.05)
    cleanup='observed-known-terminal' if ready is not None and not residual and code is not None else 'unknown-private-hold'
    # A measured subject's retained cause precedes apparatus cleanup/reporting
    # errors. They remain secondary; neither may erase the consumed child result.
    if phase=='normal' and code is not None and code!=0:
        error=ValueError('PROC measured terminal grammar differs')
        failed=slot/'measurement.subject.json'
        try:
            if failed.is_file() and not failed.is_symlink() and 0<failed.stat().st_size<=MAX_OUTPUT:
                detail=json.loads(failed.read_bytes())
                error.proc_subject_failure=detail.get('firstCause')
                error.proc_subject_result=detail
        except BaseException as read_error:additional.append(failure_record(read_error))
        if getattr(error,'proc_subject_failure',None) is not None or first is None:
            if first is not None:
                additional.append(failure_record(first))
                if hasattr(first,'proc_measurement_report_disposition'):error.proc_measurement_report_disposition=first.proc_measurement_report_disposition
            first=error
    record={'phase':phase,'measurementBinding':proof,'additionalFailures':additional,'firstCause':getattr(first,'proc_subject_failure',None) or failure_record(first),'tracer':tracer,'tracerExit':code,'pidfdSignalSent':signal_sent,
        'subjectStartAuthenticated':ready is not None,'subjectReadiness':ready,'controlReadiness':control,
        'sampledBirths':list(known.values()),'samples':samples,'sampledPeakRssBytes':peak,
        'unsampledInstrumentation':{'sourceProbeBound':2,'count':None,'identities':None,'lifetimes':None,'peakRssBytes':None},
        'instrumentationRetirementBasis':'source-normal-startup-plus-sampled-terminal' if ready is not None else 'not-established',
        'residuals':residual,'cleanup':cleanup,'measurementLimitation':'Sampled ordinary accounting; no complete instrumentation census or transient peak guarantee.'}
    try:proc_private_record(slot/'measurement.apparatus.json',record,spec['totalDeadline'],exclusive=True)
    except BaseException as error:
        if first is None:first=error
        else:additional.append(failure_record(error))
        if hasattr(error,'proc_private_report'):
            first.proc_measurement_report_disposition=error.proc_private_report
    if first is not None:
        first.proc_measurement_additional=additional
        raise first
    if cleanup!='observed-known-terminal':raise ValueError('PROC measurement cleanup unknown; retain private hold')
    if phase!='normal' and (not signal_sent or code!=-signal.SIGKILL):raise ValueError('PROC kernel control terminal grammar differs')
    if phase!='normal':
        if trace_store is None:retirement=proc_control_retirement_evidence(spec,slot,descriptor,authority_path,authority_sha,record,proof,child)
        else:retirement=proc_control_retirement_evidence(spec,slot,descriptor,authority_path,authority_sha,record,proof,child,trace_store=trace_store)
        record['controlRetirement']=retirement
        # The original observation remains immutable; this separate bounded join is
        # a fixed control artifact, never a replacement successful normal trace.
        proc_private_record(slot/'measurement.control-proof.json',{'apparatusSha256':hashlib.sha256((slot/'measurement.apparatus.json').read_bytes()).hexdigest(),
            'retirement':retirement,'expectedTracerAbort':True},spec['totalDeadline'],exclusive=True)
    return record


def proc_kernel_child(descriptor,authority_path,authority_sha):
    spec=json.loads(Path(descriptor).read_bytes());proc_command_preflight(spec,now=time.monotonic())
    root=Path(spec['operationRoot']);slot=root/'control-session'
    if not sys.flags.isolated or not sys.flags.no_site or not sys.dont_write_bytecode or sys.flags.optimize or spec['role']!='qualification':
        raise ValueError('PROC closed unoptimized kernel child role')
    if hashlib.sha256(Path(authority_path).read_bytes()).hexdigest()!=authority_sha:raise ValueError('PROC kernel child root input drift')
    ready=json.loads(proc_private_artifact(slot/'measurement.ready.json',8192));parent=proc_actor(os.getppid());actual=proc_actor(os.getpid())
    if (parent is None or actual is None or parent['pid']!=ready['subject']['pid'] or parent['start']!=ready['subject']['start']
            or actual['tracerPid']!=ready['tracer']['pid'] or parent['tracerPid']!=actual['tracerPid']
            or actual['pgid']!=actual['pid'] or actual['sid']!=actual['pid']):
        raise ValueError('PROC kernel child lacks actual measured parent/session/tracer relation')
    proc_private_record(slot/'measurement.control.json',{'phase':'session','child':actual,'controller':parent,
        'bootId':ready['bootId']},ready['subjectWorkDeadline'],exclusive=True)
    while time.monotonic()<ready['subjectWorkDeadline']:time.sleep(.1)
    raise TimeoutError('PROC kernel control tracer did not abort before original deadline')


def proc_load_qualification_header(spec,authority,measurement):
    if (authority.get('qualificationCredentialReference')!=PROC_QUALIFICATION_CREDENTIAL or authority.get('nativeCollectorApproved') is not True
            or spec['request']['credentialReference']!=PROC_QUALIFICATION_CREDENTIAL):
        raise ValueError('PROC separate root read-only qualification credential admission unavailable')
    root=Path(spec['operationRoot']);state=json.loads((root/'command.json').read_bytes());semantic=state.get('semanticHeader') or {}
    if (set(semantic)!={'path','identity','bytes','creator','ordinal','purpose'} or semantic['path']!=str(root/'authorization.header')
            or semantic['ordinal']!=spec['qualificationAttempt'] or semantic['purpose']!=PROC_QUALIFICATION_CREDENTIAL
            or semantic['creator']['pid']!=measurement['apparatus']['pid'] or semantic['creator']['start']!=measurement['apparatus']['start']):
        raise ValueError('PROC exact apparatus-created semantic header provenance unavailable')
    creator=proc_actor(semantic['creator']['pid'])
    if creator is None or creator['start']!=semantic['creator']['start']:raise ValueError('PROC semantic input creator birth unavailable')
    fd=os.open(root/'authorization.header',os.O_RDONLY|os.O_NOFOLLOW)
    try:
        current=os.fstat(fd);raw=os.read(fd,8192+64)
        identity={'device':current.st_dev,'inode':current.st_ino,'uid':current.st_uid,'mode':stat.S_IMODE(current.st_mode),'kind':stat.S_IFMT(current.st_mode)}
        if (identity!=semantic['identity'] or current.st_nlink!=1 or current.st_uid!=os.geteuid() or current.st_mode&0o777!=0o600
                or current.st_size!=semantic['bytes'] or not 0<len(raw)<=8192+64
                or not re.fullmatch(rb'Authorization: Bearer [\x21-\x7e]+\n',raw)):
            raise ValueError('PROC qualification semantic single-header input drift/shape')
    finally:os.close(fd)
    return {'path':semantic['path'],'identity':identity,'_expectedBytes':raw}


def proc_native_qualification(owner,spec,slot,env,check,header,deadline,destination):
    selected=dict(spec);selected['workDeadline']=deadline
    retirement={'profile':'qualification','receipt':None};records={}
    destination.update(nativeCollectorObserved=False,records=records,requests=retirement.setdefault('readCounts',{'api':0,'storage':0,'total':0}),
                       consumedLegs=retirement.setdefault('consumedLegs',[]),readLifetime=retirement,firstCause=None)
    try:
        def guarded(*args):check(*args);proc_sensitive_unchanged(header)
        proc_charge_read(retirement,'api','repo-public')
        argv=proc_curl_argv(selected,slot,'repos/'+PROC_REPOSITORY,'GET',authorization=False,
                            remaining=deadline-time.monotonic(),name='qualification.native')
        receipt=owner.run(argv,str(slot),env,deadline,spec['cleanupSeconds'],spec['rssBytes'],str(slot/'qualification.native.log'),
                          spec['logBytes'],control=guarded,observe_child_exit=True)
        repository=json.loads(proc_curl_response(slot,'qualification.native','https://api.github.com/repos/'+PROC_REPOSITORY,spec['logBytes']))
        if (repository.get('id')!=1346720714 or repository.get('full_name')!=PROC_REPOSITORY
                or repository.get('private') is not False or repository.get('visibility')!='public'):
            raise ValueError('PROC fresh fixed public native repository identity differs')
        records['publicRepository']={'id':1346720714,'receipt':receipt}
        log=proc_storage_read(owner,selected,slot,env,guarded,'repos/'+PROC_REPOSITORY+'/actions/jobs/115630940784/logs','terminal-job',retirement)
        text=log.decode('utf-8','strict')
        if '\x00' in text or not 0<len(text.splitlines())<=10000:raise ValueError('PROC bounded original native log framing refusal')
        records['originalJob']={'jobId':115630940784,'runId':38495729817,'runAttempt':1,'head':spec['originalPublisher'],
                                'bytes':len(log),'sha256':hashlib.sha256(log).hexdigest(),'lines':len(text.splitlines())}
        archive=proc_storage_read(owner,selected,slot,env,guarded,'repos/'+PROC_REPOSITORY+'/actions/artifacts/11863691435/zip','terminal-archive',retirement)
        digest=hashlib.sha256(archive).hexdigest()
        if len(archive)!=497795 or digest!='e885b5a0a80807e461559b89547dda91c9c822a89216145a0092b53e3a3c879e':
            raise ValueError('PROC fresh original native archive identity differs; no historical substitution')
        records['originalArtifact']={'artifactId':11863691435,'runId':38495729817,'bytes':len(archive),'sha256':digest}
        readers=retirement.get('apiReaders',[])
        if (len(readers)!=2 or any(row['receipt'].get('cleanup')!='observed-empty-group' or row['receipt'].get('custody')!='observed-owned-group' for row in readers)
                or retirement.get('storagePrivateHold') or retirement['readCounts']!={'api':3,'storage':2,'total':5}):
            raise ValueError('PROC all native readers/legs retirement unavailable')
        proc_sensitive_unchanged(header)
        destination.update({'nativeCollectorObserved':True,'records':records,'requests':retirement['readCounts'],
                'consumedLegs':retirement['consumedLegs'],'apiReaders':readers,'storageRetirements':retirement.get('storageInputRetirements',[])})
        return destination

    except BaseException as error:
        destination['firstCause']=failure_record(error)
        if hasattr(error,'proc_read_status'):destination['failureStatus']=error.proc_read_status
        raise


def proc_measured_entry(descriptor,authority_path,authority_sha,fd,phase):
    if not sys.flags.isolated or not sys.flags.no_site or not sys.dont_write_bytecode or sys.flags.optimize:
        raise ValueError('PROC measured actual interpreter flags')
    spec=json.loads(Path(descriptor).read_bytes());proc_command_preflight(spec,now=time.monotonic())
    if dict(os.environ)!=spec['environment'] or os.getcwd()!=spec['operationRoot']:
        raise ValueError('PROC measured inherited environment/cwd differs from fixed profile')
    if os.readlink('/proc/self/fd/0')!='/dev/null':raise ValueError('PROC measured input must be dev-null')
    proof=proc_measured_handshake(spec,descriptor,authority_path,authority_sha,fd,phase)
    if phase=='normal':return execute_proc_command(descriptor,authority_path,authority_sha,measurement=proof)
    if phase=='controller':
        while time.monotonic()<proof['subjectWorkDeadline']:time.sleep(.1)
        raise TimeoutError('PROC controller negative control was not aborted')
    # One ordinary source-owned Python leader/session under the unchanged owner.
    path=Path(__file__).with_name('shared_process_owner.py')
    if pin_file(path)['sha256']!=OWNER_SHA:raise ValueError('PROC kernel control shared owner source drift')
    source=importlib.util.spec_from_file_location('proc_kernel_control_owner',path)
    owner=importlib.util.module_from_spec(source);source.loader.exec_module(owner)
    argv=[os.path.realpath(sys.executable),'-I','-S','-B',str(Path(__file__).resolve()),'command','--proc-kernel-child',
          str(descriptor),str(authority_path),authority_sha]
    slot=Path(proof['slot'])
    def guard(*args):
        proc_read_drift(spec)
        if time.monotonic()>=proof['subjectWorkDeadline']:raise TimeoutError('PROC original kernel-control deadline')
    owner.run(argv,str(slot),dict(spec['environment']),proof['subjectWorkDeadline'],spec['cleanupSeconds'],spec['rssBytes'],
              str(slot/'measurement.child.log'),spec['logBytes'],control=guard,observe_child_exit=True)
    raise ValueError('PROC kernel session control unexpectedly returned normally')


def proc_qualification_apparatus(spec,descriptor,authority_path,authority_sha,authority,*,trace_profile=None):
    root=Path(spec['operationRoot']);first=None;additional=[];records=[];subject=None;header=None;normal_attempted=False;trace_store=None
    if trace_profile is None:
        if authority.get('futureTraceSourceProfile') is not None:raise ValueError('PROC future trace caller has not selected a profile')
    elif (not proc_future_source_profile(trace_profile) or authority.get('futureTraceSourceProfile')!=trace_profile):
        raise ValueError('PROC future trace profile/authority differ')
    bootstrap=json.loads(Path(spec['proofInputs']['bootstrapEvidence']).read_bytes())
    proc_bootstrap_evidence(spec,authority,bootstrap)
    if bootstrap.get('instrumentation')!=PROC_INSTRUMENTATION or authority.get('instrumentation')!=PROC_INSTRUMENTATION:
        raise ValueError('PROC authenticated explicit instrumentation source boundary unavailable')
    source_closure_sha=None
    if trace_profile is not None:
        source_closure_sha=proc_future_source_closure_binding(spec,bootstrap,authority)
    if spec['request']['nativeGet'] and (authority.get('qualificationCredentialReference')!=PROC_QUALIFICATION_CREDENTIAL or authority.get('nativeCollectorApproved') is not True):
        raise ValueError('PROC qualification fixed borrowed read binding not separately admitted')
    admission=json.loads(Path(spec['proofInputs']['admission']).read_bytes())
    if admission.get('recovery',{}).get('clientQualificationEnabled') is not True:raise ValueError('PROC protected qualification remains disabled')
    for path in (root/'input.json',root/'command.json'):
        if path.is_symlink() or not path.is_file():raise ValueError('PROC root-prepared measurement state missing')
    raw=Path(descriptor).read_bytes();prepared=json.loads((root/'command.json').read_bytes())
    if ((root/'input.json').read_bytes()!=raw or prepared.get('sendState')!='prepared-no-send'
            or prepared.get('descriptorSha256')!=hashlib.sha256(raw).hexdigest()
            or authority.get('canonicalBaseIdentity')!=proc_directory_identity(PROC_OPERATION_BASE/str(os.geteuid()))
            or authority.get('canonicalQualificationLineageIdentity')!=proc_directory_identity(root.parent)
            or authority.get('canonicalOperationIdentity')!=proc_directory_identity(root)):
        raise ValueError('PROC measurement original prepared/lineage state drift')
    proc_private_record(root/'dispatch-input.json',None,spec['totalDeadline'],exclusive=True)
    state={'schema':RESULT_SCHEMA,'operation':spec['operation'],'originalOperation':PROC_ORIGINAL,
           'descriptorSha256':hashlib.sha256(raw).hexdigest(),'sendState':'measurement-started','commandAccepted':False,
           'publicationAuthorized':False,'rootAccepted':False,'qualificationAccepted':False}
    state_raw=proc_private_record(root/'command.json',state,spec['totalDeadline'],expected=(root/'command.json').read_bytes())
    try:
        if trace_profile is not None:trace_store=ProcFutureTraceStore(spec,trace_profile,source_closure_sha=source_closure_sha)
        for phase in ('controller','session'):
            if trace_store is None:records.append(proc_measure_once(spec,authority,bootstrap,descriptor,authority_path,authority_sha,phase))
            else:records.append(proc_measure_once(spec,authority,bootstrap,descriptor,authority_path,authority_sha,phase,trace_store=trace_store))
        if spec['request']['nativeGet']:
            token=os.environ.pop('GH_TOKEN',None)
            header=proc_sensitive_header(root,token,deadline=spec['workDeadline']-10);del token
            state['semanticHeader']={'path':header['path'],'identity':header['identity'],'bytes':len(header['_expectedBytes']),
                                    'creator':proc_actor(os.getpid()),'ordinal':spec['qualificationAttempt'],'purpose':PROC_QUALIFICATION_CREDENTIAL}
            state_raw=proc_private_record(root/'command.json',state,spec['totalDeadline'],expected=state_raw)
        normal_attempted=True
        if trace_store is None:records.append(proc_measure_once(spec,authority,bootstrap,descriptor,authority_path,authority_sha,'normal'))
        else:records.append(proc_measure_once(spec,authority,bootstrap,descriptor,authority_path,authority_sha,'normal',trace_store=trace_store))
        output=root/'measurement.subject.json'
        if output.is_symlink() or not 0<output.stat().st_size<=MAX_OUTPUT:raise ValueError('PROC complete measured subject result unavailable')
        subject=json.loads(output.read_bytes())
        if subject.get('commandAccepted') is not True or subject.get('firstCause') is not None or subject.get('additionalFailures')!=[]:
            raise ValueError('PROC measured canonical controller failed')
        if trace_store is None:trace=proc_trace_evidence(spec,root,authority_path,records[-1]['tracer']['pid'],apparatus_pid=os.getpid())
        else:trace=proc_trace_evidence(spec,root,authority_path,records[-1]['tracer']['pid'],apparatus_pid=os.getpid(),trace_store=trace_store)
        if not trace['clocked'] or trace['rootPid']!=records[-1]['subjectReadiness']['subject']['pid']:
            raise ValueError('PROC complete measured subject timestamp/root join unavailable')
        if header is not None:
            collector=subject.get('nativeCollector') or {};readers=collector.get('apiReaders') or []
            observed_reads={row['pid'] for row in trace['semanticReads'] if row['path']==header['path'] and row['bytes']>0}
            if (collector.get('nativeCollectorObserved') is not True or len(readers)!=2
                    or any(row['receipt'].get('cleanup')!='observed-empty-group' or row['receipt'].get('custody')!='observed-owned-group'
                           or row['receipt']['leader']['pid'] not in observed_reads for row in readers)):
                raise ValueError('PROC both API header readers lack complete raw read/retirement evidence')
            subject_birth=records[-1]['subjectReadiness']['subject'];boot=records[-1]['subjectReadiness']['bootId']
            if boot!=Path('/proc/sys/kernel/random/boot_id').read_text().strip():raise ValueError('PROC API reader boot join changed')
            forks={row['pid']:row for row in trace['forkProvenance']}
            sampled={row['pid']:row['start'] for row in records[-1]['sampledBirths']}
            processes={row['pid']:row for row in trace['processes']};joins=[]
            for reader in readers:
                leader=reader['receipt']['leader'];pid=leader['pid'];fork=forks.get(pid)
                if (type(leader.get('start')) is not int or leader['start']<subject_birth['start']
                        or leader.get('ppid')!=subject_birth['pid'] or fork is None or fork['parentPid']!=subject_birth['pid']
                        or pid not in processes or processes[pid]['terminal']!='+++ exited with 0 +++'
                        or pid in sampled and sampled[pid]!=leader['start']):
                    raise ValueError('PROC literal API read requires same-boot owner birth and unique traced fork/lifetime')
                joins.append({'stage':reader['stage'],'bootId':boot,'pid':pid,'start':leader['start'],'fork':fork,
                              'traceSha256':processes[pid]['sha256'],'headerIdentity':header['identity'],
                              'startWitness':'unchanged shared-owner live kernel birth; sampled apparatus corroboration when present'})
            state['semanticReadBirthJoins']=joins
            proc_sensitive_unchanged(header);Path(header['path']).unlink()
            if Path(header['path']).exists():raise ValueError('PROC semantic header absence not established')
            state['semanticHeaderRetirement']={'disposition':'removed-after-both-api-readers-subject-and-tracer','identity':header['identity']}
            header=None
        proc_read_drift(spec);proc_capacity_evidence(spec,authority,bootstrap['capacity'])
        state.update(subject);state['runtimeEvidence']['controllerStartupTraceComplete']=True
        state['runtimeEvidence'].pop('controllerTraceGap',None)
        state['runtimeEvidence']['traceComplete']=trace['complete'];state['completeSubjectTrace']=trace
        if trace_store is not None:
            state['futureTraceManifest']=trace_store.finish();state['futureTraceSourceOnly']=True
        state['subjectResult']={'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'bytes':output.stat().st_size}
        state['sendState']='qualification-completed';state['qualificationAccepted']=False
        state['qualificationBoundary']=('Complete subject measurement, root acceptance remains separate.' if trace_store is None
            else 'Source-only prospective representation; current CLI/runtime/root admission remains refused.')
    except BaseException as error:
        first=error;state['sendState']='qualification-failed-or-unknown'
        if hasattr(error,'proc_subject_result'):state['failedSubjectResult']=error.proc_subject_result
        additional.extend(getattr(error,'proc_measurement_additional',[]))
        if hasattr(error,'proc_measurement_report_disposition'):state['measurementReportDisposition']=error.proc_measurement_report_disposition
        if hasattr(error,'proc_sensitive_creation'):
            state['semanticHeaderCreationFailure']=error.proc_sensitive_creation
            if hasattr(error,'proc_sensitive_close_failure'):additional.append(error.proc_sensitive_close_failure)
    finally:
        if trace_store is not None:
            try:trace_store.close()
            except BaseException as error:
                if first is None:first=error
                else:additional.append(failure_record(error))
                additional.extend(getattr(error,'proc_measurement_additional',[]))
        if header is not None:
            state['semanticHeaderRetirement']={'disposition':'held-unknown-reader-retirement','identity':header['identity']}
            if not normal_attempted:
                try:
                    proc_sensitive_unchanged(header);Path(header['path']).unlink()
                    state['semanticHeaderRetirement']['disposition']='removed-before-normal-subject'
                except BaseException as error:additional.append(failure_record(error))
            elif first is None:first=ValueError('PROC semantic header remains held; no positive qualification')
        state.update(firstCause=getattr(first,'proc_subject_failure',None) or failure_record(first),additionalFailures=additional,apparatusObservations=records,
                     instrumentationBoundary=PROC_INSTRUMENTATION,publicationAuthorized=False,rootAccepted=False,qualificationAccepted=False)
        if first is not None:
            state['commandAccepted']=False
            if trace_store is not None:state['sendState']='qualification-failed-or-unknown'
        # Registration and canonical failure reporting are independent attempts.
        # An occupied registration never makes this command successful.
        for report_path in (root/'measurement.collector.json',root/'command.json'):
            state.update(firstCause=getattr(first,'proc_subject_failure',None) or failure_record(first),additionalFailures=additional)
            if first is not None:
                state['commandAccepted']=False
                if trace_store is not None:state['sendState']='qualification-failed-or-unknown'
            try:
                if report_path.name=='measurement.collector.json':
                    proc_private_record(report_path,{'descriptorSha256':state['descriptorSha256'],'collectorPid':os.getpid()},spec['totalDeadline'],exclusive=True)
                else:proc_private_record(report_path,state,spec['totalDeadline'],expected=state_raw)
            except BaseException as error:
                if first is None:first=error
                else:additional.append(failure_record(error))
                print(json.dumps({'schema':RESULT_SCHEMA,'firstCause':getattr(first,'proc_subject_failure',None) or failure_record(first),
                                  'reportingFailure':failure_record(error),'reportDisposition':getattr(error,'proc_private_report',None)}),file=sys.stderr)
    return 0 if first is None else 1


def execute_proc_command(descriptor, authority_path, authority_sha, *, measurement=None):
    """Existing canonical input/result slots, one shared owner, no send replay."""
    first=None; additional=[]; receipt=None; spec=None; result=None; claimed=False; sensitive=None; qualification_header=None; retirement={'receipt':None}
    def read_regular(path,cap):
        p=Path(path)
        if any(x.is_symlink() for x in (p,*p.parents)) or not p.is_file() or not 0<p.stat().st_size<=cap:
            raise ValueError('PROC selected descriptor/authority unsafe')
        return p.read_bytes()
    def persist(path,value,exclusive=False):
        if measurement is not None and path.name=='command.json':path=path.with_name('measurement.subject.json')
        deadline=spec['totalDeadline'] if spec is not None else time.monotonic()
        cap=256*1024 if path.name=='input.json' else MAX_OUTPUT
        return proc_private_record(path,value,deadline,exclusive=exclusive,cap=cap)
    try:
        raw=read_regular(descriptor,256*1024);spec=json.loads(raw)
        authority_raw=read_regular(authority_path,256*1024)
        if hashlib.sha256(authority_raw).hexdigest()!=authority_sha:raise ValueError('PROC root-selected authority drift')
        authority=json.loads(authority_raw)
        future_profile=authority.get('futureTraceSourceProfile')
        if future_profile is not None:
            proc_future_source_profile(future_profile)
            if (measurement is None or measurement.get('futureTrace',{}).get('profile')!=future_profile
                    or measurement.get('futureTrace',{}).get('sourceClosureSha256')!=proc_future_source_closure_digest(proc_future_source_closure(spec))
                    or authority.get('futureTraceSourceClosure')!=proc_future_source_closure(spec)
                    or proc_future_source_context(spec,measurement.get('futureTrace')) is None):
                raise ValueError('PROC future encoding has no current CLI/runtime admission')
        if (authority.get('schema')!=COMMAND_SCHEMA or authority.get('descriptorSha256')!=hashlib.sha256(raw).hexdigest()
                or authority.get('operation')!=spec['operation'] or authority.get('operationRoot')!=spec['operationRoot']
                or authority.get('profile')!=spec['profile'] or authority.get('approved') is not True
                or authority.get('independentCapacityQualified') is not True
                or spec.get('profile')!='proc-pair-client-qualification' and (authority.get('runtimeQualified') is not True
                    or authority.get('readFenceQualified') is not True or authority.get('clientAutomaticPostRetry') is not False)):
            raise ValueError('PROC current root authority/runtime/read/retry/capacity admission unavailable')
        if not sys.flags.isolated or not sys.flags.no_site or not sys.dont_write_bytecode or sys.flags.optimize!=0:
            raise ValueError('PROC actual interpreter must use isolated/no-site/no-bytecode and optimize=0 flags')
        proc_command_preflight(spec,now=time.monotonic())
        if set(os.sched_getaffinity(0))!=set(spec['cpuAffinity']):raise ValueError('PROC actual CPU affinity differs')
        import resource
        expected_address_space=resource.RLIM_INFINITY if spec['addressSpaceBytes'] is None else spec['addressSpaceBytes']
        if resource.getrlimit(resource.RLIMIT_AS)[0]!=expected_address_space:raise ValueError('PROC actual virtual address-space bound differs')
        proc_read_drift(spec)
        if os.path.realpath(sys.executable) not in spec['sourceReadAllowlist']:
            raise ValueError('PROC actual interpreter outside qualified runtime closure')
        required_sources=(Path(__file__),Path(__file__).with_name('shared_process_owner.py'),
                          Path(__file__).with_name('test-callable-cli-installed-harness.py'),
                          Path(__file__).with_name('write-callable-cli-release-readback.py'))
        if any(str(path.resolve()) not in spec['sourceReadAllowlist'] for path in required_sources):
            raise ValueError('PROC actual validator/runner/support closure absent')
        root=Path(spec['operationRoot']);parent=root.parent
        canonical_base=PROC_OPERATION_BASE/str(os.geteuid())
        if spec['role']=='qualification' and authority.get('canonicalQualificationLineageIdentity')!=proc_directory_identity(parent):
            raise ValueError('PROC root-prepared qualification lineage identity unavailable')
        base_identity=proc_directory_identity(canonical_base)
        parent_identity=proc_directory_identity(parent)
        operation_identity=proc_directory_identity(root)
        if authority.get('canonicalBaseIdentity')!=base_identity or authority.get('canonicalOperationIdentity')!=operation_identity:
            raise ValueError('PROC root-qualified canonical directory identity differs; reconciliation required')
        owner_path=Path(__file__).with_name('shared_process_owner.py')
        if not any(pin['path']==str(owner_path) and pin['sha256']==OWNER_SHA for pin in spec['pins']):
            raise ValueError('PROC shared owner closure absent')
        if spec['role']=='qualification' and measurement is None:
            if os.readlink('/proc/self/fd/0')!='/dev/null':raise ValueError('PROC qualification stdin must be root-selected dev-null')
            return proc_qualification_apparatus(spec,descriptor,authority_path,authority_sha,authority)
        owner_deadline=spec['workDeadline']
        if measurement is not None:
            actual=proc_actor(os.getpid());ready=json.loads((root/'measurement.ready.json').read_bytes())
            if (spec['role']!='qualification' or measurement.get('phase')!='normal' or actual is None
                    or actual['pid']!=ready['subject']['pid'] or actual['start']!=ready['subject']['start']
                    or actual['tracerPid']!=measurement['tracer']['pid'] or actual['tracerPid']==0
                    or ready['subjectWorkDeadline']!=spec['workDeadline']-10):
                raise ValueError('PROC measured role cannot assert root authority or launch an effect')
            owner_deadline=measurement['subjectWorkDeadline']
        validators=__import__('runpy').run_path(str(Path(__file__).with_name('test-callable-cli-installed-harness.py')))
        def document(key):return json.loads(read_regular(spec['proofInputs'][key],2*1024**2))
        admission=document('admission');selected=validators['pair_recovery_selection'](admission)
        qualification=document('bootstrapEvidence' if spec['role']=='qualification' else 'runtimeQualification')
        if spec['role']=='qualification':
            if os.readlink('/proc/self/fd/0')!='/dev/null':raise ValueError('PROC qualification stdin must be root-selected dev-null')
            proc_bootstrap_evidence(spec,authority,qualification)
        else:
            proc_runtime_evidence(spec,authority,qualification)
            proc_capacity_evidence(spec,authority,qualification.get('capacity') or {})
        correspondence=document('sourceCorrespondence')
        proc_source_correspondence(spec,correspondence)
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
                for field in ('__file__','__cached__'):
                    source=getattr(imported,field,None)
                    if source is not None and Path(source).is_file() and str(Path(source).resolve()) not in spec['sourceReadAllowlist']:
                        raise ValueError('PROC unexpected actual import/bytecode/support source')
        imports_unchanged()
        if spec['profile']=='proc-publisher-promotion':
            validators['pair_promotion_acceptance'](document('promotionEvidence'),authority.get('promotionTrust'),spec['publisher'],time.time())
            # No operational activation exists in this inert source window.
            if selected['promotion']!={'enabled':True,'sourceTrustAccepted':True,'operation':spec['operation'],'ref':PROC_PUBLISHER_REF,'kind':'lightweight'}:
                raise ValueError('PROC publisher promotion effect/source-trust gate remains disabled')
        if (spec['role']=='qualification' and selected.get('clientQualificationEnabled') is not True
                or spec['profile']=='proc-pair-recovery-dispatch' and selected['enabled'] is not True
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
            # Only explicit root preparation creates the persistent slot/input/result.
            # Missing state never means never-used; the runner cannot recreate it.
            try:
                prepared_raw=read_regular(root/'input.json',256*1024)
                prepared=json.loads(read_regular(root/'command.json',MAX_OUTPUT))
            except (OSError,ValueError) as error:
                raise ValueError('PROC canonical input/result missing or unsafe; reconciliation required') from error
            expected_state='measurement-started' if measurement is not None else 'prepared-no-send'
            if (prepared_raw!=raw or prepared.get('schema')!=RESULT_SCHEMA
                    or prepared.get('operation')!=spec['operation'] or prepared.get('sendState')!=expected_state
                    or prepared.get('descriptorSha256')!=hashlib.sha256(raw).hexdigest()):
                raise ValueError('PROC canonical prepared state differs/consumed; reconciliation required')
            # Existing canonical dispatch input is the exclusive claim. Failure here
            # cannot overwrite the first invocation's original result.
            if measurement is not None:
                if not (root/'dispatch-input.json').is_file() or (root/'measurement.subject.json').exists():raise ValueError('PROC measured original claim/result missing or occupied')
            else:persist(root/'dispatch-input.json',spec['request']['body'],exclusive=True)
            claimed=True;slot=root
        for name in ('tmp','home'):
            if spec['role']=='watch':(slot/name).mkdir(mode=0o700)
            else:proc_directory_identity(slot/name)
        if authority.get('canonicalSupportIdentity')!=proc_directory_identity(proc_support_root()):
            raise ValueError('PROC root-qualified support directory identity differs')
        proc_directory_identity(proc_support_root()/'ca-empty')
        if (read_regular(proc_support_root()/'openssl.cnf',4096)!=PROC_OPENSSL_CONFIG or not (proc_support_root()/'ca-empty').is_dir()
                or (proc_support_root()/'ca-empty').is_symlink() or list((proc_support_root()/'ca-empty').iterdir())):
            raise ValueError('PROC minimal TLS configuration/empty trust directory differs')
        if spec['role']=='watch':persist(slot/'input.json',spec,exclusive=True)
        generated_inputs=[]
        result={'schema':RESULT_SCHEMA,'operation':spec['operation'],'originalOperation':PROC_ORIGINAL,
                'commandAccepted':False,'sendState':'not-applicable' if spec['role']=='watch' else 'prepared-no-send',
                'firstCause':None,'additionalFailures':[],'ownerReceipt':None,'nativeResponse':None,
                'originalPublication':original_proof,'retainedAttestation':attestation_proof,
                'publisher':spec['publisher'],
                'remoteTerminal':'unknown','reportingOutcome':'pending','publicationAccepted':False}
        if spec['role']!='watch':
            row=pin_file(root/'dispatch-input.json');row['mode']=0o600;generated_inputs.append(row)
            persist(root/'command.json',result)
        env=dict(spec['environment'])
        if spec['role']!='qualification' and ('GH_TOKEN' not in os.environ or not os.environ['GH_TOKEN']):
            raise ValueError('PROC approved credential binding unavailable')
        def check(event,child,leader,known):
            if event!='after-cleanup' and time.monotonic()>=owner_deadline:raise TimeoutError('PROC original subject work deadline')
            if proc_directory_identity(canonical_base)!=base_identity or proc_directory_identity(parent)!=parent_identity or proc_directory_identity(root)!=operation_identity:
                raise ValueError('PROC canonical state directory identity drift; reconciliation required')
            proc_read_drift(spec)
            if authority.get('canonicalSupportIdentity')!=proc_directory_identity(proc_support_root()):
                raise ValueError('PROC root-qualified support directory identity differs')
            proc_capacity_evidence(spec,authority,qualification.get('capacity') or {})
            if read_regular(descriptor,256*1024)!=raw or read_regular(authority_path,256*1024)!=authority_raw:
                raise ValueError('PROC root-selected descriptor/authority input drift')
            if list((proc_support_root()/'ca-empty').iterdir()):raise ValueError('PROC private empty CA population drift')
            for pin in generated_inputs:proc_regular_pin(pin)
            imports_unchanged()
            if sensitive is not None:proc_sensitive_unchanged(sensitive)
            if qualification_header is not None:proc_sensitive_unchanged(qualification_header)
            future_context=(measurement or {}).get('futureTrace')
            if future_context is None:observed=census(slot,min(spec['totalDeadline'],time.monotonic()+1),limit=spec['diskBytes'])
            else:observed=census(slot,min(spec['totalDeadline'],time.monotonic()+1),limit=spec['diskBytes'],future_fifos=proc_future_source_context(spec,future_context))
            if observed['fixtureSymlinks'] or observed['objects']>25000:raise ValueError('PROC operation scope changed')
            if spec['role']=='qualification':
                traces=list(slot.glob('qualification.trace.*'))
                if len(traces)>32 or any(path.is_symlink() or not path.is_file() for path in traces) or sum(path.stat().st_size for path in traces)>8*1024**2:
                    raise ValueError('PROC live qualification trace population/8MiB bound')
            # This is observed-group read exclusion, not a claim about transient opens.
            # Root-accepted complete qualification observations and reviewed source alternatives remain mandatory.
            if leader is not None and event!='after-cleanup':
                members=owner.census(leader['pgid'])
                if len(members)>32:raise ValueError('PROC observed group population bound')
                for member in members:
                    if spec['role']=='qualification':
                        proc_join_qualification_birth(member,known,owner.proc)
                    elif known.get(member['pid'])!=member['start']:raise ValueError('PROC unknown observed group birth')
                    for target in owned_fd_targets(member,owner.proc,additional):
                        qualification_output=spec['role']=='qualification' and Path(target).parent==slot and (Path(target).name.startswith('qualification.trace.') and Path(target).name.rsplit('.',1)[-1].isdigit() or Path(target).name in ('qualification.observed.json','qualification.native.log','qualification.native.body','fixture-request.json',*(f'fixture-{case}{suffix}' for case in PROC_QUALIFICATION_CASES for suffix in ('.log','.body'))))
                        typed_metadata=target in {row['path'] for row in spec['readEvidence']['directories']}
                        if future_context is not None and target==str(Path(spec['operationRoot'])/'lossless-traces'):
                            proc_future_source_context(spec,future_context);typed_metadata=True
                        if target.startswith('/') and target not in spec['sourceReadAllowlist'] and not qualification_output and not typed_metadata and not (Path(target).parent==slot and Path(target).name in ('authorization.header','runner.body','client-preflight.body','terminal-jobs.body','terminal-artifacts.body','terminal-job.body','terminal-archive.body','storage-url.conf','terminal-job.headers','terminal-archive.headers','terminal-job-api.body','terminal-archive-api.body')) and target not in (
                                '/dev/null',str(slot/'runner.log'),str(slot/'client-preflight.log'),str(root/'dispatch-input.json'),
                                *(str(slot/(name+'.log')) for name in ('terminal-jobs','terminal-artifacts','terminal-job','terminal-archive','terminal-job-api','terminal-archive-api'))):
                            raise ValueError('PROC unexpected observed source/config read')
        module_spec=importlib.util.spec_from_file_location('proc_shared_owner',owner_path)
        owner=importlib.util.module_from_spec(module_spec);module_spec.loader.exec_module(owner)
        # Benign actual-client startup is checked before send intent, under the same
        # original limits; virtual address space is distinct from sampled RSS.
        startup=owner.run([spec['argv'][0],'--version'],str(slot),env,owner_deadline,spec['cleanupSeconds'],
                          spec['rssBytes'],str(slot/'client-preflight.log'),spec['logBytes'],control=check,observe_child_exit=True)
        version=read_regular(slot/'client-preflight.log',4096).decode().strip()
        if spec['role']!='qualification' and version!=qualification['runtimeEvidence'].get('clientVersion'):
            raise ValueError('PROC actual current client startup/version differs')
        result['clientPreflightReceipt']=startup
        if spec['role']=='qualification':
            result['sendState']='qualification-started';persist(root/'command.json',result)
            receipt,observed=proc_qualify(owner,spec,slot,env,check,authority,version,authority_path,authority_sha,owner_deadline)
            if spec['request']['nativeGet']:
                qualification_header=proc_load_qualification_header(spec,authority,measurement)
                result['nativeCollector']={}
                proc_native_qualification(owner,spec,slot,env,check,qualification_header,owner_deadline,result['nativeCollector'])
                observed['nativeGetObserved']=True;observed['nativeCollectorObserved']=True
            result['qualificationObservations']=observed
            result['capacity']=qualification['capacity']
            result['runtimeEvidence']=proc_observed_runtime(spec,observed,version)
            result['publicationAuthorized']=False;result['rootAccepted']=False;result['sendState']='qualification-completed'
            result['qualificationAccepted']=False
            result['qualificationBoundary']='worker observations complete; outer supervisor trace not established; root reconciliation required'
            result['commandAccepted']=True
        else:
            sensitive=proc_sensitive_header(slot,os.environ['GH_TOKEN'],deadline=spec['workDeadline']);retirement['receipt']=startup
            if spec['role']=='dispatch':
                if spec['profile']=='proc-publisher-promotion':
                    validators['pair_promotion_acceptance'](document('promotionEvidence'),authority.get('promotionTrust'),spec['publisher'],time.time())
                result['sendState']='send-started'
                result['nativeCreatedAfterEpoch']=time.time()
                result['nativeCreatedBeforeEpoch']=result['nativeCreatedAfterEpoch']+max(0,spec['workDeadline']-time.monotonic())+10
                # Durable intent precedes exactly one native POST. Never transition back.
                persist(root/'command.json',result)
            transfer=proc_curl_argv(spec,slot,spec['request']['endpoint'],spec['request']['method'],
                                    body=root/'dispatch-input.json' if spec['role']=='dispatch' else None,
                                    remaining=spec['workDeadline']-time.monotonic(),name='runner')
            retirement['receipt']=None
            if spec['role']=='watch':proc_charge_read(retirement,'api')
            receipt=owner.run(transfer,str(slot),env,spec['workDeadline'],spec['cleanupSeconds'],spec['rssBytes'],
                              str(slot/'runner.log'),spec['logBytes'],control=check,observe_child_exit=True)
            retirement['receipt']=receipt
            response=proc_curl_response(slot,'runner','https://api.github.com/'+spec['request']['endpoint'],spec['logBytes'],expected_status='201' if spec['profile']=='proc-publisher-promotion' else '200')
            if spec['role']=='dispatch':
                result['nativeResponse']=(validators['pair_promotion_response'](json.loads(response),spec['publisher']) if spec['profile']=='proc-publisher-promotion' else proc_dispatch_response(response));result['sendState']='consumed-accepted'
            else:
                native=proc_native_correlation(spec,prior,json.loads(response))
                result['remoteTerminal']=native.get('status','unknown');result['nativeResponse']=native
                if native.get('status')=='completed':
                    result['terminalEvidence']=proc_terminal_evidence(owner,spec,slot,env,check,native,retirement)
        result['commandAccepted']=True
    except BaseException as error:
        first=error
        if result is not None:result['commandAccepted']=False
        if result is not None and spec['role']=='qualification':result['sendState']='qualification-failed-or-unknown'
        if result is not None and spec['role']=='dispatch' and result['sendState']=='send-started':result['sendState']='consumed-unknown'
    finally:
        if result is not None:
            if time.monotonic()>=spec['totalDeadline']:
                error=TimeoutError('PROC original reporting deadline')
                if first is None:first=error
                else:additional.append(failure_record(error))
            result['firstCause']=failure_record(first);result['additionalFailures']=additional
            if first is not None and hasattr(first,'proc_sensitive_creation'):
                result['sensitiveCreationFailure']=first.proc_sensitive_creation
                if hasattr(first,'proc_sensitive_close_failure'):additional.append(first.proc_sensitive_close_failure)
            result['ownerReceipt']=receipt
            if receipt is None and 'slot' in locals() and (slot/'runner.log.result.json').is_file():
                try:
                    result['ownerReceipt']=json.loads((slot/'runner.log.result.json').read_bytes())
                    if retirement['receipt'] is None:retirement['receipt']=result['ownerReceipt']
                except BaseException as error:additional.append(failure_record(error))
            if spec['role']=='watch':result['readCounts']=retirement.get('readCounts',{'api':0,'storage':0,'total':0})
            try:
                result['sensitiveInputRetirement']=(first.proc_sensitive_creation if first is not None and hasattr(first,'proc_sensitive_creation')
                    else proc_sensitive_retire(sensitive,retirement['receipt']))
            except BaseException as error:
                additional.append(failure_record(error))
                result['sensitiveInputRetirement']={'disposition':'held-uncertain-retirement'}
                if first is None:first=error
            result['firstCause']=failure_record(first)
            result['commandAccepted']=result['commandAccepted'] and first is None and not additional
            result['reportingOutcome']='written'
            try:persist(slot/'command.json',result,exclusive=spec['role']=='watch')
            except BaseException as error:
                additional.append(failure_record(error));result['reportingOutcome']='failed'
                result['reportDisposition']=getattr(error,'proc_private_report',None)
                print(json.dumps({'schema':RESULT_SCHEMA,'firstCause':failure_record(first or error),
                                  'reportingFailure':failure_record(error),'reportDisposition':result['reportDisposition']}),file=sys.stderr)
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


def preparser_startup_observation(summary, log, endpoint):
    """Observed startup class only; complete current input/retirement checks are separate."""
    try:
        if (summary['schema'] != RESULT_SCHEMA or summary['commandAccepted'] is not False
                or summary['commandExit'] != -signal.SIGTERM
                or summary['firstCause'] != {'type':'TimeoutError','message':'original canonical work deadline'}
                or summary['additionalFailures'] != []): return False
        owner = summary['owner']; life = summary['lifecycle']
        if (owner['firstCause'] != 'TimeoutError: original canonical work deadline'
                or owner['additionalFailures'] != [] or owner['exit'] != -signal.SIGTERM
                or owner['cleanup'] != 'observed-empty-group' or owner['custody'] != 'observed-owned-group'
                or owner['birthIdentityUnavailable'] is not False): return False
        if (f'Starting checker server on port {endpoint}...' not in log
                or f'The Apalache server is running on port {endpoint}.' not in log
                or any(marker in log for marker in ('SanyParser','PASS #','Parsing file','TLC2 Version','[TLC]',
                                                     'states generated','distinct states','initial states','violation found',
                                                     'Invariant','[error]','ERROR','Error:','error:','Exception'))): return False
        births = life['serverBirths']
        if births['omittedMembers'] != 0 or len(births['members']) != 1: return False
        server = births['members'][0]; leader = life['ownerLeader']
        if (any(type(row[k]) is not int or row[k] <= 0 for row in (server,leader) for k in ('pid','start'))
                or type(leader['pgid']) is not int or leader['pgid'] != leader['pid']
                or server['pid'] == leader['pid']): return False
        if life['fdDenials'] != {'entries':[],'omitted':0}: return False
        observations = life['endpointObservations']
        if not observations['entries'] or type(observations['omitted']) is not int or observations['omitted'] < 0: return False
        proof = observations['entries'][-1]
        if proof['event'] != 'poll' or proof['disposition'] != 'proved' or proof['failure'] is not None: return False
        inodes = proof['matchedInodes']
        if (inodes['omitted'] != 0 or not inodes['values']
                or proof['beforeInodes'] != inodes or proof['afterInodes'] != inodes
                or any(type(x) is not str or not x.isascii() or not x.isdigit() or len(x)>20 for x in inodes['values'])): return False
        for name in ('beforeMembers','afterMembers'):
            rows = proof[name]
            if rows['omittedMembers'] != 0 or not any(
                row['pid'] == server['pid'] and row['start'] == server['start']
                and row['pgid'] == leader['pgid'] and row['state'] not in ('Z','X') for row in rows['members']): return False
        scans = proof['fdScans']
        if not any(scan['disposition'] == 'scanned' and scan['missingReadlinks'] == 0
                   and scan['afterMismatchObserved'] is False and scan['afterMismatchBirth'] is None
                   and scan['before']['pid'] == server['pid'] and scan['before']['start'] == server['start']
                   and scan['before']['pgid'] == leader['pgid'] and scan['before']['state'] not in ('Z','X')
                   and scan['socketInodes']['omitted'] == 0
                   and set(inodes['values']) <= set(scan['socketInodes']['values']) for scan in scans): return False
        return True
    except (KeyError,TypeError,ValueError,IndexError,AttributeError): return False


def preparser_startup_retry_eligible(value, log, seconds, endpoint, argv, *, now):
    """Full current receipt joins, never an inference from a retained diagnostic projection."""
    try:
        spec = value['input']; owner = value['ownerReceipt']
        command_preflight(spec, now=spec['started'])
        if (spec['workSeconds'] != seconds or spec['endpoint'] != endpoint
                or not spec['workDeadline'] <= value['cleanupObservedAt'] <= now < spec['totalDeadline']
                or spec['argv'][:-3] != list(argv) or spec['argv'][-2:] != ['--server-endpoint',f'localhost:{endpoint}']
                or not spec['argv'][-3].startswith('--tlc-config=')
                or owner['argv'] != spec['argv'] or type(owner['ownedPopenPid']) is not int
                or owner['ownedPopenPid'] != owner['leader']['pid']
                or owner['leader']['pid'] != owner['leader']['pgid']
                or owner['residual'] != [] or owner['escapedResidual'] != [] or owner['escapedDescendants'] != []
                or value['originalServerStopRequests'] != [] or value['exitMembers'] is not None
                or value['modelCheckingRequired'] is not True): return False
        summary = {key:value[key] for key in ('schema','commandExit','commandAccepted','firstCause','additionalFailures')}
        summary['owner'] = {key:owner[key] for key in ('firstCause','additionalFailures','cleanup','custody','exit','birthIdentityUnavailable')}
        summary['lifecycle'] = lifecycle_summary(value)
        return preparser_startup_observation(summary,log,endpoint)
    except (KeyError,TypeError,ValueError,IndexError,AttributeError): return False


def classify_preparser_startup_retry(seconds, log_path, endpoint, argv):
    """One closed preflight before a retry; missing/stale/malformed evidence refuses."""
    def read(path, cap):
        fd = os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
        try:
            before = os.fstat(fd)
            if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= cap: raise ValueError('retry evidence bound')
            body = bytearray()
            while len(body) <= cap:
                block = os.read(fd,min(65536,cap+1-len(body)))
                if not block: break
                body.extend(block)
            after = os.fstat(fd)
            stable = lambda row:(row.st_dev,row.st_ino,row.st_size,row.st_mtime_ns,row.st_ctime_ns,row.st_mode,row.st_uid,row.st_gid)
            if stable(before) != stable(after) or len(body) != before.st_size: raise ValueError('retry evidence changed')
            return bytes(body)
        finally: os.close(fd)
    def unique(pairs):
        result = {}
        for key,val in pairs:
            if key in result: raise ValueError('duplicate retry receipt key')
            result[key] = val
        return result
    try:
        seconds,endpoint = int(seconds),int(endpoint)
        path = Path(log_path).absolute()
        if path.parent != Path.cwd() or not path.name.endswith('.attempt-1'): return ''
        value = json.loads(read(str(path)+'.command.json',MAX_OUTPUT),object_pairs_hook=unique)
        log = read(path,8*1024**2).decode('utf-8',errors='strict')
        if not preparser_startup_retry_eligible(value,log,seconds,endpoint,argv,now=time.monotonic()): return ''
        spec = value['input']
        def fresh_pin(path):
            canonical = Path(path).resolve(strict=True)
            fd = os.open(canonical,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
            try:
                before = os.fstat(fd)
                if not stat.S_ISREG(before.st_mode): raise ValueError('retry pin type')
                digest=hashlib.sha256();size=0
                while True:
                    if time.monotonic() >= spec['totalDeadline']: raise ValueError('retry pin deadline')
                    block=os.read(fd,65536)
                    if not block:break
                    size+=len(block);digest.update(block)
                    if size>before.st_size:raise ValueError('retry pin growth')
                after=os.fstat(fd)
                if (before.st_dev,before.st_ino,before.st_size,before.st_mtime_ns,before.st_ctime_ns)!=(after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns) or size!=before.st_size:
                    raise ValueError('retry pin changed')
                return {'path':str(canonical),'bytes':size,'sha256':digest.hexdigest()}
            finally:os.close(fd)
        if (json.loads(read(str(path)+'.input.json',64*1024),object_pairs_hook=unique) != spec
                or json.loads(read(str(path)+'.result.json',MAX_OUTPUT),object_pairs_hook=unique) != value['ownerReceipt']): return ''
        expected = [Path(argv[0]),Path(argv[2]),Path(os.environ['JAVA_HOME'])/'bin/java',
                    Path(os.environ['QUINT_HOME'])/'apalache-dist-0.56.1/apalache/lib/apalache.jar',
                    Path(__file__).with_name('shared_process_owner.py'),Path(__file__),Path(str(path)+'.tlc.json')]
        if (spec['argv'][-3] != '--tlc-config='+str(path)+'.tlc.json'
                or spec['mutableScope']['cwd'] != str(path.parent)
                or {row['path'] for row in spec['pins']} != {str(p.resolve(strict=True)) for p in expected}
                or len(spec['pins']) != len(expected)): return ''
        # The timeout path exits before execute_command's normal post-command checks.
        # Repeat every original pin/alias check within that original cleanup/reporting deadline.
        for row in spec['pins']:
            if time.monotonic() >= spec['totalDeadline'] or fresh_pin(row['path']) != row: return ''
        for row in spec['aliases']:
            if time.monotonic() >= spec['totalDeadline'] or os.readlink(row['path']) != row['target']: return ''
        archive = os.environ.get('FSGG_QUINT_TOOLCHAIN_ARCHIVE')
        if (fresh_pin(archive)['sha256'] if archive else None) != spec['archiveSha256']: return ''
        if time.monotonic() >= spec['totalDeadline']: return ''
        return 'owned-startup-timeout-before-parser'
    except (OSError,ValueError,TypeError,KeyError,IndexError,AttributeError,RecursionError): return ''


def execute_command(seconds, log_path, endpoint, argv):
    """Existing canonical adapter with one exact shared owner; no nested supervisor."""
    started = time.monotonic(); first = None; additional = []; receipt = None; returned = None
    output = Path(log_path).absolute(); owner_path = Path(__file__).with_name('shared_process_owner.py')
    spec = None; servers = set(); known = set(); original_exit = 1; exit_members = {'members':None}
    server_handles = {}; stop_requests = []; disk_samples = []; disk_omitted = 0; last_disk = None
    fd_denials = {'entries':[], 'omitted':0}
    endpoint_observations = {'entries':[], 'omitted':0}
    mutable_root = Path.cwd(); original_tmp_identity = None
    def sample_disk(cutoff, force=False):
        nonlocal last_disk, disk_omitted
        now = time.monotonic()
        if spec is not None:
            current_tmp = Path(spec['mutableScope']['tmpdir'])
            require(current_tmp.resolve(strict=True) == current_tmp and current_tmp.is_dir(), 'tmp-scope-retargeted')
        if force or last_disk is None or now - last_disk >= .5:
            observed = census(mutable_root, min(cutoff, now + 1), limit=DISK_LIMIT - 384 * 1024,
                              mutable_tmp=current_tmp,expected_tmp_identity=original_tmp_identity)
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
        tmp_stat=tmpdir.lstat()
        original_tmp_identity=(tmp_stat.st_dev,tmp_stat.st_ino,tmp_stat.st_mode,tmp_stat.st_uid,tmp_stat.st_gid)
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
    if len(sys.argv)==8 and sys.argv[2]=='--proc-measured-controller':
        sys.exit(proc_measured_entry(sys.argv[3],sys.argv[4],sys.argv[5],int(sys.argv[6]),sys.argv[7]))
    if len(sys.argv)==6 and sys.argv[2]=='--proc-kernel-child':
        sys.exit(proc_kernel_child(sys.argv[3],sys.argv[4],sys.argv[5]))
    if len(sys.argv)==7 and sys.argv[2]=='--proc-qualification-worker':
        sys.exit(proc_qualification_worker(sys.argv[3],sys.argv[4],sys.argv[5],sys.argv[6]))
    if len(sys.argv)==6 and sys.argv[2]=='--proc':
        sys.exit(execute_proc_command(sys.argv[3],sys.argv[4],sys.argv[5]))
    if len(sys.argv) < 9 or sys.argv[5] != '--': sys.exit(64)
    sys.exit(execute_command(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[6:]))
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'startup-retry':
    if len(sys.argv) < 9 or sys.argv[5] != '--': sys.exit(64)
    selected = classify_preparser_startup_retry(sys.argv[2],sys.argv[3],sys.argv[4],sys.argv[6:])
    if selected: print(selected)
    sys.exit(0 if selected else 1)
elif __name__ == '__main__':
    try:
        capture(sys.argv[1], sys.argv[2], int(sys.argv[3]),
                max_output=int(sys.argv[4]) if len(sys.argv) == 5 else MAX_OUTPUT)
    except Exception:
        # Never echo errors, paths, input bytes or credentials into public logs.
        sys.exit(2)
