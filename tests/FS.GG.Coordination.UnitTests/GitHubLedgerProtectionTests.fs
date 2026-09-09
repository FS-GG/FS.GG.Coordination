module FS.GG.Coordination.GitHubLedgerProtectionTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private at = DateTimeOffset.Parse("2026-09-08T12:00:00Z")
let private integrity =
    { Id=21872115L; Name="v2-journal-integrity"; Target=LedgerRulesetTarget.Branch; Enforcement=LedgerRulesetEnforcement.Active; Inherited=false
      Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[LedgerRule.Deletion; LedgerRule.NonFastForward]; Bypass=[] }
let private writer =
    { Id=21872113L; Name="v2-journal-writer"; Target=LedgerRulesetTarget.Branch; Enforcement=LedgerRulesetEnforcement.Active; Inherited=false
      Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[LedgerRule.Creation; LedgerRule.Update]
      Bypass=[{Actor=LedgerActor.App 4166418L;Mode=LedgerBypassMode.Always}] }
let private baseline =
    { SchemaVersion=1; Repository=LedgerProtectionPlanAdapter.authorityRepository; RepositoryId=LedgerProtectionPlanAdapter.authorityRepositoryId
      Revision=String.replicate 40 "a"; ObservedAt=at; PagesComplete=true; PageSha256=[String.replicate 64 "b"]; PageDigestSha256="78700acf3b3d42f416e19b9ca0b40b5e2f217bbd3d6930cff1e96901cc482a6e"
      PreviousObservationSha256=Some(String.replicate 64 "c"); PreviousObservationEvidenceSha256=Some(String.replicate 64 "c"); ProviderEnvelopeSha256=None; ProviderRawSetSha256=None; ProviderNormalizedSetSha256=None; Rulesets=LedgerObservation.Observed [writer; integrity]
      PhaseTags=LedgerObservation.ProvenAbsent; Environment=LedgerObservation.ProvenAbsent; DedicatedWriterApp=LedgerObservation.ProvenAbsent; ControlIssue=LedgerObservation.ProvenAbsent }

[<Fact>]
let ``fleet ref is independently joined from canonical length framed address`` () =
    let address = ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production" |> Result.defaultWith (failwithf "%A")
    Assert.Equal("d546289f29b34a4967e27425acba1c9ad2feb4f4b2110f5db41a5544976cb363", address.Digest)
    Assert.Equal("refs/heads/fsgg/v2/journal/cutover/d5", LedgerProtectionPlanAdapter.fleetRef)
    Assert.Equal(address.Ref, LedgerProtectionPlanAdapter.fleetRef)

[<Fact>]
let ``prestate compiles to source-only blocked deterministic plan`` () =
    let plan = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) baseline |> Result.defaultWith (failwithf "%A")
    Assert.False(plan.ApplyAuthorized)
    Assert.Equal(7, plan.Intents.Length)
    Assert.Contains(plan.Intents, fun x -> x.Kind="shared-writer-carve-out" && x.Target=LedgerProtectionPlanAdapter.fleetRef)
    Assert.Contains(plan.ProductionBlockers, fun x -> x.Contains("identity is missing"))
    Assert.Equal(Ok plan, LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) baseline)

[<Fact>]
let ``unknown observation pagination stale and contradictory composition refuse`` () =
    Assert.True(LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { baseline with Rulesets=LedgerObservation.Unknown "provider" } |> Result.isError)
    Assert.True(LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { baseline with PagesComplete=false } |> Result.isError)
    Assert.True(LedgerProtectionPlanAdapter.compile (at.AddDays 2) (TimeSpan.FromHours 24) baseline |> Result.isError)
    Assert.True(LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { baseline with Rulesets=LedgerObservation.Observed [writer; { integrity with Rules=[LedgerRule.Creation] }] } |> Result.isError)

[<Fact>]
let ``generated and independent control inventories are exact`` () =
    let pass = GitHubLedgerProtectionQualification.requiredControls |> List.map (fun x -> { Control=x; Passed=true })
    Assert.Equal(Ok(), GitHubLedgerProtectionQualification.validate pass pass)
    Assert.True(GitHubLedgerProtectionQualification.validate pass (List.tail pass) |> Result.isError)

[<Fact>]
let ``dedicated writer is contents-only and exact identity is sealed`` () =
    let app = { Id=9001L; ContentsPermission="write"; AdditionalWritePermissions=[] }
    let snapshot = { baseline with DedicatedWriterApp=LedgerObservation.Observed app }
    let plan = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) snapshot |> Result.defaultWith (failwithf "%A")
    Assert.Contains(plan.Intents, fun x -> x.Kind="dedicated-contents-writer" && x.Bypass=[{Actor=LedgerActor.App 9001L;Mode=LedgerBypassMode.Always}])
    Assert.DoesNotContain(plan.ProductionBlockers, fun x -> x.Contains("identity is missing"))
    let overprivileged = { app with AdditionalWritePermissions=["issues:write"] }
    Assert.True(LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with DedicatedWriterApp=LedgerObservation.Observed overprivileged } |> Result.isError)

[<Fact>]
let ``observation seal binds environment app issue and page continuity`` () =
    let plan = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) baseline |> Result.defaultWith (failwithf "%A")
    for changed in [ { baseline with Environment=LedgerObservation.Observed "fleet-cutover" }; { baseline with ControlIssue=LedgerObservation.Observed 42L } ] do
        Assert.Equal(Error [AlteredLedgerProtectionSeal], LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) changed)
    Assert.True(LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) { baseline with PreviousObservationEvidenceSha256=Some(String.replicate 64 "d") } |> Result.isError)

[<Fact>]
let ``authority identity selectors and effective GitHub ruleset semantics are exact`` () =
    Assert.Equal("FS-GG/FS.GG.Coordination.Authority", LedgerProtectionPlanAdapter.authorityRepository)
    Assert.Equal(1351660651L, LedgerProtectionPlanAdapter.authorityRepositoryId)
    Assert.Equal("refs/heads/fsgg/v2/journal/**/*", LedgerProtectionPlanAdapter.journalPattern)
    Assert.Equal("refs/tags/fsgg/v2/fleet-cutover/**/*", LedgerProtectionPlanAdapter.phaseTagPattern)
    for invalid in
        [ { baseline with Repository="FS-GG/FS.GG.Coordination" }
          { baseline with Rulesets=LedgerObservation.Observed [{ writer with Bypass=[{Actor=LedgerActor.App 4166418L;Mode=LedgerBypassMode.PullRequest}] }; integrity] }
          { baseline with Rulesets=LedgerObservation.Observed [writer; { integrity with Enforcement=LedgerRulesetEnforcement.Evaluate }] }
          { baseline with Rulesets=LedgerObservation.Observed [writer; { integrity with Inherited=true }] }
          { baseline with Rulesets=LedgerObservation.Observed [{ writer with Includes=["refs/heads/fsgg/v2/journal/**"] }; integrity] } ] do
        Assert.True(LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) invalid |> Result.isError)
