#!/usr/bin/env python3
import base64
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("source_capsule", ROOT / "eng/fourd-public-provider/source_capsule.py")
capsule = importlib.util.module_from_spec(SPEC); assert SPEC.loader is not None
sys.modules[SPEC.name] = capsule; SPEC.loader.exec_module(capsule)
QUALIFY_SPEC = importlib.util.spec_from_file_location("source_capsule_qualify", ROOT / "eng/fourd-public-provider/qualify.py")
qualify = importlib.util.module_from_spec(QUALIFY_SPEC); assert QUALIFY_SPEC.loader is not None
sys.modules[QUALIFY_SPEC.name] = qualify; QUALIFY_SPEC.loader.exec_module(qualify)
SEALER = ROOT / "eng/fourd-public-provider/seal_native_custody.mjs"


class SourceCapsuleTests(unittest.TestCase):
    def fixture(self, root):
        repo = root / "original"; repo.mkdir()
        env = {"PATH":"/usr/bin:/bin", "HOME":"/nonexistent", "LANG":"C.UTF-8",
               "GIT_AUTHOR_NAME":"fixture", "GIT_AUTHOR_EMAIL":"fixture@example.invalid",
               "GIT_COMMITTER_NAME":"fixture", "GIT_COMMITTER_EMAIL":"fixture@example.invalid"}
        subprocess.run(["git","init","-q"],cwd=repo,env=env,check=True)
        (repo/"README.md").write_bytes(b"private fixture\n")
        (repo/"tool.sh").write_bytes(b"#!/bin/sh\nexit 0\n"); (repo/"tool.sh").chmod(0o755)
        subprocess.run(["git","add","."],cwd=repo,env=env,check=True)
        subprocess.run(["git","commit","-qm","fixture source"],cwd=repo,env=env,check=True)
        subprocess.run(["git","commit","--allow-empty","-qm","nonroot pinned source"],cwd=repo,env=env,check=True)
        sha=subprocess.check_output(["git","rev-parse","HEAD"],cwd=repo,text=True).strip()
        tree=subprocess.check_output(["git","rev-parse","HEAD^{tree}"],cwd=repo,text=True).strip()
        commit=subprocess.check_output(["git","cat-file","commit",sha],cwd=repo)
        records=[]; inventory=hashlib.sha256()
        for name in ("README.md","tool.sh"):
            content=(repo/name).read_bytes(); mode="100755" if name=="tool.sh" else "100644"
            blob=subprocess.check_output(["git","hash-object",name],cwd=repo,text=True).strip()
            inventory.update(name.encode()+b"\0"+content+b"\0")
            records.append({"path":name,"mode":mode,"blobOid":blob,"bytes":len(content),
                            "sha256":hashlib.sha256(content).hexdigest(),
                            "contentBase64":base64.b64encode(content).decode()})
        value={"schema":capsule.SNAPSHOT_SCHEMA,"sourceSha":sha,"sourceTree":tree,
               "inventorySha256":inventory.hexdigest(),"commitObjectBase64":base64.b64encode(commit).decode(),
               "files":records}
        return value,{"sourceSha":sha,"sourceTree":tree,"inventorySha256":inventory.hexdigest()}

    def test_real_sealer_roundtrip_reconstructs_original_raw_commit_tree_and_clean_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary); value,expected=self.fixture(root)
            plain=root/"snapshot.json"; plain.write_bytes(capsule.canonical(value)); plain.chmod(0o600)
            private=root/"private.pem"; public=root/"public.pem"
            subprocess.run(["openssl","genpkey","-algorithm","RSA","-pkeyopt","rsa_keygen_bits:3072","-out",private],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            private.chmod(0o600)
            subprocess.run(["openssl","pkey","-in",private,"-pubout","-out",public],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            public.chmod(0o600); sealed=root/"capsule.json"; restored=root/"restored.json"
            nonce="fixture-source-01-source"; profile="9"*64
            subprocess.run(["node",SEALER,"seal",plain,public,sealed,nonce,expected["sourceSha"],profile],check=True)
            capsule.validate_outer_capsule(sealed.read_bytes(),nonce,expected["sourceSha"],profile)
            subprocess.run(["node",SEALER,"unseal",sealed,private,restored,nonce,expected["sourceSha"],profile],check=True)
            previous=os.umask(0o077)
            try: checkout=capsule.reconstruct(restored.read_bytes(),root/"checkout",expected)
            finally: os.umask(previous)
            self.assertEqual(expected["sourceSha"],subprocess.check_output(["git","rev-parse","HEAD"],cwd=checkout,text=True).strip())
            self.assertEqual(b"",subprocess.check_output(["git","status","--porcelain"],cwd=checkout))
            self.assertEqual(0o600,(checkout/"README.md").stat().st_mode & 0o777)
            self.assertEqual(0o700,(checkout/"tool.sh").stat().st_mode & 0o777)
            parent=subprocess.run(["git","cat-file","-e","HEAD^"],cwd=checkout,stderr=subprocess.DEVNULL).returncode
            self.assertNotEqual(0,parent)
            capture=root/"downstream-capture";capture.mkdir(mode=0o700)
            safe={"PATH":"/usr/bin:/bin","HOME":"/nonexistent","LANG":"C.UTF-8"};effects=qualify.Effects()
            qualify.validate_checkout(checkout,effects,safe,capture)
            self.assertEqual(expected["inventorySha256"],qualify.git_inventory(checkout,effects,safe,capture/"inventory.json"))
            self.assertNotIn("private fixture",sealed.read_text())

    def test_modified_file_mode_commit_tree_inventory_and_order_refuse(self):
        with tempfile.TemporaryDirectory() as temporary:
            value,expected=self.fixture(pathlib.Path(temporary))
            mutations=[]
            for field in ("contentBase64","mode","blobOid","sha256"):
                changed=json.loads(json.dumps(value)); changed["files"][0][field]="bad"; mutations.append(changed)
            changed=json.loads(json.dumps(value)); changed["sourceTree"]="0"*40; mutations.append(changed)
            changed=json.loads(json.dumps(value)); changed["files"].reverse(); mutations.append(changed)
            for changed in mutations:
                with self.subTest(changed=changed), self.assertRaises(capsule.CapsuleRefusal):
                    capsule.validate_snapshot(capsule.canonical(changed),expected)
            changed=json.loads(json.dumps(value)); changed["commitObjectBase64"]=base64.b64encode(b"tree "+b"0"*40+b"\n").decode()
            with self.assertRaises(capsule.CapsuleRefusal):
                capsule.reconstruct(capsule.canonical(changed),pathlib.Path(temporary)/"bad-commit",expected)

    def test_duplicate_traversal_dotgit_links_gitlinks_and_bounds_refuse(self):
        with tempfile.TemporaryDirectory() as temporary:
            value,expected=self.fixture(pathlib.Path(temporary)); raw=capsule.canonical(value)
            duplicate=raw.replace(b'"schema":"fsgg.fourd.source-snapshot/1"',b'"schema":"x","schema":"fsgg.fourd.source-snapshot/1"')
            with self.assertRaises(capsule.CapsuleRefusal): capsule.validate_snapshot(duplicate,expected)
            for name,mode in (("../x","100644"),(".git/config","100644"),("link","120000"),("submodule","160000")):
                changed=json.loads(raw); changed["files"][0]["path"]=name; changed["files"][0]["mode"]=mode
                with self.subTest(name=name), self.assertRaises(capsule.CapsuleRefusal):
                    capsule.validate_snapshot(capsule.canonical(changed),expected)
            with self.assertRaises(capsule.CapsuleRefusal): capsule.closed_json(b"x"*(capsule.MAX_PLAINTEXT+1),capsule.MAX_PLAINTEXT)

    def test_outer_capsule_rejects_wrong_run_source_profile_aad_and_extra_fields(self):
        aad=lambda run,source,profile: hashlib.sha256(f"fsgg-native-custody/1\0{run}\0{source}\0{profile}".encode()).hexdigest()
        run="fixture-source-01-source"; source="1"*40; profile="2"*64
        value={"schema":"fsgg.telemetry.native-custody-capsule/1","algorithm":"AES-256-GCM+RSA-OAEP-SHA256",
               "runNonce":run,"sourceSha":source,"profileSha256":profile,"aadSha256":aad(run,source,profile),
               "nonce":base64.b64encode(b"n"*12).decode(),"tag":base64.b64encode(b"t"*16).decode(),
               "wrappedKey":base64.b64encode(b"w"*384).decode(),"ciphertext":base64.b64encode(b"{}\n").decode()}
        capsule.validate_outer_capsule(capsule.canonical(value),run,source,profile)
        for field,bad in (("runNonce","wrong-run"),("sourceSha","0"*40),("profileSha256","0"*64),("aadSha256","0"*64)):
            changed=dict(value); changed[field]=bad
            with self.subTest(field=field), self.assertRaises(capsule.CapsuleRefusal):
                capsule.validate_outer_capsule(capsule.canonical(changed),run,source,profile)
        changed=dict(value); changed["extra"]=True
        with self.assertRaises(capsule.CapsuleRefusal): capsule.validate_outer_capsule(capsule.canonical(changed),run,source,profile)

    def test_reconstruction_runner_cancellation_is_sticky_and_never_claims_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary);value,expected=self.fixture(root);calls=[]
            def runner(argv,cwd,timeout):
                calls.append(list(argv))
                if len(calls)==4: raise capsule.CapsuleRefusal("cancelled")
                return capsule._run(argv,cwd,timeout)
            with self.assertRaisesRegex(capsule.CapsuleRefusal,"cancelled"):
                capsule.reconstruct(capsule.canonical(value),root/"cancelled",expected,runner=runner,
                                    deadline_monotonic=__import__("time").monotonic()+60)
            self.assertFalse((root/"cancelled/.git/shallow").exists())

    def test_actual_acquisition_unseals_nonroot_source_and_hands_off_without_secrets(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=pathlib.Path(temporary);value,expected=self.fixture(root);plain=root/"snapshot";plain.write_bytes(capsule.canonical(value));plain.chmod(0o600)
            private_key=root/"recipient.pem";public_key=root/"recipient-public.pem"
            subprocess.run(["openssl","genpkey","-algorithm","RSA","-pkeyopt","rsa_keygen_bits:3072","-out",private_key],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);private_key.chmod(0o600)
            subprocess.run(["openssl","pkey","-in",private_key,"-pubout","-out",public_key],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);public_key.chmod(0o600)
            run="run-12345-02";descriptor={"plaintextBytes":plain.stat().st_size,"plaintextSha256":hashlib.sha256(plain.read_bytes()).hexdigest()}
            descriptor_raw=json.dumps(descriptor,sort_keys=True,separators=(",",":")).encode()+b"\n";profile=hashlib.sha256(descriptor_raw).hexdigest();sealed=root/"source.capsule.json"
            subprocess.run(["node",SEALER,"seal",plain,public_key,sealed,run+"-source",expected["sourceSha"],profile],check=True)
            transport={"releaseId":"30","assetId":"31","tag":"fourd-source-12345-02","name":"fourd-source-run-12345-02.capsule.json",
                "ciphertextBytes":sealed.stat().st_size,"ciphertextSha256":hashlib.sha256(sealed.read_bytes()).hexdigest(),
                "descriptor":descriptor,"descriptorSha256":profile,"recipientPublicKeySha256":hashlib.sha256(public_key.read_bytes()).hexdigest()}
            admitted={"runNonce":run,"sourceCapsule":transport}
            metadata=json.dumps({"id":30,"tag_name":transport["tag"],"assets":[{"id":31,"name":transport["name"],"size":transport["ciphertextBytes"]}]}).encode()
            class Response:
                def __init__(self,data,url):self.data=data;self.url=url;self.offset=0
                def __enter__(self):return self
                def __exit__(self,*_):return None
                def geturl(self):return self.url
                def read(self,n):v=self.data[self.offset:self.offset+n];self.offset+=len(v);return v
            class Opener:
                def __init__(self):self.values=[Response(metadata,"https://api.github.com/release"),Response(sealed.read_bytes(),"https://release-assets.githubusercontent.com/asset")]
                def open(self,*_args,**_kwargs):return self.values.pop(0)
            private=root/"private";private.mkdir(mode=0o700);(private/"home").mkdir(mode=0o700);env={qualify.ADMISSION_SECRET:"x",qualify.KEY_SECRET:"y"}
            with mock.patch.multiple(qualify,FOURD_SHA=expected["sourceSha"],FOURD_TREE=expected["sourceTree"],FOURD_INVENTORY=expected["inventorySha256"]),mock.patch.object(qualify.urllib.request,"build_opener",return_value=Opener()):
                source=qualify.acquire_source(private_key.read_bytes(),admitted,private,ROOT/"eng/fourd-public-provider/github-known-hosts",qualify.Effects(),env)
            self.assertEqual(expected["sourceSha"],subprocess.check_output(["git","rev-parse","HEAD"],cwd=source,text=True).strip())
            self.assertFalse((private/"credentials").exists());self.assertTrue((private/"source-acquisition.json").is_file())
            self.assertNotIn(qualify.ADMISSION_SECRET,env);self.assertNotIn(qualify.KEY_SECRET,env)


if __name__ == "__main__": unittest.main()
