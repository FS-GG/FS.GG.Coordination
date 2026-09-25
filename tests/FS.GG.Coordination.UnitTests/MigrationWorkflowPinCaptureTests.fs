module FS.GG.Coordination.MigrationWorkflowPinCaptureTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

type private Fixture =
    { Options: MigrationGitHubReadOptions
      Cohort: GitHubMigrationCopyCohort
      Bytes: byte array
      BlobSha: string
      BlobUri: string
      Tree: string
      ReceiverResponses: TransportOutcome list
      PinResponses: TransportOutcome list
      Declaration: MigrationWorkflowPinDeclaration }

let private fixture () =
    let head = String.replicate 40 "a"
    let treeSha = String.replicate 40 "b"
    let path = ".github/workflows/check.yml"
    let bytes = Encoding.UTF8.GetBytes "name: controlled\n"
    let bytesSha = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let header = Encoding.ASCII.GetBytes($"blob {bytes.LongLength}\u0000")
    let blobSha = Array.append header bytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let blobUri = $"https://api.github.test/repos/FS-GG/copy/git/blobs/{blobSha}"
    let options =
        { ApiBase=Uri "https://api.github.test/"
          GraphQLUri=Uri "https://api.github.test/graphql"
          Token="test-token"; UserAgent="pin-capture-test"
          Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }
    let cohort: GitHubMigrationCopyCohort =
        { Repositories=[ { Id=42L; NodeId="REPO_42"; FullName="FS-GG/copy"
                           SourceHead=String.replicate 40 "d"; TargetHead=head } ]
          Receivers=[ { Receiver="receiver-a"; RepositoryId=42L; RefName="refs/heads/main"
                        ExpectedHead=head } ]
          ProjectOrganization="FS-GG"; ProjectNumber=1; ProjectNodeId="PROJECT_1"
          SourceRevision=String.replicate 40 "e"; Isolated=true }
    let reply body =
        Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let identity = reply """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
    let branch = reply $"""{{"ref":"refs/heads/main","object":{{"type":"commit","sha":"{head}"}}}}"""
    let commit = reply $"""{{"sha":"{head}","tree":{{"sha":"{treeSha}"}}}}"""
    let tree =
        $"""{{"sha":"{treeSha}","truncated":false,"tree":[{{"path":"{path}","mode":"100644","type":"blob","sha":"{blobSha}","size":{bytes.Length}}}]}}"""
    let blob =
        reply $"""{{"sha":"{blobSha}","url":"{blobUri}","encoding":"base64","content":"{Convert.ToBase64String bytes}","size":{bytes.Length}}}"""
    let declaration =
        { Kind=MigrationWorkflowPinKind.Workflow; Receiver="receiver-a"
          RepositoryId=42L; RepositoryNodeId="REPO_42"; RepositoryFullName="FS-GG/copy"
          RefName="refs/heads/main"; ExpectedHead=head; Path=path; ExpectedMode="100644"
          ExpectedBlobSha=blobSha; ExpectedBytesSha256=bytesSha }
    { Options=options; Cohort=cohort; Bytes=bytes; BlobSha=blobSha; BlobUri=blobUri
      Tree=tree; ReceiverResponses=[ identity; branch; commit; reply tree; branch ]
      PinResponses=[ identity; branch; commit; reply tree; branch; blob; branch ]
      Declaration=declaration }

let private receiverProof fixture =
    let transport = FakeTransport(fixture.ReceiverResponses @ fixture.ReceiverResponses)
    match MigrationReceiverCapture.captureTwoPass fixture.Cohort fixture.Options transport with
    | Ok proof -> proof
    | Error failure -> failwithf "receiver capture refused: %s" failure

[<Fact>]
let ``declared workflow pin captures exact isolated copy bytes in two read-only passes`` () =
    let f = fixture ()
    let transport = FakeTransport(f.PinResponses @ f.PinResponses)
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ f.Declaration ] (receiverProof f) f.Options transport with
    | Error failure -> failwithf "pin capture refused: %s" failure
    | Ok proof ->
        Assert.Equal(GitHubMigrationInspect.cohortSha256 f.Cohort, proof.CohortSha256)
        Assert.Equal(2, proof.First.Length + proof.Second.Length)
        for pin in proof.First @ proof.Second do
            Assert.True(Array.forall2 (=) f.Bytes pin.Bytes)
            Assert.Equal(f.Declaration.ExpectedBytesSha256, pin.BytesSha256)
            Assert.Equal(f.BlobSha, pin.GitBlobSha)
            Assert.Equal(f.BlobUri, pin.RequestUri)
            Assert.NotEmpty(pin.RawBody)
            let rawSha = SHA256.HashData(Encoding.UTF8.GetBytes pin.RawBody)
                         |> Convert.ToHexString |> _.ToLowerInvariant()
            Assert.Equal(rawSha, pin.RawBodySha256)
        Assert.Equal(14, transport.Requests.Length)
        for request in transport.Requests do
            match request with
            | Rest value -> Assert.Equal(Get, value.Method); Assert.True(value.Body.IsNone)
            | _ -> failwith "pin capture emitted GraphQL"

[<Fact>]
let ``pin capture refuses missing duplicate foreign or wrong planned declarations`` () =
    let f = fixture ()
    let proof = receiverProof f
    let cases =
        [ [], "invalid:pin-declarations", true
          [ f.Declaration; f.Declaration ], "invalid:pin-declarations", true
          [ { f.Declaration with RepositoryNodeId="REPO_OTHER" } ], "foreign:pin-declaration", true
          [ { f.Declaration with Path=".github/workflows/nested/check.yml" } ], "invalid:pin-declarations", true
          [ { f.Declaration with Kind=MigrationWorkflowPinKind.PackageToolPin } ], "invalid:pin-declarations", true
          [ { f.Declaration with ExpectedBytesSha256=String.replicate 64 "f" } ],
              "changed:pin-content:receiver-a:.github/workflows/check.yml", false ]
    for declarations, expected, noRead in cases do
        let transport = FakeTransport(f.PinResponses @ f.PinResponses)
        Assert.Equal(Error expected,
                     MigrationWorkflowPinCapture.captureTwoPass f.Cohort declarations proof f.Options transport)
        if noRead then Assert.Empty(transport.Requests)

[<Fact>]
let ``pin capture refuses omitted and extra workflow or package paths`` () =
    let f = fixture ()
    let proof = receiverProof f
    let omitted = { f.Declaration with Path=".github/workflows/missing.yml" }
    let transport = FakeTransport f.PinResponses
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ omitted ] proof f.Options transport with
    | Error failure -> Assert.Contains("receiver-pin-path-census", failure)
    | Ok _ -> failwith "omitted workflow pin passed"
    Assert.Equal(5, transport.Requests.Length)
    let extraTree =
        $"""{{"sha":"{String.replicate 40 "b"}","truncated":false,"tree":[{{"path":"{f.Declaration.Path}","mode":"100644","type":"blob","sha":"{f.BlobSha}","size":{f.Bytes.Length}}},{{"path":"package-lock.json","mode":"100644","type":"blob","sha":"{f.BlobSha}","size":{f.Bytes.Length}}}]}}"""
    let reply body =
        Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let changedPass =
        f.PinResponses |> List.mapi (fun index item -> if index = 3 then reply extraTree else item)
    let changed = FakeTransport changedPass
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ f.Declaration ] proof f.Options changed with
    | Error failure -> Assert.Contains("receiver-pin-path-census", failure)
    | Ok _ -> failwith "extra package pin passed"

[<Fact>]
let ``declared package tool pin joins workflow pin in complete tree census`` () =
    let f = fixture ()
    let packagePath = "package-lock.json"
    let completeTree =
        $"""{{"sha":"{String.replicate 40 "b"}","truncated":false,"tree":[{{"path":"{f.Declaration.Path}","mode":"100644","type":"blob","sha":"{f.BlobSha}","size":{f.Bytes.Length}}},{{"path":"{packagePath}","mode":"100644","type":"blob","sha":"{f.BlobSha}","size":{f.Bytes.Length}}}]}}"""
    let treeReply =
        Response { StatusCode=200; Headers=Map.empty; Body=completeTree; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let receiverPass =
        f.ReceiverResponses |> List.mapi (fun index item -> if index = 3 then treeReply else item)
    let receiverTransport = FakeTransport(receiverPass @ receiverPass)
    let receivers =
        match MigrationReceiverCapture.captureTwoPass f.Cohort f.Options receiverTransport with
        | Ok value -> value
        | Error failure -> failwithf "complete receiver refused: %s" failure
    let pinPass =
        let withTree = f.PinResponses |> List.mapi (fun index item -> if index = 3 then treeReply else item)
        List.take 6 withTree @ [ List.item 5 withTree; List.item 6 withTree ]
    let package =
        { f.Declaration with Kind=MigrationWorkflowPinKind.PackageToolPin; Path=packagePath }
    let transport = FakeTransport(pinPass @ pinPass)
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ f.Declaration; package ] receivers f.Options transport with
    | Error failure -> failwithf "complete pin census refused: %s" failure
    | Ok proof ->
        Assert.Equal(2, proof.First.Length)
        Assert.True([ f.Declaration.Path; packagePath ] = (proof.First |> List.map _.Declaration.Path))
        Assert.Equal(16, transport.Requests.Length)

[<Fact>]
let ``pin capture refuses changed tree or terminal ref between read passes`` () =
    let f = fixture ()
    let proof = receiverProof f
    let reply body =
        Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let changedTree = f.Tree.Replace("100644", "100755")
    let treeDrift =
        f.PinResponses |> List.mapi (fun index item -> if index = 3 then reply changedTree else item)
    let changed = FakeTransport(f.PinResponses @ treeDrift)
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ f.Declaration ] proof f.Options changed with
    | Error failure -> Assert.Contains("changed:pin-receiver-snapshot", failure)
    | Ok _ -> failwith "changed second-pass tree passed"
    let foreignHead = String.replicate 40 "f"
    let foreignRef = reply $"""{{"ref":"refs/heads/main","object":{{"type":"commit","sha":"{foreignHead}"}}}}"""
    let refDrift =
        f.PinResponses |> List.mapi (fun index item -> if index = 6 then foreignRef else item)
    let changedRef = FakeTransport(f.PinResponses @ refDrift)
    match MigrationWorkflowPinCapture.captureTwoPass f.Cohort [ f.Declaration ] proof f.Options changedRef with
    | Error failure -> Assert.Contains("IdentityDrift", failure)
    | Ok _ -> failwith "changed second-pass terminal ref passed"
