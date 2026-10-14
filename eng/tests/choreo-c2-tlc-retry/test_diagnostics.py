"""Injected source controls only: no Quint, Java, CLR or model execution."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import unittest
import copy
import time
import hashlib
import contextlib
import io
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('choreo_diagnostics', ROOT / 'eng/retain-choreo-c2-diagnostics.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiagnosticControls(unittest.TestCase):
    def proc_fixture(self, root, mode='good'):
        """Declared proof adapters are inert fixtures, never runtime/effect acceptance."""
        operation='recover-execution-pair-030-b11fb121-original38495729817'
        # Only tests replace the producer constant. Neither production descriptor
        # nor a matching newly authored authority can alter this namespace.
        namespace=patch.object(module,'PROC_OPERATION_BASE',root/'producer-state')
        namespace.start();self.addCleanup(namespace.stop)
        module.PROC_OPERATION_BASE.mkdir(mode=0o700)
        base=module.PROC_OPERATION_BASE/str(os.geteuid());base.mkdir(mode=0o700)
        slot=module.proc_operation_root(operation);slot.mkdir(mode=0o700)
        client=root/'inert-curl'
        body={'workflow_run_id':42,'run_url':'https://api.github.com/repos/FS-GG/FS.GG.Coordination/actions/runs/42',
              'html_url':'https://github.com/FS-GG/FS.GG.Coordination/actions/runs/42'}
        if mode=='foreign':body['run_url']=body['run_url'].replace('Coordination/','Foreign/')
        if mode=='no-id':body={}
        extra=''
        if mode=='unexpected-read':
            foreign=root/'foreign-config';foreign.write_text('inert-only')
            extra='held=open('+repr(str(foreign))+')\n'
        if mode=='timeout':extra='time.sleep(20)\n'
        meta={'httpCode':'202' if mode=='202' else '200',
              'effectiveUrl':'https://api.github.com/repos/FS-GG/FS.GG.Coordination/actions/workflows/361038177/dispatches',
              'redirects':0,'sslVerifyResult':0}
        client.write_text('#!/usr/bin/python3\nimport sys,time,os\nif sys.argv[1:]==["--version"]:print("curl inert-source-control");sys.exit(0)\n'
                          +'assert "GH_TOKEN" not in os.environ\n'+extra+'time.sleep(.2)\n'+
                          ('sys.exit(7)\n' if mode=='lost' else
                           'open(sys.argv[sys.argv.index("--output")+1],"wb").write('+repr(json.dumps(body).encode())+')\n'
                           +'sys.stdout.write('+repr(json.dumps(meta)+'\n')+')\n'))
        client.chmod(0o700)
        for name in ('home','tmp'):(slot/name).mkdir(mode=0o700)
        support=module.proc_support_root();support.mkdir(mode=0o700);(support/'ca-empty').mkdir(mode=0o700)
        (support/'openssl.cnf').write_bytes(module.PROC_OPENSSL_CONFIG)
        ca=support/'trusted-ca.pem';ca.write_text('inert-fixture-ca')
        native={key:str(root/(key+'.json')) for key in ('admission','originalRun','originalJobs','originalArtifact',
                                                        'originalArchive','verifiedAttestation','sourceCorrespondence','runtimeQualification')}
        for name in native.values():Path(name).write_text('{}')
        admission={'enabled':False,'recovery':{'enabled':True,'policyObservationEnabled':False}}
        Path(native['admission']).write_text(json.dumps(admission))
        correspondence={'publisher':'d'*40,'source':'b11fb121f046898e655b779dfc6809f35fc7aac6',
                        'originalPublisher':'0ccfb6d067d912bbd8a1e26c4b56449358443b2e','protectedSourceVerified':True,
                        'aToEExactThreePaths':True,'aToPublisherExactElevenPaths':True}
        Path(native['sourceCorrespondence']).write_text(json.dumps(correspondence))
        consumer=root/'consumer-fixture';consumer.mkdir()
        for name in ('run.json','jobs.json',*(case+suffix for case in ('sdd-s1','fsquint-s1','sdd-rollback','fsquint-rollback')
                                            for suffix in ('.artifact.json','.job.log','.zip'))):
            (consumer/name).write_text('{}')
        pins=[]
        imports=[Path(getattr(m,field)).resolve() for m in list(os.sys.modules.values()) for field in ('__file__','__cached__')
                 if getattr(m,field,None) is not None and Path(getattr(m,field)).is_file()]
        import runpy
        imports.append(Path(runpy.__file__).resolve())
        for path in dict.fromkeys((client,ROOT/'eng/shared_process_owner.py',ROOT/'eng/retain-choreo-c2-diagnostics.py',
                     ROOT/'eng/test-callable-cli-installed-harness.py',ROOT/'eng/write-callable-cli-release-readback.py',
                     Path(os.path.realpath(os.sys.executable)),module.proc_support_root()/'openssl.cnf',ca,*(Path(p) for p in native.values()),*consumer.iterdir(),*imports)):
            row=module.pin_file(path);row['mode']=path.stat().st_mode&0o777;pins.append(row)
        native['consumerRoot']=str(root/'consumer-fixture')
        started=time.monotonic()
        spec={'schema':module.COMMAND_SCHEMA,'profile':'proc-pair-recovery-dispatch','role':'dispatch',
              'campaign':'unified-roadmap-20261003','item':'V2-PROC-01.6','attempt':'inert-source-control',
              'originalOperation':module.PROC_ORIGINAL,'operation':operation,'publisher':'d'*40,
              'repositoryId':1346720714,'workflowId':361038177,'workflowPath':module.PROC_WORKFLOW,
              'source':correspondence['source'],'originalPublisher':correspondence['originalPublisher'],
              'request':{'method':'POST','endpoint':'repos/FS-GG/FS.GG.Coordination/actions/workflows/361038177/dispatches',
                         'apiVersion':'2026-03-10','requestLimit':1,'body':{'ref':'main','inputs':{
                             'execution_pair':True,'pair_recovery':True,'preflight_only':False,'cli_only':False,'readback_only':False,
                             'publisher_source':'d'*40,'operation':operation,'source':correspondence['source'],
                             'protected_merge':correspondence['source'],'expected_sha256':'c8772596977e2eb172672fa393d78daab5867aefcf24aa9960a1c5627d4f7ca8'}}},
              'argv':[str(client)],'transport':{'client':str(client),'caBundle':str(ca),'tracer':str(client)},
              'readEvidence':{'contract':'ordinary-exact-profile-observation-and-drift','directories':[], 'aliases':[], 'absences':[],'runtimeData':copy.deepcopy(module.PROC_RUNTIME_DATA)},
              'pins':pins,'proofInputs':native,'sourceReadAllowlist':[p['path'] for p in pins],
              'environment':{'HOME':str(slot/'home'),'TMPDIR':str(slot/'tmp'),'PATH':'/usr/bin',
                             'LANG':'C','LC_ALL':'C','TZ':'UTC','OPENSSL_CONF':str(module.proc_support_root()/'openssl.cnf')},
              'operationRoot':str(slot),'qualificationAttempt':None,'started':started,'workDeadline':started+5,'totalDeadline':started+15,'cleanupSeconds':10,
              'rssBytes':512*1024**2,'addressSpaceBytes':512*1024**2,'logBytes':1024**2,'diskBytes':16*1024**2,'cpuAffinity':sorted(os.sched_getaffinity(0))}
        # Canonical-shaped inert evidence exercises joins, never supplies genuine gh qualification.
        capacity={'directory':'/sys/fs/cgroup','independentPrivateScope':True,'heldStoresUsed':False,'activeWorkers':None,
                  'identity':module.proc_read_identity('/sys/fs/cgroup'),'membership':Path('/proc/self/cgroup').read_text()}
        qualification={'rootAccepted':True,'publicationAuthorized':False,'capacity':capacity,'schema':module.RESULT_SCHEMA,'commandAccepted':True,'qualificationAccepted':True,'firstCause':None,'additionalFailures':[],'reportingOutcome':'written',
                       'ownerReceipt':{'cleanup':'observed-empty-group','custody':'observed-owned-group','exit':0,'firstCause':None,
                                       'leader':{'start':1},'residual':[],'escapedResidual':[],'escapedDescendants':[]},
                       'runtimeEvidence':{'currentBootId':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                                          'effectiveUid':os.geteuid(),'actualInterpreter':os.path.realpath(os.sys.executable),
                                          'interpreterFlags':{'isolated':1,'noSite':1,'noBytecode':True,'optimize':0},
                                          'canonicalBaseIdentity':module.proc_directory_identity(base),
                                          'nativeGetObserved':True,'nativeCollectorObserved':True,'qualifiedSourcePins':[pin for pin in pins if pin['path'].endswith('/retain-choreo-c2-diagnostics.py')],
                                          'supportPins':[pin for pin in pins if pin['path'] not in native.values()],
                                          'bounds':{k:spec[k] for k in ('cpuAffinity','rssBytes','addressSpaceBytes','logBytes','diskBytes')},
                                          'clientPath':str(client),'clientVersion':'curl inert-source-control','postRetryExcluded':True,
                                          'readContract':'ordinary-exact-profile-observation-and-drift','readEvidence':spec['readEvidence'],
                                          'traceComplete':True,'controllerStartupTraceComplete':True,'sourceAlternativesReviewSha256':'a'*64,
                                          'sourceReadAllowlist':spec['sourceReadAllowlist']}}
        Path(native['runtimeQualification']).write_text(json.dumps(qualification))
        row=module.pin_file(native['runtimeQualification']);row['mode']=Path(row['path']).stat().st_mode&0o777
        pins[:]=[row if pin['path']==row['path'] else pin for pin in pins]
        descriptor=root/'selected-input.json';descriptor.write_text(json.dumps(spec))
        (slot/'input.json').write_bytes(descriptor.read_bytes())
        (slot/'command.json').write_text(json.dumps({'schema':module.RESULT_SCHEMA,'operation':operation,
            'sendState':'prepared-no-send','descriptorSha256':hashlib.sha256(descriptor.read_bytes()).hexdigest()}))
        authority={'schema':module.COMMAND_SCHEMA,'descriptorSha256':hashlib.sha256(descriptor.read_bytes()).hexdigest(),
                   'profile':spec['profile'],'operation':operation,'operationRoot':str(slot),'approved':True,
                   'runtimeQualified':True,'readFenceQualified':True,'clientAutomaticPostRetry':False,
                   'canonicalSupportIdentity':module.proc_directory_identity(support),
                   'canonicalBaseIdentity':module.proc_directory_identity(base),'canonicalOperationIdentity':module.proc_directory_identity(slot),
                   'independentCapacityQualified':True,'sourceAlternativesReviewSha256':'a'*64,'runtimeQualification':qualification}
        approved=root/'selected-authority.json';approved.write_text(json.dumps(authority))
        return spec,descriptor,approved

    def test_proc_transport_one_use_and_uncertainty_keep_canonical_state(self):
        # Actual shared owner and subprocess; fake curl never reaches a network or credential.
        adapters={'pair_recovery_selection':lambda a:a['recovery'], 'pair_original_failure':lambda *a:{},
                  'pair_verified_attestation':lambda *a:{},'pair_consumers':lambda *a:{}}
        for mode in ('good','lost','202','foreign','no-id','unexpected-read','timeout'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);root.chmod(0o700)
                spec,descriptor,approved=self.proc_fixture(root,mode)
                sha=hashlib.sha256(approved.read_bytes()).hexdigest()
                with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}):
                    code=module.execute_proc_command(descriptor,approved,sha)
                    self.assertEqual(0 if mode=='good' else 1,code)
                    result=Path(spec['operationRoot'])/'command.json'
                    before=result.read_bytes();state=json.loads(before)
                    self.assertEqual('consumed-accepted' if mode=='good' else 'consumed-unknown',state['sendState'])
                    self.assertEqual('observed-empty-group',state['ownerReceipt']['cleanup'])
                    self.assertFalse(state['publicationAccepted'])
                    self.assertNotIn(b'fixture-only',before)
                    self.assertEqual('removed-after-qualified-retirement',state['sensitiveInputRetirement']['disposition'])
                    self.assertFalse((Path(spec['operationRoot'])/'authorization.header').exists())
                    self.assertEqual(1,module.execute_proc_command(descriptor,approved,sha))
                    self.assertEqual(before,result.read_bytes())

    def test_proc_preflight_refuses_exchange_drift_and_unqualified_runtime(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            module.proc_command_preflight(spec,now=time.monotonic())
            changes={'wrong-role':lambda s:s.update(role='watch'),'replay':lambda s:s.update(operation=module.PROC_ORIGINAL),
                     'argv':lambda s:s['argv'].append('arbitrary'),'method':lambda s:s['request'].update(method='GET'),
                     'deadline':lambda s:s.update(workDeadline=s['started']-1),'config':lambda s:s['environment'].update(GH_DEBUG='api'),
                     'read-fence':lambda s:s['sourceReadAllowlist'].append('/foreign'),'alternate-root':lambda s:s.update(operationRoot=str(root/'alternate'))}
            for name,change in changes.items():
                with self.subTest(name=name):
                    bad=copy.deepcopy(spec);change(bad)
                    with self.assertRaises(ValueError):module.proc_command_preflight(bad,now=time.monotonic())
            authority=json.loads(approved.read_bytes());authority['runtimeQualified']=False;approved.write_text(json.dumps(authority))
            self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            self.assertFalse((Path(spec['operationRoot'])/'dispatch-input.json').exists())

    def test_proc_actual_pin_drift_and_reporting_failure_preserve_send_slot(self):
        adapters={'pair_recovery_selection':lambda a:a['recovery'], 'pair_original_failure':lambda *a:{},
                  'pair_verified_attestation':lambda *a:{},'pair_consumers':lambda *a:{}}
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            Path(spec['argv'][0]).write_text('changed-runtime')
            self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            self.assertFalse((Path(spec['operationRoot'])/'dispatch-input.json').exists())
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            original=module.os.replace;writes=[]
            def refuse_final(source,target):
                if Path(target).name=='command.json':
                    writes.append(target)
                    if len(writes)==3:raise OSError('inert receipt failure')
                return original(source,target)
            with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}),patch.object(module.os,'replace',side_effect=refuse_final):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            result=json.loads((Path(spec['operationRoot'])/'command.json').read_bytes())
            self.assertEqual('send-started',result['sendState'])
            with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))

        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            write=module.os.write;close=module.os.close;advisory=io.StringIO()
            def report_fd(fd):
                try:return Path(os.readlink('/proc/self/fd/'+str(fd))).name=='command.json.pending'
                except OSError:return False
            def failed_write(fd,raw):
                if report_fd(fd):raise OSError('original canonical write failure')
                return write(fd,raw)
            def failed_close(fd):
                selected=report_fd(fd);close(fd)
                if selected:raise OSError('canonical close failure')
            with patch('runpy.run_path',return_value=adapters),patch.object(module.os,'write',side_effect=failed_write),patch.object(module.os,'close',side_effect=failed_close),contextlib.redirect_stderr(advisory):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            projected=json.loads(advisory.getvalue().splitlines()[-1])
            self.assertEqual('original canonical write failure',projected['firstCause']['message'])
            self.assertEqual([{'type':'OSError','message':'canonical close failure'}],projected['firstCause']['secondaryFailures'])
            slot=Path(spec['operationRoot'])
            self.assertEqual('prepared-no-send',json.loads((slot/'command.json').read_bytes())['sendState'])
            self.assertTrue((slot/'command.json.pending').exists());self.assertTrue((slot/'dispatch-input.json').exists())

    def test_proc_uncertain_native_correlation_never_reopens_send(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            spec['request']['runId']=None
            native={'id':42,'workflow_id':361038177,'head_sha':spec['publisher'],'event':'workflow_dispatch','run_attempt':1,
                    'repository':{'id':1346720714},'path':module.PROC_WORKFLOW,'head_branch':'main',
                    'display_title':'PROC '+spec['operation']+' @ '+spec['publisher'],'created_at':'2026-10-14T04:00:00Z'}
            import datetime
            epoch=datetime.datetime(2026,10,14,4,tzinfo=datetime.timezone.utc).timestamp()
            prior={'sendState':'consumed-unknown','nativeCreatedAfterEpoch':epoch,'nativeCreatedBeforeEpoch':epoch+90}
            self.assertEqual(native,module.proc_native_correlation(spec,prior,{'total_count':1,'workflow_runs':[native]}))
            for payload in ({'total_count':0,'workflow_runs':[]},{'total_count':2,'workflow_runs':[native,dict(native,id=43)]},
                            {'total_count':2,'workflow_runs':[native]},
                            {'total_count':1,'workflow_runs':[dict(native,display_title='foreign')]},
                            {'total_count':1,'workflow_runs':[dict(native,created_at='2026-10-14T03:00:00Z')]}):
                with self.assertRaises(ValueError):module.proc_native_correlation(spec,prior,payload)
            self.assertEqual('consumed-unknown',prior['sendState'])

    def test_proc_terminal_collection_preserves_independent_artifact_after_log_metadata_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            from types import SimpleNamespace
            archive=b'inert terminal archive';calls=[]
            def inert_run(argv,cwd,env,deadline,cleanup,rss,out,cap,**kwargs):
                target=argv[-1];calls.append(target);output=Path(out)
                if '/jobs?' in target:
                    output.write_bytes(b'inert causal failure')
                    output.with_suffix('.log.result.json').write_text(json.dumps({'cleanup':'observed-empty-group','custody':'observed-owned-group'}))
                    raise RuntimeError('inert original terminal job metadata failure')
                if '/artifacts?' in target:
                    value={'total_count':1,'artifacts':[{'id':71,'name':'execution-pair-publication-42-1','expired':False,
                           'workflow_run':{'id':42,'head_sha':spec['publisher']},'size_in_bytes':len(archive),
                           'digest':'sha256:'+hashlib.sha256(archive).hexdigest()}]}
                    raw=json.dumps(value).encode()
                else:raw=archive
                Path(argv[argv.index('--output')+1]).write_bytes(raw)
                output.write_text(json.dumps({'httpCode':'200','effectiveUrl':target,'redirects':0,'sslVerifyResult':0}))
                return {'cleanup':'observed-empty-group','custody':'observed-owned-group'}
            def inert_storage(owner,spec,slot,env,check,target,name,retirement):
                calls.append('explicit-storage-join')
                module.proc_charge_read(retirement,'api');module.proc_charge_read(retirement,'storage')
                return archive
            with patch.object(module,'proc_storage_read',side_effect=inert_storage):
                result=module.proc_terminal_evidence(SimpleNamespace(run=inert_run),spec,root,{},lambda *a:None,{'id':42},{})
            self.assertEqual('failed',result['records']['terminal-jobs']['state'])
            self.assertEqual('collected',result['records']['terminal-archive']['state'])
            self.assertIn('original terminal job metadata failure',result['additionalFailures'][0]['cause']['message'])
            self.assertEqual(3,len(calls))
            self.assertFalse(result['publicationAccepted'])

    def test_proc_fixed_producer_location_refuses_new_authority_under_another_parent(self):
        adapters={'pair_recovery_selection':lambda a:a['recovery'], 'pair_original_failure':lambda *a:{},
                  'pair_verified_attestation':lambda *a:{},'pair_consumers':lambda *a:{}}
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            sha=hashlib.sha256(approved.read_bytes()).hexdigest()
            with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}):
                self.assertEqual(0,module.execute_proc_command(descriptor,approved,sha))
            state=Path(spec['operationRoot'])/'command.json';original=state.read_bytes()
            alternate=root/'different-parent';alternate.mkdir(mode=0o700)
            moved=copy.deepcopy(spec);new_slot=alternate/spec['operation'];moved['operationRoot']=str(new_slot)
            moved['argv'][-1]=str(new_slot/'dispatch-input.json')
            for key,value in moved['environment'].items():moved['environment'][key]=value.replace(spec['operationRoot'],str(new_slot))
            relocated=alternate/'input.json';relocated.write_text(json.dumps(moved))
            authority=json.loads(approved.read_bytes());authority['operationRoot']=str(new_slot)
            authority['descriptorSha256']=hashlib.sha256(relocated.read_bytes()).hexdigest()
            other_authority=alternate/'authority.json';other_authority.write_text(json.dumps(authority))
            with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}):
                self.assertEqual(1,module.execute_proc_command(relocated,other_authority,hashlib.sha256(other_authority.read_bytes()).hexdigest()))
            self.assertFalse(new_slot.exists());self.assertEqual(original,state.read_bytes())

    def test_proc_canonical_base_and_known_state_loss_refuse_without_recreation(self):
        adapters={'pair_recovery_selection':lambda a:a['recovery'], 'pair_original_failure':lambda *a:{},
                  'pair_verified_attestation':lambda *a:{},'pair_consumers':lambda *a:{}}
        for mode in ('base-mode','base-inode','base-alias','missing-input','missing-result','missing-slot'):
            with self.subTest(mode=mode),tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
                slot=Path(spec['operationRoot']);base=slot.parent;sha=hashlib.sha256(approved.read_bytes()).hexdigest()
                if mode=='base-mode':base.chmod(0o755)
                if mode=='base-inode':
                    previous=base.with_name('previous-base');base.rename(previous);base.mkdir(mode=0o700)
                    (previous/slot.name).rename(slot)
                if mode=='base-alias':
                    previous=base.with_name('previous-base');base.rename(previous);base.symlink_to(previous,target_is_directory=True)
                if mode=='missing-input':(slot/'input.json').unlink()
                if mode=='missing-result':(slot/'command.json').unlink()
                if mode=='missing-slot':
                    shutil.rmtree(slot)
                with patch('runpy.run_path',return_value=adapters),patch.dict(os.environ,{'GH_TOKEN':'fixture-only'}):
                    self.assertEqual(1,module.execute_proc_command(descriptor,approved,sha))
                self.assertFalse((slot/'dispatch-input.json').exists())
                if mode=='missing-slot':self.assertFalse(slot.exists())

    def test_proc_actual_optimized_interpreter_refuses_before_slot_or_child(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            state=Path(spec['operationRoot'])/'command.json';before=state.read_bytes()
            result=subprocess.run([os.sys.executable,'-I','-S','-B','-O',str(ROOT/'eng/retain-choreo-c2-diagnostics.py'),
                'command','--proc',str(descriptor),str(approved),hashlib.sha256(approved.read_bytes()).hexdigest()],
                capture_output=True,timeout=5)
            self.assertEqual(1,result.returncode,result.stderr)
            self.assertIn(b'optimize=0',result.stderr)
            self.assertEqual(before,state.read_bytes())
            self.assertFalse((Path(spec['operationRoot'])/'dispatch-input.json').exists())

    def test_proc_fixed_curl_argv_and_sensitive_input_retirement(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            slot=Path(spec['operationRoot'])
            argv=module.proc_curl_argv(spec,slot,spec['request']['endpoint'],'POST',body=slot/'dispatch-input.json')
            self.assertEqual('-q',argv[1]);self.assertEqual('https://api.github.com/'+spec['request']['endpoint'],argv[-1])
            self.assertIn('Expect:',argv);self.assertIn('@'+str(slot/'authorization.header'),argv)
            for forbidden in ('--location','-L','--retry','--anyauth','--netrc','--config','--parallel','--next','--user'):
                self.assertNotIn(forbidden,argv)
            header=module.proc_sensitive_header(slot,'inert-sensitive-source-control')
            self.assertEqual(0o600,(slot/'authorization.header').stat().st_mode&0o777)
            self.assertNotIn('inert-sensitive',json.dumps({k:v for k,v in header.items() if not k.startswith('_')}));self.assertNotIn('sha256',header)
            with self.assertRaises(FileExistsError):module.proc_sensitive_header(slot,'second-inert-value')
            self.assertEqual('held-uncertain-retirement',module.proc_sensitive_retire(header,{'cleanup':'unknown'})['disposition'])
            self.assertTrue((slot/'authorization.header').exists())
            receipt={'cleanup':'observed-empty-group','custody':'observed-owned-group'}
            self.assertEqual('removed-after-qualified-retirement',module.proc_sensitive_retire(header,receipt)['disposition'])
            for invalid in ('line\nbreak','line\rbreak','nul\0break',''):
                with self.assertRaises(ValueError):module.proc_sensitive_header(slot,invalid)

    def test_proc_typed_read_drift_observes_alias_population_and_absence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            selected=root/'bounded-support';selected.mkdir();file=selected/'file';file.write_text('owned support')
            alias=root/'support-alias';alias.symlink_to(file)
            identity=module.proc_read_identity
            spec['readEvidence']={'contract':'ordinary-exact-profile-observation-and-drift','runtimeData':copy.deepcopy(module.PROC_RUNTIME_DATA),
                'directories':[{'path':str(selected),'identity':identity(selected),'relevantEntries':{'file':identity(file)},'completePopulation':True}],
                'aliases':[{'path':str(alias),'target':str(file),'resolved':str(file),'identity':identity(alias)}],
                'absences':[module.proc_absence(root/'absent-config')]}
            module.proc_read_drift(spec)
            (selected/'unexpected').write_text('new support')
            with self.assertRaisesRegex(ValueError,'population'):module.proc_read_drift(spec)
            (selected/'unexpected').unlink();(root/'absent-config').write_text('new config')
            with self.assertRaisesRegex(ValueError,'negative'):module.proc_read_drift(spec)
            (root/'absent-config').unlink();alias.unlink();alias.symlink_to(selected/'foreign')
            with self.assertRaises(ValueError):module.proc_read_drift(spec)

    def test_proc_negative_intermediate_directory_and_alias_drift(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            path=root/'missing'/'nested'/'socket'
            row=module.proc_absence(path)
            self.assertEqual(['missing','nested','socket'],row['missingComponents'])
            spec['readEvidence']['absences']=[row];module.proc_read_drift(spec)
            (root/'missing').mkdir()
            with self.assertRaisesRegex(ValueError,'missing chain'):module.proc_read_drift(spec)
            (root/'missing').rmdir();(root/'missing').symlink_to(root)
            with self.assertRaises(ValueError):module.proc_read_drift(spec)

    def test_proc_sensitive_creation_failure_is_owned_or_held_and_never_loops(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            for operation in ('write','fsync'):
                failure=OSError('original inert '+operation+' failure')
                with patch.object(module.os,operation,side_effect=failure):
                    with self.assertRaises(OSError) as raised:module.proc_sensitive_header(root,'inert-value')
                self.assertIs(failure,raised.exception)
                self.assertEqual('removed-before-child',failure.proc_sensitive_creation['disposition'])
                self.assertFalse((root/'authorization.header').exists())
            with patch.object(module.os,'write',return_value=0):
                with self.assertRaisesRegex(OSError,'zero write'):module.proc_sensitive_header(root,'inert-value')
            failure=OSError('preserve original write failure')
            with patch.object(module.os,'write',side_effect=failure),patch.object(module.Path,'unlink',side_effect=OSError('inert retirement denied')):
                with self.assertRaises(OSError) as raised:module.proc_sensitive_header(root,'inert-value')
            self.assertIs(failure,raised.exception)
            self.assertEqual('held-creation-failure',failure.proc_sensitive_creation['disposition'])
            self.assertTrue((root/'authorization.header').exists())
            (root/'authorization.header').unlink()
            close=module.os.close
            def close_failure(fd):close(fd);raise OSError('original inert close failure')
            with patch.object(module.os,'close',side_effect=close_failure):
                with self.assertRaisesRegex(OSError,'original inert close') as raised:module.proc_sensitive_header(root,'inert-value')
            self.assertEqual('removed-before-child',raised.exception.proc_sensitive_creation['disposition'])
            failure=OSError('original inert write before close failure')
            with patch.object(module.os,'write',side_effect=failure),patch.object(module.os,'close',side_effect=close_failure):
                with self.assertRaises(OSError) as raised:module.proc_sensitive_header(root,'inert-value')
            self.assertIs(failure,raised.exception)
            self.assertIn('original inert close',failure.proc_sensitive_close_failure['message'])
            self.assertFalse((root/'authorization.header').exists())

    def test_proc_qualification_birth_reuse_and_parent_reuse_refuse(self):
        known={10:100};member={'pid':11,'ppid':10,'start':200}
        records={10:{'pid':10,'start':100},11:member}
        module.proc_join_qualification_birth(member,known,records.get)
        self.assertEqual(200,known[11])
        records[11]=dict(member,start=201)
        with self.assertRaisesRegex(ValueError,'PID birth reuse'):
            module.proc_join_qualification_birth(dict(member,start=201),known,records.get)
        known={10:100};records[10]['start']=101;records[11]=member
        with self.assertRaisesRegex(ValueError,'parent/birth'):
            module.proc_join_qualification_birth(member,known,records.get)
        self.assertNotIn(11,known)

    def test_proc_effect_capacity_claim_cannot_replace_live_enclosing_checks(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            runtime=Path(spec['proofInputs']['runtimeQualification'])
            qualification=json.loads(runtime.read_bytes());qualification['capacity']['membership']='foreign'
            runtime.write_text(json.dumps(qualification))
            pin=module.pin_file(runtime);pin['mode']=runtime.stat().st_mode&0o777
            spec['pins']=[pin if row['path']==str(runtime) else row for row in spec['pins']]
            descriptor.write_text(json.dumps(spec));authority=json.loads(approved.read_bytes())
            authority.update(descriptorSha256=hashlib.sha256(descriptor.read_bytes()).hexdigest(),runtimeQualification=qualification)
            approved.write_text(json.dumps(authority))
            slot=Path(spec['operationRoot']);before=(slot/'command.json').read_bytes()
            with contextlib.redirect_stderr(io.StringIO()),patch('runpy.run_path',return_value={'pair_recovery_selection':lambda a:a['recovery']}):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            self.assertEqual(before,(slot/'command.json').read_bytes());self.assertFalse((slot/'dispatch-input.json').exists())

    def test_proc_state_byte_cap_keeps_exclusive_claim_and_launches_no_child(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            slot=Path(spec['operationRoot']);before=(slot/'command.json').read_bytes()
            adapters={'pair_recovery_selection':lambda a:a['recovery'],
                      'pair_original_failure':lambda *a:{'oversized':'x'*module.MAX_OUTPUT},
                      'pair_verified_attestation':lambda *a:{},'pair_consumers':lambda *a:{}}
            with patch('runpy.run_path',return_value=adapters):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            self.assertEqual(before,(slot/'command.json').read_bytes());self.assertTrue((slot/'dispatch-input.json').exists())
            self.assertFalse((slot/'client-preflight.log').exists());self.assertFalse((slot/'authorization.header').exists())

    def test_proc_kernel_data_is_census_data_and_closed_actor_metadata(self):
        classify=module.proc_kernel_data_read;actors={10,11};args=(10,actors,9,True,set())
        self.assertTrue(classify('/proc/999/stat','openat(AT_FDCWD, "stat", O_RDONLY) = 3',*args))
        self.assertFalse(classify('/proc/999/status','openat(AT_FDCWD, "status", O_RDONLY) = 3',*args))
        self.assertTrue(classify('/proc/11/exe','readlink("exe", "/pinned/python", 4096) = 10',*args))
        for path in ('/proc/11/environ','/proc/11/mem','/proc/11/root/file','/proc/999/ns/pid'):
            self.assertFalse(classify(path,'openat(AT_FDCWD, "file", O_RDONLY) = 3',*args))
        self.assertFalse(classify('/proc/11/fd/3','openat(AT_FDCWD, "fd/3", O_RDONLY) = 3',*args))

    def storage_fixture_url(self,archive=False):
        from datetime import datetime,timezone
        now=int(time.time());stamp=lambda seconds:datetime.fromtimestamp(seconds,timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
        fields={key:'inert' for key in module.PROC_STORAGE_KEYS}
        fields.update(sp='r',spr='https',sr='b',sv='2025-11-05',st=stamp(now-60),se=stamp(now+600),sig='SENSITIVE-SENTINEL')
        if archive:fields['rscd']='inert'
        url='https://'+module.PROC_STORAGE_HOST+'/closed/object?'+ '&'.join(key+'='+value for key,value in sorted(fields.items()))
        date=datetime.fromtimestamp(now,timezone.utc).strftime('%a, %d %b %Y %H:%M:%S GMT')
        return url,date,now

    def test_proc_storage_grammar_counter_and_private_argv(self):
        url,date,now=self.storage_fixture_url()
        self.assertGreater(module.proc_storage_location(url,archive=False,api_date=date,now_epoch=now),0)
        for bad in (url.replace(module.PROC_STORAGE_HOST,'foreign.invalid'),url+'&sig=second',url.replace('sp=r&','sp=rw&'),
                    url.replace('sv=2025-11-05','sv=unknown'),url+'#fragment',url.replace('https://','https://user@'),url.replace('/closed/','/../')):
            with self.assertRaises(ValueError) as raised:module.proc_storage_location(bad,archive=False,api_date=date,now_epoch=now)
            self.assertNotIn('SENSITIVE-SENTINEL',str(raised.exception))
        with tempfile.TemporaryDirectory() as tmp:
            spec,_,_=self.proc_fixture(Path(tmp));slot=Path(spec['operationRoot'])
            argv=module.proc_storage_argv(spec,slot,'terminal-job',5)
            self.assertNotIn(url,json.dumps(argv));self.assertNotIn('SENSITIVE-SENTINEL',json.dumps(argv))
            self.assertNotIn('--show-error',argv);self.assertNotIn('%{url_effective}',json.dumps(argv))
            self.assertNotIn('@'+str(slot/'authorization.header'),argv);self.assertEqual('-q',argv[1])
        retirement={}
        for kind in ('api',)*5+('storage',)*2:module.proc_charge_read(retirement,kind)
        self.assertEqual({'api':5,'storage':2,'total':7},retirement['readCounts'])
        with self.assertRaises(ValueError):module.proc_charge_read(retirement,'storage')

    def test_proc_archive_controls_are_bounded_crc_and_safe_paths(self):
        import zipfile
        def archive(name):
            target=io.BytesIO()
            with zipfile.ZipFile(target,'w') as writer:writer.writestr(name,b'inert native archive control')
            return target.getvalue()
        module.proc_archive_shape(archive('proof.json'),deadline=time.monotonic()+1)
        for name in ('../outside','/absolute','nested\\outside'):
            with self.assertRaises(ValueError):module.proc_archive_shape(archive(name))
        with self.assertRaises(TimeoutError):module.proc_archive_shape(archive('proof.json'),deadline=time.monotonic()-1)
        for status,classification in (('401','access-authentication-refused'),('403','storage-access-indeterminate'),
                                      ('404','native-evidence-unavailable'),('410','native-evidence-expired'),('302','second-redirect-refused')):
            error=module.proc_read_failure('storage',status,'inert refusal')
            self.assertEqual(classification,error.proc_read_status['disposition'])
        error=module.proc_read_failure('storage','SENSITIVE-SENTINEL','inert refusal')
        self.assertIsNone(error.proc_read_status['httpStatus'])
        self.assertNotIn('SENSITIVE-SENTINEL',json.dumps(error.proc_read_status))

    def test_proc_storage_read_uses_actual_owner_and_separated_inert_children(self):
        # Owned Python transport adapter only: no curl, TLS, tracer or network.
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,_=self.proc_fixture(root)
            slot=Path(spec['operationRoot']);url,date,_=self.storage_fixture_url()
            client=Path(spec['transport']['client'])
            client.write_text('#!/usr/bin/python3\nimport sys,json,os\na=sys.argv\nassert "GH_TOKEN" not in os.environ\n'
                +'out=a[a.index("--output")+1]\n'
                +'if "--dump-header" in a:\n'
                +' assert "--config" not in a\n'
                +' open(a[a.index("--dump-header")+1],"wb").write('+repr(('HTTP/1.1 302 Found\r\nDate: '+date+'\r\nLocation: '+url+'\r\n\r\n').encode())+')\n'
                +' open(out,"wb").write(b"SENSITIVE-SENTINEL")\n'
                +' print(json.dumps({"httpCode":"302","effectiveUrl":a[-1],"redirects":0,"sslVerifyResult":0}))\n'
                +'else:\n'
                +' assert "--config" in a and not any("authorization.header" in v for v in a)\n'
                +' assert "SENSITIVE-SENTINEL" in open(a[a.index("--config")+1]).read()\n'
                +' open(out,"wb").write(b"inert log")\n'
                +' print(json.dumps({"httpCode":"200","redirects":0,"sslVerifyResult":0,"bytes":9}))\n')
            owner_spec=importlib.util.spec_from_file_location('inert_storage_owner',ROOT/'eng/shared_process_owner.py')
            owner=importlib.util.module_from_spec(owner_spec);owner_spec.loader.exec_module(owner)
            retirement={};argv_seen=[]
            class Capture:
                def run(self,argv,*args,**kwargs):argv_seen.append(argv);return owner.run(argv,*args,**kwargs)
            raw=module.proc_storage_read(Capture(),spec,slot,spec['environment'],lambda *args:None,
                'repos/FS-GG/FS.GG.Coordination/actions/jobs/1/logs','terminal-job',retirement)
            self.assertEqual(b'inert log',raw);self.assertEqual(2,len(argv_seen))
            self.assertNotIn('SENSITIVE-SENTINEL',json.dumps(argv_seen)+json.dumps(retirement))
            self.assertFalse((slot/'storage-url.conf').exists());self.assertFalse((slot/'terminal-job.headers').exists())
            self.assertEqual({'api':1,'storage':1,'total':2},retirement['readCounts'])
            # Same real API-child lifecycle, then a deterministic parse refusal.
            # Raw redirect body must retire even though the storage child never runs.
            retirement={};argv_seen.clear();negative=slot/'negative-source-control';negative.mkdir(mode=0o700)
            with patch.object(module,'proc_storage_location',side_effect=ValueError('original inert location refusal')):
                with self.assertRaisesRegex(ValueError,'original inert location'):
                    module.proc_storage_read(Capture(),spec,negative,spec['environment'],lambda *args:None,
                        'repos/FS-GG/FS.GG.Coordination/actions/jobs/1/logs','terminal-job',retirement)
            self.assertEqual(1,len(argv_seen));self.assertFalse((negative/'terminal-job-api.body').exists())
            self.assertFalse((negative/'terminal-job.headers').exists());self.assertFalse((negative/'storage-url.conf').exists())
            self.assertNotIn('SENSITIVE-SENTINEL',json.dumps(retirement)+json.dumps(argv_seen))
            self.assertEqual('removed-after-qualified-child-retirement',retirement['redirectBodyDisposition'])

    def test_proc_qualification_trace_has_complete_births_and_refuses_unknown_paths(self):
        # Parser controls are inert metadata, never authentic qualification evidence.
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,approved=self.proc_fixture(root)
            bootstrap=root/'bootstrap.json';bootstrap.write_text(json.dumps({'capacity':{'directory':'/sys/fs/cgroup'}}))
            spec['proofInputs']['bootstrapEvidence']=str(bootstrap)
            slot=Path(spec['operationRoot']);python=os.path.realpath(os.sys.executable);client=spec['transport']['client']
            parent=slot/'qualification.trace.10';child=slot/'qualification.trace.11'
            parent.write_text('execve('+json.dumps(python)+', ["python", "-I"], 0x0 /* 7 vars */) = 0\nclone(child_stack=NULL, flags=SIGCHLD) = 11\n+++ exited with 0 +++\n')
            child.write_text('execve('+json.dumps(client)+', ["curl", "-q"], 0x0 /* 7 vars */) = 0\n+++ exited with 52 +++\n')
            self.assertTrue(module.proc_trace_evidence(spec,slot,approved)['complete'])
            original=child.read_text()
            child.write_text(original.replace('+++ exited', 'openat(AT_FDCWD, "/foreign/config", O_RDONLY) = 3</foreign/config>\n+++ exited'))
            with self.assertRaisesRegex(ValueError,'outside bootstrap'):module.proc_trace_evidence(spec,slot,approved)
            child.write_text(original);child.unlink()
            with self.assertRaisesRegex(ValueError,'birth join'):module.proc_trace_evidence(spec,slot,approved)

    def test_proc_trace_closed_grammar_cwd_raw_reads_and_exec_fd_lifetime(self):
        # Exact parser controls only, never native trace/qualification evidence.
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,_,approved=self.proc_fixture(root)
            bootstrap=root/'bootstrap.json';bootstrap.write_text(json.dumps({'capacity':{'directory':'/sys/fs/cgroup'}}))
            spec['proofInputs']['bootstrapEvidence']=str(bootstrap);slot=Path(spec['operationRoot'])
            python=os.path.realpath(os.sys.executable);client=spec['transport']['client'];header=str(slot/'authorization.header')
            parent=slot/'qualification.trace.10';child=slot/'qualification.trace.11'
            begin='execve('+json.dumps(python)+', ["python", "-I"], 0x0 /* 7 vars */) = 0\n'
            finish='+++ exited with 0 +++\n'
            for syscall in ('getxattr("/foreign/secret", "user.value", NULL, 0) = 4','truncate("/foreign/state", 0) = 0',
                            'unmodelled_file_call("/foreign", 0) = 0',
                            'openat(AT_FDCWD, "/foreign", O_RDONLY <unfinished ...>\n<... execve resumed>) = 3'):
                parent.write_text(begin+syscall+'\n'+finish)
                with self.assertRaises(ValueError):module.proc_trace_evidence(spec,slot,approved)
            parent.write_text(begin+'openat(AT_FDCWD<'+str(slot)+'>, "input.json", O_RDONLY) = 3<'+str(slot/'input.json')+'>\n'+finish)
            self.assertTrue(module.proc_trace_evidence(spec,slot,approved)['complete'])
            for flag in ('O_CLOEXEC','0'):
                parent.write_text(begin+'openat(AT_FDCWD, '+json.dumps(header)+', O_RDONLY|'+flag+') = 3<'+header+'>\nclone(child_stack=NULL, flags=SIGCHLD) = 11\n'+finish)
                child.write_text('execve('+json.dumps(client)+', ["curl", "-q"], 0x0 /* 7 vars */) = 0\nread(0x3, 0x1000, 0x40) = 0x20\n'+finish)
                if flag=='O_CLOEXEC':
                    with self.assertRaisesRegex(ValueError,'descriptor lifetime'):module.proc_trace_evidence(spec,slot,approved)
                else:self.assertEqual(11,module.proc_trace_evidence(spec,slot,approved)['semanticReads'][0]['pid'])
            child.unlink()
            # CLONE_FILES shares a table until this actor explicitly unshares it.
            measured=[python,'-I','-S','-B',str(ROOT/'eng/retain-choreo-c2-diagnostics.py'),'command','--proc-measured-controller',str(slot/'input.json'),str(approved),hashlib.sha256(approved.read_bytes()).hexdigest(),'9','normal']
            timed_begin='1.000 execve('+json.dumps(python)+', '+json.dumps(measured)+', 0x0 /* 7 vars */) = 0\n'
            parent.write_text(timed_begin+'1.100 openat(AT_FDCWD, '+json.dumps(header)+', O_RDONLY) = 3<'+header+'>\n1.200 clone(child_stack=NULL, flags=CLONE_FILES|SIGCHLD) = 11\n3.000 read(0x3, 0x1000, 0x40) = 0x20\n4.000 '+finish)
            child.write_text('2.000 close_range(3, ~0U, CLOSE_RANGE_UNSHARE) = 0\n2.100 '+finish)
            self.assertEqual(10,module.proc_trace_evidence(spec,slot,approved)['semanticReads'][0]['pid'])
            child.write_text('2.000 close_range(3, ~0U, 0) = 0\n2.100 '+finish)
            with self.assertRaisesRegex(ValueError,'descriptor lifetime'):module.proc_trace_evidence(spec,slot,approved)
            child.unlink()
            spec['role']='qualification'
            argv=module.proc_trace_argv(spec,slot,slot/'input.json',approved,'a'*64,3,'normal')
            selected=next(arg for arg in argv if arg.startswith('trace=')).split('=',1)[1].split(',')
            self.assertIn('close_range',selected);self.assertIn('fcntl64',selected)
            parent.write_text(begin+'openat(AT_FDCWD, '+json.dumps(header)+', O_RDONLY) = 3<'+header+'>\nfcntl(3<'+header+'>, F_DUPFD, 10) = 10\nread(0xa, 0x1000, 0x40) = 0x20\n'+finish)
            self.assertEqual(10,module.proc_trace_evidence(spec,slot,approved)['semanticReads'][0]['fd'])
            for read in ('readv(0x3, 0x1000, 0) = 0x20','read(0x3, 0x1000, 0) = 0x20','read(0x3, "SENSITIVE-SENTINEL", 0x40) = 0x20'):
                parent.write_text(begin+'openat(AT_FDCWD, '+json.dumps(header)+', O_RDONLY) = 3<'+header+'>\n'+read+'\n'+finish)
                with self.assertRaises(ValueError) as raised:module.proc_trace_evidence(spec,slot,approved)
                self.assertNotIn('SENSITIVE-SENTINEL',str(raised.exception))

    def test_proc_private_reporting_refuses_cap_deadline_collision_and_retains_partial(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);path=root/'record.json'
            with self.assertRaises(ValueError):module.proc_private_record(path,{'large':'x'*module.MAX_OUTPUT},time.monotonic()+1,exclusive=True)
            self.assertFalse(path.exists())
            with self.assertRaises(TimeoutError):module.proc_private_record(path,{},time.monotonic()-1,exclusive=True)
            path.write_text('original')
            with self.assertRaises(FileExistsError):module.proc_private_record(path,{},time.monotonic()+1,exclusive=True)
            self.assertEqual('original',path.read_text());path.unlink()
            with patch.object(module.os,'write',return_value=0):
                with self.assertRaises(OSError) as raised:module.proc_private_record(path,{},time.monotonic()+1,exclusive=True)
            self.assertEqual('held-partial-or-completed-report',raised.exception.proc_private_report['disposition'])
            self.assertTrue(path.exists());path.unlink()
            # A completed rename followed by an expired deadline holds the actual
            # promoted inode, never the obsolete temporary pathname.
            clock=[time.monotonic()];deadline=clock[0]+1;replace=module.os.replace
            def late_replace(source,target):replace(source,target);clock[0]=deadline+1
            with patch.object(module.time,'monotonic',side_effect=lambda:clock[0]),patch.object(module.os,'replace',side_effect=late_replace):
                with self.assertRaises(TimeoutError) as raised:module.proc_private_record(path,{'complete':True},deadline)
            self.assertEqual(str(path),raised.exception.proc_private_report['path'])
            self.assertTrue(path.exists());self.assertFalse(path.with_name(path.name+'.pending').exists())
            self.assertEqual(module.proc_read_identity(path),raised.exception.proc_private_report['identity'])
            path.unlink();close=module.os.close
            def close_then_fail(fd):close(fd);raise OSError('inert writer close failure')
            with patch.object(module.os,'write',side_effect=OSError('original inert write failure')),patch.object(module.os,'close',side_effect=close_then_fail):
                with self.assertRaisesRegex(OSError,'original inert write failure') as raised:
                    module.proc_private_record(path,{},time.monotonic()+1,exclusive=True)
            projected=module.failure_record(raised.exception)
            self.assertEqual('original inert write failure',projected['message'])
            self.assertEqual([{'type':'OSError','message':'inert writer close failure'}],projected['secondaryFailures'])
            self.assertEqual(str(path),raised.exception.proc_private_report['path']);self.assertTrue(path.exists())

    def test_proc_subject_failure_survives_apparatus_reporting_and_collector_collision(self):
        # Same maintained caller paths; injected transport/report failures are
        # inert and provide no measurement or native qualification evidence.
        from unittest.mock import Mock
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            slot=Path(spec['operationRoot']);spec['role']='qualification';spec['workDeadline']=time.monotonic()+30
            subject={'firstCause':{'type':'RuntimeError','message':'original inert subject cause'},'commandAccepted':False}
            (slot/'measurement.subject.json').write_text(json.dumps(subject))
            child=Mock(pid=123456);child.poll.return_value=7
            with patch('subprocess.Popen',return_value=child),patch.object(module,'proc_actor',return_value=None),patch.object(module,'proc_private_record',side_effect=OSError('inert apparatus report failure')):
                with self.assertRaises(ValueError) as raised:
                    module.proc_measure_once(spec,{}, {},descriptor,approved,'a'*64,'normal')
            self.assertEqual(subject,raised.exception.proc_subject_result)
            self.assertEqual(subject['firstCause'],raised.exception.proc_subject_failure)
            self.assertIn({'type':'OSError','message':'inert apparatus report failure'},raised.exception.proc_measurement_additional)
            # Exit0 or a negative-control phase does not imply that cleanup or
            # apparatus reporting succeeded. Every common raise exports both.
            original_open=Path.open
            class FailingClose:
                def __init__(self,stream):self.stream=stream
                def close(self):self.stream.close();raise OSError('inert stream close failure')
                def __getattr__(self,name):return getattr(self.stream,name)
            def selected_open(path,*args,**kwargs):
                stream=original_open(path,*args,**kwargs)
                return FailingClose(stream) if path.name=='measurement.tracer.log' else stream
            for phase in ('normal','controller','session'):
                isolated=slot/('inert-secondary-'+phase);isolated.mkdir(mode=0o700)
                phase_spec=dict(spec,operationRoot=str(isolated));child.poll.return_value=0
                with patch('subprocess.Popen',return_value=child),patch.object(module,'proc_actor',return_value=None),patch.object(Path,'open',selected_open),patch.object(module,'proc_private_record',side_effect=OSError('inert apparatus report failure')):
                    with self.assertRaisesRegex(ValueError,'actual direct tracer birth') as raised:
                        module.proc_measure_once(phase_spec,{}, {},descriptor,approved,'a'*64,phase)
                self.assertEqual([{'type':'OSError','message':'inert stream close failure'},
                                  {'type':'OSError','message':'inert apparatus report failure'}],raised.exception.proc_measurement_additional)
            # Exercise the actual cleanup branch with an inert child/pidfd adapter:
            # a failed send never produces affirmative signal evidence.
            actor={'pid':123456,'ppid':os.getpid(),'uid':os.geteuid(),'pgid':123456,'sid':123456,'start':7}
            child.poll.return_value=None;child.wait.return_value=7
            resolve=Path.resolve
            def resolve_actor(path,*args,**kwargs):
                if str(path)=='/proc/123456/exe':return Path(spec['transport']['tracer'])
                return resolve(path,*args,**kwargs)
            # Retained log is occupied from the first independent attempt. This
            # test uses a separate ordinary slot, never resets a consumed attempt.
            second=slot/'inert-cleanup-slot';second.mkdir(mode=0o700)
            cleanup_spec=dict(spec,operationRoot=str(second));(second/'measurement.subject.json').write_text(json.dumps(subject))
            with patch('subprocess.Popen',return_value=child),patch.object(module,'proc_actor',return_value=actor),patch.object(Path,'resolve',resolve_actor),patch.object(module.os,'pidfd_open',side_effect=lambda pid:os.dup(0)),patch.object(module.signal,'pidfd_send_signal',side_effect=OSError('inert failed signal')):
                with self.assertRaises(ValueError):
                    module.proc_measure_once(cleanup_spec,{'descriptorSha256':'wrong'}, {},descriptor,approved,'a'*64,'normal')
            cleanup_record=json.loads((second/'measurement.apparatus.json').read_bytes())
            self.assertEqual(subject['firstCause'],cleanup_record['firstCause'])
            self.assertFalse(cleanup_record['pidfdSignalSent'])
            self.assertIn({'type':'OSError','message':'inert failed signal'},cleanup_record['additionalFailures'])
            # Registration is occupied, yet the canonical command gets its failed
            # result independently and preserves both the original cause/file.
            bootstrap=root/'bootstrap.json';bootstrap.write_text(json.dumps({'instrumentation':module.PROC_INSTRUMENTATION}))
            spec['proofInputs']['bootstrapEvidence']=str(bootstrap);spec['request']['nativeGet']=False
            admission=root/'admission-control.json';admission.write_text(json.dumps({'recovery':{'clientQualificationEnabled':True}}));spec['proofInputs']['admission']=str(admission)
            descriptor.write_text(json.dumps(spec));(slot/'input.json').write_bytes(descriptor.read_bytes())
            (slot/'command.json').write_text(json.dumps({'sendState':'prepared-no-send','descriptorSha256':hashlib.sha256(descriptor.read_bytes()).hexdigest()}))
            authority=json.loads(approved.read_bytes());authority['instrumentation']=module.PROC_INSTRUMENTATION
            authority['canonicalQualificationLineageIdentity']=module.proc_directory_identity(slot.parent)
            (slot/'measurement.collector.json').write_text('original occupied registration')
            cause=ValueError('inert failed subject');cause.proc_subject_failure=subject['firstCause'];cause.proc_subject_result=subject
            with patch.object(module,'proc_bootstrap_evidence'),patch.object(module,'proc_measure_once',side_effect=cause),contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(1,module.proc_qualification_apparatus(spec,descriptor,approved,'a'*64,authority))
            state=json.loads((slot/'command.json').read_bytes())
            self.assertEqual(subject['firstCause'],state['firstCause']);self.assertFalse(state['commandAccepted'])
            self.assertEqual('FileExistsError',state['additionalFailures'][-1]['type'])
            self.assertEqual('original occupied registration',(slot/'measurement.collector.json').read_text())
            self.assertTrue((slot/'dispatch-input.json').exists())

    def test_proc_private_control_artifact_refuses_alias_type_owner_mode_bound_and_read_drift(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            for name in ('measurement.ready.json','measurement.control.json'):
                path=root/name;raw=b'{"INERT":"bounded fixture"}';path.write_bytes(raw);path.chmod(0o600)
                self.assertEqual(raw,module.proc_private_artifact(path,8192))
                path.chmod(0o644)
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                path.chmod(0o600)
                with patch.object(module.os,'geteuid',return_value=os.geteuid()+1):
                    with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                os.link(path,root/'hardlink')
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                (root/'hardlink').unlink();path.unlink();path.symlink_to(root/'target')
                with self.assertRaisesRegex(ValueError,'path/bound'):module.proc_private_artifact(path,8192)
                path.unlink();os.mkfifo(path,0o600)
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                path.unlink();path.mkdir(mode=0o600)
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                path.rmdir();path.write_bytes(b'x'*8193);path.chmod(0o600)
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                path.write_bytes(b'')
                with self.assertRaisesRegex(ValueError,'regular/owner/link/mode/size'):module.proc_private_artifact(path,8192)
                path.write_bytes(raw);read=module.os.read
                def changed_after_read(fd,cap):
                    observed=read(fd,cap);path.write_bytes(raw[:-1]+b'x');return observed
                with patch.object(module.os,'read',side_effect=changed_after_read):
                    with self.assertRaisesRegex(ValueError,'identity/size drift'):module.proc_private_artifact(path,8192)
                path.write_bytes(raw)
                def replaced_after_read(fd,cap):
                    observed=read(fd,cap);replacement=root/'replacement';replacement.write_bytes(raw);replacement.chmod(0o600);os.replace(replacement,path);return observed
                with patch.object(module.os,'read',side_effect=replaced_after_read):
                    with self.assertRaisesRegex(ValueError,'identity/size drift'):module.proc_private_artifact(path,8192)
                with patch.object(module.os,'read',return_value=b'x'):
                    with self.assertRaisesRegex(ValueError,'identity/size drift'):module.proc_private_artifact(path,8192)
                with patch.object(module.os,'read',return_value=b'x'*8193):
                    with self.assertRaisesRegex(ValueError,'identity/size drift'):module.proc_private_artifact(path,8192)
                # Opening a replacement inode also refuses before any JSON is trusted.
                opened=module.os.open
                def replaced_before_open(selected,flags,*args,**kwargs):
                    replacement=root/'replacement';replacement.write_bytes(raw);replacement.chmod(0o600);os.replace(replacement,path);return opened(selected,flags,*args,**kwargs)
                with patch.object(module.os,'open',side_effect=replaced_before_open):
                    with self.assertRaisesRegex(ValueError,'opened identity drift'):module.proc_private_artifact(path,8192)

    def test_proc_negative_control_retirement_requires_actual_origin_and_keeps_partial_trace_unqualified(self):
        # Inert kernel/Popen adapters only: no tracer, PID adoption or native qualification.
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);namespace=root/'producer';namespace.mkdir()
            with patch.object(module,'PROC_OPERATION_BASE',namespace):
                lineage=namespace/str(os.geteuid())/module.PROC_PROFILES['proc-pair-client-qualification']
                lineage.mkdir(parents=True);operation=lineage.name;slot_root=module.proc_operation_root(operation,2);slot_root.mkdir()
                spec={'profile':'proc-pair-client-qualification','role':'qualification','operation':operation,
                      'qualificationAttempt':2,'operationRoot':str(slot_root),'workDeadline':100.0}
                descriptor=slot_root/'input.json';descriptor.write_text(json.dumps(spec))
                authority=root/'authority.json';authority.write_text('INERT authority adapter')
                authority_sha=hashlib.sha256(authority.read_bytes()).hexdigest()
                namespaces={'pid':'pid:[1]','mnt':'mnt:[2]','user':'user:[3]'}
                apparatus={'pid':os.getpid(),'start':1,'uid':os.geteuid(),'pgid':os.getpid(),'sid':os.getpid(),'namespaces':namespaces}
                tracer=dict(apparatus,pid=10,start=2,ppid=apparatus['pid'],pgid=10,sid=10,tracerPid=0)
                subject=dict(tracer,pid=11,start=3,ppid=10,tracerPid=10)
                nested=dict(subject,pid=12,start=4,ppid=11,pgid=12,sid=12)
                child=SimpleNamespace(pid=10,returncode=-module.signal.SIGKILL)
                def original(phase):
                    slot=slot_root/('control-'+phase);slot.mkdir()
                    proof={'apparatus':apparatus,'tracer':tracer,'phase':phase,'slot':str(slot),
                           'bootId':Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                           'descriptorSha256':hashlib.sha256(descriptor.read_bytes()).hexdigest(),
                           'authoritySha256':authority_sha,'subjectWorkDeadline':90.0}
                    ready={k:proof[k] for k in ('bootId','descriptorSha256','phase','subjectWorkDeadline')}
                    ready.update(tracer=tracer,subject=subject)
                    ready_path=slot/'measurement.ready.json';ready_path.write_text(json.dumps(ready));ready_path.chmod(0o600)
                    control=None
                    if phase=='session':
                        control={'phase':phase,'bootId':proof['bootId'],'controller':subject,'child':nested}
                        (slot/'measurement.control.json').write_text(json.dumps(control));(slot/'measurement.control.json').chmod(0o600)
                    for actor in ([subject] if phase=='controller' else [subject,nested]):
                        # Expected abort may stop mid syscall; custody never converts it into complete trace.
                        (slot/('qualification.trace.'+str(actor['pid']))).write_text('1.0 read(3, <unfinished ...>')
                    actors=[apparatus,tracer,subject]+([] if phase=='controller' else [nested])
                    record={'phase':phase,'measurementBinding':proof,'tracer':tracer,'subjectReadiness':ready,
                            'controlReadiness':control,'firstCause':None,'additionalFailures':[],
                            'subjectStartAuthenticated':True,'pidfdSignalSent':True,'tracerExit':-module.signal.SIGKILL,
                            'cleanup':'observed-known-terminal','residuals':[],
                            'sampledBirths':[{'pid':a['pid'],'start':a['start'],'tgid':a['pid']} for a in actors]}
                    return slot,record,proof
                with patch.object(module,'proc_actor',side_effect=lambda pid: apparatus if pid==apparatus['pid'] else None):
                    for phase in ('controller','session'):
                        slot,record,proof=original(phase)
                        evidence=module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        self.assertFalse(evidence['traceComplete']);self.assertFalse(evidence['readClosureQualified'])
                        self.assertFalse(evidence['publicationAuthorized']);self.assertEqual(phase,evidence['phase'])
                        self.assertNotIn(apparatus['pid'],[a['pid'] for a in evidence['knownTerminalBirths']])
                        self.assertEqual(1 if phase=='controller' else 2,len(evidence['traceArtifacts']))
                        bad=[]
                        for key,value in (('pidfdSignalSent',False),('tracerExit',0),('cleanup','unknown-private-hold'),
                                          ('subjectStartAuthenticated',False),('residuals',[{'pid':11}]),
                                          ('firstCause',{'message':'original failure'}),('additionalFailures',[{'message':'secondary'}])):
                            changed=copy.deepcopy(record);changed[key]=value;bad.append(changed)
                        for key,value in (('bootId','foreign'),('descriptorSha256','0'*64),('authoritySha256','0'*64),
                                          ('phase','normal'),('slot',str(slot_root)),('subjectWorkDeadline',80.0)):
                            changed=copy.deepcopy(record);changed['measurementBinding'][key]=value;bad.append(changed)
                        changed=copy.deepcopy(record);changed['sampledBirths'][1]['start']=99;bad.append(changed)
                        changed=copy.deepcopy(record);changed['subjectReadiness']['subject']['tracerPid']=99;bad.append(changed)
                        changed=copy.deepcopy(record);changed['sampledBirths'].append(changed['sampledBirths'][0]);bad.append(changed)
                        changed=copy.deepcopy(record);changed['controlReadiness']=None if phase=='session' else {'phase':'controller'};bad.append(changed)
                        for changed in bad:
                            with self.subTest(phase=phase,changed=changed):
                                with self.assertRaises(ValueError):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,changed,changed['measurementBinding'],child)
                        with self.assertRaises(ValueError):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,None)
                        with self.assertRaises(ValueError):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,SimpleNamespace(pid=10,returncode=0))
                        foreign=slot/'qualification.trace.99';foreign.write_text('INERT foreign actor artifact')
                        with self.assertRaisesRegex(ValueError,'actor population'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        foreign.unlink()
                        original_trace=slot/'qualification.trace.11';held=original_trace.read_bytes();original_trace.unlink()
                        original_trace.symlink_to(descriptor)
                        with self.assertRaisesRegex(ValueError,'custody/bound'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        original_trace.unlink();original_trace.write_bytes(held)
                        os.link(original_trace,slot/'inert-hardlink')
                        with self.assertRaisesRegex(ValueError,'custody/bound'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        (slot/'inert-hardlink').unlink()
                        with original_trace.open('wb') as stream:stream.truncate(8*1024**2+1)
                        with self.assertRaisesRegex(ValueError,'custody/bound'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        original_trace.write_bytes(held)
                        for pid,current in ((11,dict(subject,state='R')),(11,dict(subject,start=99,state='Z'))):
                            with patch.object(module,'proc_actor',side_effect=lambda selected: apparatus if selected==apparatus['pid'] else current if selected==pid else None):
                                with self.assertRaisesRegex(ValueError,'terminal birth'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        with patch.object(module,'proc_actor',side_effect=lambda pid: dict(apparatus,start=99) if pid==apparatus['pid'] else None):
                            with self.assertRaisesRegex(ValueError,'apparatus birth'):module.proc_control_retirement_evidence(spec,slot,descriptor,authority,authority_sha,record,proof,child)
                        # The unchanged complete normal parser refuses this same incomplete artifact.
                        normal=dict(spec,readEvidence={'directories':[],'aliases':[],'absences':[]},sourceReadAllowlist=[])
                        with self.assertRaisesRegex(ValueError,'bound/completeness'):
                            module.proc_trace_evidence(normal,slot,authority)

    def test_proc_qualification_native_legs_are_consumed_without_repeat_or_watch_widening(self):
        state={'profile':'qualification'}
        module.proc_charge_read(state,'api','repo-public')
        with self.assertRaises(ValueError):module.proc_charge_read(state,'api','repo-public')
        with self.assertRaises(ValueError):module.proc_charge_read(state,'api','arbitrary')
        for kind,leg in (('api','terminal-job'),('storage','terminal-job'),('api','terminal-archive'),('storage','terminal-archive')):
            module.proc_charge_read(state,kind,leg)
        self.assertEqual({'api':3,'storage':2,'total':5},state['readCounts'])
        self.assertEqual(5,len(state['consumedLegs']))
        watch={}
        for kind in ('api',)*5+('storage',)*2:module.proc_charge_read(watch,kind)
        self.assertEqual(7,watch['readCounts']['total'])

    def test_proc_qualification_bootstrap_cannot_borrow_dispatch_or_native_get_authority(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);root.chmod(0o700);spec,descriptor,approved=self.proc_fixture(root)
            spec['profile']='proc-pair-client-qualification';spec['role']='qualification'
            spec['operation']=module.PROC_PROFILES[spec['profile']];spec['qualificationAttempt']=1
            lineage=module.proc_operation_root(spec['operation']);lineage.mkdir(mode=0o700)
            slot=module.proc_operation_root(spec['operation'],1);slot.mkdir(mode=0o700)
            for name in ('home','tmp','ca-empty'):(slot/name).mkdir(mode=0o700)
            config=module.proc_support_root()/'openssl.cnf'
            spec['operationRoot']=str(slot)
            spec['environment'].update(HOME=str(slot/'home'),TMPDIR=str(slot/'tmp'),OPENSSL_CONF=str(config))
            spec['proofInputs']['bootstrapEvidence']=spec['proofInputs'].pop('runtimeQualification')
            spec['request']={'method':'GET','endpoint':'repos/'+module.PROC_REPOSITORY,'apiVersion':'2026-03-10',
                             'body':None,'requestLimit':5,'nativeGet':False,'credentialReference':None,'cases':list(module.PROC_QUALIFICATION_CASES)}
            self.assertEqual('qualification',module.proc_command_preflight(spec,now=time.monotonic())['role'])
            self.assertNotIn('runtimeQualification',spec['proofInputs'])
            descriptor.write_text(json.dumps(spec))
            authority=json.loads(approved.read_bytes());authority.update(profile=spec['profile'],operation=spec['operation'],
                operationRoot=str(slot),descriptorSha256=hashlib.sha256(descriptor.read_bytes()).hexdigest(),
                canonicalOperationIdentity=module.proc_directory_identity(slot),canonicalQualificationLineageIdentity=module.proc_directory_identity(lineage))
            approved.write_text(json.dumps(authority))
            # The old dispatch admission does not authorize qualification; native
            # client execution cannot be reached by this source-only control.
            original=module.os.readlink
            def dev_null(path,*args,**kwargs):return '/dev/null' if str(path)=='/proc/self/fd/0' else original(path,*args,**kwargs)
            errors=io.StringIO()
            with contextlib.redirect_stderr(errors),patch('runpy.run_path',return_value={'pair_recovery_selection':lambda a:a['recovery']}),patch.object(module,'proc_bootstrap_evidence',side_effect=ValueError('explicit non-effect bootstrap unavailable')),patch.object(module.os,'readlink',side_effect=dev_null):
                self.assertEqual(1,module.execute_proc_command(descriptor,approved,hashlib.sha256(approved.read_bytes()).hexdigest()))
            self.assertIn('explicit non-effect bootstrap unavailable',errors.getvalue())
            self.assertFalse((slot/'dispatch-input.json').exists())
            bad=copy.deepcopy(spec);bad['request']['method']='POST'
            with self.assertRaises(ValueError):module.proc_command_preflight(bad,now=time.monotonic())
            for ordinal in (None,0,5,True):
                bad=copy.deepcopy(spec);bad['qualificationAttempt']=ordinal
                with self.assertRaises(ValueError):module.proc_command_preflight(bad,now=time.monotonic())
            for ordinal in (1,2,3,4):
                bad=copy.deepcopy(spec);bad['qualificationAttempt']=ordinal
                path=module.proc_operation_root(spec['operation'],ordinal)
                bad['operationRoot']=str(path);bad['environment'].update(HOME=str(path/'home'),TMPDIR=str(path/'tmp'))
                self.assertEqual('qualification',module.proc_command_preflight(bad,now=time.monotonic())['role'])
            # The sole successor is a global ordinal in the existing lineage;
            # it does not select an alternate root or authorize a fifth slot.
            for ordinal in (0,5,True):
                with self.assertRaises(ValueError):module.proc_operation_root(spec['operation'],ordinal)
            with self.assertRaises(ValueError):module.proc_operation_root(module.PROC_ORIGINAL,4)
            successor=copy.deepcopy(bad)
            successor['operationRoot']=str(module.proc_operation_root(spec['operation'],3))
            with self.assertRaises(ValueError):module.proc_command_preflight(successor,now=time.monotonic())
            successor=copy.deepcopy(bad);successor['role']='dispatch'
            with self.assertRaises(ValueError):module.proc_command_preflight(successor,now=time.monotonic())
            self.assertEqual(spec['operation'],module.proc_operation_root(spec['operation'],2).parent.name)

    def test_bootstrap_base_retains_command_cause_before_fixture_scratch_cleanup(self):
        for inherited in (False, True):
            with self.subTest(inherited=inherited), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                gates = root / 'eng/bootstrap-gates'
                gates.mkdir(parents=True)
                shutil.copyfile(ROOT / 'eng/bootstrap-gates/canonical-quint-shard.sh', gates / 'canonical-quint-shard.sh')
                (gates / 'canonical-quint.sh').write_text(
                    '#!/usr/bin/env bash\nset -eu\nmkdir -p "$FSGG_QUINT_SHARD_ROOT"\nprintf "{}\\n" > "$FSGG_QUINT_RECEIPT"\n')
                (root / 'eng/verify-choreo-c2-bounded.sh').write_text('''#!/usr/bin/env bash
set -eu
python3 - <<'PYFIXTURE'
import importlib.util, json, os, pathlib, shutil
source = pathlib.Path(os.environ['FIXTURE_COLLECTOR'])
spec = importlib.util.spec_from_file_location('fixture_collector', source)
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)
scratch = pathlib.Path.cwd() / 'fixture-scratch'
scratch.mkdir()
report = {'schema': collector.RESULT_SCHEMA, 'commandExit': 0, 'commandAccepted': False,
          'firstCause': {'type': 'ValueError', 'message': 'original post-TLC fixture cause'},
          'additionalFailures': [], 'ownerReceipt': {'cleanup': 'observed-empty-group'},
          'input': {'private': 'NEVER COPY'}}
(scratch / 'runner.log.attempt-1.command.json').write_text(json.dumps(report))
collector.capture(scratch, pathlib.Path(os.environ['FSGG_CHOREO_DIAGNOSTIC_ROOT']), 37)
shutil.rmtree(scratch)
PYFIXTURE
exit 37
''')
                archive = root / 'toolchain.fixture'
                archive.write_text('fixture-only; never executed or extracted')
                shard_root = root / 'receipts'
                destination = root / 'inherited-diagnostics' if inherited else shard_root / 'base-choreo-failure-diagnostics'
                env = dict(os.environ, FSGG_QUINT_SHARD='base', FSGG_QUINT_TOOLCHAIN_ARCHIVE=str(archive),
                           FSGG_QUINT_SHARD_ROOT=str(shard_root), FIXTURE_COLLECTOR=str(ROOT / 'eng/retain-choreo-c2-diagnostics.py'))
                env.pop('FSGG_CHOREO_DIAGNOSTIC_ROOT', None)
                if inherited:
                    env['FSGG_CHOREO_DIAGNOSTIC_ROOT'] = str(destination)
                result = subprocess.run(['bash', str(gates / 'canonical-quint-shard.sh')], cwd=root,
                                        env=env, capture_output=True, timeout=10)
                self.assertEqual(37, result.returncode, result.stderr)
                self.assertFalse((root / 'fixture-scratch').exists())
                artifact = json.loads((destination / 'failure.json').read_text())
                self.assertEqual(37, artifact['originalExitCode'])
                self.assertIn('original post-TLC fixture cause', json.dumps(artifact))
                self.assertNotIn('NEVER COPY', json.dumps(artifact))

    def test_compiler_detail_survives_filtered_bounded_copy(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            server = scratch / '_apalache-out/server/attempt'; server.mkdir(parents=True)
            (server / 'detailed.log').write_text('compiler error: missing operator\nAuthorization: Bearer sentinel\npassword=sentinel\nhttps://host/path?key=sentinel\n' + str(scratch))
            (server / 'key.pem').write_text('NEVER COPY')
            value = module.capture(scratch, root / 'diagnostics', 37)
            text = json.dumps(value)
            self.assertIn('missing operator', text)
            self.assertNotIn('sentinel', text)
            self.assertNotIn('NEVER COPY', text)
            self.assertNotIn(str(scratch), text)
            self.assertEqual(37, value['originalExitCode'])
            self.assertEqual('compiler-server', value['logs'][0]['source'])
            self.assertEqual(0o600, (root / 'diagnostics/failure.json').stat().st_mode & 0o777)

    def test_typed_command_cause_survives_without_input_or_arbitrary_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            report = {'schema': module.RESULT_SCHEMA, 'commandExit': 37, 'commandAccepted': False,
                      'firstCause': {'type': 'RuntimeError', 'message': 'original failed command'},
                      'additionalFailures': [{'type': 'ValueError', 'message': 'secondary reporting'}],
                      'ownerReceipt': {'firstCause': 'original owner', 'cleanup': 'observed-empty-group'},
                      'input': {'private': 'NEVER COPY'}}
            (scratch / 'runner.log.attempt-1.command.json').write_text(json.dumps(report))
            (scratch / 'foreign.json').write_text('NEVER COPY')
            value = module.capture(scratch, root / 'diagnostics', 37)
            text = json.dumps(value)
            self.assertIn('original failed command', text)
            self.assertIn('secondary reporting', text)
            self.assertIn('observed-empty-group', text)
            self.assertNotIn('NEVER COPY', text)
            self.assertEqual('owned-command', value['logs'][0]['source'])

    def test_large_file_retains_head_and_tail_with_explicit_omission(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            (scratch / 'runner.log').write_bytes(b'HEAD\n' + b'x' * 200000 + b'\nTAIL-CAUSE')
            value = module.capture(scratch, root / 'diagnostics', 1)
            self.assertTrue(value['logs'][0]['truncated'])
            self.assertIn('HEAD', value['logs'][0]['text'])
            self.assertIn('TAIL-CAUSE', value['logs'][0]['text'])
            self.assertLess((root / 'diagnostics/failure.json').stat().st_size, module.MAX_OUTPUT)

    def test_symlinks_and_non_logs_are_never_read(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            private = root / 'private'; private.write_text('NOT READ')
            (scratch / 'foreign.log').symlink_to(private)
            (scratch / '_apalache-out').symlink_to(root, target_is_directory=True)
            self.assertEqual([], module.capture(scratch, root / 'diagnostics', 1)['logs'])

    def test_collection_population_is_bounded(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            for i in range(140): (scratch / f'{i}.log').write_text('detail')
            value = module.capture(scratch, root / 'diagnostics', 1)
            self.assertEqual(16, len(value['logs']))
            self.assertIn('collection-bounded', value['omittedCoverage'])

    def test_expired_collection_reports_unknown_coverage(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            ticks = iter([0, 3, 3])
            value = module.capture(scratch, root / 'diagnostics', 1, clock=lambda: next(ticks))
            self.assertEqual([], value['logs'])
            self.assertIn('collection-bounded', value['omittedCoverage'])

    def test_existing_destination_and_success_refuse(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir(); dest = root / 'diagnostics'; dest.mkdir()
            with self.assertRaises(FileExistsError): module.capture(scratch, dest, 1)
            with self.assertRaises(ValueError): module.capture(scratch, root / 'other', 0)

    def run_gate(self, stale=False, success=False):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); binary = root / 'bin'; binary.mkdir()
            for name in ['java', 'sha256sum']:
                p = binary / name; p.write_text('#!/bin/sh\nexit 0\n'); p.chmod(0o755)
            quint = binary / 'fake-quint'
            quint.write_text('''#!/bin/sh
if [ "$1" = verify ]; then
  mkdir -p _apalache-out/server/attempt
  echo 'compiler detail: failing stage' > _apalache-out/server/attempt/detailed.log
  echo 'Authorization: Bearer synthetic-secret' >> _apalache-out/server/attempt/detailed.log
  if [ "$FAKE_SUCCESS" = yes ]; then
    echo '[ok] No violation found'
    echo '715 states generated, 593 distinct states found, 0 states left on queue.'
  else
    echo 'error: error'
    exit 37
  fi
fi
exit 0
'''); quint.chmod(0o755)
            dest = root / 'diagnostics'
            if stale: dest.mkdir()
            quint_home = root / 'quint-home'
            jar = quint_home / 'apalache-dist-0.56.1/apalache/lib/apalache.jar'
            jar.parent.mkdir(parents=True); jar.write_text('fake JAR; never executed')
            env = dict(os.environ, PATH=str(binary) + ':' + os.environ['PATH'], RUNNER_TEMP=str(root), FSGG_QUINT_BIN=str(quint), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(dest), FAKE_SUCCESS='yes' if success else 'no', JAVA_HOME=str(root), QUINT_HOME=str(quint_home))
            env.pop('FSGG_QUINT_TOOLCHAIN_ARCHIVE', None)
            result = subprocess.run(['bash', str(ROOT / 'eng/verify-choreo-c2-bounded.sh')], env=env, capture_output=True, text=True, timeout=10)
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))
            value = json.loads((dest / 'failure.json').read_text()) if (dest / 'failure.json').exists() else None
            return result, value

    def test_existing_disk_inventory_bounds_generated_scope_without_following_links(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root / 'regular').write_text('generated')
            outside = root.parent / 'outside-does-not-exist'
            (root / 'external').symlink_to(outside)
            observed = module.census(root, 100, clock=lambda: 1)
            self.assertEqual(3, observed['objects'])
            self.assertEqual(1, observed['fixtureSymlinks'])
            self.assertFalse(observed['linkTraversal'])
            (root / 'sparse').touch()
            with (root / 'sparse').open('r+b') as file: file.truncate(1073741825)
            with self.assertRaisesRegex(RuntimeError, 'disk-byte-cap'):
                module.census(root, 100, clock=lambda: 1)
            (root / 'sparse').unlink()
            with self.assertRaisesRegex(RuntimeError, 'disk-byte-cap'):
                module.census(root, 100, limit=0, clock=lambda: 1)
            with self.assertRaisesRegex(RuntimeError, 'disk-budget'):
                module.census(root, 1, clock=lambda: 1)
            fifo = root / 'fifo'; os.mkfifo(fifo)
            with self.assertRaisesRegex(RuntimeError, 'disk-special-file'):
                module.census(root, 100, clock=lambda: 1)

    def test_actual_gate_preserves_exit_and_cleans_scratch(self):
        result, value = self.run_gate()
        self.assertEqual(37, result.returncode, result.stderr)
        self.assertEqual(37, value['originalExitCode'])
        self.assertIn('failing stage', json.dumps(value))
        self.assertNotIn('synthetic-secret', json.dumps(value))
        self.assertIn('retained', result.stderr)
        self.assertNotIn('APALACHE_STARTUP_RETRY', result.stderr)

    def test_reporting_failure_preserves_first_exit_and_cleanup(self):
        result, value = self.run_gate(stale=True)
        self.assertEqual(37, result.returncode, result.stderr)
        self.assertIsNone(value)
        self.assertIn('unavailable', result.stderr)

    def test_success_exit_does_not_collect_and_still_cleans(self):
        # Exercise the actual EXIT implementation without launching any gate.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); dest = root / 'diagnostics'
            script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
            script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
            result = subprocess.run(['bash', '-c', script + '\nexit 0\n'], env=dict(os.environ, RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(dest)), capture_output=True, timeout=5)
            self.assertEqual(0, result.returncode)
            self.assertFalse(dest.exists())
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))

    def test_broken_reporting_sink_cannot_replace_original_exit(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
            script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
            with open('/dev/full', 'wb') as sink:
                result = subprocess.run(['bash', '-c', script + '\nexit 37\n'], env=dict(os.environ, RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(root / 'diagnostics')), stdout=subprocess.DEVNULL, stderr=sink, timeout=5)
            self.assertEqual(37, result.returncode)
            self.assertFalse(list(root.glob('fsgg-choreo-c2-bounded.*')))

    def test_cleanup_failure_fails_success_and_preserves_original_failure(self):
        for original, expected in [(0, 73), (37, 37)]:
            with self.subTest(original=original), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); binary = root / 'bin'; binary.mkdir()
                fake_rm = binary / 'rm'
                fake_rm.write_text('#!/bin/sh\nexit 73\n')
                fake_rm.chmod(0o755)
                script = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text().split('quint_sha=', 1)[0]
                script = script.replace('repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"', 'repo_root="' + str(ROOT) + '"')
                env = dict(os.environ, PATH=str(binary) + ':' + os.environ['PATH'], RUNNER_TEMP=str(root), FSGG_CHOREO_DIAGNOSTIC_ROOT=str(root / 'diagnostics'))
                result = subprocess.run(['bash', '-c', script + f'\nexit {original}\n'], env=env, capture_output=True, text=True, timeout=5)
                self.assertEqual(expected, result.returncode, result.stderr)
                self.assertIn('CHOREO_SCRATCH_CLEANUP failed', result.stderr)
                self.assertTrue(list(root.glob('fsgg-choreo-c2-bounded.*')))
                if original:
                    value = json.loads((root / 'diagnostics/failure.json').read_text())
                    self.assertEqual(original, value['originalExitCode'])
                else:
                    self.assertFalse((root / 'diagnostics').exists())

    def test_collector_is_in_formal_source_identity_and_only_existing_artifact(self):
        plan = json.loads((ROOT / 'eng/bootstrap-qualification-plan.json').read_text())
        self.assertIn('eng/retain-choreo-c2-diagnostics.py', plan['formalReuse']['exactPaths'])
        self.assertIn('eng/shared_process_owner.py', plan['formalReuse']['exactPaths'])
        self.assertIn('eng/run-choreo-c2-tlc.sh', plan['formalReuse']['exactPaths'])
        script = (ROOT / 'eng/optimistic-run-formal-fragment.sh').read_text()
        self.assertIn('FSGG_CHOREO_DIAGNOSTIC_ROOT="$fragment/choreo-failure-diagnostics"', script)




class ParityDiagnosticControls(unittest.TestCase):
    def module(self):
        s = importlib.util.spec_from_file_location('parity_diagnostic_control', ROOT / 'eng/verify-choreo-c5-parity.py')
        m = importlib.util.module_from_spec(s); s.loader.exec_module(m)
        return m

    def test_actual_c5_exception_retains_details_before_original_cleanup(self):
        from unittest.mock import patch
        import io
        m = self.module(); observed = []; original = RuntimeError('original compile failure')
        with tempfile.TemporaryDirectory() as tmp:
            destination = Path(tmp) / 'choreo-failure-diagnostics'
            def fail(directory):
                observed.append(directory)
                logs = directory / '_apalache-out/server/attempt'; logs.mkdir(parents=True)
                (logs / 'detailed.log').write_text('compiler detail: failed translation\npassword=synthetic-c5-secret')
                raise original
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(destination)), patch.object(m, 'export_legacy', side_effect=fail), patch.object(m.sys, 'stderr', io.StringIO()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)
            self.assertFalse(observed[0].exists())
            artifact = Path(str(destination) + '-parity') / 'failure.json'
            text = artifact.read_text()
            self.assertIn('failed translation', text)
            self.assertNotIn('synthetic-c5-secret', text)
            self.assertLessEqual(artifact.stat().st_size, 96 * 1024)

    def test_c5_reporting_failure_preserves_exact_original_exception_and_cleanup(self):
        from unittest.mock import patch
        import io
        m = self.module(); observed = []; original = RuntimeError('first cause')
        def fail(directory): observed.append(directory); raise original
        with tempfile.TemporaryDirectory() as tmp:
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(Path(tmp) / 'diagnostics')), patch.object(m, 'export_legacy', side_effect=fail), patch.object(m.runpy, 'run_path', side_effect=OSError('reporting unavailable')), patch.object(m.sys, 'stderr', io.StringIO()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)
            self.assertFalse(observed[0].exists())

    def test_c5_cleanup_failure_cannot_green_success_or_replace_first_cause(self):
        from unittest.mock import patch
        import io
        for fails in [False, True]:
            m = self.module(); original = RuntimeError('first cause')
            with tempfile.TemporaryDirectory() as tmp:
                class FakeTemporary:
                    name = tmp
                    def cleanup(self): raise OSError('cleanup failed')
                with patch.object(m.tempfile, 'TemporaryDirectory', return_value=FakeTemporary()), patch.object(m, 'export_legacy', side_effect=original if fails else None), patch.object(m, 'legacy', return_value=[]), patch.object(m, 'choreo', return_value=[]), patch.object(m, 'compare'), patch.object(m, 'relative_retry', return_value=[]), patch.object(m, 'retain_failure'), patch.object(m.sys, 'stderr', io.StringIO()):
                    with self.assertRaises(RuntimeError if fails else OSError) as actual: m.main()
                if fails: self.assertIs(original, actual.exception)

    def test_joint_output_cap_reports_omission_without_losing_original_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); scratch = root / 'scratch'; scratch.mkdir()
            for i in range(16): (scratch / f'{i}.log').write_text('\N{SNOWMAN}' * 8192)
            value = module.capture(scratch, root / 'diagnostics', 37, max_output=96 * 1024)
            self.assertIn('output-byte-bound', value['omittedCoverage'])
            self.assertEqual(37, value['originalExitCode'])
            self.assertLessEqual((root / 'diagnostics/failure.json').stat().st_size, 96 * 1024)
            # Both runtime callers select half the original diagnostic output cap.
            gate = (ROOT / 'eng/verify-choreo-c2-bounded.sh').read_text()
            parity = (ROOT / 'eng/verify-choreo-c5-parity.py').read_text()
            self.assertIn('"$status" 98304; then', gate)
            self.assertIn('max_output=96 * 1024', parity)
            self.assertEqual(192 * 1024, 2 * 96 * 1024)

    def test_c5_interrupted_or_broken_reporting_preserves_original_exception(self):
        from unittest.mock import patch
        m = self.module(); original = RuntimeError('first cause')
        class BrokenSink:
            def write(self, value): raise OSError('broken reporting')
        with tempfile.TemporaryDirectory() as tmp:
            with patch.dict(os.environ, FSGG_CHOREO_DIAGNOSTIC_ROOT=str(Path(tmp) / 'diagnostics')), patch.object(m, 'export_legacy', side_effect=original), patch.object(m.runpy, 'run_path', side_effect=SystemExit(99)), patch.object(m.sys, 'stderr', BrokenSink()):
                with self.assertRaises(RuntimeError) as actual: m.main()
            self.assertIs(original, actual.exception)


class CommandInterfaceControls(unittest.TestCase):
    def test_current_endpoint_proof_prioritizes_server_without_reading_irrelevant_denied_leader(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        rows=[leader,server]; owner=Mock(); owner.census.side_effect=[list(rows),list(rows)]
        owner.proc.return_value=server; child=Mock(pid=101,returncode=None)
        known=set(); servers={(102,21)}; handles={(102,21):7}; original=PermissionError('live leader FD denial')
        def targets(row,*args,**kwargs):
            if row['pid'] == 101: raise original
            return ['socket:[123]']
        listeners=Mock(side_effect=[{'123'},{'123'}])
        with patch.object(module,'owned_fd_targets',side_effect=targets) as reads:
            module.observe_endpoint_ownership(owner,child,leader,servers,handles,known,Mock(),6,listeners,[],{'entries':[],'omitted':0})
        self.assertEqual([102],[call.args[0]['pid'] for call in reads.call_args_list])
        self.assertEqual({(101,20),(102,21)},known)
        self.assertEqual(2,owner.census.call_count); self.assertEqual(2,listeners.call_count)
        child.poll.assert_not_called(); owner.proc.assert_called_once_with(102)

    def test_empty_endpoint_still_observes_both_censuses_and_listener_stability(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        for disposition in ('empty','unknown-census','generation-cap'):
            with self.subTest(disposition=disposition):
                owner=Mock(); known=set(); rows=[leader]
                if disposition == 'generation-cap': rows += [dict(leader,pid=i,start=i) for i in range(102,107)]
                original=PermissionError('fresh custody census unknown')
                owner.census.side_effect=[list(rows),original if disposition == 'unknown-census' else list(rows)]
                listeners=Mock(side_effect=[set(),set()])
                with patch.object(module,'owned_fd_targets') as reads:
                    if disposition == 'empty':
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,set(),{},known,Mock(),6,listeners,[],{'entries':[],'omitted':0})
                        self.assertEqual(2,owner.census.call_count); self.assertEqual(2,listeners.call_count)
                    else:
                        with self.assertRaises(PermissionError if disposition == 'unknown-census' else ValueError) as caught:
                            module.observe_endpoint_ownership(owner,Mock(pid=101),leader,set(),{},known,Mock(),6,listeners,[],{'entries':[],'omitted':0})
                        if disposition == 'unknown-census': self.assertIs(original,caught.exception)
                    reads.assert_not_called()

    def test_endpoint_proof_refuses_replaced_unknown_changed_or_unmatched_ownership(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        for disposition in ('replaced','changed-group','unknown-birth','membership-changed','foreign-inode','needed-denial'):
            with self.subTest(disposition=disposition):
                original=PermissionError('required FD ownership denied')
                owner=Mock(); owner.census.side_effect=[[leader,server],[leader] if disposition == 'membership-changed' else [leader,server]]
                owner.proc.side_effect=original if disposition == 'unknown-birth' else None
                owner.proc.return_value=dict(server,start=22) if disposition == 'replaced' else dict(server,pgid=999) if disposition == 'changed-group' else server
                inodes={'123','999'} if disposition == 'foreign-inode' else {'123'}
                listeners=Mock(side_effect=[inodes,inodes])
                def targets(row,*args,**kwargs):
                    if disposition == 'needed-denial' or row['pid'] == 101: raise original
                    return ['socket:[123]']
                with patch.object(module,'owned_fd_targets',side_effect=targets):
                    with self.assertRaises(PermissionError if disposition in ('unknown-birth','foreign-inode','needed-denial') else ValueError) as caught:
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0})
                    if disposition in ('unknown-birth','foreign-inode','needed-denial'): self.assertIs(original,caught.exception)

    def test_endpoint_proof_retains_foreign_custody_member_for_refusing_retirement(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        foreign={'pid':103,'start':22,'pgid':101,'state':'S'}
        owner=Mock(); owner.census.side_effect=[[leader,server],[leader,server,foreign],[server,foreign]]
        owner.proc.return_value=server; known=set(); servers={(102,21)}; handles={(102,21):7}; stops=[]; observed={'members':None}
        with patch.object(module,'owned_fd_targets',return_value=['socket:[123]']), patch.object(module.signal,'pidfd_send_signal') as signals:
            module.observe_endpoint_ownership(owner,Mock(pid=101),leader,servers,handles,known,Mock(),6,Mock(side_effect=[{'123'},{'123'}]),[],{'entries':[],'omitted':0})
            self.assertIn((103,22),known)
            module.retire_original_server(owner,Mock(returncode=0),leader,servers,handles,stops,Mock(),known,6,observed)
            signals.assert_not_called()
        receipt={'firstCause':'RuntimeError: child exited with live owned-group descendants','exit':0,'leader':leader,
                 'exitObservation':{'code':0,'members':[server,foreign],'omittedMembers':0}}
        self.assertFalse(module.expected_server_retirement(receipt,servers,observed['members'],stops))

    def test_one_known_listener_transition_requires_a_fresh_complete_stable_proof(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        for event,sets in [('after-birth',[set(),{'123'},{'123'},{'123'}]),
                           ('poll',[{'123'},{'456'},{'456'},{'456'}]),
                           ('before-success',[{'123'},{'456'},{'456'},{'456'}]),
                           ('child-exit-observed',[{'123'},set(),set(),set()])]:
            with self.subTest(event=event):
                order=[]; pending=list(sets); owner=Mock(); retained={'entries':[],'omitted':0}
                def census(group): order.append('census'); return [leader,server]
                def listeners(): order.append('listeners'); return pending.pop(0)
                def birth(pid): order.append('birth'); return server
                def targets(row,*args,**kwargs): order.append('fd'); return ['socket:[123]','socket:[456]']
                owner.census.side_effect=census; owner.proc.side_effect=birth
                with patch.object(module,'owned_fd_targets',side_effect=targets):
                    module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained,event)
                per_proof=['census','listeners','fd','birth','census','listeners']
                no_fd=['census','listeners','census','listeners']
                self.assertEqual((no_fd if event=='after-birth' else per_proof)+(no_fd if event=='child-exit-observed' else per_proof),order)
                self.assertEqual([1,2],[x['attempt'] for x in retained['entries']])
                self.assertEqual(['changed','proved'],[x['disposition'] for x in retained['entries']])
                for ordinal,entry in enumerate(retained['entries']):
                    self.assertEqual(event,entry['event']); self.assertIsNone(entry['failure'])
                    self.assertEqual(sorted(sets[ordinal*2]),entry['beforeInodes']['values'])
                    self.assertEqual(sorted(sets[ordinal*2+1]),entry['afterInodes']['values'])
                    self.assertEqual(2,len(entry['beforeMembers']['members'])); self.assertEqual(2,len(entry['afterMembers']['members']))

    def test_two_known_listener_transitions_refuse_and_preserve_both_snapshots(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}; server=dict(leader,pid=102,start=21,state='S')
        owner=Mock(); owner.census.return_value=[leader,server]; owner.proc.return_value=server
        retained={'entries':[],'omitted':0}; listeners=Mock(side_effect=[{'123'},{'456'},{'456'},{'789'}])
        with patch.object(module,'owned_fd_targets',return_value=['socket:[123]','socket:[456]']):
            with self.assertRaisesRegex(ValueError,'endpoint listeners changed during ownership observation'):
                module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
        self.assertEqual(4,owner.census.call_count); self.assertEqual(4,listeners.call_count)
        self.assertEqual(['changed','refused'],[x['disposition'] for x in retained['entries']])
        self.assertEqual(['123'],retained['entries'][0]['beforeInodes']['values']); self.assertEqual(['456'],retained['entries'][0]['afterInodes']['values'])
        self.assertEqual(['456'],retained['entries'][1]['beforeInodes']['values']); self.assertEqual(['789'],retained['entries'][1]['afterInodes']['values'])
        self.assertEqual('ValueError',retained['entries'][1]['failure']['type'])

    def test_uncertain_endpoint_error_cannot_be_overwritten_by_a_later_stable_state(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}; server=dict(leader,pid=102,start=21,state='S')
        for kind in ('fd','census','listener','birth','foreign','changed-group','deadline','generation'):
            with self.subTest(kind=kind):
                original=TimeoutError('original deadline') if kind=='deadline' else PermissionError('original '+kind+' observation')
                owner=Mock(); owner.census.return_value=[leader,server]; owner.proc.return_value=dict(server,pgid=999) if kind=='changed-group' else server
                if kind=='census': owner.census.side_effect=original
                if kind=='birth': owner.proc.side_effect=original
                listeners=Mock(return_value={'123'})
                if kind=='listener': listeners.side_effect=original
                checkpoint=Mock(side_effect=original if kind=='deadline' else None)
                known={(pid,pid) for pid in range(200,207)} if kind=='generation' else set()
                retained={'entries':[],'omitted':0}
                with patch.object(module,'owned_fd_targets',side_effect=original if kind=='fd' else None,
                                  return_value=[] if kind=='foreign' else ['socket:[123]']):
                    with self.assertRaises(ValueError if kind in ('foreign','changed-group','generation') else type(original)) as caught:
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},known,checkpoint,6,listeners,[],{'entries':[],'omitted':0},retained)
                if kind not in ('foreign','changed-group','generation'): self.assertIs(original,caught.exception)
                self.assertEqual(1,len(retained['entries'])); self.assertEqual('refused',retained['entries'][0]['disposition'])
                self.assertEqual(type(caught.exception).__name__,retained['entries'][0]['failure']['type'])
                self.assertLessEqual(listeners.call_count,2 if kind=='foreign' else 1)
        # A known transition does not make a later denial retryable or replaceable.
        original=PermissionError('second proof required FD denied')
        owner=Mock(); owner.census.side_effect=lambda group:[leader,server]; owner.proc.return_value=server
        retained={'entries':[],'omitted':0}; listeners=Mock(side_effect=[{'123'},{'456'},{'456'},{'456'}])
        with patch.object(module,'owned_fd_targets',side_effect=[['socket:[123]'],original]) as reads:
            with self.assertRaises(PermissionError) as caught:
                module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
        self.assertIs(original,caught.exception); self.assertEqual(2,reads.call_count)
        self.assertEqual(3,owner.census.call_count); self.assertEqual(3,listeners.call_count)
        self.assertEqual(['changed','refused'],[x['disposition'] for x in retained['entries']])
        self.assertEqual('PermissionError',retained['entries'][1]['failure']['type'])
        self.assertIsNone(retained['entries'][1]['afterInodes'])

    def test_mismatched_listener_diagnostics_never_replace_original_refusal(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        for kind in ('stable-foreign','listener-disappeared','birth-absent','birth-replaced',
                     'post-census-denied','post-birth-denied','post-listener-denied','deadline'):
            with self.subTest(kind=kind):
                owner=Mock(); secondary=PermissionError('diagnostic '+kind)
                owner.census.side_effect=[[server],secondary if kind=='post-census-denied' else [server]]
                owner.proc.side_effect=secondary if kind=='post-birth-denied' else None
                owner.proc.return_value=None if kind=='birth-absent' else dict(server,start=22) if kind=='birth-replaced' else server
                listeners=Mock(side_effect=[{'123'},secondary if kind=='post-listener-denied' else set() if kind=='listener-disappeared' else {'123'}])
                after_read=False
                def targets(row,*args,**kwargs):
                    nonlocal after_read
                    scan=kwargs['scan_observation']; scan['disposition']='scanned'
                    scan['socketInodes']={'values':['999'],'omitted':0}
                    after_read=True
                    return ['socket:[999]']
                def checkpoint():
                    if kind=='deadline' and after_read: raise TimeoutError('original diagnostic deadline')
                retained={'entries':[],'omitted':0}; additional=[]; child=Mock(pid=101)
                with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
                    with self.assertRaisesRegex(ValueError,'^foreign endpoint listener$') as caught:
                        module.observe_endpoint_ownership(owner,child,leader,{(102,21)},{(102,21):7},set(),checkpoint,6,listeners,additional,{'entries':[],'omitted':0},retained)
                    signals.assert_not_called(); child.poll.assert_not_called()
                self.assertEqual(1,len(retained['entries']))
                entry=retained['entries'][0]; self.assertEqual('refused',entry['disposition'])
                self.assertEqual({'type':'ValueError','message':'foreign endpoint listener'},entry['failure'])
                self.assertEqual(['123'],entry['beforeInodes']['values']); self.assertEqual([],entry['matchedInodes']['values'])
                scan=entry['fdScans'][0]; self.assertEqual(server,scan['before'])
                self.assertEqual(['999'],scan['socketInodes']['values'])
                if kind in ('post-census-denied','post-birth-denied','post-listener-denied','deadline'):
                    self.assertTrue(additional); self.assertEqual('TimeoutError' if kind=='deadline' else 'PermissionError',additional[0]['type'])
                else: self.assertEqual([],additional)
                if kind=='listener-disappeared': self.assertEqual([],entry['afterInodes']['values'])
                if kind=='birth-absent': self.assertTrue(scan['afterMismatchObserved']); self.assertIsNone(scan['afterMismatchBirth'])
                if kind=='birth-replaced': self.assertEqual(22,scan['afterMismatchBirth']['start'])
                if kind in ('post-birth-denied','deadline'): self.assertFalse(scan['afterMismatchObserved'])
                projected=module.lifecycle_summary({'endpointObservations':retained})
                self.assertEqual(scan,projected['endpointObservations']['entries'][0]['fdScans'][0])

    @staticmethod
    def _endpoint_prior_proof(server):
        members={'members':[dict(server)],'omittedMembers':0}
        inodes={'values':['123'],'omitted':0}
        return {'event':'poll','attempt':1,'disposition':'proved','failure':None,
                'beforeMembers':members,'afterMembers':members,
                'beforeInodes':dict(inodes),'afterInodes':dict(inodes),'matchedInodes':dict(inodes),
                'fdScans':[{'before':dict(server),'disposition':'scanned','missingReadlinks':0,
                            'socketInodes':dict(inodes)}]}

    def test_known_unmatched_listener_disappearance_requires_second_full_stable_proof(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        owner=Mock(); owner.census.return_value=[server]; owner.proc.return_value=server
        retained={'entries':[self._endpoint_prior_proof(server)],'omitted':0}; order=[]
        sets=[{'123'},set(),set(),set()]
        def listeners(): order.append('listener'); return sets.pop(0)
        def targets(row,*args,**kwargs):
            order.append('fd'); scan=kwargs['scan_observation']
            scan['disposition']='scanned'; scan['socketInodes']={'values':['999'],'omitted':0}
            return ['socket:[999]']
        def census(group): order.append('census'); return [server]
        owner.census.side_effect=census
        with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
            module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
            signals.assert_not_called()
        self.assertEqual(['changed','proved'],[entry['disposition'] for entry in retained['entries']])
        self.assertEqual(4,owner.census.call_count)
        self.assertEqual(['census','listener','fd','census','listener','census','listener','census','listener'],order)
        first,second=retained['entries']
        self.assertEqual({'type':'ValueError','message':'foreign endpoint listener'},first['failure'])
        self.assertEqual(['123'],first['beforeInodes']['values']); self.assertEqual([],first['afterInodes']['values'])
        self.assertEqual([],second['beforeInodes']['values']); self.assertEqual([],second['afterInodes']['values'])
        self.assertEqual(1,len(first['fdScans'])); self.assertEqual([],second['fdScans'])
        self.assertEqual([server],second['beforeMembers']['members']); self.assertEqual([server],second['afterMembers']['members'])

    def test_authenticated_leader_exit_keeps_prior_server_fd_proof_for_one_reobservation(self):
        from unittest.mock import Mock, patch
        leader={'pid':5757,'start':105065,'pgid':5757,'state':'S'}
        server={'pid':5769,'start':109670,'pgid':5757,'state':'S'}
        owner=Mock(); owner.census.side_effect=[[leader,server],[server]]+[[server]]*4
        owner.proc.return_value=server; child=Mock(pid=5757)
        retained={'entries':[],'omitted':0}; servers={(5769,109670)}; handles={(5769,109670):7}
        listeners=Mock(side_effect=[{'28643'},{'28643'},{'28643'},set(),set(),set()])
        reads=0
        def targets(row,*args,**kwargs):
            nonlocal reads
            reads+=1; scan=kwargs['scan_observation']; scan['disposition']='scanned'
            inode='28643' if reads==1 else '999'
            scan['socketInodes']={'values':[inode],'omitted':0}
            return ['socket:['+inode+']']
        with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
            module.observe_endpoint_ownership(owner,child,leader,servers,handles,set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
            prior=copy.deepcopy(retained['entries'][-1])
            self.assertEqual('proved',prior['disposition'])
            self.assertEqual([leader,server],prior['beforeMembers']['members'])
            self.assertEqual([server],prior['afterMembers']['members'])
            self.assertEqual(['28643'],prior['matchedInodes']['values'])
            module.observe_endpoint_ownership(owner,child,leader,servers,handles,set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained,'child-exit-observed')
            signals.assert_not_called(); child.poll.assert_not_called()
        self.assertEqual(['changed','proved'],[entry['disposition'] for entry in retained['entries']])
        self.assertEqual('foreign endpoint listener',retained['entries'][0]['failure']['message'])
        self.assertEqual([],retained['entries'][1]['beforeInodes']['values'])
        self.assertEqual([],retained['entries'][1]['afterInodes']['values'])
        self.assertEqual(6,owner.census.call_count); self.assertEqual(6,listeners.call_count)
        self.assertEqual(2,reads)

    def test_prior_leader_exit_exception_refuses_other_membership_or_identity_changes(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'S'}
        server=dict(leader,pid=102,start=21)
        faults=('foreign-disappeared','leader-reused','leader-group','leader-state',
                'extra-member','server-disappeared','server-replaced','leader-still-present',
                'prior-after-omitted','wrong-direct-child','missing-handle','prior-fd-unproved')
        for fault in faults:
            with self.subTest(fault=fault):
                prior=self._endpoint_prior_proof(server)
                before=[dict(server),dict(leader)]; after=[dict(server)]
                if fault=='foreign-disappeared': before[1]['pid']=103
                if fault=='leader-reused': before[1]['start']=99
                if fault=='leader-group': before[1]['pgid']=999
                if fault=='leader-state': before[1]['state']='?'
                if fault=='extra-member': before.append(dict(server,pid=104,start=22))
                if fault=='server-disappeared': after=[dict(leader)]
                if fault=='server-replaced': after=[dict(server,start=22)]
                if fault=='leader-still-present': after=[dict(server),dict(leader),dict(server,pid=104,start=22)]
                prior['beforeMembers']={'members':before,'omittedMembers':0}
                prior['afterMembers']={'members':after,'omittedMembers':1 if fault=='prior-after-omitted' else 0}
                if fault=='prior-fd-unproved': prior['fdScans'][0]['socketInodes']['values']=['999']
                retained={'entries':[prior],'omitted':0}; owner=Mock()
                owner.census.return_value=[server]; owner.proc.return_value=server
                listeners=Mock(side_effect=[{'123'},set()]); child=Mock(pid=999 if fault=='wrong-direct-child' else 101)
                def targets(row,*args,**kwargs):
                    scan=kwargs['scan_observation']; scan['disposition']='scanned'
                    scan['socketInodes']={'values':['999'],'omitted':0}
                    return ['socket:[999]']
                with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
                    with self.assertRaisesRegex(ValueError,'^foreign endpoint listener$'):
                        module.observe_endpoint_ownership(owner,child,leader,{(102,21)},{} if fault=='missing-handle' else {(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
                    signals.assert_not_called(); child.poll.assert_not_called()
                self.assertEqual('refused',retained['entries'][-1]['disposition'])
                self.assertEqual(2,owner.census.call_count); self.assertEqual(2,listeners.call_count)

    def test_unknown_mismatch_cannot_use_listener_disappearance_as_ownership(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        foreign=dict(server,pid=103,start=22)
        for fault in ('foreign-member','replaced-member','missing-readlink','socket-omission','unknown-state','birth-replaced','birth-absent','unretained-handle','previous-error'):
            with self.subTest(fault=fault):
                owner=Mock(); owner.census.side_effect=[[server],[server,foreign] if fault=='foreign-member' else [dict(server,start=22)] if fault=='replaced-member' else [server]]
                owner.proc.return_value=None if fault=='birth-absent' else dict(server,start=22) if fault=='birth-replaced' else dict(server,state='?') if fault=='unknown-state' else server
                retained={'entries':[self._endpoint_prior_proof(server)],'omitted':0}; additional=[{'type':'PermissionError','message':'earlier unknown observation'}] if fault=='previous-error' else []
                listeners=Mock(side_effect=[{'123'},set()])
                def targets(row,*args,**kwargs):
                    scan=kwargs['scan_observation']; scan['disposition']='scanned'
                    scan['missingReadlinks']=1 if fault=='missing-readlink' else 0
                    scan['socketInodes']={'values':['999'],'omitted':1 if fault=='socket-omission' else 0}
                    return ['socket:[999]']
                with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
                    with self.assertRaisesRegex(ValueError,'^foreign endpoint listener$'):
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{} if fault=='unretained-handle' else {(102,21):7},set(),Mock(),6,listeners,additional,{'entries':[],'omitted':0},retained)
                    signals.assert_not_called()
                self.assertEqual(2,len(retained['entries'])); self.assertEqual('refused',retained['entries'][-1]['disposition'])
                self.assertEqual(2,listeners.call_count); self.assertEqual(2,owner.census.call_count)

    def test_second_unmatched_disappearance_refuses_and_keeps_both_attempts(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}; server=dict(leader,pid=102,start=21,state='S')
        owner=Mock(); owner.census.return_value=[server]; owner.proc.return_value=server
        retained={'entries':[self._endpoint_prior_proof(server)],'omitted':0}; listeners=Mock(side_effect=[{'123'},set(),{'456'},set()])
        def targets(row,*args,**kwargs):
            scan=kwargs['scan_observation']; scan['disposition']='scanned'; scan['socketInodes']={'values':['999'],'omitted':0}
            return ['socket:[999]']
        with patch.object(module,'owned_fd_targets',side_effect=targets):
            with self.assertRaisesRegex(ValueError,'^foreign endpoint listener$'):
                module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
        self.assertEqual(['changed','refused'],[entry['disposition'] for entry in retained['entries']])
        self.assertEqual(4,listeners.call_count); self.assertEqual(4,owner.census.call_count)

    def test_disappearance_requires_exact_complete_prior_owned_inode_proof(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server=dict(leader,pid=102,start=21,state='S')
        faults=('no-prior','prior-refused','prior-failure','prior-before-omitted',
                'prior-after-changed','prior-matched-missing','prior-members-omitted',
                'prior-members-changed','prior-foreign-group','prior-unknown-member',
                'prior-replaced-birth','prior-scan-group','prior-scan-denied',
                'prior-readlink-missing','prior-socket-omitted','prior-socket-foreign')
        for fault in faults:
            with self.subTest(fault=fault):
                prior=self._endpoint_prior_proof(server)
                if fault=='prior-refused': prior['disposition']='refused'
                if fault=='prior-failure': prior['failure']={'type':'ValueError','message':'earlier refusal'}
                if fault=='prior-before-omitted': prior['beforeInodes']['omitted']=1
                if fault=='prior-after-changed': prior['afterInodes']['values']=['456']
                if fault=='prior-matched-missing': prior['matchedInodes']['values']=[]
                if fault=='prior-members-omitted': prior['beforeMembers']['omittedMembers']=1
                if fault=='prior-members-changed': prior['afterMembers']={'members':[],'omittedMembers':0}
                if fault=='prior-foreign-group': prior['beforeMembers']['members'][0]['pgid']=999
                if fault=='prior-unknown-member': prior['beforeMembers']['members'][0]['state']='?'
                scan=prior['fdScans'][0]
                if fault=='prior-replaced-birth': scan['before']['start']=22
                if fault=='prior-scan-group': scan['before']['pgid']=999
                if fault=='prior-scan-denied': scan['disposition']='denied'
                if fault=='prior-readlink-missing': scan['missingReadlinks']=1
                if fault=='prior-socket-omitted': scan['socketInodes']['omitted']=1
                if fault=='prior-socket-foreign': scan['socketInodes']['values']=['999']
                retained={'entries':[] if fault=='no-prior' else [prior],'omitted':0}
                owner=Mock(); owner.census.return_value=[server]; owner.proc.return_value=server
                listeners=Mock(side_effect=[{'123'},set()])
                def targets(row,*args,**kwargs):
                    scan=kwargs['scan_observation']; scan['disposition']='scanned'
                    scan['socketInodes']={'values':['999'],'omitted':0}
                    return ['socket:[999]']
                with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
                    with self.assertRaisesRegex(ValueError,'^foreign endpoint listener$'):
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
                    signals.assert_not_called()
                self.assertEqual('refused',retained['entries'][-1]['disposition'])
                self.assertEqual(2,listeners.call_count); self.assertEqual(2,owner.census.call_count)

    def test_prior_owned_disappearance_cannot_supply_second_current_proof(self):
        from unittest.mock import Mock, patch
        leader={'pid':101,'start':20,'pgid':101,'state':'R'}
        server=dict(leader,pid=102,start=21,state='S')
        for fault in ('fresh-foreign-inode','fresh-fd-denial','fresh-replaced-birth','fresh-listener-change'):
            with self.subTest(fault=fault):
                retained={'entries':[self._endpoint_prior_proof(server)],'omitted':0}
                owner=Mock(); owner.census.return_value=[server]; owner.proc.return_value=server
                listeners=Mock(side_effect=[{'123'},set(),{'456'}, {'789'} if fault=='fresh-listener-change' else {'456'}])
                reads=0
                def targets(row,*args,**kwargs):
                    nonlocal reads
                    reads+=1
                    scan=kwargs['scan_observation']; scan['disposition']='scanned'
                    scan['socketInodes']={'values':['999'],'omitted':0}
                    if reads==1: return ['socket:[999]']
                    if fault=='fresh-fd-denial': raise PermissionError('fresh unknown FD observation')
                    if fault=='fresh-replaced-birth': owner.proc.return_value=dict(server,start=22)
                    return ['socket:[999]'] if fault=='fresh-foreign-inode' else ['socket:[456]']
                with patch.object(module,'owned_fd_targets',side_effect=targets), patch.object(module.signal,'pidfd_send_signal') as signals:
                    with self.assertRaises((ValueError,PermissionError)):
                        module.observe_endpoint_ownership(owner,Mock(pid=101),leader,{(102,21)},{(102,21):7},set(),Mock(),6,listeners,[],{'entries':[],'omitted':0},retained)
                    signals.assert_not_called()
                self.assertEqual(['changed','refused'],[entry['disposition'] for entry in retained['entries']])
                self.assertEqual('foreign endpoint listener',retained['entries'][0]['failure']['message'])

    def test_fd_scan_diagnostics_bound_socket_inodes_and_distinguish_disappearance(self):
        from unittest.mock import Mock, patch
        row={'pid':102,'start':21,'pgid':101,'state':'S'}
        def fresh_scan():
            return {'before':row,'disposition':'pending','missingReadlinks':0,
                    'socketInodes':{'values':[],'omitted':0},'afterMismatchObserved':False,'afterMismatchBirth':None}
        missing=fresh_scan()
        with patch.object(module.Path,'iterdir',side_effect=FileNotFoundError()):
            self.assertEqual([],module.owned_fd_targets(row,Mock(),scan_observation=missing))
        self.assertEqual('directory-missing',missing['disposition'])
        scan=fresh_scan(); targets=['socket:['+str(i)+']' for i in range(8)]
        reads=targets+[FileNotFoundError()]
        with patch.object(module.Path,'iterdir',return_value=iter(range(9))), patch.object(module.os,'readlink',side_effect=reads):
            self.assertEqual(targets,module.owned_fd_targets(row,Mock(),scan_observation=scan))
        self.assertEqual('scanned',scan['disposition']); self.assertEqual(1,scan['missingReadlinks'])
        self.assertEqual({'values':[str(i) for i in range(6)],'omitted':2},scan['socketInodes'])

    def test_lifecycle_byte_cap_retains_structured_primary_cause(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); scratch=root/'scratch'; scratch.mkdir(); large=10**120
            cause={'type':'PermissionError','message':'original observation refusal'}
            value={'schema':module.RESULT_SCHEMA,'commandExit':0,'commandAccepted':False,'firstCause':cause,'additionalFailures':[],
                   'ownerReceipt':{'exitObservation':{'code':0,'members':[{'pid':large+i,'start':large,'pgid':large,'state':'S'} for i in range(32)],'omittedMembers':0}}}
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value))
            artifact=module.capture(scratch,root/'diagnostics',1)
            retained=json.loads(artifact['logs'][0]['text'])
            self.assertEqual(cause,retained['firstCause']); self.assertIn('lifecycle-byte-bound',artifact['omittedCoverage'])
            self.assertEqual('lifecycle summary byte cap',retained['lifecycle']['projectionFailure']['message'])
            # Non-lifecycle overflow must also remain complete typed JSON.
            large_message='original primary observation '+('\u2603'*16000)
            cause={'type':'PermissionError','message':large_message}
            value={'schema':module.RESULT_SCHEMA,'commandExit':0,'commandAccepted':False,
                   'firstCause':cause,'additionalFailures':[{'type':'ValueError','message':'secondary '*100} for _ in range(8)],
                   'ownerReceipt':{'firstCause':cause,'additionalFailures':[{'type':'RuntimeError','message':'owner secondary '*100} for _ in range(7)]}}
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value,ensure_ascii=False))
            artifact=module.capture(scratch,root/'overflow-diagnostics',1)
            retained=json.loads(artifact['logs'][0]['text'])
            self.assertEqual({'type':'PermissionError','message':large_message[:224]},retained['firstCause'])
            self.assertEqual(retained['firstCause'],retained['owner']['firstCause'])
            omissions=retained['omittedProjection']
            self.assertEqual(len(large_message)-224,omissions['firstCause']['messageCharacters'])
            self.assertEqual(8,omissions['additionalFailures']); self.assertEqual(7,omissions['ownerAdditionalFailures'])
            self.assertIn('typed-summary-byte-bound',artifact['omittedCoverage'])
            self.assertLessEqual(len(artifact['logs'][0]['text'].encode()),module.FILE_BYTES)

    def test_denied_fd_direct_child_probe_requires_fresh_terminal_original_birth(self):
        from unittest.mock import Mock, patch
        row={'pid':101,'start':20,'pgid':101,'state':'R'}
        for operation in ('directory','readlink'):
            for disposition in ('absent','replaced','zombie','live','unknown','changed-group','still-running','poll-error'):
                with self.subTest(operation=operation,disposition=disposition):
                    original=PermissionError('original FD denial')
                    next_birth = None if disposition == 'absent' else dict(row,start=21) if disposition == 'replaced' else dict(row,state='Z') if disposition == 'zombie' else dict(row,pgid=999) if disposition == 'changed-group' else PermissionError('fresh birth unknown') if disposition == 'unknown' else row
                    observe=Mock(side_effect=[row,next_birth])
                    probe=Mock(return_value=None if disposition == 'still-running' else 0)
                    if disposition == 'poll-error': probe.side_effect=OSError('original Popen observation failed')
                    additional=[]; retained={'entries':[],'omitted':0}
                    with patch.object(module.Path,'iterdir',side_effect=original if operation == 'directory' else None,
                                      return_value=[Path('/proc/101/fd/7')]), \
                         patch.object(module.os,'readlink',side_effect=original):
                        if disposition in ('absent','replaced','zombie'):
                            self.assertEqual([],module.owned_fd_targets(row,observe,additional,terminal_probe=probe,denial_observations=retained))
                        else:
                            with self.assertRaises(PermissionError) as caught:
                                module.owned_fd_targets(row,observe,additional,terminal_probe=probe,denial_observations=retained)
                            self.assertIs(original,caught.exception)
                    probe.assert_called_once()
                    self.assertEqual(operation,retained['entries'][0]['operation'])
                    self.assertEqual(row,retained['entries'][0]['freshBeforeTerminalPoll'])
                    self.assertEqual(disposition == 'still-running' or disposition == 'poll-error',observe.call_count == 1)
                    self.assertEqual(disposition in ('unknown','changed-group','poll-error'),bool(additional))

    def test_denied_fd_probe_does_not_run_after_unknown_or_changed_group_and_observations_are_bounded(self):
        from unittest.mock import Mock, patch
        row={'pid':101,'start':20,'pgid':101,'state':'S'}
        for fresh in (PermissionError('unknown birth'),dict(row,pgid=999),row):
            with self.subTest(fresh=fresh):
                original=PermissionError('original FD denial'); probe=Mock()
                retained={'entries':[{}]*6,'omitted':0}; additional=[]
                observe=Mock(side_effect=fresh) if isinstance(fresh,BaseException) else Mock(return_value=fresh)
                with patch.object(module.Path,'iterdir',side_effect=original):
                    with self.assertRaises(PermissionError) as caught:
                        module.owned_fd_targets(row,observe,additional,terminal_probe=probe if isinstance(fresh,BaseException) or fresh['pgid'] != row['pgid'] else None,denial_observations=retained)
                    self.assertIs(original,caught.exception)
                probe.assert_not_called(); self.assertEqual(6,len(retained['entries'])); self.assertEqual(1,retained['omitted'])

    def test_reaping_in_denied_fd_probe_cannot_bypass_owner_terminal_callback(self):
        from unittest.mock import Mock, patch
        with tempfile.TemporaryDirectory() as tmp:
            spec=importlib.util.spec_from_file_location('fixture_owner',ROOT/'eng/shared_process_owner.py')
            owner=importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
            leader={'pid':101,'start':20,'pgid':101,'ppid':os.getpid(),'state':'S','rss':1}
            child=Mock(pid=101,returncode=0); child.poll.return_value=0
            observe=Mock(side_effect=[leader,None]); events=[]; retained={'entries':[],'omitted':0}
            def hook(event,*args):
                events.append(event)
                if event == 'poll':
                    original=PermissionError('transition FD denial')
                    with patch.object(module.Path,'iterdir',side_effect=original):
                        self.assertEqual([],module.owned_fd_targets(leader,observe,terminal_probe=child.poll,denial_observations=retained))
                if event == 'child-exit-observed': self.assertGreaterEqual(child.poll.call_count,2)
            with patch.object(owner.subprocess,'Popen',return_value=child), \
                 patch.object(owner,'proc',side_effect=lambda pid: leader if pid == 101 else dict(leader,pid=pid)), \
                 patch.object(owner,'census',return_value=[]), \
                 patch.object(owner,'observed_escapes',return_value=[]),patch.object(owner.os,'killpg'):
                owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,str(Path(tmp)/'run.log'),
                          control=hook,observe_child_exit=True)
            self.assertEqual(1,events.count('child-exit-observed'))
            self.assertLess(events.index('poll'),events.index('child-exit-observed'))
            self.assertEqual(0,retained['entries'][0]['terminalExit'])

    def test_actual_retirement_path_fresh_sole_server_and_live_unknown_foreign_refusals(self):
        from unittest.mock import Mock, patch
        server={'pid':102,'start':21,'pgid':101,'state':'S'}
        leader={'pid':101,'start':20,'pgid':101,'state':'S'}
        for kind in ('sole','empty','foreign','multiple','replaced','changed-group','unknown','live','nonzero','generation-cap'):
            with self.subTest(kind=kind):
                child=Mock(returncode=None if kind == 'live' else 37 if kind == 'nonzero' else 0)
                rows=[] if kind == 'empty' else [server,leader] if kind in ('foreign','multiple') else [server]
                owner=Mock(); owner.census.return_value=rows
                owner.proc.return_value=dict(server,start=22) if kind == 'replaced' else dict(server,pgid=999) if kind == 'changed-group' else server
                if kind == 'unknown': owner.proc.side_effect=PermissionError('unknown birth')
                servers={(102,21),(103,22)} if kind == 'multiple' else {(102,21)}
                stops=[]; known=set(); checkpoint=Mock(); observation={'members':None}
                with patch.object(module.signal,'pidfd_send_signal') as send:
                    args=(owner,child,leader,servers,{(102,21):77},stops,checkpoint,known,1 if kind == 'generation-cap' else 6,observation)
                    if kind in ('replaced','changed-group','unknown','live','generation-cap'):
                        with self.assertRaises((ValueError,PermissionError)): module.retire_original_server(*args)
                    else:
                        members=module.retire_original_server(*args)
                        self.assertEqual({(r['pid'],r['start']) for r in rows},members)
                    if kind == 'sole':
                        send.assert_called_once_with(77,module.signal.SIGTERM)
                        self.assertEqual((102,21),(stops[0]['pid'],stops[0]['start']))
                    else:
                        send.assert_not_called(); self.assertEqual([],stops)
                    child.poll.assert_not_called()
                    if kind == 'live': self.assertIsNone(observation['members'])
                    else: self.assertEqual({(r['pid'],r['start']) for r in rows},observation['members'])

    def test_opt_in_owner_exit_hook_precedes_fresh_refusal_and_preserves_default_events(self):
        from unittest.mock import Mock, patch
        for opt_in in (False, True):
            for remaining in ([], [{'pid':102,'start':21,'pgid':101,'state':'S','rss':1}]):
                with self.subTest(opt_in=opt_in, remaining=remaining), tempfile.TemporaryDirectory() as tmp:
                    spec = importlib.util.spec_from_file_location('fixture_owner', ROOT / 'eng/shared_process_owner.py')
                    owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
                    leader = {'pid':101,'start':20,'pgid':101,'ppid':os.getpid(),'state':'S','rss':1}
                    child = Mock(pid=101, returncode=0); child.poll.return_value=0
                    events = []; observations = []
                    def hook(event, child, birth, known):
                        events.append(event)
                        if event == 'child-exit-observed':
                            self.assertGreater(child.poll.call_count,0)
                            self.assertEqual(1, census.call_count)
                            observations.append(dict(known))
                    with patch.object(owner.subprocess,'Popen',return_value=child), \
                         patch.object(owner,'proc',side_effect=lambda pid: leader if pid == 101 else dict(leader,pid=pid,pgid=pid)), \
                         patch.object(owner,'census',side_effect=[[leader],remaining,[],[],[]]) as census, \
                         patch.object(owner,'observed_escapes',return_value=[]), \
                         patch.object(owner.os,'killpg') as killpg:
                        log = str(Path(tmp)/'run.log')
                        if remaining:
                            with self.assertRaisesRegex(RuntimeError,'live owned-group descendants'):
                                owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                          control=hook,observe_child_exit=opt_in)
                        else:
                            owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                      control=hook,observe_child_exit=opt_in)
                    self.assertEqual(opt_in,'child-exit-observed' in events)
                    self.assertEqual(['before-popen','after-birth','poll'],events[:3])
                    self.assertEqual('after-cleanup',events[-1])
                    receipt = json.loads(Path(log+'.result.json').read_text())
                    self.assertEqual(remaining and [{'pid':102,'start':21,'pgid':101,'state':'S'}] or [],receipt['exitObservation']['members'])
                    self.assertEqual('observed-empty-group',receipt['cleanup'])

    def test_unknown_birth_and_hook_failure_do_not_license_terminal_success(self):
        from unittest.mock import Mock, patch
        for unknown in (False,True):
            with self.subTest(unknown=unknown), tempfile.TemporaryDirectory() as tmp:
                spec = importlib.util.spec_from_file_location('fixture_owner', ROOT / 'eng/shared_process_owner.py')
                owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)
                leader = {'pid':101,'start':20,'pgid':101,'ppid':os.getpid(),'state':'S','rss':1}
                child=Mock(pid=101,returncode=0); child.poll.return_value=0
                original=PermissionError('terminal observation unavailable'); events=[]
                def hook(event,*args):
                    events.append(event)
                    if event == 'child-exit-observed': raise original
                with patch.object(owner.subprocess,'Popen',return_value=child), \
                     patch.object(owner,'proc',side_effect=lambda pid: None if unknown else dict(leader,pid=pid)), \
                     patch.object(owner,'census',return_value=[]), \
                     patch.object(owner,'observed_escapes',return_value=[]), \
                     patch.object(owner.os,'killpg') as killpg, patch.object(owner.os,'kill') as kill:
                    log=str(Path(tmp)/'run.log')
                    with self.assertRaises(BaseException) as caught:
                        owner.run(['fixture'],tmp,{},owner.time.monotonic()+10,0,100,log,
                                  control=hook,observe_child_exit=True)
                    if unknown:
                        self.assertNotIn('child-exit-observed',events)
                        killpg.assert_not_called(); kill.assert_not_called()
                    else:
                        self.assertIs(original,caught.exception)
                receipt=json.loads(Path(log+'.result.json').read_text())
                self.assertEqual(unknown,receipt['birthIdentityUnavailable'])
                self.assertIsNone(receipt['exitObservation'] if unknown else receipt['exitObservation']['members'])

    def test_lifecycle_projection_preserves_unknown_empty_and_refusing_identity(self):
        unknown=module.lifecycle_summary({'ownerReceipt':{}})
        self.assertIsNone(unknown['adapterExitMembership'])
        self.assertIsNone(unknown['ownerExitObservation'])
        value={'originalServerBirths':[{'pid':102,'start':21,'private':'NEVER COPY'}],
               'exitMembers':[], 'originalServerStopRequests':[],
               'ownerReceipt':{'exitObservation':{'code':0,'members':[{'pid':103,'start':22,'pgid':101,'state':'S','private':'NEVER COPY'}], 'omittedMembers':0}},
               'input':{'private':'NEVER COPY'}}
        value['ownerReceipt']['leader']={'pid':101,'start':20,'pgid':101,'state':'S','private':'NEVER COPY'}
        value['fdDenialObservations']={'entries':[{'operation':'directory','before':{'pid':101,'start':20,'pgid':101,'state':'R'},'freshBeforeTerminalPoll':{'pid':101,'start':20,'pgid':101,'state':'R'},'terminalPollAttempted':True,'terminalExit':0,'freshAfterTerminalPoll':None,'observationFailure':None,'private':'NEVER COPY'}],'omitted':0}
        value['endpointObservations']={'entries':[{'event':'poll','attempt':2,'disposition':'refused',
            'beforeInodes':{'values':['123'],'omitted':0,'private':'NEVER COPY'},'afterInodes':{'values':['456'],'omitted':0},
            'matchedInodes':{'values':['123'],'omitted':0},'beforeMembers':{'members':[{'pid':102,'start':21,'pgid':101,'state':'S'}],'omittedMembers':0},
            'afterMembers':None,'failure':{'type':'ValueError','message':'endpoint changed'},'private':'NEVER COPY'}],'omitted':9}
        summary=module.lifecycle_summary(value)
        self.assertEqual(9,summary['endpointObservations']['omitted'])
        self.assertEqual(['456'],summary['endpointObservations']['entries'][0]['afterInodes']['values'])
        self.assertEqual([],summary['adapterExitMembership']['members'])
        self.assertEqual(103,summary['ownerExitObservation']['membership']['members'][0]['pid'])
        self.assertNotIn('NEVER COPY',json.dumps(summary))
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); scratch=root/'scratch'; scratch.mkdir()
            value.update(schema=module.RESULT_SCHEMA,commandExit=0,commandAccepted=False,firstCause={'type':'RuntimeError','message':'live owned-group descendants'},additionalFailures=[])
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value))
            artifact=module.capture(scratch,root/'diagnostics',1)
            retained=json.loads(artifact['logs'][0]['text'])
            self.assertEqual(summary,retained['lifecycle'])
            self.assertNotIn('NEVER COPY',json.dumps(artifact))
            value['ownerReceipt']['exitObservation']['omittedMembers'] = {'private':'NEVER COPY'}
            (scratch/'runner.log.attempt-1.command.json').write_text(json.dumps(value))
            malformed=module.capture(scratch,root/'malformed-diagnostics',1)
            self.assertIn('live owned-group descendants',malformed['logs'][0]['text'])
            self.assertIn('lifecycle-fields-unavailable',malformed['omittedCoverage'])
            self.assertNotIn('NEVER COPY',json.dumps(malformed))

    def test_denied_fd_observation_requires_fresh_terminal_original_birth(self):
        from unittest.mock import Mock, patch
        row = dict(pid=101, start=20, pgid=101, state='R')
        vectors = [(None, True), (dict(row, start=21), True),
                   (dict(row, state='Z'), True), (dict(row), False),
                   (dict(row, state='S'), False), (dict(row, state='Z', pgid=102), False)]
        for operation in ('directory', 'link'):
            for fresh, terminal in vectors:
                with self.subTest(operation=operation, fresh=fresh):
                    error = PermissionError('original denied FD observation')
                    observe = Mock(return_value=fresh)
                    additional = []
                    with patch.object(module.Path, 'iterdir', side_effect=error if operation == 'directory' else None,
                                      return_value=[Path('/proc/101/fd/7')]), \
                         patch.object(module.os, 'readlink', side_effect=error):
                        if terminal:
                            self.assertEqual([], module.owned_fd_targets(row, observe, additional))
                        else:
                            with self.assertRaises(PermissionError) as caught:
                                module.owned_fd_targets(row, observe, additional)
                            self.assertIs(error, caught.exception)
                        if fresh is not None and fresh['start'] == row['start'] and fresh['pgid'] != row['pgid']:
                            self.assertEqual('ValueError', additional[0]['type'])
                            self.assertIn('server group changed', additional[0]['message'])
                        else:
                            self.assertEqual([], additional)
                    observe.assert_called_once_with(row['pid'])

    def test_fd_observation_retains_targets_bounds_and_unknown_birth_failure(self):
        from unittest.mock import Mock, patch
        row = dict(pid=101, start=20, pgid=101, state='R')
        with patch.object(module.Path, 'iterdir', return_value=[Path('/proc/101/fd/7')]), \
             patch.object(module.os, 'readlink', return_value='socket:[123]'):
            self.assertEqual(['socket:[123]'], module.owned_fd_targets(row, Mock()))
        with patch.object(module.Path, 'iterdir', return_value=[Path('/proc/101/fd/7')] * 257):
            with self.assertRaisesRegex(ValueError, 'server FD cap'):
                module.owned_fd_targets(row, Mock())
        original = PermissionError('original denied FD observation')
        additional = []
        with patch.object(module.Path, 'iterdir', side_effect=original):
            with self.assertRaises(PermissionError) as caught:
                module.owned_fd_targets(row, Mock(side_effect=PermissionError('fresh birth unreadable')), additional)
            self.assertIs(original, caught.exception)
            self.assertEqual([module.failure_record(PermissionError('fresh birth unreadable'))], additional)

    def spec(self):
        return dict(schema=module.COMMAND_SCHEMA, role='verify', argv=['/quint','verify','/model'],
                    pins=[{'path':'/input'+str(i),'bytes':10,'sha256':'a'*64} for i in range(5)], aliases=[],
                    semanticToolchainId=module.TOOLCHAIN_ID, archiveSha256='b'*64,
                    started=100.0, workDeadline=250.0, totalDeadline=280.0, workSeconds=150,
                    cleanupSeconds=30, rssBytes=6*1024**3, logBytes=8*1024**2, maximumGenerations=6,
                    endpoint=19822, jvmArgs=['-Xmx1024m','-Xms64m'],
                    tlcConfig={'maxHeap':'-Xmx2G','stackSize':'-Xss64m','workers':1},
                    mutableScope={'cwd':'/scope','tmpdir':'/scope/tmp','diskBytes':1073741824,
                                  'inventorySeconds':1,'maximumEntries':25000,'sampleIntervalSeconds':.5,
                                  'javaToolOptions':'-Djava.io.tmpdir=/scope/tmp',
                                  'denoDir':'/scope/cache/deno','xdgCacheHome':'/scope/cache'})

    def test_actual_shape_separates_semantic_identity_from_archive_bytes(self):
        result = module.command_preflight(self.spec(), now=101)
        self.assertTrue(result['passed'])
        self.assertNotEqual(self.spec()['semanticToolchainId'], self.spec()['archiveSha256'])

    def test_resource_clock_and_actual_field_contradictions_refuse(self):
        replacements = [('role','watch'), ('semanticToolchainId','b'*64), ('archiveSha256','not-a-digest'),
                        ('workDeadline',251), ('totalDeadline',281), ('started',float('nan')),
                        ('jvmArgs',['-Xmx4096m']), ('rssBytes',2*1024**3), ('maximumGenerations',7),
                        ('tlcConfig',{'maxHeap':'-Xmx8G','stackSize':'-Xss515m','workers':'auto'})]
        for field, value in replacements:
            with self.subTest(field=field):
                spec = self.spec(); spec[field] = value
                with self.assertRaises(ValueError): module.command_preflight(spec, now=101)
        with self.assertRaises(ValueError): module.command_preflight(self.spec(), now=250)
        spec = self.spec(); del spec['pins']
        with self.assertRaises(ValueError): module.command_preflight(spec, now=101)

    def test_at_watch_does_not_repurpose_a_dispatch_runtime_entry(self):
        self.assertEqual('watch', module.command_role('credential-entry','watch'))
        with self.assertRaises(ValueError): module.command_role('dispatch-runtime-entry','watch')
        self.assertEqual('dispatch', module.command_role('dispatch-runtime-entry','dispatch'))

    def test_actual_launch_grant_uses_original_sixty_seconds(self):
        module.validate_launch_grant({'startedAtEpoch':1000,'launchBeforeEpoch':1060})
        for grant in [{'startedAtEpoch':1000,'absoluteDeadlineEpoch':1060},
                      {'startedAtEpoch':1000,'launchBeforeEpoch':1120},
                      {'startedAtEpoch':float('nan'),'launchBeforeEpoch':float('nan')}]:
            with self.assertRaises(ValueError): module.validate_launch_grant(grant)

    def test_actual_extraction_fields_do_not_establish_compiler_acceptance(self):
        receipt = dict(accepted=True, selectedManifestSha256='a'*64,
                       allSelectedBytesAndModesVerified=True, extractedCodeExecuted=False, providerRequests=0)
        self.assertEqual({'extractionAccepted':True,'compilerAccepted':False,'ciAccepted':False},
                         module.extraction_receipt_join(receipt,'a'*64))
        wrong = copy.deepcopy(receipt); del wrong['allSelectedBytesAndModesVerified']
        wrong['allBytesModesVerified'] = True
        with self.assertRaises(ValueError): module.extraction_receipt_join(wrong,'a'*64)
        with self.assertRaises(ValueError): module.extraction_receipt_join(receipt,'b'*64)
        with self.assertRaises(ValueError): module.extraction_receipt_join(receipt,None)
        wrong = copy.deepcopy(receipt); wrong['providerRequests'] = False
        with self.assertRaises(ValueError): module.extraction_receipt_join(wrong,'a'*64)

    def test_expected_server_retirement_cannot_accept_foreign_live_descendants(self):
        receipt = {'exit':0,'firstCause':'RuntimeError: child exited with live owned-group descendants',
                   'leader':{'pgid':101},'exitObservation':{'code':0,'omittedMembers':0,
                   'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}]}}
        stops = [{'pid':101,'start':20,'signal':15,'disposition':'sent'}]
        self.assertTrue(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},stops))
        self.assertFalse(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},[]))
        for servers, members in [(set(),set()), ({(101,20)},{(101,20),(102,21)}),
                                 ({(101,20)},{(101,19)}), ({(101,20)},None), ({(101,20),(102,21)},{(101,20),(102,21)})]:
            with self.subTest(servers=servers,members=members):
                self.assertFalse(module.expected_server_retirement(receipt,servers,members,stops))
        for observation in (None, {}, {'code':0,'members':None,'omittedMembers':None},
                            {'code':0,'members':[],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}, {'pid':102,'start':21,'pgid':101,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':19,'pgid':101,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':999,'state':'S'}],'omittedMembers':0},
                            {'code':0,'members':[{'pid':101,'start':20,'pgid':101,'state':'S'}],'omittedMembers':1}):
            with self.subTest(ownerObservation=observation):
                later = copy.deepcopy(receipt); later['exitObservation'] = observation
                # Adapter sole-server membership and recorded stop never waive
                # the owner's later unknown/foreign/replaced/partial refusal.
                self.assertFalse(module.expected_server_retirement(later,{(101,20)},{(101,20)},stops))
        receipt['exit'] = 37
        self.assertFalse(module.expected_server_retirement(receipt,{(101,20)},{(101,20)},stops))

    def test_first_failure_projection_is_serializable_bounded_and_filters_secrets(self):
        original = module.failure_record(RuntimeError('original compiler failure'))
        secondary = module.failure_record(OSError('report failed'))
        body = json.loads(json.dumps({'firstCause':original,'additionalFailures':[secondary]}))
        self.assertEqual('original compiler failure',body['firstCause']['message'])
        self.assertEqual('report failed',body['additionalFailures'][0]['message'])
        self.assertLessEqual(len(module.failure_record(ValueError('x'*4096))['message']),512)
        self.assertNotIn('synthetic-secret',module.failure_record(ValueError('Authorization: Bearer synthetic-secret'))['message'])


if __name__ == '__main__': unittest.main()
