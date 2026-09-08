module FS.GG.Coordination.GitHubEventBenefitTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventBenefitQualification

let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private metric value = { Value = Some value; UnknownReason = None }
let private attempt number request workload = { Attempt = number; Request = request; ResponseStatus = Some 200; RateOutcome = "remaining=4999"; WorkloadCall = workload }
let private source id category page pageCount payload attempts =
    { SourceId = id; Category = category; Repository = "FS-GG/FS.GG.Coordination"; Workflow = None; TestedHead = None
      RunId = None; RunAttempt = None; AttemptCount = None; Page = page; PageCount = pageCount
      EventAt = None; IngestedAt = None; QueuedAt = None; StartedAt = None; EndedAt = None
      RawPayload = payload; RawPayloadSha256 = sha payload; ApiAttempts = attempts }
let private hint id subject revision kind operation state sourceId =
    { HintId = id; Subject = subject; Revision = revision; Kind = kind; OperationId = operation
      ArrivedAt = $"2026-09-08T00:0{revision}:00Z"; State = state; SourceId = sourceId }
let private baselineFacts () =
    let current1 =
        { source "current-registration-runs" "current-provider-observation" 1 2 "page=1;native=delivered" [ attempt 1 "GET /actions/runs/34176802894/attempts/1" false ] with
            Workflow = Some "bootstrap-qualification"; TestedHead = Some(String.replicate 40 "a"); RunId = Some 34176802894L
            RunAttempt = Some 1; AttemptCount = Some 1; EventAt = Some "2026-09-08T00:00:00Z"; IngestedAt = Some "2026-09-08T00:00:02Z"
            QueuedAt = Some "2026-09-08T00:00:03Z"; StartedAt = Some "2026-09-08T00:00:04Z"; EndedAt = Some "2026-09-08T00:01:00Z" }
    let current2 = { current1 with Page = 2; RawPayload = "page=2;native=delivered"; RawPayloadSha256 = sha "page=2;native=delivered" }
    let historical = source "routine-burst" "historical-provider-evidence" 1 1 "routine-burst;native=delivered" [ attempt 1 "GET retained routine-burst" false ]
    let replay = { source "bounded-replay" "executable-replay" 1 1 "same-workload;native=refused" [ attempt 1 "replay reconcile A" true; attempt 2 "replay reconcile B" true ] with TestedHead = Some(String.replicate 40 "a") }
    let injected = { source "withheld-C" "injected-negative-control" 1 1 "withheld=C;native=unknown" [] with TestedHead = Some(String.replicate 40 "a") }
    { Unit = "GS2-07.7"; PrerequisiteReceiptSha256 = prerequisiteReceiptSha256; RoadmapRevision = roadmapRevision
      RoadmapSha256 = roadmapSha256; CandidateHead = String.replicate 40 "a"; Population = [ "issue:A"; "issue:B"; "issue:C" ]
      WindowStart = "2026-09-08T00:00:00Z"; WindowEnd = "2026-09-08T01:00:00Z"
      Sources = [ current1; current2; historical; replay; injected ]
      Hints =
        [ hint "A-1" "issue:A" 1L "reconcile" "reconcile-A-1" "applied" "bounded-replay"
          hint "A-2" "issue:A" 2L "reconcile" "reconcile-A-2" "pending" "bounded-replay"
          hint "A-2-duplicate" "issue:A" 2L "reconcile" "reconcile-A-2" "pending" "bounded-replay"
          hint "A-approval" "issue:A" 2L "approval-change" "approval-A-2" "applied" "bounded-replay"
          hint "A-dispatch" "issue:A" 2L "non-idempotent-command" "dispatch-A-once" "applying" "bounded-replay"
          hint "B-1" "issue:B" 1L "reconcile" "reconcile-B-1" "pending" "bounded-replay" ]
      AuditObservations =
        [ { AuditId = "audit-C"; Subject = "issue:C"; Revision = 1L; ScheduledAt = "2026-09-08T00:30:00Z"
            DiscoveredAt = "2026-09-08T00:31:00Z"; ConvergedAt = "2026-09-08T00:32:00Z"
            SourceId = "withheld-C"; InjectedWithheldHint = true } ]
      FullScanApiCalls = metric 8L; FullScanSchedules = metric 3L; HostedClaim = None }
let private get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let private has expected = function Error errors -> List.contains expected errors | Ok _ -> false

[<Fact>]
let ``bounded report derives comparison coalescing isolation and audit repair`` () =
    let report = baselineFacts () |> compile |> get
    Assert.Equal(6, report.HintCount)
    Assert.Equal(3, report.SubjectCount)
    Assert.Equal(2, report.NarrowApiCallAttempts)
    Assert.Equal(3, report.CollectorApiCallAttempts)
    Assert.Equal(Some 2000L, report.EventLatencyMilliseconds.Value)
    Assert.Equal(Some 120000L, report.RepairDelayMilliseconds.Value)
    let a = report.Subjects |> List.find (fun subject -> subject.Subject = "issue:A")
    let b = report.Subjects |> List.find (fun subject -> subject.Subject = "issue:B")
    let c = report.Subjects |> List.find (fun subject -> subject.Subject = "issue:C")
    Assert.Equal(2L, a.ReconcileRevision)
    Assert.Equal<string list>([ "approval-A-2"; "dispatch-A-once" ], a.OperationIds)
    Assert.Equal<string list>([ "dispatch-A-once" ], a.ApplyingOperationIds)
    Assert.Equal("narrow-reconcile-admitted", b.Outcome)
    Assert.Equal("audit-repaired-injected", c.Outcome)
    Assert.False(report.OrdinaryMergeDependency)
    Assert.Equal("scheduled-complete-audit", report.CompleteAuditAuthority)
    Assert.Equal("retain", report.PollingDecision)
    Assert.False(report.InstalledBenefit)
    Assert.False(report.ProductionBenefit)
    Assert.Contains("bounded replay benefit demonstrated", report.Conclusion)

[<Fact>]
let ``canonical report round trips and replay is exact`` () =
    let facts = baselineFacts ()
    let report = compile facts |> get
    let bytes = serialize report
    Assert.Equal(Ok report, parse bytes)
    Assert.Equal(Ok report, verify report.Seal report)
    Assert.Equal(Ok report, replay report facts)
    Assert.True(replay report { facts with FullScanSchedules = metric 4L } |> has EventBenefitFinding.ReplayConflict)

[<Fact>]
let ``population window pages attempts digests and provider accounting fail closed`` () =
    let facts = baselineFacts ()
    Assert.True(compile { facts with Population = List.rev facts.Population } |> has EventBenefitFinding.UnboundedPopulation)
    Assert.True(compile { facts with WindowEnd = facts.WindowStart } |> has EventBenefitFinding.UnboundedWindow)
    Assert.True(compile { facts with Sources = facts.Sources |> List.filter (fun source -> source.Page <> 2) } |> has (EventBenefitFinding.IncompletePage "current-registration-runs"))
    let first = facts.Sources.Head
    Assert.True(compile { facts with Sources = { first with AttemptCount = Some 2 } :: facts.Sources.Tail } |> has (EventBenefitFinding.IncompleteRunAttempt first.SourceId))
    Assert.True(compile { facts with Sources = { first with RawPayloadSha256 = String.replicate 64 "0" } :: facts.Sources.Tail } |> has (EventBenefitFinding.AlteredSourceDigest first.SourceId))
    Assert.True(compile { facts with Sources = { first with TestedHead = Some(String.replicate 40 "b") } :: facts.Sources.Tail } |> has EventBenefitFinding.SubstitutedHead)
    Assert.True(compile { facts with Sources = { first with EndedAt = Some facts.WindowEnd } :: facts.Sources.Tail } |> Result.isOk)
    Assert.True(compile { facts with Sources = { first with EndedAt = Some "2026-09-08T01:00:01Z" } :: facts.Sources.Tail } |> has (EventBenefitFinding.OutsideWindow $"{first.SourceId}:endedAt"))
    Assert.True(compile { facts with Sources = { first with ApiAttempts = [] } :: facts.Sources.Tail } |> has (EventBenefitFinding.MissingCallAttempt first.SourceId))
    let noRate = { first.ApiAttempts.Head with RateOutcome = "" }
    Assert.True(compile { facts with Sources = { first with ApiAttempts = [ noRate ] } :: facts.Sources.Tail } |> has (EventBenefitFinding.MissingRateOutcome first.SourceId))

[<Fact>]
let ``unsupported contradictory and unrepaired hints refuse`` () =
    let facts = baselineFacts ()
    let unsupported = { facts.Hints.Head with Kind = "delete" }
    Assert.True(compile { facts with Hints = unsupported :: facts.Hints.Tail } |> has (EventBenefitFinding.UnsupportedHint unsupported.HintId))
    let contradiction = { facts.Hints.Head with OperationId = "other" }
    Assert.True(compile { facts with Hints = contradiction :: facts.Hints } |> has (EventBenefitFinding.ContradictoryRevision contradiction.Subject))
    Assert.True(compile { facts with AuditObservations = [] } |> has (EventBenefitFinding.DroppedEventUnrepaired "withheld-control"))

[<Fact>]
let ``missing metrics and native observations remain unknown not zero`` () =
    let facts = baselineFacts ()
    let unknownCalls = { Value = None; UnknownReason = Some "provider counter unavailable" }
    let report = compile { facts with FullScanApiCalls = unknownCalls } |> get
    Assert.Equal(None, report.FullScanApiCalls.Value)
    Assert.Contains("full-scan-api-calls:provider counter unavailable", report.UnknownOutcomes)
    Assert.Equal("no demonstrated benefit; retain polling", report.Conclusion)
    Assert.Contains("native-refused", report.FalseOutcomes)
    Assert.Contains(report.UnknownOutcomes, fun value -> value.Contains("native-outcome-unknown"))

[<Fact>]
let ``observer loss cannot rewrite retained native delivery`` () =
    let facts = baselineFacts ()
    let withCurrentObserverLoss =
        { facts with
            Sources =
                facts.Sources
                |> List.map (fun source ->
                    if source.Category = "current-provider-observation" then
                        let payload = $"page={source.Page};native=unknown;observer=lost"
                        { source with RawPayload = payload; RawPayloadSha256 = sha payload }
                    else source) }
    let report = compile withCurrentObserverLoss |> get
    Assert.Contains("delivered", report.DeliveryOutcomes)
    Assert.Contains("refused", report.DeliveryOutcomes)
    Assert.False(report.InstalledBenefit)
    Assert.Equal("retain", report.PollingDecision)

[<Fact>]
let ``hosted identity must bind exact head window population and source digests`` () =
    let facts = baselineFacts ()
    let report = compile facts |> get
    let hosted =
        { Repository = "FS-GG/FS.GG.Coordination"; Workflow = "measurement"; RunId = 1L; RunAttempt = 1
          TestedHead = facts.CandidateHead; WindowStart = facts.WindowStart; WindowEnd = facts.WindowEnd
          PopulationDigest = report.PopulationDigest; SourceDigests = report.SourceDigests }
    Assert.True(compile { facts with HostedClaim = Some hosted } |> Result.isOk)
    Assert.True(compile { facts with HostedClaim = Some { hosted with TestedHead = String.replicate 40 "b" } } |> has EventBenefitFinding.HostedEvidenceIncomplete)

[<Fact>]
let ``tampered policy and seal cannot be accepted`` () =
    let report = baselineFacts () |> compile |> get
    Assert.True(verify report.Seal { report with PollingDecision = "remove" } |> has EventBenefitFinding.UnsealedReport)
    Assert.True(verify report.Seal { report with InstalledBenefit = true } |> has EventBenefitFinding.ReplayAsInstalled)
    Assert.True(verify report.Seal { report with ProductionBenefit = true } |> has EventBenefitFinding.SandboxAsProduction)
    Assert.True(verify report.Seal { report with Seal = String.replicate 64 "0" } |> has EventBenefitFinding.AlteredSeal)

[<Fact>]
let ``control validator requires exact independently executed inventory`` () =
    let generated: EventBenefitControlResult list = requiredControls |> List.map (fun id -> { ControlId = id; ControlPassed = true; BaselineGreen = true; Evidence = $"generated:{id}" })
    let independent: EventBenefitControlResult list = requiredControls |> List.map (fun id -> { ControlId = id; ControlPassed = true; BaselineGreen = true; Evidence = $"independent:{id}" })
    Assert.Equal(Ok (), validateControls generated independent)
    Assert.True(validateControls generated generated |> Result.isError)
    Assert.True(validateControls generated.Tail independent |> Result.isError)
    Assert.True(validateControls ({ generated.Head with ControlPassed = false } :: generated.Tail) independent |> Result.isError)
