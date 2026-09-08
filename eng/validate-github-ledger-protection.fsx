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
let integrity = { Id=21872115L; Name="v2-journal-integrity"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]; Rules=[Creation;Update;Deletion;NonFastForward]; Bypass=[] }
let writer = { Id=21872113L; Name="v2-journal-writer"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]; Rules=[Creation;Update]; Bypass=[App 4166418L] }
let snapshot =
    { SchemaVersion=1; Repository="FS-GG/FS.GG.Coordination"; RepositoryId=849557450L; Revision=String.replicate 40 "a"; ObservedAt=at
      PagesComplete=true; PageSha256=[String.replicate 64 "b"]; PageDigestSha256="78700acf3b3d42f416e19b9ca0b40b5e2f217bbd3d6930cff1e96901cc482a6e"; PreviousObservationSha256=Some(String.replicate 64 "c")
      Rulesets=Observed [writer;integrity]; PhaseTags=ProvenAbsent; Environment=ProvenAbsent; DedicatedWriterApp=ProvenAbsent; ControlIssue=ProvenAbsent }
let plan = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) snapshot |> Result.defaultWith (failwithf "%A")
if plan.ApplyAuthorized then failwith "source-only plan authorized application"
if plan.FleetRef <> "refs/heads/fsgg/v2/journal/cutover/d5" then failwith "wrong fleet ref"
if LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 24) snapshot <> Ok plan then failwith "exact replay failed"
let unknownFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Unknown "provider" } |> Result.isError
let absentAppPlans = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with DedicatedWriterApp=ProvenAbsent } |> Result.isOk
let paginationFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PagesComplete=false } |> Result.isError
let digestFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PageDigestSha256="bad" } |> Result.isError
let continuityFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with PreviousObservationSha256=None } |> Result.isError
let staleFails = LedgerProtectionPlanAdapter.compile (at.AddDays 2) (TimeSpan.FromHours 24) snapshot |> Result.isError
let compositionFails = LedgerProtectionPlanAdapter.compile at (TimeSpan.FromHours 24) { snapshot with Rulesets=Observed [writer; { integrity with Rules=[Creation] }] } |> Result.isError
let tamperFails = LedgerProtectionPlanAdapter.verify (String.replicate 64 "0") at (TimeSpan.FromHours 24) snapshot |> Result.isError
let failures = [ unknownFails; paginationFails; digestFails; continuityFails; staleFails; compositionFails; tamperFails ]
if failures |> List.exists not then failwith "negative corpus escaped"
let intent kind target = plan.Intents |> List.exists (fun x -> x.Kind=kind && x.Target=target)
let passes = function
    | ExactFleetRef -> plan.FleetRef="refs/heads/fsgg/v2/journal/cutover/d5"
    | SharedWriterCarveOut -> intent "shared-writer-carve-out" plan.FleetRef
    | DedicatedWriterIdentity -> absentAppPlans && (plan.ProductionBlockers |> List.exists (fun x -> x.Contains("identity is missing")))
    | NamespaceIntegrity -> intent "namespace-integrity" "refs/heads/fsgg/v2/journal/**"
    | PhaseTagCreation -> intent "phase-tag-create" plan.PhaseTagPattern
    | PhaseTagImmutability -> intent "phase-tag-integrity" plan.PhaseTagPattern
    | EffectiveComposition -> compositionFails
    | UnknownVsAbsent -> unknownFails && absentAppPlans
    | ObservationFreshness -> staleFails | Pagination -> paginationFails | ObservationDigest -> digestFails
    | Continuity -> continuityFails | Tamper -> tamperFails
    | ProtectedEnvironment -> intent "protected-environment" "fleet-cutover"
    | ControlIssue -> intent "control-issue" "fleet-cutover"
    | NoApply -> not plan.ApplyAuthorized
let generated = GitHubLedgerProtectionQualification.requiredControls |> List.map (fun c -> { Control=c; Passed=passes c })
let expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/independent-expectations.json")))
let expectedIds = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
let independent = GitHubLedgerProtectionQualification.requiredControls |> List.map (fun c -> { Control=c; Passed=expectedIds.Contains(GitHubLedgerProtectionQualification.controlId c) && passes c })
if GitHubLedgerProtectionQualification.validate generated independent <> Ok() then failwith "dual qualification failed"
printfn "GITHUB_LEDGER_PROTECTION_OK fleetRef=%s intents=%d controls=%d applyAuthorized=%b seal=%s" plan.FleetRef plan.Intents.Length generated.Length plan.ApplyAuthorized plan.Seal
