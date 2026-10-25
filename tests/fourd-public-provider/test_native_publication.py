"""Inert controls for the frozen archive and four one-shot release writes."""
import base64
import copy
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import tarfile
import time
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng/fourd-public-provider'))
import native_publication as pub

SOURCE, TREE = '1'*40, '2'*40
RUN, ARTIFACT, PUBLISHER_RUN = 700, 800, 900
PREFIX = '/repos/' + pub.REPOSITORY


def candidate():
    files = {name: ('fixture-'+name).encode() for name in pub.bootstrap.RUNTIME_NAMES}
    hashes = {name: pub.sha(raw) for name, raw in files.items()}
    binding = pub.capsules.canonical(dict(schema='fsgg.fourd.typed-policy-binding/1', sourceSha=SOURCE, files=hashes))
    provenance = pub.capsules.canonical(dict(schema='fsgg.fourd.native-runtime-provenance/1', sourceSha=SOURCE,
        sourceTree=TREE, bindingSha256=pub.sha(binding), files=hashes, sdkVersion='10.0.400',
        runtimeVersion='10.0.9', hostSha256='3'*64))
    files.update({'typed-policy-binding.json': binding, 'native-runtime-provenance.json': provenance})
    archive = pub.canonical_tar(files)
    adapters = {name: ('adapter-'+name).encode() for name in pub.bootstrap.ADAPTERS}
    producer = dict(sourceSha=SOURCE, sourceTree=TREE, bytes=len(archive), sha256=pub.sha(archive),
        bindingSha256=pub.sha(binding), provenanceSha256=pub.sha(provenance), files=hashes,
        adapterFiles={name: pub.sha(raw) for name, raw in adapters.items()})
    manifest = pub.capsules.canonical(dict(schema='fsgg.fourd.native-producer-artifact/1', producer=producer))
    return archive, manifest, adapters


def zip_pair(archive, manifest):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as package:
        package.writestr(pub.ARCHIVE, archive)
        package.writestr(pub.MANIFEST, manifest)
    return buffer.getvalue()


class FixtureAPI:
    def __init__(self):
        self.archive, self.manifest, adapters = candidate()
        self.zipped = zip_pair(self.archive, self.manifest)
        self.identity = dict(source=SOURCE, qualificationRunId=RUN, artifactId=ARTIFACT,
            archiveSha256=pub.sha(self.archive), manifestSha256=pub.sha(self.manifest),
            operationId='fixture-original', publisherRunId=PUBLISHER_RUN)
        self.environment = dict(GITHUB_REPOSITORY=pub.REPOSITORY, GITHUB_EVENT_NAME='workflow_dispatch',
            GITHUB_REF='refs/heads/main', GITHUB_RUN_ATTEMPT='1', GITHUB_SHA=SOURCE,
            GITHUB_RUN_ID=str(PUBLISHER_RUN), GITHUB_ACTOR_ID='55',
            GITHUB_WORKFLOW_REF=pub.REPOSITORY+'/'+pub.PUBLISHER+'@refs/heads/main')
        self.rows = {
            PREFIX: dict(id=pub.REPOSITORY_ID, full_name=pub.REPOSITORY, private=False, default_branch='main'),
            PREFIX+'/git/ref/heads/main': dict(ref='refs/heads/main', object=dict(type='commit', sha=SOURCE)),
            PREFIX+'/git/commits/'+SOURCE: dict(sha=SOURCE, tree=dict(sha=TREE)),
            PREFIX+'/actions/workflows/'+pub.QUALIFIER.split('/')[-1]: dict(id=600, path=pub.QUALIFIER),
            PREFIX+'/actions/workflows/'+pub.PUBLISHER.split('/')[-1]: dict(id=601, path=pub.PUBLISHER),
            PREFIX+'/actions/runs/'+str(RUN): dict(id=RUN, workflow_id=600, path=pub.QUALIFIER, head_sha=SOURCE,
                head_branch='main', event='push', run_attempt=1, status='completed', conclusion='success',
                repository=dict(id=pub.REPOSITORY_ID, full_name=pub.REPOSITORY),
                head_repository=dict(id=pub.REPOSITORY_ID), check_suite_id=650),
            PREFIX+'/actions/runs/'+str(RUN)+'/attempts/1/jobs?per_page=100': dict(total_count=1, jobs=[dict(
                id=701, run_id=RUN, run_attempt=1, head_sha=SOURCE, name='source-constructor',
                status='completed', conclusion='success', steps=[dict(number=i+1, name=name, status='completed', conclusion='success')
                for i, name in enumerate(('Compile and exercise actual native producer and capsule IO',
                                         'Retain the frozen qualified native producer'))])]),
            PREFIX+'/check-runs/701': dict(id=701, app=dict(id=15368), head_sha=SOURCE,
                name='source-constructor', status='completed', conclusion='success', check_suite=dict(id=650)),
            PREFIX+'/actions/artifacts/'+str(ARTIFACT): dict(id=ARTIFACT,
                name=pub.ARTIFACT_PREFIX+SOURCE+'-'+str(RUN)+'-1', expired=False, size_in_bytes=len(self.zipped),
                workflow_run=dict(id=RUN, head_sha=SOURCE, repository_id=pub.REPOSITORY_ID,
                                  head_repository_id=pub.REPOSITORY_ID), digest='sha256:'+pub.sha(self.zipped)),
            PREFIX+'/actions/runs/'+str(PUBLISHER_RUN): dict(id=PUBLISHER_RUN, workflow_id=601,
                event='workflow_dispatch', path=pub.PUBLISHER, head_sha=SOURCE, head_branch='main', run_attempt=1,
                status='in_progress', display_title='FourD producer '+SOURCE+' fixture-original',
                repository=dict(id=pub.REPOSITORY_ID), head_repository=dict(id=pub.REPOSITORY_ID),
                actor=dict(id=55), triggering_actor=dict(id=55)),
            PREFIX+'/actions/workflows/'+pub.PUBLISHER.split('/')[-1]+'/runs?head_sha='+SOURCE+'&per_page=100':
                dict(total_count=1, workflow_runs=[dict(id=PUBLISHER_RUN)]),
        }
        for name, raw in adapters.items():
            self.rows[PREFIX+'/contents/eng/fourd-public-provider/'+name+'?ref='+SOURCE] = dict(
                type='file', encoding='base64', size=len(raw), content=base64.b64encode(raw).decode(),
                sha=hashlib.sha1(f'blob {len(raw)}\0'.encode()+raw).hexdigest())
        self.tag, self.release = None, None
        self.writes, self.calls = [], []
        self.unknown_at, self.drift_after, self.directory = None, None, None
        self.wrong_download = False

    def get(self, endpoint, **kwargs):
        status, raw = self.request('GET', endpoint, **kwargs)
        pub.require(status == 200, 'fixture unavailable metadata')
        return pub.closed(raw)

    def request(self, method, endpoint, body=None, *, limit=pub.MAX_JSON, binary=False, public=False):
        self.calls.append((method, endpoint, public))
        if method == 'GET':
            if endpoint in self.rows:
                return 200, pub.capsules.canonical(self.rows[endpoint])
            if endpoint == PREFIX+'/actions/artifacts/'+str(ARTIFACT)+'/zip':
                return 200, self.zipped
            if endpoint == PREFIX+'/git/ref/tags/'+pub.tag_name(SOURCE):
                return (200, pub.capsules.canonical(self.tag)) if self.tag else (404, b'{}')
            if endpoint in (PREFIX+'/releases/tags/'+pub.tag_name(SOURCE), PREFIX+'/releases/1000'):
                if self.release is None or (public and self.release['draft']): return 404, b'{}'
                return 200, pub.capsules.canonical(self.release)
            if endpoint == PREFIX+'/releases/assets/1001':
                return 200, self.archive if not self.wrong_download else self.archive+b'foreign'
            raise AssertionError('unexpected read '+endpoint)
        journal = pub.closed((self.directory/'journal.json').read_bytes())
        assert journal['writes'][-1]['state'] == 'possible-send'
        assert journal['writes'][-1]['method'] == method and journal['writes'][-1]['endpoint'] == endpoint
        assert journal['writes'][-1]['requestSha256'] == pub.sha(body)
        self.writes.append((method, endpoint, body))
        if endpoint == PREFIX+'/git/refs':
            value=pub.closed(body);self.tag=dict(ref=value['ref'], object=dict(type='commit', sha=value['sha']))
            response=self.tag;status=201
        elif endpoint == PREFIX+'/releases':
            value=pub.closed(body);assert value['make_latest']=='false'
            self.release=dict(id=1000, tag_name=value['tag_name'], target_commitish=value['target_commitish'],
                body=value['body'], draft=True, prerelease=False, author=dict(id=56), assets=[])
            response=self.release;status=201
        elif '/assets?name=' in endpoint:
            assert body==self.archive
            response=dict(id=1001, name=pub.ARCHIVE, size=len(body), digest='sha256:'+pub.sha(body),
                state='uploaded', uploader=dict(id=56));self.release['assets']=[response];status=201
        elif method=='PATCH' and endpoint==PREFIX+'/releases/1000':
            assert pub.closed(body)==dict(draft=False, make_latest='false')
            self.release['draft']=False;response=self.release;status=200
        else:raise AssertionError('unexpected write '+endpoint)
        if self.drift_after == len(self.writes):
            self.rows[PREFIX+'/git/ref/heads/main']['object']['sha']='9'*40
        if self.unknown_at == len(self.writes):raise pub.Unknown('fixture lost response')
        return status, pub.capsules.canonical(response)


class CandidateControls(unittest.TestCase):
    def setUp(self):
        self.archive,self.manifest,self.adapters=candidate()

    def test_exact_seven_member_candidate_and_zip(self):
        pub.verify_candidate(self.archive,self.manifest,source=SOURCE,tree=TREE,
            archive_sha=pub.sha(self.archive),manifest_sha=pub.sha(self.manifest))
        self.assertEqual((self.archive,self.manifest),pub.unpack_pair(zip_pair(self.archive,self.manifest)))

    def test_selected_source_and_digests_refuse_after_positive(self):
        pub.verify_candidate(self.archive,self.manifest)
        for options in (dict(source='3'*40),dict(tree='4'*40),dict(archive_sha='5'*64),
                        dict(manifest_sha='6'*64),dict(adapters={})):
            with self.subTest(options=options),self.assertRaises(pub.Refused):
                pub.verify_candidate(self.archive,self.manifest,**options)

    def test_extra_member_wrong_mode_trailing_and_changed_bytes_refuse(self):
        for raw in (self.archive+b'foreign',self.archive[:-512],self.archive.replace(b'fixture-FourD.Typed.dll',b'foreign-FourD.Typed.dll')):
            with self.subTest(size=len(raw)),self.assertRaises(pub.Refused):pub.verify_candidate(raw,self.manifest)
        members={name:b'x' for name in pub.bootstrap.MEMBERS};members['extra']=b'x'
        raw=pub.canonical_tar(members);m=pub.closed(self.manifest);m['producer'].update(bytes=len(raw),sha256=pub.sha(raw))
        with self.assertRaises(pub.Refused):pub.verify_candidate(raw,pub.capsules.canonical(m))

    def test_extra_zip_entry_and_manifest_fields_refuse(self):
        buffer=io.BytesIO(zip_pair(self.archive,self.manifest))
        with zipfile.ZipFile(buffer,'a') as z:z.writestr('extra',b'foreign')
        with self.assertRaises(pub.Refused):pub.unpack_pair(buffer.getvalue())
        m=pub.closed(self.manifest);m['qualified']=True
        with self.assertRaises(pub.Refused):pub.verify_candidate(self.archive,pub.capsules.canonical(m))

    def test_one_header_or_closure_fault_after_positive_rehashes_causally(self):
        pub.verify_candidate(self.archive,self.manifest)
        for fault in ('mode','uid','duplicate','extra','trailing','sdk'):
            with self.subTest(fault=fault):
                with tarfile.open(fileobj=io.BytesIO(self.archive),mode='r:') as package:
                    rows=[(copy.copy(row),package.extractfile(row).read()) for row in package]
                if fault=='mode':rows[0][0].mode=0o755
                if fault=='uid':rows[0][0].uid=1
                if fault=='duplicate':rows.append((copy.copy(rows[0][0]),rows[0][1]))
                if fault=='extra':
                    row=tarfile.TarInfo('extra');row.size=1;row.mode=0o600;rows.append((row,b'x'))
                m=pub.closed(self.manifest)
                if fault=='sdk':
                    for index,(row,raw) in enumerate(rows):
                        if row.name=='native-runtime-provenance.json':
                            value=pub.closed(raw);value['sdkVersion']='10.0.401';raw=pub.capsules.canonical(value)
                            row.size=len(raw);rows[index]=(row,raw);m['producer']['provenanceSha256']=pub.sha(raw)
                buffer=io.BytesIO()
                with tarfile.open(fileobj=buffer,mode='w:',format=tarfile.USTAR_FORMAT) as package:
                    for row,raw in rows:package.addfile(row,io.BytesIO(raw))
                raw=buffer.getvalue()+(b'foreign' if fault=='trailing' else b'')
                m['producer'].update(bytes=len(raw),sha256=pub.sha(raw))
                with self.assertRaises(pub.Refused):pub.verify_candidate(raw,pub.capsules.canonical(m))

    def test_prepare_freezes_once_and_refuses_existing_output(self):
        with tempfile.TemporaryDirectory(prefix='fourd-freeze-controls-',dir='/tmp') as temporary:
            output=Path(temporary)/'candidate';producer=pub.closed(self.manifest)['producer']
            def create(source,published,host,archive):
                archive.write_bytes(self.archive);return producer
            with mock.patch.object(pub.bootstrap,'prepare_artifact',side_effect=create) as prepare:
                self.assertEqual(producer,pub.prepare(Path('source'),Path('published'),Path('host'),output))
                self.assertEqual(1,prepare.call_count)
                self.assertEqual(self.archive,pub.read_pair(output)[0])
                with self.assertRaises(FileExistsError):pub.prepare(Path('source'),Path('published'),Path('host'),output)
                self.assertEqual(1,prepare.call_count)


class PublicationControls(unittest.TestCase):
    def fixture(self):
        storage=tempfile.TemporaryDirectory(prefix='fourd-publication-controls-',dir='/tmp');self.addCleanup(storage.cleanup)
        api=FixtureAPI();api.directory=Path(storage.name)/'original';return api

    def publish(self,api):
        return pub.publish(api,api.identity,api.environment,api.directory)

    def test_four_writes_journal_before_send_and_public_readback(self):
        api=self.fixture();result=self.publish(api)
        self.assertTrue(result['publication']['publicationObserved']);self.assertFalse(result['adoptionSelected'])
        self.assertEqual(4,len(api.writes))
        journal=pub.closed((api.directory/'journal.json').read_bytes())
        self.assertEqual(['create-tag','create-draft','upload-archive','publish-release'],[w['kind'] for w in journal['writes']])
        self.assertTrue(all(w['state']=='confirmed' for w in journal['writes']))
        self.assertEqual(api.archive,(api.directory/pub.ARCHIVE).read_bytes())
        self.assertTrue(any('/releases/assets/' in path and public for _,path,public in api.calls))

    def test_unknown_at_each_write_seals_original_and_readback_never_resends(self):
        for point in range(1,5):
            with self.subTest(point=point):
                api=self.fixture();api.unknown_at=point
                with self.assertRaises(pub.Unknown):self.publish(api)
                journal=pub.closed((api.directory/'journal.json').read_bytes())
                self.assertEqual(point,len(api.writes));self.assertEqual('possible-send',journal['writes'][-1]['state'])
                with self.assertRaises(pub.Refused):self.publish(api)
                self.assertEqual(point,len(api.writes))
                observed=pub.public_readback(api,api.identity,pub.closed(api.manifest),allow_absent=True)
                self.assertEqual(point==4,observed['publicationObserved']);self.assertEqual(point,len(api.writes))

    def test_tag_collision_refuses_without_write(self):
        api=self.fixture();api.tag=dict(ref='refs/tags/'+pub.tag_name(SOURCE),object=dict(type='commit',sha=SOURCE))
        with self.assertRaisesRegex(pub.Refused,'collision'):self.publish(api)
        self.assertEqual([],api.writes)

    def test_original_readback_rejects_substituted_operation_release_or_asset(self):
        api=self.fixture();self.publish(api)
        facts=dict(releaseId=1000,authorId=56,assetId=1001)
        original=copy.deepcopy(api.release)
        self.assertTrue(pub.public_readback(api,api.identity,pub.closed(api.manifest),
                                          original_facts=facts)['publicationObserved'])
        for mutation in (lambda r:r.update(body='another operation'),lambda r:r.update(id=1002),
                         lambda r:r['author'].update(id=57),lambda r:r['assets'][0].update(id=1003)):
            api.release=copy.deepcopy(original);mutation(api.release)
            with self.assertRaises(pub.Refused):
                pub.public_readback(api,api.identity,pub.closed(api.manifest),original_facts=facts)
            self.assertEqual(4,len(api.writes))

    def test_source_drift_after_each_confirmed_write_stops_next_effect(self):
        for point in range(1,5):
            with self.subTest(point=point):
                api=self.fixture();api.drift_after=point
                with self.assertRaisesRegex(pub.Refused,'source/main drift'):self.publish(api)
                self.assertEqual(point,len(api.writes))

    def test_qualification_change_or_publisher_cancellation_stops_before_next_write(self):
        for path,change in ((PREFIX+'/actions/runs/'+str(RUN),dict(run_attempt=2)),
                            (PREFIX+'/actions/runs/'+str(PUBLISHER_RUN),dict(status='completed'))):
            api=self.fixture();original=api.request
            def request(method,endpoint,body=None,**options):
                result=original(method,endpoint,body,**options)
                if method!='GET' and len(api.writes)==1:api.rows[path].update(change)
                return result
            api.request=request
            with self.assertRaises(pub.Refused):self.publish(api)
            self.assertEqual(1,len(api.writes))

    def test_public_download_failure_cannot_report_publication_readback(self):
        api=self.fixture();api.wrong_download=True
        with self.assertRaises(pub.Refused):self.publish(api)
        self.assertEqual(4,len(api.writes))
        self.assertNotIn('publicReadback',pub.closed((api.directory/'journal.json').read_bytes()))

    def test_preflight_identity_mutations_are_causal_zero_write_controls(self):
        baseline=self.fixture();self.assertTrue(self.publish(baseline)['publication']['publicationObserved'])
        changes=[
            lambda a:a.rows[PREFIX+'/actions/runs/'+str(RUN)].update(event='pull_request'),
            lambda a:a.rows[PREFIX+'/actions/runs/'+str(RUN)].update(run_attempt=True),
            lambda a:a.rows[PREFIX+'/check-runs/701']['app'].update(id=99),
            lambda a:a.rows[PREFIX+'/actions/runs/'+str(RUN)+'/attempts/1/jobs?per_page=100']['jobs'][0]['steps'][1].update(conclusion='skipped'),
            lambda a:a.rows[PREFIX+'/actions/artifacts/'+str(ARTIFACT)].update(expired=True),
            lambda a:a.rows[PREFIX+'/actions/artifacts/'+str(ARTIFACT)].update(size_in_bytes=len(a.zipped)+1),
            lambda a:a.rows[PREFIX+'/actions/artifacts/'+str(ARTIFACT)]['workflow_run'].update(head_sha='4'*40),
            lambda a:a.rows[PREFIX+'/actions/workflows/'+pub.PUBLISHER.split('/')[-1]+'/runs?head_sha='+SOURCE+'&per_page=100'].update(total_count=2),
            lambda a:a.environment.update(GITHUB_RUN_ID='901'),
            lambda a:a.identity.update(manifestSha256='4'*64),
        ]
        for index,change in enumerate(changes):
            with self.subTest(index=index):
                api=self.fixture();change(api)
                with self.assertRaises(pub.Refused):self.publish(api)
                self.assertEqual([],api.writes)

    def test_readonly_preflight_has_no_writer_context_and_no_possible_send(self):
        api=self.fixture();directory=api.directory;arguments=['native_publication.py','preflight',
            '--source',SOURCE,'--qualification-run-id',str(RUN),'--artifact-id',str(ARTIFACT),
            '--archive-sha256',api.identity['archiveSha256'],'--manifest-sha256',api.identity['manifestSha256'],
            '--directory',str(directory)]
        output=io.StringIO()
        with mock.patch.object(sys,'argv',arguments),mock.patch.object(pub,'API',return_value=api),\
             mock.patch.dict(os.environ,{},clear=True),contextlib.redirect_stdout(output):
            self.assertEqual(0,pub.main())
        result=json.loads(output.getvalue());self.assertTrue(result['candidateVerified']);self.assertFalse(result['publicationSelected'])
        self.assertEqual([],api.writes);self.assertFalse((directory/'journal.json').exists())
        self.assertEqual('10.0.400',result['runtime']['sdkVersion'])

    def test_actual_readback_entrypoint_cannot_mutate_original_unknown(self):
        api=self.fixture();api.unknown_at=1
        with self.assertRaises(pub.Unknown):self.publish(api)
        output=io.StringIO();arguments=['native_publication.py','readback','--journal',str(api.directory/'journal.json'),
            '--candidate',str(api.directory)]
        with mock.patch.object(sys,'argv',arguments),mock.patch.object(pub,'API',return_value=api),\
             mock.patch.dict(os.environ,{},clear=True),contextlib.redirect_stdout(output):
            self.assertEqual(0,pub.main())
        self.assertTrue(json.loads(output.getvalue())['originalRecoveryOnly']);self.assertEqual(1,len(api.writes))


class TransportControls(unittest.TestCase):
    class Response:
        def __init__(self,status,body=b'',location=None):
            from email.message import Message
            self.status=status;self.body=io.BytesIO(body);self.headers=Message()
            if location:self.headers['Location']=location
        def __enter__(self):return self
        def __exit__(self,*unused):pass
        def read(self,size):return self.body.read(size)

    def test_artifact_redirect_never_carries_credential(self):
        observed=[];responses=[self.Response(302,location='https://github.com/fixture'),self.Response(200,b'fixture')]
        def open_request(request,timeout):
            observed.append(request);return responses.pop(0)
        opener=mock.Mock();opener.open.side_effect=open_request
        with mock.patch.object(pub.urllib.request,'build_opener',return_value=opener):
            status,raw=pub.API('fixture-secret',time.monotonic()+10).request('GET',PREFIX+'/actions/artifacts/800/zip',binary=True)
        self.assertEqual((200,b'fixture'),(status,raw));self.assertIn('Authorization',observed[0].headers)
        self.assertNotIn('Authorization',observed[1].headers)

    def test_write_redirect_and_foreign_read_redirect_are_not_followed(self):
        for method,location in (('POST','https://github.com/fixture'),('GET','https://foreign.invalid/fixture')):
            opener=mock.Mock();opener.open.return_value=self.Response(302,location=location)
            with mock.patch.object(pub.urllib.request,'build_opener',return_value=opener):
                with self.assertRaises(pub.Refused):pub.API('fixture-secret',time.monotonic()+10).request(method,PREFIX+'/releases',{})
            self.assertEqual(1,opener.open.call_count)

    def test_original_deadline_and_body_bound_refuse(self):
        with self.assertRaises(pub.Refused):pub.API('fixture',time.monotonic()-1).request('GET',PREFIX+'/releases')
        opener=mock.Mock();opener.open.return_value=self.Response(200,b'oversized')
        with mock.patch.object(pub.urllib.request,'build_opener',return_value=opener):
            with self.assertRaises(pub.Refused):pub.API('fixture',time.monotonic()+10).request('GET',PREFIX+'/releases',limit=2)


if __name__=='__main__':unittest.main()
