"""Pending fixed-0.101 owner successor; original 4e428 remains immutable.

One direct child and one synchronous hook, no second owner/thread. Unexecuted source.
Sampled RSS/disk are stops, not hard containment. Unknown birth never licenses a signal.
"""
import hashlib, json, os, pathlib, signal, stat, subprocess, time

def describe(error):
    try:return type(error).__name__+': '+str(error)
    except BaseException:return type(error).__name__+': <message-unavailable>'

def proc(pid, deadline=None):
    try:
        if deadline is not None and time.monotonic()>=deadline:raise TimeoutError('original proc deadline')
        with pathlib.Path(f'/proc/{pid}/stat').open() as stream:s=stream.read(4097)
        if len(s)>4096:raise ValueError('proc stat bound')
        a=s[s.rindex(')')+2:].split()
        return {'pid':pid,'state':a[0],'ppid':int(a[1]),'pgid':int(a[2]),'start':int(a[19]),'rss':int(a[21])*os.sysconf('SC_PAGE_SIZE')}
    except (FileNotFoundError, ProcessLookupError): return None

def census(pgid, deadline=None):
    found=[]
    for index,p in enumerate(pathlib.Path('/proc').iterdir()):
        if deadline is not None and (time.monotonic()>=deadline or index>=4096):raise RuntimeError('bounded census incomplete')
        if p.name.isdigit():
            row=proc(int(p.name)) if deadline is None else proc(int(p.name),deadline)
            if row and row['pgid']==pgid and row['state']!='Z':found.append(row)
    return found

def observed_escapes(leader, known, deadline=None):
    all_rows=[]
    for index,p in enumerate(pathlib.Path('/proc').iterdir()):
        if deadline is not None and (time.monotonic()>=deadline or index>=4096):raise RuntimeError('bounded lineage census incomplete')
        if p.name.isdigit():
            row=proc(int(p.name)) if deadline is None else proc(int(p.name),deadline)
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

def _terminal_pidfd(fd):
    import select
    poll=select.poll();poll.register(fd,select.POLLIN)
    events=poll.poll(0)
    if any(mask & ~(select.POLLIN | select.POLLHUP) or not mask & select.POLLIN for _,mask in events):
        raise RuntimeError('pidfd terminal observation unknown')
    return bool(events)


class DetachedDomain:
    """Synchronous sampled original-birth custody; not a launcher or containment proof.

    One explicitly selected executable may lead a detached group. The caller's
    single owner loop invokes observe/settled/retire; no extra polling thread or
    supervisor is created. Unobserved/transient members remain outside this proof.
    """
    def __init__(self, executable, executable_sha256, retirement_end, *, maximum_members=128,
                 observe=None, group_members=None, acquire=None, terminal=None, send=None,
                 release=None, executable_identity=None, clock=None):
        self.clock=clock or time.monotonic
        if not 0 < retirement_end-self.clock() <= 180 or not 1 <= maximum_members <= 128:
            raise ValueError('finite original detached-domain retirement/member bound')
        self.executable=str(pathlib.Path(executable).resolve());self.executable_sha256=executable_sha256
        self.end=retirement_end;self.maximum_members=maximum_members;self.leader=None
        self.proc=observe or (lambda pid:proc(pid,self.end));self.census=group_members or (lambda group:census(group,self.end))
        self.acquire=acquire or os.pidfd_open;self.terminal=terminal or _terminal_pidfd
        self.send=send or (lambda fd,sig:signal.pidfd_send_signal(fd,sig,None,0))
        self.release=release or os.close;self.executable_identity=executable_identity or self._executable
        self.records={};self.groups={};self.browser=None;self.last_members=None;self.observations=[]

    def checkpoint(self):
        if self.clock() >= self.end:raise TimeoutError('original detached-domain retirement end')

    @staticmethod
    def birth(row):return row['pid'],row['start'],row['pgid']

    def _executable(self,pid):
        # Open the proc executable itself: resolving its pathname can select a
        # replacement inode while the process still executes the unlinked inode.
        self.checkpoint();p=pathlib.Path('/proc')/str(pid)/'exe'
        def identity(stream):
            value=os.fstat(stream.fileno())
            if not stat.S_ISREG(value.st_mode) or not 0<value.st_size<=256*1024*1024:
                raise ValueError('detached executable byte/type cap')
            return value.st_dev,value.st_ino,value.st_size,value.st_mtime_ns,value.st_ctime_ns
        digest=hashlib.sha256();count=0
        with p.open('rb') as running,pathlib.Path(self.executable).open('rb') as selected:
            original=identity(running)
            if identity(selected)!=original:raise ValueError('running executable inode differs from selected input')
            while True:
                self.checkpoint();block=running.read(1024*1024)
                if not block:break
                count+=len(block)
                if count>256*1024*1024 or count>original[2]:raise ValueError('actual executable read cap')
                digest.update(block)
            if count!=original[2] or identity(running)!=original or identity(selected)!=original:
                raise ValueError('executable descriptor changed during hash')
            self.checkpoint()
            with p.open('rb') as current,pathlib.Path(self.executable).open('rb') as current_selected:
                if identity(current)!=original or identity(current_selected)!=original:
                    raise ValueError('executable process/path identity changed after hash')
        return self.executable,digest.hexdigest()

    def _retain(self,row):
        self.checkpoint();key=self.birth(row)
        if key in self.records:return
        if len(self.records)>=self.maximum_members:raise ValueError('detached-domain member cap')
        fd=self.acquire(row['pid']);retained=False
        try:
            fresh=self.proc(row['pid'])
            if fresh is None or self.birth(fresh)!=key:raise ValueError('pidfd original birth drift')
            if self.terminal(fd):raise ValueError('pidfd first observation already terminal')
            self.records[key]={'identity':dict(fresh),'fd':fd,'terminalObserved':False,'descriptorClosed':False};retained=True
        finally:
            if not retained:self.release(fd)

    def bind(self,leader):
        if self.leader is not None:raise ValueError('domain already bound')
        if leader['pid']!=leader['pgid']:raise ValueError('original command group required')
        self._retain(leader);self.leader=dict(leader);self.groups[leader['pgid']]=self.birth(leader)

    def _lineage(self,row):
        # Current complete parent chain must reach an independently held original birth.
        seen=set();candidate=row
        for _ in range(32):
            self.checkpoint();parent=candidate.get('ppid')
            if not isinstance(parent,int) or parent<=1 or parent in seen:break
            seen.add(parent);fresh=self.proc(parent)
            if fresh is None:break
            if self.birth(fresh) in self.records:return
            candidate=fresh
        raise ValueError('detached original parent lineage unknown')

    def observe(self,escapes=()):
        self.checkpoint()
        if self.leader is None:raise ValueError('unbound original command')
        for row in escapes:
            if self.birth(row) in self.records:continue
            if row['pgid'] in self.groups:continue
            if self.browser is not None or row['pid']!=row['pgid']:
                raise ValueError('unqualified detached group')
            self._lineage(row)
            if self.executable_identity(row['pid'])!=(self.executable,self.executable_sha256):
                raise ValueError('detached executable identity differs')
            self._retain(row)
            if self.executable_identity(row['pid'])!=(self.executable,self.executable_sha256):
                raise ValueError('retained executable identity drift')
            self.browser=self.birth(row);self.groups[row['pgid']]=self.browser
        current=[]
        for pgid,root in self.groups.items():
            self.checkpoint();rows=self.census(pgid)
            if len(rows)>self.maximum_members:raise ValueError('detached census member cap')
            for row in rows:
                self.checkpoint()
                if row['pgid']!=pgid:raise ValueError('detached census group drift')
                key=self.birth(row)
                if key not in self.records:
                    if self.terminal(self.records[root]['fd']):raise ValueError('new member after group leader terminal')
                    self._lineage(row);self._retain(row)
                current.append(dict(row))
            if len(current)>self.maximum_members:raise ValueError('combined domain member cap')
        self.last_members=current
        return current

    def owns(self,row):
        self.checkpoint();key=self.birth(row)
        if key not in self.records:return False
        fresh=self.proc(row['pid'])
        return fresh is not None and self.birth(fresh)==key

    def settled(self):
        rows=self.observe()
        for record in self.records.values():record['terminalObserved']=self.terminal(record['fd'])
        return not rows and all(v['terminalObserved'] for v in self.records.values())

    def retire(self,selected):
        # Held original descriptors only. Numeric PID/group signals are never used here.
        self.checkpoint()
        for key,record in self.records.items():
            self.checkpoint()
            if self.terminal(record['fd']):continue
            fresh=self.proc(key[0])
            if fresh is None or self.birth(fresh)!=key:raise ValueError('signal original birth unknown')
            try:self.send(record['fd'],selected)
            except ProcessLookupError:pass

    def close(self):
        # Unknown/live descriptors remain with the caller; no release is implied.
        if not all(self.terminal(v['fd']) for v in self.records.values()):
            raise RuntimeError('original live pidfd custody retained; settlement unknown')
        errors=[]
        for record in self.records.values():
            try:self.release(record['fd']);record['descriptorClosed']=True
            except BaseException as error:errors.append(describe(error))
        if errors:raise RuntimeError('pidfd custody close: '+errors[0])


def run(argv, cwd, env, deadline, cleanup_seconds, rss_limit, out,
        output_cap=10*1024*1024, control=None, observe_child_exit=False,
        retirement_deadline=None, detached_domain=None):
    """Retain birth/custody evidence; opt-in terminal hook precedes fresh refusal census."""
    if retirement_deadline is not None:
        remaining=retirement_deadline-time.monotonic()
        if not 0 < remaining <= 180 or deadline>retirement_deadline-4 or cleanup_seconds<0:
            raise ValueError('strict original absolute retirement bound')
    if detached_domain is not None and not isinstance(detached_domain,DetachedDomain):
        raise ValueError('maintained detached-domain adapter required')
    if detached_domain is not None and (retirement_deadline is None or detached_domain.end!=retirement_deadline):
        raise ValueError('detached custody requires same strict original end')
    inherited=os.getpid(); output=pathlib.Path(out); output.parent.mkdir(exist_ok=True)
    leader=None; child=None; known={}; escaped=[]; rows=[]; residual=[]; escape_residual=[]
    primary=None; additional=[]; peak=0; cleanup='unknown'; result=None; birth_unknown=False
    exit_observation=None
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
        if detached_domain is not None:detached_domain.bind(leader)
        hook('after-birth',child,leader,known)
        while True:
            hook('poll',child,leader,known)
            escaped=observed_escapes(leader,known) if retirement_deadline is None else observed_escapes(leader,known,retirement_deadline)
            if detached_domain is not None:
                detached_domain.observe(escaped)
                escaped=[row for row in escaped if not detached_domain.owns(row)]
            if escaped:raise RuntimeError('observed descendant escaped owned process group; custody/accounting lost')
            rows=census(child.pid) if retirement_deadline is None else census(child.pid,retirement_deadline)
            root=proc(inherited) if retirement_deadline is None else proc(inherited,retirement_deadline)
            for row in rows:known[row['pid']]=row['start']
            if root is None:raise RuntimeError('supervisor root accounting unknown')
            domain_rows=[] if detached_domain is None else detached_domain.last_members or []
            extra=[r for r in domain_rows if r['pgid']!=leader['pgid']]
            total=root['rss']+sum(r['rss'] for r in rows+extra);peak=max(peak,total)
            if total>rss_limit:raise RuntimeError('sampled aggregate RSS exceeded')
            if output.stat().st_size>output_cap:raise RuntimeError('output cap exceeded')
            if time.monotonic()>=deadline:raise TimeoutError('original stage deadline exhausted')
            code=child.poll()
            if code is not None:
                # Existing users retain their event set. The opted-in hook observes
                # terminal status before the owner makes its fresh refusal decision.
                exit_observation={'code':code,'members':None,'omittedMembers':None}
                if observe_child_exit:hook('child-exit-observed',child,leader,known)
                rows=census(child.pid) if retirement_deadline is None else census(child.pid,retirement_deadline)
                exit_observation['members']=[{k:r[k] for k in ('pid','start','pgid','state')} for r in rows[:32]]
                exit_observation['omittedMembers']=max(0,len(rows)-32)
                if rows:raise RuntimeError('child exited with live owned-group descendants')
                if code:raise RuntimeError('command failed with exit '+str(code))
                break
            time.sleep(.1 if retirement_deadline is None else max(0,min(.1,deadline-time.monotonic())))
        if detached_domain is not None and not detached_domain.settled():
            raise RuntimeError('detached domain terminal/membership settlement incomplete')
        hook('before-success',child,leader,known)
    except BaseException as error:
        primary=error
    finally:
        # No diagnostics or user hook is allowed to interrupt cleanup.
        end=retirement_deadline if retirement_deadline is not None else min(time.monotonic()+cleanup_seconds,deadline+cleanup_seconds)
        try:
            if child is None:
                cleanup='not-started'
            elif detached_domain is not None and detached_domain.leader is not None:
                detached_domain.retire(signal.SIGTERM)
                complete=False;kill_at=min(time.monotonic()+2,end)
                while time.monotonic()<kill_at:
                    if detached_domain.settled():complete=True;break
                    child.poll();time.sleep(min(.02,max(0,kill_at-time.monotonic())))
                if not complete and time.monotonic()<end:
                    detached_domain.retire(signal.SIGKILL)
                    while time.monotonic()<end:
                        if detached_domain.settled():complete=True;break
                        child.poll();time.sleep(min(.02,max(0,end-time.monotonic())))
                remaining=end-time.monotonic()
                if remaining>0:
                    try:child.wait(timeout=remaining)
                    except subprocess.TimeoutExpired:pass
                cleanup='observed-empty-domain' if complete and child.poll() is not None else 'unknown'
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
                    child.poll();time.sleep(min(.1,max(0,end-time.monotonic())))
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
        if primary is None and cleanup not in ('observed-empty-group','observed-empty-domain'):
            primary=RuntimeError('cleanup unqualified')
        final_exit=None
        if child is not None:
            try:final_exit=child.poll()
            except BaseException as error:
                if primary is None:primary=error
                else:additional.append('terminal-poll:'+describe(error))
        if detached_domain is not None:
            try:detached_domain.close()
            except BaseException as error:
                if primary is None:primary=error
                else:additional.append('domain-custody-close:'+describe(error))
        try:log.close()
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append('output-close:'+describe(error))
        result={'argv':argv,'leader':leader,'ownedPopenPid':child.pid if child else None,
            'birthIdentityUnavailable':birth_unknown,'exit':final_exit,'exitObservation':exit_observation,
            'firstCause':describe(primary) if primary else None,
            'additionalFailures':additional,'cleanup':cleanup,'residual':residual,
            'escapedDescendants':escaped,'escapedResidual':escape_residual,
            'custody':'unknown' if birth_unknown else ('no-child' if child is None else
                       ('lost' if escaped else 'observed-owned-group')),
            'sampledPeakAggregateRss':peak,
            'absoluteRetirementDeadline':retirement_deadline,
            'detachedDomainEvidence':None if detached_domain is None else {
                'browserBirth':detached_domain.browser,'originalLeader':detached_domain.leader,
                'members':[{'identity':v['identity'],'terminalObserved':v['terminalObserved'],
                            'descriptorClosed':v['descriptorClosed']} for v in detached_domain.records.values()],
                'lastMembers':detached_domain.last_members,
                'scope':'Sampled original handles/membership only; not durable custody or whole-tree containment'},
            'detachedDomainScope':'sampled original-birth/pidfd membership only; no transient/whole-tree containment or nonchild reaping proof' if detached_domain is not None else None,
            'accountingScope':'supervisor plus observed owned process group at100ms; escaped/transient descendants not contained; no hard whole-tree guarantee'}
        try:output.with_suffix(output.suffix+'.result.json').write_text(json.dumps(result,indent=2)+'\n')
        except BaseException as error:
            if primary is None:primary=error
            else:additional.append('receipt-report:'+describe(error))
    if primary is not None:raise primary
    return result
