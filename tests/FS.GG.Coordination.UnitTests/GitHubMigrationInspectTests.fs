module FS.GG.Coordination.GitHubMigrationInspectTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private digest (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private time value = DateTimeOffset.Parse value
let private cohort =
    { Repositories=[ { Id=42L; NodeId="R_disposable_copy"
                       FullName="FS-GG/disposable-copy"
                       SourceHead=String.replicate 40 "e"
                       TargetHead=String.replicate 40 "f" } ]
      Receivers=[ { Receiver="disposable-receiver"; RepositoryId=42L
                    RefName="refs/heads/main"; ExpectedHead=String.replicate 40 "f" } ]
      ProjectOrganization="FS-GG"
      ProjectNumber=99
      ProjectNodeId="PVT_disposable_copy"
      SourceRevision=String.replicate 40 "a"
      Isolated=true }

let private request =
    { Cohort=cohort
      RoadmapRevision=String.replicate 40 "b"
      RoadmapSha256=String.replicate 64 "c"
      UnitContractSha256=String.replicate 64 "d"
      ReceiptDigests=[ String.replicate 64 "1"; String.replicate 64 "2" ]
      First={ StartedAt=time "2026-09-25T10:00:00Z"; CompletedAt=time "2026-09-25T10:02:00Z" }
      Second={ StartedAt=time "2026-09-25T10:03:00Z"; CompletedAt=time "2026-09-25T10:05:00Z" } }

let private observation pass authority body =
    let subjects = []
    { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
      ScopeVerified=true; SubjectsParsedFromRaw=true
      Read={ Authority=authority
             ObservedAt=if pass = 1 then time "2026-09-25T10:01:00Z" else time "2026-09-25T10:04:00Z"
             PageCount=1; ItemCount=0; Terminal=true; NextCursor=None
             HighWaterMark=authority + ":watermark"; Subjects=subjects }
      Pages=[ { RequestedUri="https://api.github.test/" + authority
                RequestIdentitySha256=digest ("https://api.github.test/" + authority)
                RawBody=body; PayloadSha256=digest body; NextRequestIdentitySha256=None
                Subjects=subjects } ] }

type private FakeSource(read: int -> string -> Result<GitHubMigrationInspectAuthority, string>) =
    let calls = ResizeArray<int * string>()
    member _.Calls = calls |> Seq.toList
    interface IGitHubMigrationInspectSource with
        member _.ReadAuthority(pass, authority) =
            calls.Add(pass, authority)
            read pass authority

type private MissingManifest() =
    let mutable calls = 0
    member _.Calls = calls
    interface IGitHubMigrationInspectStages with
        member _.BuildManifest _ =
            calls <- calls + 1
            Error "no-copy-specific-manifest-adapter"
        member _.BuildTransforms _ = failwith "transform stage must not run"
        member _.BuildOperations _ = failwith "operation stage must not run"

[<Fact>]
let ``a live subject masquerading as an isolated copy refuses before reads`` () =
    let source = FakeSource(fun pass authority -> Ok(observation pass authority "{}"))
    let stages = MissingManifest()
    let live = { request with Cohort={ cohort with Isolated=false } }
    Assert.Equal(Error GitHubMigrationInspectFailure.InvalidCohort,
                 GitHubMigrationInspect.inspect live source stages)
    Assert.Empty(source.Calls)
    Assert.Equal(0, stages.Calls)

[<Fact>]
let ``cohort digest changes with source revision and repository heads`` () =
    let original = GitHubMigrationInspect.cohortSha256 cohort
    let changedRevision = { cohort with SourceRevision=String.replicate 40 "9" }
    let changedHead =
        { cohort with Repositories=[ { cohort.Repositories.Head with TargetHead=String.replicate 40 "8" } ] }
    Assert.NotEqual(original, GitHubMigrationInspect.cohortSha256 changedRevision)
    Assert.NotEqual(original, GitHubMigrationInspect.cohortSha256 changedHead)
    let changedReceiver =
        { cohort with Receivers=[ { cohort.Receivers.Head with RefName="refs/heads/other" } ] }
    Assert.NotEqual(original, GitHubMigrationInspect.cohortSha256 changedReceiver)

[<Fact>]
let ``receiver cohort rejects ambiguous or unsafe branch refs before discovery`` () =
    for invalid in [ "refs/tags/v1"; "refs/heads/a?b"; "refs/heads/a#b";
                     "refs/heads/a%b"; "refs/heads/a.lock"; "refs/heads/a..b"; "refs/heads/a.";
                     "refs/heads/a b"; "refs/heads/a^b"; "refs/heads/a:b";
                     "refs/heads/a*b"; "refs/heads/a[b"; "refs/heads/a\\b" ] do
        let changed = { cohort with Receivers=[ { cohort.Receivers.Head with RefName=invalid } ] }
        Assert.False(GitHubMigrationInspect.validCohort changed)
    Assert.False(GitHubMigrationInspect.validCohort { cohort with Receivers=[] })
    Assert.True(GitHubMigrationInspect.validCohort
        { cohort with Receivers=[ { cohort.Receivers.Head with ExpectedHead=String.replicate 40 "1" } ] })
    Assert.True(GitHubMigrationInspect.validCohort cohort)

[<Fact>]
let ``one unavailable authority refuses without manufacturing a nine-authority pass`` () =
    let missing = "claim-and-event-streams"
    let source =
        FakeSource(fun pass authority ->
            if authority = missing then Error "protected-claim-journal-unavailable"
            else Ok(observation pass authority "{}"))
    let stages = MissingManifest()
    match GitHubMigrationInspect.inspect request source stages with
    | Error(GitHubMigrationInspectFailure.SourceRefused(1, authority, reason)) ->
        Assert.Equal(missing, authority)
        Assert.Equal("protected-claim-journal-unavailable", reason)
    | other -> failwithf "Expected unavailable authority refusal; got %A" other
    Assert.Equal(0, stages.Calls)

[<Fact>]
let ``raw page digest and terminal page proof are required before stage building`` () =
    let firstAuthority = GitHubCompleteDiscoveryQualification.expectedAuthorities.Head
    let badDigest =
        FakeSource(fun pass authority ->
            let value = observation pass authority "{}"
            if authority = firstAuthority then
                Ok { value with Pages=[ { value.Pages.Head with PayloadSha256=String.replicate 64 "0" } ] }
            else Ok value)
    let stages = MissingManifest()
    Assert.Equal(
        Error(GitHubMigrationInspectFailure.InvalidProviderEvidence(1, firstAuthority, "raw-page")),
        GitHubMigrationInspect.inspect request badDigest stages)
    let missingPage =
        FakeSource(fun pass authority ->
            let value = observation pass authority "{}"
            if authority = firstAuthority then Ok { value with Pages=[] }
            else Ok value)
    Assert.Equal(
        Error(GitHubMigrationInspectFailure.InvalidProviderEvidence(1, firstAuthority, "incomplete-pages")),
        GitHubMigrationInspect.inspect request missingPage stages)
    Assert.Equal(0, stages.Calls)

[<Fact>]
let ``an unqualified foreign-scope or unparsed-subject adapter refuses`` () =
    let firstAuthority = GitHubCompleteDiscoveryQualification.expectedAuthorities.Head
    for unqualified in
        [ fun value -> { value with ScopeVerified=false }
          fun value -> { value with SubjectsParsedFromRaw=false } ] do
        let source =
            FakeSource(fun pass authority ->
                let value = observation pass authority "{}"
                if authority = firstAuthority then Ok(unqualified value)
                else Ok value)
        let stages = MissingManifest()
        Assert.Equal(
            Error(GitHubMigrationInspectFailure.InvalidProviderEvidence(1, firstAuthority, "unqualified-adapter")),
            GitHubMigrationInspect.inspect request source stages)
        Assert.Equal(0, stages.Calls)

[<Fact>]
let ``changed provider page across complete passes refuses before a manifest`` () =
    let source =
        FakeSource(fun pass authority ->
            Ok(observation pass authority (if pass = 1 then "{}" else "{ }")))
    let stages = MissingManifest()
    let firstAuthority = GitHubCompleteDiscoveryQualification.expectedAuthorities.Head
    Assert.Equal(
        Error(GitHubMigrationInspectFailure.ChangedProviderPages firstAuthority),
        GitHubMigrationInspect.inspect request source stages)
    Assert.Equal(18, source.Calls.Length)
    Assert.Equal(0, stages.Calls)

[<Fact>]
let ``complete quiescent provider evidence still refuses absent manifest source`` () =
    let source = FakeSource(fun pass authority -> Ok(observation pass authority "{}"))
    let stages = MissingManifest()
    Assert.Equal(
        Error(GitHubMigrationInspectFailure.MissingStageInput("manifest", "no-copy-specific-manifest-adapter")),
        GitHubMigrationInspect.inspect request source stages)
    Assert.Equal(18, source.Calls.Length)
    Assert.Equal(1, stages.Calls)
