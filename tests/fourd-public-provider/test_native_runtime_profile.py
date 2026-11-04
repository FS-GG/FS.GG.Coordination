"""Effect-free staging controls; no toolchain or downloaded producer is executed."""
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import time
import unittest
import sys
from unittest import mock

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('native_runtime_profile',ROOT/'eng/fourd-public-provider/native_runtime_profile.py')
runtime=importlib.util.module_from_spec(spec);spec.loader.exec_module(runtime)

def fake_archive(path, members, special=None):
    with tarfile.open(path,'w:gz') as archive:
        for name,body in members.items():
            info=tarfile.TarInfo(name);info.size=len(body);info.mode=0o755 if name=='dotnet' else 0o644
            archive.addfile(info,io.BytesIO(body))
        if special:
            item=tarfile.TarInfo(special);item.type=tarfile.SYMTYPE;item.linkname='/outside';archive.addfile(item)
    raw=path.read_bytes()
    return dict(name=path.name,url='https://builds.dotnet.microsoft.com/dotnet/fixture/'+path.name,bytes=len(raw),sha512=hashlib.sha512(raw).hexdigest(),files=len(members),directories=0,unpackedBytes=sum(map(len,members.values())))

class RuntimeProfileControls(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix='native-runtime-fixture-',dir='/tmp');self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.archives=self.root/'archives';self.archives.mkdir(mode=0o700)
        self.profile=dict(schema='fsgg.fourd.native-runtime-profile/1',sdkVersions=['10.0.400','10.0.401'],runtimeVersions=['10.0.11','10.0.12','10.0.13'],runtimeVersion='10.0.14',archives=[],files={})
        sets=[{'dotnet':b'oldhost400','sdk/10.0.400/.version':b'sdk400','sdk/10.0.400/FSharp/fsc.dll':b'fsc400','shared/Microsoft.NETCore.App/10.0.11/core':b'core11','host/fxr/10.0.11/fxr':b'fxr11','empty':b''},
              {'dotnet':b'oldhost401','sdk/10.0.401/.version':b'sdk401','sdk/10.0.401/FSharp/fsc.dll':b'fsc401','shared/Microsoft.NETCore.App/10.0.12/core':b'core12','host/fxr/10.0.12/fxr':b'fxr12'},
              {'dotnet':b'exacthost13','shared/Microsoft.NETCore.App/10.0.13/core':b'core13','host/fxr/10.0.13/fxr':b'fxr13'}]
        self.sets=sets
        for i,members in enumerate(sets):
            self.profile['archives'].append(fake_archive(self.archives/f'input{i}.tar.gz',members))
            for name,body in members.items():
                if name!='empty':self.profile['files'][name]={'bytes':len(body),'sha256':hashlib.sha256(body).hexdigest()}
        self.profile_sha=hashlib.sha256(runtime.canonical(self.profile)).hexdigest();self.output=self.root/'runtime'
    def stage(self):return runtime.stage(self.archives,self.output,self.profile,self.profile_sha,time.monotonic()+10,download=False)
    def test_exact_three_inputs_stage_and_verify_without_execution(self):
        with mock.patch('subprocess.run',side_effect=AssertionError('no execution permitted')):
            result=self.stage();verified=runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()+10)
        self.assertEqual(result,verified);self.assertEqual(str(self.output/'dotnet'),result['hostPath'])
        self.assertFalse(result['producerExecuted']);self.assertFalse(result['effectiveRuntimeJoinVerified'])
        self.assertEqual(b'exacthost13',(self.output/'dotnet').read_bytes())
    def test_wrong_archive_digest_refuses_before_creating_target(self):
        self.profile['archives'][0]['sha512']='0'*128
        with self.assertRaisesRegex(runtime.Refused,'SHA512'):self.stage()
        self.assertFalse(self.output.exists())
    def test_existing_prefix_and_c068_are_never_overwritten(self):
        self.stage()
        with self.assertRaisesRegex(runtime.Refused,'fresh'):self.stage()
        with self.assertRaisesRegex(runtime.Refused,'c068'):runtime.root_path(runtime.BASELINE)
        with self.assertRaisesRegex(runtime.Refused,'c068'):runtime.root_path(runtime.BASELINE/'consumer')
    def test_archive_traversal_and_links_refuse(self):
        for name,symlink in (('../foreign',None),('/foreign',None),('link','link')):
            with self.subTest(name=name):
                output=self.root/('runtime-'+str(len(list(self.root.iterdir()))))
                members={name:b'foreign'} if symlink is None else self.sets[0]
                changed=fake_archive(self.archives/'input0.tar.gz',members,symlink)
                p=copy.deepcopy(self.profile);p['archives'][0]=changed
                with self.assertRaises(runtime.Refused):runtime.stage(self.archives,output,p,self.profile_sha,time.monotonic()+10,download=False)
                self.assertFalse((self.root/'foreign').exists())
    def test_installed_mutation_missing_extra_and_symlink_refuse(self):
        self.stage();host=self.output/'dotnet';original=host.read_bytes()
        host.write_bytes(b'otherbytes')
        with self.assertRaises(runtime.Refused):runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()+10)
        host.write_bytes(original);extra=self.output/'extra';extra.write_bytes(b'extra')
        with self.assertRaises(runtime.Refused):runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()+10)
        extra.unlink();host.unlink();host.symlink_to('/outside')
        with self.assertRaises(OSError):runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()+10)
    def test_self_consistent_receipt_cannot_replace_fixed_host_pin(self):
        self.stage();body=b'foreignhost';(self.output/'dotnet').write_bytes(body)
        path=self.output/runtime.RECEIPT;r=json.loads(path.read_bytes());r['files']['dotnet'].update(bytes=len(body),sha256=hashlib.sha256(body).hexdigest());path.write_bytes(runtime.canonical(r))
        with self.assertRaisesRegex(runtime.Refused,'pinned'):runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()+10)
    def test_late_original_deadline_and_profile_drift_refuse(self):
        self.stage()
        with self.assertRaisesRegex(runtime.Refused,'deadline'):runtime.verify(self.output,self.profile,self.profile_sha,time.monotonic()-1)
        with self.assertRaisesRegex(runtime.Refused,'provenance'):runtime.verify(self.output,self.profile,'0'*64,time.monotonic()+10)
    def test_shipped_profile_is_exact_official_403_14(self):
        p,digest=runtime.profile();self.assertEqual(['10.0.403'],p['sdkVersions']);self.assertEqual('10.0.14',p['runtimeVersion'])
        self.assertEqual(['10.0.14'],p['runtimeVersions'])
        self.assertEqual('634efbf2414144d140d6f59d167342c8b3b53b4c34346ed786dd0470ddc9a3cb',p['files']['dotnet']['sha256'])
        self.assertEqual(1,len(p['archives']));self.assertEqual('dotnet-sdk-10.0.403-linux-x64.tar.gz',p['archives'][0]['name']);self.assertEqual(64,len(digest))

    def test_actual_host_path_is_constructed_only_from_verified_stage(self):
        facts=self.stage()
        producer=dict(sourceSha='1'*40,sourceTree='2'*40,bytes=123,sha256='3'*64,bindingSha256='4'*64,
                      provenanceSha256='5'*64,files={n:'6'*64 for n in runtime.RUNTIME_NAMES},adapterFiles={n:'7'*64 for n in runtime.ADAPTERS})
        release=dict(releaseObserved=True,publicationObserved=True,releaseId=1,assetId=2,authorId=3,
                     tag='fourd-native-producer-'+'1'*40,name='fourd-native-producer-linux-x64.tar',bytes=123,sha256='3'*64)
        value=dict(producer=producer,publication=release,runtime=dict(sdkVersion='10.0.403',runtimeVersion='10.0.14',
                   hostSha256=facts['hostSha256']),adoptionSelected=False)
        actual=runtime.adoption_from_publication(json.dumps(value),facts,self.profile)
        self.assertEqual(str(self.output/'dotnet'),actual['hostPath'])
        value['producer']['hostPath']='/invented'
        with self.assertRaisesRegex(runtime.Refused,'identity'):runtime.adoption_from_publication(json.dumps(value),facts,self.profile)
        del value['producer']['hostPath'];value['runtime']['hostSha256']='0'*64
        with self.assertRaisesRegex(runtime.Refused,'profile'):runtime.adoption_from_publication(json.dumps(value),facts,self.profile)

    def test_duplicate_publication_and_expired_stage_refuse_before_output(self):
        with self.assertRaisesRegex(runtime.Refused,'duplicate'):runtime.closed_json('{"producer":{},"producer":{}}')
        with self.assertRaisesRegex(runtime.Refused,'deadline'):
            runtime.stage(self.archives,self.output,self.profile,self.profile_sha,time.monotonic()-1,download=False)
        self.assertFalse(self.output.exists())

    def test_nonobject_publication_refuses_cleanly(self):
        for value in (None, [], {'producer':None,'publication':{},'runtime':{},'adoptionSelected':False}):
            with self.subTest(value=value),self.assertRaises(runtime.Refused):
                runtime.publication_fields(json.dumps(value),self.profile)

    def test_bad_publication_prevents_toolchain_acquisition(self):
        argv=['runtime','stage','--archives',str(self.root/'new-archives'),'--output',str(self.output),
              '--publication-env','PUBLICATION','--adoption-output',str(self.root/'adoption.json')]
        with mock.patch.object(sys,'argv',argv),mock.patch.dict(os.environ,{'PUBLICATION':'{}'},clear=True), \
             mock.patch.object(runtime,'stage',side_effect=AssertionError('expensive stage must not start')) as launch:
            with self.assertRaisesRegex(runtime.Refused,'closed published'):runtime.main()
            launch.assert_not_called()
        self.assertFalse(self.output.exists())

if __name__=='__main__':unittest.main()
