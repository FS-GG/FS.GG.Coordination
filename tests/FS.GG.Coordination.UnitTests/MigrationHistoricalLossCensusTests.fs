module FS.GG.Coordination.MigrationHistoricalLossCensusTests

open System
open Xunit
open FS.GG.Coordination.Cli

let private digest character = String.replicate 64 (string character)
let private revision = String.replicate 40 "a"
let private cutoff = DateTimeOffset.Parse "2026-09-25T00:00:00Z"
let private families = [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]
let private sources =
    families |> List.mapi (fun index family ->
        { Family=family; ProducerId=$"producer-{index}"; SourceRevision=revision
          SourceUri=$"https://api.github.test/source/{index}?ref={revision}"
          SourceBlobSha256=digest (char (int 'a' + index)) })
let private request =
    { RepositoryId=42L; RepositoryFullName="FS-GG/copy"; CutoffUtc=cutoff
      MissingFamilies=families; SourceBindings=sources; CallerCompletenessFlags=Map.empty }
let private pages =
    [ { Stream="issues"; SubjectNumber=None; RequestedUri="https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100"
        PayloadSha256=digest 'd'; NextUri=None } ]
let private subjects =
    [ { SubjectKind="issue"; SubjectNumber=7; NodeId="I_7"
        ObservedAtUtc=cutoff.AddHours(-1.0); PayloadSha256=digest 'e' } ]
let private text kind number node body =
    { SourceKind=kind; SubjectNumber=number; NodeId=node; ObservedAtUtc=cutoff.AddHours(-1.0)
      PayloadSha256=digest 'f'; Body=body }
let private run texts = MigrationHistoricalLossCensus.proposeVerifiedForTests request pages subjects texts
let private expectError fragment result =
    match result with
    | Error reason ->
        Assert.StartsWith("historical-loss-census-refused:", reason)
        Assert.Contains(fragment, reason)
    | Ok _ -> Assert.Fail("expected historical loss census refusal")

[<Fact>]
let ``zero markers remain observed-zero with all producer and history gaps unresolved`` () =
    let proposal = run [ text "issue-body" 7 "I_7" "ordinary issue" ] |> Result.defaultWith failwith
    Assert.Equal<string list>(families, proposal.Families |> List.map _.Family)
    Assert.All(proposal.Families, fun family ->
        Assert.Equal("observed-zero", family.ObservationState)
        Assert.Empty(family.Observations))
    Assert.Equal<string list>(families, proposal.UnresolvedProducerFamilies)
    Assert.Equal<string list>(families, proposal.UnresolvedHistoryFamilies)
    Assert.Equal(64, proposal.CensusFingerprint.Length)
    Assert.Equal(64, proposal.ProposalFingerprint.Length)
    Assert.Equal(proposal, run [ text "issue-body" 7 "I_7" "ordinary issue" ] |> Result.defaultWith failwith)

[<Fact>]
let ``three missing marker families are counted from exact retained bodies without disposition`` () =
    let texts =
        [ text "pull-comment" 10 "IC_10" "<!-- fsgg:delivery-receipt id=kit head=abc evidence=https://example.test/kit -->"
          text "issue-body" 11 "I_11" "<!-- fsgg:intake-receipt draft=tx-11 -->"
          text "issue-comment" 12 "IC_12" "<!-- fsgg:done-receipt v=1 -->" ]
    let proposal = run texts |> Result.defaultWith failwith
    Assert.All(proposal.Families, fun family ->
        Assert.Equal("observed-1", family.ObservationState)
        Assert.Single(family.Observations) |> ignore
        Assert.Contains("unresolved", family.HistoryGap))

[<Fact>]
let ``post cutoff and unknown subject time refuse`` () =
    let post = { subjects.Head with ObservedAtUtc=cutoff.AddSeconds(1.0) }
    expectError "post-cutoff-or-unknown-subject"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests request pages [ post ] [])
    let nonUtc = { subjects.Head with ObservedAtUtc=DateTimeOffset(2026, 9, 24, 23, 0, 0, TimeSpan.FromHours(1.0)) }
    expectError "post-cutoff-or-unknown-subject"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests request pages [ nonUtc ] [])

[<Fact>]
let ``malformed duplicate and unknown marker prefixes refuse`` () =
    expectError "malformed-marker:delivery-receipt"
        (run [ text "pull-comment" 7 "IC_7" "<!-- fsgg:delivery-receipt id=x" ])
    expectError "malformed-marker:delivery-receipt"
        (run [ text "pull-comment" 7 "IC_7" "<!-- fsgg:delivery-receipt id=UPPER head=h evidence=e -->" ])
    expectError "malformed-marker:legacy-done-receipt"
        (run [ text "pull-comment" 7 "IC_7" "<!-- fsgg:done-receipt v=1 -->" ])
    expectError "duplicate-marker:legacy-done-receipt"
        (run [ text "issue-comment" 7 "IC_7" "<!-- fsgg:done-receipt v=1 -->\n<!-- fsgg:done-receipt v=1 -->" ])
    expectError "unknown-prefix:future-receipt/v9"
        (run [ text "issue-comment" 7 "IC_7" "<!-- fsgg:future-receipt/v9 value=x -->" ])

[<Fact>]
let ``unknown family source mismatch and caller completeness flags refuse`` () =
    expectError "invalid-request"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests
            { request with MissingFamilies=[ "delivery-receipt" ]; SourceBindings=sources |> List.take 1 }
            pages subjects [])
    expectError "invalid-request"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests
            { request with CallerCompletenessFlags=Map [ "native", true ] }
            pages subjects [])
    let changed = { sources.Head with SourceBlobSha256=digest 'A' }
    expectError "invalid-request"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests
            { request with SourceBindings=changed :: sources.Tail }
            pages subjects [])

[<Fact>]
let ``missing duplicate and malformed page evidence refuse`` () =
    expectError "incomplete-pages"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests request [] subjects [])
    expectError "duplicate-page"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests request (pages @ pages) subjects [])
    expectError "page-binding"
        (MigrationHistoricalLossCensus.proposeVerifiedForTests request [ { pages.Head with PayloadSha256="bad" } ] subjects [])
