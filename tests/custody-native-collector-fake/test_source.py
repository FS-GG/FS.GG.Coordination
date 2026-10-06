"""Pure source controls. Native C controls in test_fake.c are authored, unrun."""
import hashlib
import json
import pathlib
import re
import struct
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
CUSTODY = ROOT / 'src/FS.GG.Coordination.Orchestration.Execution/Custody'
HEADER = (CUSTODY / 'native-collector-fake.h').read_text()
BOOTSTRAP = (CUSTODY / 'bootstrap.c').read_text()

class SourceControls(unittest.TestCase):
    def test_ordinary_producer_source_unchanged(self):
        original = BOOTSTRAP.replace('#include "native-collector-fake.h"\n\n', '')
        original = original.replace(' if(argc>1 && !strcmp(argv[1],FAKE_MODE)) fake_main(argc,argv);\n', '')
        original = original.replace(' if(argc==2 && !strcmp(argv[1],"--export-native-collector-fake-filter")) {\n  return write(1,fake_rules,sizeof(fake_rules))==(ssize_t)sizeof(fake_rules)?0:125;\n }\n', '')
        self.assertEqual(hashlib.sha256(original.encode()).hexdigest(), '4d2105f8223b2eceb89d8fee675b9ee41759c50a36a9d9c86d59a4c5978cd610')

    def test_exact_fixed_fixture_source(self):
        for name, expected in [('fake_fixture','69e764675313417a96e863aba73a41b78cf70836a176aeedba33cfda8d8376bf'), ('fake_packet','cf02e16bb5d9e765cbea047c718424795e92404f652f46fe97a8696ac54d046b'), ('fake_schema','96064563fe913c04db4f2ba3d6bdfed4bc1df9795d8180db1f914b3779e8eddb')]:
            with self.subTest(name=name):
                block = re.search(r'static const char '+name+r'\[\] =\n(.*?)^;', HEADER, re.M|re.S).group(1)
                raw = ''.join(json.loads(line.strip()) for line in block.splitlines()).encode()
                self.assertEqual(hashlib.sha256(raw).hexdigest(), expected)

    def test_denied_operation_diagnostics_use_only_captured_stdout(self):
        block = re.search(r'static const char fake_fixture\[\] =\n(.*?)^;', HEADER, re.M|re.S).group(1)
        fixture = ''.join(json.loads(line.strip()) for line in block.splitlines())
        self.assertEqual(fixture.count('exec 2>&1'), 1)
        self.assertLess(fixture.index('for fd in {3..63}'), fixture.index('exec 2>&1'))
        self.assertLess(fixture.index('> /unbounded-root-write'), fixture.index('exec 2>&1'))
        self.assertLess(fixture.index('exec 2>&1'), fixture.index("trap 'code=$?"))
        self.assertIn("exec /usr/bin/unshare --user /usr/bin/bash --noprofile --norc -c ':' 2>&1", fixture)
        self.assertNotIn('/proc/self/fd', fixture)

    def test_fake_filter_source_has_selected_byte_shape(self):
        block = HEADER.split('static struct sock_filter fake_rules[] = {',1)[1].split('\n};',1)[0]
        deny = list(map(int,re.findall(r'FAKE_ERRNO\((\d+)\)',block)))
        self.assertEqual(deny,[56,57,58,101,165,166,246,248,249,250,272,298,304,308,310,311,321])
        for token in ('AUDIT_ARCH_X86_64','0x40000000U','435','SECCOMP_RET_ERRNO | ENOSYS','SECCOMP_RET_ALLOW'):
            self.assertIn(token,block)
        rows=[(0x20,0,0,4),(0x15,1,0,0xc000003e),(6,0,0,0x80000000),(0x20,0,0,0),(0x45,0,1,0x40000000),(6,0,0,0x80000000)]
        for number in deny: rows.extend(((0x15,0,1,number),(6,0,0,0x50001)))
        rows.extend(((0x15,0,1,435),(6,0,0,0x50026),(6,0,0,0x7fff0000)))
        raw=b''.join(struct.pack('<HBBI',*row)for row in rows)
        self.assertEqual(hashlib.sha256(raw).hexdigest(),'8924388729dfd5a649a6d4218ad83c8a9bcc07361c50ec6829a32b783808593a')

    def test_closed_descriptor_recipe_and_no_generic_execution(self):
        recipe=HEADER.split('static void fake_recipe(',1)[1].split('static void fake_main(',1)[0]
        self.assertEqual(recipe.count('PUSH("--seccomp")'),1)
        self.assertIn('PUSH("--seccomp");PUSH("3")',recipe)
        self.assertIn('PUSH("--json-status-fd");PUSH("2")',recipe)
        self.assertIn('PUSH("--block-fd");PUSH("0")',recipe)
        self.assertNotIn('--disable-userns',recipe)
        self.assertNotIn('--share-net',recipe)
        self.assertNotIn('PUSH("--proc")',recipe)
        self.assertEqual(HEADER.count('execve('),1)
        self.assertIn('execve("/usr/bin/bwrap",args,environment)',HEADER)
        self.assertNotIn('getenv(',HEADER)

    def test_filter_order_and_fatal_refusal_boundaries(self):
        body=HEADER.split('static void fake_main(',1)[1]
        ordered=['fake_parse(', '__NR_close_range', 'fake_exact_file(fixture', 'fake_empty_directory(', 'PR_SET_NO_NEW_PRIVS', 'fake_filter_fd(', 'fake_recipe(', 'fake_before(deadline)', 'execve(']
        indices=[body.index(term)for term in ordered]
        self.assertEqual(indices,sorted(indices))
        for reason in ('fake-closed-fds','fake-filter-fd','fake-filter-write','fake-filter-seal','fake-filter-inheritance','fake-bwrap-exec','fake-scratch-not-empty'):
            self.assertIn('reject("'+reason+'")',HEADER)
        self.assertIn('F_SEAL_WRITE|F_SEAL_GROW|F_SEAL_SHRINK|F_SEAL_SEAL',HEADER)
        self.assertIn('O_NOFOLLOW|O_NONBLOCK',HEADER)

    def test_generated_existing_assets_remain_historical(self):
        for name, expected in [('bootstrap.linux-x64.bin','28c5c28bb6279f994606584e9fe930213c8848b7a0cf9366c03e150520974716'),('filter.linux-x64.bpf','53281c62a5db55e8b407aa45f11484bd564bc1907b6ffc6bd65acb950cedbbe1')]:
            self.assertEqual(hashlib.sha256((CUSTODY/name).read_bytes()).hexdigest(),expected)
        manifest=json.loads((CUSTODY/'manifest.json').read_bytes())
        self.assertEqual(manifest['sourceSha256'],'4d2105f8223b2eceb89d8fee675b9ee41759c50a36a9d9c86d59a4c5978cd610')
        self.assertNotIn('nativeCollectorFake',manifest)

    def test_future_build_requires_actual_export_and_header_join(self):
        source=(CUSTODY/'build.py').read_text()
        compile(source,'build.py','exec')
        self.assertIn("fake_source=(root/'native-collector-fake.h').read_bytes()",source)
        self.assertIn("subprocess.check_output([str(binary),'--export-native-collector-fake-filter']",source)
        self.assertIn("'headerSha256':hashlib.sha256(fake_source).hexdigest()",source)
        self.assertIn("'hostAdmissionAvailable':False",source)
        self.assertIn("'nativeQualificationRecorded':False",source)

    def test_authored_native_controls_are_present_not_invoked(self):
        source=(pathlib.Path(__file__).parent/'test_fake.c').read_text()
        for value in ('fake_parse(7,argv,&req)','fake_recipe(args,&req','fake_filter_fd(1000)','fault<=10','memcpy(captured+captured_size','injected_clock_gettime'):
            self.assertIn(value,source)
        self.assertNotIn('subprocess',source)

if __name__ == '__main__': unittest.main()
