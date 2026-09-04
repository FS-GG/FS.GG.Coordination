namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type GitHubReconciliationEvent =
    { EventKind: string; Repository: string; SourceRevision: string; SubjectKind: string; SubjectId: string
      SubjectRevision: int64; DeliveryId: string; Route: string; Origin: string; AttemptsDerivedWrite: bool }
type GitHubReconciliationQueueEntry =
    { EventKind: string; Subject: string; SubjectRevision: int64; SchedulingKey: string
      QueueReceipt: string; DeduplicationDisposition: string }
type GitHubReconciliationPlan =
    { SchemaVersion: int; Repository: string; SourceRevision: string; SupportedEventKinds: string list
      Entries: GitHubReconciliationQueueEntry list; WriterBoundary: string list; Seal: string }
[<RequireQualifiedAccess>]
type GitHubNarrowReconciliationFinding =
    | MissingField of string | MalformedField of string | UnknownEventKind of string | IncompleteEventInventory
    | CrossScope of string | StaleRevision of string | ConflictingSubject of string | AlteredRouting of string
    | DirectWrite of string | UnsealedPlan | AlteredSeal | ReplayConflict of string | InvalidSerialization of string
type GitHubNarrowReconciliationControl =
    | ReconciliationPrerequisites | ReconciliationRoadmap | ReconciliationCompleteness
    | ReconciliationEventKind | ReconciliationSubject | ReconciliationRevision | ReconciliationRouting
    | ReconciliationSchedulingKey | ReconciliationDeduplication | ReconciliationDuplicate
    | ReconciliationReorder | ReconciliationUnsupported | ReconciliationScope
    | ReconciliationExclusiveWriter | ReconciliationDirectWrite | ReconciliationSealedPlan
    | ReconciliationOrdering | ReconciliationSeal | ReconciliationReplay
    | ReconciliationQuintPreservation | ReconciliationNoNetwork
    | ReconciliationNoProductionQueue | ReconciliationNoMutation
type GitHubNarrowReconciliationControlResult =
    { Control: GitHubNarrowReconciliationControl; ControlPassed: bool; BaselineGreen: bool }

module GitHubNarrowReconciliationQualification =
    let supportedEventKinds =
        [ "issue"; "relation"; "project"; "repository"; "ruleset"; "run-check"; "release"; "installation" ]
    let writerBoundary = [ "fresh-observe"; "reduce"; "sealed-plan"; "apply"; "verify" ]
    let requiredControls =
        [ ReconciliationPrerequisites; ReconciliationRoadmap; ReconciliationCompleteness
          ReconciliationEventKind; ReconciliationSubject; ReconciliationRevision; ReconciliationRouting
          ReconciliationSchedulingKey; ReconciliationDeduplication; ReconciliationDuplicate
          ReconciliationReorder; ReconciliationUnsupported; ReconciliationScope
          ReconciliationExclusiveWriter; ReconciliationDirectWrite; ReconciliationSealedPlan
          ReconciliationOrdering; ReconciliationSeal; ReconciliationReplay
          ReconciliationQuintPreservation; ReconciliationNoNetwork
          ReconciliationNoProductionQueue; ReconciliationNoMutation ]

    let controlId = function
        | ReconciliationPrerequisites -> "reconciliation-prerequisites"
        | ReconciliationRoadmap -> "reconciliation-roadmap"
        | ReconciliationCompleteness -> "reconciliation-completeness"
        | ReconciliationEventKind -> "reconciliation-event-kind"
        | ReconciliationSubject -> "reconciliation-subject"
        | ReconciliationRevision -> "reconciliation-revision"
        | ReconciliationRouting -> "reconciliation-routing"
        | ReconciliationSchedulingKey -> "reconciliation-scheduling-key"
        | ReconciliationDeduplication -> "reconciliation-deduplication"
        | ReconciliationDuplicate -> "reconciliation-duplicate"
        | ReconciliationReorder -> "reconciliation-reorder"
        | ReconciliationUnsupported -> "reconciliation-unsupported"
        | ReconciliationScope -> "reconciliation-scope"
        | ReconciliationExclusiveWriter -> "reconciliation-exclusive-writer"
        | ReconciliationDirectWrite -> "reconciliation-direct-write"
        | ReconciliationSealedPlan -> "reconciliation-sealed-plan"
        | ReconciliationOrdering -> "reconciliation-ordering"
        | ReconciliationSeal -> "reconciliation-seal"
        | ReconciliationReplay -> "reconciliation-replay"
        | ReconciliationQuintPreservation -> "reconciliation-quint-preservation"
        | ReconciliationNoNetwork -> "reconciliation-no-network"
        | ReconciliationNoProductionQueue -> "reconciliation-no-production-queue"
        | ReconciliationNoMutation -> "reconciliation-no-mutation"

    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private sha = Regex("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private expectedRoute kind = $"reconcile/{kind}"
    let private normalizedSubject (event: GitHubReconciliationEvent) = $"{event.SubjectKind}:{event.SubjectId}"
    let private schedulingKey repository subject = [ repository; subject ] |> List.map frame |> String.concat "" |> hash
    let private entryBytes entry =
        [ entry.EventKind; entry.Subject; string entry.SubjectRevision; entry.SchedulingKey
          entry.QueueReceipt; entry.DeduplicationDisposition ] |> List.map frame |> String.concat ""
    let private seal repository sourceRevision entries =
        [ "github-narrow-reconciliation/v1"; repository; sourceRevision
          supportedEventKinds |> List.map frame |> String.concat ""
          entries |> List.map entryBytes |> String.concat ""
          writerBoundary |> List.map frame |> String.concat "" ]
        |> List.map frame |> String.concat "" |> hash

    let compile (repository: string) (sourceRevision: string) (events: GitHubReconciliationEvent list) =
        let errors = ResizeArray<GitHubNarrowReconciliationFinding>()
        let validateText name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubNarrowReconciliationFinding.MissingField name)
            elif not (token.IsMatch value) then errors.Add(GitHubNarrowReconciliationFinding.MalformedField name)
        validateText "repository" repository
        validateText "sourceRevision" sourceRevision
        if not (String.IsNullOrWhiteSpace sourceRevision) && not (sha.IsMatch sourceRevision) then
            errors.Add(GitHubNarrowReconciliationFinding.StaleRevision sourceRevision)
        if events.IsEmpty then errors.Add GitHubNarrowReconciliationFinding.IncompleteEventInventory
        for event in events do
            for name, value in
                [ "eventKind", event.EventKind; "event.repository", event.Repository
                  "event.sourceRevision", event.SourceRevision; "subjectKind", event.SubjectKind
                  "subjectId", event.SubjectId; "deliveryId", event.DeliveryId
                  "route", event.Route; "origin", event.Origin ] do
                validateText name value
            if not (supportedEventKinds |> List.contains event.EventKind) then
                errors.Add(GitHubNarrowReconciliationFinding.UnknownEventKind event.EventKind)
            if event.Repository <> repository then
                errors.Add(GitHubNarrowReconciliationFinding.CrossScope event.Repository)
            if event.SourceRevision <> sourceRevision || not (sha.IsMatch event.SourceRevision) then
                errors.Add(GitHubNarrowReconciliationFinding.StaleRevision event.SourceRevision)
            if event.SubjectRevision <= 0L then
                errors.Add(GitHubNarrowReconciliationFinding.StaleRevision(string event.SubjectRevision))
            if event.SubjectKind <> event.EventKind then
                errors.Add(GitHubNarrowReconciliationFinding.ConflictingSubject(normalizedSubject event))
            if event.Route <> expectedRoute event.EventKind then
                errors.Add(GitHubNarrowReconciliationFinding.AlteredRouting event.Route)
            if event.Origin <> "event" && event.Origin <> "command" then
                errors.Add(GitHubNarrowReconciliationFinding.MalformedField "origin")
            if event.AttemptsDerivedWrite then
                errors.Add(GitHubNarrowReconciliationFinding.DirectWrite event.DeliveryId)
        let validEvents =
            events
            |> List.filter (fun (event: GitHubReconciliationEvent) ->
                supportedEventKinds |> List.contains event.EventKind
                && event.Repository = repository && event.SourceRevision = sourceRevision
                && event.SubjectKind = event.EventKind && event.SubjectRevision > 0L
                && event.Route = expectedRoute event.EventKind && not event.AttemptsDerivedWrite)
        validEvents
        |> List.groupBy (fun (event: GitHubReconciliationEvent) -> normalizedSubject event, event.SubjectRevision)
        |> List.iter (fun ((subject, _), (rows: GitHubReconciliationEvent list)) ->
            if rows |> List.map (fun (row: GitHubReconciliationEvent) -> row.EventKind, row.Route, row.Origin) |> Set.ofList |> Set.count > 1 then
                errors.Add(GitHubNarrowReconciliationFinding.ConflictingSubject subject))
        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let entries =
                events
                |> List.groupBy normalizedSubject
                |> List.map (fun (subject, (rows: GitHubReconciliationEvent list)) ->
                    let newest = rows |> List.maxBy (fun (event: GitHubReconciliationEvent) -> event.SubjectRevision)
                    let deliveries = rows |> List.map (fun (event: GitHubReconciliationEvent) -> event.DeliveryId) |> List.distinct |> List.sort
                    let key = schedulingKey repository subject
                    let receipt =
                        [ key; string newest.SubjectRevision; deliveries |> List.map frame |> String.concat "" ]
                        |> List.map frame |> String.concat "" |> hash
                    { EventKind = newest.EventKind; Subject = subject; SubjectRevision = newest.SubjectRevision
                      SchedulingKey = key; QueueReceipt = receipt
                      DeduplicationDisposition = if rows.Length = 1 then "queued" else "deduplicated" })
                |> List.sortBy (fun (entry: GitHubReconciliationQueueEntry) -> entry.SchedulingKey, entry.Subject)
            Ok
                { SchemaVersion = 1; Repository = repository; SourceRevision = sourceRevision
                  SupportedEventKinds = supportedEventKinds; Entries = entries; WriterBoundary = writerBoundary
                  Seal = seal repository sourceRevision entries }

    let serialize (plan: GitHubReconciliationPlan) =
        let entry (value: GitHubReconciliationQueueEntry) =
            {| eventKind = value.EventKind; subject = value.Subject; subjectRevision = value.SubjectRevision
               schedulingKey = value.SchedulingKey; queueReceipt = value.QueueReceipt
               deduplicationDisposition = value.DeduplicationDisposition |}
        JsonSerializer.Serialize(
            {| schemaVersion = plan.SchemaVersion; repository = plan.Repository; sourceRevision = plan.SourceRevision
               supportedEventKinds = plan.SupportedEventKinds; entries = plan.Entries |> List.map entry
               writerBoundary = plan.WriterBoundary; seal = plan.Seal |})

    let verify (expectedSeal: string) (plan: GitHubReconciliationPlan) =
        let expected = seal plan.Repository plan.SourceRevision plan.Entries
        if plan.SchemaVersion <> 1 then Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "schemaVersion" ]
        elif plan.SupportedEventKinds <> supportedEventKinds then Error [ GitHubNarrowReconciliationFinding.IncompleteEventInventory ]
        elif plan.WriterBoundary <> writerBoundary then Error [ GitHubNarrowReconciliationFinding.UnsealedPlan ]
        elif plan.Entries <> (plan.Entries |> List.sortBy (fun (entry: GitHubReconciliationQueueEntry) -> entry.SchedulingKey, entry.Subject)) then
            Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "entry ordering" ]
        elif plan.Seal <> expected || plan.Seal <> expectedSeal then Error [ GitHubNarrowReconciliationFinding.AlteredSeal ]
        else Ok plan

    let parse (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
            let entries =
                root.GetProperty("entries").EnumerateArray()
                |> Seq.map (fun (node: JsonElement) ->
                    { EventKind = text "eventKind" node; Subject = text "subject" node
                      SubjectRevision = node.GetProperty("subjectRevision").GetInt64()
                      SchedulingKey = text "schedulingKey" node; QueueReceipt = text "queueReceipt" node
                      DeduplicationDisposition = text "deduplicationDisposition" node })
                |> Seq.toList
            let plan: GitHubReconciliationPlan =
                { SchemaVersion = root.GetProperty("schemaVersion").GetInt32()
                  Repository = text "repository" root; SourceRevision = text "sourceRevision" root
                  SupportedEventKinds = root.GetProperty("supportedEventKinds").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                  Entries = entries
                  WriterBoundary = root.GetProperty("writerBoundary").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                  Seal = text "seal" root }
            match verify plan.Seal plan with
            | Error errors -> Error errors
            | Ok plan when serialize plan <> value -> Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "non-canonical bytes" ]
            | Ok plan -> Ok plan
        with error -> Error [ GitHubNarrowReconciliationFinding.InvalidSerialization error.Message ]

    let replay (prior: GitHubReconciliationPlan) (events: GitHubReconciliationEvent list) =
        match verify prior.Seal prior, compile prior.Repository prior.SourceRevision events with
        | Error errors, _ -> Error errors
        | _, Error errors -> Error errors
        | Ok _, Ok candidate ->
            let priorBySubject = prior.Entries |> List.map (fun (entry: GitHubReconciliationQueueEntry) -> entry.Subject, entry) |> Map.ofList
            let replayOnly =
                candidate.Entries
                |> List.forall (fun (entry: GitHubReconciliationQueueEntry) ->
                    match priorBySubject |> Map.tryFind entry.Subject with
                    | Some priorEntry -> entry.SubjectRevision <= priorEntry.SubjectRevision
                    | None -> false)
            if replayOnly then Ok prior
            else Error [ GitHubNarrowReconciliationFinding.ReplayConflict "new or newer subject requires fresh reconciliation" ]

    let validateControls (generated: GitHubNarrowReconciliationControlResult list) (independent: GitHubNarrowReconciliationControlResult list) =
        let expected = requiredControls |> List.map controlId
        let validate (label: string) (rows: GitHubNarrowReconciliationControlResult list) =
            [ if rows |> List.map (fun (row: GitHubNarrowReconciliationControlResult) -> controlId row.Control) <> expected then yield $"{label} control inventory differs"
              if rows |> List.exists (fun (row: GitHubNarrowReconciliationControlResult) -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok () else Error errors
