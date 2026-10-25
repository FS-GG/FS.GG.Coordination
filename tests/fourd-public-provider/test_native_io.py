"""Fixture-only real producer/capsule/raw-Git acquisition; no native operations."""
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng/fourd-public-provider'))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_bootstrap as bootstrap
import native_io as io
import native_run as driver
from test_native_protocol import NativeFixture, canonical, sha
import test_source_capsule as capsule_tests


class NativeIOTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        command = json.loads(os.environ['FSGG_FOURD_NATIVE_TEST_COMMAND'])
        if len(command) != 1:
            raise AssertionError('actual standalone producer required')
        cls.command = command
        cls.storage = tempfile.TemporaryDirectory(prefix='native-io-', dir='/tmp')
        cls.root = Path(cls.storage.name)
        cls.host = Path(shutil.which('dotnet')).resolve()
        archive = cls.root / 'runtime.tar'
        prepared = bootstrap.prepare_artifact(ROOT, Path(command[0]).parent, cls.host, archive)
        cls.adoption = dict(schema='fsgg.fourd.native-producer-adoption/1', **prepared,
                            releaseId=90, assetId=91, tag='fixture-runtime', name='runtime.tar',
                            authorId=9, hostPath=str(cls.host))
        cls.runtime_bytes = archive.read_bytes()
        cls.key = cls.root / 'publisher-key.pem'
        cls.public = cls.root / 'publisher-public.pem'
        subprocess.run(['openssl','genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:3072','-out',cls.key],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.key.chmod(0o600)
        subprocess.run(['openssl','pkey','-in',cls.key,'-pubout','-out',cls.public], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    @classmethod
    def tearDownClass(cls):
        cls.storage.cleanup()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='native-io-case-', dir='/tmp')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def bootstrap(self, mutation=None):
        adoption = json.loads(json.dumps(self.adoption))
        if mutation:
            mutation(adoption)
        raw = self.runtime_bytes
        metadata = dict(id=90,tag_name='fixture-runtime',author=dict(id=9),draft=False,prerelease=False,
                        assets=[dict(id=91,name='runtime.tar',size=len(raw),digest='sha256:'+sha(raw),
                                     state='uploaded',uploader=dict(id=9))])
        class Reader:
            def request(self, endpoint, limit, deadline, accept=None):
                value = canonical(metadata) if endpoint.endswith('/90') else raw
                assert len(value) <= limit
                return 200,value,[]
        return bootstrap.acquire_producer(adoption,ROOT,self.root/'acquired',reader=Reader(),deadline=time.monotonic()+60)

    def fixture(self, additional_files=None):
        producer = self.bootstrap()
        f = NativeFixture(self.root, [str(Path(producer['runtimeRoot'])/'FourD.Typed')])
        value, expected = capsule_tests.SourceCapsuleTests().fixture(self.root)
        if additional_files:
            repo=self.root/'original'
            for name,(raw,mode) in additional_files.items():
                path=repo/name;path.parent.mkdir(parents=True,exist_ok=True)
                path.write_bytes(raw);path.chmod(mode)
            subprocess.run(['git','-C',str(repo),'add','.'],check=True)
            subprocess.run(['git','-C',str(repo),'-c','user.name=Fixture','-c','user.email=fixture@example.invalid',
                            'commit','--quiet','-m','actual consumer closure'],check=True)
            def git(*arguments):return subprocess.check_output(['git','-C',str(repo),*arguments])
            revision=git('rev-parse','HEAD').decode().strip();tree=git('rev-parse','HEAD^{tree}').decode().strip()
            inventory=hashlib.sha256();records=[]
            for item in git('ls-tree','-rz','--full-tree','HEAD').split(b'\0'):
                if not item:continue
                metadata,name=item.split(b'\t',1);mode,kind,oid=metadata.split()
                if kind!=b'blob':raise AssertionError('fixture closure must contain ordinary blobs')
                raw=(repo/name.decode()).read_bytes();inventory.update(name+b'\0'+raw+b'\0')
                records.append(dict(path=name.decode(),mode=mode.decode(),blobOid=oid.decode(),bytes=len(raw),
                                    sha256=sha(raw),contentBase64=base64.b64encode(raw).decode()))
            expected=dict(sourceSha=revision,sourceTree=tree,inventorySha256=inventory.hexdigest())
            value=dict(schema=io.capsules.SNAPSHOT_SCHEMA,**expected,commitObjectBase64=base64.b64encode(git('cat-file','commit','HEAD')).decode(),files=records)
        substitutions = {f.revision:expected['sourceSha'],f.tree:expected['sourceTree'],f.inventory:expected['inventorySha256']}
        def replace(value):
            if isinstance(value,dict):return {key:replace(item) for key,item in value.items()}
            if isinstance(value,list):return list(map(replace,value))
            return substitutions.get(value,value) if isinstance(value,str) else value
        for path in (f.run_path,f.job_path):
            path.write_bytes(canonical(replace(json.loads(path.read_bytes()))))
        f.packet=replace(f.packet)
        f.packet['producer'] = dict(sourceSha=producer['expectedSourceSha'],sourceTree=producer['expectedSourceTree'],
                                  files=producer['expectedFiles'],bindingSha256=producer['expectedBindingSha256'],
                                  provenanceSha256=producer['expectedProvenanceSha256'])
        descriptor=f.packet['sourceCapsule']['descriptor']
        plain=io.capsules.canonical(value)
        descriptor.update(plaintextBytes=len(plain),plaintextSha256=sha(plain),
                          recipientPublicKeySha256=sha(self.public.read_bytes()),
                          sealerSha256=sha((io.HERE/'seal_native_custody.mjs').read_bytes()))
        capsule=f.packet['sourceCapsule']
        capsule.update(descriptorSha256=sha(canonical(descriptor)),recipientPublicKeySha256=descriptor['recipientPublicKeySha256'])
        publisher_plain=self.root/'publisher-snapshot.json';publisher_plain.write_bytes(plain);publisher_plain.chmod(0o600)
        sealed=self.root/'publisher-cipher.json'
        subprocess.run(['node',str(io.HERE/'seal_native_custody.mjs'),'seal',str(publisher_plain),str(self.public),str(sealed),
                        f.packet['runNonce']+'-source',expected['sourceSha'],capsule['descriptorSha256']],check=True,
                       stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        capsule.update(ciphertextBytes=sealed.stat().st_size,ciphertextSha256=sha(sealed.read_bytes()))
        f.packet_path.write_bytes(canonical(f.packet))
        for asset in f.release['assets']:
            asset.update(size=f.packet_path.stat().st_size if asset['id']==72 else capsule['ciphertextBytes'],
                         digest='sha256:'+(sha(f.packet_path.read_bytes()) if asset['id']==72 else capsule['ciphertextSha256']))
        f.release_path.write_bytes(canonical(f.release))
        private=self.root/'private';private.mkdir(mode=0o700)
        client=io.NativeClient(Path(producer['runtimeRoot'])/'FourD.Typed',private/'native-control',mode='full',
                               started=time.monotonic(),started_unix=f.started,host=self.host)
        client.config=dict(private=str(private),coordinationSource=str(ROOT))
        self.assertTrue(client.observe('producer',evidence=producer)['eventAccepted'])
        evidence=dict(f.root_evidence,expectedPlacementSha=expected['sourceSha'])
        self.assertTrue(client.observe('packet',evidence=evidence)['eventAccepted'])
        def read(kind,identity,limit,deadline):
            raw=canonical(f.release) if kind=='release' else sealed.read_bytes()
            if len(raw)>limit:raise AssertionError('fixture exceeds actual IO bound')
            return raw
        return f,client,private,expected,read

    def acquire(self,fixture,read=None):
        f,client,private,expected,reader=fixture
        effects=io.Effects()
        source=io.acquire_capsule(f.packet,self.key.read_bytes(),private,effects,deadline=client.started+600,
                                  client=client,read=read or reader)
        self.assertTrue(effects.settle())
        context=driver.prepare_context(source,private/'context',effects,deadline=client.started+600,client=client)
        proof=driver.source_evidence(client,context,source,effects)
        result=client.observe('source',evidence=proof)
        return source,context,result

    def test_actual_acquisition_joins_raw_nonroot_commit_modes_and_clean_context(self):
        fixture=self.fixture();source,context,result=self.acquire(fixture)
        expected=fixture[3]
        self.assertTrue(result['eventAccepted'],result)
        self.assertEqual('source-ready',result['state']['phase'])
        self.assertFalse((context/'.git').exists())
        self.assertEqual(expected['sourceSha'],subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip())
        self.assertEqual(b'',subprocess.check_output(['git','-C',str(source),'status','--porcelain']))
        self.assertEqual(0o700,(source/'tool.sh').stat().st_mode&0o777)
        self.assertNotEqual(0,subprocess.run(['git','-C',str(source),'cat-file','-e','HEAD^'],stderr=subprocess.DEVNULL).returncode)
        self.assertEqual([],list((fixture[2]/'credentials').iterdir()))
        self.assertIn('recipient-intent',[item['resource'] for item in result['state']['owned']])
        self.assertEqual(['preflight'],result['actions'])
        self.assertTrue(all(not path.read_bytes() for path in (fixture[1].work).glob('git-observation-*')))

    def test_wrong_ciphertext_refuses_before_key_or_plaintext_creation(self):
        fixture=self.fixture()
        def corrupt(kind,identity,limit,deadline):
            raw=fixture[4](kind,identity,limit,deadline)
            return raw+b' ' if kind=='asset' else raw
        with self.assertRaisesRegex(io.NativeIOError,'native-capsule-join-refused'):
            self.acquire(fixture,read=corrupt)
        self.assertFalse((fixture[2]/'source').exists())
        self.assertEqual([],list((fixture[2]/'credentials').iterdir()))
        self.assertFalse(any(row['kind']=='key-exposed' for row in fixture[1].history))

    def test_context_mode_mutation_is_causal_at_compiled_source_join(self):
        fixture=self.fixture();source,context,result=self.acquire(fixture)
        self.assertTrue(result['eventAccepted'])
        (context/'tool.sh').chmod(0o600)
        proof=driver.source_evidence(fixture[1],context,source,io.Effects())
        result=fixture[1].observe('revalidate-source',evidence=proof)
        self.assertFalse(result['eventAccepted'])
        self.assertIn('native-context-mode-refused',result['refusal'])
        self.assertFalse(result['state']['qualified'])

    def test_cancel_after_key_creation_wipes_only_after_child_settlement(self):
        f,client,private,expected,reader=self.fixture()
        effects=io.Effects()
        original=effects.run
        def cancel_key(argv,**options):
            if argv[:2]==["/usr/bin/openssl","pkey"]:
                self.assertTrue((private/"credentials/recipient.pem").exists())
                self.assertIn("recipient-intent",[row["resource"] for row in client.response["state"]["owned"]])
                client.signal();effects.request_cancel()
                raise io.NativeIOError("fixture-cancel-before-key-readback")
            return original(argv,**options)
        with mock.patch.object(effects,"run",side_effect=cancel_key):
            with self.assertRaisesRegex(io.NativeIOError,"fixture-cancel-before-key-readback"):
                io.acquire_capsule(f.packet,self.key.read_bytes(),private,effects,deadline=client.started+600,
                                   client=client,read=reader)
        self.assertTrue(effects.settle())
        self.assertEqual([],list((private/"credentials").iterdir()))
        self.assertFalse((private/"source").exists())
        result=client.observe("cancel")
        self.assertFalse(result["state"]["cleanupComplete"])
        self.assertFalse(result["state"]["qualified"])

    def test_wrong_runtime_binding_refuses_before_any_source_or_key(self):
        with self.assertRaisesRegex(io.NativeIOError,'native-bootstrap-member-bytes-refused'):
            self.bootstrap(lambda value:value.update(bindingSha256='0'*64))
        self.assertFalse((self.root/'private').exists())


if __name__=='__main__':
    unittest.main()
