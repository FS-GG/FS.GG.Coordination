"""Acquired-byte controls for the actual native F# command boundary.

Set FSGG_FOURD_NATIVE_TEST_COMMAND to a JSON argv prefix for the compiled executable.
Fixtures exercise Git objects/modes and a complete small OCI tar; native effects are absent.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import io


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


class NativeFixture:
    def __init__(self, root, command, mode="full"):
        self.root, self.command, self.mode = Path(root), command, mode
        self.history, self.observed, self.counter = [], [], 0
        self.started = 1800000000
        self.runtime = self.root / "runtime"
        self.runtime.mkdir()
        if len(command) != 1:
            raise AssertionError("native tests require the standalone production FourD.Typed apphost")
        apphost = Path(command[0]).resolve(strict=True)
        if apphost.name != "FourD.Typed":
            raise AssertionError("native tests require the production FourD.Typed apphost")
        self.files = {}
        for name in ("FourD.Typed", "FourD.Typed.dll", "FourD.Typed.deps.json", "FourD.Typed.runtimeconfig.json", "FSharp.Core.dll"):
            acquired = apphost.parent / name
            shutil.copy2(acquired, self.runtime / name)
            self.files[name] = sha(acquired.read_bytes())
        host_name = os.environ.get("FSGG_FOURD_NATIVE_TEST_DOTNET_HOST") or shutil.which("dotnet")
        if not host_name:
            raise AssertionError("actual dotnet host required")
        host = Path(host_name).resolve(strict=True)
        binding = {"schema": "fsgg.fourd.typed-policy-binding/1", "sourceSha": "a" * 40, "files": self.files}
        binding_path = self.write("binding.json", binding)
        self.producer = dict(runtimeRoot=str(self.runtime), bindingPath=str(binding_path), hostPath=str(host),
                             expectedSourceSha="a" * 40, expectedSourceTree="b" * 40,
                             expectedBindingSha256=sha(binding_path.read_bytes()), expectedFiles=self.files)
        provenance = dict(schema="fsgg.fourd.native-runtime-provenance/1", sourceSha="a" * 40, sourceTree="b" * 40,
                          bindingSha256=self.producer["expectedBindingSha256"], files=self.files, sdkVersion="10.0.400",
                          runtimeVersion=self.call("native-runtime-info", {"schema":"fsgg.fourd.native-runtime-info-request/1"})[1]["runtimeVersion"], hostSha256=sha(host.read_bytes()))
        provenance_path = self.write("provenance.json", provenance)
        self.producer.update(provenancePath=str(provenance_path), expectedProvenanceSha256=sha(provenance_path.read_bytes()))
        self.context = self.root / "context"
        self.context.mkdir()
        (self.context / "README").write_bytes(b"native constructor fixture\n")
        (self.context / "run.sh").write_bytes(b"#!/bin/sh\nexit 0\n")
        (self.context / "run.sh").chmod(0o755)
        self.git("init", "--quiet")
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture")
        self.revision = self.git("rev-parse", "HEAD").decode().strip()
        self.tree = self.git("rev-parse", "HEAD^{tree}").decode().strip()
        git_obs = dict(revision=self.revision, tree=self.tree,
                       rawCommitBase64=base64.b64encode(self.git("cat-file", "commit", "HEAD")).decode(),
                       treeObjects=[dict(sha=self.tree, base64=base64.b64encode(self.git("cat-file", "tree", self.tree)).decode())])
        self.git_path = self.write("git-observation.json", git_obs)
        shutil.rmtree(self.context / ".git")
        inventory = hashlib.sha256()
        for path in sorted(self.context.iterdir()):
            inventory.update(path.name.encode() + b"\0" + path.read_bytes() + b"\0")
        self.inventory = inventory.hexdigest()
        self.source = dict(sourceRoot=str(self.context), observedRevision=self.revision,
                           observedTree=self.tree, gitObservationPath=str(self.git_path))
        executor = self.root / "executor.dll"
        executor.write_bytes(b"fixture-pinned-executor")
        self.p2proof = dict(sourceSha="9d99573628ee31e966a61a28b9b36f7414b10f33", sourceTree="e96774e11969b2730ebcf40ab47a06d7a17d46d6", executorDllPath=str(executor))
        ciphertext = self.root / "ciphertext.json"
        ciphertext.write_bytes(b"fixture ciphertext bytes")
        sealer = self.root / "sealer.dll"
        sealer.write_bytes(b"fixture-pinned-sealer")
        descriptor = dict(schema="fsgg.fourd.source-capsule-descriptor/1", purpose="fourd-source-acquisition",
                          placementSha=self.revision, runId="42", runAttempt="1", runNonce="fixture-run",
                          coordinationRepositoryId="1346720714", fourdRepositoryId="1390568106",
                          sourceSha=self.revision, sourceTree=self.tree, inventorySha256=self.inventory,
                          plaintextBytes=100, plaintextSha256="c" * 64, recipientPublicKeySha256="d" * 64,
                          sealerSha256=sha(sealer.read_bytes()), issuedAt="2027-01-15T08:00:00Z", expiresAt="2027-01-15T09:30:00Z")
        # Native descriptor preserves the existing exact raw JSON bytes plus newline.
        import datetime
        self.started = int(datetime.datetime.fromisoformat(descriptor["issuedAt"]).timestamp())
        capsule = dict(transport="coordination-release-asset", releaseId=70, assetId=71, tag="fourd-native-42",
                       name="source-capsule.json", ciphertextBytes=ciphertext.stat().st_size,
                       ciphertextSha256=sha(ciphertext.read_bytes()), descriptor=descriptor,
                       descriptorSha256=sha(canonical(descriptor)), recipientPublicKeySha256="d" * 64)
        self.packet = dict(schema="fsgg.fourd.native-root-packet/1", mode=mode, runId=42, runAttempt=1,
                           placementSha=self.revision, placementTree=self.tree, originalActorId=9, triggeringActorId=9,
                           runNonce="fixture-run", issuedAt=self.started, expiresAt=self.started + 5400,
                           producer=dict(sourceSha="a" * 40, sourceTree="b" * 40, files=self.files,
                                         bindingSha256=self.producer["expectedBindingSha256"], provenanceSha256=self.producer["expectedProvenanceSha256"]),
                           product=dict(revision=self.revision, tree=self.tree, inventorySha256=self.inventory),
                           p2=dict(revision=self.p2proof["sourceSha"], tree=self.p2proof["sourceTree"], executorDllSha256=sha(executor.read_bytes())),
                           runner=dict(id=5, name="fixture-runner", groupId=6, labels=["linux", "x64"]),
                           sourceCapsule=capsule, operationId="fourd-portable-technical")
        self.packet_path = self.write("packet.json", self.packet)
        run = dict(id=42, run_attempt=1, head_sha=self.revision, event="workflow_dispatch",
                   path=".github/workflows/fourd-native-image-qualification.yml", head_branch="main",
                   repository=dict(full_name="FS-GG/FS.GG.FourD", id=1390568106), actor=dict(id=9), triggering_actor=dict(id=9), status="in_progress")
        self.run_path = self.write("run.json", run)
        job = dict(id=43, name="native-qualification", run_id=42, run_attempt=1, head_sha=self.revision, status="in_progress", runner_id=5,
                   runner_name="fixture-runner", runner_group_id=6, labels=["linux", "x64"])
        self.job_path = self.write("job.json", dict(total_count=1,jobs=[job]))
        self.release = dict(id=70, tag_name="fourd-native-42", author=dict(id=9), draft=False, prerelease=False,
                            assets=[dict(id=72, name="fourd-native-packet.json", size=self.packet_path.stat().st_size,
                                         digest="sha256:" + sha(self.packet_path.read_bytes()), state="uploaded", uploader=dict(id=9)),
                                    dict(id=71, name=capsule["name"], size=capsule["ciphertextBytes"], digest="sha256:" + capsule["ciphertextSha256"], state="uploaded", uploader=dict(id=9))])
        self.release_path = self.write("release.json", self.release)
        self.root_evidence = dict(packetPath=str(self.packet_path), releasePath=str(self.release_path), packetAssetId=72,
                                  locator=dict(repository="FS-GG/FS.GG.Coordination", tagPrefix="fourd-native", packetName="fourd-native-packet.json", authorId=9),
                                  runPath=str(self.run_path), jobPath=str(self.job_path), jobLinkHeaders=[], expectedRunId=42,
                                  expectedPlacementSha=self.revision, expectedActorId=9, nowUnixSeconds=self.started + 2)
        self.capsule_evidence = dict(releasePath=str(self.release_path), assetPath=str(ciphertext), sealerPath=str(sealer))
        self.make_native_evidence()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.context), *args], stderr=subprocess.PIPE)

    def write(self, name, value):
        path = self.root / name
        path.write_bytes(canonical(value))
        return path

    def call(self, command, request):
        self.counter += 1
        inp, out = self.root / f"request-{self.counter}.json", self.root / f"response-{self.counter}.json"
        inp.write_bytes(canonical(request))
        done = subprocess.run([*self.command, command, str(inp), str(out)], capture_output=True, timeout=20)
        if not out.exists():
            raise AssertionError((done.returncode, done.stdout, done.stderr))
        return done.returncode, json.loads(out.read_bytes())

    def event(self, kind, elapsed=None, **fields):
        if elapsed is None:
            elapsed = 0 if getattr(self, "trace_zero", False) else len(self.history) + 1
        observation = dict(kind=kind, elapsed=elapsed, **fields)
        code, result = self.call("native-transition", dict(schema="fsgg.fourd.native-transition-request/1", mode=self.mode,
                                                        startedUnixSeconds=self.started, history=self.history, observation=observation))
        if "history" in result:
            self.history = result["history"]
            self.observed.append(dict(action=kind, state=result["modelState"]))
        return code, result

    def ok(self, kind, **fields):
        code, result = self.event(kind, **fields)
        if code:
            raise AssertionError((kind, result))
        return result

    def intent(self, operation):
        self.ok("intent", operation=operation, resource=operation + "-scope", identity=operation + "-inode")
        self.ok("ack", operation=operation, pid=100 + len(self.history), startIdentity="pid-start-" + operation)

    def bound(self):
        self.ok("producer", evidence=self.producer)
        self.ok("packet", evidence=self.root_evidence)
        self.ok("capsule", evidence=self.capsule_evidence)
        self.ok("key-exposed", resource="key", identity="key-inode", publicKeySha256="d" * 64)

    def prepared(self):
        self.bound()
        self.intent("source")
        self.ok("source", evidence=self.source)
        self.ok("resources", path=str(self.resources_path), launcherUid=1000, launcherGid=1000)
        self.intent("preflight")
        self.ok("preflight", path=str(self.preflight_path))

    def image_build_ready(self):
        self.prepared()
        self.ok("revalidate-source", evidence=self.source)
        self.intent("image")
        result = self.ok("revalidate-source", evidence=self.source)
        if result["actions"] != ["execute-image-command"]:
            raise AssertionError(result)

    def image_archive_ready(self):
        self.image_build_ready()
        result = self.ok("archive", path=str(self.archive_path))
        if result["actions"] != ["inspect-image-custody", "load-image"]:
            raise AssertionError(result)
        if "imageBinding" in result and result["imageBinding"] is not None:
            raise AssertionError("P2 binding appeared before native image validation")

    def image_ready(self):
        self.image_archive_ready()
        result = self.ok("image", path=str(self.image_path))
        # The owner wires this projection into NativeProtocol's response. Once present,
        # verify that only F# derived the compatibility flags from the validated snapshot.
        if "imageBinding" in result:
            expected = dict(self.image, temporaryMountNamespaceExited=True, postNamespaceIdentityVerified=True)
            if result["imageBinding"] != expected:
                raise AssertionError("P2 binding is not the exact validated-image projection")
            if self.image["temporaryMountNamespaceExited"] or self.image["postNamespaceIdentityVerified"]:
                raise AssertionError("fixture raw flags must not authorize the P2 projection")

    def finish(self):
        self.ok("begin-cleanup")
        state = self.ok("intent", operation="cleanup", resource="cleanup-scope", identity="cleanup-inode")["state"]
        self.ok("ack", operation="cleanup", pid=900, startIdentity="pid-start-cleanup")
        for resource in state["owned"]:
            self.ok("close", resource=resource["resource"], identity=resource["identity"], childrenRemaining=0, readersRemaining=0, pathsRemaining=0)
        self.ok("retired")
        self.intent("collection")
        self.ok("close", resource="collection-scope", identity="collection-inode", childrenRemaining=0, readersRemaining=0, pathsRemaining=0)
        return self.ok("finish")

    def make_native_evidence(self, layer_bytes=None, include_directories=False):
        gib = 1024**3
        memory = dict(schema="fsgg.fourd.image-temporary-memory/1", memTotalBytes=16 * gib, memAvailableBytes=15 * gib,
                      swapTotalBytesObservedNotCredited=0, swapFreeBytesObservedNotCredited=0,
                      cgroupAncestors=[dict(path="lane", current=gib, max=10 * gib, high="max", events={}), dict(path=".", hierarchyRoot=True)],
                      cgroupMembership="/lane", cgroupMountRoot="/", cgroupMountpoint="/sys/fs/cgroup", processId=10,
                      availableHeadroomBytes=9 * gib, requiredHeadroomBytes=7 * gib, scratchBytes=5 * gib, reserveBytes=2 * gib, admitted=False)
        fs = dict(schema="fsgg.fourd.persistent-storage/1", phase="before-effects", device=2, totalBytes=100 * gib,
                  totalInodes=100000, freeBytes=90 * gib, freeInodes=90000, path="fixture")
        self.resources_path = self.write("resources.json", dict(schema="fsgg.fourd.pre-effect-resources/1", admitted=False, memory=memory,
                        cpu=dict(affinity=[0, 1], effectiveCpuUpperBound=2.0, cgroupAncestors=[dict(path="lane", quota="200000", period=100000), dict(path=".", hierarchyRoot=True)]),
                        filesystems=[fs, fs], metadataFloorBytes=64 * 1024**2, metadataFloorInodes=1024, fullImageDiskAdmitted=False, mountNamespace="mnt:[1]"))
        probes = []
        for requested in (16 * 1024**2, 5 * gib):
            facts = dict(device=4, freeBytes=requested, capacityBytes=requested, inodeCapacity=65536, mountInfoSha256="f" * 64)
            probes.append(dict(requestedBytes=requested, initial=facts, afterProbeCleanup=facts, subprocessDeviceMatched=True, podmanChildDeviceMatched=True))
        temporary = dict(schema="fsgg.fourd.image-temporary-capability/2", hostUid=1000, hostMountNamespace="mnt:[1]", childMountNamespace="mnt:[2]",
                         mountNamespaceIsolated=True, memory=memory, namespaceMappings=dict(uidMap=[[0, 1000, 1], [1, 100000, 65536]], gidMap=[[0, 1000, 1], [1, 100000, 65536]], namespaceRootMapsLauncher=True, operationUidGid32768Mapped=True),
                         mappedCancellation=dict(uid=32768, gid=32768, termination="terminated", cleanupComplete=True), probes=probes, cleanupComplete=True, admitted=False)
        self.preflight_path = self.write("preflight.json", dict(schema="fsgg.fourd.native-image-preflight/1", sourceRevision=self.revision, sourceTree=self.tree,
                           sourceInventorySha256=self.inventory, podmanVersion="fixture", utilLinuxUnshareVersion="fixture", utilLinuxMountVersion="fixture",
                           rootlessUidMapSha256="f" * 64, rootlessGidMapSha256="f" * 64, operationUidGid32768Mapped=True, namespaceRootMapsLaunchingUser=True,
                           persistentStorage={}, targetStores={name: dict(graphDriver="vfs", rootless=True) for name in ("build", "fresh-load")},
                           runrootsShortDistinctPrivate=True, imageCopyTemporaryStorage=temporary, targetStoreSurvivorListingProbed=True, noSudoOrHostFallback=True))
        layer = b"fixture uncompressed layer bytes" if layer_bytes is None else layer_bytes
        config = canonical(dict(os="linux", architecture="amd64", config=dict(User="32768:32768", Env=["HOME=/output/home"], Entrypoint=[], Cmd=[], Volumes={}), rootfs=dict(type="layers", diff_ids=["sha256:" + sha(layer)]))).rstrip(b"\n")
        descriptor = lambda media, raw: dict(mediaType=media, digest="sha256:" + sha(raw), size=len(raw))
        config_d = descriptor("application/vnd.oci.image.config.v1+json", config)
        layer_d = descriptor("application/vnd.oci.image.layer.v1.tar", layer)
        manifest = canonical(dict(schemaVersion=2, config=config_d, layers=[layer_d])).rstrip(b"\n")
        manifest_d = descriptor("application/vnd.oci.image.manifest.v1+json", manifest)
        members = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}', "index.json": canonical(dict(schemaVersion=2, manifests=[manifest_d])).rstrip(b"\n")}
        for raw in (config, layer, manifest):
            members["blobs/sha256/" + sha(raw)] = raw
        archive = self.root / "image.tar"
        directories = [".", "blobs", "blobs/sha256"] if include_directories else []
        with tarfile.open(archive, "w", format=tarfile.USTAR_FORMAT) as tar:
            for name in directories:
                info = tarfile.TarInfo(name + "/")
                info.type, info.mode = tarfile.DIRTYPE, 0o700
                tar.addfile(info)
            for name, raw in members.items():
                info = tarfile.TarInfo(name)
                info.size, info.mode = len(raw), 0o600
                tar.addfile(info, io.BytesIO(raw))
        inventory = [dict(path=name, type="file", bytes=len(raw), sha256=sha(raw)) for name, raw in sorted(members.items())]
        inventory += [dict(path=name, type="directory", bytes=0) for name in directories]
        inventory.sort(key=lambda item: item["path"])
        descriptors = [dict(role=role, digest=d["digest"], bytes=d["size"], mediaType=d["mediaType"]) for role, d in (("manifest", manifest_d), ("config", config_d), ("layer", layer_d))]
        self.image = dict(schema="fsgg.fourd.native-image-binding/1", qualified=False, sourceRevision=self.revision, sourceTree=self.tree,
                          sourceInventorySha256=self.inventory, imageReference="fixture@" + manifest_d["digest"], imageId=config_d["digest"], buildConfigId=config_d["digest"], manifestDigest=manifest_d["digest"], configDigest=config_d["digest"],
                          archivePath=str(archive), archiveSha256=sha(archive.read_bytes()), archiveBytes=archive.stat().st_size,
                          archiveInventorySha256=sha(canonical(inventory).rstrip(b"\n")), ociDescriptors=descriptors, buildIdentity={}, freshLoadStore="fixture/store", freshLoadRunroot="fixture/runroot", loadOutputSha256="f" * 64,
                          remainingGate="production P2 native operation", temporaryMountNamespaceExited=False, postNamespaceIdentityVerified=False,
                          nativeObservations=dict(loadedImageId=config_d["digest"][7:], loadedManifestDigest=manifest_d["digest"],
                              postNamespaceImageId=config_d["digest"][7:], postNamespaceManifestDigest=manifest_d["digest"],
                              hostNamespaceBefore="mnt:[10]", hostNamespaceAfter="mnt:[10]", temporaryDeviceBefore=3,
                              temporaryDeviceAfter=3, supervisorChildrenRemaining=0, supervisorReadersRemaining=0))
        self.image_path = self.write("image.json", self.image)
        self.archive_candidate = {name: self.image[name] for name in (
            "sourceRevision", "sourceTree", "sourceInventorySha256", "archivePath", "archiveSha256", "archiveBytes", "archiveInventorySha256", "ociDescriptors", "buildConfigId", "configDigest", "manifestDigest")}
        self.archive_candidate["schema"] = "fsgg.fourd.native-archive-candidate/1"
        self.archive_path = self.write("archive-candidate.json", self.archive_candidate)
        self.p2 = dict(schema="fsgg.fourd.native-p2-qualification/1", outcome="passed", qualified=True,
                       coordinationRevision=self.p2proof["sourceSha"], executorDllSha256=self.packet["p2"]["executorDllSha256"],
                       sourceRevision=self.revision, sourceTree=self.tree, sourceInventorySha256=self.inventory,
                       imageReference=self.image["imageReference"], imageId=self.image["imageId"], manifestDigest=self.image["manifestDigest"], operationIdentity="test",
                       verificationSha256="ce00be26c8836753bc8c7518c43727835f813b24fdc36ed76dbc44016f75c1a3", outputSha256="f" * 64,
                       executionStarted=True, cleanupCompleted=True, cancellationRequested=False, terminationObserved=False,
                       duplicateSuppressedAfterReconstruction=True, settledRecoveryWithoutRelaunch=True, remainingExecutionRoots=0, closedOperationOnly=True,
                       prelaunchRefusals=dict(source="source-refused", scope="scope-refused", operation="operation-refused"))
        self.p2_path = self.write("p2.json", self.p2)


@unittest.skipUnless(os.environ.get("FSGG_FOURD_NATIVE_TEST_COMMAND"), "compiled native command required")
class NativeProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="fourd-native-test-")
        self.addCleanup(self.temporary.cleanup)
        self.f = NativeFixture(self.temporary.name, json.loads(os.environ["FSGG_FOURD_NATIVE_TEST_COMMAND"]))

    def test_full_acquired_journey_and_retired_plaintext(self):
        f = self.f
        f.image_ready()
        f.ok("revalidate-source", evidence=f.source)
        f.ok("revalidate-p2", evidence=f.p2proof)
        f.intent("p2")
        f.ok("p2", path=str(f.p2_path))
        shutil.rmtree(f.context)
        f.git_path.unlink()
        Path(f.image["archivePath"]).unlink()
        f.archive_path.unlink()
        Path(f.p2proof["executorDllPath"]).unlink()
        result = f.finish()
        self.assertTrue(result["state"]["successful"])
        self.assertTrue(result["state"]["cleanupComplete"])
        self.assertIsNone(result["state"]["remoteConclusion"])
        self.assertFalse(result["state"]["wholeAccepted"])
        self.assertIsNone(result["state"]["remoteCredentialRetirement"])
        run = json.loads(f.run_path.read_bytes())
        run.update(status="completed", conclusion="success")
        jobs = json.loads(f.job_path.read_bytes())
        jobs["jobs"][0].update(status="completed", conclusion="success")
        remote_run = f.write("terminal-run.json", run)
        remote_jobs = f.write("terminal-jobs.json", jobs)
        local_elapsed = result["state"]["elapsed"]
        result = f.ok("remote", elapsed=local_elapsed, evidence=dict(runPath=str(remote_run), jobPath=str(remote_jobs), jobLinkHeaders=[]))
        self.assertFalse(result["state"]["wholeAccepted"])
        capsule = f.packet["sourceCapsule"]
        result = f.ok("remote-credential-retirement", elapsed=local_elapsed, evidence=dict(rootIdentity=result["state"]["rootIdentity"],
                      runId=42, runAttempt=1, releaseId=capsule["releaseId"], assetId=capsule["assetId"], tag=capsule["tag"],
                      secretCount=0, releaseCount=0, failureCount=0, assetStatus="404", releaseStatus="404", tagStatus="404", privateKeyAbsent=True, publicKeyAbsent=True))
        self.assertTrue(result["state"]["wholeAccepted"])

    def test_quint_journey_capture(self):
        target = os.environ.get("FSGG_FOURD_NATIVE_QUINT_OBSERVATIONS")
        if not target:
            self.skipTest("correspondence capture path required")
        f = self.f
        f.trace_zero = True
        f.root_evidence["nowUnixSeconds"] = f.started
        f.image_ready()
        f.ok("revalidate-source", evidence=f.source)
        f.ok("revalidate-p2", evidence=f.p2proof)
        f.intent("p2")
        f.ok("p2", path=str(f.p2_path))
        f.finish()
        Path(target).write_bytes(canonical(f.observed))

    def test_source_executable_mode_is_real(self):
        f = self.f
        f.bound()
        f.intent("source")
        (f.context / "run.sh").chmod(0o600)
        code, result = f.event("source", evidence=f.source)
        self.assertEqual(code, 2)
        self.assertIn("native-context-mode-refused", result["refusal"])
        self.assertEqual(result["history"][-1]["kind"], "refuse")
        (f.context / "run.sh").chmod(0o700)
        code, retried = f.event("source", evidence=f.source)
        self.assertEqual(code, 2)
        self.assertEqual(retried["state"]["refusal"], "native-context-mode-refused")
        self.assertFalse(retried["state"]["successful"])

    def test_borrowed_runtime_closure_cannot_qualify_the_executing_producer(self):
        f = self.f
        actual_apphost = Path(f.command[0])
        original_apphost = sha(actual_apphost.read_bytes())
        original_dll = sha(actual_apphost.with_name("FourD.Typed.dll").read_bytes())
        for member in ("FourD.Typed.dll", "FourD.Typed"):
            with self.subTest(member=member):
                borrowed = f.root / ("borrowed-" + member)
                borrowed.mkdir()
                for name in f.files:
                    shutil.copy2(f.runtime / name, borrowed / name)
                (borrowed / member).write_bytes(b"foreign closure member with a matching declared hash")
                files = {name: sha((borrowed / name).read_bytes()) for name in f.files}
                evidence = dict(f.producer, runtimeRoot=str(borrowed), expectedFiles=files)
                binding = dict(schema="fsgg.fourd.typed-policy-binding/1", sourceSha=evidence["expectedSourceSha"], files=files)
                binding_path = f.write("borrowed-binding.json", binding)
                evidence.update(bindingPath=str(binding_path), expectedBindingSha256=sha(binding_path.read_bytes()))
                provenance = json.loads(Path(f.producer["provenancePath"]).read_bytes())
                provenance.update(files=files, bindingSha256=evidence["expectedBindingSha256"])
                provenance_path = f.write("borrowed-provenance.json", provenance)
                evidence.update(provenancePath=str(provenance_path), expectedProvenanceSha256=sha(provenance_path.read_bytes()))
                code, result = f.call("native-join", dict(schema="fsgg.fourd.native-producer-request/1", producer=evidence))
                self.assertEqual(code, 2)
                self.assertIn("native-acquired-bytes-refused", result["refusal"])
                self.assertEqual(sha(actual_apphost.read_bytes()), original_apphost)
                self.assertEqual(sha(actual_apphost.with_name("FourD.Typed.dll").read_bytes()), original_dll)

    def test_packet_wait_consumes_original_clock(self):
        f = self.f
        f.ok("producer", evidence=f.producer)
        code, result = f.event("wait", elapsed=600)
        self.assertEqual(code, 2)
        self.assertIn("native-packet-wait-expired", result["refusal"])
        self.assertEqual(result["actions"], ["begin-cleanup"])

    def test_duplicate_packet_asset_refuses_before_key(self):
        f = self.f
        f.release["assets"].append(dict(f.release["assets"][0]))
        f.write("release.json", f.release)
        f.ok("producer", evidence=f.producer)
        code, result = f.event("packet", evidence=f.root_evidence)
        self.assertEqual(code, 2)
        self.assertIn("ambiguity", result["refusal"])

    def test_missing_launch_ack_cannot_make_native_result(self):
        f = self.f
        f.prepared()
        f.ok("revalidate-source", evidence=f.source)
        f.ok("intent", operation="image", resource="image-scope", identity="image-inode")
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertIn("without-acquired-join", result["refusal"])

    def test_p2_requires_fresh_acquired_executor_before_intent(self):
        f = self.f
        f.image_ready()
        f.ok("revalidate-source", evidence=f.source)
        Path(f.p2proof["executorDllPath"]).write_bytes(b"mutated")
        code, result = f.event("revalidate-p2", evidence=f.p2proof)
        self.assertEqual(code, 2)
        self.assertIn("acquired-bytes-refused", result["refusal"])

    def test_owned_settlement_is_exact(self):
        f = self.f
        f.bound()
        f.ok("begin-cleanup")
        code, result = f.event("close", resource="key", identity="replacement-inode", childrenRemaining=0, readersRemaining=0, pathsRemaining=0)
        self.assertEqual(code, 2)
        self.assertIn("owned-settlement-refused", result["refusal"])

    def test_archive_false_digest_cannot_make_image(self):
        f = self.f
        f.image_archive_ready()
        Path(f.image["archivePath"]).write_bytes(b"altered OCI archive")
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertIn("archive-size-refused", result["refusal"])

    def test_image_requires_archive_admission_before_custody_or_load(self):
        f = self.f
        f.image_build_ready()
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertFalse(result["eventAccepted"])
        self.assertEqual(result["state"]["refusal"], "native-image-archive-proof-required")
        self.assertNotIn("load-image", result["actions"])

    def test_oci_directory_transport_spelling_keeps_existing_inventory(self):
        f = self.f
        f.make_native_evidence(include_directories=True)
        f.image_archive_ready()
        result = f.ok("image", path=str(f.image_path))
        self.assertTrue(result["eventAccepted"])
        self.assertEqual(result["state"]["phase"], "image-ready")

    def test_archive_config_mismatch_refuses_before_load(self):
        f = self.f
        f.image_build_ready()
        f.archive_candidate["buildConfigId"] = "sha256:" + "a" * 64
        f.write("archive-candidate.json", f.archive_candidate)
        code, result = f.event("archive", path=str(f.archive_path))
        self.assertEqual(code, 2)
        self.assertFalse(result["eventAccepted"])
        self.assertEqual(result["state"]["refusal"], "native-archive-profile-refused")
        self.assertIsNone(result["state"]["archiveProof"])
        self.assertNotIn("load-image", result["actions"])

    def test_changed_archive_bytes_refuse_before_load(self):
        f = self.f
        f.image_build_ready()
        Path(f.image["archivePath"]).write_bytes(b"changed archive bytes")
        code, result = f.event("archive", path=str(f.archive_path))
        self.assertEqual(code, 2)
        self.assertFalse(result["eventAccepted"])
        self.assertEqual(result["state"]["refusal"], "native-archive-size-refused")
        self.assertNotIn("load-image", result["actions"])

    def test_valid_second_archive_cannot_rebind_admission(self):
        f = self.f
        target = os.environ.get("FSGG_FOURD_NATIVE_QUINT_ARCHIVE_MUTATION_OBSERVATIONS")
        if target:
            f.trace_zero = True
            f.root_evidence["nowUnixSeconds"] = f.started
        f.image_archive_ready()
        previous = f.observed[-1]["state"]
        f.make_native_evidence(layer_bytes=b"different valid OCI generation")
        code, result = f.event("archive", path=str(f.archive_path))
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-archive-rebind-refused")
        self.assertEqual(previous["archive"], result["modelState"]["archive"])
        self.assertEqual(result["modelState"]["currentArchive"], 2)
        self.assertNotIn("load-image", result["actions"])
        if target:
            Path(target).write_bytes(canonical(f.observed))

    def test_post_namespace_raw_image_identity_cannot_be_summarized_away(self):
        f = self.f
        f.image_archive_ready()
        f.image["nativeObservations"]["postNamespaceImageId"] = "a" * 64
        f.image["temporaryMountNamespaceExited"] = True
        f.image["postNamespaceIdentityVerified"] = True
        f.write("image.json", f.image)
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-loaded-image-join-refused")
        self.assertFalse(result["state"]["imageObserved"])

    def test_post_namespace_raw_boundary_mismatch_refuses(self):
        f = self.f
        f.image_archive_ready()
        f.image["nativeObservations"]["hostNamespaceAfter"] = "mnt:[11]"
        f.write("image.json", f.image)
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-post-namespace-boundary-refused")

    def test_unsettled_image_supervisor_cannot_make_result(self):
        f = self.f
        f.image_archive_ready()
        f.image["nativeObservations"]["supervisorReadersRemaining"] = 1
        f.write("image.json", f.image)
        code, result = f.event("image", path=str(f.image_path))
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-image-supervisor-unsettled")

    def test_archive_allowance_cannot_survive_original_image_deadline(self):
        f = self.f
        f.image_archive_ready()
        code, result = f.event("revalidate-source", elapsed=3325, evidence=f.source)
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-build-deadline-refused")
        self.assertNotIn("execute-image-command", result["actions"])
        self.assertNotIn("load-image", result["actions"])

    def test_selected_native_profile_requires_two_derived_cpus(self):
        f = self.f
        f.bound()
        f.intent("source")
        f.ok("source", evidence=f.source)
        facts = json.loads(f.resources_path.read_bytes())
        facts["cpu"]["affinity"] = [0]
        facts["cpu"]["effectiveCpuUpperBound"] = 1.0
        facts["cpu"]["cgroupAncestors"][0]["quota"] = "100000"
        f.write("resources.json", facts)
        code, result = f.event("resources", path=str(f.resources_path), launcherUid=1000, launcherGid=1000)
        self.assertEqual(code, 2)
        self.assertEqual(result["state"]["refusal"], "native-cpu-derived-refused")

    def test_capability_cannot_start_image(self):
        other = Path(self.temporary.name) / "capability"
        other.mkdir()
        f = NativeFixture(other, self.f.command, mode="capability")
        f.prepared()
        f.ok("revalidate-source", evidence=f.source)
        code, result = f.event("intent", operation="image", resource="image", identity="image-inode")
        self.assertEqual(code, 2)
        self.assertIn("capability-effect-refused", result["refusal"])

    def test_cleanup_cannot_erase_cancellation(self):
        f = self.f
        f.prepared()
        code, result = f.event("cancel")
        self.assertEqual(code, 2)
        self.assertTrue(result["state"]["cancelled"])
        self.assertFalse(result["state"]["successful"])
        self.assertEqual(result["actions"], ["begin-cleanup"])
        code, result = f.event("begin-cleanup")
        self.assertEqual(code, 2)
        self.assertTrue(result["state"]["cancelled"])
        self.assertEqual(result["state"]["disposition"], "refused")

    def test_insufficient_p2_budget_never_launches(self):
        f = self.f
        f.image_ready()
        f.ok("revalidate-source", evidence=f.source)
        f.ok("revalidate-p2", evidence=f.p2proof)
        code, result = f.event("intent", elapsed=3326, operation="p2", resource="p2", identity="p2-inode")
        self.assertEqual(code, 2)
        self.assertIn("insufficient-p2-budget", result["refusal"])

    def test_partial_job_census_never_exposes_key(self):
        f = self.f
        jobs = json.loads(f.job_path.read_bytes())
        jobs["total_count"] = 2
        f.write("job.json", jobs)
        f.ok("producer", evidence=f.producer)
        code, result = f.event("packet", evidence=f.root_evidence)
        self.assertEqual(code, 2)
        self.assertIn("job-census-refused", result["refusal"])

    def test_self_authored_historical_source_receipt_is_not_fresh_evidence(self):
        f = self.f
        f.bound()
        f.intent("source")
        code, result = f.event("source-proof", proof=dict(identity="f" * 64, revision=f.revision, tree=f.tree, inventorySha256=f.inventory))
        self.assertEqual(code, 2)
        self.assertIn("self-authored-source-proof-refused", result["refusal"])


if __name__ == "__main__":
    unittest.main()
