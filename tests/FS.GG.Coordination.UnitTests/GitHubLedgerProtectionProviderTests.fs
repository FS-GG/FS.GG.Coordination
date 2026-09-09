module FS.GG.Coordination.GitHubLedgerProtectionProviderTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private at = DateTimeOffset.Parse("2026-09-08T18:00:00Z")
let private integrity =
    { Id=21872115L; Name="v2-journal-integrity"; Target=LedgerRulesetTarget.Branch; Enforcement=LedgerRulesetEnforcement.Active; Inherited=false
      Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[LedgerRule.Deletion;LedgerRule.NonFastForward]; Bypass=[] }
let private writer =
    { Id=21872113L; Name="v2-journal-writer"; Target=LedgerRulesetTarget.Branch; Enforcement=LedgerRulesetEnforcement.Active; Inherited=false
      Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[LedgerRule.Creation;LedgerRule.Update]
      Bypass=[{Actor=LedgerActor.App 4166418L;Mode=LedgerBypassMode.Always}] }
let private page endpoint page last payload =
    { Endpoint=endpoint; Page=page; LastPage=last; IsTerminal=(page=last); HttpStatus=200; ObservedAt=at
      PayloadSha256=LedgerProtectionProviderAdapter.payloadSha256 payload; Payload=payload }
let private baseline =
    { SchemaVersion=1; Repository=LedgerProtectionPlanAdapter.authorityRepository; RepositoryId=LedgerProtectionPlanAdapter.authorityRepositoryId
      Revision=String.replicate 40 "a"; PreviousObservationSha256=Some(String.replicate 64 "c"); PreviousObservationEvidenceSha256=Some(String.replicate 64 "c"); RawSetSha256=Some(String.replicate 64 "d"); NormalizedSetSha256=Some(String.replicate 64 "e"); DedicatedWriterAppId=None; ControlIssueNumber=None
      Pages=
        [ page LedgerProtectionProviderAdapter.rulesetsEndpoint 2 2 (RulesetsPage [integrity])
          page LedgerProtectionProviderAdapter.rulesetsEndpoint 1 2 (RulesetsPage [writer])
          page LedgerProtectionProviderAdapter.phaseTagsEndpoint 1 1 (PhaseTagsPage [])
          page LedgerProtectionProviderAdapter.environmentEndpoint 1 1 (EnvironmentsPage ["github-substrate-v2-sandbox"])
          page LedgerProtectionProviderAdapter.installationsEndpoint 1 1 (OrganizationInstallationsPage [])
          page LedgerProtectionProviderAdapter.controlIssuesEndpoint 1 1 (ControlIssuesPage [{Number=1L;IsPullRequest=false}]) ] }

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
    let nonterminal = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.phaseTagsEndpoint then { value with IsTerminal=false } else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) nonterminal |> Result.isError)
    let stale = { baseline with Pages=baseline.Pages |> List.map (fun value -> { value with ObservedAt=at.AddHours(-2) }) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) stale |> Result.isError)
    let failed = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.environmentEndpoint then { value with HttpStatus=403 } else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) failed |> Result.isError)
    let altered = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.rulesetsEndpoint && value.Page=1 then { value with PayloadSha256=String.replicate 64 "0" } else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) altered |> Result.isError)
    let wrongTag = { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.phaseTagsEndpoint then page value.Endpoint 1 1 (PhaseTagsPage ["refs/tags/fsgg/v2/epoch/fs-gg-production/phase/Frozen"]) else value) }
    Assert.True(LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) wrongTag |> Result.isError)

[<Fact>]
let ``unbound desired identity differs from observed absence`` () =
    let replaceEnvironment payload =
        let pages =
            baseline.Pages
            |> List.map (fun value ->
                if value.Endpoint=LedgerProtectionProviderAdapter.environmentEndpoint then page value.Endpoint 1 1 payload
                else value)
        { baseline with Pages=pages }
    let absent = replaceEnvironment (EnvironmentsPage ["github-substrate-v2-sandbox"]) |> LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(LedgerObservation.ProvenAbsent, absent.Environment)
    Assert.Equal(LedgerObservation.Unknown "dedicated-writer-unbound", absent.DedicatedWriterApp)
    Assert.Equal(LedgerObservation.Unknown "control-issue-unbound", absent.ControlIssue)

[<Fact>]
let ``real endpoints and Authority issue inventory never reuse parent issue`` () =
    Assert.Contains("FS.GG.Coordination.Authority/rulesets", LedgerProtectionProviderAdapter.rulesetsEndpoint)
    Assert.Contains("/orgs/FS-GG/installations", LedgerProtectionProviderAdapter.installationsEndpoint)
    Assert.Contains("FS-GG/.github/environments", LedgerProtectionProviderAdapter.environmentEndpoint)
    Assert.Contains("FS.GG.Coordination.Authority/issues", LedgerProtectionProviderAdapter.controlIssuesEndpoint)
    Assert.DoesNotContain("2964", LedgerProtectionProviderAdapter.controlIssuesEndpoint)
    Assert.DoesNotContain("dedicated-ledger-writer", LedgerProtectionProviderAdapter.installationsEndpoint)

[<Fact>]
let ``provider envelope metadata and explicit selected repository evidence are sealed`` () =
    let normalized = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
    let plan = LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
    let retimed = { baseline with Pages=baseline.Pages |> List.map (fun value -> { value with ObservedAt=at.AddMinutes(-1) }) }
    let retimedNormalized = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) retimed |> Result.defaultWith (failwithf "%A")
    Assert.NotEqual(normalized.ProviderEnvelopeSha256, retimedNormalized.ProviderEnvelopeSha256)
    Assert.Equal(Error [AlteredLedgerProtectionSeal], LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 1) retimedNormalized)
    let installation =
        { InstallationId=77L; AppId=9001L; Slug="dedicated-ledger-writer"; RepositorySelection="selected"
          Permissions=[("contents","write");("metadata","read")]; SelectedRepositoriesEndpoint=Some(LedgerProtectionProviderAdapter.selectedRepositoriesEndpoint 77L); SelectedRepositoriesPagesComplete=true
          SelectedRepositories=LedgerObservation.Observed [LedgerProtectionPlanAdapter.authorityRepository] }
    let bound =
        { baseline with DedicatedWriterAppId=Some 9001L
                        Pages=baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.installationsEndpoint then page value.Endpoint 1 1 (OrganizationInstallationsPage [installation]) else value) }
    let boundPlan = LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) bound |> Result.defaultWith (failwithf "%A")
    Assert.DoesNotContain(boundPlan.ProductionBlockers, fun value -> value.Contains("identity is missing"))
    let appAuthenticated =
        { bound with Pages=bound.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.installationsEndpoint then page value.Endpoint 1 1 (OrganizationInstallationsPage [{installation with SelectedRepositoriesEndpoint=Some LedgerProtectionProviderAdapter.appSelectedRepositoriesEndpoint}]) else value) }
    Assert.True(LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) appAuthenticated |> Result.isOk)
    let extraRepository = {installation with SelectedRepositories=LedgerObservation.Observed [LedgerProtectionPlanAdapter.authorityRepository;"FS-GG/another-repository"]}
    let notExact = {bound with Pages=bound.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.installationsEndpoint then page value.Endpoint 1 1 (OrganizationInstallationsPage [extraRepository]) else value)}
    let notExactNormalized = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) notExact |> Result.defaultWith (failwithf "%A")
    Assert.Equal(LedgerObservation.ProvenAbsent,notExactNormalized.DedicatedWriterApp)
    let overprivileged = {installation with Permissions=("issues","write")::installation.Permissions}
    let refused = {bound with Pages=bound.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.installationsEndpoint then page value.Endpoint 1 1 (OrganizationInstallationsPage [overprivileged]) else value)}
    Assert.True(LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) refused |> Result.isError)

[<Fact>]
let ``generated and independent provider controls are exact`` () =
    let pass : GitHubLedgerProtectionProviderControlResult list =
        GitHubLedgerProtectionProviderQualification.requiredControls
        |> List.map (fun value -> { Control=value; Passed=true })
    Assert.Equal(Ok(), GitHubLedgerProtectionProviderQualification.validate pass pass)
    Assert.True(GitHubLedgerProtectionProviderQualification.validate pass (List.tail pass) |> Result.isError)
