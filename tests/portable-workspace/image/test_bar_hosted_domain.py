#!/usr/bin/env python3
"""Synthetic controls for the hosted-VM BAR domain helper. No VM, service,
volume, userland or BAR content is involved."""
import argparse
import copy
import importlib.util
import hashlib
import json
import re
import os
from pathlib import Path
import sys
import subprocess
import textwrap
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


DOMAIN = load('bar_hosted_domain_controls', ROOT / 'eng/bar_hosted_domain.py')
BAR = load('bar_hosted_domain_reader', ROOT / 'eng/portable_workspace_bar.py')
GIB = 1024**3


def observed(device='7:0', backing='8:1'):
    return {'schema': DOMAIN.SCHEMA, 'relation': DOMAIN.RELATION, 'verdict': None, 'refusals': None,
            'bootId': '0f5c2a9e-3b1d-4c8e-9a7f-1e2d3c4b5a69',
            'runner': {'imageOS': 'ubuntu24', 'imageVersion': DOMAIN.RUNNER_IMAGE_VERSION, 'githubHosted': True},
            'services': {'stopped': list(DOMAIN.STOPPED_SERVICES), 'active': [], 'daemonProcesses': 0},
            'userns': {'apparmorRestrictUnprivileged': 0, 'maxUserNamespaces': 63838},
            'volume': {'device': device, 'loadStoreDevice': device, 'filesystem': 'ext4',
                       'bytes': 127 * GIB, 'allocatedBytes': 127 * GIB, 'freeBytes': 116 * GIB,
                       'freeInodes': 7_806_964, 'discard': 'nodiscard', 'loopBacked': True, 'backingDevice': backing},
            'builder': {'version': 'buildah version 1.43.1 (image-spec 1.1.1, runtime-spec 1.2.1)'},
            'resources': {'memAvailableBytes': 15 * GIB, 'logicalCpus': 4, 'affinity': 4}}


class HostedDomain(unittest.TestCase):
    def test_unreadable_security_evidence_stays_unknown(self):
        with patch('builtins.open', side_effect=PermissionError('authored denial')):
            value = DOMAIN.security_observation()
        self.assertTrue(all(row['status'] == 'Unknown' for row in value['observations'].values()))
        self.assertNotIn('apparmorEnabled', value)
        self.assertIn('not host LSM absence', value['scope'])

    def test_failed_kernel_readback_preserves_unknown_and_original_window(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / 'security.json'
            args = argparse.Namespace(evidence=str(target), owner_uid=os.getuid(), owner_gid=os.getgid())
            with patch.dict(os.environ, {'HOST_JOB_STARTED': '1700000000'}), \
                 patch.object(DOMAIN, 'command', return_value=(1, '', 'authored denial')) as call:
                DOMAIN.security_readback(args)
            value = json.loads(target.read_text())
            self.assertEqual(value['audit']['absenceConclusion'], 'Unknown')
            self.assertEqual(value['audit']['exit'], 1)
            self.assertIn('--since=@1700000000', call.call_args.args[0])

    def test_contract_constants_match_the_closed_bar_reader(self):
        self.assertEqual(DOMAIN.SCHEMA, BAR.HOSTED_DOMAIN_SCHEMA)
        self.assertEqual(DOMAIN.RELATION, BAR.HOSTED_RELATION)
        self.assertEqual(DOMAIN.RUNNER_IMAGE, BAR.HOSTED_RUNNER_IMAGE)
        self.assertEqual(DOMAIN.RUNNER_IMAGE_VERSION, BAR.HOSTED_RUNNER_IMAGE_VERSION)
        self.assertEqual(DOMAIN.STOPPED_SERVICES, BAR.HOSTED_STOPPED_SERVICES)
        self.assertEqual(DOMAIN.DISK_BYTES, BAR.shared.DISK_BYTES)
        self.assertEqual(DOMAIN.MEMORY_BYTES, BAR.shared.RSS_BYTES + 2 * GIB)
        self.assertEqual(DOMAIN.BUILDER_VERSION, BAR.shared.BUILDER_VERSION)

    def test_admitted_evidence_is_accepted_by_the_actual_bar_reader(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            allocation, load_root = root / 'allocation', root / 'load'
            allocation.mkdir(); load_root.mkdir()
            device = DOMAIN.device_text(allocation)
            value = observed(device, '8:2' if device == '8:1' else '8:1')
            value['bootId'] = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
            evidence = DOMAIN.finish(value)
            self.assertEqual((evidence['verdict'], evidence['refusals']), ('admitted', []))
            target, disposition = root / 'domain.json', root / 'disposition.json'
            target.write_bytes(DOMAIN.canonical(evidence)); target.chmod(0o600)
            disposition.write_bytes(DOMAIN.canonical({'sharedHostReservedMemoryBytes': 0, 'sharedHostReservedCpu': 0,
                                                      'sharedHostReservedDiskBytes': 0, 'unquantifiedResourceConflicts': []}))
            disposition.chmod(0o600)
            digest = lambda value: __import__('hashlib').sha256(value.read_bytes()).hexdigest()
            selection = {'resourceDomain': {'relation': BAR.HOSTED_RELATION}, 'clock': {'bootId': value['bootId']},
                         'admission': {'domain': {'path': str(target), 'sha256': digest(target)},
                                       'resourceDisposition': {'path': str(disposition), 'sha256': digest(disposition)}}}
            joined = BAR.hosted_domain(selection, allocation, load_root)
            self.assertEqual(joined['evidenceSha256'], digest(target))

    def test_each_decision_refusal_control(self):
        cases = {
            'runner-image': lambda e: e['runner'].update(imageVersion='20261019.340.1'),
            'boot-identity': lambda e: e.update(bootId=''),
            'service-stop': lambda e: e['services'].update(daemonProcesses=1),
            'rootless-userns': lambda e: e['userns'].update(apparmorRestrictUnprivileged=1),
            'volume-format': lambda e: e['volume'].update(discard='discard'),
            'volume-preallocation': lambda e: e['volume'].update(allocatedBytes=2 * GIB),
            'loop-not-backed': lambda e: e['volume'].update(loopBacked=False),
            'backing-same-device': lambda e: e['volume'].update(backingDevice=e['volume']['device']),
            'volume-capacity': lambda e: e['volume'].update(loadStoreDevice='7:1'),
            'builder-version': lambda e: e['builder'].update(version='buildah version 1.33.7 (image-spec 1.1.0-rc.5, runtime-spec 1.1.0)'),
            'memory': lambda e: e['resources'].update(memAvailableBytes=14 * GIB - 1),
            'cpu': lambda e: e['resources'].update(affinity=2),
        }
        for refusal, mutate in cases.items():
            value = observed(); mutate(value)
            expected = {'loop-not-backed': 'volume-preallocation',
                        'backing-same-device': 'volume-preallocation'}.get(refusal, refusal)
            with self.subTest(refusal=refusal):
                evidence = DOMAIN.finish(value)
                self.assertEqual(evidence['verdict'], 'refused')
                self.assertEqual(evidence['refusals'], [expected])
                self.assertNotIn('loadStoreDevice', evidence['volume'])
        floor = observed(); floor['volume']['freeBytes'] = 80 * GIB - 1
        self.assertEqual(DOMAIN.finish(floor)['refusals'], ['volume-capacity'])

    def test_mount_row_selects_the_deepest_mount_of_the_volume_device(self):
        with tempfile.TemporaryDirectory() as temporary:
            value = Path(temporary)
            device = DOMAIN.device_text(value)
            mountinfo = (f'1 0 {device} / / rw,relatime - ext4 /dev/sda1 rw,discard\n'
                         f'2 1 {device} / {value} rw,nodiscard - ext4 /dev/loop0 rw\n'
                         f'3 1 254:9 / {value} rw - tmpfs tmpfs rw\n')
            source, filesystem, options = DOMAIN.mount_row(value, mountinfo)
            self.assertEqual((source, filesystem), ('/dev/loop0', 'ext4'))
            self.assertNotIn('discard', options)
            with self.assertRaisesRegex(RuntimeError, 'unavailable'):
                DOMAIN.mount_row(value, '1 0 254:9 / / rw - tmpfs tmpfs rw\n')

    def test_loop_backing_reads_the_loop_sysfs_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            sysfs = Path(temporary)
            loop = sysfs / 'dev/block/7:0/loop'
            loop.mkdir(parents=True)
            (loop / 'backing_file').write_text('/fsgg-bar-volume.img\n')
            (loop / 'offset').write_text('0\n'); (loop / 'sizelimit').write_text('0\n')
            self.assertEqual(DOMAIN.loop_backing('7:0', sysfs), (Path('/fsgg-bar-volume.img'), 0, 0))
            self.assertEqual(DOMAIN.loop_backing('8:1', sysfs), (None, None, None))

    def test_rootless_mapping_must_cover_the_image_user(self):
        self.assertTrue(DOMAIN.uid_map_covers('         0       1001          1\n         1     165536      65536\n', 10001))
        self.assertFalse(DOMAIN.uid_map_covers('         0       1001          1\n', 10001))
        self.assertFalse(DOMAIN.uid_map_covers('garbage', 10001))


class UserlandEntry(unittest.TestCase):
    """Execute the maintained shell plan with privileged commands replaced.

    This checks composition and fail-closed ordering, not Linux namespace
    semantics; the original hosted failure still requires native qualification.
    """

    def test_workflow_bytes_and_references_match_execution_pin_registration(self):
        path = '.github/workflows/bar-hosted-domain-preflight.yml'
        extension = json.loads((ROOT / 'evidence/github-substrate-v2/gs2-06-7/execution-pins-extension.json').read_text())
        registrations = [document for document in extension['documents'] if document['path'] == path]
        self.assertEqual(len(registrations), 1, 'workflow needs one execution-pin registration')
        registration = registrations[0]
        workflow = (ROOT / path).read_bytes()
        self.assertEqual(registration['sha256'], hashlib.sha256(workflow).hexdigest(),
                         'workflow changed: update its execution-pins-extension digest with the reviewed source')
        uses = re.findall(r'^\s*(?:-\s*)?uses:\s*([^\s#]+)', workflow.decode(), re.MULTILINE)
        self.assertEqual(sorted(registration['externalReferences']), sorted(use for use in uses if not use.startswith('./')))
        self.assertEqual(sorted(registration['localReferences']), sorted(use for use in uses if use.startswith('./')))

    @staticmethod
    def script():
        workflow = (ROOT / '.github/workflows/bar-hosted-domain-preflight.yml').read_text()
        block = workflow.split('          ENTER_SCRIPT: |\n', 1)[1].split('        run: |', 1)[0]
        return textwrap.dedent(block)

    def run_entry(self, fail='', transform=lambda script: script):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            userland, tools = root / 'userland', root / 'tools'
            userland.mkdir(); tools.mkdir()
            log = root / 'commands'
            stub = tools / 'stub'
            stub.write_text('''#!/bin/bash
set -eu
name=${0##*/}
printf '%s' "$name" >> "$TRACE"
printf ' <%s>' "$@" >> "$TRACE"
printf '\\n' >> "$TRACE"
[ "$name" != "${FAIL_COMMAND:-}" ] || exit 41
case "$name" in
  pivot_root)
    # A non-mount root or a pivot from the wrong cwd is invalid.
    test "$PWD" = "$USERLAND"
    test "$*" = '. .old-root'
    /usr/bin/grep -Fx "mount <--bind> <$USERLAND> <$USERLAND>" "$TRACE" >/dev/null
    ;;
  setpriv)
    /usr/bin/grep -Fx 'umount <--lazy> </.old-root>' "$TRACE" >/dev/null
    /usr/bin/grep -Fx 'rmdir </.old-root>' "$TRACE" >/dev/null
    ;;
  chroot) exit 42 ;;
esac
''')
            stub.chmod(0o700)
            for name in ('mount', 'mkdir', 'install', 'pivot_root', 'umount', 'rmdir', 'setpriv', 'chroot'):
                (tools / name).symlink_to(stub)
            script = transform(self.script()).replace('/usr/bin/setpriv', str(tools / 'setpriv'))
            result = subprocess.run(['/bin/bash', '-c', script], capture_output=True, text=True,
                                    timeout=10, env={**os.environ, 'PATH': str(tools) + ':/usr/bin:/bin',
                                                     'TRACE': str(log), 'USERLAND': str(userland),
                                                     'VOLUME': '/fixture-volume', 'FAIL_COMMAND': fail})
            return result, log.read_text()

    def test_real_entry_plan_pivots_then_detaches_before_dropping_privilege(self):
        result, trace = self.run_entry()
        self.assertEqual(result.returncode, 0, result.stderr)
        ordered = ['mount <--make-rprivate> </>', 'mount <--bind>', 'mount <-t> <proc>',
                   'mount <-t> <cgroup2>', 'pivot_root <.> <.old-root>',
                   'umount <--lazy> </.old-root>', 'rmdir </.old-root>', 'setpriv <--reuid=1001> <--regid=1001>']
        cursor = 0
        for operation in ordered:
            cursor = trace.index(operation, cursor) + len(operation)
        self.assertNotIn('chroot', trace)
        self.assertIn('--init-groups> <--reset-env>', trace)
        self.assertIn('--expected-uid 1001', trace)
        self.assertIn('--allocation /fsgg-bar/allocation', trace)
        self.assertIn('--load-store /home/runner/.local/share/fsgg/headless-images/bar/preflight', trace)

    def test_failed_root_transition_never_starts_the_userland_check(self):
        for operation in ('mount', 'pivot_root', 'umount', 'rmdir'):
            with self.subTest(operation=operation):
                result, trace = self.run_entry(operation)
                self.assertEqual(result.returncode, 41, result.stderr)
                self.assertFalse(any(row.startswith('setpriv ') for row in trace.splitlines()))

    def test_broken_chroot_and_non_mount_root_plans_are_rejected(self):
        def chroot(script):
            start = script.index('mkdir "$USERLAND/.old-root"')
            end = script.index('exec /usr/bin/setpriv', start)
            return script[:start] + 'exec chroot "$USERLAND" ' + script[end + len('exec '):]

        variants = {
            'old-chroot': chroot,
            'non-mount-root': lambda script: script.replace('mount --bind "$USERLAND" "$USERLAND"', ':'),
            'wrong-pivot-root': lambda script: script.replace('pivot_root . .old-root', 'pivot_root / .old-root'),
        }
        for name, transform in variants.items():
            with self.subTest(name=name):
                result, trace = self.run_entry(transform=transform)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(row.startswith('setpriv ') for row in trace.splitlines()))

    def test_missing_pivot_tool_never_starts_the_userland_check(self):
        result, trace = self.run_entry(transform=lambda script: script.replace(
            'pivot_root . .old-root', 'fsgg-fixture-missing-pivot-root . .old-root'))
        self.assertEqual(result.returncode, 127, result.stderr)
        self.assertFalse(any(row.startswith('setpriv ') for row in trace.splitlines()))

    def test_shell_syntax(self):
        result = subprocess.run(['/bin/bash', '-n'], input=self.script(), text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
