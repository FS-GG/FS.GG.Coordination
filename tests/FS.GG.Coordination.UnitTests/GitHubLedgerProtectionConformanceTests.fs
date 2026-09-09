module FS.GG.Coordination.GitHubLedgerProtectionConformanceTests

open System
open System.IO
open Xunit
open FS.GG.Coordination.GitHub

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private capture () =
    File.ReadAllBytes(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/live-capture-pass2.json"))
    |> ReadOnlyMemory<byte> |> LedgerProtectionProviderCodec.decode |> Result.defaultWith (failwithf "%A")

[<Fact>]
let ``retained live observation is exactly current pre-install state`` () =
    let value = capture()
    Assert.Equal(CurrentPreInstall, LedgerProtectionConformance.classify value.CapturedAt (TimeSpan.FromMinutes 5.0) value.Conformance)
    let preparation = LedgerProtectionConformance.prepare value.CapturedAt (TimeSpan.FromMinutes 5.0) value.Conformance
    Assert.Equal(CurrentPreInstall, preparation.State)
    Assert.False(preparation.ApplyAuthorized)
    Assert.Contains("configure-fs-gg-dotgithub-fleet-cutover-environment",preparation.Operations)
    Assert.Contains(preparation.Blockers, fun blocker -> blocker.Contains("provider administration is not authorized"))

[<Fact>]
let ``unknown provider dimension is incomplete and stale provider evidence is tamper`` () =
    let value = capture()
    Assert.Equal(IncompleteOrUnknown, LedgerProtectionConformance.classify value.CapturedAt (TimeSpan.FromMinutes 5.0) {value.Conformance with EffectiveRules=LedgerObservation.Unknown "denied"})
    Assert.Equal(DriftOrTamper, LedgerProtectionConformance.classify (value.CapturedAt.AddHours 2) (TimeSpan.FromMinutes 5.0) value.Conformance)

let private installed operational =
    let value = capture()
    let at = value.CapturedAt
    let ordinary,cutover,control = 5001L,5002L,42L
    let rule id name target includeRef excludes rules bypass =
        {Id=id;Name=name;Target=target;Enforcement=Active;Inherited=false;Includes=[includeRef];Excludes=excludes;Rules=rules;Bypass=bypass}
    let rules =
        [ rule 9001L "ordinary-writer" Branch LedgerProtectionPlanAdapter.journalPattern [LedgerProtectionPlanAdapter.fleetRef] [Creation;Update] [{Actor=App ordinary;Mode=Always}]
          rule 9002L "cutover-writer" Branch LedgerProtectionPlanAdapter.fleetRef [] [Creation;Update] [{Actor=App cutover;Mode=Always}]
          rule 21872115L "integrity" Branch LedgerProtectionPlanAdapter.journalPattern [] [Deletion;NonFastForward] []
          rule 9003L "phase-create" Tag LedgerProtectionPlanAdapter.phaseTagPattern [] [Creation] [{Actor=App cutover;Mode=Always}]
          rule 9004L "phase-integrity" Tag LedgerProtectionPlanAdapter.phaseTagPattern [] [Update;Deletion] [] ]
    let app installation appId slug =
        {InstallationId=installation;AppId=appId;Slug=slug;RepositorySelection="selected";Permissions=[("contents","write");("metadata","read")]
         SelectedRepositoriesEndpoint=Some($"GET /user/installations/{installation}/repositories?per_page=100");SelectedRepositoriesPagesComplete=true;SelectedRepositories=LedgerObservation.Observed [LedgerProtectionPlanAdapter.authorityRepository]}
    let page endpoint payload = {Endpoint=endpoint;Page=1;LastPage=1;IsTerminal=true;HttpStatus=200;ObservedAt=at;PayloadSha256=LedgerProtectionProviderAdapter.payloadSha256 payload;Payload=payload}
    let pages =
        [page LedgerProtectionProviderAdapter.rulesetsEndpoint (RulesetsPage rules)
         page LedgerProtectionProviderAdapter.phaseTagsEndpoint (PhaseTagsPage ["refs/tags/fsgg/v2/fleet-cutover/open-v2"])
         page LedgerProtectionProviderAdapter.environmentEndpoint (EnvironmentsPage ["fleet-cutover"])
         page LedgerProtectionProviderAdapter.installationsEndpoint (OrganizationInstallationsPage [app 7001L ordinary "ordinary";app 7002L cutover "cutover"])
         page LedgerProtectionProviderAdapter.controlIssuesEndpoint (ControlIssuesPage [{Number=control;IsPullRequest=false}])]
    { value.Conformance with
        Provider={value.Conformance.Provider with DedicatedWriterAppId=Some cutover;ControlIssueNumber=Some control;Pages=pages}
        FleetHeads=LedgerObservation.Observed [{Name=LedgerProtectionPlanAdapter.fleetRef;ObjectSha=String.replicate 40 "a"}]
        PhaseTags=LedgerObservation.Observed [{Name="refs/tags/fsgg/v2/fleet-cutover/open-v2";ObjectSha=String.replicate 40 "b"}]
        EffectiveRules=LedgerObservation.Observed [Creation;Update;Deletion;NonFastForward];ClassicProtection=LedgerObservation.ProvenAbsent
        FleetEnvironment=LedgerObservation.Observed {Repository="FS-GG/.github";Name="fleet-cutover";ReviewerIds=[1645484L;4456104L];PreventSelfReview=true;CanAdminsBypass=false;ProtectedBranches=false;CustomBranchPolicies=true;DeploymentBranchPatterns=["main"]}
        BranchProtectionRulePatterns=LedgerObservation.Observed []
        Bindings={OrdinaryWriterAppId=Some ordinary;CutoverWriterAppId=Some cutover;ControlIssueNumber=Some control}
        Operational=operational }, at

[<Fact>]
let ``installed fleet and production are distinct from incomplete operational observation`` () =
    let incomplete,at = installed {SettingsApplied=LedgerObservation.Observed true;AppCustodyReady=LedgerObservation.Unknown "not-read";FleetInitialized=LedgerObservation.Observed false;MonitoringReady=LedgerObservation.Observed false}
    Assert.Equal(IncompleteOrUnknown, LedgerProtectionConformance.classify at (TimeSpan.FromMinutes 5.0) incomplete)
    let fleet,_ = installed {SettingsApplied=LedgerObservation.Observed true;AppCustodyReady=LedgerObservation.Observed false;FleetInitialized=LedgerObservation.Observed false;MonitoringReady=LedgerObservation.Observed false}
    Assert.Equal(InstalledFleetProtection, LedgerProtectionConformance.classify at (TimeSpan.FromMinutes 5.0) fleet)
    let production,_ = installed {SettingsApplied=LedgerObservation.Observed true;AppCustodyReady=LedgerObservation.Observed true;FleetInitialized=LedgerObservation.Observed true;MonitoringReady=LedgerObservation.Observed true}
    Assert.Equal(InstalledProductionProtection, LedgerProtectionConformance.classify at (TimeSpan.FromMinutes 5.0) production)

[<Fact>]
let ``reviewer or App permission drift is refused`` () =
    let installedValue,at = installed {SettingsApplied=LedgerObservation.Observed true;AppCustodyReady=LedgerObservation.Observed false;FleetInitialized=LedgerObservation.Observed false;MonitoringReady=LedgerObservation.Observed false}
    let wrongEnvironment = match installedValue.FleetEnvironment with LedgerObservation.Observed value -> LedgerObservation.Observed {value with ReviewerIds=[1645484L]} | state -> state
    Assert.Equal(DriftOrTamper, LedgerProtectionConformance.classify at (TimeSpan.FromMinutes 5.0) {installedValue with FleetEnvironment=wrongEnvironment})
    let original = LedgerProtectionConformance.prepare at (TimeSpan.FromMinutes 5.0) installedValue
    let changed = LedgerProtectionConformance.prepare at (TimeSpan.FromMinutes 5.0) {installedValue with FleetEnvironment=wrongEnvironment}
    Assert.NotEqual(original.Seal,changed.Seal)
