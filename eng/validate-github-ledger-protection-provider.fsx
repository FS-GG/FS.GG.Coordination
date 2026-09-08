#load "../src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/LedgerProtectionProviderAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubLedgerProtectionProviderQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "." |> Path.GetFullPath
let at = DateTimeOffset.Parse("2026-09-08T18:00:00Z")
let integrity = { Id=21872115L; Name="v2-journal-integrity"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]; Rules=[LedgerRule.Creation;LedgerRule.Update;LedgerRule.Deletion;LedgerRule.NonFastForward]; Bypass=[] }
let writer = { Id=21872113L; Name="v2-journal-writer"; Includes=["refs/heads/fsgg/v2/journal/**"]; Excludes=[]; Rules=[LedgerRule.Creation;LedgerRule.Update]; Bypass=[LedgerActor.App 4166418L] }
let page endpoint number last payload =
    let status =
        match payload with
        | EnvironmentState LedgerObservation.ProvenAbsent | DedicatedWriterAppState LedgerObservation.ProvenAbsent | ControlIssueState LedgerObservation.ProvenAbsent -> 404
        | _ -> 200
    { Endpoint=endpoint; Page=number; LastPage=last; HttpStatus=status; ObservedAt=at
      PayloadSha256=LedgerProtectionProviderAdapter.payloadSha256 payload; Payload=payload }
let baseline =
    { SchemaVersion=1; Repository="FS-GG/FS.GG.Coordination"; RepositoryId=849557450L; Revision=String.replicate 40 "a"
      PreviousObservationSha256=Some(String.replicate 64 "c")
      Pages=
        [ page LedgerProtectionProviderAdapter.rulesetsEndpoint 2 2 (RulesetsPage [integrity])
          page LedgerProtectionProviderAdapter.rulesetsEndpoint 1 2 (RulesetsPage [writer])
          page LedgerProtectionProviderAdapter.phaseTagsEndpoint 1 1 (PhaseTagsPage [])
          page LedgerProtectionProviderAdapter.environmentEndpoint 1 1 (EnvironmentState LedgerObservation.ProvenAbsent)
          page LedgerProtectionProviderAdapter.dedicatedWriterAppEndpoint 1 1 (DedicatedWriterAppState LedgerObservation.ProvenAbsent)
          page LedgerProtectionProviderAdapter.controlIssueEndpoint 1 1 (ControlIssueState (LedgerObservation.Observed 2964L)) ] }
let normalized = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
let plan = LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) baseline |> Result.defaultWith (failwithf "%A")
let withoutTags = { baseline with Pages=baseline.Pages |> List.filter (fun value -> value.Endpoint <> LedgerProtectionProviderAdapter.phaseTagsEndpoint) }
let incompleteFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) withoutTags |> Result.isError
let duplicateFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) { baseline with Pages=List.head baseline.Pages::baseline.Pages } |> Result.isError
let staleFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) { baseline with Pages=baseline.Pages |> List.map (fun value -> { value with ObservedAt=at.AddHours(-2) }) } |> Result.isError
let alteredFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) { baseline with Pages=baseline.Pages |> List.map (fun value -> if value.Page=1 && value.Endpoint=LedgerProtectionProviderAdapter.rulesetsEndpoint then { value with PayloadSha256=String.replicate 64 "0" } else value) } |> Result.isError
let bindingFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) { baseline with Repository="someone/else" } |> Result.isError
let continuityFails = LedgerProtectionProviderAdapter.normalize at (TimeSpan.FromHours 1) { baseline with PreviousObservationSha256=None } |> Result.isError
let replaceEnvironment payload =
    let pages = baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.environmentEndpoint then page value.Endpoint 1 1 payload else value)
    { baseline with Pages=pages }
let unknownFails = replaceEnvironment (EnvironmentState (LedgerObservation.Unknown "permission-denied")) |> LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) |> Result.isError
let absentWorks = replaceEnvironment (EnvironmentState LedgerObservation.ProvenAbsent) |> LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) |> Result.isOk
let weakIntegrity = { integrity with Rules=[LedgerRule.Creation] }
let compositionFails =
    let pages = baseline.Pages |> List.map (fun value -> if value.Endpoint=LedgerProtectionProviderAdapter.rulesetsEndpoint && value.Page=2 then page value.Endpoint 2 2 (RulesetsPage [weakIntegrity]) else value)
    LedgerProtectionProviderAdapter.compile at (TimeSpan.FromHours 1) { baseline with Pages=pages } |> Result.isError
let exactReplay = LedgerProtectionPlanAdapter.verify plan.Seal at (TimeSpan.FromHours 1) normalized = Ok plan
let tamperFails = LedgerProtectionPlanAdapter.verify (String.replicate 64 "0") at (TimeSpan.FromHours 1) normalized |> Result.isError
let passes = function
    | ProviderBinding -> bindingFails | PageOrdering -> normalized.PageSha256.Length=6
    | CompletePagination -> incompleteFails && duplicateFails | PayloadDigest -> alteredFails
    | ProviderFreshness -> staleFails | ProviderUnknownVsAbsent -> unknownFails && absentWorks
    | ProviderEffectiveComposition -> compositionFails | ProviderContinuity -> continuityFails
    | ProviderExactFleetRef -> plan.FleetRef="refs/heads/fsgg/v2/journal/cutover/d5"
    | DryOperationSeal -> exactReplay && tamperFails | DedicatedWriterBlocker -> plan.ProductionBlockers |> List.exists _.Contains("identity is missing")
    | ProviderNoApply -> not plan.ApplyAuthorized
let generated : GitHubLedgerProtectionProviderControlResult list = GitHubLedgerProtectionProviderQualification.requiredControls |> List.map (fun control -> { Control=control; Passed=passes control })
let corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/provider-observation-corpus.json")))
if corpus.RootElement.GetProperty("sourceCategory").GetString() <> "sanitized-deterministic-fixture" then failwith "provider corpus claims a live observation"
if corpus.RootElement.GetProperty("writesAttempted").GetInt32() <> 0 || corpus.RootElement.GetProperty("providerReadbackClaimed").GetBoolean() || corpus.RootElement.GetProperty("applyAuthorized").GetBoolean() then failwith "provider corpus crosses the no-effect boundary"
let expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"evidence/github-substrate-v2/gs2-08-2/provider-independent-expectations.json")))
let expectedIds = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
let independent : GitHubLedgerProtectionProviderControlResult list = GitHubLedgerProtectionProviderQualification.requiredControls |> List.map (fun control -> { Control=control; Passed=expectedIds.Contains(GitHubLedgerProtectionProviderQualification.controlId control) && passes control })
GitHubLedgerProtectionProviderQualification.validate generated independent |> Result.defaultWith (failwithf "%A")
printfn "GITHUB_LEDGER_PROTECTION_PROVIDER_OK pages=%d controls=%d fleetRef=%s applyAuthorized=%b seal=%s" normalized.PageSha256.Length generated.Length plan.FleetRef plan.ApplyAuthorized plan.Seal
