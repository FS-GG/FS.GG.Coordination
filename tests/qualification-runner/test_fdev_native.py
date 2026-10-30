"""Causal controls for the fdev native-window launcher (M0, .github#4778 / #4650).

No VM, guest, unit, cap, claim or GitHub effect is performed: effects go to a recording
executor over a temporary cgroup tree, and every fail-closed check is shown to refuse
on its own with zero effects before the point of failure.
"""
import copy
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('fdev_native', ROOT / 'deployment/qualification-runner/fdev/fdev_native.py')
fn = importlib.util.module_from_spec(spec); spec.loader.exec_module(fn)
spec2 = importlib.util.spec_from_file_location('dedicated_runner', ROOT / 'eng/dedicated_qualification_runner.py')
dqr = importlib.util.module_from_spec(spec2); spec2.loader.exec_module(dqr)
GOLDEN = ROOT / 'tests/qualification-runner/fdev-golden'

OP = '0123456789abcdef0123456789abcdef'
NOW = dt.datetime(2026, 10, 30, 19, 0, 0, tzinfo=dt.timezone.utc)
LANE = '/user.slice/user-1000.slice/user@1000.service/app.slice/tmux-spawn-lane.scope'
ROOT_SESSION = '/user.slice/user-1000.slice/user@1000.service/app.slice/tmux-spawn-root.scope'
STORE = '/user.slice/user-30033.slice/user@30033.service/app.slice/fsgg-store.service'
LEARN = '/user.slice/user-30034.slice/user@30034.service/app.slice/learn-qualification.service'
DEVICES = {'/srv/v3-pool': '254:16', '/srv/bulk': '254:80', '/srv/sc2-reserved': '254:32',
           '/mnt/storage/containers/learn-qualification/filesystem': '254:0'}
HOME_DF = ('     1B-blocks          Used           Avail    Inodes  IUsed     IFree\n'
           '17927505408000 3437075230720 13590403108864 274661376 610836 274050540\n')


def host_df(measured='2026-10-30T18:24:13Z'):
    return {'schema': 'fsgg.fdev-native-home-df/1', 'measuredAt': measured,
            'source': 'https://github.com/FS-GG/.github/issues/4778#issuecomment-6530390005', 'dfOutput': HOME_DF}


def selection():
    return {'schema': 'fsgg.fdev-native-window-selection/1', 'operationId': OP, 'step': 'bake',
            'rootSelection': {'issue': 'FS-GG/.github#4778', 'commentId': 1},
            'clocks': {'preStartSeconds': 300, 'usefulSeconds': 4800, 'retireSeconds': 600, 'collectorSeconds': 900,
                       'collectorRetireSeconds': 60, 'enclosingSeconds': 8940},
            'ioCaps': {'rbps': 20_000_000, 'wbps': 10_000_000, 'riops': 200, 'wiops': 100},
            'exemptCgroups': [ROOT_SESSION],
            'rootDrainAttestation': 'https://github.com/FS-GG/.github/issues/4778#issuecomment-1',
            'image': {'path': '/srv/v3-pool/native/inputs/noble.img', 'lock': fn.NOBLE_LOCK},
            'readOnlyDisks': [], 'seedDir': None, 'hostPartition': host_df(), 'hostPartitionMaxAgeSeconds': 7200}


def comment():
    return {'author': 'EHotwagner', 'issueUrl': 'https://github.com/FS-GG/.github/issues/4778',
            'body': f'Root selects operation {OP} for the bake step.'}


def facts():
    return {
        'kvm': {'present': True, 'charDevice': True, 'rw': True, 'nested': '1'},
        'cgroup': {'v2': True, 'rootSubtree': ['cpuset', 'cpu', 'io', 'memory', 'pids'], 'cpusEffective': '0-11',
                   'userManagerControllers': ['cpuset', 'cpu', 'io', 'memory', 'pids']},
        'systemdVersion': 262, 'tools': {t: '/usr/bin/' + t for t in fn.POLICY['tools']}, 'sudo': True,
        'memAvailableBytes': 28 * fn.GIB,
        'pool': {'mounted': True, 'label': 'systemadmin-v3', 'uuid': 'u', 'source': '/dev/vdb', 'device': '254:16',
                 'freeBytes': 2000 * fn.GIB, 'freeInodes': 10 ** 8},
        'commitRoot': {'exists': False, 'dir': False, 'mode': None, 'uid': None, 'parentWritable': True},
        'hddDevices': dict(DEVICES),
        'units': {'native': [], 'nativeListingOk': True, 'v3PilotActive': [], 'sc2': [], 'ownScope': None,
                  'selfCgroup': ROOT_SESSION},
        'lanes': {'users': {'developer': 1000, 'fsgg-store-dev': 30033, 'fsgg-learn-qual': 30034},
                  'lanes': [{'cgroup': LANE, 'unit': 'tmux-spawn-lane.scope', 'uid': 1000, 'procs': 1,
                             'comms': ['nu'], 'busy': []}],
                  'exempt': [{'cgroup': ROOT_SESSION, 'unit': 'tmux-spawn-root.scope', 'uid': 1000, 'procs': 2,
                              'comms': ['claude', 'nu'], 'workload': []}],
                  'exemptDeclared': [ROOT_SESSION]},
        'services': [{'role': 'fsgg-store', 'user': 'fsgg-store-dev', 'uid': 30033, 'unit': 'fsgg-store.service',
                      'cgroup': STORE, 'present': True},
                     {'role': 'learn', 'user': 'fsgg-learn-qual', 'uid': 30034, 'unit': 'learn-qualification.service',
                      'cgroup': LEARN, 'present': True}],
        'ioMax': {LANE: [], STORE: [], LEARN: []},
        'ioStat': ['254:80 rbytes=1 wbytes=2'], 'ioPressure': ['some avg10=0.00 avg60=0.00 avg300=0.00 total=1'],
        'poolWriters': {'status': 'none', 'holders': [], 'unreadableProcesses': 0},
        'dotnetRuntimes': ['10.0.11', '10.0.12'], 'claimExists': False,
    }


IMAGE = {'path': '/x', 'expected': 'a' * 64, 'sha256': 'a' * 64, 'match': True}


def rows(f=None, s=None, c=None, d=None, image=None, now=NOW):
    return fn.evaluate(f or facts(), s or selection(), c if c is not None else comment(),
                       d if d is not None else host_df(), now, image or IMAGE)


# One mutation per fail-closed check. Each must flip exactly its own row.
def _set(path, value):
    def apply(f, s, c, d, image):
        target = {'f': f, 's': s, 'c': c, 'd': d, 'i': image}[path[0]]
        for key in path[1:-1]:
            target = target[key]
        target[path[-1]] = value
    return apply


MUTATIONS = {
    'selection.valid': _set(('s', 'schema'), 'other/1'),
    'selection.operation-not-retired': _set(('s', 'operationId'), 'd2f2171d7c5d55aacdc560a09e19b46a'),
    'selection.root-comment-binds-operation': _set(('c', 'body'), 'Root selects another operation.'),
    'claim.absent': _set(('f', 'claimExists'), True),
    'kvm.device': _set(('f', 'kvm', 'rw'), False),
    'kvm.nested': _set(('f', 'kvm', 'nested'), '0'),
    'cgroup.v2-controllers': _set(('f', 'cgroup', 'rootSubtree'), ['cpuset', 'cpu', 'memory', 'pids']),
    'cgroup.cpuset-cpus': _set(('f', 'cgroup', 'cpusEffective'), '0-7'),
    'cgroup.user-io-delegated': _set(('f', 'cgroup', 'userManagerControllers'), ['cpu', 'memory', 'pids']),
    'systemd.version': _set(('f', 'systemdVersion'), 249),
    'tools.present': _set(('f', 'tools', 'qemu-img'), None),
    'privilege.sudo-noninteractive': _set(('f', 'sudo'), False),
    'exclusive.lanes-drained': _set(('f', 'lanes', 'lanes', 0, 'busy'), ['dotnet']),
    'exclusive.exempt-sessions-quiet': _set(('f', 'lanes', 'exempt', 0, 'workload'), ['MSBuild']),
    'exclusive.no-other-native-unit': _set(('f', 'units', 'native'), ['fsgg-native-other-bake.service']),
    'exclusive.no-v3-pilot': _set(('f', 'units', 'v3PilotActive'), ['v3-storage@x.service']),
    'memory.available': _set(('f', 'memAvailableBytes'), 26 * fn.GIB - 1),
    'pool.mounted-identity': _set(('f', 'pool', 'label'), 'fdev-bulk'),
    'pool.free-space': _set(('f', 'pool', 'freeBytes'), 300 * fn.GIB),
    'pool.no-data-writers': _set(('f', 'poolWriters'), {'status': 'found', 'holders': [{'pid': 9, 'comm': 'dd'}]}),
    'commit-root.ready': _set(('f', 'commitRoot'), {'exists': True, 'dir': True, 'mode': 0o755, 'uid': os.getuid(),
                                                     'parentWritable': True}),
    'io.hdd-devices-resolved': _set(('f', 'hddDevices', '/srv/bulk'), None),
    'io.caps-bound': _set(('s', 'ioCaps', 'wbps'), 0),
    'io.no-stale-caps': _set(('f', 'ioMax', STORE), ['254:80 rbps=max wbps=1000 riops=max wiops=max']),
    'services.cap-targets-resolved': _set(('f', 'services', 0, 'uid'), None),
    'capacity.host-partition': _set(('d', 'measuredAt'), '2026-10-29T18:24:13Z'),
    'image.stock-digest': _set(('i', 'match'), False),
    'dotnet.runtime': _set(('f', 'dotnetRuntimes'), ['10.0.11']),
}


class FakeHost:
    """Read-only probes over a temporary cgroup tree plus scripted systemctl output."""

    def __init__(self, cgroot, units=None, self_cgroup=None):
        self.cgroot = Path(cgroot)
        self.units = units or {}
        self.self_cgroup = self_cgroup
        self.mono = 1000.0
        self.clock = NOW

    def read(self, path, maximum=1024 ** 2):
        if str(path) == '/proc/self/cgroup':
            return '0::' + self.self_cgroup + '\n'
        if str(path) == '/proc/sys/kernel/random/boot_id':
            return 'boot\n'
        return Path(path).read_text()

    def run(self, argv, timeout=30):
        if 'show' in argv:
            unit = argv[argv.index('show') + 1]
            props = self.units.get(unit, {'ActiveState': 'inactive', 'SubState': 'dead'})
            return 0, ''.join(f'{k}={v}\n' for k, v in props.items())
        return 1, ''

    def now(self):
        return self.clock

    def monotonic(self):
        return self.mono


class RecordingExecutor(fn.Executor):
    """Records every effect; simulates systemd applying io.max into the fake cgroup tree."""

    def __init__(self, host, fail_effect=None, cap_value=None, refuse_claim=False):
        super().__init__()
        self.host, self.effects, self.fail_effect = host, [], fail_effect
        self.cap_value, self.refuse_claim = cap_value, refuse_claim

    def run(self, argv, effect, timeout=None):
        self.effects.append((effect, argv))
        if effect == self.fail_effect:
            raise fn.Refusal('effect.' + effect, 'simulated failure')
        if effect in ('io.cap-apply', 'io.cap-remove'):
            unit = argv[argv.index('--runtime') + 1]
            for cgdir in self.host.cgroot.rglob(unit):
                lines = {}
                for assignment in argv[argv.index('--runtime') + 2:]:
                    prop, _, value = assignment.partition('=')
                    if not value:
                        lines = {}; break
                    dev, amount = value.removeprefix('/dev/block/').split()
                    key = dict(fn.IO_PROPS)[prop]
                    lines.setdefault(dev, {'rbps': 'max', 'wbps': 'max', 'riops': 'max', 'wiops': 'max'})
                    lines[dev][key] = self.cap_value or amount
                (cgdir / 'io.max').write_text(''.join(
                    f'{dev} rbps={v["rbps"]} wbps={v["wbps"]} riops={v["riops"]} wiops={v["wiops"]}\n'
                    for dev, v in sorted(lines.items())))
        return ''

    def create_claim(self, path, payload):
        self.effects.append(('claim', path))
        if self.refuse_claim:
            raise fn.Refusal('claim.absent', f'{path} exists; an existing claim refuses forever')

    def mkdir(self, path, mode):
        self.effects.append(('mkdir', path))

    def write(self, path, data, mode=0o600):
        self.effects.append(('write', path))

    def kinds(self):
        return [effect for effect, _ in self.effects]


class FakeGitHub:
    def __init__(self, start=NOW, date=NOW + dt.timedelta(seconds=400)):
        self.start, self._date = start, date

    def post_start_witness(self, op, step):
        return self.start

    def date(self):
        return self._date


class PreflightChecks(unittest.TestCase):
    def test_positive_control_all_supported(self):
        result = rows()
        self.assertEqual([r['check'] for r in result if r['status'] != fn.SUPPORTED], [])
        self.assertFalse(fn.shared_codex_host(result))
        self.assertEqual(set(MUTATIONS), {r['check'] for r in result}, 'every check has exactly one causal mutation')

    def test_each_check_refuses_alone(self):
        for check, mutate in MUTATIONS.items():
            with self.subTest(check=check):
                f, s, c, d, image = facts(), selection(), comment(), host_df(), dict(IMAGE)
                mutate(f, s, c, d, image)
                result = fn.evaluate(f, s, c, d, NOW, image)
                bad = [(r['check'], r['status']) for r in result if r['status'] != fn.SUPPORTED]
                if check in ('selection.valid', 'selection.operation-not-retired'):
                    # an invalid selection cannot be bound to a comment, so that row is Unknown by design
                    self.assertEqual(bad, [(check, fn.UNSUPPORTED),
                                           ('selection.root-comment-binds-operation', fn.UNKNOWN)])
                else:
                    self.assertEqual([name for name, _ in bad], [check])

    def test_unknown_is_never_success(self):
        for key in ('memAvailableBytes', 'sudo', 'systemdVersion', 'dotnetRuntimes'):
            with self.subTest(fact=key):
                f = facts(); f[key] = None
                bad = {r['check']: r['status'] for r in rows(f) if r['status'] != fn.SUPPORTED}
                self.assertEqual(set(bad.values()), {fn.UNKNOWN})
                with self.assertRaises(fn.Refusal):
                    fn.refuse_unless_all_supported(rows(f))
        f = facts(); f['poolWriters'] = {'status': 'unknown', 'unreadableProcesses': 3}
        self.assertEqual({r['check']: r['status'] for r in rows(f)}['pool.no-data-writers'], fn.UNKNOWN)

    def test_missing_inputs_report_unknown_not_guessed(self):
        result = {r['check']: r for r in fn.evaluate(facts(), None, None, None, NOW, None)}
        for check in ('selection.valid', 'claim.absent', 'io.caps-bound', 'capacity.host-partition',
                      'image.stock-digest'):
            self.assertEqual(result[check]['status'], fn.UNKNOWN, check)

    def test_retired_short_prefix_refuses(self):
        for op in ('2ae4ffffffffffffffffffffffffffff', '83a08fffffffffffffffffffffffffff'):
            with self.assertRaises(fn.Refusal):
                fn.validate_operation_id(op)

    def test_host_partition_reread_hook(self):
        ok = fn.validate_host_partition(host_df(), NOW, 7200)
        self.assertEqual(ok['avail'], 13_590_403_108_864)
        self.assertEqual(ok['iavail'], 274_050_540)
        for doc, max_age in ((host_df(), 60), (dict(host_df(), dfOutput='size\n1 2 3'), 7200),
                             (dict(host_df(), source='https://example.com'), 7200),
                             (dict(host_df(), measuredAt='2026-10-30T20:00:00Z'), 7200)):
            with self.assertRaises(fn.Refusal):
                fn.validate_host_partition(doc, NOW, max_age)
        tight = host_df(); tight['dfOutput'] = HOME_DF.replace('13590403108864', '1000')
        self.assertEqual({r['check']: r['status'] for r in rows(d=tight)}['capacity.host-partition'], fn.UNSUPPORTED)

    def test_shared_codex_host_true_unless_every_exclusivity_check_passes(self):
        for check in fn.EXCLUSIVITY_CHECKS:
            f = facts(); MUTATIONS[check](f, selection(), comment(), host_df(), dict(IMAGE))
            self.assertTrue(fn.shared_codex_host(rows(f)), check)


class Builders(unittest.TestCase):
    def normalise(self, argv):
        text = json.dumps(argv)
        for old, new in ((sys.executable, '<python>'), (str(Path(fn.__file__).resolve()), '<fdev_native.py>'),
                         (f'--uid={os.getuid()}', '--uid=<uid>'), (f'--gid={os.getgid()}', '--gid=<gid>')):
            text = text.replace(json.dumps(old)[1:-1], new)
        return json.loads(text)

    def test_guest_unit_golden(self):
        s = selection(); s['readOnlyDisks'] = [{'path': '/srv/v3-pool/native/runs/x/inputs.img', 'sha256': 'b' * 64}]
        s['seedDir'] = '/srv/v3-pool/native/runs/x/seed'
        argv = self.normalise(fn.guest_unit_argv(s, f'/srv/v3-pool/native/runs/{OP}', '00000000-0000-4000-8000-000000000000'))
        golden = json.loads((GOLDEN / 'guest-unit.argv.json').read_text())
        self.assertEqual(argv, golden)

    def test_guest_unit_retention_oom_and_no_memory_oom_group(self):
        argv = fn.guest_unit_argv(selection(), '/r', 'u')
        props = [argv[i + 1] for i, a in enumerate(argv) if a == '-p']
        for prop in ('Type=exec', 'ExitType=cgroup', 'RemainAfterExit=yes', 'Restart=no', 'CollectMode=inactive',
                     'OOMPolicy=kill', 'KillMode=control-group', 'RuntimeMaxSec=4800', 'TimeoutStopSec=600',
                     'MemoryMax=19G', 'TasksMax=256'):
            self.assertIn(prop, props)
        self.assertFalse(any('MemoryOOMGroup' in a or 'memory.oom.group' in a for a in argv))
        self.assertIn('--slice=fsgg-native.slice', argv)
        self.assertTrue(any(p.startswith('ExecStop=') and 'qmp-powerdown' in p and '$MAINPID' in p for p in props))
        qemu = argv[argv.index('--') + 1:]
        for flag in ('-nic', 'none', '-mem-prealloc', '-nodefaults', '-uuid'):
            self.assertIn(flag, qemu)
        disk = json.loads(qemu[qemu.index('-blockdev') + 1])
        self.assertEqual((disk['discard'], disk['detect-zeroes'], disk['file']['aio'], disk['file']['cache']['direct']),
                         ('ignore', 'off', 'native', True))

    def test_collector_in_its_own_slice(self):
        argv = fn.collector_unit_argv(selection(), '/r')
        self.assertIn('--slice=fsgg-native-collect.slice', argv)
        self.assertIn('MemoryMax=1G', argv)
        self.assertIn('RuntimeMaxSec=900', argv)

    def test_launcher_scope_is_bounded(self):
        argv = fn.launcher_scope_argv(selection(), ['python3', 'fdev_native.py', 'launch'])
        self.assertEqual(argv[:3], ['systemd-run', '--user', '--scope'])
        props = [argv[i + 1] for i, a in enumerate(argv) if a == '-p']
        self.assertEqual([p.split('=')[0] for p in props], ['RuntimeMaxSec', 'MemoryMax', 'TasksMax'])
        self.assertIn(f'--unit=fsgg-native-{OP}-bake-launcher.scope', argv)

    def test_caps_never_touch_exempt_sessions_slices_or_launcher(self):
        targets = fn.cap_targets(facts())
        self.assertEqual({t['cgroup'] for t in targets}, {LANE, STORE, LEARN})
        f = facts(); f['lanes']['lanes'].append({'cgroup': ROOT_SESSION, 'unit': 'tmux-spawn-root.scope', 'uid': 1000})
        with self.assertRaises(fn.Refusal):
            fn.cap_targets(f)
        argvs = [fn.cap_apply_argv(t, ['254:80'], selection()['ioCaps'], 1000) for t in targets]
        self.assertFalse(any('user-1000.slice' in a for argv in argvs for a in argv))
        by_role = {t['role']: a for t, a in zip(targets, argvs)}
        self.assertEqual(by_role['lane'][:4], ['systemctl', '--user', 'set-property', '--runtime'])
        self.assertEqual(by_role['fsgg-store'][:5], ['sudo', '-n', 'systemctl', '--user', '--machine=fsgg-store-dev@.host'])
        self.assertIn('IOWriteBandwidthMax=/dev/block/254:80 10000000', by_role['learn'])
        self.assertEqual(fn.cap_remove_argv(targets[0], 1000)[-4:], ['IOReadBandwidthMax=', 'IOWriteBandwidthMax=',
                                                                    'IOReadIOPSMax=', 'IOWriteIOPSMax='])

    def test_logind_session_scope_is_capped_through_the_system_manager(self):
        f = facts()
        f['lanes']['lanes'].append({'cgroup': '/user.slice/user-1000.slice/session-10.scope', 'unit': 'session-10.scope',
                                    'uid': 1000, 'manager': 'system', 'busy': []})
        session = [t for t in fn.cap_targets(f) if t['unit'] == 'session-10.scope'][0]
        self.assertEqual(fn.cap_apply_argv(session, ['254:80'], selection()['ioCaps'], 1000)[:5],
                         ['sudo', '-n', 'systemctl', 'set-property', '--runtime'])

    def test_discovered_sc2_unit_is_capped(self):
        f = facts()
        f['services'].append({'role': 'sc2', 'user': None, 'uid': None, 'unit': 'fsgg-sc2-x.service',
                              'cgroup': '/system.slice/fsgg-sc2-x.service', 'present': True, 'manager': 'system'})
        targets = fn.cap_targets(f)
        sc2 = [t for t in targets if t['role'] == 'sc2'][0]
        self.assertEqual(fn.cap_apply_argv(sc2, ['254:32'], selection()['ioCaps'], 1000)[:4],
                         ['sudo', '-n', 'systemctl', 'set-property'])

    def test_target_is_truthful_and_accepted_by_the_shared_validator(self):
        good = fn.build_target(selection(), rows(), 'uuid-1', 'c' * 64)
        self.assertEqual(dqr.validate_target_data(good)['hostKind'], 'fdev-agent-vm-drained-window')
        f = facts(); f['lanes']['lanes'][0]['busy'] = ['dotnet']
        shared = fn.build_target(selection(), rows(f), 'uuid-1', 'c' * 64)
        self.assertTrue(shared['sharedCodexHost'])
        with self.assertRaises(ValueError):
            dqr.validate_target_data(shared)


class Clocks(unittest.TestCase):
    def test_plan_m1_bake_clocks_accepted_and_overcommit_refused(self):
        self.assertEqual(fn.validate_clocks(selection()['clocks'])['enclosingSeconds'], 8940)
        bad = selection()['clocks']; bad['usefulSeconds'] = 8000
        with self.assertRaises(fn.Refusal):
            fn.validate_clocks(bad)
        for value in (0, -1, 1.5, True):
            bad = selection()['clocks']; bad['retireSeconds'] = value
            with self.assertRaises(fn.Refusal):
                fn.validate_clocks(bad)

    def test_pre_start_cut_is_inclusive_then_refuses(self):
        clocks = selection()['clocks']
        self.assertEqual(fn.check_pre_start(100.0, 400.0, clocks), 300.0)
        with self.assertRaises(fn.Refusal):
            fn.check_pre_start(100.0, 400.001, clocks)
        with self.assertRaises(fn.Refusal):
            fn.check_pre_start(100.0, 99.0, clocks)

    def test_github_enclosing_bound_with_reserve(self):
        clocks = selection()['clocks']
        date = fn.parse_http_date('Fri, 30 Oct 2026 19:10:00 GMT')
        start = dt.datetime(2026, 10, 30, 19, 0, 0, tzinfo=dt.timezone.utc)
        self.assertEqual(fn.check_enclosing(start, date, clocks, 8340), 600)
        with self.assertRaises(fn.Refusal):
            fn.check_enclosing(start, date, clocks, 8341)
        with self.assertRaises(fn.Refusal):
            fn.check_enclosing(date, start, clocks)
        with self.assertRaises(fn.Refusal):
            fn.parse_http_date('Fri, 30 Oct 2026 19:10:00 +0100')

    def test_realtime_verdict_after_the_fact(self):
        clocks = selection()['clocks']
        start = NOW
        at_limit = fn.realtime_verdict(start, start + dt.timedelta(seconds=8940 + 300), clocks)
        over = fn.realtime_verdict(start, start + dt.timedelta(seconds=8940 + 301), clocks)
        self.assertTrue(at_limit['acceptableAsQualification'])
        self.assertTrue(over['exceeded'])
        self.assertFalse(over['acceptableAsQualification'])


class Evidence(unittest.TestCase):
    def window(self, **extra):
        base = {'enteredAt': '2026-10-30T19:00:00Z', 'exitedAt': '2026-10-30T21:00:00Z',
                'capsApplied': {'readbackOk': True}, 'capsRemoved': {'readbackOk': True},
                'ioStatBefore': ['a'], 'ioStatAfter': ['b'], 'psiBefore': ['c'], 'psiAfter': ['d']}
        base.update(extra)
        return base

    def home_io(self, **extra):
        doc = {'schema': 'fsgg.fdev-native-home-io-evidence/1', 'observedBy': 'Home',
               'source': 'https://github.com/FS-GG/.github/issues/4778#issuecomment-2',
               'observedFrom': '2026-10-30T18:59:00Z', 'observedTo': '2026-10-30T21:01:00Z',
               'iostat': {'command': 'iostat -dx 10', 'samples': 732, 'rawSha256': 'e' * 64},
               'controls': {'scheduledWriterStopped': True, 'interactiveWorkloadAbsent': True}}
        doc.update(extra)
        return doc

    def test_default_never_claims_independence_or_io_correspondence(self):
        evidence = fn.build_evidence(selection(), rows(), self.window())
        fn.validate_evidence(evidence)
        self.assertEqual(evidence['independence'], {'fromFdev': False, 'fromHome': False})
        self.assertEqual(evidence['ioCorrespondence']['status'], fn.IO_NOT_ESTABLISHED)
        self.assertIn('no Home-observed window evidence supplied', evidence['ioCorrespondence']['reasons'])
        self.assertFalse(evidence['sharedCodexHost'])
        self.assertTrue(evidence['ioSharedWithHostCoTenants'])

    def test_forged_claims_refuse(self):
        base = fn.build_evidence(selection(), rows(), self.window())
        forgeries = {
            'independence': {'fromFdev': True, 'fromHome': False},
            'hostKind': 'dedicated-hardware', 'ioSharedWithHostCoTenants': False, 'tamperProof': True,
            'custody': 'verified', 'sharedCodexHost': True,
            'ioCorrespondence': dict(base['ioCorrespondence'], status='established'),
        }
        for key, value in forgeries.items():
            with self.subTest(field=key):
                doc = copy.deepcopy(base); doc[key] = value
                with self.assertRaises(fn.Refusal):
                    fn.validate_evidence(doc)
        doc = copy.deepcopy(base); doc['ioCorrespondence']['acceptedByRoot'] = True
        with self.assertRaises(fn.Refusal):
            fn.validate_evidence(doc)
        doc = copy.deepcopy(base); doc['ioCorrespondence']['status'] = fn.IO_CAPTURED
        with self.assertRaises(fn.Refusal):
            fn.validate_evidence(doc)

    def test_home_window_evidence_is_required_and_must_cover_the_window(self):
        evidence = fn.build_evidence(selection(), rows(), self.window())
        captured = fn.attach_home_io(evidence, self.home_io())
        self.assertEqual(captured['ioCorrespondence']['status'], fn.IO_CAPTURED)
        self.assertFalse(captured['ioCorrespondence']['acceptedByRoot'])
        for bad in (self.home_io(observedTo='2026-10-30T20:59:59Z'), self.home_io(observedBy='fdev'),
                    self.home_io(controls={'scheduledWriterStopped': False, 'interactiveWorkloadAbsent': True}),
                    self.home_io(iostat={'command': 'psi', 'samples': 1, 'rawSha256': 'e' * 64})):
            self.assertEqual(fn.attach_home_io(evidence, bad)['ioCorrespondence']['status'], fn.IO_NOT_ESTABLISHED)

    def test_fdev_side_capture_gaps_keep_not_established(self):
        for window in (self.window(capsApplied={'readbackOk': False}), self.window(capsRemoved={'readbackOk': False}),
                       self.window(psiAfter=None)):
            evidence = fn.build_evidence(selection(), rows(), window)
            self.assertEqual(fn.attach_home_io(evidence, self.home_io())['ioCorrespondence']['status'],
                             fn.IO_NOT_ESTABLISHED)


class LaunchControls(unittest.TestCase):
    """The launch path with a recording executor. Nothing real is started or changed."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        cg = Path(self.tmp.name)
        for path in (LANE, ROOT_SESSION, STORE, LEARN):
            (cg / path.lstrip('/')).mkdir(parents=True)
            (cg / path.lstrip('/') / 'io.max').write_text('')
        names = fn.unit_names(OP, 'bake')
        self.scope_cg = '/user.slice/user-1000.slice/user@1000.service/app.slice/' + names['launcher']
        units = {names['launcher']: {'ActiveState': 'active', 'RuntimeMaxUSec': '2h', 'MemoryMax': '1073741824',
                                     'TasksMax': '128'},
                 names['guest']: {'ActiveState': 'active', 'SubState': 'exited', 'Result': 'success',
                                  'ExecMainStatus': '0'},
                 names['collector']: {'ActiveState': 'active', 'SubState': 'exited', 'Result': 'success'}}
        self.host = FakeHost(cg, units, self.scope_cg)
        self.lock = mock.patch.object(fn, 'lock_image_sha', return_value=hashlib.sha256(b'image').hexdigest())
        self.digest = mock.patch.object(fn, 'sha256_file', return_value=(hashlib.sha256(b'image').hexdigest(), 5))
        self.lock.start(); self.digest.start()

    def tearDown(self):
        self.lock.stop(); self.digest.stop(); self.tmp.cleanup()

    def launch(self, ex, f=None, r=None, reobserve=None, github=None):
        f = f or facts()
        return fn.launch(self.host, ex, selection(), f, r or rows(f), run_root='/runs', github=github or FakeGitHub(),
                         poll=0, reobserve=reobserve or (lambda: f))

    def test_success_path_order_and_caps_removed(self):
        ex = RecordingExecutor(self.host)
        win = self.launch(ex)
        kinds = ex.kinds()
        self.assertEqual(kinds[0], 'claim')
        start = kinds.index('unit.start-guest')
        self.assertLess(max(i for i, k in enumerate(kinds) if k == 'io.cap-apply'), start)
        self.assertGreater(min(i for i, k in enumerate(kinds) if k == 'io.cap-remove'), kinds.index('unit.start-collector'))
        self.assertEqual(kinds.count('io.cap-apply'), 3)
        self.assertEqual(kinds.count('io.cap-remove'), 3)
        self.assertTrue(win.record['capsApplied']['readbackOk'])
        self.assertTrue(win.record['capsRemoved']['readbackOk'])
        self.assertEqual((Path(self.host.cgroot) / ROOT_SESSION.lstrip('/') / 'io.max').read_text(), '')
        fn.validate_evidence(win.evidence)
        self.assertEqual(win.evidence['ioCorrespondence']['status'], fn.IO_NOT_ESTABLISHED)
        self.assertFalse(win.evidence['sharedCodexHost'])

    def test_every_preflight_refusal_performs_zero_effects(self):
        for check, mutate in MUTATIONS.items():
            with self.subTest(check=check):
                f, s, c, d, image = facts(), selection(), comment(), host_df(), dict(IMAGE)
                mutate(f, s, c, d, image)
                ex = RecordingExecutor(self.host)
                with self.assertRaises(fn.Refusal) as caught:
                    fn.launch(self.host, ex, selection(), f, fn.evaluate(f, s, c, d, NOW, image), run_root='/runs',
                              github=FakeGitHub(), poll=0, reobserve=lambda: f)
                self.assertEqual(caught.exception.check, check)
                self.assertEqual(ex.effects, [])

    def test_unbounded_launcher_scope_refuses_before_claim(self):
        for change in ({'MemoryMax': 'infinity'}, {'RuntimeMaxUSec': 'infinity'}, {'ActiveState': 'inactive'}):
            with self.subTest(change=change):
                name = fn.unit_names(OP, 'bake')['launcher']
                saved = dict(self.host.units[name]); self.host.units[name].update(change)
                ex = RecordingExecutor(self.host)
                with self.assertRaises(fn.Refusal) as caught:
                    self.launch(ex)
                self.assertEqual(caught.exception.check, 'scope.bounded')
                self.assertEqual(ex.effects, [])
                self.host.units[name] = saved
        self.host.self_cgroup = ROOT_SESSION
        ex = RecordingExecutor(self.host)
        with self.assertRaises(fn.Refusal):
            self.launch(ex)
        self.assertEqual(ex.effects, [])

    def test_existing_claim_refuses_forever(self):
        ex = RecordingExecutor(self.host, refuse_claim=True)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'claim.absent')
        self.assertEqual(ex.kinds(), ['claim'])

    def test_window_entry_reread_refuses_after_claim_without_caps(self):
        for key, value, check in (('memAvailableBytes', 20 * fn.GIB, 'memory.available'),
                                  (('pool', 'freeBytes'), 10 * fn.GIB, 'pool.free-space')):
            with self.subTest(check=check):
                later = facts()
                if isinstance(key, tuple):
                    later[key[0]][key[1]] = value
                else:
                    later[key] = value
                ex = RecordingExecutor(self.host)
                with self.assertRaises(fn.Refusal) as caught:
                    self.launch(ex, reobserve=lambda: later)
                self.assertEqual(caught.exception.check, check)
                self.assertEqual(ex.kinds(), ['claim'])

    def test_cap_readback_mismatch_removes_caps_and_starts_nothing(self):
        ex = RecordingExecutor(self.host, cap_value='999')
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'io.cap-readback')
        self.assertNotIn('unit.start-guest', ex.kinds())
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)

    def test_stock_image_digest_mismatch_removes_caps(self):
        with mock.patch.object(fn, 'sha256_file', return_value=('f' * 64, 5)):
            ex = RecordingExecutor(self.host)
            with self.assertRaises(fn.Refusal) as caught:
                self.launch(ex)
        self.assertEqual(caught.exception.check, 'image.stock-digest')
        self.assertNotIn('disk.prepare', ex.kinds())
        self.assertNotIn('unit.start-guest', ex.kinds())
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)

    def test_pre_start_cut_refuses_guest_start(self):
        ex = RecordingExecutor(self.host)
        original = self.host.monotonic
        calls = iter([1000.0, 1301.0, 1301.0, 1301.0])
        self.host.monotonic = lambda: next(calls, 1301.0)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.host.monotonic = original
        self.assertEqual(caught.exception.check, 'clock.pre-start')
        self.assertNotIn('unit.start-guest', ex.kinds())
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)

    def test_enclosing_bound_refuses_guest_start(self):
        ex = RecordingExecutor(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex, github=FakeGitHub(date=NOW + dt.timedelta(seconds=8940 - 6359)))
        self.assertEqual(caught.exception.check, 'clock.enclosing')
        self.assertNotIn('unit.start-guest', ex.kinds())

    def test_failed_guest_start_still_exits_the_window(self):
        ex = RecordingExecutor(self.host, fail_effect='unit.start-guest')
        with self.assertRaises(fn.Refusal):
            self.launch(ex)
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)
        self.assertIn('slice.revert', ex.kinds())

    def test_ungated_plan_only_for_dry_run(self):
        with self.assertRaises(fn.Refusal) as caught:
            fn.launch(self.host, RecordingExecutor(self.host), selection(), facts(), rows(), plan_ungated=True)
        self.assertEqual(caught.exception.check, 'launch.gate')

    def test_window_exit_recovery_removes_recorded_caps(self):
        targets = fn.cap_targets(facts())
        manifest = Path(self.tmp.name) / 'caps-manifest.json'
        manifest.write_text(json.dumps({'operationId': OP, 'devices': ['254:80'], 'targets': targets,
                                        'caps': selection()['ioCaps']}))
        (Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').write_text('254:80 rbps=1 wbps=1 riops=1 wiops=1\n')
        ex = RecordingExecutor(self.host)
        self.assertEqual(fn.window_exit(self.host, ex, manifest), {'removed': 3, 'readbackOk': True})
        stuck = RecordingExecutor(self.host, fail_effect=None)
        stuck.run = lambda argv, effect, timeout=None: ''
        (Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').write_text('254:80 rbps=1 wbps=1 riops=1 wiops=1\n')
        with self.assertRaises(fn.Refusal):
            fn.window_exit(self.host, stuck, manifest)


class DryRun(unittest.TestCase):
    def test_dry_run_touches_nothing_and_lists_the_plan(self):
        host = FakeHost(tempfile.gettempdir(), {}, ROOT_SESSION)
        f = facts(); f['memAvailableBytes'] = 1
        boom = mock.Mock(side_effect=AssertionError('dry-run performed an effect'))
        with mock.patch.object(subprocess, 'run', boom), mock.patch.object(os, 'mkdir', boom), \
                mock.patch.object(os, 'makedirs', boom), mock.patch.object(fn, 'sha256_file', boom):
            result = fn.dry_run(host, selection(), f, rows(f))
        self.assertFalse(result['changesMade'])
        self.assertEqual([r['check'] for r in result['wouldRefuse']], ['memory.available'])
        kinds = [step['effect'] for step in result['plan']]
        for kind in ('claim', 'io.cap-apply', 'slice.properties', 'disk.prepare', 'unit.start-guest',
                     'unit.start-collector', 'io.cap-remove', 'slice.revert'):
            self.assertIn(kind, kinds)
        self.assertEqual(result['evidencePreview']['ioCorrespondence']['status'], fn.IO_NOT_ESTABLISHED)


class LiveProbes(unittest.TestCase):
    def test_observe_is_read_only_and_complete(self):
        boom = mock.Mock(side_effect=AssertionError('observe wrote'))
        host = fn.LiveHost()
        with mock.patch.object(os, 'mkdir', boom), mock.patch.object(os, 'makedirs', boom):
            observed = fn.observe(host, selection())
        result = fn.evaluate(observed, selection(), None, None, host.now(), None)
        self.assertEqual({r['check'] for r in result}, set(MUTATIONS))
        self.assertIn(observed['poolWriters']['status'], ('none', 'found', 'unknown'))

    def test_census_finds_a_holder(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'held'
            path.write_text('x')
            with open(path) as held:
                child = subprocess.Popen(['sleep', '30'], stdin=held, cwd='/')
            try:
                found = fn.open_file_census(directory)
            finally:
                child.kill(); child.wait()
        self.assertEqual(found['status'], 'found')
        self.assertIn(child.pid, [h['pid'] for h in found['holders']])

    def test_qmp_powerdown_sends_system_powerdown(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'qmp.sock')
            server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); server.bind(path); server.listen(1)
            received = []

            def serve():
                conn, _ = server.accept()
                stream = conn.makefile('rwb')
                stream.write(b'{"QMP": {}}\n'); stream.flush()
                for _ in range(2):
                    received.append(json.loads(stream.readline())['execute'])
                    stream.write(b'{"return": {}}\n'); stream.flush()
                conn.close()
            thread = threading.Thread(target=serve); thread.start()
            self.assertEqual(fn.qmp_powerdown(path, None, timeout=5), 0)
            thread.join(5); server.close()
        self.assertEqual(received, ['qmp_capabilities', 'system_powerdown'])


if __name__ == '__main__':
    unittest.main(verbosity=1)
