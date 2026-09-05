namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type GitHubAuditEventHistory =
    { Repository: string; SourceRevision: string; SubjectKind: string; SubjectId: string; SubjectRevision: int64; DeliveryId: string }
type GitHubScheduledAuditObservation =
    { Repository: string; SourceRevision: string; AuditScope: string list; Cursor: string; Page: int; PageCount: int
      SubjectKind: string; SubjectId: string; SubjectRevision: int64; Classification: string; EvidenceId: string
      Route: string; Origin: string; AttemptsDerivedWrite: bool }
type GitHubAuditRepairQueueEntry =
    { Repository: string; Subject: string; SubjectRevision: int64; Classifications: string list
      SchedulingKey: string; QueueReceipt: string; DeduplicationDisposition: string }
type GitHubAuditRepairPlan =
    { SchemaVersion: int; Repository: string; SourceRevision: string; AuditScope: string list; Cursor: string
      RequiredClassifications: string list; EventHistoryDigest: string; Entries: GitHubAuditRepairQueueEntry list
      WriterBoundary: string list; Seal: string }
[<RequireQualifiedAccess>]
type GitHubAuditRepairFinding =
    | MissingField of string | MalformedField of string | IncompleteAuditScope | PartialPage of string
    | StaleCursor of string | StaleRevision of string | ConflictingSubject of string | AlteredScope of string
    | AlteredObservation of string | UnknownSubjectKind of string | AlteredClassification of string | OmittedClassification of string
    | AlteredRouting of string | DirectWrite of string | UnsealedPlan | AlteredSeal | ReplayConflict of string
    | InvalidSerialization of string
type GitHubAuditRepairControl =
    | AuditPrerequisites | AuditRoadmap | AuditCompleteness | AuditScope | AuditCursor
    | AuditEventHistory | AuditObservation | AuditDeliveryGap | AuditPreviewGap
    | AuditExternalRepository | AuditSchemaDrift | AuditRepairRouting
    | AuditSchedulingKey | AuditDeduplication | AuditConvergence | AuditOmission
    | AuditExclusiveWriter | AuditDirectWrite | AuditSealedPlan | AuditOrdering
    | AuditSeal | AuditReplay | AuditQuintPreservation | AuditNoNetwork
    | AuditNoProductionQueue | AuditNoMutation
type GitHubAuditRepairControlResult =
    { Control: GitHubAuditRepairControl; ControlPassed: bool; BaselineGreen: bool }

module GitHubAuditRepairQualification =
    let requiredClassifications = [ "dropped-delivery"; "preview-gap"; "external-repository"; "schema-drift" ]
    let writerBoundary = [ "fresh-observe"; "reduce"; "sealed-plan"; "apply"; "verify" ]
    let requiredControls =
        [ AuditPrerequisites; AuditRoadmap; AuditCompleteness; AuditScope; AuditCursor
          AuditEventHistory; AuditObservation; AuditDeliveryGap; AuditPreviewGap
          AuditExternalRepository; AuditSchemaDrift; AuditRepairRouting
          AuditSchedulingKey; AuditDeduplication; AuditConvergence; AuditOmission
          AuditExclusiveWriter; AuditDirectWrite; AuditSealedPlan; AuditOrdering
          AuditSeal; AuditReplay; AuditQuintPreservation; AuditNoNetwork
          AuditNoProductionQueue; AuditNoMutation ]

    let controlId = function
        | AuditPrerequisites -> "audit-prerequisites" | AuditRoadmap -> "audit-roadmap"
        | AuditCompleteness -> "audit-completeness" | AuditScope -> "audit-scope"
        | AuditCursor -> "audit-cursor" | AuditEventHistory -> "audit-event-history"
        | AuditObservation -> "audit-observation" | AuditDeliveryGap -> "audit-delivery-gap"
        | AuditPreviewGap -> "audit-preview-gap" | AuditExternalRepository -> "audit-external-repository"
        | AuditSchemaDrift -> "audit-schema-drift" | AuditRepairRouting -> "audit-repair-routing"
        | AuditSchedulingKey -> "audit-scheduling-key" | AuditDeduplication -> "audit-deduplication"
        | AuditConvergence -> "audit-convergence" | AuditOmission -> "audit-omission"
        | AuditExclusiveWriter -> "audit-exclusive-writer" | AuditDirectWrite -> "audit-direct-write"
        | AuditSealedPlan -> "audit-sealed-plan" | AuditOrdering -> "audit-ordering"
        | AuditSeal -> "audit-seal" | AuditReplay -> "audit-replay"
        | AuditQuintPreservation -> "audit-quint-preservation" | AuditNoNetwork -> "audit-no-network"
        | AuditNoProductionQueue -> "audit-no-production-queue" | AuditNoMutation -> "audit-no-mutation"

    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private sha = Regex("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant)
    let private cursorPattern = Regex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private strings (values: string list) = values |> List.map frame |> String.concat ""
    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private subject (kind: string) (id: string) = $"{kind}:{id}"
    let private subjectIdentity (repository: string) (kind: string) (id: string) = repository, subject kind id
    let private subjectLabel (repository: string) (subjectValue: string) = $"{repository}|{subjectValue}"
    let private expectedRoute (kind: string) = $"reconcile/{kind}"
    let private schedulingKey (repository: string) (subjectValue: string) = strings [ repository; subjectValue ] |> hash
    let private historyBytes (history: GitHubAuditEventHistory list) =
        history
        |> List.sortBy (fun (row: GitHubAuditEventHistory) -> row.Repository, row.SourceRevision, row.SubjectKind, row.SubjectId, row.SubjectRevision, row.DeliveryId)
        |> List.map (fun (row: GitHubAuditEventHistory) -> strings [ row.Repository; row.SourceRevision; row.SubjectKind; row.SubjectId; string row.SubjectRevision; row.DeliveryId ])
        |> String.concat ""
    let private entryBytes (entry: GitHubAuditRepairQueueEntry) =
        strings [ entry.Repository; entry.Subject; string entry.SubjectRevision; strings entry.Classifications
                  entry.SchedulingKey; entry.QueueReceipt; entry.DeduplicationDisposition ]
    let private seal (repository: string) (sourceRevision: string) (scope: string list) (cursor: string) (historyDigest: string) (entries: GitHubAuditRepairQueueEntry list) =
        [ "github-audit-repair/v1"; repository; sourceRevision; strings scope; cursor
          strings requiredClassifications; historyDigest; entries |> List.map entryBytes |> String.concat ""
          strings writerBoundary ]
        |> strings |> hash

    let compile
        (repository: string)
        (sourceRevision: string)
        (auditScope: string list)
        (cursor: string)
        (eventHistory: GitHubAuditEventHistory list)
        (observations: GitHubScheduledAuditObservation list)
        : Result<GitHubAuditRepairPlan, GitHubAuditRepairFinding list> =
        let errors = ResizeArray<GitHubAuditRepairFinding>()
        let validateText (name: string) (value: string) =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubAuditRepairFinding.MissingField name)
            elif not (token.IsMatch value) then errors.Add(GitHubAuditRepairFinding.MalformedField name)
        validateText "repository" repository
        validateText "sourceRevision" sourceRevision
        if not (String.IsNullOrWhiteSpace sourceRevision) && not (sha.IsMatch sourceRevision) then errors.Add(GitHubAuditRepairFinding.StaleRevision sourceRevision)
        if String.IsNullOrWhiteSpace cursor then errors.Add(GitHubAuditRepairFinding.MissingField "cursor")
        elif not (cursorPattern.IsMatch cursor) || not (cursor.Contains(':')) then errors.Add(GitHubAuditRepairFinding.StaleCursor cursor)
        if auditScope.IsEmpty || auditScope <> (auditScope |> List.distinct |> List.sort) || not (auditScope |> List.contains repository) then
            errors.Add GitHubAuditRepairFinding.IncompleteAuditScope
        for scoped in auditScope do validateText "auditScope" scoped
        if observations.IsEmpty then errors.Add GitHubAuditRepairFinding.IncompleteAuditScope

        let historySubjects =
            eventHistory
            |> List.map (fun (row: GitHubAuditEventHistory) -> subjectIdentity row.Repository row.SubjectKind row.SubjectId)
            |> Set.ofList
        for row in eventHistory do
            for name, value in [ "history.repository", row.Repository; "history.sourceRevision", row.SourceRevision; "history.subjectKind", row.SubjectKind; "history.subjectId", row.SubjectId; "history.deliveryId", row.DeliveryId ] do validateText name value
            if not (auditScope |> List.contains row.Repository) then errors.Add(GitHubAuditRepairFinding.AlteredScope row.Repository)
            if row.SourceRevision <> sourceRevision then errors.Add(GitHubAuditRepairFinding.StaleRevision row.SourceRevision)
            if row.SubjectRevision <= 0L then errors.Add(GitHubAuditRepairFinding.StaleRevision(string row.SubjectRevision))
            if not (GitHubNarrowReconciliationQualification.supportedEventKinds |> List.contains row.SubjectKind) then
                errors.Add(GitHubAuditRepairFinding.UnknownSubjectKind row.SubjectKind)

        for row in observations do
            for name, value in
                [ "observation.repository", row.Repository; "observation.sourceRevision", row.SourceRevision
                  "observation.cursor", row.Cursor
                  "observation.subjectKind", row.SubjectKind; "observation.subjectId", row.SubjectId
                  "observation.classification", row.Classification; "observation.evidenceId", row.EvidenceId
                  "observation.route", row.Route; "observation.origin", row.Origin ] do validateText name value
            if not (auditScope |> List.contains row.Repository) then errors.Add(GitHubAuditRepairFinding.AlteredScope row.Repository)
            if row.AuditScope <> auditScope then errors.Add(GitHubAuditRepairFinding.AlteredScope row.Repository)
            if row.SourceRevision <> sourceRevision then errors.Add(GitHubAuditRepairFinding.StaleRevision row.SourceRevision)
            if row.Cursor <> cursor then errors.Add(GitHubAuditRepairFinding.StaleCursor row.Cursor)
            if row.Page <= 0 || row.PageCount <= 0 || row.Page > row.PageCount then errors.Add(GitHubAuditRepairFinding.PartialPage row.Repository)
            if row.SubjectRevision <= 0L then errors.Add(GitHubAuditRepairFinding.StaleRevision(string row.SubjectRevision))
            if not (GitHubNarrowReconciliationQualification.supportedEventKinds |> List.contains row.SubjectKind) then
                errors.Add(GitHubAuditRepairFinding.UnknownSubjectKind row.SubjectKind)
            if not (requiredClassifications |> List.contains row.Classification) then errors.Add(GitHubAuditRepairFinding.AlteredClassification row.Classification)
            if row.Route <> expectedRoute row.SubjectKind then errors.Add(GitHubAuditRepairFinding.AlteredRouting row.Route)
            if row.Origin <> "audit" then errors.Add(GitHubAuditRepairFinding.AlteredObservation row.EvidenceId)
            if row.AttemptsDerivedWrite then errors.Add(GitHubAuditRepairFinding.DirectWrite row.EvidenceId)

        observations
        |> List.groupBy (fun (row: GitHubScheduledAuditObservation) -> row.Repository)
        |> List.iter (fun (repo, rows) ->
            let pageCounts = rows |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.PageCount) |> Set.ofList
            let pages = rows |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.Page) |> Set.ofList
            if pageCounts.Count <> 1 || pages <> Set.ofList [ 1 .. rows.Head.PageCount ] then errors.Add(GitHubAuditRepairFinding.PartialPage repo))
        for scoped in auditScope do
            if observations |> List.exists (fun (row: GitHubScheduledAuditObservation) -> row.Repository = scoped) |> not then
                errors.Add GitHubAuditRepairFinding.IncompleteAuditScope
        for classification in requiredClassifications do
            if observations |> List.exists (fun (row: GitHubScheduledAuditObservation) -> row.Classification = classification) |> not then errors.Add(GitHubAuditRepairFinding.OmittedClassification classification)
        for historyRepository, historySubject in historySubjects do
            if observations |> List.exists (fun (row: GitHubScheduledAuditObservation) -> subjectIdentity row.Repository row.SubjectKind row.SubjectId = (historyRepository, historySubject)) |> not then
                errors.Add(GitHubAuditRepairFinding.AlteredObservation(subjectLabel historyRepository historySubject))
        observations
        |> List.groupBy (fun (row: GitHubScheduledAuditObservation) -> subjectIdentity row.Repository row.SubjectKind row.SubjectId, row.SubjectRevision)
        |> List.iter (fun (((identityRepository, identitySubject), _), rows) ->
            if rows |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.SubjectKind, row.Route) |> Set.ofList |> Set.count > 1 then
                errors.Add(GitHubAuditRepairFinding.ConflictingSubject(subjectLabel identityRepository identitySubject)))

        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let historyDigest = historyBytes eventHistory |> hash
            let auditRows: ((string * string) * Choice<GitHubAuditEventHistory, GitHubScheduledAuditObservation>) list =
                observations
                |> List.map (fun (row: GitHubScheduledAuditObservation) -> subjectIdentity row.Repository row.SubjectKind row.SubjectId, Choice2Of2 row)
            let historyRows: ((string * string) * Choice<GitHubAuditEventHistory, GitHubScheduledAuditObservation>) list =
                eventHistory
                |> List.map (fun (row: GitHubAuditEventHistory) -> subjectIdentity row.Repository row.SubjectKind row.SubjectId, Choice1Of2 row)
            let entries =
                historyRows @ auditRows
                |> List.groupBy fst
                |> List.map (fun ((repo, identity), rows) ->
                    let histories: GitHubAuditEventHistory list = rows |> List.choose (function _, Choice1Of2 row -> Some row | _ -> None)
                    let audits: GitHubScheduledAuditObservation list = rows |> List.choose (function _, Choice2Of2 row -> Some row | _ -> None)
                    let revision =
                        [ yield! histories |> List.map (fun (row: GitHubAuditEventHistory) -> row.SubjectRevision)
                          yield! audits |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.SubjectRevision) ] |> List.max
                    let classifications = audits |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.Classification) |> List.distinct |> List.sort
                    let key = schedulingKey repo identity
                    let evidence =
                        [ yield! histories |> List.map (fun (row: GitHubAuditEventHistory) -> row.DeliveryId)
                          yield! audits |> List.map (fun (row: GitHubScheduledAuditObservation) -> row.EvidenceId) ] |> List.distinct |> List.sort
                    let receipt = strings [ key; string revision; strings classifications; strings evidence ] |> hash
                    ({ Repository = repo; Subject = identity; SubjectRevision = revision; Classifications = classifications;
                      SchedulingKey = key; QueueReceipt = receipt;
                      DeduplicationDisposition = if histories.IsEmpty then "audit-repair" else "event-audit-converged" }: GitHubAuditRepairQueueEntry))
                |> List.sortBy (fun (entry: GitHubAuditRepairQueueEntry) -> entry.SchedulingKey, entry.Subject)
            Ok
                { SchemaVersion = 1; Repository = repository; SourceRevision = sourceRevision; AuditScope = auditScope
                  Cursor = cursor; RequiredClassifications = requiredClassifications; EventHistoryDigest = historyDigest
                  Entries = entries; WriterBoundary = writerBoundary
                  Seal = seal repository sourceRevision auditScope cursor historyDigest entries }

    let serialize (plan: GitHubAuditRepairPlan) =
        let entry (value: GitHubAuditRepairQueueEntry) =
            {| repository = value.Repository; subject = value.Subject; subjectRevision = value.SubjectRevision
               classifications = value.Classifications; schedulingKey = value.SchedulingKey
               queueReceipt = value.QueueReceipt; deduplicationDisposition = value.DeduplicationDisposition |}
        JsonSerializer.Serialize(
            {| schemaVersion = plan.SchemaVersion; repository = plan.Repository; sourceRevision = plan.SourceRevision
               auditScope = plan.AuditScope; cursor = plan.Cursor; requiredClassifications = plan.RequiredClassifications
               eventHistoryDigest = plan.EventHistoryDigest; entries = plan.Entries |> List.map entry
               writerBoundary = plan.WriterBoundary; seal = plan.Seal |})

    let verify (expectedSeal: string) (plan: GitHubAuditRepairPlan) =
        let expected = seal plan.Repository plan.SourceRevision plan.AuditScope plan.Cursor plan.EventHistoryDigest plan.Entries
        if plan.SchemaVersion <> 1 then Error [ GitHubAuditRepairFinding.InvalidSerialization "schemaVersion" ]
        elif plan.AuditScope.IsEmpty || plan.AuditScope <> (plan.AuditScope |> List.distinct |> List.sort) then Error [ GitHubAuditRepairFinding.IncompleteAuditScope ]
        elif plan.RequiredClassifications <> requiredClassifications then Error [ GitHubAuditRepairFinding.OmittedClassification "required inventory" ]
        elif plan.WriterBoundary <> writerBoundary then Error [ GitHubAuditRepairFinding.UnsealedPlan ]
        elif plan.Entries <> (plan.Entries |> List.sortBy (fun (entry: GitHubAuditRepairQueueEntry) -> entry.SchedulingKey, entry.Subject)) then Error [ GitHubAuditRepairFinding.InvalidSerialization "entry ordering" ]
        elif plan.Seal <> expected || plan.Seal <> expectedSeal then Error [ GitHubAuditRepairFinding.AlteredSeal ]
        else Ok plan

    let parse (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
            let texts (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            let entries: GitHubAuditRepairQueueEntry list =
                root.GetProperty("entries").EnumerateArray()
                |> Seq.map (fun (node: JsonElement) ->
                    { Repository = text "repository" node; Subject = text "subject" node
                      SubjectRevision = node.GetProperty("subjectRevision").GetInt64()
                      Classifications = texts "classifications" node; SchedulingKey = text "schedulingKey" node
                      QueueReceipt = text "queueReceipt" node; DeduplicationDisposition = text "deduplicationDisposition" node })
                |> Seq.toList
            let plan: GitHubAuditRepairPlan =
                { SchemaVersion = root.GetProperty("schemaVersion").GetInt32(); Repository = text "repository" root
                  SourceRevision = text "sourceRevision" root; AuditScope = texts "auditScope" root
                  Cursor = text "cursor" root; RequiredClassifications = texts "requiredClassifications" root
                  EventHistoryDigest = text "eventHistoryDigest" root; Entries = entries
                  WriterBoundary = texts "writerBoundary" root; Seal = text "seal" root }
            match verify plan.Seal plan with
            | Error findings -> Error findings
            | Ok plan when serialize plan <> value -> Error [ GitHubAuditRepairFinding.InvalidSerialization "non-canonical bytes" ]
            | Ok plan -> Ok plan
        with error -> Error [ GitHubAuditRepairFinding.InvalidSerialization error.Message ]

    let replay (prior: GitHubAuditRepairPlan) (eventHistory: GitHubAuditEventHistory list) (observations: GitHubScheduledAuditObservation list) =
        match verify prior.Seal prior, compile prior.Repository prior.SourceRevision prior.AuditScope prior.Cursor eventHistory observations with
        | Error findings, _ -> Error findings
        | _, Error findings -> Error findings
        | Ok _, Ok candidate when serialize candidate = serialize prior -> Ok prior
        | Ok _, Ok _ -> Error [ GitHubAuditRepairFinding.ReplayConflict "audit replay differs from the sealed plan" ]

    let validateControls (generated: GitHubAuditRepairControlResult list) (independent: GitHubAuditRepairControlResult list) =
        let expected = requiredControls |> List.map controlId
        let validate (label: string) (rows: GitHubAuditRepairControlResult list) =
            [ if rows |> List.map (fun (row: GitHubAuditRepairControlResult) -> controlId row.Control) <> expected then yield $"{label} control inventory differs"
              if rows |> List.exists (fun (row: GitHubAuditRepairControlResult) -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok () else Error errors
