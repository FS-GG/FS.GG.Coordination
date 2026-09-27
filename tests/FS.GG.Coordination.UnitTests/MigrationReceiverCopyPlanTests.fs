module FS.GG.Coordination.MigrationReceiverCopyPlanTests

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Cli

let private repositoryRoot =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private read relative =
    File.ReadAllBytes(Path.Combine(repositoryRoot, relative)) |> ReadOnlyMemory<byte>

let private evidence =
    { Gs2083ReceiptBytes = read "evidence/github-substrate-v2/accepted/GS2-08.3.json"
      ReceiverSourceBindingBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json"
      ReceiverCensusBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json"
      ReceiverSourceManifestsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-manifests.json.gz"
      ReceiverSourceBlobsGzipBytes = read "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-blobs.json.gz" }

let private candidate = String.replicate 40 "a"
let private run =
    { CandidateSha = candidate
      WorkflowRunId = 36086215835L
      WorkflowRunAttempt = 1
      RunNonce = $"36086215835-1-{candidate}"
      CorpusSha256 = String.replicate 64 "b" }

let private derive accepted requested =
    match MigrationReceiverCopyPlan.derive accepted requested with
    | Ok value -> value
    | Error error -> Assert.Fail(error); Unchecked.defaultof<_>

let private flipped (bytes: ReadOnlyMemory<byte>) =
    let copy = bytes.ToArray()
    copy[copy.Length / 2] <- copy[copy.Length / 2] ^^^ 1uy
    ReadOnlyMemory<byte>(copy)

let private expectError result = Assert.True(Result.isError result)

let private sha256Text (value: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes value) |> Convert.ToHexString |> _.ToLowerInvariant()

let private jsonOptions =
    JsonSerializerOptions(WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private unzip (bytes: ReadOnlyMemory<byte>) =
    use source = new MemoryStream(bytes.ToArray())
    use gzip = new GZipStream(source, CompressionMode.Decompress)
    use target = new MemoryStream()
    gzip.CopyTo target
    target.ToArray()

let private zip (bytes: byte array) =
    use target = new MemoryStream()
    do
        use gzip = new GZipStream(target, CompressionLevel.SmallestSize, true)
        gzip.Write(bytes, 0, bytes.Length)
    ReadOnlyMemory<byte>(target.ToArray())

let private parseNode (bytes: byte array) = JsonNode.Parse(bytes).AsObject()
let private jsonBytes (node: JsonNode) = node.ToJsonString(jsonOptions) |> Encoding.UTF8.GetBytes

let private mutateGzipJson bytes edit =
    let root = unzip bytes |> parseNode
    edit root
    root |> jsonBytes |> zip

let private mutateManifestAndClaims edit =
    let census = evidence.ReceiverCensusBytes.ToArray() |> parseNode
    let manifests = evidence.ReceiverSourceManifestsGzipBytes |> unzip |> parseNode
    edit manifests
    for receiverNode in manifests["receivers"].AsArray() do
        let receiver = receiverNode.AsObject()
        let id = receiver["id"].GetValue<string>()
        let digest = receiver["manifest"].ToJsonString(jsonOptions) |> sha256Text
        receiver["sourceManifestSha256"] <- digest
        let censusReceiver =
            census["receivers"].AsArray()
            |> Seq.map _.AsObject()
            |> Seq.find (fun row -> row["id"].GetValue<string>() = id)
        censusReceiver["sourceManifestSha256"] <- digest
    ReadOnlyMemory<byte>(jsonBytes census), zip (jsonBytes manifests)

let private inner census manifests blobs =
    MigrationReceiverCopyPlan.deriveUnpinnedForTests census manifests blobs run

let private expectInnerError fragment census manifests blobs =
    match inner census manifests blobs with
    | Error error -> Assert.Contains(fragment, error)
    | Ok _ -> Assert.Fail($"expected inner refusal containing {fragment}")

let private missingDigest mapping =
    mapping.ReceiverCopyMissingBlobSha1s |> String.concat "\n" |> sha256Text

[<Fact>]
let ``accepted fixture derives seven bounded receiver copies with missing full-tree bytes`` () =
    let result = derive evidence run
    Assert.Equal(7, result.ReceiverCopyMappings.Length)
    Assert.Equal([ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ],
                 result.ReceiverCopyMappings |> List.map _.ReceiverCopyId)
    Assert.Equal([ 2715; 4801; 3612; 505; 496; 233; 158 ],
                 result.ReceiverCopyMappings |> List.map (_.ReceiverCopyRequiredEntries.Length))
    Assert.Equal(460, result.ReceiverCopyRetainedBlobSha256BySha1.Count)
    let missingCounts = result.ReceiverCopyMappings |> List.map (fun mapping -> mapping.ReceiverCopyId, mapping.ReceiverCopyMissingBlobSha1s.Length)
    let expectedCounts =
        [ "sdd", 1787; "rendering", 3686; "governance", 2550; "templates", 295
          "game", 315; "audio", 97; "net", 78 ]
    Assert.True((missingCounts = expectedCounts), sprintf "unexpected missing counts: %A" missingCounts)
    let missingDigests = result.ReceiverCopyMappings |> List.map (fun mapping -> mapping.ReceiverCopyId, missingDigest mapping)
    let expectedDigests =
        [ "sdd", "81adbaa7f1abd6356ab35d0093a77d17acb4ec6fb51a520be6355b1a305446a3"
          "rendering", "df1fd2b5cc50a3df2620816aef490ed9a3a6846408adba63628c3fe99ad377c7"
          "governance", "b985e27f71db5c15bd52f5de7a8082cd3b9b5c693980c22de2ad13891782fafe"
          "templates", "a9761bdf5fa605fc4eba0fa2e4ea5bebce0d51ed9d1787b1fd4771ac309513ff"
          "game", "23c3a22cb772e614cd4193f64929ba1511f18326d292fbb797eb98d0af30b934"
          "audio", "c94eb29f3d22767814c6926c1d066a8aa12dd32ffe39a16f11b1da363385648d"
          "net", "6d61f966457d6a401b75f21c63fc28fd286cabb0af66e5f16c17998b9e707265" ]
    Assert.True((missingDigests = expectedDigests), sprintf "unexpected missing digests: %A" missingDigests)
    Assert.All(result.ReceiverCopyMappings, fun mapping ->
        Assert.Equal("FS-GG/FS.GG.GitHub.Substrate.Sandbox", mapping.ReceiverCopyRepository)
        Assert.Equal($"refs/heads/gs2-09-7/{run.RunNonce}/receivers/{mapping.ReceiverCopyId}", mapping.ReceiverCopyPlannedRef)
        Assert.NotEmpty(mapping.ReceiverCopyMissingBlobSha1s))
    Assert.True(result.ReceiverCopyReceiptVerification.CompatibilityApplied)
    Assert.Equal(64, result.ReceiverCopyFingerprint.Length)
    Assert.Equal(Ok result, MigrationReceiverCopyPlan.verify evidence run result)

[<Fact>]
let ``receipt and every accepted artifact byte binding reject tampering`` () =
    Assert.Equal(
        Error "receipt-raw-sha256",
        MigrationReceiverCopyPlan.derive
            { evidence with Gs2083ReceiptBytes = flipped evidence.Gs2083ReceiptBytes }
            run)
    [ { evidence with ReceiverSourceBindingBytes = flipped evidence.ReceiverSourceBindingBytes }
      { evidence with ReceiverCensusBytes = flipped evidence.ReceiverCensusBytes }
      { evidence with ReceiverSourceManifestsGzipBytes = flipped evidence.ReceiverSourceManifestsGzipBytes }
      { evidence with ReceiverSourceBlobsGzipBytes = flipped evidence.ReceiverSourceBlobsGzipBytes } ]
    |> List.iter (fun accepted -> expectError (MigrationReceiverCopyPlan.derive accepted run))

[<Fact>]
let ``inner validator recomputes manifest content and exact source correspondence`` () =
    match inner evidence.ReceiverCensusBytes evidence.ReceiverSourceManifestsGzipBytes evidence.ReceiverSourceBlobsGzipBytes with
    | Error error -> Assert.Fail(error)
    | Ok(mappings, retained) -> Assert.Equal(7, mappings.Length); Assert.Equal(460, retained.Count)

    let changedManifest =
        mutateGzipJson evidence.ReceiverSourceManifestsGzipBytes (fun root ->
            let receiver = root["receivers"].AsArray()[0]
            let entry = receiver["manifest"].AsArray()[0]
            entry["sha"] <- String.replicate 40 "0")
    expectInnerError "manifest-content-sha256" evidence.ReceiverCensusBytes changedManifest evidence.ReceiverSourceBlobsGzipBytes

    let censusRoot = evidence.ReceiverCensusBytes.ToArray() |> parseNode
    let source = (censusRoot["sourceIdentities"].AsArray()[0]).AsObject()
    let receiverId, path = source["receiver"].GetValue<string>(), source["path"].GetValue<string>()
    let missingRelevant =
        mutateGzipJson evidence.ReceiverSourceManifestsGzipBytes (fun root ->
            let receiver = root["receivers"].AsArray() |> Seq.map _.AsObject() |> Seq.find (fun row -> row["id"].GetValue<string>() = receiverId)
            let rows = receiver["relevantSources"].AsArray()
            let index = rows |> Seq.findIndex (fun row -> row["path"].GetValue<string>() = path)
            rows.RemoveAt index)
    expectInnerError "manifest-relevant-source-join" evidence.ReceiverCensusBytes missingRelevant evidence.ReceiverSourceBlobsGzipBytes

[<Fact>]
let ``inner validator binds every external declaration and retained object`` () =
    let missingExternal =
        mutateGzipJson evidence.ReceiverSourceManifestsGzipBytes (fun root -> root["reviewedCallees"].AsArray().RemoveAt 0)
    expectInnerError "manifest-reviewed-callee-join" evidence.ReceiverCensusBytes missingExternal evidence.ReceiverSourceBlobsGzipBytes

    let missingBlob =
        mutateGzipJson evidence.ReceiverSourceBlobsGzipBytes (fun root -> root["blobs"].AsArray().RemoveAt 0)
    expectInnerError "blob-census-binding" evidence.ReceiverCensusBytes evidence.ReceiverSourceManifestsGzipBytes missingBlob

    let changedBlob =
        mutateGzipJson evidence.ReceiverSourceBlobsGzipBytes (fun root ->
            let blob = root["blobs"].AsArray()[0]
            blob["bytesBase64"] <- "AA==")
    expectInnerError "blob-bytes-sha256" evidence.ReceiverCensusBytes evidence.ReceiverSourceManifestsGzipBytes changedBlob

[<Fact>]
let ``inner validator refuses unsafe malformed duplicate overlimit and changed trees`` () =
    let unsafeCensus, unsafeManifests =
        mutateManifestAndClaims (fun root ->
            let receiver = root["receivers"].AsArray()[0]
            let entry = receiver["manifest"].AsArray()[0]
            entry["path"] <- "../unsafe")
    expectInnerError "manifest-unsafe-path" unsafeCensus unsafeManifests evidence.ReceiverSourceBlobsGzipBytes

    let treeCensus, treeManifests =
        mutateManifestAndClaims (fun root ->
            let receiver = root["receivers"].AsArray()[0]
            let entry = receiver["manifest"].AsArray()[0]
            entry["sha"] <- String.replicate 40 "0")
    expectInnerError "manifest-tree-sha1" treeCensus treeManifests evidence.ReceiverSourceBlobsGzipBytes

    let raw = unzip evidence.ReceiverSourceManifestsGzipBytes |> Encoding.UTF8.GetString
    let duplicate = raw.Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":") |> Encoding.UTF8.GetBytes |> zip
    expectInnerError "duplicate-json-member" evidence.ReceiverCensusBytes duplicate evidence.ReceiverSourceBlobsGzipBytes

    let malformed = zip [| 0xffuy |]
    expectInnerError "Unable to translate bytes" evidence.ReceiverCensusBytes malformed evidence.ReceiverSourceBlobsGzipBytes

    let overlimit = ReadOnlyMemory<byte>(Array.zeroCreate<byte> (1024 * 1024 + 1))
    expectInnerError "source-manifests-compressed-size" evidence.ReceiverCensusBytes overlimit evidence.ReceiverSourceBlobsGzipBytes

[<Fact>]
let ``omitted receiver and object evidence cannot derive a partial plan`` () =
    let truncate (bytes: ReadOnlyMemory<byte>) = ReadOnlyMemory<byte>(bytes.ToArray()[0..bytes.Length - 2])
    expectError (MigrationReceiverCopyPlan.derive { evidence with ReceiverCensusBytes = truncate evidence.ReceiverCensusBytes } run)
    expectError (MigrationReceiverCopyPlan.derive { evidence with ReceiverSourceManifestsGzipBytes = truncate evidence.ReceiverSourceManifestsGzipBytes } run)
    expectError (MigrationReceiverCopyPlan.derive { evidence with ReceiverSourceBlobsGzipBytes = truncate evidence.ReceiverSourceBlobsGzipBytes } run)

[<Fact>]
let ``unsafe and destination-colliding manifest mutations remain outside the accepted byte boundary`` () =
    let replacement = Text.Encoding.UTF8.GetBytes("../unsafe\u0000A/a\u0000a/A") |> ReadOnlyMemory<byte>
    expectError (MigrationReceiverCopyPlan.derive { evidence with ReceiverSourceManifestsGzipBytes = replacement } run)

[<Fact>]
let ``run nonce and verify identity drift refuse`` () =
    expectError (MigrationReceiverCopyPlan.derive evidence { run with RunNonce = "changed" })
    let observed = derive evidence run
    let otherCandidate = String.replicate 40 "c"
    let otherRun =
        { run with CandidateSha = otherCandidate
                   RunNonce = $"{run.WorkflowRunId}-{run.WorkflowRunAttempt}-{otherCandidate}" }
    expectError (MigrationReceiverCopyPlan.verify evidence otherRun observed)

[<Fact>]
let ``verify independently rejects fingerprint mapping and retained-object drift`` () =
    let observed = derive evidence run
    expectError (MigrationReceiverCopyPlan.verify evidence run { observed with ReceiverCopyFingerprint = String.replicate 64 "0" })
    let first = observed.ReceiverCopyMappings.Head
    let changed = { first with ReceiverCopyPlannedRef = first.ReceiverCopyPlannedRef + "-changed" }
    expectError (MigrationReceiverCopyPlan.verify evidence run { observed with ReceiverCopyMappings = changed :: observed.ReceiverCopyMappings.Tail })
    let retained = observed.ReceiverCopyRetainedBlobSha256BySha1 |> Map.toList |> List.tail |> Map.ofList
    expectError (MigrationReceiverCopyPlan.verify evidence run { observed with ReceiverCopyRetainedBlobSha256BySha1 = retained })

[<Fact>]
let ``receiver copy plan contract exposes exact retained and missing object identities`` () =
    let mappingFields =
        FSharp.Reflection.FSharpType.GetRecordFields(typeof<MigrationReceiverCopyMapping>)
        |> Array.map _.Name |> Set.ofArray
    let resultFields =
        FSharp.Reflection.FSharpType.GetRecordFields(typeof<MigrationReceiverCopyPlanResult>)
        |> Array.map _.Name |> Set.ofArray
    Assert.Contains("ReceiverCopyRequiredEntries", mappingFields)
    Assert.Contains("ReceiverCopyMissingBlobSha1s", mappingFields)
    Assert.Contains("ReceiverCopyRetainedBlobSha256BySha1", resultFields)
