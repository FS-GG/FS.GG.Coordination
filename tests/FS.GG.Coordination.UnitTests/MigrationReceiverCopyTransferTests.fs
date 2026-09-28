module FS.GG.Coordination.MigrationReceiverCopyTransferTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private unwrap = function | Ok value -> value | Error error -> Assert.Fail error; Unchecked.defaultof<_>
let private run =
    let candidate = String.replicate 40 "a"
    { CandidateSha = candidate; WorkflowRunId = 991L; WorkflowRunAttempt = 1
      RunNonce = $"991-1-{candidate}"; CorpusSha256 = String.replicate 64 "b" }
let private receiverIds = [ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ]
let private refsPrefix = $"refs/heads/gs2-09-7/{run.RunNonce}/receivers/"

let private git cwd arguments =
    use child = new Process()
    let info = ProcessStartInfo("/usr/bin/git")
    if cwd <> "" then
        info.ArgumentList.Add "-C"
        info.ArgumentList.Add cwd
    for argument in arguments do info.ArgumentList.Add argument
    info.UseShellExecute <- false; info.RedirectStandardInput <- true; info.RedirectStandardOutput <- true; info.RedirectStandardError <- true
    child.StartInfo <- info
    Assert.True(child.Start()); child.StandardInput.Close()
    let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
    Assert.True(child.WaitForExit(30000)); Assert.True(child.ExitCode = 0, error)
    output.Trim()

let private withTemp action =
    let root = Path.Combine(Path.GetTempPath(), "receiver-copy-transfer-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try action root
    finally if Directory.Exists root then Directory.Delete(root, true)

let private receivers =
    [ for id in receiverIds ->
            id, $"FS-GG/FS.GG.{id}", String.replicate 40 (string ((id.Length % 9) + 1)), "", refsPrefix + id,
            [ "README.md", "100644", "blob", Text.Encoding.UTF8.GetBytes($"receiver {id}\n")
              ".github/workflows/test.yml", "100644", "blob", Text.Encoding.UTF8.GetBytes($"name: {id}\n")
              "tools/run.sh", "100755", "blob", Text.Encoding.UTF8.GetBytes("#!/bin/sh\n") ] ]
let private prepare root =
    let store = Path.Combine(root, "source.git")
    let manifest = MigrationReceiverCopyTransfer.prepareSyntheticForTests run (String.replicate 64 "c") (String.replicate 64 "d") store receivers |> unwrap
    let verified = MigrationReceiverCopyTransfer.verifySyntheticForTests run (String.replicate 64 "c") (String.replicate 64 "d") store receivers manifest |> unwrap
    manifest, verified

let private bare root name =
    let path = Path.Combine(root, name)
    git "" [ "init"; "--bare"; path ] |> ignore
    path

let private authority =
    { TargetRepository = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"
      ContentsWrite = true; WorkflowsWrite = true; ActionsSuppressed = true; ProtectedCustody = true }

let private reseal (candidate: MigrationReceiverCopyExecutionReceipt) =
    let operation =
        match candidate.Operation with
        | CreateReceiverCopies -> "create"
        | ReadReceiverCopies -> "read"
        | RemoveReceiverCopies -> "remove"
    let fields =
        [ "fsgg.receiver-copy-execution-receipt/2"; operation; candidate.AttemptId
          candidate.ManifestFingerprint; string candidate.Applied; string candidate.DispatchCount ]
        @ [ for KeyValue(name, commit) in candidate.Refs do yield name; yield commit ]
        @ [ for item in candidate.TargetObjects do
                yield item.RefName; yield item.CommitOid; yield item.TreeOid
                yield String.concat "," item.ParentOids; yield item.AuthorIdentity
                yield item.CommitterIdentity; yield item.SignatureStatus
                yield item.RequestIdentitySha256 ]
    let digest =
        fields |> String.concat "\000" |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()
    { candidate with Fingerprint=digest }

[<Fact>]
let ``seven deterministic parentless copies roundtrip through one local atomic create and cleanup`` () =
    withTemp (fun root ->
        let manifest, verified = prepare root
        Assert.Equal(7, manifest.DerivedRefs.Length)
        Assert.All(manifest.DerivedRefs, fun row ->
            Assert.Equal(row.DerivedCommit, git manifest.ObjectStorePath [ "rev-list"; "--parents"; "-n"; "1"; row.DerivedCommit ])
            Assert.NotEqual(row.SourceCommit, row.DerivedCommit)
            Assert.Equal(row.SourceTree, row.DerivedTree))
        let target = bare root "target.git"
        git manifest.ObjectStorePath [ "push"; target; manifest.DerivedRefs.Head.DerivedCommit + ":refs/heads/unrelated" ] |> ignore
        let transport = MigrationReceiverCopyGitTransport.localBare target |> unwrap
        let attempts = Path.Combine(root, "attempts")
        let created = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts transport |> unwrap
        Assert.True(MigrationReceiverCopyExecution.verifyReceipt manifest created)
        let wrongObject =
            { created.TargetObjects.Head with RequestIdentitySha256=String.replicate 64 "0" }
        let tampered =
            [ { created with Fingerprint=String.replicate 64 "0" }
              reseal { created with AttemptId=String.replicate 64 "0" }
              reseal { created with DispatchCount=2 }
              reseal { created with TargetObjects=wrongObject :: created.TargetObjects.Tail } ]
        Assert.All(tampered, fun candidate ->
            Assert.False(MigrationReceiverCopyExecution.verifyReceipt manifest candidate))
        Assert.True(created.Applied); Assert.Equal(1, created.DispatchCount); Assert.Equal(7, created.Refs.Count)
        Assert.Equal(7, created.TargetObjects.Length)
        Assert.All(created.TargetObjects, fun observed ->
            let planned = manifest.DerivedRefs |> List.find (fun row -> row.DerivedRef = observed.RefName)
            Assert.Equal(planned.DerivedCommit, observed.CommitOid)
            Assert.Equal(planned.SourceTree, observed.TreeOid)
            Assert.Empty(observed.ParentOids)
            Assert.Equal("unsigned-derived-copy", observed.SignatureStatus)
            Assert.Equal(64, observed.RequestIdentitySha256.Length))
        let replay = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts transport |> unwrap
        Assert.Equal(0, replay.DispatchCount)
        let read = MigrationReceiverCopyExecution.execute authority verified ReadReceiverCopies attempts transport |> unwrap
        Assert.True(MigrationReceiverCopyExecution.verifyReceipt manifest read)
        Assert.False(MigrationReceiverCopyExecution.verifyReceipt manifest (reseal { read with DispatchCount=1 }))
        Assert.True(created.Refs = read.Refs)
        let removed = MigrationReceiverCopyExecution.execute authority verified RemoveReceiverCopies attempts transport |> unwrap
        Assert.Equal(1, removed.DispatchCount); Assert.Empty(removed.Refs)
        Assert.Equal(manifest.DerivedRefs.Head.DerivedCommit, git target [ "rev-parse"; "refs/heads/unrelated" ])
        let removedReplay = MigrationReceiverCopyExecution.execute authority verified RemoveReceiverCopies attempts transport |> unwrap
        Assert.Equal(0, removedReplay.DispatchCount))

type private LostResponse(inner: IMigrationReceiverCopyGitTransport) =
    let mutable pushes = 0
    member _.Pushes = pushes
    interface IMigrationReceiverCopyGitTransport with
        member _.SupportsAtomic = inner.SupportsAtomic
        member _.Read(target, prefix) = inner.Read(target, prefix)
        member _.VerifyFresh(target, root, manifest) = inner.VerifyFresh(target, root, manifest)
        member _.PushAtomic(source, target, updates) =
            pushes <- pushes + 1
            inner.PushAtomic(source, target, updates) |> Result.bind (fun () -> Error "simulated-lost-response")

[<Fact>]
let ``lost response is reconciled by readback and never resent`` () =
    withTemp (fun root ->
        let (manifest, verified), target = prepare root, bare root "target.git"
        let lost = LostResponse(MigrationReceiverCopyGitTransport.localBare target |> unwrap)
        let attempts = Path.Combine(root, "attempts")
        let first = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts (lost :> IMigrationReceiverCopyGitTransport)
        Assert.Equal(Error "simulated-lost-response", first)
        let replay = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts (lost :> IMigrationReceiverCopyGitTransport) |> unwrap
        Assert.Equal(0, replay.DispatchCount); Assert.Equal(1, lost.Pushes))

[<Fact>]
let ``reservation interruption never causes a blind resend`` () =
    withTemp (fun root ->
        let (_, verified), target = prepare root, bare root "target.git"
        let transport = MigrationReceiverCopyGitTransport.localBare target |> unwrap
        let attempts = Path.Combine(root, "attempts")
        let cut = MigrationReceiverCopyExecution.executeWithCutForTests authority verified CreateReceiverCopies attempts transport true
        Assert.Equal(Error "receiver-copy-execution-cut-after-reservation", cut)
        let replay = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts transport
        Assert.Equal(Error "receiver-copy-execution-recovery-absent", replay))

[<Fact>]
let ``authority atomic and exact mapping controls fail closed`` () =
    withTemp (fun root ->
        let (manifest, verified), target = prepare root, bare root "target.git"
        let transport = MigrationReceiverCopyGitTransport.localBare target |> unwrap
        let attempt name auth value =
            let result = MigrationReceiverCopyExecution.execute auth value CreateReceiverCopies (Path.Combine(root, name)) transport
            Assert.True(Result.isError result)
        attempt "no-workflows" { authority with WorkflowsWrite = false } verified
        attempt "actions-enabled" { authority with ActionsSuppressed = false } verified
        attempt "no-custody" { authority with ProtectedCustody = false } verified
        let unsupported = MigrationReceiverCopyGitTransport.localBareWithAtomicSupportForTests target false |> unwrap
        Assert.Equal(Error "receiver-copy-execution-atomic-unsupported", MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies (Path.Combine(root, "unsupported")) unsupported))

[<Fact>]
let ``self-consistent caller substitutions cannot become verified transfer authority`` () =
    withTemp (fun root ->
        let manifest, _ = prepare root
        let first = manifest.DerivedRefs.Head
        let candidates =
            [ { manifest with DerivedRefs = { first with SourceRepository = first.SourceRepository + "-foreign" } :: manifest.DerivedRefs.Tail }
              { manifest with DerivedRefs = { first with DerivedCommit = String.replicate 40 "f" } :: manifest.DerivedRefs.Tail }
              { manifest with ObjectStorePath = Path.Combine(root, "substituted.git") }
              { manifest with BlobSha256BySha1 = manifest.BlobSha256BySha1 |> Map.add (manifest.BlobSha256BySha1.Keys |> Seq.head) (String.replicate 64 "e") } ]
            |> List.map MigrationReceiverCopyTransfer.recomputeFingerprintForTests
        for candidate in candidates do
            let result = MigrationReceiverCopyTransfer.verifySyntheticForTests run (String.replicate 64 "c") (String.replicate 64 "d") (Path.Combine(root, "source.git")) receivers candidate
            Assert.True(Result.isError result))

[<Fact>]
let ``cleanup refuses corrupt stale and foreign create markers before delete`` () =
    withTemp (fun root ->
        for kind in [ "corrupt"; "stale"; "foreign" ] do
            let caseRoot = Path.Combine(root, kind)
            Directory.CreateDirectory caseRoot |> ignore
            let manifest, verified = prepare caseRoot
            let target = bare caseRoot "target.git"
            let transport = MigrationReceiverCopyGitTransport.localBare target |> unwrap
            let attempts = Path.Combine(caseRoot, "attempts")
            MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies attempts transport |> unwrap |> ignore
            let marker = Path.Combine(attempts, "create.applied")
            let lines = File.ReadAllLines marker
            match kind with
            | "corrupt" -> lines[0] <- "unknown-schema"
            | "stale" -> lines[2] <- String.replicate 64 "1"
            | _ -> lines[3] <- String.replicate 64 "2"
            File.WriteAllText(marker, String.Join('\n', lines) + "\n")
            let cleanup = MigrationReceiverCopyExecution.execute authority verified RemoveReceiverCopies attempts transport
            Assert.True(Result.isError cleanup)
            let readback = MigrationReceiverCopyExecution.execute authority verified ReadReceiverCopies attempts transport |> unwrap
            Assert.Equal(7, readback.Refs.Count))

type private ControlledTransport(initial: Map<string, string>) =
    let mutable refs = initial
    let mutable pushed = 0
    member _.Pushes = pushed
    interface IMigrationReceiverCopyGitTransport with
        member _.SupportsAtomic = true
        member _.Read(target, _) = Ok { TargetRepository = target; Refs = refs; Objects=[]; UnrelatedRefsFingerprint = String.replicate 64 "0" }
        member _.VerifyFresh(target, _, manifest) =
            let objects =
                manifest.DerivedRefs
                |> List.map (fun row ->
                    { RefName=row.DerivedRef; CommitOid=row.DerivedCommit; TreeOid=row.DerivedTree
                      ParentOids=[]; AuthorIdentity="FS.GG Migration Copy <migration-copy@invalid>"
                      CommitterIdentity="FS.GG Migration Copy <migration-copy@invalid>"
                      SignatureStatus="unsigned-derived-copy"; RequestIdentitySha256=String.replicate 64 "a" })
            Ok { TargetRepository = target
                 Refs = manifest.DerivedRefs |> List.map (fun row -> row.DerivedRef, row.DerivedCommit) |> Map.ofList
                 Objects=objects; UnrelatedRefsFingerprint = "" }
        member _.PushAtomic(_, _, updates) =
            pushed <- pushed + 1
            Assert.Equal(7, updates.Length)
            refs <- updates |> List.choose (fun update -> update.NewCommit |> Option.map (fun commit -> update.RefName, commit)) |> Map.ofList
            Ok()

[<Fact>]
let ``permissive or swapped local object store refuses verification and execution`` () =
    withTemp (fun root ->
        let modeRoot = Path.Combine(root, "mode")
        Directory.CreateDirectory modeRoot |> ignore
        let manifest, verified = prepare modeRoot
        File.SetUnixFileMode(manifest.ObjectStorePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
        Assert.True(MigrationReceiverCopyTransfer.verifySyntheticForTests run (String.replicate 64 "c") (String.replicate 64 "d") manifest.ObjectStorePath receivers manifest |> Result.isError)
        let modeTransport = ControlledTransport Map.empty
        Assert.True(MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies (Path.Combine(modeRoot, "attempts")) (modeTransport :> IMigrationReceiverCopyGitTransport) |> Result.isError)
        Assert.Equal(0, modeTransport.Pushes)

        let swapRoot = Path.Combine(root, "swap")
        Directory.CreateDirectory swapRoot |> ignore
        let swappedManifest, swappedVerified = prepare swapRoot
        let saved = swappedManifest.ObjectStorePath + ".saved"
        Directory.Move(swappedManifest.ObjectStorePath, saved)
        Directory.CreateSymbolicLink(swappedManifest.ObjectStorePath, saved) |> ignore
        let swapTransport = ControlledTransport Map.empty
        Assert.True(MigrationReceiverCopyExecution.execute authority swappedVerified CreateReceiverCopies (Path.Combine(swapRoot, "attempts")) (swapTransport :> IMigrationReceiverCopyGitTransport) |> Result.isError)
        Assert.Equal(0, swapTransport.Pushes)

        let nestedModeRoot = Path.Combine(root, "nested-mode")
        Directory.CreateDirectory nestedModeRoot |> ignore
        let nestedModeManifest, nestedModeVerified = prepare nestedModeRoot
        let config = Path.Combine(nestedModeManifest.ObjectStorePath, "config")
        File.SetUnixFileMode(config, File.GetUnixFileMode(config) ||| UnixFileMode.GroupRead)
        Assert.True(MigrationReceiverCopyTransfer.verifySyntheticForTests run (String.replicate 64 "c") (String.replicate 64 "d") nestedModeManifest.ObjectStorePath receivers nestedModeManifest |> Result.isError)
        let nestedModeTransport = ControlledTransport Map.empty
        Assert.True(MigrationReceiverCopyExecution.execute authority nestedModeVerified CreateReceiverCopies (Path.Combine(nestedModeRoot, "attempts")) (nestedModeTransport :> IMigrationReceiverCopyGitTransport) |> Result.isError)
        Assert.Equal(0, nestedModeTransport.Pushes)

        let nestedLinkRoot = Path.Combine(root, "nested-link")
        Directory.CreateDirectory nestedLinkRoot |> ignore
        let nestedLinkManifest, nestedLinkVerified = prepare nestedLinkRoot
        let description = Path.Combine(nestedLinkManifest.ObjectStorePath, "description")
        let savedDescription = Path.Combine(nestedLinkRoot, "saved-description")
        File.Move(description, savedDescription)
        File.CreateSymbolicLink(description, savedDescription) |> ignore
        let nestedLinkTransport = ControlledTransport Map.empty
        Assert.True(MigrationReceiverCopyExecution.execute authority nestedLinkVerified CreateReceiverCopies (Path.Combine(nestedLinkRoot, "attempts")) (nestedLinkTransport :> IMigrationReceiverCopyGitTransport) |> Result.isError)
        Assert.Equal(0, nestedLinkTransport.Pushes)
        // This unprivileged test process cannot chown an entry to a foreign UID;
        // owner-only 0700 traversal prevents another UID from injecting one.
        )

[<Fact>]
let ``mixed wrong and unknown receiver refs refuse with zero dispatch`` () =
    withTemp (fun root ->
        let manifest, verified = prepare root
        let expected = manifest.DerivedRefs |> List.map (fun row -> row.DerivedRef, row.DerivedCommit) |> Map.ofList
        let cases =
            [ "mixed", expected |> Map.remove manifest.DerivedRefs.Head.DerivedRef
              "wrong", expected |> Map.add manifest.DerivedRefs.Head.DerivedRef (String.replicate 40 "f")
              "unknown", expected |> Map.add (refsPrefix + "foreign") (String.replicate 40 "e") ]
        for name, refs in cases do
            let transport = ControlledTransport refs
            let result = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies (Path.Combine(root, name)) (transport :> IMigrationReceiverCopyGitTransport)
            Assert.True(Result.isError result); Assert.Equal(0, transport.Pushes))

[<Fact>]
let ``complete accepted 8999 object corpus reconstructs all seven copies`` () =
    if Environment.GetEnvironmentVariable("FSGG_CAPTURE_ALL_RECEIVER_BLOBS") = "1" then
        let repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
        let projectsRoot = Directory.GetParent(repositoryRoot).FullName
        let read relative = File.ReadAllBytes(Path.Combine(repositoryRoot, relative)) |> ReadOnlyMemory<byte>
        let evidence =
            { Gs2083ReceiptBytes = read "evidence/github-substrate-v2/accepted/GS2-08.3.json"
              ReceiverSourceBindingBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json"
              ReceiverCensusBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json"
              ReceiverSourceManifestsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-manifests.json.gz"
              ReceiverSourceBlobsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-blobs.json.gz" }
        let actualRun =
            let candidate = String.replicate 40 "a"
            { CandidateSha = candidate; WorkflowRunId = 36086215835L; WorkflowRunAttempt = 1
              RunNonce = $"36086215835-1-{candidate}"; CorpusSha256 = String.replicate 64 "b" }
        let copyPlan = MigrationReceiverCopyPlan.derive evidence actualRun |> unwrap
        let locations =
            [ "sdd", "FS.GG.SDD"; "rendering", "FS.GG.Rendering"; "governance", "FS.GG.Governance"
              "templates", "FS.GG.Templates"; "game", "FS.GG.Game"; "audio", "FS.GG.Audio"; "net", "FS.GG.Net" ]
            |> List.map (fun (id, directory) -> id, Path.Combine(projectsRoot, directory)) |> Map.ofList
        use source = MigrationReceiverCopyBlobCapture.createLocalGitSource evidence actualRun copyPlan locations |> unwrap
        let batches = MigrationReceiverCopyBlobCapture.planBatches evidence actualRun copyPlan |> unwrap
        let artifactRoot = Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-blobs-{actualRun.RunNonce}")
        let artifacts = batches |> List.map (MigrationReceiverCopyBlobCapture.captureBatch evidence actualRun copyPlan source artifactRoot >> unwrap)
        let coverage = MigrationReceiverCopyBlobCapture.verifyCoverage evidence actualRun copyPlan batches artifacts |> unwrap
        Assert.Equal(8999, coverage.ReceiverCopyBlobCoverageSha256BySha1.Count)
        withTemp (fun root ->
            let objectStore = Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-transfer-{actualRun.RunNonce}.git")
            if Directory.Exists objectStore then Directory.Delete(objectStore, true)
            let manifest = MigrationReceiverCopyTransfer.prepare evidence actualRun copyPlan batches artifacts coverage objectStore |> unwrap
            let verified = MigrationReceiverCopyTransfer.verify evidence actualRun copyPlan batches artifacts coverage manifest |> unwrap
            Assert.Equal(7, manifest.DerivedRefs.Length)
            Assert.Equal(9836, manifest.DerivedRefs |> List.sumBy _.BlobCount)
            let target = bare root "target.git"
            let transport = MigrationReceiverCopyGitTransport.localBare target |> unwrap
            let created = MigrationReceiverCopyExecution.execute authority verified CreateReceiverCopies (Path.Combine(root, "attempts")) transport |> unwrap
            Assert.Equal(7, created.Refs.Count)
            let removed = MigrationReceiverCopyExecution.execute authority verified RemoveReceiverCopies (Path.Combine(root, "attempts")) transport |> unwrap
            Assert.Empty(removed.Refs)
            Directory.Delete(objectStore, true))
