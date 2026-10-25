"""Pure, inert preflight of the actual C2 producer/artifact/base-consumer contract."""
import copy, hashlib, importlib.util, io, json, os, pathlib, stat, tempfile, unittest, zipfile, types
from unittest.mock import patch, Mock
ROOT=pathlib.Path(__file__).resolve().parents[3]
spec=importlib.util.spec_from_file_location('client',ROOT/'eng/c2_diagnostic_client.py');M=importlib.util.module_from_spec(spec);spec.loader.exec_module(M)
class Controls(unittest.TestCase):
    def fixture(self):
        source=M.revision();c=dict(repository=M.REPO,repositoryId=M.REPO_ID,run=101,attempt=1,source=source,runHead=source,workflow='.github/workflows/bootstrap-qualification.yml',producer='canonical-quint-prepare')
        binary=b'inert byte fixture, never executed';license=ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE'
        m=dict(schema='fsgg.c2-diagnostic-client-build/1',identity=M.inputs()['identity'],custody=c,inputsSha256=M.file_sha(M.INPUT),recipe=M.recipe(),binary=dict(path=M.BIN,bytes=len(binary),sha256=M.digest(binary)),buildRoot='/tmp/owned-build',license=dict(path='LICENSE',bytes=license.stat().st_size,sha256=M.file_sha(license)),preflight=dict(oldMissingExplanation=True,newRetainedExplanation=True,exitPreserved=True,oneCompileEach=True,functionControls=6,runtimeInventorySha256=M.inputs()['runtimeInventorySha256']))
        run=dict(id=101,run_attempt=1,head_sha=source,repository=dict(id=M.REPO_ID,full_name=M.REPO),path=c['workflow'],event='pull_request')
        job=dict(id=201,name=c['producer'],run_id=101,run_attempt=1,head_sha=source,status='completed',conclusion='success')
        jobs=dict(total_count=1,jobs=[job]);archive=self.archive(m,binary)
        a=dict(id=301,name=M.name(c),expired=False,size_in_bytes=len(archive),digest='sha256:'+M.digest(archive),workflow_run=dict(id=101,head_sha=source,repository_id=M.REPO_ID,head_repository_id=M.REPO_ID))
        return m,run,jobs,dict(total_count=1,artifacts=[a]),archive
    def archive(self,m,binary=b'inert byte fixture, never executed',extra=None):
        out=io.BytesIO()
        with zipfile.ZipFile(out,'w') as z:
            z.writestr('manifest.json',json.dumps(m));z.writestr(M.BIN,binary);z.writestr('LICENSE',(ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE').read_bytes())
            if extra:z.writestr(extra,b'unselected')
        return out.getvalue()
    def refused(self,fn):
        with self.assertRaises((ValueError,KeyError,TypeError,zipfile.BadZipFile)):fn()
    def test_exact_current_source_producer_upload_and_bytes_permit_staging_only(self):
        m,r,j,a,raw=self.fixture();M.validate_manifest(m,M.revision(),101)
        meta=M.native_join(m,r,j,a,M.revision(),101,301,M.digest(raw))
        with tempfile.TemporaryDirectory() as d,patch.dict(os.environ,GITHUB_RUN_ID='101'):
            out=pathlib.Path(d)/'new';M.extract(raw,meta,out)
            self.assertEqual((out/M.BIN).read_bytes(),b'inert byte fixture, never executed')
            self.assertEqual(stat.S_IMODE((out/M.BIN).stat().st_mode),0o700)
    def test_build_final_stage_cannot_publish_a_late_positive_manifest(self):
        m,*_=self.fixture()
        for late in (False,True):
            clock=[0.0];original=pathlib.Path.write_text
            def write(path,*args,**kwargs):
                result=original(path,*args,**kwargs)
                if late and path.name=='manifest.json':clock[0]=11.0
                return result
            with tempfile.TemporaryDirectory() as d,patch.object(M.time,'monotonic',side_effect=lambda:clock[0]),patch.object(pathlib.Path,'write_text',write):
                root=pathlib.Path(d);binary=root/'built';binary.write_bytes(b'inert byte fixture, never executed')
                if late:
                    with self.assertRaisesRegex(ValueError,'late build manifest'):M.finish_build(root/'artifact',binary,m,10)
                else:self.assertEqual(M.finish_build(root/'artifact',binary,m,10)['binary'],m['binary'])

    def test_late_final_process_completion_is_not_a_positive_native_read(self):
        clock=[0.0];out=Mock();err=Mock();out.fileno.return_value=31;err.fileno.return_value=32
        process=Mock(stdout=out,stderr=err);process.poll.return_value=0
        def wait(timeout):clock[0]=11.0;return 0
        process.wait.side_effect=wait
        class Selector:
            def __init__(self):self.entries={}
            def register(self,file,event,data):self.entries[file]=types.SimpleNamespace(fileobj=file,data=data)
            def unregister(self,file):self.entries.pop(file)
            def get_map(self):return self.entries
            def select(self,timeout):return [(v,None) for v in list(self.entries.values())]
            def close(self):self.entries.clear()
        with patch.object(M.time,'monotonic',side_effect=lambda:clock[0]),patch.object(M.subprocess,'Popen',return_value=process),patch.object(M.selectors,'DefaultSelector',Selector),patch.object(M.os,'read',return_value=b''):
            with self.assertRaisesRegex(ValueError,'late native process'):M.bounded_gh('fixed',32,10)
        process.wait.assert_called_once_with(timeout=10.0)

    def test_late_extraction_and_custody_never_authorize_formal_work(self):
        for phase in ('extract','custody'):
            m,r,j,a,raw=self.fixture();clock=[0.0];responses=[json.dumps(x).encode() for x in (r,j,a)]+[raw]
            env=dict(GITHUB_RUN_ID='101',GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY=M.REPO,FSGG_C2_ARTIFACT_ID='301',FSGG_C2_ARTIFACT_DIGEST=M.digest(raw))
            original_extract=M.extract;original_write=pathlib.Path.write_text
            def extract(*args):
                value=original_extract(*args)
                if phase=='extract':clock[0]=117.0
                return value
            def write(path,*args,**kwargs):
                value=original_write(path,*args,**kwargs)
                if path.name=='custody.json' and phase=='custody':clock[0]=117.0
                return value
            with patch.dict(os.environ,env),patch.object(M.time,'monotonic',side_effect=lambda:clock[0]),patch.object(M,'bounded_gh',side_effect=responses),patch.object(M,'extract',side_effect=extract),patch.object(pathlib.Path,'write_text',write),patch('builtins.print') as positive,tempfile.TemporaryDirectory() as d:
                with self.assertRaisesRegex(ValueError,'late consumer'):M.consume(pathlib.Path(d)/'new')
                positive.assert_not_called()

    def test_actual_consumer_stages_only_authenticated_archive_and_local_tamper_refuses(self):
        m,r,j,a,raw=self.fixture();responses=[json.dumps(x).encode() for x in (r,j,a)]+[raw]
        env=dict(GITHUB_RUN_ID='101',GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY=M.REPO,FSGG_C2_ARTIFACT_ID='301',FSGG_C2_ARTIFACT_DIGEST=M.digest(raw))
        with patch.dict(os.environ,env),patch.object(M,'bounded_gh',side_effect=responses),tempfile.TemporaryDirectory() as d:
            out=pathlib.Path(d)/'client';M.consume(out)
            os.environ['FSGG_C2_CLIENT_MANIFEST_SHA256']=M.file_sha(out/'manifest.json')
            profile=M.local_client(out);self.assertEqual(profile['binarySha256'],m['binary']['sha256'])
            (out/M.BIN).write_bytes(b'changed');self.refused(lambda:M.local_client(out))
            m['binary'].update(bytes=7,sha256=M.digest(b'changed'));(out/'manifest.json').write_text(json.dumps(m))
            custody=json.loads((out/'custody.json').read_text());custody['manifestSha256']=M.file_sha(out/'manifest.json');(out/'custody.json').write_text(json.dumps(custody))
            self.refused(lambda:M.local_client(out))

    def test_each_missing_stale_or_foreign_provenance_refuses_before_bytes(self):
        cases=[lambda m,r,j,a:m['custody'].update(source='a'*40),lambda m,r,j,a:m['custody'].update(attempt=2),lambda m,r,j,a:m['custody'].update(attempt=True),lambda m,r,j,a:r.update(run_attempt=2),lambda m,r,j,a:r.update(head_sha='b'*40),lambda m,r,j,a:r.update(path='foreign.yml'),lambda m,r,j,a:r['repository'].update(id=7),lambda m,r,j,a:j.update(total_count=2),lambda m,r,j,a:j['jobs'].append(copy.deepcopy(j['jobs'][0])),lambda m,r,j,a:j['jobs'][0].update(name='foreign-producer'),lambda m,r,j,a:j['jobs'][0].update(run_attempt=2),lambda m,r,j,a:j['jobs'][0].update(conclusion='failure'),lambda m,r,j,a:j['jobs'][0].update(head_sha='c'*40),lambda m,r,j,a:a['artifacts'][0].update(expired=True),lambda m,r,j,a:a['artifacts'][0]['workflow_run'].update(id=99),lambda m,r,j,a:a['artifacts'].append(copy.deepcopy(a['artifacts'][0]))]
        for change in cases:
            m,r,j,a,raw=self.fixture();change(m,r,j,a)
            with self.subTest(change=change):self.refused(lambda:M.native_join(m,r,j,a,M.revision(),101,301,M.digest(raw)))
        for identifier,digest in [(302,'a'*64),(301,'b'*64)]:
            m,r,j,a,raw=self.fixture();self.refused(lambda:M.native_join(m,r,j,a,M.revision(),101,identifier,digest))
    def test_manifest_recipe_inputs_and_preflight_cannot_authorize_arbitrary_paths(self):
        cases=[lambda m:m.update(extra='unknown'),lambda m:m.update(inputsSha256='a'*64),lambda m:m['recipe'].update({'foreign.py':'b'*64}),lambda m:m['binary'].update(path='../executable'),lambda m:m['binary'].update(path='/tmp/executable'),lambda m:m['binary'].update(bytes=True),lambda m:m['binary'].update(bytes=M.LIMIT+1),lambda m:m['preflight'].update(oldMissingExplanation=False),lambda m:m['preflight'].update(oneCompileEach=False),lambda m:m['preflight'].update(functionControls=6.0),lambda m:m['preflight'].update(runtimeInventorySha256='c'*64),lambda m:m['license'].update(path='../LICENSE')]
        for change in cases:
            m,*_=self.fixture();change(m)
            with self.subTest(change=change):self.refused(lambda:M.validate_manifest(m,M.revision(),101))
    def test_archive_digest_population_binary_change_or_symlink_refuse(self):
        m,r,j,a,raw=self.fixture()
        for changed in [raw+b'extra',b'not zip',self.archive(m,extra='../foreign'),self.archive(m,b'changed binary')]:
            meta=dict(a['artifacts'][0],size_in_bytes=len(changed),digest='sha256:'+M.digest(changed))
            with tempfile.TemporaryDirectory() as d,patch.dict(os.environ,GITHUB_RUN_ID='101'):
                self.refused(lambda:M.extract(changed,meta,pathlib.Path(d)/'new'))
        # A changed transport digest fails even if the embedded fields look valid.
        with tempfile.TemporaryDirectory() as d,patch.dict(os.environ,GITHUB_RUN_ID='101'):
            self.refused(lambda:M.extract(raw,dict(a['artifacts'][0],digest='sha256:'+'0'*64),pathlib.Path(d)/'new'))
        out=io.BytesIO()
        with zipfile.ZipFile(out,'w') as z:
            z.writestr('manifest.json',json.dumps(m));z.writestr('LICENSE',(ROOT/'eng/vendor/quint-c2-diagnostic/LICENSE').read_bytes());info=zipfile.ZipInfo(M.BIN);info.external_attr=(stat.S_IFLNK|0o777)<<16;z.writestr(info,b'target')
        raw=out.getvalue();meta=dict(a['artifacts'][0],size_in_bytes=len(raw),digest='sha256:'+M.digest(raw))
        with tempfile.TemporaryDirectory() as d,patch.dict(os.environ,GITHUB_RUN_ID='101'):
            self.refused(lambda:M.extract(raw,meta,pathlib.Path(d)/'new'))
    def test_consumption_requires_current_producer_outputs_before_binary_download(self):
        m,r,j,a,raw=self.fixture();responses=[r,j,a];calls=[]
        def read(endpoint,cap,end):
            calls.append(endpoint)
            return json.dumps(responses[len(calls)-1]).encode()
        env=dict(GITHUB_RUN_ID='101',GITHUB_RUN_ATTEMPT='1',GITHUB_REPOSITORY=M.REPO,FSGG_C2_ARTIFACT_ID='999',FSGG_C2_ARTIFACT_DIGEST=M.digest(raw))
        with patch.dict(os.environ,env),patch.object(M,'bounded_gh',side_effect=read),tempfile.TemporaryDirectory() as d:
            self.refused(lambda:M.consume(pathlib.Path(d)/'new'))
        self.assertEqual(len(calls),3,'foreign artifact must not be downloaded')
    def test_workflow_preflight_runs_once_and_only_base_consumes_without_build_credentials(self):
        for path,producer,consumer,condition in [('.github/workflows/bootstrap-qualification.yml','canonical-quint-prepare','canonical-quint-semantic',"matrix.shard == 'base'"),('eng/optimistic-parallel-validation.yml.template','formal-prepare','run-partition',"matrix.kind == 'formal' && matrix.shard == 'base'")]:
            text=(ROOT/path).read_text();self.assertEqual(text.count('c2_diagnostic_client.py build'),1);self.assertEqual(text.count('c2_diagnostic_client.py consume'),1)
            preparation=text.split('  '+producer+':',1)[1].split('\n  '+consumer+':',1)[0]
            self.assertIn('persist-credentials: false',preparation);self.assertNotIn('GH_TOKEN:',preparation)
            self.assertLess(preparation.index('c2_diagnostic_client.py build'),preparation.index('id: c2-upload'))
            block=text.split('      - name: Authenticate the current C2 client',1)[1].split('      - name: Run one',1)[0]
            self.assertIn(condition,block);self.assertIn('needs.'+producer+'.outputs.c2-artifact-id',block);self.assertIn('needs.'+producer+'.outputs.c2-artifact-digest',block)
        prepare=(ROOT/'eng/bootstrap-gates/optimistic-prepare.sh').read_text();self.assertIn('eng/c2_diagnostic_client.py',prepare);self.assertIn('eng/vendor/quint-c2-diagnostic/*',prepare)
    def test_public_profile_is_distinct_closed_and_legacy_identity_is_not_replaced(self):
        p=dict(identity='quint-0.32.0-fsgg-c2-diagnostic-1',source='a'*40,run=101,attempt=1,artifactId=301,artifactDigest='sha256:'+'b'*64,manifestSha256='c'*64,inputsSha256='d'*64,binarySha256='e'*64)
        self.assertEqual(M.profile_shape(p),p)
        for change in [dict(identity='0.32.0'),dict(attempt=2),dict(artifactId=True),dict(extra='unknown'),dict(binarySha256='bad')]:
            self.refused(lambda:M.profile_shape(dict(p,**change)))
        script=(ROOT/'eng/verify-choreo-c2-bounded.sh').read_text();self.assertIn('"$c2_quint_bin" verify',script);self.assertIn('FSGG_QUINT_BIN="$quint_bin" bash',script);self.assertIn('FSGG_QUINT_BIN="$quint_bin" python3',script)
if __name__=='__main__':unittest.main()
