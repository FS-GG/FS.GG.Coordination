namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type EventBenefitMetric = { Value: int64 option; UnknownReason: string option }
type EventBenefitApiAttempt = { Attempt: int; Request: string; ResponseStatus: int option; RateOutcome: string; WorkloadCall: bool }
type EventBenefitSource =
    { SourceId: string; Category: string; Repository: string; Workflow: string option; TestedHead: string option
      RunId: int64 option; RunAttempt: int option; AttemptCount: int option; Page: int; PageCount: int
      EventAt: string option; IngestedAt: string option; QueuedAt: string option; StartedAt: string option; EndedAt: string option
      RawPayload: string; RawPayloadSha256: string; ApiAttempts: EventBenefitApiAttempt list }
type EventBenefitHint =
    { HintId: string; Subject: string; Revision: int64; Kind: string; OperationId: string
      ArrivedAt: string; State: string; SourceId: string }
type EventBenefitAuditObservation =
    { AuditId: string; Subject: string; Revision: int64; ScheduledAt: string; DiscoveredAt: string
      ConvergedAt: string; SourceId: string; InjectedWithheldHint: bool }
type EventBenefitHostedIdentity =
    { Repository: string; Workflow: string; RunId: int64; RunAttempt: int; TestedHead: string
      WindowStart: string; WindowEnd: string; PopulationDigest: string; SourceDigests: string list }
type EventBenefitFacts =
    { Unit: string; PrerequisiteReceiptSha256: string; RoadmapRevision: string; RoadmapSha256: string
      CandidateHead: string; Population: string list; WindowStart: string; WindowEnd: string
      Sources: EventBenefitSource list; Hints: EventBenefitHint list; AuditObservations: EventBenefitAuditObservation list
      FullScanApiCalls: EventBenefitMetric; FullScanSchedules: EventBenefitMetric; HostedClaim: EventBenefitHostedIdentity option }
type EventBenefitSubjectResult =
    { Subject: string; ReconcileRevision: int64; HintCount: int; ScheduledCount: int
      OperationIds: string list; ApplyingOperationIds: string list; Outcome: string }
type EventBenefitReport =
    { SchemaVersion: int; Unit: string; PrerequisiteReceiptSha256: string; RoadmapRevision: string; RoadmapSha256: string
      CandidateHead: string; Population: string list; PopulationDigest: string; WindowStart: string; WindowEnd: string
      SourceCategories: string list; SourceDigests: string list; SourceCount: int; PageCount: int; RunAttemptCount: int
      HintCount: int; SubjectCount: int; ScheduleAdmissions: int; NarrowApiCallAttempts: int; CollectorApiCallAttempts: int
      FullScanApiCalls: EventBenefitMetric; FullScanSchedules: EventBenefitMetric; EventLatencyMilliseconds: EventBenefitMetric
      RepairDelayMilliseconds: EventBenefitMetric; Subjects: EventBenefitSubjectResult list; DeliveryOutcomes: string list
      FalseOutcomes: string list; UnknownOutcomes: string list; Coverage: string list; Limits: string list
      CompleteAuditAuthority: string; OrdinaryMergeDependency: bool; PollingDecision: string; InstalledBenefit: bool
      ProductionBenefit: bool; HostedIdentity: EventBenefitHostedIdentity option; Conclusion: string; Seal: string }
[<RequireQualifiedAccess>]
type EventBenefitFinding =
    | MissingField of string | MalformedField of string | ChangedPrerequisite | ChangedRoadmap | SubstitutedHead
    | UnboundedPopulation | UnboundedWindow | UnknownSourceCategory of string | IncompletePage of string
    | IncompleteRunAttempt of string | MissingTimestamp of string | OutsideWindow of string | MissingCallAttempt of string
    | MissingRateOutcome of string | AlteredSourceDigest of string | ContradictoryRevision of string
    | UnsupportedHint of string | LostSemanticCommand of string | LostDistinctSubject of string
    | CancelledInFlightEffect of string | DroppedEventUnrepaired of string | HostedEvidenceIncomplete
    | ReplayAsInstalled | SandboxAsProduction | UnsealedReport | AlteredSeal | ReplayConflict
    | InvalidSerialization of string
type EventBenefitControlResult = { ControlId: string; ControlPassed: bool; BaselineGreen: bool; Evidence: string }

module GitHubEventBenefitQualification =
    let prerequisiteReceiptSha256 = "eaf032038cc3ed1fb3f1a21db81a32f7af7969f84a0d9b77cd1d7eea68346bc6"
    let roadmapRevision = "7216ec4aae14b17f151a1ed3616eb8a2f4ed2d47"
    let roadmapSha256 = "0498209c27cdf75d3c1067dad2c3b88084b03c20f1bd87dcb99192aa43457c36"
    let sourceCategories = [ "current-provider-observation"; "historical-provider-evidence"; "executable-replay"; "injected-negative-control" ]
    let requiredControls =
        [ "prerequisite"; "roadmap"; "command-identity"; "metric-provenance"; "finite-population"; "finite-window"
          "source-category"; "pagination"; "run-attempt"; "timestamp"; "call-attempt"; "rate-outcome"; "latency"
          "api-cost"; "schedule-count"; "dropped-event-repair"; "false-outcome"; "unknown-outcome"; "coalescing"
          "subject-isolation"; "semantic-command-preservation"; "in-flight-effect"; "complete-audit-authority"
          "retained-polling"; "replay-distinction"; "sandbox-distinction"; "tamper"; "exact-head-hosted-evidence"
          "no-write-permission"; "no-production-mutation"; "no-acceptance-claim"; "no-successor-authority" ]

    let private shaPattern = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private tokenPattern = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private hashBytes (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private hashText (value: string) = value |> Encoding.UTF8.GetBytes |> hashBytes
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private populationDigest values = values |> List.map frame |> String.concat "" |> hashText
    let private known value = { Value = Some value; UnknownReason = None }
    let private unknown reason = { Value = None; UnknownReason = Some reason }
    let private validMetric metric =
        match metric.Value, metric.UnknownReason with
        | Some value, None when value >= 0L -> true
        | None, Some reason when not(String.IsNullOrWhiteSpace reason) -> true
        | _ -> false
    let private timestamp (value: string) =
        match DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, parsed -> Some parsed
        | _ -> None
    let private optionTimestamp = function Some value -> timestamp value | None -> None
    let private meanMetric (values: int64 list) reason = if values.IsEmpty then unknown reason else known (values |> List.sum |> fun total -> total / int64 values.Length)
    let private sourceOutcome (source: EventBenefitSource) =
        [ "delivered"; "refused"; "unknown" ]
        |> List.tryFind (fun value -> source.RawPayload.Contains($"native={value}", StringComparison.Ordinal))
        |> Option.defaultValue "unknown"
    let private reportBytes (report: EventBenefitReport) = JsonSerializer.Serialize(report, jsonOptions)
    let private sealOf (report: EventBenefitReport) = reportBytes { report with Seal = "" } |> hashText

    let compile (facts: EventBenefitFacts) =
        let errors = ResizeArray<EventBenefitFinding>()
        let requireText name value =
            if String.IsNullOrWhiteSpace value then errors.Add(EventBenefitFinding.MissingField name)
            elif not(tokenPattern.IsMatch value) then errors.Add(EventBenefitFinding.MalformedField name)
        if facts.Unit <> "GS2-07.7" then errors.Add(EventBenefitFinding.MalformedField "unit")
        if facts.PrerequisiteReceiptSha256 <> prerequisiteReceiptSha256 then errors.Add EventBenefitFinding.ChangedPrerequisite
        if facts.RoadmapRevision <> roadmapRevision || facts.RoadmapSha256 <> roadmapSha256 then errors.Add EventBenefitFinding.ChangedRoadmap
        if not(shaPattern.IsMatch facts.CandidateHead) then errors.Add EventBenefitFinding.SubstitutedHead
        let population = facts.Population |> List.distinct |> List.sort
        if population.IsEmpty || population <> facts.Population then errors.Add EventBenefitFinding.UnboundedPopulation
        population |> List.iter (requireText "population")
        let windowStart, windowEnd = timestamp facts.WindowStart, timestamp facts.WindowEnd
        match windowStart, windowEnd with
        | Some startAt, Some endAt when startAt < endAt && (endAt - startAt).TotalDays <= 31.0 -> ()
        | _ -> errors.Add EventBenefitFinding.UnboundedWindow
        let withinWindow label value =
            match windowStart, windowEnd, timestamp value with
            | Some startAt, Some endAt, Some observed when startAt <= observed && observed <= endAt -> ()
            | _, _, Some _ -> errors.Add(EventBenefitFinding.OutsideWindow label)
            | _ -> errors.Add(EventBenefitFinding.MissingTimestamp label)
        if not(validMetric facts.FullScanApiCalls) then errors.Add(EventBenefitFinding.MalformedField "fullScanApiCalls")
        if not(validMetric facts.FullScanSchedules) then errors.Add(EventBenefitFinding.MalformedField "fullScanSchedules")
        if facts.Sources.IsEmpty then errors.Add(EventBenefitFinding.MissingField "sources")
        for category in sourceCategories do
            if facts.Sources |> List.exists (fun source -> source.Category = category) |> not then errors.Add(EventBenefitFinding.UnknownSourceCategory category)
        let sourceIds = facts.Sources |> List.map _.SourceId |> Set.ofList
        for source in facts.Sources do
            requireText "sourceId" source.SourceId
            requireText "repository" source.Repository
            if not(sourceCategories |> List.contains source.Category) then errors.Add(EventBenefitFinding.UnknownSourceCategory source.Category)
            match source.TestedHead with
            | Some head when not(shaPattern.IsMatch head) -> errors.Add EventBenefitFinding.SubstitutedHead
            | Some head when source.Category <> "historical-provider-evidence" && head <> facts.CandidateHead -> errors.Add EventBenefitFinding.SubstitutedHead
            | None when source.Category = "current-provider-observation" || source.Category = "executable-replay" || source.Category = "injected-negative-control" ->
                errors.Add(EventBenefitFinding.MissingField $"testedHead:{source.SourceId}")
            | _ -> ()
            if source.Page <= 0 || source.PageCount <= 0 || source.Page > source.PageCount then errors.Add(EventBenefitFinding.IncompletePage source.SourceId)
            if String.IsNullOrEmpty source.RawPayload then errors.Add(EventBenefitFinding.MissingField $"rawPayload:{source.SourceId}")
            if not(digestPattern.IsMatch source.RawPayloadSha256) || hashText source.RawPayload <> source.RawPayloadSha256 then errors.Add(EventBenefitFinding.AlteredSourceDigest source.SourceId)
            match source.RunId, source.RunAttempt, source.AttemptCount with
            | None, None, None -> ()
            | Some runId, Some attempt, Some count when runId > 0L && attempt > 0 && count > 0 && attempt <= count -> ()
            | _ -> errors.Add(EventBenefitFinding.IncompleteRunAttempt source.SourceId)
            if source.Category = "current-provider-observation" || source.Category = "historical-provider-evidence" then
                if source.ApiAttempts.IsEmpty then errors.Add(EventBenefitFinding.MissingCallAttempt source.SourceId)
                for attempt in source.ApiAttempts do
                    if attempt.Attempt <= 0 || String.IsNullOrWhiteSpace attempt.Request then errors.Add(EventBenefitFinding.MissingCallAttempt source.SourceId)
                    if String.IsNullOrWhiteSpace attempt.RateOutcome then errors.Add(EventBenefitFinding.MissingRateOutcome source.SourceId)
            for name, value in [ "eventAt", source.EventAt; "ingestedAt", source.IngestedAt; "queuedAt", source.QueuedAt; "startedAt", source.StartedAt; "endedAt", source.EndedAt ] do
                match value with Some raw -> withinWindow $"{source.SourceId}:{name}" raw | None -> ()
        facts.Sources
        |> List.groupBy (fun source -> source.SourceId)
        |> List.iter (fun (sourceId, pages) ->
            let counts = pages |> List.map _.PageCount |> Set.ofList
            let observed = pages |> List.map _.Page |> Set.ofList
            if counts.Count <> 1 || observed <> Set.ofList [ 1 .. pages.Head.PageCount ] then errors.Add(EventBenefitFinding.IncompletePage sourceId))
        facts.Sources
        |> List.choose (fun source -> source.RunId |> Option.map (fun runId -> (source.SourceId, runId), source))
        |> List.groupBy fst
        |> List.iter (fun ((sourceId, _), rows) ->
            let sources = rows |> List.map snd
            let counts = sources |> List.choose _.AttemptCount |> Set.ofList
            let attempts = sources |> List.choose _.RunAttempt |> Set.ofList
            if counts.Count <> 1 || attempts <> Set.ofList [ 1 .. sources.Head.AttemptCount.Value ] then errors.Add(EventBenefitFinding.IncompleteRunAttempt sourceId))

        let allowedKinds = [ "reconcile"; "approval-change"; "grant-change"; "semantic-command"; "non-idempotent-command" ]
        for hint in facts.Hints do
            requireText "hintId" hint.HintId; requireText "subject" hint.Subject; requireText "operationId" hint.OperationId
            if hint.Revision <= 0L || not(population |> List.contains hint.Subject) then errors.Add(EventBenefitFinding.ContradictoryRevision hint.Subject)
            if not(allowedKinds |> List.contains hint.Kind) then errors.Add(EventBenefitFinding.UnsupportedHint hint.HintId)
            if not(sourceIds.Contains hint.SourceId) then errors.Add(EventBenefitFinding.MissingField $"hintSource:{hint.HintId}")
            withinWindow hint.HintId hint.ArrivedAt
            if not([ "pending"; "applying"; "applied" ] |> List.contains hint.State) then errors.Add(EventBenefitFinding.MalformedField $"hintState:{hint.HintId}")
        facts.Hints
        |> List.groupBy (fun hint -> hint.Subject, hint.Revision)
        |> List.iter (fun ((subject, _), hints) ->
            let reconciling = hints |> List.filter (fun hint -> hint.Kind = "reconcile")
            if reconciling |> List.map _.OperationId |> Set.ofList |> Set.count > 1 then errors.Add(EventBenefitFinding.ContradictoryRevision subject))

        for audit in facts.AuditObservations do
            requireText "auditId" audit.AuditId; requireText "subject" audit.Subject
            if audit.Revision <= 0L || not(population |> List.contains audit.Subject) then errors.Add(EventBenefitFinding.ContradictoryRevision audit.Subject)
            if not(sourceIds.Contains audit.SourceId) then errors.Add(EventBenefitFinding.MissingField $"auditSource:{audit.AuditId}")
            let times = [ timestamp audit.ScheduledAt; timestamp audit.DiscoveredAt; timestamp audit.ConvergedAt ]
            if times |> List.exists Option.isNone then errors.Add(EventBenefitFinding.MissingTimestamp audit.AuditId)
            for name, value in [ "scheduledAt", audit.ScheduledAt; "discoveredAt", audit.DiscoveredAt; "convergedAt", audit.ConvergedAt ] do
                withinWindow $"{audit.AuditId}:{name}" value
            match times with
            | [ Some scheduled; Some discovered; Some converged ] when scheduled <= discovered && discovered <= converged -> ()
            | _ -> errors.Add(EventBenefitFinding.DroppedEventUnrepaired audit.Subject)
            if audit.InjectedWithheldHint then
                let source = facts.Sources |> List.tryFind (fun source -> source.SourceId = audit.SourceId)
                if source |> Option.forall (fun value -> value.Category <> "injected-negative-control") then errors.Add(EventBenefitFinding.DroppedEventUnrepaired audit.Subject)
                if facts.Hints |> List.exists (fun hint -> hint.Subject = audit.Subject && hint.Revision >= audit.Revision) then errors.Add(EventBenefitFinding.DroppedEventUnrepaired audit.Subject)
        if facts.AuditObservations |> List.exists _.InjectedWithheldHint |> not then errors.Add(EventBenefitFinding.DroppedEventUnrepaired "withheld-control")

        let popDigest = populationDigest population
        match facts.HostedClaim with
        | None -> ()
        | Some hosted ->
            let sourceDigests = facts.Sources |> List.map _.RawPayloadSha256 |> List.distinct |> List.sort
            if hosted.Repository <> "FS-GG/FS.GG.Coordination" || String.IsNullOrWhiteSpace hosted.Workflow
               || hosted.RunId <= 0L || hosted.RunAttempt <= 0 || hosted.TestedHead <> facts.CandidateHead
               || hosted.WindowStart <> facts.WindowStart || hosted.WindowEnd <> facts.WindowEnd
               || hosted.PopulationDigest <> popDigest || hosted.SourceDigests <> sourceDigests then errors.Add EventBenefitFinding.HostedEvidenceIncomplete

        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let subjects =
                population
                |> List.map (fun subject ->
                    let hints = facts.Hints |> List.filter (fun hint -> hint.Subject = subject)
                    let audits = facts.AuditObservations |> List.filter (fun audit -> audit.Subject = subject)
                    let revision =
                        [ yield! hints |> List.map _.Revision; yield! audits |> List.map _.Revision ]
                        |> function [] -> 0L | values -> List.max values
                    let operations =
                        hints
                        |> List.filter (fun hint -> hint.Kind <> "reconcile")
                        |> List.map _.OperationId
                        |> List.distinct
                        |> List.sort
                    let applying = hints |> List.filter (fun hint -> hint.State = "applying") |> List.map _.OperationId |> List.distinct |> List.sort
                    let outcome =
                        if audits |> List.exists _.InjectedWithheldHint then "audit-repaired-injected"
                        elif hints.IsEmpty then "unknown-no-observation"
                        else "narrow-reconcile-admitted"
                    { Subject = subject; ReconcileRevision = revision; HintCount = hints.Length; ScheduledCount = audits.Length
                      OperationIds = operations; ApplyingOperationIds = applying; Outcome = outcome })
            let delivery = facts.Sources |> List.map sourceOutcome
            let falseOutcomes = delivery |> List.filter ((=) "refused") |> List.map (fun value -> $"native-{value}")
            let timestampUnknowns =
                facts.Sources
                |> List.collect (fun source ->
                    [ "event", source.EventAt; "ingestion", source.IngestedAt; "queue", source.QueuedAt; "start", source.StartedAt; "end", source.EndedAt ]
                    |> List.choose (fun (name, value) -> if value.IsNone then Some $"{source.SourceId}:{name}-timestamp-unknown" else None))
            let metricUnknowns =
                [ "full-scan-api-calls", facts.FullScanApiCalls; "full-scan-schedules", facts.FullScanSchedules ]
                |> List.choose (fun (name, metric) -> metric.UnknownReason |> Option.map (fun reason -> $"{name}:{reason}"))
            let unknownOutcomes =
                [ yield! delivery |> List.mapi (fun index value -> index, value) |> List.choose (fun (index, value) -> if value = "unknown" then Some $"source-{index + 1}:native-outcome-unknown" else None)
                  yield! timestampUnknowns; yield! metricUnknowns ] |> List.distinct |> List.sort
            let eventLatencies =
                facts.Sources
                |> List.choose (fun source ->
                    match optionTimestamp source.EventAt, optionTimestamp source.IngestedAt with
                    | Some eventAt, Some ingestedAt when ingestedAt >= eventAt -> Some(int64 (ingestedAt - eventAt).TotalMilliseconds)
                    | _ -> None)
            let repairDelays =
                facts.AuditObservations
                |> List.filter _.InjectedWithheldHint
                |> List.choose (fun audit -> match timestamp audit.ScheduledAt, timestamp audit.ConvergedAt with Some startAt, Some endAt -> Some(int64 (endAt - startAt).TotalMilliseconds) | _ -> None)
            let workloadCalls = facts.Sources |> List.sumBy (fun source -> source.ApiAttempts |> List.filter _.WorkloadCall |> List.length)
            let collectorCalls = facts.Sources |> List.sumBy (fun source -> source.ApiAttempts |> List.filter (fun attempt -> not attempt.WorkloadCall) |> List.length)
            let sourceDigests = facts.Sources |> List.map _.RawPayloadSha256 |> List.distinct |> List.sort
            let benefit = facts.FullScanApiCalls.Value |> Option.exists (fun baseline -> baseline > int64 workloadCalls)
            let conclusion = if benefit then "bounded replay benefit demonstrated; no installed or production benefit established; retain polling" else "no demonstrated benefit; retain polling"
            let provisional =
                { SchemaVersion = 1; Unit = facts.Unit; PrerequisiteReceiptSha256 = facts.PrerequisiteReceiptSha256
                  RoadmapRevision = facts.RoadmapRevision; RoadmapSha256 = facts.RoadmapSha256; CandidateHead = facts.CandidateHead
                  Population = population; PopulationDigest = popDigest; WindowStart = facts.WindowStart; WindowEnd = facts.WindowEnd
                  SourceCategories = sourceCategories
                  SourceDigests = sourceDigests; SourceCount = facts.Sources.Length; PageCount = facts.Sources.Length
                  RunAttemptCount = facts.Sources |> List.choose _.RunAttempt |> List.length; HintCount = facts.Hints.Length
                  SubjectCount = subjects.Length; ScheduleAdmissions = facts.AuditObservations.Length
                  NarrowApiCallAttempts = workloadCalls; CollectorApiCallAttempts = collectorCalls
                  FullScanApiCalls = facts.FullScanApiCalls; FullScanSchedules = facts.FullScanSchedules
                  EventLatencyMilliseconds = meanMetric eventLatencies "event/ingestion timestamps unavailable"
                  RepairDelayMilliseconds = meanMetric repairDelays "injected repair timestamps unavailable"
                  Subjects = subjects; DeliveryOutcomes = delivery; FalseOutcomes = falseOutcomes; UnknownOutcomes = unknownOutcomes
                  Coverage = [ "finite-declared-population"; "finite-declared-window"; "complete-pagination"; "complete-run-attempts"; "narrow-reconciliation"; "same-subject-coalescing"; "subject-isolation"; "scheduled-complete-audit-repair" ]
                  Limits = [ "bounded observation only"; "provider prices unavailable"; "replay duration is not provider dispatch latency"; "no installed event worker observed"; "section-7.4 attribution incomplete" ]
                  CompleteAuditAuthority = "scheduled-complete-audit"; OrdinaryMergeDependency = false; PollingDecision = "retain"
                  InstalledBenefit = false; ProductionBenefit = false; HostedIdentity = facts.HostedClaim; Conclusion = conclusion; Seal = "" }
            Ok { provisional with Seal = sealOf provisional }

    let serialize report = reportBytes report

    let verify expectedSeal report =
        let errors = ResizeArray<EventBenefitFinding>()
        if report.SchemaVersion <> 1 || report.Unit <> "GS2-07.7" then errors.Add(EventBenefitFinding.InvalidSerialization "identity")
        if report.PrerequisiteReceiptSha256 <> prerequisiteReceiptSha256 then errors.Add EventBenefitFinding.ChangedPrerequisite
        if report.RoadmapRevision <> roadmapRevision || report.RoadmapSha256 <> roadmapSha256 then errors.Add EventBenefitFinding.ChangedRoadmap
        if report.Population.IsEmpty || report.Population <> (report.Population |> List.distinct |> List.sort) then errors.Add EventBenefitFinding.UnboundedPopulation
        if report.SourceCategories <> sourceCategories then errors.Add(EventBenefitFinding.UnknownSourceCategory "inventory")
        if report.CompleteAuditAuthority <> "scheduled-complete-audit" || report.OrdinaryMergeDependency then errors.Add EventBenefitFinding.UnsealedReport
        if report.PollingDecision <> "retain" then errors.Add EventBenefitFinding.UnsealedReport
        if report.InstalledBenefit then errors.Add EventBenefitFinding.ReplayAsInstalled
        if report.ProductionBenefit then errors.Add EventBenefitFinding.SandboxAsProduction
        let actual = sealOf report
        if report.Seal <> actual || report.Seal <> expectedSeal then errors.Add EventBenefitFinding.AlteredSeal
        if errors.Count = 0 then Ok report else Error(List.ofSeq errors)

    let parse (value: string) =
        try
            let report = JsonSerializer.Deserialize<EventBenefitReport>(value, jsonOptions)
            if isNull(box report) then Error [ EventBenefitFinding.InvalidSerialization "null" ]
            else
                match verify report.Seal report with
                | Ok parsed when serialize parsed = value.TrimEnd('\r', '\n') -> Ok parsed
                | Ok _ -> Error [ EventBenefitFinding.InvalidSerialization "non-canonical bytes" ]
                | Error errors -> Error errors
        with error -> Error [ EventBenefitFinding.InvalidSerialization error.Message ]

    let replay prior facts =
        match compile facts with
        | Ok next when next = prior -> Ok prior
        | Ok _ -> Error [ EventBenefitFinding.ReplayConflict ]
        | Error errors -> Error errors

    let validateControls generated independent =
        let validate label rows =
            [ if rows |> List.map _.ControlId <> requiredControls then yield $"{label} control inventory differs"
              if rows |> List.exists (fun row -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed"
              if rows |> List.exists (fun row -> String.IsNullOrWhiteSpace row.Evidence || not(row.Evidence.Contains(row.ControlId, StringComparison.Ordinal))) then yield $"{label} control evidence is unbound" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if (generated |> List.map _.ControlId) <> (independent |> List.map _.ControlId) then Error [ "control identities differ" ]
        elif List.zip generated independent |> List.exists (fun (left, right) -> left.Evidence = right.Evidence) then Error [ "control evidence is not independently identified" ]
        elif errors.IsEmpty then Ok () else Error errors
