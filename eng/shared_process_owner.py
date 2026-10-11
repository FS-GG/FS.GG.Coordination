"""Pending fixed-0.101 owner successor; original 4e428 remains immutable.

One direct child and one synchronous hook, no second owner/thread. Unexecuted source.
Sampled RSS/disk are stops, not hard containment. Unknown birth never licenses a signal.
"""
import hashlib, json, os, pathlib, signal, subprocess, time

def describe(error):
    try:return type(error).__name__+': '+str(error)
    except BaseException:return type(error).__name__+': <message-unavailable>'

def proc(pid):
    try:
        s=pathlib.Path(f'/proc/{pid}/stat').read_text(); a=s[s.rindex(')')+2:].split()
        return {'pid':pid,'state':a[0],'ppid':int(a[1]),'pgid':int(a[2]),'start':int(a[19]),'rss':int(a[21])*os.sysconf('SC_PAGE_SIZE')}
    except (FileNotFoundError, ProcessLookupError): return None

def census(pgid):
    found=[]
    for p in pathlib.Path('/proc').iterdir():
        if p.name.isdigit():
            row=proc(int(p.name))
            if row and row['pgid']==pgid and row['state']!='Z':found.append(row)
    return found

def observed_escapes(leader, known):
    all_rows=[]
    for p in pathlib.Path('/proc').iterdir():
        if p.name.isdigit():
            row=proc(int(p.name))
            if row:all_rows.append(row)
    by_pid={r['pid']:r for r in all_rows}
    progress=True
    while progress:
        progress=False
        for row in all_rows:
            parent=by_pid.get(row['ppid'])
            if parent and known.get(parent['pid'])==parent['start'] and row['pid'] not in known:
                known[row['pid']]=row['start'];progress=True
    return [r for r in all_rows if known.get(r['pid'])==r['start'] and r['pgid']!=leader['pgid'] and r['state']!='Z']

def run(argv, cwd, env, deadline, cleanup_seconds, rss_limit, out,
        output_cap=10*1024*1024, control=None):
    """Retain a receipt even when a spawned child's birth cannot be authenticated."""
    inherited=os.getpid(); output=pathlib.Path(out); output.parent.mkdir(exist_ok=True)
    leader=None; child=None; known={}; escaped=[]; rows=[]; residual=[]; escape_residual=[]
    primary=None; additional=[]; peak=0; cleanup='unknown'; result=None; birth_unknown=False
    hook=control if control is not None else lambda event, child, leader, known: None
    log=output.open('xb')
    try:
        hook('before-popen',None,None,known)
        if time.monotonic()>=deadline:raise TimeoutError('stage deadline exhausted before child launch')
        child=subprocess.Popen(argv,cwd=cwd,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        # Until the first authenticated PID/start/group read completes, even
        # an exception from proc() leaves this spawned child unqualified.
        birth_unknown=True
        leader=proc(child.pid)
        if leader is None or leader['pgid']!=child.pid:
            raise RuntimeError('spawned child birth identity unavailable')
        birth_unknown=False
        known[child.pid]=leader['start']
        hook('after-birth',child,leader,known)
        while True:
            hook('poll',child,leader,known)
            escaped=observed_escapes(leader,known)
            if escaped:raise RuntimeError('observed descendant escaped owned process group; custody/accounting lost')
            rows=census(child.pid); root=proc(inherited)
            for row in rows:known[row['pid']]=row['start']
            if root is None:raise RuntimeError('supervisor root accounting unknown')
            total=root['rss']+sum(r['rss'] for r in rows);peak=max(peak,total)
            if total>rss_limit:raise RuntimeError('sampled aggregate RSS exceeded')
            if output.stat().st_size>output_cap:raise RuntimeError('output cap exceeded')
            if time.monotonic()>=deadline:raise TimeoutError('original stage deadline exhausted')
            code=child.poll()
            if code is not None:
                rows=census(child.pid)
                if rows:raise RuntimeError('child exited with live owned-group descendants')
                if code:raise RuntimeError('command failed with exit '+str(code))
                break
            time.sleep(.1)
        hook('before-success',child,leader,known)
    except BaseException as error:
        primary=error
    finally:
        # No diagnostics or user hook is allowed to interrupt cleanup.
        end=min(time.monotonic()+cleanup_seconds,deadline+cleanup_seconds)
        try:
            if child is None:
                cleanup='not-started'
            elif birth_unknown or leader is None:
                # A Popen PID is retained for evidence only. No numeric signal is safe.
                child.poll()
                cleanup='unknown-birth-identity'
            else:
                now=proc(child.pid)
                if now and now['start']==leader['start'] and now['pgid']==leader['pgid']:
                    try:os.killpg(child.pid,signal.SIGTERM)
                    except ProcessLookupError:pass
                while census(child.pid) and time.monotonic()<end:
                    child.poll();time.sleep(.1)
                residual=census(child.pid)
                if residual or escaped:
                    for row in residual+escaped:
                        now=proc(row['pid'])
                        if now and now['start']==row['start'] and known.get(now['pid'])==now['start']:
                            try:os.kill(row['pid'],signal.SIGKILL)
                            except ProcessLookupError:pass
                try:child.wait(timeout=max(0,end-time.monotonic()))
                except subprocess.TimeoutExpired:pass
                residual=census(child.pid);escape_residual=observed_escapes(leader,known)
                cleanup='observed-empty-group' if not residual and not escape_residual and child.poll() is not None else 'unknown'
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append(describe(error))
        try:
            hook('after-cleanup',child,leader,known)
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append(describe(error))
        if primary is None and cleanup!='observed-empty-group':
            primary=RuntimeError('cleanup unqualified')
        final_exit=None
        if child is not None:
            try:final_exit=child.poll()
            except BaseException as error:
                if primary is None:primary=error
                else:additional.append('terminal-poll:'+describe(error))
        try:log.close()
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append('output-close:'+describe(error))
        result={'argv':argv,'leader':leader,'ownedPopenPid':child.pid if child else None,
            'birthIdentityUnavailable':birth_unknown,'exit':final_exit,
            'firstCause':describe(primary) if primary else None,
            'additionalFailures':additional,'cleanup':cleanup,'residual':residual,
            'escapedDescendants':escaped,'escapedResidual':escape_residual,
            'custody':'unknown' if birth_unknown else ('no-child' if child is None else
                       ('lost' if escaped else 'observed-owned-group')),
            'sampledPeakAggregateRss':peak,
            'accountingScope':'supervisor plus observed owned process group at100ms; escaped/transient descendants not contained; no hard whole-tree guarantee'}
        try:output.with_suffix(output.suffix+'.result.json').write_text(json.dumps(result,indent=2)+'\n')
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append('receipt-report:'+describe(error))
    if primary is not None:raise primary
    return result
