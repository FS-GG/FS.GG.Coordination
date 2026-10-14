#!/usr/bin/env python3
"""Inert SYNTHETIC fixtures; success proves neither signatures nor pacman semantics."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import sys
import io

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('common_image', ROOT / 'eng/portable-workspace-image.py')
IMAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMAGE)


def digest(value):
    return hashlib.sha256(value).hexdigest()


class CommonArchInputs(unittest.TestCase):
    def setUp(self):
        for target in ('subprocess.run', 'urllib.request.urlopen'):
            guard = patch(target, side_effect=AssertionError('external execution forbidden in inert controls'))
            guard.start()
            self.addCleanup(guard.stop)
        self.temporary = tempfile.TemporaryDirectory(prefix='SYNTHETIC-common-inputs-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bundle = self.root / 'bundle'
        self.bundle.mkdir()
        self.proof = b'SYNTHETIC TEST ONLY: no authenticated signature/transaction\n'
        # Only the expected effective-proof digest/size are patched for inert fixtures.
        self.synthetic_policy_patches = []
        for name, value in (('COMMON_DONOR_POLICY_SHA256', digest(self.proof)),
                            ('COMMON_DONOR_POLICY_BYTES', len(self.proof))):
            guard = patch.object(IMAGE, name, value)
            guard.start(); self.addCleanup(guard.stop)
            self.synthetic_policy_patches.append(guard)
        self.before = b'donor 1-1\n'
        self.files = []
        def put(path, payload, role=None):
            target = self.bundle / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload)
            result = {'path': path, 'bytes': len(payload), 'sha256': digest(payload)}
            if role:
                self.files.append({**result, 'role': role})
            return result
        proof = put('proof/synthetic.txt', self.proof)
        before = put('proof/before.txt', self.before)
        final = put('acquired/package-inventory.expected', b'donor 1-1\npython 3-1\n', 'inventory')
        dbs = []
        for repo in ('core', 'extra'):
            db = put(f'acquired/repositories/{repo}/os/x86_64/{repo}.db', b'SYNTHETIC DB ' + repo.encode(), 'database')
            dbs.append({**db, 'repository': repo, 'signature': {'availability': 'absent-observed', 'carrierPath': None,
                         'sha256': None, 'evidenceRef': 'synthetic'}, 'evidenceRef': 'synthetic'})
        package = put('acquired/repositories/core/os/x86_64/python-3-1-any.pkg.tar.zst', b'SYNTHETIC PACKAGE', 'package')
        sig = put(package['path'] + '.sig', b'SYNTHETIC SIGNATURE', 'package-signature')
        self.identity = {'repository': 'FS-GG/.github', 'revision': 'a'*40, 'tree': 'b'*40,
                         'containerfileSha256': IMAGE.COMMON_PINS['Containerfile'],
                         'pacmanConfigSha256': IMAGE.COMMON_PINS['pacman.conf'], 'inputsSha256': IMAGE.COMMON_PINS['inputs.json']}
        self.source = {'classification': 'source-specification-not-acquired-input-lock', 'platform': 'linux/amd64',
                       'donor': {'reference': IMAGE.COMMON_DONOR_REFERENCE, 'indexDigest': 'sha256:'+'a'*64,
                                 'configDigest': IMAGE.COMMON_DONOR_CONFIG_DIGEST, 'layers': [{'sha256': 'c'*64, 'bytes': 1}]},
                       'snapshot': {'date': '2026-10-12', 'acquisitionServer': 'synthetic', 'repositories': ['core','extra']},
                       'trust': {'candidateSigLevel': 'Required DatabaseOptional', 'candidateLocalFileSigLevel': 'Required'},
                       'targets': ['python'], 'acquiredInputs': None}
        trust = {k: 'SYNTHETIC' for k in ('pacmanVersion','libalpmVersion','keyringPackageVersion')}
        trust['donorPacmanConfigSha256'] = IMAGE.COMMON_DONOR_PACMAN_CONFIG_SHA256
        trust['acquisitionConfigSha256'] = IMAGE.COMMON_ACQUISITION_CONFIG_SHA256
        trust['finalConfigSha256'] = IMAGE.COMMON_PINS['pacman.conf']
        trust.update({k: 'Required DatabaseOptional' for k in ('donorEffectiveSigLevel','finalEffectiveSigLevel')})
        trust['donorEffectiveLocalFileSigLevel'] = 'Optional'
        trust['finalEffectiveLocalFileSigLevel'] = 'Required'
        trust.update({k: 'synthetic' for k in ('donorPolicyEvidenceRef','keyringEvidenceRef','reconciliationEvidenceRef')})
        self.lock = {**copy.deepcopy(self.source), 'classification': 'acquired-input-lock', 'acquiredInputs': {
            'imageSource': self.identity, 'donor': {**self.source['donor'], 'evidenceRefs': ['synthetic']},
            'snapshot': {**self.source['snapshot'], 'databases': dbs}, 'trust': trust,
            'transaction': {'method': 'pacman-full-system', 'targets': ['python'],
                **{k: 'synthetic' for k in ('resolutionEvidenceRef','downloadEvidenceRef','offlineTransactionEvidenceRef','hookEvidenceRef')},
                'beforeInventory': {**before,'evidenceRef':'before'}, 'finalInventory': final,
                'providers': [], 'replacements': [], 'removals': []},
            'packages': [{**package, 'repository': 'core','name':'python','version':'3-1','architecture':'any',
                'signature': {'kind':'detached','carrierPath':sig['path'],'sha256':sig['sha256'],
                              'signerFingerprint':'A'*40,'evidenceRef':'synthetic'}, 'transactionEvidenceRef':'synthetic'}],
            'files': sorted(self.files, key=lambda f:f['path']), 'checksumsSha256':'',
            'evidence': {'synthetic':proof,'before':before}}}
        self.refresh_checksums()

    def refresh_checksums(self):
        a = self.lock['acquiredInputs']
        value = ''.join(f"{f['sha256']}  {f['path'][9:]}\n" for f in a['files']).encode()
        (self.bundle / 'acquired/SHA256SUMS').write_bytes(value)
        a['checksumsSha256'] = digest(value)

    def validate(self):
        with patch.object(IMAGE, 'run', side_effect=AssertionError('process forbidden')), patch.object(IMAGE, 'download', side_effect=AssertionError('download forbidden')):
            return IMAGE.validate_common_inputs(self.bundle,self.lock,self.source,self.identity)

    def test_valid_synthetic_checker(self):
        self.assertEqual(set(self.validate()), {f['path'] for f in self.files})

    def test_structural_mutations_refuse(self):
        original = copy.deepcopy(self.lock)
        mutations = [
            lambda a:a['trust'].update(donorEffectiveSigLevel='Optional'),
            lambda a:a['transaction'].pop('hookEvidenceRef'),
            lambda a:a['packages'][0]['signature'].update(evidenceRef='absent'),
            lambda a:a['transaction'].update(targets=['other']),
            lambda a:a['donor'].update(reference='other'),
            lambda a:a['snapshot'].update(date='2026-10-13'),
            lambda a:a['evidence']['synthetic'].update(sha256='0'*64),
            lambda a:a['files'].append(copy.deepcopy(a['files'][0])),
            lambda a:a['files'][0].update(bytes=True),
            lambda a:a['donor']['layers'][0].update(bytes=True),
            lambda a:a['packages'][0].update(bytes=True),
            lambda a:a['files'][1].update(path='acquired/repositories/core/os/x86_64/bad:alias.db'),
            lambda a:a['files'][0].update(bytes=IMAGE.MAX_PAYLOAD_BYTES+1),
            lambda a:a['files'][0].update(path='acquired/../escape'),
            lambda a:a['imageSource'].update(repository='wrong'),
            lambda a:a['transaction']['beforeInventory'].update(evidenceRef='synthetic'),
        ]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                self.lock=copy.deepcopy(original);mutate(self.lock['acquiredInputs'])
                with self.assertRaises(RuntimeError):self.validate()
        self.lock=original


    def test_archive_path_cannot_bind_two_packages(self):
        a=self.lock['acquiredInputs']
        second=copy.deepcopy(a['packages'][0]);second.update(name='donor',version='1-1')
        a['packages'].append(second)
        with self.assertRaisesRegex(RuntimeError,'duplicate package archive path'):self.validate()

    def test_payload_count_includes_checksum_separately(self):
        # The population walker counts files; the checker adds the checksum slot
        # beyond its independently enforced 32768 payload descriptor maximum.
        calls=[]
        original=IMAGE.common_population
        def observe(root,prefix,bound):
            calls.append((prefix,bound));return original(root,prefix,bound)
        with patch.object(IMAGE,'common_population',side_effect=observe):self.validate()
        self.assertIn(('acquired',32769),calls)


    def test_production_policy_pins_refuse_synthetic_evidence(self):
        for guard in reversed(self.synthetic_policy_patches):guard.stop()
        self.assertEqual(IMAGE.COMMON_DONOR_POLICY_SHA256, '60d18150be3245f6df3e53a6a301380e9eb5cab17047d5112b6709eedaf9d0ac')
        self.assertEqual(IMAGE.COMMON_DONOR_POLICY_BYTES, 1751)
        self.assertEqual(IMAGE.COMMON_DONOR_PACMAN_CONFIG_SHA256, '1b1c6195b21865a660480f41a29a172f103b1aa3e846d1db877e04cd6220e454')
        self.assertEqual(IMAGE.COMMON_ACQUISITION_CONFIG_SHA256, '513b2a7cb4de9afdbaf275d880b7a077bdb96c3975f4eac50ae9bcfd4d0c1cc9')
        self.assertEqual(IMAGE.COMMON_DONOR_CONFIG_DIGEST, 'sha256:5a30b4acc83ff841682eb21795e00f7ff030796f5e6fd2a127decd7a45ba7cef')
        self.assertEqual(IMAGE.COMMON_DONOR_REFERENCE, 'docker.io/library/archlinux@sha256:363129b67b2bc6c5a634f8fb4961a7e2eba213a2ec075d159127b1260290a722')
        with self.assertRaisesRegex(RuntimeError, 'policy proof mismatch'):self.validate()

    def test_exact_policy_tuple_refuses_changes(self):
        original=copy.deepcopy(self.lock)
        mutations=[
            lambda a:a['trust'].update(donorEffectiveLocalFileSigLevel='Required'),
            lambda a:a['trust'].update(finalEffectiveLocalFileSigLevel='Optional'),
            lambda a:a['trust'].update(donorEffectiveLocalFileSigLevel='Never'),
            lambda a:a['trust'].update(finalEffectiveLocalFileSigLevel='Required TrustAll'),
            lambda a:a['trust'].update(donorEffectiveSigLevel='Optional DatabaseOptional'),
            lambda a:a['trust'].update(finalEffectiveSigLevel='Required DatabaseOptional TrustAll'),
            lambda a:a['trust'].update(donorEffectiveSigLevel='Required DatabaseRequired'),
            lambda a:a['trust'].update(donorPacmanConfigSha256='0'*64),
            lambda a:a['trust'].update(acquisitionConfigSha256='0'*64),
            lambda a:a['trust'].update(finalConfigSha256='0'*64),
            lambda a:a['trust'].update(donorPolicyEvidenceRef='missing'),
            lambda a:a['trust'].update(reconciliationEvidenceRef='missing'),
            lambda a:a['trust'].update(keyringEvidenceRef='missing'),
            lambda a:a['donor'].update(reference='other'),
            lambda a:a['donor'].update(configDigest='sha256:'+'0'*64),
        ]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                self.lock=copy.deepcopy(original);mutate(self.lock['acquiredInputs'])
                with self.assertRaises(RuntimeError):self.validate()
        self.lock=original
        for field,value in (('COMMON_DONOR_POLICY_BYTES',len(self.proof)+1),('COMMON_DONOR_POLICY_SHA256','0'*64)):
            with patch.object(IMAGE,field,value):
                with self.assertRaisesRegex(RuntimeError,'policy proof mismatch'):self.validate()
        (self.bundle/'proof/synthetic.txt').write_bytes(b'changed proof with stale descriptor')
        with self.assertRaisesRegex(RuntimeError,'input bytes differ'):self.validate()

    def test_duplicate_json_key(self):
        with self.assertRaisesRegex(RuntimeError,'duplicate JSON'):
            IMAGE.common_json(b'{"a":1,"a":2}')

    def test_unsafe_populations(self):
        path=self.bundle/'acquired/extra'
        for kind in ('extra','symlink','hardlink','fifo'):
            with self.subTest(kind=kind):
                if kind=='extra':path.write_bytes(b'extra')
                elif kind=='symlink':path.symlink_to(self.bundle/'proof/synthetic.txt')
                elif kind=='hardlink':
                    import os
                    os.link(self.bundle/'acquired/package-inventory.expected',path)
                else:
                    import os
                    os.mkfifo(path)
                with self.assertRaises(RuntimeError):self.validate()
                path.unlink()

    def test_inventories_and_coverage(self):
        for payload in (b'python 3-1\n', b'python 3-1\ndonor 1-1\n',b'donor 1-1\ndonor 1-1\npython 3-1\n',b'donor 2-1\npython 3-1\n'):
            with self.subTest(payload=payload):
                final=self.lock['acquiredInputs']['transaction']['finalInventory']
                path=self.bundle/final['path'];path.write_bytes(payload)
                final.update(bytes=len(payload),sha256=digest(payload))
                record=next(f for f in self.lock['acquiredInputs']['files'] if f['role']=='inventory')
                record.update(bytes=len(payload),sha256=digest(payload));self.refresh_checksums()
                with self.assertRaises(RuntimeError):self.validate()
        self.setUp()
        (self.bundle/'acquired/SHA256SUMS').write_bytes(b'wrong coverage\n')
        self.lock['acquiredInputs']['checksumsSha256']=digest(b'wrong coverage\n')
        with self.assertRaisesRegex(RuntimeError,'coverage'):self.validate()

    def cli(self,profile,mode,extra=()):
        argv=['image.py',mode,'--profile',profile,'--source','/missing','--expected-source-revision','a'*40,
              '--state-dir',str(self.root/'state'),'--root',str(self.root/'graph'),'--runroot',str(self.root/'run'),*extra]
        with patch.object(sys,'argv',argv), patch.object(IMAGE,'common_raw_path',side_effect=AssertionError('I/O before refusal')), patch.object(IMAGE,'require_storage_paths',side_effect=AssertionError('I/O before refusal')),patch('sys.stderr',io.StringIO()):
            with self.assertRaises(SystemExit):IMAGE.main()
        self.assertFalse((self.root/'state').exists())

    def test_cli_immediate_refusals(self):
        for mode in ('preflight','qualify'):self.cli('common-arch',mode)
        self.cli('unknown','preflight')
        self.cli('portable-python','check-inputs')
        self.cli('portable-python','preflight',['--image-source','/missing'])

    def test_actual_staging_and_commands(self):
        image=self.root/'image'
        recipe=image/'deployment/provisioned-podman';recipe.mkdir(parents=True)
        recipe_bytes={'Containerfile':b'SYNTHETIC recipe\n','pacman.conf':b'SYNTHETIC config\n','inputs.json':IMAGE.canonical_bytes(self.source)}
        pins={name:digest(value) for name,value in recipe_bytes.items()}
        for name,value in recipe_bytes.items():(recipe/name).write_bytes(value)
        self.identity.update(containerfileSha256=pins['Containerfile'],pacmanConfigSha256=pins['pacman.conf'],inputsSha256=pins['inputs.json'])
        self.lock['acquiredInputs']['trust']['finalConfigSha256']=pins['pacman.conf']
        payload=IMAGE.canonical_bytes(self.lock);(self.bundle/'inputs.acquired.json').write_bytes(payload)
        args=argparse.Namespace(source=str(ROOT),image_source=str(image),acquisition_dir=str(self.bundle),state_dir=str(self.root/'state'),root=str(self.root/'graph'),runroot=str(self.root/'run'),podman='/usr/bin/podman',expected_source_revision='c'*40,expected_image_source_revision='a'*40,expected_acquired_inputs_sha256=digest(payload))
        def git(checkout,*flags):
            if flags[0]=='status':return ''
            if flags[-1]=='HEAD^{tree}':return 'b'*40
            return 'c'*40 if checkout==ROOT else 'a'*40
        with patch.dict(IMAGE.COMMON_PINS,pins),patch.object(IMAGE,'git_value',side_effect=git),patch.object(IMAGE,'run',side_effect=AssertionError('native forbidden')),patch.object(IMAGE,'download',side_effect=AssertionError('download forbidden')):
            argv=['image.py','check-inputs','--profile','common-arch','--source',args.source,
                  '--expected-source-revision',args.expected_source_revision,'--state-dir',args.state_dir,
                  '--root',args.root,'--runroot',args.runroot,'--image-source',args.image_source,
                  '--expected-image-source-revision',args.expected_image_source_revision,
                  '--acquisition-dir',args.acquisition_dir,'--expected-acquired-inputs-sha256',args.expected_acquired_inputs_sha256]
            output=io.StringIO()
            with patch.object(sys,'argv',argv),patch('sys.stdout',output):
                self.assertEqual(IMAGE.main(),0)
            result=json.loads(output.getvalue())
            context=Path(result['context'])
            self.assertFalse(result['nativeExecutionEnabled'])
            self.assertFalse((context/'proof').exists())
            self.assertFalse((context/'inputs.acquired.json').exists())
            self.assertEqual({p.relative_to(context).as_posix() for p in context.rglob('*') if p.is_file()},
                             {'Containerfile','pacman.conf','acquired/SHA256SUMS'}|{f['path'] for f in self.files})
            self.assertIn('--network=none',result['buildCommands'][0])
            self.assertEqual(result['buildCommands'][1][-1],IMAGE.COMMON_IMAGE_NAME)
            self.assertNotIn('schema',result)
            with self.assertRaisesRegex(RuntimeError,'context already exists'):IMAGE.check_common_inputs(args)
            import shutil
            shutil.rmtree(context)
            args.expected_acquired_inputs_sha256='0'*64
            with self.assertRaisesRegex(RuntimeError,'external lock'):IMAGE.check_common_inputs(args)
            args.expected_acquired_inputs_sha256=digest(payload)
            original=IMAGE.common_read
            def corrupt(path,*a,**kw):
                result=original(path,*a,**kw)
                destination=kw.get('destination',a[2] if len(a)>2 else None)
                if destination is not None and destination.name=='Containerfile':destination.write_bytes(b'late corruption')
                return result
            with patch.object(IMAGE,'common_read',side_effect=corrupt):
                with self.assertRaisesRegex(RuntimeError,'input bytes differ'):IMAGE.check_common_inputs(args)
            self.assertFalse(context.exists())
            saved=context.with_name('original-owned-context')
            substituted=[False]
            def replace_context(path,*a,**kw):
                result=original(path,*a,**kw)
                destination=kw.get('destination',a[2] if len(a)>2 else None)
                if destination is not None and not substituted[0]:
                    context.rename(saved);context.mkdir();(context/'foreign').write_bytes(b'foreign owner')
                    substituted[0]=True
                return result
            with patch.object(IMAGE,'common_read',side_effect=replace_context):
                with self.assertRaisesRegex(RuntimeError,'context substituted') as failure:IMAGE.check_common_inputs(args)
            self.assertTrue((context/'foreign').exists())
            self.assertTrue(saved.exists())
            self.assertTrue(any('cleanup unconfirmed' in note for note in failure.exception.__notes__))
            with patch.object(IMAGE,'git_value',return_value='dirty'):
                with self.assertRaisesRegex(RuntimeError,'dirty/wrong'):IMAGE.check_common_inputs(args)
            args.source=str(image)
            with self.assertRaisesRegex(RuntimeError,'substitution'):IMAGE.check_common_inputs(args)


if __name__=='__main__':unittest.main()
