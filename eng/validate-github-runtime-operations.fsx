#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts

module Q = GitHubRuntimeOperationsQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-runtime-operations.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let read relative = File.ReadAllText(path relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let json relative = JsonDocument.Parse(read relative)
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let contract = json "evidence/github-substrate-v2/gs2-07-8/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.coordination.github-runtime-operations-evidence/1" || text "unit" c <> "GS2-07.8" then failwith "contract identity differs"
if text "roadmapRevision" c <> Q.roadmapRevision || text "roadmapSha256" c <> Q.roadmapSha256 then failwith "roadmap identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-07.7.json" <> text "prerequisiteFileSha256" c then failwith "predecessor receipt bytes differ"
if Q.modelIdentity <> "486e1a956d53f9809f183d336bb97824785b4937f9627e34c558a8c0ef548bc2" then failwith "accepted model identity differs"

let evaluatedBuild () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root; info.UseShellExecute <- false; info.RedirectStandardOutput <- true; info.RedirectStandardError <- true
    for argument in [ "msbuild"; "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj"; "-getProperty:OutputType"; "-getProperty:IsPackable"; "-getProperty:PublishProfile"; "-getProperty:RuntimeIdentifier"; "-getProperty:SelfContained" ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 || error.Trim() <> "" then failwithf "MSBuild evaluation failed: %s" error
    use result = JsonDocument.Parse output
    let p = result.RootElement.GetProperty("Properties")
    { OutputType = text "OutputType" p; IsPackable = text "IsPackable" p = "true"
      PublishProfile = match text "PublishProfile" p with "" -> None | value -> Some value
      RuntimeIdentifier = match text "RuntimeIdentifier" p with "" -> None | value -> Some value
      SelfContained = text "SelfContained" p = "true"; Listening = false; DeploymentConfigured = false; ProductionAuthority = false
      EvaluatedProjects = [ "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj" ] }
let authority = text "candidateHead" c
let exercise failure subjects pages recoveryPath confirmed settlement diagnostic =
    { ExerciseId = $"exercise-{failure}"; Failure = failure; ExpectedSubjects = subjects; RecoveredSubjects = subjects
      ExpectedPages = pages; RecoveredPages = pages; AuthorityBefore = authority; AuthorityAfter = authority
      RecoveryPath = recoveryPath; ProviderConfirmed = confirmed; Settlement = settlement
      DiagnosticInput = diagnostic |> Option.map (fun secret -> $"recovery interrupted synthetic-secret={secret}")
      DiagnosticOutput = diagnostic |> Option.map (fun _ -> "recovery interrupted synthetic-secret=<redacted>") }
let facts: RuntimeOperationsFacts =
    { Unit = "GS2-07.8"; PrerequisiteReceiptSha256 = Q.prerequisiteReceiptSha256; RoadmapRevision = Q.roadmapRevision
      RoadmapSha256 = Q.roadmapSha256; CandidateHead = authority; BuildInputs = evaluatedBuild (); Clauses = Q.requiredClauses
      Exercises =
        [ exercise "event-absence" [ "issue:17" ] [ 1 ] "scheduled-complete-audit" true "converged" None
          exercise "provider-unavailability" [ "project:3"; "repository:coordination" ] [ 1; 2 ] "retry-after-provider-read" true "converged" None
          exercise "incomplete-audit" [ "issue:21"; "issue:22" ] [ 1; 2; 3 ] "discard-partial-and-repeat-complete-audit" true "converged" None
          exercise "backlog-replay" [ "issue:31"; "relation:31-32" ] [ 1; 2 ] "replay-through-shared-reconciler" true "converged" None
          exercise "interruption" [ "ruleset:main" ] [ 1 ] "reobserve-before-resume" false "unsettled" (Some "gs2-07-8-test-secret") ]
      AcceptedChildren = Q.acceptedChildren; ModelIdentity = Q.modelIdentity
      ProductionV2 = false; InstalledAuditExecution = false; PollingReduced = false; Gs208Claimed = false }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = Q.compile facts |> get
let baselineGreen = Q.parse (Q.serialize baseline) = Ok baseline && Q.verify baseline.Seal baseline = Ok baseline && Q.replay baseline facts = Ok baseline
let retained relative = let doc = json relative in strings "controls" doc.RootElement, strings "cases" doc.RootElement
let generatedIds, generatedCases = retained "evidence/github-substrate-v2/gs2-07-8/generated-controls.json"
let independentIds, independentCases = retained "evidence/github-substrate-v2/gs2-07-8/independent-controls.json"
if generatedIds <> Q.requiredControls || independentIds <> Q.requiredControls || generatedCases.Length <> Q.requiredControls.Length || independentCases.Length <> Q.requiredControls.Length || generatedCases = independentCases then failwith "control inventories are not independently complete"
let first = facts.Exercises.Head
let interruption = facts.Exercises |> List.last
let replaceFirst value = value :: facts.Exercises.Tail
let mutation control =
    match control with
    | "prerequisite" -> Q.compile { facts with PrerequisiteReceiptSha256 = String.replicate 64 "0" } |> has RuntimeOperationsFinding.ChangedPrerequisite
    | "roadmap" -> Q.compile { facts with RoadmapRevision = String.replicate 40 "0" } |> has RuntimeOperationsFinding.ChangedRoadmap
    | "command-identity" -> let catalog = read "eng/github-substrate-v2-gates.json" in catalog.Contains("github-runtime-operations-contract") && catalog.Contains("github-runtime-recovery-contract")
    | "evaluated-build-inputs" -> facts.BuildInputs.OutputType = "Library" && not facts.BuildInputs.IsPackable && facts.BuildInputs.PublishProfile.IsNone
    | "host-activation" -> Q.compile { facts with BuildInputs = { facts.BuildInputs with Listening = true } } |> has (RuntimeOperationsFinding.HostActivated "Listening")
    | "runtime-disposition" -> baseline.RuntimeDisposition = "no-host-scheduled-audit-authoritative" && baseline.AuditAuthority = "scheduled-complete-audit"
    | "clause-inventory" -> Q.compile { facts with Clauses = facts.Clauses.Tail } |> has RuntimeOperationsFinding.ClauseInventoryChanged
    | "event-absence" | "provider-unavailability" | "incomplete-audit" | "backlog-replay" as failure -> facts.Exercises |> List.exists (fun value -> value.Failure = failure && value.ProviderConfirmed && value.Settlement = "converged")
    | "interruption" -> interruption.RecoveryPath = "reobserve-before-resume" && not interruption.ProviderConfirmed && interruption.Settlement = "unsettled"
    | "subject-completeness" -> Q.compile { facts with Exercises = replaceFirst { first with RecoveredSubjects = [] } } |> has (RuntimeOperationsFinding.OmittedSubject first.Failure)
    | "page-completeness" -> Q.compile { facts with Exercises = replaceFirst { first with RecoveredPages = [] } } |> has (RuntimeOperationsFinding.OmittedPage first.Failure)
    | "replay-authority" -> Q.compile { facts with Exercises = replaceFirst { first with AuthorityAfter = String.replicate 40 "b" } } |> has (RuntimeOperationsFinding.StaleReplayAuthority first.Failure)
    | "unsettled-outcome" -> Q.compile { facts with Exercises = (facts.Exercises |> List.take 4) @ [ { interruption with ProviderConfirmed = true; Settlement = "converged" } ] } |> has (RuntimeOperationsFinding.InventedSettlement "interruption")
    | "diagnostic-redaction" -> interruption.DiagnosticOutput = Some "recovery interrupted synthetic-secret=<redacted>"
    | "synthetic-secret" -> Q.compile { facts with Exercises = (facts.Exercises |> List.take 4) @ [ { interruption with DiagnosticOutput = interruption.DiagnosticInput } ] } |> has (RuntimeOperationsFinding.SecretLeak "interruption")
    | "child-receipts" -> Q.acceptedChildren |> List.forall (fun child -> let receipt = json $"evidence/github-substrate-v2/accepted/{child.UnitId}.json" in text "digest" receipt.RootElement = child.ReceiptDigest)
    | "comprehensive-command-set" -> let units = read "eng/github-substrate-v2-units.json" in strings "parentClosureCommands" c |> List.forall (fun command -> units.Contains(command, StringComparison.Ordinal))
    | "cold-execution" -> text "closureMode" c = "comprehensive-cold"
    | "model-identity" -> baseline.ModelIdentity = "486e1a956d53f9809f183d336bb97824785b4937f9627e34c558a8c0ef548bc2"
    | "no-production-v2" -> not baseline.ProductionV2
    | "no-installed-audit" -> not baseline.InstalledAuditExecution
    | "retained-polling" -> not baseline.PollingReduced
    | "no-gs2-08" -> not baseline.Gs208Claimed
    | "no-host-writer" -> not facts.BuildInputs.Listening && not facts.BuildInputs.DeploymentConfigured && not facts.BuildInputs.ProductionAuthority
    | "tamper" -> Q.verify baseline.Seal { baseline with RuntimeDisposition = "host-enabled" } |> has (RuntimeOperationsFinding.HostActivated "runtime-disposition")
    | "replay" -> Q.replay baseline facts = Ok baseline
    | _ -> false
let generated: RuntimeOperationsControlResult list = List.map2 (fun id caseName -> { ControlId = id; ControlPassed = mutation id; BaselineGreen = baselineGreen; Evidence = $"generated:{id}:{caseName}" }) Q.requiredControls generatedCases
let independent: RuntimeOperationsControlResult list = List.map2 (fun id caseName -> { ControlId = id; ControlPassed = mutation id; BaselineGreen = baselineGreen; Evidence = $"independent:{id}:{caseName}" }) Q.requiredControls independentCases
match Q.validateControls generated independent with Ok () -> () | Error errors -> failwithf "controls failed: %A" errors
let reportPath = path "evidence/github-substrate-v2/gs2-07-8/qualification-report.json"
let bytes = Q.serialize baseline
if File.Exists reportPath then
    if File.ReadAllText(reportPath).TrimEnd('\r', '\n') <> bytes then failwith "retained qualification report differs"
else printfn "RUNTIME_OPERATIONS_REPORT_JSON=%s" bytes
printfn "GITHUB_RUNTIME_OPERATIONS_OK clauses=%d exercises=%d subjects=%d pages=%d controls=%d seal=%s" baseline.Clauses.Length baseline.Exercises.Length (baseline.Exercises |> List.sumBy (_.ExpectedSubjects.Length)) (baseline.Exercises |> List.sumBy (_.ExpectedPages.Length)) Q.requiredControls.Length baseline.Seal
