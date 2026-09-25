module FS.GG.Coordination.MigrationClaimEventCaptureTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private stamp = DateTimeOffset.Parse "2026-09-24T10:00:00Z"
let private raw = "{}"
let private page path =
    { RequestedUri="https://api.github.test/" + path; PayloadSha256=sha raw; NextUri=None }

let private claimBody = "<!-- fsgg:claim worker=worker-1 lease=30 renewed=1 -->"
let private comment body =
    { DatabaseId=201L; NodeId="IC_201"; SubjectNumber=1; ActorLogin="worker-1"
      CreatedAt=stamp; UpdatedAt=stamp; Body=body; PayloadJson=raw; PayloadSha256=sha raw }

let private native body =
    let input : MigrationNativeActivityInput =
        { Issues={ RepositoryId=42L; PageCount=1; Terminal=true; Pages=[ page "issues" ]; PullRequestCount=0
                   Issues=[ { Number=1; DatabaseId=101L; NodeId="I_1"; State="open"; UpdatedAt=stamp
                              PayloadJson=raw; PayloadSha256=sha raw } ] }
          PullRequests={ RepositoryId=42L; PageCount=1; Terminal=true; Pages=[ page "pulls" ]; PullRequests=[] }
          IssueComments=[ { RepositoryId=42L; SubjectNumber=1; SubjectNodeId="I_1"; PageCount=1
                            Terminal=true; Pages=[ page "comments" ]; Comments=[ comment body ] } ]
          IssueEvents=[ { RepositoryId=42L; SubjectNumber=1; SubjectNodeId="I_1"; PageCount=1
                          Terminal=true; Pages=[ page "events" ]; Events=[] } ]
          PullRequestComments=[]; PullRequestReviews=[]; PullRequestInlineComments=[] }
    { Input=input; Snapshot=MigrationNativeActivity.reconcile input |> Result.defaultWith (fun e -> failwithf "%A" e) }

let private target = { Kind=JournalKind.Claim; AggregateId="claim:FS-GG/copy#1" }
let private link =
    { CommentNodeId="IC_201"; Target=target; Generation=1L; OperationId="claim-operation-1" }

let private declaration =
    { RepositoryId=42L; RepositoryFullName="FS-GG/copy"; InventoryComplete=true
      Targets=[ target ]; Links=[ link ] }

let private journal body subjectNodeId commentNodeId =
    let address = ShardedJournalAdapter.address target.Kind target.AggregateId |> Result.defaultWith (fun e -> failwithf "%A" e)
    let eventJson =
        $"""{{"legacyBodySha256":"{sha body}","legacyCommentNodeId":"{commentNodeId}","legacyMarkerKind":"claim","operationId":"claim-operation-1","repositoryId":42,"subjectNodeId":"{subjectNodeId}","subjectNumber":1}}"""
    let eventBytes = ShardedJournalAdapter.canonicalJson eventJson |> Result.defaultWith failwith
    let eventDigest = ShardedJournalAdapter.sha256 eventBytes
    let initialHead =
        { SchemaVersion=1; Address=address; Generation=1L; EventDigest=eventDigest
          SnapshotDigest=None; Terminal=false; PriorHeadDigest=None; HeadDigest=String.replicate 64 "0" }
    let headBytes = ShardedJournalAdapter.journalHeadBytes initialHead
    let head = { initialHead with HeadDigest=ShardedJournalAdapter.sha256 headBytes }
    let commit =
        { CommitOid=String.replicate 40 "a"; ParentOid=None; TreeOid=String.replicate 40 "b"
          OperationId="claim-operation-1"; Head=head; HeadBytes=headBytes
          Event={ Bytes=eventBytes; Digest=eventDigest }; Checkpoint=None }
    JournalComplete(String.replicate 40 "c", [ commit ])

let private goodJournal = journal claimBody "I_1" "IC_201"

let private response body =
    Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
               RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }

type private FakeNativeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

type private NoJournalRead() =
    interface IMigrationClaimJournalRead with
        member _.Read _ = failwith "An empty inventory must not read a journal"

let private readOptions =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="controlled-test-token"; UserAgent="migration-capture-test"
      Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }

let private requireOk = function
    | Ok result -> result
    | Error error -> failwithf "Unexpected refusal %A" error

[<Fact>]
let ``legacy marker binds native comment and exact protected claim event`` () =
    let result = MigrationClaimEventCapture.reconcile declaration (native claimBody) [ target, goodJournal ] |> requireOk
    Assert.Equal(42L, result.RepositoryId)
    Assert.Single(result.Markers) |> ignore
    Assert.Equal(1, result.JournalEventCount)
    Assert.Equal(64, result.NormalizedSha256.Length)

[<Fact>]
let ``missing inventory and omitted marker correspondence refuse`` () =
    Assert.Equal(Error MissingInventory,
        MigrationClaimEventCapture.reconcile { declaration with InventoryComplete=false } (native claimBody) [ target, goodJournal ])
    Assert.Equal(Error(MissingCorrespondence "legacy-markers"),
        MigrationClaimEventCapture.reconcile { declaration with Links=[] } (native claimBody) [ target, goodJournal ])

[<Fact>]
let ``foreign subject and comment digest refuse`` () =
    Assert.Equal(Error(ForeignCorrespondence "event-payload"),
        MigrationClaimEventCapture.reconcile declaration (native claimBody)
            [ target, journal claimBody "I_foreign" "IC_201" ])
    Assert.Equal(Error(ForeignCorrespondence "event-payload"),
        MigrationClaimEventCapture.reconcile declaration (native claimBody)
            [ target, journal "changed marker" "I_1" "IC_201" ])

[<Fact>]
let ``missing journal generation and unreadable history refuse`` () =
    Assert.Equal(Error(ForeignCorrespondence "journal-generation"),
        MigrationClaimEventCapture.reconcile
            { declaration with Links=[ { link with Generation=2L } ] }
            (native claimBody) [ target, goodJournal ])
    Assert.Equal(Error(JournalInvalid(UnauthorizedJournal "forbidden")),
        MigrationClaimEventCapture.reconcile declaration (native claimBody)
            [ target, JournalUnauthorized "forbidden" ])

[<Fact>]
let ``malformed claim marker and foreign claim aggregate refuse`` () =
    match MigrationClaimEventCapture.reconcile declaration
              (native "<!-- fsgg:claim worker=worker-1 -->") [ target, goodJournal ] with
    | Error(InvalidMarker "IC_201") -> ()
    | result -> failwithf "Expected invalid marker, got %A" result
    let foreign = { Kind=JournalKind.Claim; AggregateId="claim:FS-GG/foreign#1" }
    match MigrationClaimEventCapture.reconcile
              { declaration with Targets=[ foreign ]; Links=[ { link with Target=foreign } ] }
              (native claimBody) [ foreign, JournalIncomplete "no history" ] with
    | Error(JournalInvalid(IncompleteJournal "no history")) -> ()
    | result -> failwithf "Expected incomplete journal, got %A" result

[<Fact>]
let ``changed native census and extra legacy journal event refuse`` () =
    let captured = native claimBody
    let changed = { captured with Snapshot={ captured.Snapshot with NormalizedSha256=sha "altered" } }
    match MigrationClaimEventCapture.reconcile declaration changed [ target, goodJournal ] with
    | Error(NativeFailure(MigrationReadFailure.SnapshotMismatch "native-seal")) -> ()
    | result -> failwithf "Expected native seal refusal, got %A" result
    Assert.Equal(Error(MissingCorrespondence "journal-events"),
        MigrationClaimEventCapture.reconcile declaration (native claimBody)
            [ target, journal claimBody "I_1" "IC_extra" ])

[<Fact>]
let ``two complete native passes refuse changed raw page identity`` () =
    let oneCapture issuePage =
        [ response """{"id":42,"full_name":"FS-GG/copy"}"""; response issuePage
          response """{"id":42,"full_name":"FS-GG/copy"}"""; response "[]"
          response """{"id":42,"full_name":"FS-GG/copy"}"""; response issuePage
          response """{"id":42,"full_name":"FS-GG/copy"}"""; response "[]" ]
    let transport = FakeNativeTransport(oneCapture "[]" @ oneCapture "[ ]")
    let empty = { declaration with Targets=[]; Links=[] }
    Assert.Equal(Error ChangedPass,
        MigrationClaimEventCapture.captureStable readOptions transport empty (NoJournalRead()))
    Assert.Equal(16, transport.Requests.Length)
    Assert.All(transport.Requests, fun request ->
        match request with
        | Rest rest -> Assert.Equal(Get, rest.Method)
        | GraphQL _ -> failwith "Unexpected GraphQL request")
