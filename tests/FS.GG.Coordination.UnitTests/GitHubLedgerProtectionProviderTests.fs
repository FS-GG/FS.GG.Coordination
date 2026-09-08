module FS.GG.Coordination.GitHubLedgerProtectionProviderTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private at = DateTimeOffset.Parse("2026-09-08T18:00:00Z")
let private integrity =
    { Id=21872115L; Name="v2-journal-integrity"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]
      Rules=[LedgerRule.Creation;LedgerRule.Update;LedgerRule.Deletion;LedgerRule.NonFastForward]; Bypass=[] }
let private writer =
    { Id=21872113L; Name="v2-journal-writer"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]
      Rules=[LedgerRule.Creation;LedgerRule.Update]; Bypass=[LedgerActor.App 4166418L] }
let private page endpoint page last payload =
    let status =
        match payload with
        | EnvironmentState LedgerObservation.ProvenAbsent | DedicatedWriterAppState LedgerObservation.ProvenAbsent | ControlIssueState LedgerObservation.ProvenAbsent -> 404
        | _ -> 200
    { Endpoint=endpoint; Page=page; LastPage=last; HttpStatus=status; ObservedAt=at
      PayloadSha256=LedgerProtectionProviderAdapter.payloadSha256 payload; Payload=payload }
let private baseline =
    { SchemaVersion=1; Repository="FS-GG/FS.GG.Coordination"; RepositoryId=849557450L
      Revision=String.replicate 40 "a"; PreviousObservationSha256=Some(String.replicate 64 "c")
      Pages=
        [ page LedgerProtectionProviderAdapter.rulesetsEndpoint 2 2 (RulesetsPage [integrity])
          page LedgerProtectionProviderAdapter.rulesetsEndpoint 1 2 (RulesetsPage [writer])
          page LedgerProtectionProviderAdapter.phaseTagsEndpoint 1 1 (PhaseTagsPage [])
          page LedgerProtectionProviderAdapter.environmentEndpoint 1 1 (EnvironmentState LedgerObservation.ProvenAbsent)
          page LedgerProtectionProviderAdapter.dedicatedWriterAppEndpoint 1 1 (DedicatedWriterAppState LedgerObservation.ProvenAbsent)
          page LedgerProtectionProviderAdapter.controlIssueEndpoint 1 1 (ControlIssueState (LedgerObservation.Observed 2964L)) ] }

[<Fact>]
let ``unordered complete provider pages normalize canonically and remain no apply`` () =
    let normalized = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal(6, normalized.PageSha256.Length)
    Assert.Equal(LedgerObservation.ProvenAbsent, normalized.PhaseTags)
    let plan = LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal("refs/heads/fsgg/v2/journal/cutover/d5", plan.FleetRef)
    Assert.False(plan.ApplyAuthorized)
    Assert.Contains(plan.ProductionBlockers, fun value -> value.Contains("identity is missing"))
    Assert.True(LedgerProtectionPlanAdapter.verify (String.replicate 64 "0") at (TimeSpan.FromHours 1) normalized |> Result.isError)

[<Fact>]
let ``missing duplicate stale failed and altered provider pages refuse`` () =
    let withoutTags = { baseline with Pages=baseline.Pages |> List.filter (fun value -> value.Endpoint <> LedgerProtectionProviderAdapter.phaseTagsEndpoint) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) withoutTags |> Result.isError)
    let duplicate = { baseline with Pages=List.head baseline.Pages::baseline.Pages }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) duplicate |> Result.isError)
    let stale = { baseline with Pages=baseline.Pages |> List.map (fun value -> { value with ObservedAt=at.AddHours(-2) }) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) stale |> Result.isError)
    let failed = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.environmentEndpoint then { value with HttpStatus=403 } else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) failed |> Result.isError)
    let altered = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.rulesetsEndpoint && value.Page=1 then { value with PayloadSha256=String.replicate 64 "0" } else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) altered |> Result.isError)

[<Fact>]
let ``unknown provider state differs from proven absence`` () =
    let replace payload =
        let pages =
            baseline.Pages
            |> List.map (fun value ->
                if value.Endpoint=LedgerProtectionProviderAdapter.environmentEndpoint then page value.Endpoint 1 1 payload
                else value)
        { baseline with Pages=pages }
    let absent = replace (EnvironmentState LedgerObservation.ProvenAbsent) |> LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1)
    let unknown = replace (EnvironmentState (LedgerObservation.Unknown "permission-denied")) |> LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1)
    Assert.True(absent |> Result.isOk)
    Assert.True(unknown |> Result.isError)

[<Fact>]
let ``generated and independent provider controls are exact`` () =
    let pass : GitHubLedgerProtectionProviderControlResult list =
        GitHubLedgerProtectionProviderQualification.requiredControls
        |> List.map (fun value -> { Control=value; Passed=true })
    Assert.Equal(Ok(), GitHubLedgerProtectionProviderQualification.validate pass pass)
    Assert.True(GitHubLedgerProtectionProviderQualification.validate pass (List.tail pass) |> Result.isError)
