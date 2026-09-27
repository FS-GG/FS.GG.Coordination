module FS.GG.Coordination.MigrationReviewDeliveryInspectBinderTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli
open FS.GG.Coordination.Qualification.Contracts

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private head = String.replicate 40 "a"
let private merge = String.replicate 40 "b"
let private repositoryOptions =
    { ApiBase=Uri "https://api.github.test/"; GraphQLUri=Uri "https://api.github.test/graphql"
      Token="controlled"; UserAgent="binder-test"; Owner="FS-GG"; Repository="copy"
      ExpectedRepositoryId=42L }
let private cohort =
    { Repositories=[ { Id=42L; NodeId="R_42"; FullName="FS-GG/copy"
                       SourceHead=String.replicate 40 "c"; TargetHead=head } ]
      Receivers=[ { Receiver="copy-receiver"; RepositoryId=42L
                    RefName="refs/heads/main"; ExpectedHead=head } ]
      ProjectOrganization="FS-GG"; ProjectNumber=1; ProjectNodeId="P_1"
      SourceRevision=String.replicate 40 "d"; Isolated=true }
let private adapterOptions =
    { Cohort=cohort; Repository=repositoryOptions
      Project={ GraphQLUri=repositoryOptions.GraphQLUri; Token="controlled"; UserAgent="binder-test"
                Organization="FS-GG"; ProjectNumber=1; ExpectedProjectNodeId="P_1" } }

let private read (path: string) (body: string) =
    let request =
        Rest { Method=Get; Uri=Uri(repositoryOptions.ApiBase, path)
               Headers=Map.ofList [ "accept", "application/vnd.github+json"; "authorization", "Bearer controlled" ]
               Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
        |> MigrationReviewDeliveryCaptureContract.captureRequest
    { Request=request; RequestSha256=MigrationReviewDeliveryCaptureContract.requestSha256 request
      StatusCode=200; ResponseHeaders=Map.empty; RawBody=body; RawSha256=sha body; NextRequestUri=None }

let private stream kind subject path body (records: MigrationReviewDeliveryRecord list) =
    { Kind=kind; Subject=subject; Reads=[ read path body ]; Records=records }

let private nativePass () =
    let repositoryBody = """{"id":42,"node_id":"R_42","full_name":"FS-GG/copy"}"""
    let pullBody =
        $"""[{{"number":7,"node_id":"PR_7","head":{{"sha":"{head}"}}}}]"""
    let streams =
        [ stream "reviews" "7" "repos/FS-GG/copy/pulls/7/reviews?per_page=100"
              """[{"id":101}]""" [ MigrationReviewDeliveryRecord.Review(7, 101L) ]
          stream "inline-comments" "7" "repos/FS-GG/copy/pulls/7/comments?per_page=100"
              """[{"id":201}]""" [ MigrationReviewDeliveryRecord.InlineComment(7, 201L) ]
          stream "check-runs" "7" (MigrationReviewDeliveryCaptureContract.checkRunsPath "repos/FS-GG/copy" head)
              ($"""{{"total_count":1,"check_runs":[{{"id":301,"name":"build","status":"completed","conclusion":"success","head_sha":"{head}"}}]}}""")
              [ MigrationReviewDeliveryRecord.CheckRun(7, head, 301L, "build", "completed", Some "success") ]
          stream "statuses" "7" (MigrationReviewDeliveryCaptureContract.statusesPath "repos/FS-GG/copy" head)
              """[{"id":401,"context":"build","state":"success"}]"""
              [ MigrationReviewDeliveryRecord.CommitStatus(7, head, 401L, "build", "success") ]
          stream "pull-delivery" "7" "repos/FS-GG/copy/pulls/7"
              ($"""{{"number":7,"head":{{"sha":"{head}"}},"merge_commit_sha":"{merge}"}}""") [ MigrationReviewDeliveryRecord.PullDelivery(7, Some merge) ]
          stream "merge-object" "7" ($"repos/FS-GG/copy/commits/{merge}")
              ($"""{{"sha":"{merge}"}}""") [ MigrationReviewDeliveryRecord.MergeObject(7, merge) ]
          stream "tags" "repository" "repos/FS-GG/copy/tags?per_page=100"
              ($"""[{{"name":"v1","commit":{{"sha":"{merge}"}}}}]""") [ MigrationReviewDeliveryRecord.Tag("v1", merge) ]
          stream "releases" "repository" "repos/FS-GG/copy/releases?per_page=100"
              """[{"id":501,"tag_name":"v1","draft":false}]""" [ MigrationReviewDeliveryRecord.Release(501L, "v1", false) ] ]
    let pass =
        { Repository={ RepositoryId=42L; NodeId="R_42"; FullName="FS-GG/copy"
                       Read=read "repos/FS-GG/copy" repositoryBody }
          PullRequestCensus=[ read "repos/FS-GG/copy/pulls?state=all&per_page=100" pullBody ]
          PullRequests=[ { Number=7; NodeId="PR_7"; HeadSha=head } ]
          Streams=streams; Fingerprint="" }
    { pass with Fingerprint=MigrationReviewDeliveryCaptureContract.nativeFingerprint pass }

let private journalPass () =
    let repositoryBody = """{"id":42,"node_id":"R_42","full_name":"FS-GG/copy"}"""
    let reviewRef = "refs/heads/fsgg/v2/journal/review/aa"
    let operationRef = "refs/heads/fsgg/v2/journal/operation/bb"
    let reviewCommit = String.replicate 40 "1"
    let operationCommit = String.replicate 40 "2"
    let tree = String.replicate 40 "3"
    let census (prefix: string) refName commit =
        let body = $"""[{{"ref":"{refName}","object":{{"sha":"{commit}"}}}}]"""
        let prefixTail = prefix.Substring("refs/".Length)
        { Prefix=prefix; Reads=[ read ($"repos/FS-GG/copy/git/matching-refs/{prefixTail}") body ]
          Refs=[ { RefName=refName; HeadSha=commit } ] }
    let history refName commit schema kind operation mergeCommit =
        let mergeProperty = mergeCommit |> Option.map (sprintf ",\"mergeCommit\":\"%s\"") |> Option.defaultValue ""
        let body = $"""{{"schema":"{schema}","kind":"{kind}","subject":"FS-GG/copy#7","operationId":"{operation}","generation":1{mergeProperty}}}"""
        { RefName=refName; CommitSha=commit; ParentSha=None; TreeSha=tree
          HeadPath="head.json"; EventPath="events/1.json"; Reads=[ read ($"repos/FS-GG/copy/git/commits/{commit}") body ]
          Record={ Schema=schema; Kind=kind; Subject="FS-GG/copy#7"; OperationId=operation
                   Generation=1L; MergeCommit=mergeCommit; ProtectedRunId=None
                   ProtectedRunCommit=None; ProtectedRunConclusion=None } }
    let pass =
        { Repository={ RepositoryId=42L; NodeId="R_42"; FullName="FS-GG/copy"
                       Read=read "repos/FS-GG/copy" repositoryBody }
          Namespaces=
            [ census MigrationReviewDeliveryCaptureContract.journalRefPrefixes.[0] reviewRef reviewCommit
              census MigrationReviewDeliveryCaptureContract.journalRefPrefixes.[1] operationRef operationCommit ]
          Histories=
            [ history reviewRef reviewCommit "fsgg.coordination.review-authority/1" "review" "review-7" None
              history operationRef operationCommit "fsgg.coordination.delivery-authority/1" "delivery" "delivery-7" (Some merge) ]
          Fingerprint="" }
    { pass with Fingerprint=MigrationReviewDeliveryCaptureContract.journalFingerprint pass }

let private captures () : MigrationReviewDeliveryNativeTwoPass * MigrationJournalTwoPass =
    let native = nativePass ()
    let journals = journalPass ()
    ({ First=native; Second=native }: MigrationReviewDeliveryNativeTwoPass),
    ({ First=journals; Second=journals }: MigrationJournalTwoPass)

[<Fact>]
let ``canonical binder closes nonempty native release and discovered journal correspondence`` () =
    let (native: MigrationReviewDeliveryNativeTwoPass), (journals: MigrationJournalTwoPass) = captures ()
    match MigrationReviewDeliveryInspectBinder.bind cohort repositoryOptions native journals with
    | Error reason -> failwithf "Binder refused: %s" reason
    | Ok complete ->
        Assert.Single(complete.First.Correspondence) |> ignore
        match MigrationReviewDeliveryInspectBinder.authority cohort 1 complete with
        | Error reason -> failwithf "Authority refused: %s" reason
        | Ok proof ->
            Assert.Equal("review-delivery-release-records", proof.Read.Authority)
            Assert.True(proof.ScopeVerified && proof.SubjectsParsedFromRaw)
            Assert.True(proof.Read.ItemCount >= 10)
            Assert.Equal(None, proof.Pages |> List.last |> _.NextRequestIdentitySha256)
        match MigrationInspectProviderAdapter.bindReviewDeliveryRecords adapterOptions native journals with
        | Error reason -> failwithf "Adapter binder refused: %s" reason
        | Ok (first, second) ->
            Assert.Equal("review-delivery-release-records", first.Read.Authority)
            Assert.Equal(first.Read.HighWaterMark, second.Read.HighWaterMark)

[<Fact>]
let ``canonical binder refuses status URL raw drift and discovered ref without history`` () =
    let (native: MigrationReviewDeliveryNativeTwoPass), (journals: MigrationJournalTwoPass) = captures ()
    let first: MigrationReviewDeliveryNativePass = native.First
    let status: MigrationReviewDeliveryNativeStream =
        first.Streams |> List.find (fun value -> value.Kind = "statuses")
    let wrongRead = read (MigrationReviewDeliveryCaptureContract.statusesPath "repos/FS-GG/copy" merge) status.Reads.Head.RawBody
    let wrongStreams = first.Streams |> List.map (fun value -> if value.Kind = "statuses" then { value with Reads=[ wrongRead ] } else value)
    let wrongPass = { first with Streams=wrongStreams; Fingerprint="" }
    let wrongPass = { wrongPass with Fingerprint=MigrationReviewDeliveryCaptureContract.nativeFingerprint wrongPass }
    Assert.Equal(Error "review-delivery-native-raw-typed",
                 MigrationReviewDeliveryInspectBinder.bind cohort repositoryOptions
                     { First=wrongPass; Second=wrongPass } journals)

    let journal: MigrationJournalPass = journals.First
    let operation: MigrationJournalNamespaceCensus = journal.Namespaces.[1]
    let extra = { RefName="refs/heads/fsgg/v2/journal/operation/cc"; HeadSha=String.replicate 40 "4" }
    let changed = { journal with Namespaces=[ journal.Namespaces.[0]; { operation with Refs=operation.Refs @ [ extra ] } ]; Fingerprint="" }
    let changed = { changed with Fingerprint=MigrationReviewDeliveryCaptureContract.journalFingerprint changed }
    Assert.Equal(Error "review-delivery-journal-raw-typed",
                 MigrationReviewDeliveryInspectBinder.bind cohort repositoryOptions native
                     { First=changed; Second=changed })

[<Fact>]
let ``canonical binder refuses typed raw journal drift and canonical source stays unavailable`` () =
    let (native: MigrationReviewDeliveryNativeTwoPass), (journals: MigrationJournalTwoPass) = captures ()
    let journal: MigrationJournalPass = journals.First
    let changedEntry =
        { journal.Histories.[1] with Record={ journal.Histories.[1].Record with MergeCommit=Some head } }
    let changed = { journal with Histories=[ journal.Histories.[0]; changedEntry ]; Fingerprint="" }
    let changed = { changed with Fingerprint=MigrationReviewDeliveryCaptureContract.journalFingerprint changed }
    Assert.Equal(Error "review-delivery-journal-raw-typed",
                 MigrationReviewDeliveryInspectBinder.bind cohort repositoryOptions native
                     { First=changed; Second=changed })

    let noTransport =
        { new IMigrationGitHubReadTransport with member _.Send _ = failwith "provider dispatch was not expected" }
    let source = MigrationInspectProviderAdapter(adapterOptions, noTransport) :> IGitHubMigrationInspectSource
    Assert.Equal(Error "authority-adapter-unavailable:review-delivery-release-records",
                 source.ReadAuthority(1, "review-delivery-release-records"))
