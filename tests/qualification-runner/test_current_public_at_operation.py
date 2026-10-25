"""Pure original-artifact/provenance controls, without real network or guest operations."""
import copy
import hashlib
import importlib.util
import io
import json
import os
import re
import stat
import types
from unittest.mock import patch
from pathlib import Path
import tempfile
import time
import unittest
import urllib.error
import zipfile

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('at_operation_controls',ROOT/'eng/current-public-at/operate-current-public-at.py')
O=importlib.util.module_from_spec(spec);spec.loader.exec_module(O)


class Controls(unittest.TestCase):
    def setUp(self):
        self.budget=O.Budget(time.monotonic()+5)
    def fixture(self):
        body=b'explicit synthetic archive bytes'
        plan={'source':'a'*40,'runId':10,'runAttempt':1,'artifactId':20,'artifactBytes':len(body),
              'artifactSha256':hashlib.sha256(body).hexdigest(),'workflowPath':'.github/workflows/fable-external-reference-source.yml'}
        run={'id':10,'run_attempt':1,'head_sha':'a'*40,'path':plan['workflowPath'],'event':'pull_request',
             'status':'completed','conclusion':'success','repository':{'id':1281961814}}
        jobs={'total_count':2,'jobs':[{'name':'External reference workflow static controls','id':30,'run_id':10,
              'head_sha':'a'*40,'status':'completed','conclusion':'success'},{'name':'unselected native job','status':'completed','conclusion':'skipped'}]}
        artifact={'id':20,'expired':False,'size_in_bytes':len(body),'digest':'sha256:'+plan['artifactSha256'],
                  'name':'noble-no-install-source-10-1','workflow_run':{'id':10,'head_sha':'a'*40,'repository_id':1281961814}}
        return plan,run,jobs,artifact,body
    def test_actual_reader_joins_source_run_job_metadata_and_original_archive_body(self):
        plan,run,jobs,artifact,body=self.fixture();calls=[]
        def request(path,budget):
            calls.append(path)
            return json.dumps(jobs if '/attempts/' in path else run if '/runs/' in path else data).encode()
        data=artifact;raw,custody=O.native_artifact(plan,self.budget,request,lambda *_:body)
        self.assertEqual(body,raw);self.assertEqual(30,custody['job']['id']);self.assertEqual(3,len(calls))
    def test_wrong_original_scope_metadata_and_body_stop_without_native_acceptance(self):
        for attack in ('run-source','run-event','job-skipped','jobs-count','artifact-origin','artifact-digest','body'):
            plan,run,jobs,artifact,body=self.fixture()
            if attack=='run-source':run['head_sha']='b'*40
            elif attack=='run-event':run['event']='workflow_dispatch'
            elif attack=='job-skipped':jobs['jobs'][0]['conclusion']='skipped'
            elif attack=='jobs-count':jobs['total_count']=3
            elif attack=='artifact-origin':artifact['workflow_run']['repository_id']=1
            elif attack=='artifact-digest':artifact['digest']='sha256:'+'b'*64
            else:body=b'changed'
            def request(path,budget):return json.dumps(jobs if '/attempts/' in path else run if '/runs/' in path else data).encode()
            data=artifact
            with self.subTest(attack=attack),self.assertRaises(ValueError):O.native_artifact(plan,self.budget,request,lambda *_:body)
    def test_response_status_is_checked_before_read_and_body_bound_before_digest(self):
        class Response:
            status=500;headers={}
            def read(self,*args):raise AssertionError('body read after HTTP failure')
        with self.assertRaises(ValueError):O.read_response(Response(),100,self.budget)
        response=Response();response.status=200;response.headers={'Content-Length':'101'}
        with self.assertRaises(ValueError):O.read_response(response,100,self.budget)
    def test_public_metadata_never_sends_private_token_or_follows_redirect(self):
        class Response(io.BytesIO):
            status=200;headers={}
        class Opener:
            def __init__(self,redirect=False):self.redirect=redirect;self.requests=[]
            def open(self,request,**kwargs):
                self.requests.append(request)
                if self.redirect:raise urllib.error.HTTPError(request.full_url,302,'redirect',{'Location':'https://attacker.invalid/'},None)
                return Response(b'public synthetic metadata')
        opener=Opener();O.api('repos/'+O.TEMPLATES+'/actions/artifacts/1',self.budget,opener=opener)
        self.assertIsNone(opener.requests[0].get_header('Authorization'))
        opener=Opener(True)
        with self.assertRaises(urllib.error.HTTPError):O.api('repos/'+O.TEMPLATES+'/actions/artifacts/1',self.budget,opener=opener)
        self.assertEqual(1,len(opener.requests))
        with self.assertRaises(ValueError):O.api('repos/foreign/repo',self.budget,opener=opener)

    def test_fixed_attachment_requires_real_root_file_mount_and_body_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'source-artifact.zip';body=b'explicit synthetic original';path.write_bytes(body)
            plan={'artifactBytes':len(body),'artifactSha256':hashlib.sha256(body).hexdigest()}
            original_fstat=os.fstat;original_open=Path.open
            def fstat(fd):
                value=original_fstat(fd)
                return types.SimpleNamespace(st_mode=(stat.S_IFDIR|0o755) if stat.S_ISDIR(value.st_mode) else value.st_mode,
                    st_uid=0,st_nlink=value.st_nlink,st_dev=value.st_dev,st_ino=value.st_ino,st_size=value.st_size,
                    st_mtime_ns=value.st_mtime_ns,st_ctime_ns=value.st_ctime_ns)
            flags=['ro,nodev,nosuid,noexec']
            def proc(path,*args,**kwargs):
                if str(path).startswith('/proc/self/fdinfo/'):return io.StringIO('mnt_id: 42\n')
                if str(path)=='/proc/self/mountinfo':return io.StringIO('42 0 0:1 / '+str(root)+' '+flags[0]+' - fixture source ro\n')
                return original_open(path,*args,**kwargs)
            with patch.object(O,'ARCHIVE_INPUT',path),patch.object(O.os,'fstat',fstat),patch.object(Path,'open',proc):
                self.assertEqual(body,O.read_attached_archive(plan,self.budget))
                flags[0]='rw,nodev,nosuid,noexec'
                with self.assertRaises(ValueError):O.read_attached_archive(plan,self.budget)
                flags[0]='ro,nodev,nosuid,noexec';path.write_bytes(b'x'*len(body))
                with self.assertRaises(ValueError):O.read_attached_archive(plan,self.budget)
                path.unlink();path.symlink_to(root/'foreign')
                with self.assertRaises(OSError):O.read_attached_archive(plan,self.budget)

    def zip(self,omit=None,extra=None):
        output=io.BytesIO()
        with zipfile.ZipFile(output,'w') as archive:
            for name in ('transaction.json','apt-config.log','apt-update.log','apt-simulate.log','status'):
                archive.writestr('noble-source-transaction/'+name,b'original synthetic input')
            for suite in ('noble','noble-updates','noble-security'):
                names=[suite+'.InRelease']+[suite+'-'+component+'-'+kind for component in ('main','universe') for kind in ('binary-amd64-Packages.xz','source-Sources.xz')]
                for name in names:
                    if name!=omit:archive.writestr('noble-source-transaction/metadata/'+name,b'synthetic metadata')
            if extra:archive.writestr(extra,b'unsafe')
        return output.getvalue()
    def test_original_zip_requires_self_contained_exact_needed_inputs_without_reconstruction(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);O.originals(self.zip(),root,self.budget)
            self.assertEqual(20,sum(p.is_file() for p in root.rglob('*')))
        for kwargs in ({'omit':'noble-main-source-Sources.xz'},{'extra':'../escape'},{'extra':'/absolute'}):
            with self.subTest(kwargs=kwargs),tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)
                with self.assertRaises(ValueError):O.originals(self.zip(**kwargs),root,self.budget)
                self.assertEqual([],list(root.iterdir()))
    def test_duplicate_json_and_expired_original_clock_refuse(self):
        with self.assertRaises(ValueError):O.parse('{"x":1,"x":2}')
        with self.assertRaises(ValueError):O.Budget(time.monotonic()-1).left()
        for value in (float('nan'),float('inf'),float('-inf')):
            with self.subTest(value=value),self.assertRaises(ValueError):O.Budget(value)


if __name__=='__main__':unittest.main()
