#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventBenefitQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-event-benefit-measurement.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let read relative = File.ReadAllText(path relative)
let shaText (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let json relative = JsonDocument.Parse(read relative)
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let metric value = { Value = Some value; UnknownReason = None }
let attempt number request workload = { Attempt = number; Request = request; ResponseStatus = Some 200; RateOutcome = "remaining-observed"; WorkloadCall = workload }
let source id category page pageCount payload attempts =
    { SourceId = id; Category = category; Repository = "FS-GG/FS.GG.Coordination"; Workflow = None; TestedHead = None
      RunId = None; RunAttempt = None; AttemptCount = None; Page = page; PageCount = pageCount
      EventAt = None; IngestedAt = None; QueuedAt = None; StartedAt = None; EndedAt = None
      RawPayload = payload; RawPayloadSha256 = shaText payload; ApiAttempts = attempts }
let hint id subject revision kind operation arrived state sourceId =
    { HintId = id; Subject = subject; Revision = revision; Kind = kind; OperationId = operation
      ArrivedAt = arrived; State = state; SourceId = sourceId }
let contract = json "evidence/github-substrate-v2/gs2-07-7/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.github-event-benefit-evidence/v1" || text "unit" c <> "GS2-07.7" then failwith "contract identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-07.6.json" <> text "prerequisiteFileSha256" c then failwith "accepted predecessor bytes differ"
if shaFile "evidence/github-substrate-v2/gs2-07-7/provider-observation.json" <> "4db6b0ab1ab32e22186890a988a469fa14c0c8992788fd11ef7d29e3fd9df06b" then failwith "provider observation bytes differ"
if shaFile "evidence/github-substrate-v2/gs2-07-6/routine-burst.json" <> "4ba0dbd50779e59216733e2ead36097f31a11147169831330fbe21bddaba2afb" then failwith "historical evidence bytes differ"
if shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" <> text "protocolSha256" c then failwith "canonical protocol changed"

let provider1Payload = "{\"id\":34176802894,\"name\":\"Bootstrap qualification\",\"path\":\".github/workflows/bootstrap-qualification.yml\",\"head_sha\":\"a8b10e073eb7098014ea38ce6edadc35b784ff5c\",\"event\":\"push\",\"status\":\"completed\",\"conclusion\":\"success\",\"run_attempt\":1,\"created_at\":\"2026-09-08T01:29:10Z\",\"run_started_at\":\"2026-09-08T01:29:10Z\",\"updated_at\":\"2026-09-08T01:30:21Z\",\"native\":\"unknown\"}"
let provider2Payload = "{\"id\":34176802484,\"name\":\"Push on main\",\"path\":\"dynamic/github-code-scanning/codeql\",\"head_sha\":\"a8b10e073eb7098014ea38ce6edadc35b784ff5c\",\"event\":\"dynamic\",\"status\":\"completed\",\"conclusion\":\"success\",\"run_attempt\":1,\"created_at\":\"2026-09-08T01:29:10Z\",\"run_started_at\":\"2026-09-08T01:29:10Z\",\"updated_at\":\"2026-09-08T01:29:52Z\",\"native\":\"unknown\"}"
let provider1 =
    { source "current-registration-runs" "current-provider-observation" 1 2 provider1Payload [ attempt 1 "GET /actions/runs/34176802894" false ] with
        Workflow = Some "bootstrap-qualification"; TestedHead = Some "a8b10e073eb7098014ea38ce6edadc35b784ff5c"; RunId = Some 34176802894L
        RunAttempt = Some 1; AttemptCount = Some 1; StartedAt = Some "2026-09-08T01:29:10Z"; EndedAt = Some "2026-09-08T01:30:21Z" }
let provider2 =
    { source "current-registration-runs" "current-provider-observation" 2 2 provider2Payload [ attempt 1 "GET /actions/runs/34176802484" false ] with
        Workflow = Some "CodeQL"; TestedHead = Some "a8b10e073eb7098014ea38ce6edadc35b784ff5c"; RunId = Some 34176802484L
        RunAttempt = Some 1; AttemptCount = Some 1; StartedAt = Some "2026-09-08T01:29:10Z"; EndedAt = Some "2026-09-08T01:29:52Z" }
let historicalPayload = "routine-burst-sha256=4ba0dbd50779e59216733e2ead36097f31a11147169831330fbe21bddaba2afb;native=delivered"
let historical = source "routine-burst" "historical-provider-evidence" 1 1 historicalPayload [ attempt 1 "retained routine-burst read" false ]
let replayPayload = "population=issue:A,issue:B,issue:C;full-scan-calls=8;narrow-calls=2;native=refused"
let replaySource =
    { source "bounded-replay" "executable-replay" 1 1 replayPayload [ attempt 1 "reconcile issue:A" true; attempt 2 "reconcile issue:B" true ] with
        TestedHead = Some "a8b10e073eb7098014ea38ce6edadc35b784ff5c"; EventAt = Some "2026-09-08T01:10:00Z"; IngestedAt = Some "2026-09-08T01:10:02Z" }
let negativePayload = "withheld=issue:C;audit=scheduled;native=unknown"
let negativeSource = { source "withheld-C" "injected-negative-control" 1 1 negativePayload [] with TestedHead = Some "a8b10e073eb7098014ea38ce6edadc35b784ff5c" }
let facts =
    { Unit = "GS2-07.7"; PrerequisiteReceiptSha256 = prerequisiteReceiptSha256; RoadmapRevision = roadmapRevision
      RoadmapSha256 = roadmapSha256; CandidateHead = "a8b10e073eb7098014ea38ce6edadc35b784ff5c"
      Population = [ "issue:A"; "issue:B"; "issue:C" ]; WindowStart = "2026-09-08T01:00:00Z"; WindowEnd = "2026-09-08T02:00:00Z"
      Sources = [ provider1; provider2; historical; replaySource; negativeSource ]
      Hints =
        [ hint "A-1" "issue:A" 1L "reconcile" "reconcile-A-1" "2026-09-08T01:11:00Z" "applied" "bounded-replay"
          hint "A-2" "issue:A" 2L "reconcile" "reconcile-A-2" "2026-09-08T01:12:00Z" "pending" "bounded-replay"
          hint "A-2-duplicate" "issue:A" 2L "reconcile" "reconcile-A-2" "2026-09-08T01:12:01Z" "pending" "bounded-replay"
          hint "A-2-reordered" "issue:A" 2L "reconcile" "reconcile-A-2" "2026-09-08T01:11:59Z" "pending" "bounded-replay"
          hint "A-approval" "issue:A" 2L "approval-change" "approval-A-2" "2026-09-08T01:12:02Z" "applied" "bounded-replay"
          hint "A-grant" "issue:A" 2L "grant-change" "grant-A-2" "2026-09-08T01:12:03Z" "applied" "bounded-replay"
          hint "A-command" "issue:A" 2L "semantic-command" "command-A-2" "2026-09-08T01:12:04Z" "pending" "bounded-replay"
          hint "A-dispatch" "issue:A" 2L "non-idempotent-command" "dispatch-A-once" "2026-09-08T01:12:05Z" "applying" "bounded-replay"
          hint "B-1" "issue:B" 1L "reconcile" "reconcile-B-1" "2026-09-08T01:13:00Z" "pending" "bounded-replay" ]
      AuditObservations =
        [ { AuditId = "audit-C"; Subject = "issue:C"; Revision = 1L; ScheduledAt = "2026-09-08T01:30:00Z"
            DiscoveredAt = "2026-09-08T01:31:00Z"; ConvergedAt = "2026-09-08T01:32:00Z"
            SourceId = "withheld-C"; InjectedWithheldHint = true } ]
      FullScanApiCalls = metric 8L; FullScanSchedules = metric 3L; HostedClaim = None }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = compile facts |> get
let baselineGreen = parse (serialize baseline) = Ok baseline && verify baseline.Seal baseline = Ok baseline && replay baseline facts = Ok baseline
let sourceText = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventBenefitQualification.fs"
let cliText = read "src/FS.GG.Coordination.Cli/Program.fs"
let noMutation (value: string) = not(Regex.IsMatch(value, "HttpClient|Octokit|GitHubClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase))
let retained relative = let doc = json relative in strings "controls" doc.RootElement, strings "cases" doc.RootElement, text "caseContract" doc.RootElement
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-7/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-7/independent-controls.json"
if generatedIds <> requiredControls || independentIds <> requiredControls then failwith "retained control identities differ"
if generatedCases.Length <> requiredControls.Length || independentCases.Length <> requiredControls.Length then failwith "retained control cases incomplete"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship not independent"
let replaceSource original replacement = facts.Sources |> List.map (fun source -> if obj.ReferenceEquals(source, original) then replacement else source)
let hostedIdentity head windowStart windowEnd =
    { Repository = "FS-GG/FS.GG.Coordination"; Workflow = "measurement"; RunId = 1L; RunAttempt = 1; TestedHead = head
      WindowStart = windowStart; WindowEnd = windowEnd; PopulationDigest = baseline.PopulationDigest; SourceDigests = baseline.SourceDigests }
let generatedMutation control =
    match control with
    | "prerequisite" -> shaFile "evidence/github-substrate-v2/accepted/GS2-07.6.json" = text "prerequisiteFileSha256" c
    | "roadmap" -> text "roadmapSha256" c = roadmapSha256
    | "command-identity" -> let catalog = read "eng/github-substrate-v2-gates.json" in catalog.Contains("github-event-benefit-measurement-contract") && catalog.Contains("github-event-benefit-provider-observation-contract")
    | "metric-provenance" -> baseline.SourceDigests.Length = 5 && baseline.SourceCount = 5
    | "finite-population" -> compile { facts with Population = List.rev facts.Population } |> has EventBenefitFinding.UnboundedPopulation
    | "finite-window" -> compile { facts with WindowEnd = facts.WindowStart } |> has EventBenefitFinding.UnboundedWindow
    | "source-category" -> compile { facts with Sources = facts.Sources |> List.filter (fun value -> value.Category <> "injected-negative-control") } |> has (EventBenefitFinding.UnknownSourceCategory "injected-negative-control")
    | "pagination" -> compile { facts with Sources = facts.Sources |> List.filter (fun value -> value.Page <> 2) } |> has (EventBenefitFinding.IncompletePage "current-registration-runs")
    | "run-attempt" -> compile { facts with Sources = replaceSource provider1 { provider1 with AttemptCount = Some 2 } } |> has (EventBenefitFinding.IncompleteRunAttempt provider1.SourceId)
    | "timestamp" -> compile { facts with Sources = replaceSource provider1 { provider1 with EndedAt = Some "not-time" } } |> has (EventBenefitFinding.MissingTimestamp $"{provider1.SourceId}:endedAt")
    | "call-attempt" -> compile { facts with Sources = replaceSource provider1 { provider1 with ApiAttempts = [] } } |> has (EventBenefitFinding.MissingCallAttempt provider1.SourceId)
    | "rate-outcome" -> compile { facts with Sources = replaceSource provider1 { provider1 with ApiAttempts = [ { provider1.ApiAttempts.Head with RateOutcome = "" } ] } } |> has (EventBenefitFinding.MissingRateOutcome provider1.SourceId)
    | "latency" -> baseline.EventLatencyMilliseconds.Value = Some 2000L && baseline.UnknownOutcomes |> List.exists (fun value -> value.Contains("current-registration-runs:event-timestamp-unknown"))
    | "api-cost" -> baseline.NarrowApiCallAttempts = 2 && baseline.CollectorApiCallAttempts = 3 && baseline.FullScanApiCalls.Value = Some 8L
    | "schedule-count" -> baseline.ScheduleAdmissions = 1 && baseline.FullScanSchedules.Value = Some 3L
    | "dropped-event-repair" -> baseline.RepairDelayMilliseconds.Value = Some 120000L && (compile { facts with AuditObservations = [] } |> has (EventBenefitFinding.DroppedEventUnrepaired "withheld-control"))
    | "false-outcome" -> baseline.FalseOutcomes = [ "native-refused" ]
    | "unknown-outcome" -> baseline.UnknownOutcomes |> List.exists (fun value -> value.Contains("native-outcome-unknown"))
    | "coalescing" -> let a = baseline.Subjects |> List.find (fun value -> value.Subject = "issue:A") in a.ReconcileRevision = 2L && a.HintCount = 8
    | "subject-isolation" -> baseline.Subjects |> List.exists (fun value -> value.Subject = "issue:B" && value.Outcome = "narrow-reconcile-admitted")
    | "semantic-command-preservation" -> baseline.Subjects.Head.OperationIds = [ "approval-A-2"; "command-A-2"; "dispatch-A-once"; "grant-A-2" ]
    | "in-flight-effect" -> baseline.Subjects.Head.ApplyingOperationIds = [ "dispatch-A-once" ]
    | "complete-audit-authority" -> baseline.CompleteAuditAuthority = "scheduled-complete-audit" && not baseline.OrdinaryMergeDependency
    | "retained-polling" -> baseline.PollingDecision = "retain"
    | "replay-distinction" -> not baseline.InstalledBenefit && baseline.Conclusion.Contains("no installed or production benefit")
    | "sandbox-distinction" -> not baseline.ProductionBenefit
    | "tamper" -> compile { facts with Sources = replaceSource provider1 { provider1 with RawPayloadSha256 = String.replicate 64 "0" } } |> has (EventBenefitFinding.AlteredSourceDigest provider1.SourceId)
    | "exact-head-hosted-evidence" -> compile { facts with HostedClaim = Some(hostedIdentity (String.replicate 40 "b") facts.WindowStart facts.WindowEnd) } |> has EventBenefitFinding.HostedEvidenceIncomplete
    | "no-write-permission" -> noMutation sourceText
    | "no-production-mutation" -> noMutation sourceText && not(cliText.Contains("event-benefit"))
    | "no-acceptance-claim" -> text "acceptanceReceiptPhase" c = "post-protected-merge" && baseline.HostedIdentity.IsNone
    | "no-successor-authority" -> not(sourceText.Contains("GS2-07.8")) && not(cliText.Contains("GS2-07.8"))
    | _ -> false
let independentMutation control =
    match control with
    | "prerequisite" -> let receipt = json "evidence/github-substrate-v2/accepted/GS2-07.6.json" in text "unitId" receipt.RootElement = "GS2-07.6" && shaFile "evidence/github-substrate-v2/accepted/GS2-07.6.json" = text "prerequisiteFileSha256" c
    | "roadmap" -> roadmapSha256.Length = 64 && text "roadmapRevision" c = roadmapRevision
    | "command-identity" ->
        let units = json "eng/github-substrate-v2-units.json"
        let unit = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> text "id" value = "GS2-07.7")
        let expected = unit.GetProperty("gateContracts").EnumerateArray() |> Seq.map (fun value -> text "commandSha256" value) |> Seq.toList
        expected = [ "746c067ecddac460e8424d104c78946a7ffc4f7bc1c143e2ff19897415eb6f8b"; "7c4ba2dd11e32f1431a1e313141fa6c6c4bf569d900311b08bc0f926e8f78bfc" ]
    | "metric-provenance" -> (compile { facts with Sources = List.rev facts.Sources } |> get).Seal <> baseline.Seal
    | "finite-population" -> compile { facts with Population = facts.Population @ [ facts.Population.Head ] } |> has EventBenefitFinding.UnboundedPopulation
    | "finite-window" -> compile { facts with WindowEnd = "2026-10-10T02:00:00Z" } |> has EventBenefitFinding.UnboundedWindow
    | "source-category" -> compile { facts with Sources = facts.Sources |> List.filter (fun value -> value.Category <> "historical-provider-evidence") } |> has (EventBenefitFinding.UnknownSourceCategory "historical-provider-evidence")
    | "pagination" -> compile { facts with Sources = facts.Sources |> List.filter (fun value -> value.Page <> 1 || value.SourceId <> provider1.SourceId) } |> has (EventBenefitFinding.IncompletePage provider1.SourceId)
    | "run-attempt" -> compile { facts with Sources = replaceSource provider2 { provider2 with RunAttempt = Some 2; AttemptCount = Some 2 } } |> has (EventBenefitFinding.IncompleteRunAttempt provider2.SourceId)
    | "timestamp" -> compile { facts with Sources = replaceSource provider2 { provider2 with EndedAt = Some "2026-09-08T02:00:01Z" } } |> has (EventBenefitFinding.OutsideWindow $"{provider2.SourceId}:endedAt")
    | "call-attempt" -> compile { facts with Sources = replaceSource provider2 { provider2 with ApiAttempts = [] } } |> has (EventBenefitFinding.MissingCallAttempt provider2.SourceId)
    | "rate-outcome" -> compile { facts with Sources = replaceSource provider2 { provider2 with ApiAttempts = [ { provider2.ApiAttempts.Head with RateOutcome = "" } ] } } |> has (EventBenefitFinding.MissingRateOutcome provider2.SourceId)
    | "latency" -> (DateTimeOffset.Parse("2026-09-08T01:10:02Z") - DateTimeOffset.Parse("2026-09-08T01:10:00Z")).TotalMilliseconds = 2000.0
    | "api-cost" -> facts.Sources |> List.sumBy (fun source -> source.ApiAttempts |> List.filter _.WorkloadCall |> List.length) = 2
    | "schedule-count" -> facts.AuditObservations |> List.filter _.InjectedWithheldHint |> List.length = 1
    | "dropped-event-repair" -> let collision = hint "C-collision" "issue:C" 1L "reconcile" "reconcile-C-1" "2026-09-08T01:29:00Z" "pending" "bounded-replay" in compile { facts with Hints = collision :: facts.Hints } |> has (EventBenefitFinding.DroppedEventUnrepaired "issue:C")
    | "false-outcome" -> facts.Sources |> List.exists (fun source -> source.RawPayload.Contains("native=refused", StringComparison.Ordinal))
    | "unknown-outcome" -> let cleared = { provider1 with EventAt = None; IngestedAt = None; QueuedAt = None } in (compile { facts with Sources = replaceSource provider1 cleared } |> get).UnknownOutcomes |> List.exists (fun value -> value.Contains("event-timestamp-unknown"))
    | "coalescing" -> let reversed = compile { facts with Hints = List.rev facts.Hints } |> get in reversed.Subjects.Head.ReconcileRevision = 2L && reversed.Subjects.Head.HintCount = 8
    | "subject-isolation" -> let withoutB = compile { facts with Hints = facts.Hints |> List.filter (fun hint -> hint.Subject <> "issue:B") } |> get in withoutB.Subjects |> List.exists (fun subject -> subject.Subject = "issue:B" && subject.Outcome = "unknown-no-observation")
    | "semantic-command-preservation" -> (compile { facts with Hints = List.rev facts.Hints } |> get).Subjects.Head.OperationIds = baseline.Subjects.Head.OperationIds
    | "in-flight-effect" -> let newer = hint "A-3" "issue:A" 3L "reconcile" "reconcile-A-3" "2026-09-08T01:14:00Z" "pending" "bounded-replay" in (compile { facts with Hints = newer :: facts.Hints } |> get).Subjects.Head.ApplyingOperationIds = [ "dispatch-A-once" ]
    | "complete-audit-authority" -> verify baseline.Seal { baseline with CompleteAuditAuthority = "ordinary-merge" } |> has EventBenefitFinding.UnsealedReport
    | "retained-polling" -> verify baseline.Seal { baseline with PollingDecision = "remove" } |> has EventBenefitFinding.UnsealedReport
    | "replay-distinction" -> verify baseline.Seal { baseline with InstalledBenefit = true } |> has EventBenefitFinding.ReplayAsInstalled
    | "sandbox-distinction" -> verify baseline.Seal { baseline with ProductionBenefit = true } |> has EventBenefitFinding.SandboxAsProduction
    | "tamper" -> [ { baseline with CandidateHead = String.replicate 40 "b" }; { baseline with SourceDigests = baseline.SourceDigests.Tail }; { baseline with Seal = String.replicate 64 "0" } ] |> List.forall (fun altered -> verify baseline.Seal altered |> has EventBenefitFinding.AlteredSeal)
    | "exact-head-hosted-evidence" -> compile { facts with HostedClaim = Some(hostedIdentity facts.CandidateHead "2026-09-08T01:00:01Z" facts.WindowEnd) } |> has EventBenefitFinding.HostedEvidenceIncomplete
    | "no-write-permission" -> Regex.Matches(sourceText, "HttpClient|Octokit|GitHubClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase).Count = 0
    | "no-production-mutation" -> not(cliText.Contains("event-benefit", StringComparison.OrdinalIgnoreCase)) && noMutation sourceText
    | "no-acceptance-claim" -> not(File.Exists(path "evidence/github-substrate-v2/accepted/GS2-07.7.json")) && text "acceptanceReceiptPhase" c = "post-protected-merge"
    | "no-successor-authority" ->
        let index = json "eng/github-substrate-v2-units.json"
        let ids = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.map (fun value -> text "id" value) |> Seq.toList
        ids |> List.last = "GS2-07.7" && not(List.contains "GS2-07.8" ids)
    | _ -> false
let generated: EventBenefitControlResult list =
    List.map2 (fun control caseName -> { ControlId = control; ControlPassed = generatedMutation control; BaselineGreen = baselineGreen; Evidence = $"generated:{control}:{caseName}" }) requiredControls generatedCases
let independent: EventBenefitControlResult list =
    List.map2 (fun control caseName -> { ControlId = control; ControlPassed = independentMutation control; BaselineGreen = baselineGreen; Evidence = $"independent:{control}:{caseName}" }) requiredControls independentCases
match validateControls generated independent with Ok () -> () | Error errors -> failwithf "Q3 controls failed: %A\ngenerated=%A\nindependent=%A" errors generated independent
let reportPath = path "evidence/github-substrate-v2/gs2-07-7/measurement-report.json"
let bytes = serialize baseline
if File.Exists reportPath then
    if File.ReadAllText(reportPath).TrimEnd('\r', '\n') <> bytes then failwith "retained measurement report differs"
else
    printfn "MEASUREMENT_REPORT_JSON=%s" bytes
printfn "GITHUB_EVENT_BENEFIT_MEASUREMENT_OK sources=%d hints=%d subjects=%d schedules=%d narrowCalls=%d collectorCalls=%d controls=%d seal=%s" baseline.SourceCount baseline.HintCount baseline.SubjectCount baseline.ScheduleAdmissions baseline.NarrowApiCallAttempts baseline.CollectorApiCallAttempts requiredControls.Length baseline.Seal
