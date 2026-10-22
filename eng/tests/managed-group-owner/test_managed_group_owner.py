"""Focused controls for eng/managed_group_owner.py (fsgg.managed-group-contract/1).

Refusal controls run everywhere. Containment controls need a delegated cgroup v2
root (see run-ci.sh); FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT=1 turns their absence
into a failure instead of a skip. FSGG_MANAGED_GROUP_OWNER selects the owner bytes
under test (the mutation proof points it at mutants).
"""
import hashlib, importlib.util, json, os, pathlib, shutil, signal, subprocess, sys, tempfile, time, unittest

HERE = pathlib.Path(__file__).resolve().parent
OWNER_PATH = os.environ.get('FSGG_MANAGED_GROUP_OWNER') or str(HERE.parents[1] / 'managed_group_owner.py')
spec = importlib.util.spec_from_file_location('managed_group_owner', OWNER_PATH)
mg = importlib.util.module_from_spec(spec); spec.loader.exec_module(mg)
REQUIRE = os.environ.get('FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT') == '1'
REQUIRE_BCL = os.environ.get('FSGG_MANAGED_GROUP_REQUIRE_BCL') == '1'
PYTHON = os.path.realpath(sys.executable)
PYTHON_SHA = hashlib.sha256(pathlib.Path(PYTHON).read_bytes()).hexdigest()
ENV = {'LANG': 'C.UTF-8', 'PATH': '/usr/bin:/bin'}
PROBE = mg.probe()

GRANDCHILD = r'''
import os, signal, sys, time
pidfile = sys.argv[1]
if os.fork() == 0:
    os.setsid()
    if os.fork() == 0:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        with open(pidfile + '.tmp', 'w') as sink: sink.write(str(os.getpid()))
        os.rename(pidfile + '.tmp', pidfile)
        time.sleep(300)
    os._exit(0)
os.wait()
while not os.path.exists(pidfile): time.sleep(0.01)
print('leader-done', flush=True)
'''
ESCAPE = r'''
import os, signal, sys, time
target, pidfile = sys.argv[1], sys.argv[2]
hop = target.startswith('hop-')
place = target[4:] if hop else target
if os.fork() == 0:
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    relative = [l for l in open('/proc/self/cgroup').read().splitlines() if l.startswith('0::')][0][3:]
    droot = os.path.dirname('/sys/fs/cgroup' + relative)
    destination = {'sibling': os.path.join(droot, 'escape-sibling'), 'supervisor': os.path.join(droot, 'fsgg-supervisor'),
                   'nested': os.path.join(droot, 'fsgg-supervisor', 'escape-nested')}[place]
    if place != 'supervisor': os.makedirs(destination, exist_ok=True)
    with open(os.path.join(destination, 'cgroup.procs'), 'w') as sink: sink.write('0')
    with open(pidfile + '.tmp', 'w') as sink: sink.write(str(os.getpid()))
    os.rename(pidfile + '.tmp', pidfile)
    while hop:  # fork-hop: the recorded pid is always stale, only cgroup retirement can stop this
        time.sleep(0.01)
        if os.fork(): os._exit(0)
    time.sleep(300)
time.sleep(300)
'''
CHATTY = r'''
import os, signal, sys, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
os.fork()
while True:
    sys.stdout.write('x' * 512); sys.stdout.flush(); time.sleep(0.01)
'''
STUBBORN = r'''
import os, signal, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
if os.fork() == 0:
    time.sleep(300)
time.sleep(300)
'''
FORKS = r'''
import os, time
made = failed = 0
for _ in range(8):
    try:
        if os.fork() == 0:
            time.sleep(2); os._exit(0)
        made += 1
    except BlockingIOError:
        failed += 1
print(f'made={made} failed={failed}', flush=True)
for _ in range(made): os.wait()
'''
BCL = r'''
open System.Diagnostics
let start = ProcessStartInfo("/bin/sh", UseShellExecute = false)
start.ArgumentList.Add "-c"
start.ArgumentList.Add "setsid sleep 300 </dev/null >/dev/null 2>&1 &"
using (Process.Start start) (fun child -> child.WaitForExit())
printfn "bcl-started"
'''


def alive(pid):
    try:
        text = pathlib.Path(f'/proc/{pid}/stat').read_text()
    except FileNotFoundError:
        return False
    return text[text.rindex(')') + 2] != 'Z'


def gone_within(pid, seconds=3.0):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if not alive(pid):
            return True
        time.sleep(0.02)
    return not alive(pid)


class Base(unittest.TestCase):
    def setUp(self):
        base = os.environ.get('TMPDIR') or tempfile.gettempdir()
        self.root = os.path.realpath(tempfile.mkdtemp(prefix='mg-', dir=base))
        self.addCleanup(shutil.rmtree, self.root, True)
        self.count = 0

    def out(self):
        self.count += 1
        return os.path.join(self.root, f'out-{self.count}')

    def profile(self, code='pass', args=(), **changes):
        # argv admits no control characters, so multi-line programs travel as files.
        program = ['-c', code]
        if '\n' in code:
            path = os.path.join(self.root, f'program-{len(os.listdir(self.root))}.py')
            pathlib.Path(path).write_text(code); program = [path]
        value = {'schema': mg.PROFILE_SCHEMA, 'name': 'control', 'root': self.root,
                 'executable': {'path': PYTHON, 'sha256': PYTHON_SHA}, 'leaderImages': [PYTHON_SHA],
                 'argv': [PYTHON, '-I', '-S', *program, *args], 'cwd': self.root,
                 'environmentKeys': sorted(ENV), 'containment': 'cgroup2-delegated',
                 'limits': {'pidsMax': None, 'memoryMaxBytes': None, 'stdoutBytes': 1 << 20, 'stderrBytes': 1 << 20},
                 'retirement': {'onLeaderExit': 'require-empty', 'leaderExitSettleSeconds': 0.5,
                                'termGraceSeconds': 0.5, 'cleanupSeconds': 5}}
        for key, item in changes.items():
            if isinstance(item, dict) and isinstance(value.get(key), dict) and key != 'executable':
                value[key] = {**value[key], **item}
            else:
                value[key] = item
        return value

    def run_group(self, profile, seconds=20, env=ENV, **kwargs):
        return mg.run(profile, env=dict(env), deadline=time.monotonic() + seconds, out_dir=self.out(), **kwargs)

    def failed(self, profile, seconds=20, **kwargs):
        with self.assertRaises(mg.GroupFailed) as caught:
            self.run_group(profile, seconds, **kwargs)
        receipt = caught.exception.receipt
        on_disk = json.loads(pathlib.Path(self.root, f'out-{self.count}', 'receipt.json').read_text())
        self.assertEqual(on_disk['firstCause'], receipt['firstCause'])
        self.assertEqual(receipt['outcome'], 'failed')
        return receipt

    def refused(self, code, profile, marker=None, seconds=20, **kwargs):
        with self.assertRaises(mg.Refused) as caught:
            self.run_group(profile, seconds, **kwargs)
        self.assertEqual(caught.exception.code, code)
        if marker is not None:
            time.sleep(0.2)
            self.assertFalse(os.path.exists(marker), 'refusal must precede any launch')
        return caught.exception


class RefusalControls(Base):
    """Run everywhere: refusals happen before any process is launched."""

    def marker_profile(self, **changes):
        marker = os.path.join(self.root, 'launched')
        return marker, self.profile(f'open({marker!r}, "w").close()', **changes)

    def test_profile_is_closed_and_bounded(self):
        good = self.profile()
        self.assertEqual(mg.validate_profile(good)[1], hashlib.sha256(mg.canonical(good)).hexdigest())
        cases = [{**good, 'extra': 1}, {**good, 'schema': 'other/1'}, {**good, 'name': 'Bad Name'},
                 {**good, 'root': 'relative'}, {**good, 'root': '/'}, {**good, 'cwd': '/elsewhere'},
                 {**good, 'containment': 'process-group'}, {**good, 'argv': []}, {**good, 'argv': ['a\nb']},
                 {**good, 'leaderImages': []}, {**good, 'leaderImages': ['A' * 64]},
                 {**good, 'executable': {'path': PYTHON, 'sha256': 'x'}}, {**good, 'environmentKeys': ['PATH', 'LANG']},
                 {**good, 'limits': {**good['limits'], 'pidsMax': True}},
                 {**good, 'limits': {**good['limits'], 'memoryMaxBytes': 1}},
                 {**good, 'retirement': {**good['retirement'], 'onLeaderExit': 'ignore'}},
                 {**good, 'retirement': {**good['retirement'], 'termGraceSeconds': 5}},
                 {**good, 'retirement': {**good['retirement'], 'cleanupSeconds': 0}}]
        for case in cases:
            with self.subTest(case=case), self.assertRaises(mg.Refused) as caught:
                mg.validate_profile(case)
            self.assertEqual(caught.exception.code, 'profile-invalid')

    def test_executable_identity_mismatch_refuses_before_launch(self):
        marker, profile = self.marker_profile(executable={'path': PYTHON, 'sha256': '0' * 64})
        refusal = self.refused('executable-identity-mismatch', profile, marker)
        self.assertEqual(refusal.receipt['outcome'], 'refused')
        self.assertFalse(refusal.receipt['launch']['attempted'])
        on_disk = json.loads(pathlib.Path(self.root, 'out-1', 'receipt.json').read_text())
        self.assertEqual(on_disk['refusal'], 'executable-identity-mismatch')

    def test_symlinked_executable_refuses(self):
        link = os.path.join(self.root, 'python-link'); os.symlink(PYTHON, link)
        marker, profile = self.marker_profile(executable={'path': link, 'sha256': PYTHON_SHA})
        self.refused('executable-invalid', profile, marker)

    def test_containment_unavailable_refuses_before_launch(self):
        for root in (self.root, '/sys/fs/cgroup', '/nonexistent/cgroup'):
            with self.subTest(root=root):
                marker, profile = self.marker_profile()
                expected = mg.probe(root)
                self.assertFalse(expected['available'])
                refusal = self.refused('containment-unavailable', profile, marker, delegated_root=root)
                self.assertEqual(refusal.detail, expected['refusal'])
                self.assertIsNone(refusal.receipt['containment']['leaf'])

    def test_deadline_exhausted_refuses(self):
        marker, profile = self.marker_profile()
        self.refused('deadline-exhausted', profile, marker, seconds=-1)

    def test_environment_mismatch_refuses(self):
        marker, profile = self.marker_profile()
        self.refused('environment-mismatch', profile, marker, env={**ENV, 'EXTRA': '1'})

    def test_active_alarm_refuses(self):
        marker, profile = self.marker_profile()
        previous = signal.signal(signal.SIGALRM, lambda *_: None)
        signal.setitimer(signal.ITIMER_REAL, 30)
        try:
            self.refused('owner-alarm-active', profile, marker)
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0); signal.signal(signal.SIGALRM, previous)

    def test_signal_requires_recorded_birth_and_cgroup(self):
        mount = mg._cgroup2_mount()
        if mount is None:
            self.skipTest('no cgroup2 mount')
        child = subprocess.Popen([PYTHON, '-I', '-S', '-c', 'import time; time.sleep(30)'])
        try:
            start = mg._stat(child.pid)['start']
            self.assertEqual(mg._pidfd_signal(child.pid, start + 1, signal.SIGTERM, lambda d: True, mount), 'identity-changed')
            self.assertEqual(mg._pidfd_signal(child.pid, start, signal.SIGTERM, lambda d: False, mount), 'scope-changed')
            time.sleep(0.2)
            self.assertIsNone(child.poll(), 'an unverified identity or scope must never be signalled')
            self.assertEqual(mg._pidfd_signal(child.pid, start, signal.SIGTERM, lambda d: True, mount), 'sent')
            self.assertEqual(child.wait(5), -signal.SIGTERM)
        finally:
            child.kill(); child.wait()

    def test_out_dir_must_be_fresh_and_inside_root(self):
        profile = self.profile()
        for out in ('/tmp/outside-root-mg', self.root):
            with self.subTest(out=out), self.assertRaises(mg.Refused) as caught:
                mg.run(profile, env=dict(ENV), deadline=time.monotonic() + 5, out_dir=out)
            self.assertEqual(caught.exception.code, 'out-dir-invalid')


@unittest.skipUnless(PROBE['available'] or REQUIRE, 'delegated cgroup v2 root unavailable: ' + str(PROBE['refusal']))
class ContainmentControls(Base):
    """Real kernel containment. Each control leaves the delegated root exclusive again."""

    @classmethod
    def setUpClass(cls):
        if not PROBE['available']:
            raise AssertionError('containment required but unavailable: ' + str(PROBE['refusal']))

    def assert_clean(self, receipt):
        self.assertEqual(receipt['cleanup'], 'observed-empty-removed', receipt['firstCause'])
        droot = receipt['containment']['delegatedRoot']
        self.assertFalse(os.path.exists(os.path.join(droot, receipt['containment']['leaf'])))
        self.assertEqual([n for n in sorted(os.listdir(droot)) if os.path.isdir(os.path.join(droot, n))], [mg.SUPERVISOR])
        self.assertEqual(sorted(mg._dirs(droot)), [droot, os.path.join(droot, mg.SUPERVISOR)])
        self.assertEqual(set(mg._tree(droot)), {os.getpid()}, 'the delegated root must hold only the owner')
        self.assertEqual(receipt['residualInRoot'], [])

    def test_success_binds_birth_and_contains_leader(self):
        if REQUIRE:  # otherwise the no_new_privs assertion below cannot be attributed to the owner
            owner_nnp = [l.split(':')[1].strip() for l in pathlib.Path('/proc/self/status').read_text().splitlines() if l.startswith('NoNewPrivs:')]
            self.assertEqual(owner_nnp, ['0'], 'containment controls must run without inherited no_new_privs')
        receipt = self.run_group(self.profile('import sys,time; print("hello"); print("warn", file=sys.stderr); time.sleep(0.3)'))
        self.assertEqual(receipt['outcome'], 'succeeded')
        self.assertEqual(receipt['custody'], 'contained')
        leader = receipt['leader']
        self.assertEqual(leader['image'], 'admitted'); self.assertEqual(leader['imageSha256'], PYTHON_SHA)
        self.assertEqual(leader['noNewPrivileges'], '1')
        self.assertTrue(leader['cgroup'].endswith('/' + receipt['containment']['leaf']))
        self.assertEqual(receipt['exit'], {'code': 0, 'signal': None, 'atSeconds': receipt['exit']['atSeconds']})
        self.assertEqual(pathlib.Path(self.root, 'out-1', 'stdout.log').read_bytes(), b'hello\n')
        self.assertEqual(pathlib.Path(self.root, 'out-1', 'stderr.log').read_bytes(), b'warn\n')
        self.assertTrue(receipt['streams']['stdout']['eof'])
        self.assertFalse(receipt['retirement']['cgroupKill'])
        self.assert_clean(receipt)

    def test_session_escape_stays_contained_and_require_empty_refuses(self):
        pidfile = os.path.join(self.root, 'grandchild')
        receipt = self.failed(self.profile(GRANDCHILD, [pidfile]))
        self.assertIn('leader exited with live contained members', receipt['firstCause'])
        grandchild = int(pathlib.Path(pidfile).read_text())
        row = [r for r in receipt['residualAfterLeaderExit'] if r['pid'] == grandchild]
        self.assertEqual(len(row), 1); self.assertNotEqual(row[0]['sid'], receipt['leader']['pid'])
        self.assertTrue(receipt['retirement']['cgroupKill'])
        self.assertTrue(gone_within(grandchild))
        self.assertEqual(receipt['custody'], 'contained')
        self.assert_clean(receipt)

    def test_session_escape_retire_members_policy_retires(self):
        pidfile = os.path.join(self.root, 'grandchild')
        receipt = self.run_group(self.profile(GRANDCHILD, [pidfile], retirement={'onLeaderExit': 'retire-members'}))
        grandchild = int(pathlib.Path(pidfile).read_text())
        self.assertIn(grandchild, [r['pid'] for r in receipt['residualAfterLeaderExit']])
        self.assertTrue(receipt['retirement']['cgroupKill'])
        self.assertTrue(gone_within(grandchild))
        self.assert_clean(receipt)

    def test_cgroup_escape_is_refused_and_retired(self):
        expected = {'sibling': {'escape-sibling'}, 'supervisor': None, 'nested': {'fsgg-supervisor/escape-nested'},
                    'hop-sibling': {'escape-sibling'}, 'hop-supervisor': None, 'hop-nested': {'fsgg-supervisor/escape-nested'}}
        for target, created in expected.items():
            with self.subTest(target=target):
                pidfile = os.path.join(self.root, 'escaped-' + target)
                profile = self.profile(ESCAPE, [target, pidfile])
                receipt = self.failed(profile, seconds=8)
                self.assertIn('contained member escaped managed group', receipt['firstCause'])
                self.assertEqual(receipt['custody'], 'escaped-retired')
                names = {c['cgroup'] for c in receipt['createdCgroups']}
                self.assertTrue(all(c['removed'] for c in receipt['createdCgroups']))
                if created is None:
                    self.assertGreaterEqual(receipt['retirement']['quarantined'], 1)
                    self.assertTrue(all(n.startswith(mg.QUARANTINE_PREFIX) for n in names))
                else:
                    self.assertEqual(names, created)
                escaped = int(pathlib.Path(pidfile).read_text())
                self.assertTrue(gone_within(escaped))
                program = profile['argv'][3]
                survivors = [p for p in os.listdir('/proc') if p.isdigit() and program.encode() in self.cmdline(p)]
                self.assertEqual(survivors, [])
                self.assert_clean(receipt)

    @staticmethod
    def cmdline(pid):
        try:
            return pathlib.Path(f'/proc/{pid}/cmdline').read_bytes()
        except OSError:
            return b''

    def test_residue_left_in_root_is_reported_not_hidden(self):
        state = {}

        def hook(event, context):
            if event == 'before-remove':
                state['helper'] = subprocess.Popen([PYTHON, '-I', '-S', '-c', 'import time; time.sleep(60)'])
        try:
            receipt = self.failed(self.profile('pass'), control=hook)
            self.assertEqual(receipt['cleanup'], 'unknown')
            self.assertIn('cleanup unqualified: unknown', receipt['firstCause'])
            self.assertEqual(receipt['custody'], 'unknown')
            self.assertIn(state['helper'].pid, [r['pid'] for r in receipt['residualInRoot']])
        finally:
            if 'helper' in state:
                state['helper'].kill(); state['helper'].wait()

    def test_output_sink_failure_during_retirement_still_kills(self):
        def hook(event, context):
            if event == 'before-retire':
                target = os.path.join(context['outDir'], 'stdout.log')
                for fd in os.listdir('/proc/self/fd'):
                    try:
                        link = os.readlink(f'/proc/self/fd/{fd}')
                    except OSError:
                        continue
                    if link == target:  # keep the descriptor number occupied, but make every write fail
                        readonly = os.open('/dev/null', os.O_RDONLY); os.dup2(readonly, int(fd)); os.close(readonly)
        receipt = self.failed(self.profile(CHATTY, retirement={'termGraceSeconds': 1.0}), seconds=1.5, control=hook)
        self.assertIn('original deadline exhausted', receipt['firstCause'])
        self.assertIsNotNone(receipt['streams']['stdout']['sinkError'])
        self.assertTrue(any('sink failed' in f for f in receipt['additionalFailures']))
        self.assertTrue(receipt['retirement']['cgroupKill'])
        self.assert_clean(receipt)

    def test_early_exit_failure_is_reported_and_group_clean(self):
        receipt = self.failed(self.profile('raise SystemExit(3)'))
        self.assertIn('command failed with exit 3', receipt['firstCause'])
        self.assertEqual(receipt['exit']['code'], 3)
        self.assert_clean(receipt)

    def test_early_exit_success_does_not_claim_unobserved_image(self):
        receipt = self.run_group(self.profile('pass'))
        self.assertIn(receipt['leader']['image'], ('admitted', 'unobserved-leader-exited'))
        self.assert_clean(receipt)

    def test_deadline_kills_stubborn_group(self):
        began = time.monotonic()
        receipt = self.failed(self.profile(STUBBORN, retirement={'termGraceSeconds': 0.3}), seconds=1.5)
        self.assertIn('original deadline exhausted', receipt['firstCause'])
        self.assertTrue(receipt['retirement']['cgroupKill'])
        self.assertGreaterEqual(receipt['retirement']['termSignals'], 2)
        self.assertLess(time.monotonic() - began, 1.5 + 5 + 1)
        for row in receipt['members']['sample']:
            self.assertTrue(gone_within(row['pid']))
        self.assert_clean(receipt)

    def test_cleanup_failure_is_reported_not_hidden(self):
        state = {}

        def hook(event, context):
            if event == 'before-remove':
                state['leaf'] = context['leaf']
                state['helper'] = subprocess.Popen([PYTHON, '-I', '-S', '-c', 'import time; time.sleep(60)'])
                with open(os.path.join(context['leaf'], 'cgroup.procs'), 'w') as sink:
                    sink.write(str(state['helper'].pid))
        try:
            receipt = self.failed(self.profile('pass'), control=hook)
            self.assertEqual(receipt['cleanup'], 'observed-empty-remove-failed')
            self.assertIn('cleanup unqualified: observed-empty-remove-failed', receipt['firstCause'])
            self.assertIsNotNone(receipt['retirement']['removeError'])
            self.assertFalse(receipt['retirement']['leafRemoved'])
        finally:
            if 'helper' in state:
                state['helper'].kill(); state['helper'].wait()
                end = time.monotonic() + 5
                while time.monotonic() < end:
                    try:
                        os.rmdir(state['leaf']); break
                    except OSError:
                        time.sleep(0.05)

    def test_cleanup_hook_failure_does_not_replace_first_cause(self):
        def hook(event, context):
            if event == 'before-retire':
                raise ValueError('injected hook failure')
        receipt = self.failed(self.profile('raise SystemExit(4)'), control=hook)
        self.assertIn('command failed with exit 4', receipt['firstCause'])
        self.assertTrue(any('injected hook failure' in f for f in receipt['additionalFailures']))
        self.assert_clean(receipt)

    def test_output_cap_is_enforced(self):
        receipt = self.failed(self.profile('import sys,time; sys.stdout.write("x"*10000); sys.stdout.flush(); time.sleep(30)',
                                           limits={'stdoutBytes': 100}))
        self.assertIn('stdout byte cap exceeded', receipt['firstCause'])
        self.assertEqual(receipt['streams']['stdout'], {**receipt['streams']['stdout'], 'bytes': 100, 'capped': True})
        self.assert_clean(receipt)

    def test_leader_image_outside_profile_is_refused(self):
        receipt = self.failed(self.profile('import time; time.sleep(30)', leaderImages=['0' * 64]))
        self.assertIn('leader image not-admitted', receipt['firstCause'])
        self.assertEqual(receipt['leader']['imageSha256'], PYTHON_SHA)
        self.assert_clean(receipt)

    def test_non_exclusive_root_refuses_before_launch(self):
        helper = subprocess.Popen([PYTHON, '-I', '-S', '-c', 'import time; time.sleep(60)'])
        try:
            marker = os.path.join(self.root, 'launched')
            refusal = self.refused('delegated-root-not-exclusive', self.profile(f'open({marker!r}, "w").close()'), marker)
            self.assertIn(str(helper.pid), refusal.detail)
        finally:
            helper.kill(); helper.wait()

    @unittest.skipUnless('pids' in PROBE['controllers'] or REQUIRE, 'pids controller unavailable')
    def test_pids_limit_is_enforced(self):
        receipt = self.run_group(self.profile(FORKS, limits={'pidsMax': 3}))
        self.assertEqual(receipt['containment']['limitsApplied']['pids.max'], '3')
        made, failed = [int(p.split('=')[1]) for p in pathlib.Path(self.root, 'out-1', 'stdout.log').read_text().split()]
        self.assertLessEqual(made, 2); self.assertGreaterEqual(failed, 6)
        self.assert_clean(receipt)

    @unittest.skipUnless(shutil.which('dotnet') or REQUIRE_BCL, 'dotnet unavailable')
    def test_bcl_launched_session_descendants_are_covered(self):
        dotnet = os.path.realpath(shutil.which('dotnet'))
        digest = hashlib.sha256(pathlib.Path(dotnet).read_bytes()).hexdigest()
        script = os.path.join(self.root, 'bcl.fsx'); pathlib.Path(script).write_text(BCL)
        home = os.path.join(self.root, 'home'); os.mkdir(home)
        env = {'DOTNET_CLI_HOME': home, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1', 'DOTNET_ROOT': os.path.dirname(dotnet),
               'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'HOME': home, 'PATH': os.path.dirname(dotnet) + ':/usr/bin:/bin'}
        profile = self.profile(executable={'path': dotnet, 'sha256': digest}, leaderImages=[digest],
                               argv=[dotnet, 'fsi', '--quiet', script], environmentKeys=sorted(env),
                               retirement={'leaderExitSettleSeconds': 1.0})
        receipt = self.failed(profile, seconds=180, env=env)
        self.assertIn('leader exited with live contained members', receipt['firstCause'])
        self.assertIn(b'bcl-started', pathlib.Path(self.root, 'out-1', 'stdout.log').read_bytes())
        detached = [r for r in receipt['residualAfterLeaderExit'] if r['sid'] != receipt['leader']['pid']]
        self.assertTrue(detached)
        for row in detached:
            self.assertTrue(gone_within(row['pid']))
        self.assert_clean(receipt)


if __name__ == '__main__':
    unittest.main(verbosity=2)
