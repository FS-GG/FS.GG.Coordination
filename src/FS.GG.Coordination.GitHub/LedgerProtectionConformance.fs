namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LedgerProtectionConformanceState = CurrentPreInstall | InstalledFleetProtection | InstalledProductionProtection | IncompleteOrUnknown | DriftOrTamper
type LedgerGitRef = { Name: string; ObjectSha: string }
type LedgerEnvironmentProtection =
    { Repository: string; Name: string; ReviewerIds: int64 list; PreventSelfReview: bool; CanAdminsBypass: bool
      ProtectedBranches: bool; CustomBranchPolicies: bool; DeploymentBranchPatterns: string list }
type LedgerProtectionBindings = { OrdinaryWriterAppId: int64 option; CutoverWriterAppId: int64 option; ControlIssueNumber: int64 option }
type LedgerProtectionOperationalState =
    { SettingsApplied: LedgerObservation<bool>; AppCustodyReady: LedgerObservation<bool>
      FleetInitialized: LedgerObservation<bool>; MonitoringReady: LedgerObservation<bool> }
type LedgerProtectionConformanceSnapshot =
    { Provider: LedgerProviderObservation; FleetHeads: LedgerObservation<LedgerGitRef list>; PhaseTags: LedgerObservation<LedgerGitRef list>
      EffectiveRules: LedgerObservation<LedgerRule list>; ClassicProtection: LedgerObservation<bool>
      FleetEnvironment: LedgerObservation<LedgerEnvironmentProtection>; BranchProtectionRulePatterns: LedgerObservation<string list>
      Bindings: LedgerProtectionBindings; Operational: LedgerProtectionOperationalState }
type LedgerProtectionPreparation = { State: LedgerProtectionConformanceState; ApplyAuthorized: bool; Operations: string list; Blockers: string list; Seal: string }

module LedgerProtectionConformance =
    let private digest (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private digestLike (value:string) = value.Length=64 && Seq.forall Uri.IsHexDigit value
    let private known = function Observed _ | ProvenAbsent -> true | Unknown _ -> false
    let private truth = function Observed true -> true | _ -> false
    let private rules = function RulesetsPage values -> values | _ -> []
    let private installations = function OrganizationInstallationsPage values -> values | _ -> []
    let private issues = function ControlIssuesPage values -> values | _ -> []
    let private exact expected actual = Set.ofList expected=Set.ofList actual && expected.Length=actual.Length
    let private observation render = function Observed value -> "observed:"+render value | ProvenAbsent -> "absent" | Unknown why -> "unknown:"+why
    let private ruleText = function Creation -> "creation" | Update -> "update" | Deletion -> "deletion" | NonFastForward -> "non-fast-forward"
    let private refText (values:LedgerGitRef list) = values |> List.sortBy (fun value -> value.Name,value.ObjectSha) |> List.map (fun value -> value.Name+":"+value.ObjectSha) |> String.concat ","
    let private environmentText (value:LedgerEnvironmentProtection) = String.concat ":" [value.Repository;value.Name;value.ReviewerIds |> List.sort |> List.map string |> String.concat ",";string value.PreventSelfReview;string value.CanAdminsBypass;string value.ProtectedBranches;string value.CustomBranchPolicies;String.concat "," value.DeploymentBranchPatterns]
    let private contentsOnly repository id values =
        values |> List.exists (fun value ->
            value.AppId=id && value.RepositorySelection="selected" && value.SelectedRepositoriesPagesComplete
            && value.Permissions |> List.filter (fun (name,_) -> name<>"metadata") = [("contents","write")]
            && value.SelectedRepositories=Observed [repository])
    let private installedComposition snapshot =
        match snapshot.Bindings.OrdinaryWriterAppId,snapshot.Bindings.CutoverWriterAppId,snapshot.Bindings.ControlIssueNumber with
        | Some ordinary,Some cutover,Some control ->
            let allRules = snapshot.Provider.Pages |> List.collect (fun page -> rules page.Payload)
            let allApps = snapshot.Provider.Pages |> List.collect (fun page -> installations page.Payload)
            let allIssues = snapshot.Provider.Pages |> List.collect (fun page -> issues page.Payload)
            let active target includes excludes expectedRules expectedBypass =
                allRules |> List.exists (fun value -> value.Target=target && value.Enforcement=Active && not value.Inherited && value.Includes=[includes] && value.Excludes=excludes && exact expectedRules value.Rules && value.Bypass=expectedBypass)
            contentsOnly snapshot.Provider.Repository ordinary allApps
            && contentsOnly snapshot.Provider.Repository cutover allApps
            && allIssues |> List.exists (fun issue -> issue.Number=control && not issue.IsPullRequest)
            && active Branch LedgerProtectionPlanAdapter.journalPattern [LedgerProtectionPlanAdapter.fleetRef] [Creation;Update] [{Actor=App ordinary;Mode=Always}]
            && active Branch LedgerProtectionPlanAdapter.fleetRef [] [Creation;Update] [{Actor=App cutover;Mode=Always}]
            && active Branch LedgerProtectionPlanAdapter.journalPattern [] [Deletion;NonFastForward] []
            && active Tag LedgerProtectionPlanAdapter.phaseTagPattern [] [Creation] [{Actor=App cutover;Mode=Always}]
            && active Tag LedgerProtectionPlanAdapter.phaseTagPattern [] [Update;Deletion] []
        | _ -> false
    let private environmentMatches = function
        | Observed value -> value.Repository="FS-GG/.github" && value.Name="fleet-cutover" && exact LedgerProtectionPlanAdapter.environmentReviewerIds value.ReviewerIds && value.PreventSelfReview && not value.CanAdminsBypass && not value.ProtectedBranches && value.CustomBranchPolicies && value.DeploymentBranchPatterns=["main"]
        | _ -> false
    let private currentComposition snapshot normalized =
        snapshot.Bindings={OrdinaryWriterAppId=None;CutoverWriterAppId=None;ControlIssueNumber=None}
        && snapshot.FleetHeads=Observed [] && snapshot.PhaseTags=Observed [] && snapshot.FleetEnvironment=ProvenAbsent
        && snapshot.ClassicProtection=ProvenAbsent && snapshot.BranchProtectionRulePatterns=Observed []
        && match snapshot.EffectiveRules with Observed values -> exact [Creation;Update;Deletion;NonFastForward] values | _ -> false
        && normalized.DedicatedWriterApp=Unknown "dedicated-writer-unbound" && normalized.ControlIssue=Unknown "control-issue-unbound"
    let classify asOf maxAge snapshot =
        let dimensions = [known snapshot.FleetHeads;known snapshot.PhaseTags;known snapshot.EffectiveRules;known snapshot.ClassicProtection;known snapshot.FleetEnvironment;known snapshot.BranchProtectionRulePatterns]
        if dimensions |> List.contains false then IncompleteOrUnknown else
        match LedgerProtectionProviderAdapter.normalize asOf maxAge snapshot.Provider with
        | Error _ -> DriftOrTamper
        | Ok normalized when currentComposition snapshot normalized -> CurrentPreInstall
        | Ok _ when snapshot.Bindings.OrdinaryWriterAppId.IsNone || snapshot.Bindings.CutoverWriterAppId.IsNone || snapshot.Bindings.ControlIssueNumber.IsNone -> IncompleteOrUnknown
        | Ok _ when installedComposition snapshot && environmentMatches snapshot.FleetEnvironment && snapshot.ClassicProtection=ProvenAbsent ->
            let operational = [snapshot.Operational.SettingsApplied;snapshot.Operational.AppCustodyReady;snapshot.Operational.FleetInitialized;snapshot.Operational.MonitoringReady]
            if operational |> List.forall known |> not then IncompleteOrUnknown
            elif operational |> List.forall truth then InstalledProductionProtection
            else InstalledFleetProtection
        | Ok _ -> DriftOrTamper
    let prepare asOf maxAge snapshot =
        let state = classify asOf maxAge snapshot
        let operations =
            [ "bind-"+LedgerProtectionPlanAdapter.ordinaryWriterRole; "bind-"+LedgerProtectionPlanAdapter.cutoverWriterRole; "carve-fleet-ref-from-ordinary-writer"
              "install-exact-fleet-writer"; "retain-journal-integrity"; "install-phase-tag-create-and-integrity"
              "configure-fs-gg-dotgithub-fleet-cutover-environment"; "bind-authority-control-issue"; "verify-two-pass-provider-readback"
              "establish-app-custody"; "initialize-fleet"; "enable-monitoring" ]
        let blockers =
            [ if snapshot.Bindings.OrdinaryWriterAppId.IsNone then "ordinary contents-only App identity is unbound"
              if snapshot.Bindings.CutoverWriterAppId.IsNone then "cutover contents-only App identity is unbound"
              if snapshot.Bindings.ControlIssueNumber.IsNone then "Authority control issue identity is unbound"
              "provider administration is not authorized"
              "credential custody is not established"
              "fleet initialization is pending"
              "monitoring is pending"
              "GS2-08.2 acceptance is pending" ]
        let sealInput =
            String.concat "\n"
                ([ string state;snapshot.Provider.Repository;snapshot.Provider.Revision;defaultArg snapshot.Provider.RawSetSha256 "";defaultArg snapshot.Provider.NormalizedSetSha256 ""
                   snapshot.Bindings.OrdinaryWriterAppId |> Option.map string |> Option.defaultValue "";snapshot.Bindings.CutoverWriterAppId |> Option.map string |> Option.defaultValue "";snapshot.Bindings.ControlIssueNumber |> Option.map string |> Option.defaultValue ""
                   observation refText snapshot.FleetHeads;observation refText snapshot.PhaseTags;observation (List.map ruleText >> List.sort >> String.concat ",") snapshot.EffectiveRules
                   observation string snapshot.ClassicProtection;observation environmentText snapshot.FleetEnvironment;observation (List.sort >> String.concat ",") snapshot.BranchProtectionRulePatterns
                   observation string snapshot.Operational.SettingsApplied;observation string snapshot.Operational.AppCustodyReady;observation string snapshot.Operational.FleetInitialized;observation string snapshot.Operational.MonitoringReady
                   "apply-authorized:false" ] @ operations @ blockers)
        {State=state;ApplyAuthorized=false;Operations=operations;Blockers=blockers;Seal=digest sealInput}
