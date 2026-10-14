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


def synthetic_common_archive(path, inventory, *, base=(), final=(), config_update=None, wrong_diff=False):
    """Synthetic OCI transport fixture; no real image/trust/transaction success."""
    import io
    import tarfile
    import gzip
    def tar_layer(entries):
        out=io.BytesIO()
        with tarfile.open(fileobj=out,mode='w') as archive:
            for name,kind,value in entries:
                member=tarfile.TarInfo(name);member.mtime=0
                if kind=='file':
                    member.size=len(value);archive.addfile(member,io.BytesIO(value))
                elif kind=='symlink':
                    member.type=tarfile.SYMTYPE;member.linkname=value;archive.addfile(member)
                elif kind=='hardlink':
                    member.type=tarfile.LNKTYPE;member.linkname=value;archive.addfile(member)
                elif kind=='directory':
                    member.type=tarfile.DIRTYPE;archive.addfile(member)
        return out.getvalue()
    raw_layers=[tar_layer(base)] if base else []
    raw_layers.append(tar_layer([('opt/fsgg/package-inventory.txt','file',inventory),*final]))
    compressed=[gzip.compress(value,mtime=0) for value in raw_layers]
    config={'architecture':'amd64','os':'linux','config':{'User':'32768:32768','WorkingDir':'/workspace',
            'Env':['HOME=/home/fsgg'],'Entrypoint':[],'Cmd':[],'Volumes':{}},
            'rootfs':{'type':'layers','diff_ids':['sha256:'+digest(value) for value in raw_layers]}}
    if config_update:config['config'].update(config_update)
    if wrong_diff:config['rootfs']['diff_ids'][0]='sha256:'+'0'*64
    config_bytes=IMAGE.canonical_bytes(config);config_sha=digest(config_bytes)
    manifest={'schemaVersion':2,'mediaType':'application/vnd.oci.image.manifest.v1+json',
              'config':{'mediaType':'application/vnd.oci.image.config.v1+json','digest':'sha256:'+config_sha,'size':len(config_bytes)},
              'layers':[{'mediaType':'application/vnd.oci.image.layer.v1.tar+gzip','digest':'sha256:'+digest(value),'size':len(value)} for value in compressed]}
    manifest_bytes=IMAGE.canonical_bytes(manifest);manifest_sha=digest(manifest_bytes)
    index=IMAGE.canonical_bytes({'schemaVersion':2,'manifests':[{'mediaType':manifest['mediaType'],
           'digest':'sha256:'+manifest_sha,'size':len(manifest_bytes),'annotations':{'org.opencontainers.image.ref.name':IMAGE.COMMON_IMAGE_NAME}}]})
    blobs={'oci-layout':IMAGE.canonical_bytes({'imageLayoutVersion':'1.0.0'}),'index.json':index,
           'blobs/sha256/'+config_sha:config_bytes,'blobs/sha256/'+manifest_sha:manifest_bytes,
           **{'blobs/sha256/'+digest(value):value for value in compressed}}
    with tarfile.open(path,'w') as archive:
        for name in ('blobs','blobs/sha256'):
            member=tarfile.TarInfo(name);member.type=tarfile.DIRTYPE;archive.addfile(member)
        for name,value in blobs.items():
            member=tarfile.TarInfo(name);member.size=len(value);archive.addfile(member,io.BytesIO(value))
    return 'sha256:'+manifest_sha,'sha256:'+config_sha,config['config']


class SyntheticNativeTransport:
    """Inert actual maintained-call transport, always visibly test-only."""
    def __init__(self,args,checked_inventory,candidate):
        import subprocess
        self.result_type=subprocess.CompletedProcess
        self.args=args;self.prefix=IMAGE.podman_prefix(args);self.inventory=checked_inventory
        self.candidate=candidate;self.calls=[];self.containers={};self.next=1
        self.digest,self.image_id,self.config=synthetic_common_archive(candidate,checked_inventory)
        self.build_digest='sha256:'+'e'*64
        self.fail_case=False;self.fail_cleanup=False;self.wrong_donor=False;self.wrong_storage=False
    def __call__(self,argv,**kwargs):
        self.calls.append(argv)
        assert kwargs.get('capture_limit_bytes')==IMAGE.COMMON_CAPTURE_LIMIT_BYTES,'common native capture bound lost'
        assert argv[:len(self.prefix)]==self.prefix,'closed engine prefix lost'
        tail=argv[len(self.prefix):];command=tail[0]
        code=0;stdout='';stderr=''
        if tail==['info','--format=json']:
            store={'graphRoot':str(Path(self.args.root).resolve()),'runRoot':str(Path(self.args.runroot).resolve()),'graphDriverName':'vfs'}
            if self.wrong_storage:store['graphRoot']='/wrong'
            stdout=json.dumps({'store':store,'host':{'os':'linux','arch':'amd64','buildahVersion':'1.45.1',
                              'security':{'rootless':True},'idMappings':{'uidmap':[{'container_id':0,'size':65536}]}}})
        elif command=='version':stdout='6.1.3\n'
        elif len(tail)==2 and tail[1]=='--help':
            stdout='\n'.join((*IMAGE.REQUIRED_BUILD_OPTIONS,*IMAGE.REQUIRED_CREATE_OPTIONS,'--cap-drop','--security-opt','--isolation','--hooks-dir','--http-proxy'))
        elif tail[:2]==['image','inspect']:
            if tail[2]==IMAGE.COMMON_DONOR_REFERENCE:
                stdout=json.dumps([{'Os':'linux','Architecture':'amd64','Id':IMAGE.COMMON_DONOR_CONFIG_DIGEST,
                                  'Digest':'sha256:'+'0'*64 if self.wrong_donor else IMAGE.COMMON_DONOR_REFERENCE.split('@')[1],
                                  'RepoDigests':[IMAGE.COMMON_DONOR_REFERENCE]}])
            else:
                stdout=json.dumps([{'Os':'linux','Architecture':'amd64','Id':self.image_id,'Digest':self.build_digest,'Config':self.config}])
        elif command=='ps':stdout='[]'
        elif command=='build':
            assert '--pull=never' in tail and '--isolation=rootless' in tail and '--network=none' in tail
            assert '--cap-drop=NET_BIND_SERVICE' in tail and '--cap-drop=all' not in tail
            assert '--security-opt=no-new-privileges' in tail and not any(value.startswith('--cap-add') for value in tail)
            assert '--hooks-dir='+str(Path(self.args.engine_allocation)/'hooks') in tail
            stdout='SYNTHETIC BUILD SUCCESS\n'
        elif command=='save':
            assert tail[1]=='--format=oci-archive' and tail[-1]==IMAGE.COMMON_IMAGE_NAME
            import shutil
            shutil.copyfile(self.candidate,Path(tail[tail.index('--output')+1]));stdout=''
        elif command=='create':
            cid=f'{self.next:064x}';self.next+=1
            run_id=next(value.split('=',2)[2] for value in tail if value.startswith('--label=fsgg.portable-workspace.run='))
            self.containers[cid]={'Id':cid,'Image':self.image_id,'Config':{'Labels':{'fsgg.portable-workspace.run':run_id}},
                                  'State':{'ExitCode':1 if self.fail_case else 0,'Running':False}}
            stdout=cid+'\n'
        elif command=='start':
            code=1 if self.fail_case else 0;stdout='SYNTHETIC CASE\n';stderr='SYNTHETIC FIRST CASE FAILURE' if self.fail_case else ''
        elif tail[:2]==['container','inspect']:stdout=json.dumps([self.containers[tail[2]]])
        elif tail[:2]==['container','rm']:
            if self.fail_cleanup:code=125;stderr='SYNTHETIC CLEANUP FAILURE'
            else:self.containers.pop(tail[-1])
        elif tail[:2]==['container','exists']:code=0 if tail[2] in self.containers else 1
        else:raise AssertionError('unexpected native transport argv '+repr(tail))
        return self.result_type(argv,code,stdout,stderr)


class CommonArchInputs(unittest.TestCase):
    def setUp(self):
        import subprocess
        self.real_popen = subprocess.Popen
        for target in ('subprocess.run', 'subprocess.Popen', 'urllib.request.urlopen'):
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

    def test_real_checker_preserves_opaque_epoch_filename(self):
        # Synthetic payload only; mirrors authentic pacman epoch spelling without
        # replacing authentic package names or inventing resolver/trust acceptance.
        a=self.lock['acquiredInputs'];package=a['packages'][0]
        old=package['path'];new=old.replace('python-3-1-any','python-1:3-1-any')
        (self.bundle/old).rename(self.bundle/new)
        (self.bundle/(old+'.sig')).rename(self.bundle/(new+'.sig'))
        package.update(path=new,version='1:3-1');package['signature']['carrierPath']=new+'.sig'
        for descriptor in a['files']:
            if descriptor['path'] in (old,old+'.sig'):
                descriptor['path']=descriptor['path'].replace(old,new)
        inventory=b'donor 1-1\npython 1:3-1\n'
        final=a['transaction']['finalInventory'];(self.bundle/final['path']).write_bytes(inventory)
        final.update(bytes=len(inventory),sha256=digest(inventory))
        for descriptor in a['files']:
            if descriptor['role']=='inventory':descriptor.update(bytes=len(inventory),sha256=digest(inventory))
        a['files'].sort(key=lambda f:f['path']);self.refresh_checksums()
        self.assertIn(new,self.validate());self.assertIn(new+'.sig',self.validate())
        self.assertIn(new.removeprefix('acquired/').encode(),(self.bundle/'acquired/SHA256SUMS').read_bytes())

    def test_epoch_basename_does_not_relax_path_guards(self):
        original=copy.deepcopy(self.lock)
        for basename in ('python-1:3/../../escape.pkg.tar.zst','python-1:3\t-1.pkg.tar.zst',
                         'python-1:3\n-1.pkg.tar.zst','python-1:3\\-1.pkg.tar.zst',
                         'python-1:3@-1.pkg.tar.zst','python-1:3-1.pkg.tar.zst/extra'):
            self.lock=copy.deepcopy(original)
            package=next(f for f in self.lock['acquiredInputs']['files'] if f['role']=='package')
            package['path']='acquired/repositories/core/os/x86_64/'+basename
            with self.subTest(basename=basename):
                with self.assertRaises(RuntimeError):self.validate()
        self.lock=original

    def native_fixture(self):
        image=self.root/'native-image';recipe=image/'deployment/provisioned-podman';recipe.mkdir(parents=True)
        recipe_bytes={'Containerfile':b'SYNTHETIC native recipe\n','pacman.conf':b'SYNTHETIC native config\n','inputs.json':IMAGE.canonical_bytes(self.source)}
        pins={name:digest(value) for name,value in recipe_bytes.items()}
        for name,value in recipe_bytes.items():(recipe/name).write_bytes(value)
        self.identity.update(containerfileSha256=pins['Containerfile'],pacmanConfigSha256=pins['pacman.conf'],inputsSha256=pins['inputs.json'])
        self.lock['acquiredInputs']['trust']['finalConfigSha256']=pins['pacman.conf']
        payload=IMAGE.canonical_bytes(self.lock);(self.bundle/'inputs.acquired.json').write_bytes(payload)
        allocation=self.root/'engine';allocation.mkdir()
        for name in ('home','xdg-runtime','config','data','download-tmp','libpod-tmp','hooks','network'):(allocation/name).mkdir()
        config=allocation/'config/containers';config.mkdir()
        (config/'containers.conf').write_text('[containers]\ndefault_capabilities = '+json.dumps(['CAP_'+cap for cap in IMAGE.COMMON_BUILD_CAPABILITIES])+ '\nvolumes=[]\nmounts=[]\nenv=[]\nenv_host=false\nhttp_proxy=false\n[engine]\nenv=[]\ncgroup_manager="cgroupfs"\nevents_logger="file"\nruntime="crun"\nconmon_path=["/usr/bin/conmon"]\nhooks_dir=['+json.dumps(str(allocation/'hooks'))+']\n[engine.runtimes]\ncrun=["/usr/bin/crun"]\n[engine.runtimes_flags]\ncrun=[]\n')
        (config/'storage.conf').write_text('[storage]\ndriver="vfs"\nrunroot='+json.dumps(str(allocation/'runroot'))+'\ngraphroot='+json.dumps(str(allocation/'graphroot'))+'\n')
        (config/'registries.conf').write_text('unqualified-search-registries=[]\nshort-name-mode="disabled"\n')
        (config/'auth.json').write_text('{"auths":{}}\n')
        (config/'podman-connections.json').write_text('{}\n')
        environment={'PATH':'/usr/bin:/bin','HOME':str(allocation/'home'),'LANG':'C.UTF-8','LC_ALL':'C.UTF-8',
                     'XDG_RUNTIME_DIR':str(allocation/'xdg-runtime'),'XDG_CONFIG_HOME':str(allocation/'config'),
                     'XDG_DATA_HOME':str(allocation/'data'),'TMPDIR':str(allocation/'download-tmp')}
        environment.update({key:str(config/name) for key,name in {'CONTAINERS_CONF':'containers.conf',
                            'CONTAINERS_STORAGE_CONF':'storage.conf','CONTAINERS_REGISTRIES_CONF':'registries.conf',
                            'REGISTRY_AUTH_FILE':'auth.json','PODMAN_CONNECTIONS_CONF':'podman-connections.json'}.items()})
        args=argparse.Namespace(profile='common-arch',mode='qualify',source=str(ROOT),image_source=str(image),acquisition_dir=str(self.bundle),state_dir=str(self.root/'native-state'),root=str(allocation/'graphroot'),runroot=str(allocation/'runroot'),podman='/usr/bin/podman',engine_allocation=str(allocation),expected_source_revision='c'*40,expected_image_source_revision='a'*40,expected_acquired_inputs_sha256=digest(payload))
        def git(checkout,*flags,**kwargs):
            if flags[0]=='status':return ''
            if flags[-1]=='HEAD^{tree}':return 'b'*40
            return 'c'*40 if checkout==ROOT else 'a'*40
        return args,pins,environment,git

    def test_native_constructor_extended_prefix_and_environment(self):
        args,pins,environment,git=self.native_fixture()
        prefix=IMAGE.podman_prefix(args)
        self.assertGreater(len(prefix),6)
        for chosen in (prefix,prefix[:6]):
            commands=IMAGE.common_build_commands(chosen,Path('/context'),Path('/state'),hooks_dir=Path('/hooks'))
            self.assertEqual(commands[0][:len(chosen)+2],chosen+['build','--pull=never'])
            self.assertEqual(commands[1][:len(chosen)+2],chosen+['save','--format=oci-archive'])
            self.assertNotIn('--cap-drop=all',commands[0]);self.assertIn('--cap-drop=NET_BIND_SERVICE',commands[0])
        with patch.dict(IMAGE.os.environ,environment,clear=True):
            self.assertIn('CONTAINERS_CONF',IMAGE.common_engine_environment(args))
            config=Path(environment['CONTAINERS_CONF']);original=config.read_text()
            for changed in (original.replace('CAP_SYS_CHROOT','CAP_NET_ADMIN'),original.replace('crun=[]','crun=["arbitrary"]'),
                            original.replace('env_host=false','env_host=true'),original+'\n[engine.unreviewed]\nmount="/host"\n'):
                config.write_text(changed)
                with self.assertRaises(RuntimeError):IMAGE.common_engine_environment(args)
            config.write_text(original)
            with patch.dict(IMAGE.os.environ,{'BUILDAH_ISOLATION':'chroot'}):
                with self.assertRaisesRegex(RuntimeError,'unexpected engine environment'):IMAGE.common_engine_environment(args)
            (Path(args.engine_allocation)/'hooks/unapproved.json').write_text('{}')
            with self.assertRaisesRegex(RuntimeError,'hooks directory'):IMAGE.common_engine_environment(args)

    def test_actual_common_native_flow_inert_transport(self):
        args,pins,environment,git=self.native_fixture()
        inventory=(self.bundle/'acquired/package-inventory.expected').read_bytes()
        transport=SyntheticNativeTransport(args,inventory,self.root/'SYNTHETIC-candidate.oci.tar')
        argv=['image.py','qualify','--profile','common-arch','--source',args.source,
              '--expected-source-revision',args.expected_source_revision,'--state-dir',args.state_dir,'--root',args.root,
              '--runroot',args.runroot,'--image-source',args.image_source,'--expected-image-source-revision',args.expected_image_source_revision,
              '--acquisition-dir',args.acquisition_dir,'--expected-acquired-inputs-sha256',args.expected_acquired_inputs_sha256,
              '--engine-allocation',args.engine_allocation]
        with patch.dict(IMAGE.COMMON_PINS,pins),patch.dict(IMAGE.os.environ,environment,clear=True),patch.object(IMAGE,'git_value',side_effect=git),patch.object(IMAGE,'run',side_effect=transport),patch.object(sys,'argv',argv),patch('sys.stdout',io.StringIO()) as output:
            self.assertEqual(IMAGE.main(),0)
        result=json.loads(output.getvalue());manifest=json.loads(Path(result['manifest']).read_text())
        self.assertEqual(manifest['profile'],'common-arch');self.assertEqual(manifest['image']['buildDigest'],transport.build_digest)
        self.assertEqual(manifest['image']['digest'],transport.digest);self.assertNotEqual(result['buildManifest'],result['manifest'])
        self.assertEqual(manifest['image']['id'],transport.image_id);self.assertFalse(transport.containers)
        commands=[call[len(transport.prefix)] for call in transport.calls]
        self.assertEqual(commands.count('save'),1)
        self.assertEqual(sum(call[len(transport.prefix):len(transport.prefix)+2]==['build','--pull=never'] for call in transport.calls),1)
        self.assertLess(commands.index('info'),commands.index('build',commands.index('info')+1))
        records=[json.loads(line) for line in Path(result['journal']).read_text().splitlines()]
        self.assertEqual(next(r for r in records if r['event']=='run-complete')['outcome'],'passed');self.assertEqual(len([r for r in records if r['event']=='cleanup' and r['state']=='observed']),5)

    def test_native_prerequisites_refuse_before_build(self):
        args,pins,environment,git=self.native_fixture()
        inventory=(self.bundle/'acquired/package-inventory.expected').read_bytes()
        transport=SyntheticNativeTransport(args,inventory,self.root/'SYNTHETIC-candidate.oci.tar')
        with patch.dict(IMAGE.COMMON_PINS,pins),patch.dict(IMAGE.os.environ,environment,clear=True),patch.object(IMAGE,'git_value',side_effect=git),patch.object(IMAGE,'run',side_effect=transport):
            checked=IMAGE.check_common_inputs(args)
            for field in ('wrong_donor','wrong_storage'):
                setattr(transport,field,True)
                with self.assertRaises(RuntimeError):IMAGE.common_preflight(args,ROOT,Path(args.state_dir),checked)
                setattr(transport,field,False)
            self.assertFalse(any(call[len(transport.prefix):len(transport.prefix)+2]==['build','--pull=never'] for call in transport.calls))
            (Path(checked['context'])/'pacman.conf').write_bytes(b'late changed config')
            with self.assertRaisesRegex(RuntimeError,'staged context changed'):IMAGE.common_prepare(args,ROOT,Path(args.state_dir),checked)

    def test_native_first_failure_survives_cleanup_failure(self):
        args,pins,environment,git=self.native_fixture()
        inventory=(self.bundle/'acquired/package-inventory.expected').read_bytes()
        transport=SyntheticNativeTransport(args,inventory,self.root/'SYNTHETIC-candidate.oci.tar');transport.fail_case=True;transport.fail_cleanup=True
        with patch.dict(IMAGE.COMMON_PINS,pins),patch.dict(IMAGE.os.environ,environment,clear=True),patch.object(IMAGE,'git_value',side_effect=git),patch.object(IMAGE,'run',side_effect=transport):
            checked=IMAGE.check_common_inputs(args)
            with self.assertRaisesRegex(RuntimeError,'native common case failed') as failure:
                IMAGE.common_qualify(args,ROOT,Path(args.state_dir),IMAGE.COMMON_IMAGE_NAME+'@'+transport.build_digest,checked)
            self.assertTrue(any('cleanup unconfirmed' in note for note in failure.exception.__notes__))
        records=[json.loads(line) for journal in Path(args.state_dir).glob('runs/*/journal.jsonl') for line in journal.read_text().splitlines()]
        self.assertEqual(records[-1]['outcome'],'failed');self.assertTrue(any(r.get('state')=='unconfirmed' for r in records))
        self.assertTrue(transport.containers)

    def test_native_reporting_and_build_failure_preserve_primary(self):
        args,pins,environment,git=self.native_fixture()
        inventory=(self.bundle/'acquired/package-inventory.expected').read_bytes()
        transport=SyntheticNativeTransport(args,inventory,self.root/'SYNTHETIC-candidate.oci.tar')
        with patch.dict(IMAGE.COMMON_PINS,pins),patch.dict(IMAGE.os.environ,environment,clear=True),patch.object(IMAGE,'git_value',side_effect=git),patch.object(IMAGE,'run',side_effect=transport):
            checked=IMAGE.check_common_inputs(args)
            transport.fail_case=True;transport.fail_cleanup=True
            original=IMAGE.append_journal
            def failing_reporting(path,record):
                if record['event'] in ('operation','cleanup','run-complete'):raise OSError('SYNTHETIC REPORTING FAILURE')
                original(path,record)
            with patch.object(IMAGE,'append_journal',side_effect=failing_reporting):
                with self.assertRaisesRegex(RuntimeError,'native common case failed') as failure:
                    IMAGE.common_qualify(args,ROOT,Path(args.state_dir),IMAGE.COMMON_IMAGE_NAME+'@'+transport.build_digest,checked)
            self.assertTrue(any('journal reporting failed' in note for note in failure.exception.__notes__))
            def failed_build(argv,**kwargs):
                if argv[len(transport.prefix):len(transport.prefix)+1]==['build'] and '--help' not in argv:
                    raise RuntimeError('SYNTHETIC ORIGINAL BUILD FAILURE')
                return transport(argv,**kwargs)
            with patch.object(IMAGE,'run',side_effect=failed_build):
                with self.assertRaisesRegex(RuntimeError,'ORIGINAL BUILD FAILURE'):
                    IMAGE.common_prepare(args,ROOT,Path(args.state_dir),checked)
            records=[json.loads(line) for journal in Path(args.state_dir).glob('runs/*/journal.jsonl') for line in journal.read_text().splitlines()]
            self.assertTrue(any(r.get('name')=='image-build' and r.get('outcome')=='failed' for r in records))
            self.assertTrue(any('external build custody' in r.get('reason','') for r in records))

    def test_common_oci_effective_whiteouts_and_diffids(self):
        inventory=b'donor 1-1\npython 3-1\n';candidate=self.root/'SYNTHETIC-effective.oci.tar'
        base=[('var/lib/containers/storage/old','file',b'old')]
        final=[('var/lib/containers/.wh.storage','file',b'')]
        exported,image_id,_=synthetic_common_archive(candidate,inventory,base=base,final=final)
        result=IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        self.assertEqual(result['digest'],exported)
        # Genuine donor contains an empty legacy secring, not private payload.
        empty_private=[('etc/pacman.d/gnupg/secring.gpg','file',b'')]
        _,image_id,_=synthetic_common_archive(candidate,inventory,base=empty_private,
                     final=[('etc/pacman.d/gnupg/.wh.secring.gpg','file',b'')])
        IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        for kwargs in (
            {'base':base},
            {'base':base,'final':[('var/lib/containers/storage/.wh..wh..opq','file',b'')]},
            {'final':[('unexpected','symlink','/etc/pacman.d/gnupg/private-keys-v1.d/key')]},
            {'base':[('etc/pacman.d/gnupg/private-keys-v1.d/key','file',b'key'),('hidden','hardlink','etc/pacman.d/gnupg/private-keys-v1.d/key')],
             'final':[('etc/pacman.d/gnupg/.wh.private-keys-v1.d','file',b'')]},
            {'base':[('etc/pacman.d/gnupg/secring.gpg','file',b'SYNTHETIC SECRET')],
             'final':[('etc/pacman.d/gnupg/.wh.secring.gpg','file',b'')]},
            {'base':[('innocent','file',b'SYNTHETIC KEY'),('etc/pacman.d/gnupg/secring.gpg','hardlink','innocent')],
             'final':[('etc/pacman.d/gnupg/.wh.secring.gpg','file',b'')]},
            {'base':[('innocent','file',b'SYNTHETIC KEY'),('etc/pacman.d/gnupg/secring.gpg','symlink','/innocent')],
             'final':[('etc/pacman.d/gnupg/.wh.secring.gpg','file',b'')]},
            {'base':[('elsewhere/key','file',b'SYNTHETIC KEY'),('etc/pacman.d/gnupg/private-keys-v1.d','symlink','/elsewhere')],
             'final':[('etc/pacman.d/gnupg/.wh.private-keys-v1.d','file',b'')]},
            {'wrong_diff':True},
            {'config_update':{'User':'32768'}},
            {'config_update':{'Env':['HOME=/wrong']}},
        ):
            with self.subTest(kwargs=kwargs):
                _,image_id,_=synthetic_common_archive(candidate,inventory,**kwargs)
                with self.assertRaises(RuntimeError):IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        _,image_id,_=synthetic_common_archive(candidate,inventory)
        with self.assertRaises(RuntimeError):IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=b'donor 1-1\n',built_image_id=image_id)
        with self.assertRaisesRegex(RuntimeError,'built image ID'):IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id='sha256:'+'0'*64)
        with patch.object(IMAGE,'MAX_LAYER_SCAN_MEMBERS',0):
            with self.assertRaises(RuntimeError):IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        self.assertTrue(IMAGE.COMMON_NATIVE_DISPATCH_ENABLED)

    def test_existing_run_bounded_owned_process_controls(self):
        import subprocess
        import time
        births=[]
        def owned_popen(argv,**kwargs):
            self.assertEqual(argv[0],sys.executable)
            self.assertEqual(argv[1],'-c')
            process=self.real_popen(argv,**kwargs);births.append(process)
            return process
        with patch.object(subprocess,'Popen',side_effect=owned_popen):
            for cap,deadline in ((0,1),(True,1),(IMAGE.COMMON_CAPTURE_LIMIT_BYTES+1,1),(1024,float('inf'))):
                with self.assertRaisesRegex(RuntimeError,'invalid bounded command'):
                    IMAGE.run([sys.executable,'-c','pass'],capture_limit_bytes=cap,timeout_seconds=deadline)
            self.assertFalse(births)
            exact=IMAGE.run([sys.executable,'-c','import os;os.write(1,b"a"*1024);os.write(2,b"b"*1024)'],
                            capture_limit_bytes=2048,timeout_seconds=2)
            self.assertEqual((len(exact.stdout),len(exact.stderr)),(1024,1024))
            with self.assertRaisesRegex(RuntimeError,'combined 6000 byte capture limit') as overflow:
                IMAGE.run([sys.executable,'-c','import os,time;os.write(1,b"a"*4096);os.write(2,b"b"*4096);time.sleep(5)'],
                          capture_limit_bytes=6000,timeout_seconds=2)
            self.assertTrue(any('reaped' in note for note in overflow.exception.__notes__))
            self.assertTrue(any('descendant/build effects unconfirmed' in note for note in overflow.exception.__notes__))
            before=time.monotonic()
            with self.assertRaisesRegex(RuntimeError,'timed out') as timeout:
                IMAGE.run([sys.executable,'-c','import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);print("OWNED-before-timeout",flush=True);time.sleep(5)'],
                          capture_limit_bytes=1024,timeout_seconds=0.2)
            self.assertLess(time.monotonic()-before,2)
            self.assertTrue(any('OWNED-before-timeout' in note for note in timeout.exception.__notes__))
            self.assertTrue(any('exit -9' in note for note in timeout.exception.__notes__))
            with patch.object(IMAGE,'append_journal',side_effect=OSError('OWNED REPORTING FAILURE')):
                IMAGE.common_record(self.root/'journal.jsonl',{'event':'failed'},timeout.exception)
            self.assertIn('timed out',str(timeout.exception))
            self.assertTrue(any('journal reporting failed' in note for note in timeout.exception.__notes__))
            failed=IMAGE.run([sys.executable,'-c','import sys;print("OWNED-nonzero");sys.exit(7)'],
                             capture_limit_bytes=1024,timeout_seconds=2,check=False)
            self.assertEqual(failed.returncode,7)
        self.assertEqual(len(births),4)
        self.assertTrue(all(process.poll() is not None for process in births))

    def test_existing_run_attempts_all_owned_closes_after_success(self):
        import selectors
        import subprocess
        original_selector=selectors.DefaultSelector
        attempted=[];births=[]
        class CloseSpy:
            def __init__(self,owned,name,fail):self.owned=owned;self.name=name;self.fail=fail
            def __getattr__(self,name):return getattr(self.owned,name)
            def close(self):
                attempted.append(self.name);self.owned.close()
                if self.fail:raise OSError(self.fail)
        def selected():return CloseSpy(original_selector(),'selector','OWNED FIRST CLOSE FAILURE')
        def owned(argv,**kwargs):
            self.assertEqual(argv,[sys.executable,'-c','print("OWNED-success")'])
            process=self.real_popen(argv,**kwargs);births.append(process)
            process.stdout=CloseSpy(process.stdout,'stdout','OWNED SECOND CLOSE FAILURE')
            process.stderr=CloseSpy(process.stderr,'stderr',None)
            return process
        with patch.object(subprocess,'Popen',side_effect=owned),patch.object(selectors,'DefaultSelector',side_effect=selected):
            with self.assertRaisesRegex(OSError,'OWNED FIRST CLOSE FAILURE') as failure:
                IMAGE.run([sys.executable,'-c','print("OWNED-success")'],capture_limit_bytes=1024,timeout_seconds=2)
        self.assertEqual(attempted,['selector','stdout','stderr'])
        self.assertTrue(any('OWNED SECOND CLOSE FAILURE' in note for note in failure.exception.__notes__))
        self.assertEqual(len(births),1);self.assertEqual(births[0].poll(),0)

    def test_common_oci_hardlink_same_layer_order(self):
        inventory=b'donor 1-1\n';candidate=self.root/'SYNTHETIC-hardlink-order.oci.tar'
        # Existing nonzero inode is bound at the hardlink header; replacing the
        # target with an empty file later cannot erase its private provenance.
        _,image_id,_=synthetic_common_archive(candidate,inventory,
            base=[('ordinary-target','file',b'SYNTHETIC PRIVATE PAYLOAD')],
            final=[('etc/pacman.d/gnupg/secring.gpg','hardlink','ordinary-target'),
                   ('ordinary-target','file',b'')])
        with self.assertRaisesRegex(RuntimeError,'ancestor private key hardlink payload'):
            IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        _,image_id,_=synthetic_common_archive(candidate,inventory,
            final=[('forward-alias','hardlink','future-target'),('future-target','file',b'ordinary')])
        with self.assertRaisesRegex(RuntimeError,'unresolved/forward OCI hardlink'):
            IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)
        _,image_id,_=synthetic_common_archive(candidate,inventory,
            base=[('existing-target','file',b'ordinary')],
            final=[('alias','hardlink','existing-target'),('existing-target','file',b'replacement')])
        IMAGE.inspect_oci_archive(candidate,profile='common-arch',expected_inventory=inventory,built_image_id=image_id)

    def test_common_oci_parser_allocation_and_population_bounds(self):
        import tarfile
        def layer_bytes(members):
            output=io.BytesIO()
            with tarfile.open(fileobj=output,mode='w') as archive:
                for member,payload in members:archive.addfile(member,io.BytesIO(payload))
            return output.getvalue()
        duplicate=tarfile.TarInfo('same');duplicate.size=1
        with self.assertRaisesRegex(RuntimeError,'duplicate common layer path'):
            IMAGE.common_layer_entries(io.BytesIO(layer_bytes([(duplicate,b'x'),(duplicate,b'y')])), 'application/vnd.oci.image.layer.v1.tar')
        unsafe=tarfile.TarInfo('../escape');unsafe.size=1
        with self.assertRaisesRegex(RuntimeError,'unsafe OCI layer path'):
            IMAGE.common_layer_entries(io.BytesIO(layer_bytes([(unsafe,b'x')])), 'application/vnd.oci.image.layer.v1.tar')
        pax=tarfile.TarInfo('metadata');pax.type=tarfile.XHDTYPE;pax.size=65
        with patch.object(IMAGE,'MAX_LOCK_BYTES',64):
            with self.assertRaisesRegex(RuntimeError,'extended metadata bound'):
                IMAGE.common_layer_entries(io.BytesIO(pax.tobuf()),'application/vnd.oci.image.layer.v1.tar')
        one=tarfile.TarInfo('ordinary');one.size=1
        with patch.object(IMAGE,'MAX_LAYER_SCAN_BYTES',32):
            with self.assertRaisesRegex(RuntimeError,'uncompressed byte bound'):
                IMAGE.common_layer_entries(io.BytesIO(layer_bytes([(one,b'x')])),'application/vnd.oci.image.layer.v1.tar')
        malformed=self.root/'SYNTHETIC-malformed.oci.tar';malformed.write_bytes(b'not tar')
        with self.assertRaisesRegex(RuntimeError,'malformed/bounded OCI'):
            IMAGE.inspect_oci_archive(malformed,profile='common-arch',expected_inventory=b'donor 1-1\n',built_image_id='sha256:'+'0'*64)

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
        for mode in ('preflight','qualify'):
            self.cli('common-arch',mode)
            self.cli('common-arch',mode,['--image-source','/missing-image','--expected-image-source-revision','b'*40,
                     '--acquisition-dir','/missing-acquired','--expected-acquired-inputs-sha256','c'*64])
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
        def git(checkout,*flags,**kwargs):
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
