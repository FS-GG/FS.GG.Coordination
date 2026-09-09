#load "../src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubLedgerProtectionQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "." |> Path.GetFullPath
let at = DateTimeOffset.Parse("2026-09-08T12:00:00Z")
let integrity = { Id=21872115L; Name="v2-journal-integrity"; Target=Branch; Enforcement=Active; Inherited=false; Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[Deletion;NonFastForward]; Bypass=[] }
let writer = { Id=21872113L; Name="v2-journal-writer"; Target=Branch; Enforcement=Active; Inherited=false; Includes=[LedgerProtectionPlanAdapter.journalPattern]; Excludes=[]; Rules=[Creation;Update]; Bypass=[{Actor=App 4166418L;Mode=Always}] }
let snapshot =
    { SchemaVersion=1; Repository=LedgerProtectionPlanAdapter.authorityRepository; RepositoryId=LedgerProtectionPlanAdapter.authorityRepositoryId; Revision=String.replicate 40 "a"; ObservedAt=at
      PagesComplete=true; PageSha256=[String.replicate 64 "b"]; PageDigestSha256="78700acf3b3d42f416e19b9ca0b40b5e2f217bbd3d6930cff1e96901cc482a6e"; PreviousObservationSha256=Some(String.replicate 64 "c"); PreviousObservationEvidenceSha256=Some(String.replicate 64 "c"); ProviderEnvelopeSha256=None; ProviderRawSetSha256=None; ProviderNormalizedSetSha256=None
      Rulesets=Observed [writer;integrity]; PhaseTags=ProvenAbsent; Environment=ProvenAbsent; DedicatedWriterApp=ProvenAbsent; ControlIssue=ProvenAbsent }
let plan = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) snapshot |> Result.defaultWith (failwithf "%A")
if plan.ApplyAuthorized then failwith "source-only plan authorized application"
if plan.FleetRef <> "refs/heads/fsgg/v2/journal/cutover/d5" then failwith "wrong fleet ref"
if LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) snapshot <> Ok plan then failwith "exact replay failed"
let unknownFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Unknown "provider" } |> Result.isError
let absentAppPlans = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with DedicatedWriterApp=ProvenAbsent } |> Result.isOk
let paginationFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PagesComplete=false } |> Result.isError
let digestFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PageDigestSha256="bad" } |> Result.isError
let continuityFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PreviousObservationEvidenceSha256=Some(String.replicate 64 "d") } |> Result.isError
let staleFails = LedgerProtectionPlanAdapter.compile (at.AddDays 2) (TimeSpan.FromHours 24) snapshot |> Result.isError
let compositionFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Observed [writer; { integrity with Rules=[Creation] }] } |> Result.isError
let authorityFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Repository="FS-GG/FS.GG.Coordination" } |> Result.isError
let selectorFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Observed [{writer with Includes=["refs/heads/fsgg/v2/journal/**"]};integrity] } |> Result.isError
let rulesetFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Observed [{writer with Bypass=[{Actor=App 4166418L;Mode=PullRequest}]};integrity] } |> Result.isError
let tamperFails = LedgerProtectionPlanAdapter.verify (String.replicate 64 "0") at (TimeSpan.FromHours 24) snapshot |> Result.isError
let completeSealFails = LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) { snapshot with ProviderEnvelopeSha256=Some(String.replicate 64 "e") } |> Result.isError
let failures = [ unknownFails; paginationFails; digestFails; continuityFails; staleFails; compositionFails; authorityFails; selectorFails; rulesetFails; completeSealFails; tamperFails ]
if failures |> List.exists not then failwith "negative corpus escaped"
let intent kind target = plan.Intents |> List.exists (fun x -> x.Kind=kind && x.Target=target)
let passes = function
    | AuthorityIdentity -> authorityFails && plan.Repository=LedgerProtectionPlanAdapter.authorityRepository
    | ExactFleetRef -> plan.FleetRef="refs/heads/fsgg/v2/journal/cutover/d5"
    | SelectorSemantics -> selectorFails && plan.PhaseTagPattern="refs/tags/fsgg/v2/fleet-cutover/**/*"
    | RulesetSemantics -> rulesetFails
    | SharedWriterCarveOut -> intent "shared-writer-carve-out" plan.FleetRef
    | DedicatedWriterIdentity -> absentAppPlans && (plan.ProductionBlockers |> List.exists (fun x -> x.Contains("identity is missing")))
    | NamespaceIntegrity -> intent "namespace-integrity" LedgerProtectionPlanAdapter.journalPattern
    | PhaseTagCreation -> intent "phase-tag-create" plan.PhaseTagPattern
    | PhaseTagImmutability -> intent "phase-tag-integrity" plan.PhaseTagPattern
    | EffectiveComposition -> compositionFails
    | UnknownVsAbsent -> unknownFails && absentAppPlans
    | ObservationFreshness -> staleFails | Pagination -> paginationFails | ObservationDigest -> digestFails
    | Continuity -> continuityFails | CompleteSeal -> completeSealFails | Tamper -> tamperFails
    | ProtectedEnvironment -> intent "protected-environment" "FS-GG/.github:fleet-cutover"
    | ControlIssue -> intent "control-issue" "unbound" && (plan.ProductionBlockers |> List.exists _.Contains("control issue identity is unbound"))
    | NoApply -> not plan.ApplyAuthorized
let generated = GitHubLedgerProtectionQualification.requiredControls |> List.map (fun c -> { Control=c; Passed=passes c })
let expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/independent-expectations.json")))
let expectedIds = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
let independent = GitHubLedgerProtectionQualification.requiredControls |> List.map (fun c -> { Control=c; Passed=expectedIds.Contains(GitHubLedgerProtectionQualification.controlId c) && passes c })
if GitHubLedgerProtectionQualification.validate generated independent <> Ok() then failwith "dual qualification failed"
printfn "GITHUB_LEDGER_PROTECTION_OK fleetRef=%s intents=%d controls=%d applyAuthorized=%b seal=%s" plan.FleetRef plan.Intents.Length generated.Length plan.ApplyAuthorized plan.Seal
