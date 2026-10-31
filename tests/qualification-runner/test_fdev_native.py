"""Causal controls for the fdev native-window launcher (M0, .github#4778 / #4650).

No VM, guest, unit, cap, claim or GitHub effect is performed: effects go to a recording
executor over a temporary cgroup tree, and every fail-closed check is shown to refuse
on its own with zero effects before the point of failure.
"""
import contextlib
import copy
import datetime as dt
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
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


def host_memory(measured='2026-10-30T18:30:00Z', avail=12 * 1024 ** 3):
    return {'schema': 'fsgg.fdev-native-home-memory/1', 'measuredAt': measured,
            'source': 'https://github.com/FS-GG/.github/issues/4778#issuecomment-3', 'memTotalBytes': 66_500_000_000,
            'memAvailableBytes': avail}


def selection():
    return {'schema': 'fsgg.fdev-native-window-selection/1', 'operationId': OP, 'step': 'bake',
            'rootSelection': {'issue': 'FS-GG/.github#4778', 'commentId': 1},
            'clocks': {'preStartSeconds': 300, 'usefulSeconds': 4800, 'retireSeconds': 600, 'collectorSeconds': 900,
                       'collectorRetireSeconds': 60, 'enclosingSeconds': 8940},
            'ioCaps': {'rbps': 20_000_000, 'wbps': 10_000_000, 'riops': 200, 'wiops': 100},
            'exemptCgroups': [ROOT_SESSION],
            'rootDrainAttestation': 'https://github.com/FS-GG/.github/issues/4778#issuecomment-1',
            'image': {'path': '/srv/v3-pool/native/inputs/noble.img', 'lock': fn.NOBLE_LOCK},
            'readOnlyDisks': [], 'seedDir': None, 'hostPartition': host_df(), 'hostPartitionMaxAgeSeconds': 7200,
            'hostMemory': host_memory(), 'hostMemoryMinAvailableBytes': 8 * 1024 ** 3, 'hostMemoryMaxAgeSeconds': 7200}


def comment():
    return {'author': 'EHotwagner', 'issueUrl': 'https://github.com/FS-GG/.github/issues/4778',
            'body': f'Root selects operation {OP} for the bake step.\n'
                    f'<!-- fsgg:fdev-native-selection/v1 operation={OP} step=bake -->'}


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


def rows(f=None, s=None, c=None, d=None, image=None, now=NOW, m=None):
    s = s or selection()
    return fn.evaluate(f or facts(), s, c if c is not None else comment(),
                       d if d is not None else host_df(), now, image or IMAGE, m if m is not None else s['hostMemory'])


# One mutation per fail-closed check. Each must flip exactly its own row.
def _set(path, value):
    def apply(f, s, c, d, image):
        target = {'f': f, 's': s, 'c': c, 'd': d, 'i': image, 'm': s['hostMemory']}[path[0]]
        for key in path[1:-1]:
            target = target[key]
        target[path[-1]] = value
    return apply


MUTATIONS = {
    'selection.valid': _set(('s', 'schema'), 'other/1'),
    'selection.operation-not-retired': _set(('s', 'operationId'), 'd2f2171d7c5d55aacdc560a09e19b46a'),
    'selection.root-comment-binds-operation': _set(('c', 'body'), f'Root selects operation {OP} without the marker.'),
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
    'capacity.host-memory': _set(('m', 'memAvailableBytes'), 8 * 1024 ** 3 - 1),
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
        self.written = {}
        self.timeouts = []

    def run(self, argv, effect, timeout=None):
        self.effects.append((effect, argv))
        self.timeouts.append((effect, timeout))
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
        if self.refuse_claim == 'partial':
            partial = fn.Refusal('claim.partial', f'{path} was created but its payload write failed')
            partial.claim_created = True
            raise partial
        if self.refuse_claim:
            raise fn.Refusal('claim.absent', f'{path} exists; an existing claim refuses forever')

    def mkdir(self, path, mode):
        self.effects.append(('mkdir', path))

    def write(self, path, data, mode=0o600):
        self.effects.append(('write', path))
        self.written[path] = data

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
                result = fn.evaluate(f, s, c, d, NOW, image, s['hostMemory'])
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

    def test_retired_full_identity_refuses_as_retired(self):
        for op in fn.POLICY['retiredOperations']:
            with self.assertRaises(fn.Refusal) as caught:
                fn.validate_operation_id(op)
            self.assertIn('retired identity', caught.exception.reason)

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

    def test_host_memory_absent_or_unbound_is_unknown_and_stale_or_low_refuses(self):
        def status(**kw):
            return {r['check']: r for r in fn.evaluate(facts(), kw.get('s', selection()), comment(), host_df(), NOW, IMAGE,
                                                        kw.get('m'), kw.get('floor'), kw.get('age'))}['capacity.host-memory']
        self.assertEqual(status(m=None)['status'], fn.UNKNOWN)
        self.assertEqual(status(s=None, m=host_memory())['status'], fn.UNKNOWN, 'no Root floor bound')
        self.assertEqual(status(s=None, m=host_memory(), floor=8 * fn.GIB, age=7200)['status'], fn.SUPPORTED)
        self.assertEqual(status(m=host_memory(measured='2026-10-30T10:00:00Z'))['status'], fn.UNSUPPORTED)
        self.assertEqual(status(m=dict(host_memory(), memAvailableBytes=10 ** 12))['status'], fn.UNSUPPORTED)
        with self.assertRaises(fn.Refusal):
            fn.refuse_unless_all_supported(fn.evaluate(facts(), selection(), comment(), host_df(), NOW, IMAGE, None))
        unbound = selection(); del unbound['hostMemoryMinAvailableBytes']
        with self.assertRaises(fn.Refusal):
            fn.validate_selection(unbound)
        absent = selection()
        for key in ('hostMemory', 'hostMemoryMinAvailableBytes', 'hostMemoryMaxAgeSeconds'):
            del absent[key]
        fn.validate_selection(absent)  # optional input: absence is valid and reports Unknown

    def test_launchers_own_witness_comment_cannot_bind_a_selection(self):
        body = (f'fdev native claim: operation `{OP}` step `bake` claimed. Start witness. '
                'Cooperative custody; not independent of fdev or Home.')
        witness = {'author': 'EHotwagner', 'issueUrl': 'https://github.com/FS-GG/.github/issues/4778', 'body': body}
        self.assertEqual(fn.check_selection(selection(), witness)[-1]['status'], fn.UNSUPPORTED)
        other_step = dict(comment(), body=comment()['body'].replace('step=bake', 'step=acquire'))
        self.assertEqual(fn.check_selection(selection(), other_step)[-1]['status'], fn.UNSUPPORTED)
        self.assertEqual(fn.check_selection(selection(), comment())[-1]['status'], fn.SUPPORTED)
        for field, value in (('author', 'someone-else'), ('issueUrl', 'https://github.com/FS-GG/.github/issues/4779')):
            self.assertEqual(fn.check_selection(selection(), dict(comment(), **{field: value}))[-1]['status'],
                             fn.UNSUPPORTED, field)

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
        self.assertEqual([p.split('=')[0] for p in props], ['RuntimeMaxSec', 'TimeoutStopSec', 'MemoryMax', 'TasksMax'])
        clocks = selection()['clocks']
        self.assertIn(f'TimeoutStopSec={fn.scope_stop_seconds(clocks)}', props)
        self.assertGreater(fn.scope_stop_seconds(clocks), fn.exit_bound(clocks, fn.POLICY['maxCapTargets']))
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
                'ioStatBefore': ['a'], 'ioStatAfter': ['b'], 'psiBefore': ['c'], 'psiAfter': ['d'],
                'exitExclusivity': fn.check_exclusivity(facts()), 'newUncappedUnitsDuringWindow': []}
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

    def test_missing_exit_exclusivity_alone_makes_the_host_shared(self):
        window = self.window(); del window['exitExclusivity']
        self.assertEqual(window['newUncappedUnitsDuringWindow'], [])
        self.assertTrue(fn.window_shared(rows(), window))
        self.assertFalse(fn.window_shared(rows(), self.window()))
        busy = facts(); busy['lanes']['lanes'][0]['busy'] = ['dotnet']
        self.assertTrue(fn.window_shared(rows(), self.window(exitExclusivity=fn.check_exclusivity(busy))))

    def test_window_without_entry_is_not_established_not_a_crash(self):
        evidence = fn.build_evidence(selection(), rows(), {'capsApplied': {'readbackOk': True}})
        attached = fn.attach_home_io(evidence, self.home_io())
        self.assertEqual(attached['ioCorrespondence']['status'], fn.IO_NOT_ESTABLISHED)

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
        units = {names['launcher']: {'ActiveState': 'active', 'RuntimeMaxUSec': '1d', 'MemoryMax': '1073741824',
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
                    fn.launch(self.host, ex, selection(), f, fn.evaluate(f, s, c, d, NOW, image, s['hostMemory']), run_root='/runs',
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

    def test_partial_claim_leaves_a_durable_record_and_nothing_else(self):
        ex = RecordingExecutor(self.host, refuse_claim='partial')
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'claim.partial')
        self.assertEqual(ex.kinds(), ['claim', 'write'])
        self.assertEqual(caught.exception.window.record_path, fn.record_path(OP))
        record = json.loads(ex.written[fn.record_path(OP)])
        self.assertEqual(record['window']['outcome'], 'refused-claim-partial')
        self.assertEqual(record['window']['firstFailure']['check'], 'claim.partial')
        fn.validate_evidence(record)

    def test_signal_during_the_partial_claim_record_write_is_recorded_not_lost(self):
        ex = RecordingExecutor(self.host, refuse_claim='partial')
        original = ex.write

        def write(path, data, mode=0o600):
            os.kill(os.getpid(), signal.SIGTERM); time.sleep(0.2)  # lands while the record is written
            return original(path, data, mode)
        ex.write = write
        previous = fn.install_signal_exceptions()
        try:
            with self.assertRaises(fn.Refusal) as caught:
                self.launch(ex)
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
        self.assertEqual(caught.exception.check, 'claim.partial')
        self.assertIn(fn.record_path(OP), ex.written, 'the record survives the signal')
        self.assertNotIn('recordWriteError', caught.exception.window.record)
        self.assertEqual(caught.exception.window.record['signalsDuringRecord'], [int(signal.SIGTERM)])
        self.assertTrue(fn.SIGNALS['raise'], 're-armed after the claim attempt')

    def test_main_reports_a_record_write_error(self):
        refusal = fn.Refusal('claim.partial', 'payload write failed')
        window = mock.Mock(); window.record_path = None
        window.record = {'firstFailure': {'check': 'claim.partial'}, 'recordWriteError': "OSError('read-only')"}
        refusal.window = window
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        err = io.StringIO()
        saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
        try:
            with mock.patch.object(fn, 'observe', return_value=facts()), \
                    mock.patch.object(fn, 'evaluate', return_value=rows()), \
                    mock.patch.object(fn, 'launch', side_effect=refusal), contextlib.redirect_stderr(err):
                code = fn.main(['launch', '--selection', str(path), '--no-github'])
        finally:
            for signum, handler in saved.items():
                signal.signal(signum, handler)
        self.assertEqual(code, 3)
        report = json.loads(err.getvalue())
        self.assertEqual(report['recordWriteError'], "OSError('read-only')")
        self.assertIsNone(report['windowRecord'])

    def test_exit_and_post_return_signals_are_counted_once(self):
        term, hup = int(signal.SIGTERM), int(signal.SIGHUP)
        self.assertEqual(fn.recorded_signals([term], [term, hup]), [term, hup])
        self.assertEqual(fn.recorded_signals([term], [term]), [term])
        self.assertEqual(fn.recorded_signals([], [hup]), [hup])
        self.assertEqual(fn.recorded_signals([term], []), [term])
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        win = mock.Mock(); win.evidence = {'schema': 'x'}; win.record_path = fn.record_path(OP)
        win.record = {'signalsDuringExit': [term], 'outcome': 'completed-awaiting-readback'}

        def fake_launch(*args, **kwargs):
            fn.SIGNALS['pending'].extend([term, hup])  # the exit snapshot, then one more after return
            return win
        err = io.StringIO()
        saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
        try:
            with mock.patch.object(fn, 'observe', return_value=facts()), \
                    mock.patch.object(fn, 'evaluate', return_value=rows()), \
                    mock.patch.object(fn, 'launch', side_effect=fake_launch), contextlib.redirect_stderr(err), \
                    contextlib.redirect_stdout(io.StringIO()):
                code = fn.main(['launch', '--selection', str(path), '--no-github'])
        finally:
            for signum, handler in saved.items():
                signal.signal(signum, handler)
        self.assertEqual(code, 128 + term)
        self.assertEqual(json.loads(err.getvalue())['signalsDuringExit'], ['SIGTERM', 'SIGHUP'])

    def test_clean_claim_refusal_writes_no_record(self):
        ex = RecordingExecutor(self.host, refuse_claim=True)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'claim.absent')
        self.assertEqual(ex.written, {})
        self.assertIsNone(getattr(caught.exception, 'window', None))

    def test_main_exits_non_zero_when_signals_arrived_during_exit(self):
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        for recorded, expected in (([int(signal.SIGTERM)], 128 + int(signal.SIGTERM)), ([], 0)):
            with self.subTest(recorded=recorded):
                win = mock.Mock(); win.evidence = {'schema': 'x'}; win.record_path = fn.record_path(OP)
                win.record = {'signalsDuringExit': recorded, 'outcome': 'completed-awaiting-readback'}
                err = io.StringIO()
                saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
                try:
                    with mock.patch.object(fn, 'observe', return_value=facts()), \
                            mock.patch.object(fn, 'evaluate', return_value=rows()), \
                            mock.patch.object(fn, 'launch', return_value=win), contextlib.redirect_stderr(err), \
                            contextlib.redirect_stdout(io.StringIO()):
                        code = fn.main(['launch', '--selection', str(path), '--no-github'])
                finally:
                    for signum, handler in saved.items():
                        signal.signal(signum, handler)
                self.assertEqual(code, expected)
                if recorded:
                    report = json.loads(err.getvalue())
                    self.assertEqual(report['signalsDuringExit'], ['SIGTERM'])
                    self.assertEqual(report['windowRecord'], fn.record_path(OP))

    def test_main_exits_non_zero_when_a_signal_is_recorded_after_launch_returns(self):
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        win = mock.Mock(); win.evidence = {'schema': 'x'}; win.record_path = fn.record_path(OP)
        win.record = {'signalsDuringExit': [], 'outcome': 'completed-awaiting-readback'}

        def fake_launch(*args, **kwargs):
            fn.SIGNALS['pending'].append(int(signal.SIGHUP))  # disarmed after exit: recorded, not raised
            return win
        saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
        try:
            with mock.patch.object(fn, 'observe', return_value=facts()), \
                    mock.patch.object(fn, 'evaluate', return_value=rows()), \
                    mock.patch.object(fn, 'launch', side_effect=fake_launch), \
                    contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
                code = fn.main(['launch', '--selection', str(path), '--no-github'])
        finally:
            for signum, handler in saved.items():
                signal.signal(signum, handler)
        self.assertEqual(code, 128 + int(signal.SIGHUP))

    def test_window_entry_reread_refuses_after_claim_without_caps(self):
        def busy_lane(later):
            later['lanes']['lanes'][0]['busy'] = ['dotnet']

        def memory(later):
            later['memAvailableBytes'] = 20 * fn.GIB

        def pool(later):
            later['pool']['freeBytes'] = 10 * fn.GIB

        def native_unit(later):
            later['units']['native'] = ['fsgg-native-other-bake.service']
        for change, check in ((memory, 'memory.available'), (pool, 'pool.free-space'),
                              (busy_lane, 'exclusive.lanes-drained'), (native_unit, 'exclusive.no-other-native-unit')):
            with self.subTest(check=check):
                later = facts(); change(later)
                ex = RecordingExecutor(self.host)
                with self.assertRaises(fn.Refusal) as caught:
                    self.launch(ex, reobserve=lambda: later)
                self.assertEqual(caught.exception.check, check)
                self.assertEqual(ex.kinds(), ['claim', 'write'], 'no cap, slice, disk or unit effect after the claim')

    def test_refusal_after_claim_writes_a_durable_record_outside_run_dir(self):
        later = facts(); later['memAvailableBytes'] = 20 * fn.GIB
        ex = RecordingExecutor(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex, reobserve=lambda: later)
        path = fn.record_path(OP)
        self.assertTrue(path.startswith(fn.POLICY['claimsDir'] + '/'))
        self.assertFalse(path.startswith('/runs'))
        record = json.loads(ex.written[path])
        self.assertEqual(record['window']['firstFailure']['check'], 'memory.available')
        self.assertEqual(record['window']['outcome'], 'refused')
        self.assertEqual(caught.exception.window.record_path, path)
        fn.validate_evidence(record)

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
        for when in ('during-preparation', 'after-preparation'):
            with self.subTest(when=when):
                self.host.mono = 1000.0
                ex = RecordingExecutor(self.host)
                original = ex.run

                def run(argv, effect, timeout=None, when=when):
                    result = original(argv, effect, timeout)
                    prepared = ex.kinds().count('disk.prepare')
                    if effect == 'disk.prepare' and (prepared == 1 if when == 'during-preparation' else prepared == 2):
                        self.host.mono = 1301.0
                    return result
                ex.run = run
                with self.assertRaises(fn.Refusal) as caught:
                    self.launch(ex)
                self.assertEqual(caught.exception.check, 'clock.pre-start')
                self.assertEqual(ex.kinds().count('disk.prepare'), 1 if when == 'during-preparation' else 2)
                self.assertNotIn('unit.start-guest', ex.kinds())
                self.assertEqual(ex.kinds().count('io.cap-remove'), 3)
        self.host.mono = 1000.0

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

    def manifest(self):
        targets = fn.cap_targets(facts())
        path = Path(self.tmp.name) / 'caps-manifest.json'
        path.write_text(json.dumps({'operationId': OP, 'devices': ['254:80'], 'targets': targets,
                                    'caps': selection()['ioCaps']}))
        (Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').write_text('254:80 rbps=1 wbps=1 riops=1 wiops=1\n')
        return path

    def test_window_exit_recovery_removes_recorded_caps_and_reverts_slices(self):
        ex = RecordingExecutor(self.host)
        result = fn.window_exit(self.host, ex, self.manifest())
        self.assertTrue(result['readbackOk'])
        self.assertEqual([t['result'] for t in result['targets']], ['removed'] * 3)
        self.assertEqual(ex.kinds().count('slice.revert'), 2)
        self.assertEqual((Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').read_text(), '')

    def test_window_exit_attempts_every_target_past_a_vanished_unit(self):
        manifest = self.manifest()

        class Vanished(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if 'tmux-spawn-lane.scope' in argv:
                    self.effects.append((effect, argv))
                    raise fn.Refusal('effect.' + effect, 'Failed to set unit properties: Unit tmux-spawn-lane.scope not found.')
                return super().run(argv, effect, timeout)
        ex = Vanished(self.host)
        result = fn.window_exit(self.host, ex, manifest)
        attempted = [a[a.index('--runtime') + 1] for e, a in ex.effects if e == 'io.cap-remove']
        self.assertEqual(attempted, ['tmux-spawn-lane.scope', 'fsgg-store.service', 'learn-qualification.service'])
        self.assertEqual(result['targets'][0]['result'], 'already-gone')
        self.assertEqual(result['errors'], [])
        self.assertEqual(ex.kinds().count('slice.revert'), 2)
        self.assertEqual((Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').read_text(), '')

    def test_window_exit_records_other_errors_and_refuses_only_on_a_present_cap(self):
        manifest = self.manifest()

        class Flaky(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if effect == 'io.cap-remove' and 'learn-qualification.service' in argv:
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.' + effect, 'dbus timeout')
                if effect == 'slice.revert':
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.' + effect, 'access denied')
                return super().run(argv, effect, timeout)
        result = fn.window_exit(self.host, Flaky(self.host), manifest)  # learn cgroup has no cap: readback passes
        self.assertEqual([e['unit'] for e in result['errors']], ['learn-qualification.service'])
        self.assertEqual(len(result['sliceRevertErrors']), 2)

        class StoreStuck(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if effect == 'io.cap-remove' and 'fsgg-store.service' in argv:
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.' + effect, 'dbus timeout')
                return super().run(argv, effect, timeout)
        (Path(self.host.cgroot) / STORE.lstrip('/') / 'io.max').write_text('254:80 rbps=1 wbps=1 riops=1 wiops=1\n')
        stuck = StoreStuck(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            fn.window_exit(self.host, stuck, manifest)
        self.assertEqual(caught.exception.check, 'io.cap-remove-readback')
        self.assertEqual(stuck.kinds().count('io.cap-remove'), 3, 'later targets still attempted')
        self.assertEqual(stuck.kinds().count('slice.revert'), 2)

    def test_failed_cap_removal_makes_launch_refuse_with_a_window_exit_directive(self):
        class RemoveFails(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if effect == 'io.cap-remove' and 'fsgg-store.service' in argv:
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.io.cap-remove', 'dbus timeout')
                return super().run(argv, effect, timeout)
        ex = RemoveFails(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'io.cap-remove-pending')
        self.assertIn('window-exit --manifest /runs/' + OP + '/caps-manifest.json', caught.exception.reason)
        record = json.loads(ex.written[fn.record_path(OP)])
        self.assertIn('window-exit', record['window']['windowExitDirective'])
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)

    def test_removal_error_on_a_stopped_unit_is_still_pending(self):
        shutil.rmtree(Path(self.host.cgroot) / LEARN.lstrip('/'))  # not running: its runtime drop-in remains

        class LearnFails(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if effect == 'io.cap-remove' and 'learn-qualification.service' in argv:
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.io.cap-remove', 'dbus timeout')
                return super().run(argv, effect, timeout)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(LearnFails(self.host))
        self.assertEqual(caught.exception.check, 'io.cap-remove-pending')

    def test_main_exits_non_zero_and_prints_the_directive(self):
        refusal = fn.Refusal('io.cap-remove-pending', 'caps may remain; run: window-exit --manifest /runs/x')
        window = mock.Mock(); window.record = {'windowExitDirective': 'window-exit --manifest /runs/x',
                                               'firstFailure': None}
        window.record_path = fn.record_path(OP); refusal.window = window
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        err = io.StringIO()
        with mock.patch.object(fn, 'observe', return_value=facts()), \
                mock.patch.object(fn, 'evaluate', return_value=rows()), \
                mock.patch.object(fn, 'launch', side_effect=refusal), \
                mock.patch.object(fn, 'install_signal_exceptions'), contextlib.redirect_stderr(err):
            code = fn.main(['launch', '--selection', str(path), '--no-github'])
        self.assertNotEqual(code, 0)
        report = json.loads(err.getvalue())
        self.assertEqual(report['windowExitDirective'], 'window-exit --manifest /runs/x')
        self.assertEqual(report['windowRecord'], fn.record_path(OP))

    def test_signal_during_the_window_still_exits_and_records(self):
        ex = RecordingExecutor(self.host)
        original = ex.run

        def run(argv, effect, timeout=None):
            if effect == 'unit.start-guest':
                os.kill(os.getpid(), signal.SIGTERM)
            return original(argv, effect, timeout)
        ex.run = run
        previous = fn.install_signal_exceptions()
        try:
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)
        self.assertEqual(ex.kinds().count('slice.revert'), 2)
        record = json.loads(ex.written[fn.record_path(OP)])
        self.assertEqual(record['window']['firstFailure']['check'], 'launch.signal')

    def test_signals_raise_interrupted_for_term_int_and_hup(self):
        previous = fn.install_signal_exceptions()
        try:
            for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
                fn.SIGNALS['raise'] = True  # one-shot: re-arm for each signal
                with self.subTest(signal=signum), self.assertRaises(fn.Interrupted):
                    os.kill(os.getpid(), signum)
                    time.sleep(1)
            fn.SIGNALS['raise'] = True
            with self.assertRaises(fn.Interrupted):
                os.kill(os.getpid(), signal.SIGTERM); time.sleep(1)
            os.kill(os.getpid(), signal.SIGTERM); time.sleep(0.2)  # disarmed by the first: recorded only
            self.assertEqual(fn.SIGNALS['pending'], [int(signal.SIGTERM)])
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)

    def test_exit_exclusivity_and_new_lane_units_make_the_host_shared(self):
        cases = {}

        def busy(later):
            later['lanes']['lanes'][0]['busy'] = ['dotnet']

        def new_unit(later):
            later['lanes']['lanes'].append({'cgroup': LANE + '-new', 'unit': 'new.scope', 'uid': 1000, 'busy': []})

        def other_native(later):
            later['units']['native'] = ['fsgg-native-other-x.service']
        for name, change in (('busy', busy), ('new-unit', new_unit), ('other-native', other_native)):
            f = facts(); later = copy.deepcopy(f); change(later); calls = []

            def reobserve(f=f, later=later, calls=calls):
                calls.append(1)
                return f if len(calls) == 1 else later
            ex = RecordingExecutor(self.host)
            win = self.launch(ex, f=f, reobserve=reobserve)
            cases[name] = win.evidence['sharedCodexHost']
            fn.validate_evidence(win.evidence)
            forged = copy.deepcopy(win.evidence); forged['sharedCodexHost'] = False
            with self.assertRaises(fn.Refusal):
                fn.validate_evidence(forged)
        self.assertEqual(cases, {'busy': True, 'new-unit': True, 'other-native': True})
        f = facts(); post = copy.deepcopy(f); names = fn.unit_names(OP, 'bake')
        post['units']['native'] = [names['guest'], names['collector']]  # retained by CollectMode=inactive
        calls = []
        win = self.launch(RecordingExecutor(self.host), f=f,
                          reobserve=lambda: (calls.append(1), f if len(calls) == 1 else post)[1])
        self.assertFalse(win.evidence['sharedCodexHost'], 'own retained units do not count as other native units')

    def test_scope_runtime_must_exceed_the_abnormal_path_bound(self):
        clocks = selection()['clocks']
        bound = fn.internal_bound(clocks, 3)
        guest_path = clocks['usefulSeconds'] + clocks['retireSeconds'] + 60 + clocks['retireSeconds']
        collector_path = clocks['collectorSeconds'] + 2 * clocks['collectorRetireSeconds'] + 60
        self.assertGreater(bound, clocks['preStartSeconds'] + guest_path + collector_path)
        self.assertGreater(fn.scope_runtime_seconds(clocks), fn.internal_bound(clocks, fn.POLICY['maxCapTargets']))
        # abnormal path: each retire clock counts three times (the observation bound, the bounded stop on
        # the bound, and the bounded stop on window exit); each cap target costs one entry and one exit effect
        for key, weight in (('preStartSeconds', 1), ('usefulSeconds', 1), ('retireSeconds', 3), ('collectorSeconds', 1),
                            ('collectorRetireSeconds', 3)):
            bigger = dict(clocks); bigger[key] += 1
            self.assertEqual(fn.internal_bound(bigger, 3) - bound, weight, key)
        self.assertEqual(fn.internal_bound(clocks, 4) - bound, 3 * fn.EFFECT_TIMEOUT)
        argv = fn.launcher_scope_argv(selection(), ['x'])
        self.assertIn(f'RuntimeMaxSec={fn.scope_runtime_seconds(clocks)}', argv)
        name = fn.unit_names(OP, 'bake')['launcher']
        need = fn.scope_runtime_seconds(clocks)
        for runtime, ok in ((f'{bound + 1}s', False), (f'{need - 1}s', False), (f'{need}s', True), ('2h', False),
                            ('1d', True)):
            with self.subTest(runtime=runtime):
                self.host.units[name]['RuntimeMaxUSec'] = runtime
                if ok:
                    fn.check_launcher_scope(self.host, selection(), 3)
                else:
                    with self.assertRaises(fn.Refusal):
                        fn.check_launcher_scope(self.host, selection(), 3)
        self.assertEqual(fn.parse_timespan_seconds('2h 31min 40s'), 9100)
        for bad in ('infinity', '', '5 parsecs'):
            with self.assertRaises(fn.Refusal):
                fn.parse_timespan_seconds(bad)

    def test_executor_timeout_is_a_refusal(self):
        with self.assertRaises(fn.Refusal) as caught:
            fn.Executor().run([sys.executable, '-c', 'import time; time.sleep(5)'], 'probe', timeout=0.2)
        self.assertEqual(caught.exception.check, 'effect.probe')

    def test_failed_guest_is_labelled_failed(self):
        name = fn.unit_names(OP, 'bake')['guest']
        self.host.units[name].update({'Result': 'exit-code', 'ExecMainStatus': '1'})
        win = self.launch(RecordingExecutor(self.host))
        self.assertEqual(win.record['outcome'], 'guest-failed-awaiting-readback')

    def test_io_stat_before_is_taken_at_window_entry(self):
        f = facts(); entry = copy.deepcopy(f); entry['ioStat'] = ['254:80 at-entry']
        win = self.launch(RecordingExecutor(self.host), f=f, reobserve=lambda: entry)
        self.assertEqual(win.record['ioStatBefore'], ['254:80 at-entry'])

    def test_date_bound_is_checked_before_the_collector(self):
        dates = iter([NOW + dt.timedelta(seconds=400), NOW + dt.timedelta(seconds=8940 - 959)])

        class Late(FakeGitHub):
            def date(self):
                return next(dates)
        ex = RecordingExecutor(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex, github=Late())
        self.assertEqual(caught.exception.check, 'clock.enclosing')
        self.assertIn('unit.start-guest', ex.kinds())
        self.assertNotIn('unit.start-collector', ex.kinds())
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)


class ExitPathControls(unittest.TestCase):
    """R1-R3 and the exit shield: a started unit is stopped before caps go; signals cannot escape."""
    setUp, tearDown = LaunchControls.setUp, LaunchControls.tearDown
    launch, manifest = LaunchControls.launch, LaunchControls.manifest

    def running_guest(self):
        name = fn.unit_names(OP, 'bake')['guest']
        self.host.units[name] = {'ActiveState': 'active', 'SubState': 'running'}
        return name

    def assert_stopped_before_caps(self, ex, unit):
        kinds = ex.kinds()
        stops = [i for i, (e, a) in enumerate(ex.effects) if e == 'unit.stop-on-exit' and unit in a]
        self.assertEqual(len(stops), 1)
        self.assertLess(stops[0], min(i for i, k in enumerate(kinds) if k == 'io.cap-remove'))
        stop_timeout = [t for e, t in ex.timeouts if e == 'unit.stop-on-exit'][0]
        return stop_timeout

    def test_interrupt_while_observing_the_guest_stops_it_before_caps(self):
        guest = self.running_guest()
        stopped = {'ActiveState': 'failed', 'SubState': 'failed', 'Result': 'signal'}

        class StopMakesTerminal(RecordingExecutor):
            def run(inner, argv, effect, timeout=None):
                result = super().run(argv, effect, timeout)
                if effect == 'unit.stop-on-exit':
                    self.host.units[guest] = stopped
                return result
        ex = StopMakesTerminal(self.host)
        with mock.patch.object(fn.Window, 'observe_terminal', side_effect=fn.Interrupted(signal.SIGTERM)):
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        timeout = self.assert_stopped_before_caps(ex, guest)
        self.assertEqual(timeout, selection()['clocks']['retireSeconds'] + fn.EFFECT_TIMEOUT)
        record = json.loads(ex.written[fn.record_path(OP)])['window']
        self.assertTrue(record['guestStartAttempted'])
        self.assertEqual(record['outcome'], 'failed-awaiting-readback')
        self.assertTrue(record['guestStopOnExit']['terminal'])

    def test_unit_show_failure_while_observing_stops_the_guest(self):
        guest = self.running_guest()
        original = self.host.run

        def run(argv, timeout=30):
            if 'show' in argv and guest in argv:
                return None, 'timed out'
            return original(argv, timeout)
        self.host.run = run
        ex = RecordingExecutor(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            self.launch(ex)
        self.assertEqual(caught.exception.check, 'unit.show')
        self.assert_stopped_before_caps(ex, guest)
        record = json.loads(ex.written[fn.record_path(OP)])['window']
        self.assertEqual(record['outcome'], 'unknown-awaiting-readback', 'state unreadable after the stop')
        self.assertTrue(record['unitMayStillRun'])

    def test_start_timeout_still_stops_the_guest(self):
        guest = self.running_guest()
        ex = RecordingExecutor(self.host, fail_effect='unit.start-guest')
        with self.assertRaises(fn.Refusal):
            self.launch(ex)
        self.assert_stopped_before_caps(ex, guest)

    def test_interrupt_while_observing_the_collector_stops_it(self):
        collector = fn.unit_names(OP, 'bake')['collector']
        self.host.units[collector] = {'ActiveState': 'active', 'SubState': 'running'}
        real = fn.Window.observe_terminal

        def observe(win, unit, bound, stop):
            if unit == collector:
                raise fn.Interrupted(signal.SIGTERM)
            return real(win, unit, bound, stop)
        ex = RecordingExecutor(self.host)
        with mock.patch.object(fn.Window, 'observe_terminal', observe):
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        timeout = self.assert_stopped_before_caps(ex, collector)
        self.assertEqual(timeout, selection()['clocks']['collectorRetireSeconds'] + fn.EFFECT_TIMEOUT)
        guest = fn.unit_names(OP, 'bake')['guest']
        self.assertFalse(any(e == 'unit.stop-on-exit' and guest in a for e, a in ex.effects), 'guest observed terminal')

    def test_failed_stop_on_exit_marks_the_outcome_unknown(self):
        self.running_guest()
        ex = RecordingExecutor(self.host, fail_effect='unit.stop-on-exit')
        with mock.patch.object(fn.Window, 'observe_terminal', side_effect=fn.Interrupted(signal.SIGTERM)):
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        record = json.loads(ex.written[fn.record_path(OP)])['window']
        self.assertEqual(record['outcome'], 'unknown-awaiting-readback')
        self.assertFalse(record['guestStopOnExit']['stopped'])
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3, 'caps are still removed')

    def test_window_exit_refuses_when_a_stopped_unit_may_keep_its_drop_in(self):
        manifest = self.manifest()
        shutil.rmtree(Path(self.host.cgroot) / STORE.lstrip('/'))

        class StoreBus(RecordingExecutor):
            def run(self, argv, effect, timeout=None):
                if effect == 'io.cap-remove' and 'fsgg-store.service' in argv:
                    self.effects.append((effect, argv)); raise fn.Refusal('effect.io.cap-remove', 'timed out after 30s')
                return super().run(argv, effect, timeout)
        ex = StoreBus(self.host)
        with self.assertRaises(fn.Refusal) as caught:
            fn.window_exit(self.host, ex, manifest)
        self.assertEqual(caught.exception.check, 'io.cap-remove-readback')
        self.assertEqual([r['unit'] for r in caught.exception.result['unconfirmed']], ['fsgg-store.service'])
        self.assertEqual(ex.kinds().count('slice.revert'), 2)

    def test_signal_during_the_claim_is_raised_inside_the_window(self):
        ex = RecordingExecutor(self.host)
        original = ex.create_claim

        def claim(path, payload):
            original(path, payload)
            os.kill(os.getpid(), signal.SIGTERM); time.sleep(0.2)  # lands between claim and try
        ex.create_claim = claim
        previous = fn.install_signal_exceptions()
        try:
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
        record = json.loads(ex.written[fn.record_path(OP)])['window']
        self.assertEqual(record['firstFailure']['check'], 'launch.signal')
        self.assertNotIn('io.cap-apply', ex.kinds())

    def test_signal_during_window_exit_is_recorded_and_exit_completes(self):
        ex = RecordingExecutor(self.host)
        original = ex.run

        def run(argv, effect, timeout=None):
            if effect == 'io.cap-remove':
                os.kill(os.getpid(), signal.SIGTERM); time.sleep(0.05)
            return original(argv, effect, timeout)
        ex.run = run
        previous = fn.install_signal_exceptions()
        try:
            win = self.launch(ex)
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
        self.assertEqual(ex.kinds().count('io.cap-remove'), 3)
        self.assertEqual(ex.kinds().count('slice.revert'), 2)
        self.assertEqual(win.record['signalsDuringExit'], [int(signal.SIGTERM)] * 3)

    def test_signal_replacing_a_refusal_keeps_the_refusal(self):
        ex = RecordingExecutor(self.host)
        error = fn.Interrupted(signal.SIGTERM)
        error.__context__ = fn.Refusal('image.stock-digest', 'differs')
        with mock.patch.object(fn, '_window_body', side_effect=error):
            with self.assertRaises(fn.Interrupted):
                self.launch(ex)
        record = json.loads(ex.written[fn.record_path(OP)])['window']
        self.assertEqual(record['firstFailure']['replaced']['check'], 'image.stock-digest')

    def test_removals_and_reverts_are_effect_bounded(self):
        ex = RecordingExecutor(self.host)
        self.launch(ex)
        bounded = [t for e, t in ex.timeouts if e in ('io.cap-remove', 'io.cap-apply', 'slice.revert', 'slice.properties')]
        self.assertEqual(len(bounded), 10)
        self.assertEqual(set(bounded), {fn.EFFECT_TIMEOUT})

    def test_failed_exit_observation_counts_as_shared(self):
        f = facts(); calls = []

        def reobserve():
            calls.append(1)
            if len(calls) == 1:
                return f
            raise OSError('cgroup tree unreadable')
        win = self.launch(RecordingExecutor(self.host), f=f, reobserve=reobserve)
        self.assertIn('exitObservationError', win.record)
        self.assertTrue(win.evidence['sharedCodexHost'])
        fn.validate_evidence(win.evidence)

    def test_main_wires_signal_exceptions_before_launch(self):
        seen = {}

        def fake_launch(*args, **kwargs):
            seen['handler'] = signal.getsignal(signal.SIGTERM)
            seen['armed'] = fn.SIGNALS['raise']
            raise fn.Refusal('probe', 'stop here')
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
        try:
            with mock.patch.object(fn, 'observe', return_value=facts()), \
                    mock.patch.object(fn, 'evaluate', return_value=rows()), \
                    mock.patch.object(fn, 'launch', side_effect=fake_launch), contextlib.redirect_stderr(io.StringIO()):
                fn.main(['launch', '--selection', str(path), '--no-github'])
        finally:
            for signum, handler in saved.items():
                signal.signal(signum, handler)
        self.assertIs(seen['handler'], fn._on_signal)
        self.assertTrue(seen['armed'])

    def test_main_reports_an_interrupted_window_with_its_record(self):
        error = fn.Interrupted(signal.SIGTERM)
        window = mock.Mock(); window.record = {'firstFailure': {'check': 'launch.signal'}, 'outcome': 'refused'}
        window.record_path = fn.record_path(OP); error.window = window
        path = Path(self.tmp.name) / 'sel.json'; path.write_text(json.dumps(selection()))
        err = io.StringIO()
        saved = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP)}
        try:
            with mock.patch.object(fn, 'observe', return_value=facts()), \
                    mock.patch.object(fn, 'evaluate', return_value=rows()), \
                    mock.patch.object(fn, 'launch', side_effect=error), contextlib.redirect_stderr(err):
                code = fn.main(['launch', '--selection', str(path), '--no-github'])
        finally:
            for signum, handler in saved.items():
                signal.signal(signum, handler)
        self.assertEqual(code, 128 + int(signal.SIGTERM))
        report = json.loads(err.getvalue())
        self.assertEqual(report['interrupted'], 'SIGTERM')
        self.assertEqual(report['windowRecord'], fn.record_path(OP))


class RealClaim(unittest.TestCase):
    """The claim with the real executor on a private directory: exclusive, no-follow, race-safe."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=Path.home() if os.access(Path.home(), os.W_OK) else None)
        self.path = os.path.join(self.tmp.name, 'claims', OP)

    def tearDown(self):
        self.tmp.cleanup()

    def test_second_create_refuses_and_keeps_the_first(self):
        fn.Executor().create_claim(self.path, {'n': 1})
        with self.assertRaises(fn.Refusal) as caught:
            fn.Executor().create_claim(self.path, {'n': 2})
        self.assertEqual(caught.exception.check, 'claim.absent')
        self.assertEqual(json.loads(Path(self.path).read_text()), {'n': 1})

    def test_symlink_at_the_claim_path_refuses_and_is_not_followed(self):
        os.makedirs(os.path.dirname(self.path))
        victim = os.path.join(self.tmp.name, 'victim')
        Path(victim).write_text('keep')
        os.symlink(victim, self.path)
        with self.assertRaises(fn.Refusal):
            fn.Executor().create_claim(self.path, {'n': 1})
        self.assertEqual(Path(victim).read_text(), 'keep')
        os.remove(self.path); os.symlink(os.path.join(self.tmp.name, 'absent'), self.path)
        with self.assertRaises(fn.Refusal):
            fn.Executor().create_claim(self.path, {'n': 1})
        self.assertFalse(os.path.exists(os.path.join(self.tmp.name, 'absent')))

    def test_partial_payload_write_is_a_refusing_claim_flagged_for_a_record(self):
        with mock.patch.object(fn.json, 'dump', side_effect=OSError('No space left on device')):
            with self.assertRaises(fn.Refusal) as caught:
                fn.Executor().create_claim(self.path, {'n': 1})
        self.assertEqual(caught.exception.check, 'claim.partial')
        self.assertTrue(caught.exception.claim_created)
        self.assertTrue(os.path.lexists(self.path))
        with self.assertRaises(fn.Refusal) as again:
            fn.Executor().create_claim(self.path, {'n': 2})
        self.assertEqual(again.exception.check, 'claim.absent')

    def test_claim_payload_is_fsynced_and_a_failed_fsync_is_partial(self):
        synced = []
        real_fsync = os.fsync

        def fsync(fd):
            synced.append(os.readlink(f'/proc/self/fd/{fd}'))
            return real_fsync(fd)
        with mock.patch.object(fn.os, 'fsync', side_effect=fsync):
            fn.Executor().create_claim(self.path, {'n': 1})
        self.assertEqual(synced, [os.path.realpath(self.path)])
        other = self.path + '-b'
        with mock.patch.object(fn.os, 'fsync', side_effect=OSError('I/O error')):
            with self.assertRaises(fn.Refusal) as caught:
                fn.Executor().create_claim(other, {'n': 1})
        self.assertEqual(caught.exception.check, 'claim.partial')
        self.assertTrue(os.path.lexists(other))

    def test_concurrent_claims_have_exactly_one_winner(self):
        barrier = threading.Barrier(16)
        outcomes = []

        def contend(n):
            barrier.wait()
            try:
                fn.Executor().create_claim(self.path, {'n': n}); outcomes.append(('won', n))
            except fn.Refusal:
                outcomes.append(('refused', n))
        threads = [threading.Thread(target=contend, args=(n,)) for n in range(16)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(10)
        winners = [n for result, n in outcomes if result == 'won']
        self.assertEqual(len(outcomes), 16)
        self.assertEqual(len(winners), 1)
        self.assertEqual(json.loads(Path(self.path).read_text()), {'n': winners[0]})


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
        own = observed['units']['selfCgroup'].rstrip('/')
        self.assertIn(own, observed['lanes']['exemptDeclared'], 'the launcher\'s own cgroup is always exempt')
        self.assertNotIn(own, [lane['cgroup'] for lane in observed['lanes']['lanes']])
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
