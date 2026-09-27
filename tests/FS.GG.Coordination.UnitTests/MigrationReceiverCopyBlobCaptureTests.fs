module FS.GG.Coordination.MigrationReceiverCopyBlobCaptureTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FS.GG.Coordination.Cli

let private repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private projectsRoot = Directory.GetParent(repositoryRoot).FullName
let private read relative = File.ReadAllBytes(Path.Combine(repositoryRoot, relative)) |> ReadOnlyMemory<byte>
let private evidence =
    { Gs2083ReceiptBytes = read "evidence/github-substrate-v2/accepted/GS2-08.3.json"
      ReceiverSourceBindingBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json"
      ReceiverCensusBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json"
      ReceiverSourceManifestsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-manifests.json.gz"
      ReceiverSourceBlobsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-blobs.json.gz" }
let private makeRun workflowRunId =
    let candidate = String.replicate 40 "a"
    { CandidateSha = candidate; WorkflowRunId = workflowRunId; WorkflowRunAttempt = 1
      RunNonce = $"{workflowRunId}-1-{candidate}"; CorpusSha256 = String.replicate 64 "b" }
let private plan run =
    match MigrationReceiverCopyPlan.derive evidence run with
    | Ok value -> value
    | Error error -> Assert.Fail error; Unchecked.defaultof<_>
let private unwrap = function | Ok value -> value | Error error -> Assert.Fail error; Unchecked.defaultof<_>
let private locations =
    [ "sdd", "FS.GG.SDD"; "rendering", "FS.GG.Rendering"; "governance", "FS.GG.Governance"
      "templates", "FS.GG.Templates"; "game", "FS.GG.Game"; "audio", "FS.GG.Audio"; "net", "FS.GG.Net" ]
    |> List.map (fun (id, directory) -> id, Path.Combine(projectsRoot, directory)) |> Map.ofList
let private rootFor (run: MigrationSandboxSeedRequest) = Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-blobs-{run.RunNonce}")
let private runGit arguments =
    let start = ProcessStartInfo("/usr/bin/git")
    arguments |> List.iter start.ArgumentList.Add
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    use child = Process.Start start
    let diagnostics = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, diagnostics)
let private requireLocalReceiverRepositories () =
    Assert.All(locations.Values, fun path -> Assert.True(Directory.Exists path, $"missing pinned receiver repository: {path}"))

[<Fact>]
let ``accepted copy plan produces exact deterministic bounded blob batches`` () =
    let run = makeRun 36086215836L
    let copyPlan = plan run
    let batches = MigrationReceiverCopyBlobCapture.planBatches evidence run copyPlan |> unwrap
    Assert.Equal(8539, batches |> List.sumBy (_.ReceiverCopyBlobSha1s.Length))
    Assert.Equal(78453001L, batches |> List.sumBy _.ReceiverCopyBlobExpectedBytes)
    Assert.Equal([ 0 .. batches.Length - 1 ], batches |> List.map _.ReceiverCopyBlobBatchOrdinal)
    Assert.All(batches, fun batch ->
        Assert.InRange(batch.ReceiverCopyBlobSha1s.Length, 1, 64)
        Assert.InRange(batch.ReceiverCopyBlobExpectedBytes, 1L, 8388608L)
        Assert.Equal(List.sort batch.ReceiverCopyBlobSha1s, batch.ReceiverCopyBlobSha1s)
        Assert.Equal(copyPlan.ReceiverCopyFingerprint, batch.ReceiverCopyBlobPlanFingerprint)
        Assert.Equal(64, batch.ReceiverCopyBlobBatchFingerprint.Length))
    let repeated = MigrationReceiverCopyBlobCapture.planBatches evidence run copyPlan |> unwrap
    Assert.True((batches = repeated))

[<Fact>]
let ``local source refuses a census locator swapped to another accepted repository`` () =
    let run = makeRun 36086215836L
    let copyPlan = plan run
    let root = Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-identity-{Guid.NewGuid():N}")
    try
        Directory.CreateDirectory root |> ignore
        runGit [ "-C"; root; "init"; "--quiet" ]
        runGit [ "-C"; root; "remote"; "add"; "origin"; "https://github.com/FS-GG/not-the-accepted-receiver.git" ]
        let hermeticLocations =
            copyPlan.ReceiverCopyMappings
            |> List.map (fun mapping -> mapping.ReceiverCopyId, root)
            |> Map.ofList
        match MigrationReceiverCopyBlobCapture.createLocalGitSource evidence run copyPlan hermeticLocations with
        | Error error -> Assert.Contains("repository-identity", error)
        | Ok source -> source.Dispose(); Assert.Fail("expected swapped repository refusal")
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``high volume Git diagnostics are continuously drained into a bounded projection`` () =
    let bytes = ReadOnlyMemory<byte>(Array.create (1024 * 1024) (byte 'x'))
    let observed = MigrationReceiverCopyBlobCapture.drainDiagnosticsForTests bytes
    Assert.InRange(observed.Length, 65536, 65600)
    Assert.EndsWith("[diagnostics-truncated]", observed)

[<Fact>]
let ``hung high-stderr child is drained killed and bounded by timeout`` () =
    let script = "import sys,time; sys.stderr.write('x'*1048576); sys.stderr.flush(); time.sleep(60)"
    match MigrationReceiverCopyBlobCapture.runDiagnosticProcessForTests "/usr/bin/python3" [ "-c"; script ] 1000 with
    | Error error ->
        Assert.Contains("process-timeout", error)
        Assert.InRange(error.Split(':') |> Array.last |> Int32.Parse, 65536, 65600)
    | Ok _ -> Assert.Fail("expected bounded child timeout")

[<Fact>]
let ``durability errors and retained union omissions fail closed without full corpus`` () =
    Assert.Equal(Ok(), MigrationReceiverCopyBlobCapture.durabilityResultForTests "parent-fsync" 0 0)
    Assert.Equal(Error "parent-fsync:5", MigrationReceiverCopyBlobCapture.durabilityResultForTests "parent-fsync" -1 5)
    let retained = Map.ofList [ "a", "ra"; "b", "rb" ]
    let captured = Map.ofList [ "c", "cc"; "d", "cd" ]
    let required = Set.ofList [ "a"; "b"; "c" ]
    let union = MigrationReceiverCopyBlobCapture.joinCoverageForTests retained captured required 1 |> unwrap
    Assert.Equal(4, union.Count)
    Assert.True(MigrationReceiverCopyBlobCapture.joinCoverageForTests retained captured (required.Add "missing") 0 |> Result.isError)
    Assert.True(MigrationReceiverCopyBlobCapture.joinCoverageForTests retained (captured.Add("a", "collision")) required 1 |> Result.isError)

[<Fact>]
let ``precreated nonprivate capture root is refused without changing its mode`` () =
    let run = makeRun 36086215837L
    let copyPlan = plan run
    let root = rootFor run
    if Directory.Exists root then Directory.Delete(root, true)
    Directory.CreateDirectory root |> ignore
    File.SetUnixFileMode(root, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead)
    try
        use source =
            { new IMigrationReceiverCopyLocalGitObjectSource with
                member _.ReadBlob _ = Error "unexpected-read"
                member _.Dispose() = () }
        let batch = MigrationReceiverCopyBlobCapture.planBatches evidence run copyPlan |> unwrap |> List.head
        match MigrationReceiverCopyBlobCapture.captureBatch evidence run copyPlan source root batch with
        | Error error -> Assert.Contains("directory-ownership", error)
        | Ok _ -> Assert.Fail("expected precreated root refusal")
        Assert.True(File.GetUnixFileMode(root).HasFlag UnixFileMode.GroupRead)
    finally Directory.Delete(root, true)

[<Fact>]
let ``local pinned source qualification refuses missing inputs and captures only when explicitly enabled`` () =
    let run = makeRun 36086215836L
    let copyPlan = plan run
    if Environment.GetEnvironmentVariable("FSGG_CAPTURE_RECEIVER_BLOB_SAMPLE") = "1" then
        requireLocalReceiverRepositories ()
        let root = rootFor run
        if Directory.Exists root then Directory.Delete(root, true)
        try
            use source = MigrationReceiverCopyBlobCapture.createLocalGitSource evidence run copyPlan locations |> unwrap
            let batch = MigrationReceiverCopyBlobCapture.planBatches evidence run copyPlan |> unwrap |> List.head
            let artifact = MigrationReceiverCopyBlobCapture.captureBatch evidence run copyPlan source root batch |> unwrap
            Assert.Equal(artifact, MigrationReceiverCopyBlobCapture.captureBatch evidence run copyPlan source root batch |> unwrap)
            Assert.Equal(artifact, MigrationReceiverCopyBlobCapture.verifyBatch evidence run copyPlan batch artifact |> unwrap)
            Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode artifact.ReceiverCopyBlobArtifactPath)
            let objectPath = Path.Combine(root, "objects", "sha256", artifact.ReceiverCopyBlobSha256BySha1[batch.ReceiverCopyBlobSha1s.Head] + ".blob")
            let bytes = File.ReadAllBytes objectPath
            bytes[0] <- bytes[0] ^^^ 1uy
            File.WriteAllBytes(objectPath, bytes)
            Assert.True(MigrationReceiverCopyBlobCapture.verifyBatch evidence run copyPlan batch artifact |> Result.isError)
            File.Delete artifact.ReceiverCopyBlobArtifactPath
            File.CreateSymbolicLink(artifact.ReceiverCopyBlobArtifactPath, objectPath) |> ignore
            Assert.True(MigrationReceiverCopyBlobCapture.verifyBatch evidence run copyPlan batch artifact |> Result.isError)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
    else
        let missing =
            copyPlan.ReceiverCopyMappings
            |> List.map (fun mapping -> mapping.ReceiverCopyId, Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"))
            |> Map.ofList
        match MigrationReceiverCopyBlobCapture.createLocalGitSource evidence run copyPlan missing with
        | Error error -> Assert.Contains("repository-missing", error)
        | Ok source -> source.Dispose(); Assert.Fail("expected absent local receiver refusal")

[<Fact>]
let ``full local corpus capture is available as an explicit qualification`` () =
    let run = makeRun 36086215835L
    let copyPlan = plan run
    let batches = MigrationReceiverCopyBlobCapture.planBatches evidence run copyPlan |> unwrap
    if Environment.GetEnvironmentVariable("FSGG_CAPTURE_ALL_RECEIVER_BLOBS") = "1" then
        requireLocalReceiverRepositories ()
        let root = rootFor run
        use source = MigrationReceiverCopyBlobCapture.createLocalGitSource evidence run copyPlan locations |> unwrap
        let artifacts = batches |> List.map (MigrationReceiverCopyBlobCapture.captureBatch evidence run copyPlan source root >> unwrap)
        let coverage = MigrationReceiverCopyBlobCapture.verifyCoverage evidence run copyPlan batches artifacts |> unwrap
        Assert.Equal(8999, coverage.ReceiverCopyBlobCoverageSha256BySha1.Count)
        Assert.Equal(batches.Length, coverage.ReceiverCopyBlobCoverageBatchFingerprints.Length)
        let first, rest = artifacts.Head, artifacts.Tail
        let omitted =
            { first with
                ReceiverCopyBlobSha256BySha1 = first.ReceiverCopyBlobSha256BySha1 |> Map.remove batches.Head.ReceiverCopyBlobSha1s.Head }
        Assert.True(MigrationReceiverCopyBlobCapture.verifyCoverage evidence run copyPlan batches (omitted :: rest) |> Result.isError)
        let retainedKey = copyPlan.ReceiverCopyRetainedBlobSha256BySha1 |> Map.keys |> Seq.head
        let retainedOmitted =
            { copyPlan with
                ReceiverCopyRetainedBlobSha256BySha1 = copyPlan.ReceiverCopyRetainedBlobSha256BySha1 |> Map.remove retainedKey }
        Assert.True(MigrationReceiverCopyBlobCapture.verifyCoverage evidence run retainedOmitted batches artifacts |> Result.isError)
    else
        Assert.True(MigrationReceiverCopyBlobCapture.verifyCoverage evidence run copyPlan batches [] |> Result.isError)
