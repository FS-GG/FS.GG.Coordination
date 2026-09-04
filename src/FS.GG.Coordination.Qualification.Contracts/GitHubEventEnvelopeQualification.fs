namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type GitHubEventSource = { Kind: string; InstallationId: string; Repository: string; SourceRevision: string }
type GitHubEventDelivery =
    { CursorPosition: int64; DeliveryId: string; EventId: string; Subject: string; SubjectRevision: string
      CausationId: string; CorrelationId: string; ReceiptId: string; ReceiptDisposition: string }
type GitHubEventEnvelope =
    { SchemaVersion: int; Source: GitHubEventSource; Deliveries: GitHubEventDelivery list; Cursor: string list; Seal: string }
[<RequireQualifiedAccess>]
type GitHubEventEnvelopeFinding =
    | MissingField of string | MalformedField of string | UnknownSourceKind of string
    | DuplicateDeliveryConflict of string | DuplicateEventConflict of string | CursorPositionConflict of int64
    | CursorGap of expected: int64 * actual: int64 | CrossSource of string | CrossSubject of string
    | StaleRevision of string | CausationMismatch of string | CorrelationMismatch of string
    | ReceiptMismatch of string | AlteredSeal | ReplayConflict of string | InvalidSerialization of string
type GitHubEventEnvelopeControl =
    | EventPrerequisites | EventRoadmap | EventCompleteness | EventSource | EventDeliveryIdentity
    | EventIdentity | EventSubject | EventRevision | EventCausation | EventCorrelation | EventReceipt
    | EventDuplicate | EventReorder | EventConflict | EventCursor | EventOrdering | EventSeal | EventReplay
    | EventQuintPreservation | EventNoNetwork | EventNoQueue | EventNoMutation
type GitHubEventEnvelopeControlResult = { Control: GitHubEventEnvelopeControl; ControlPassed: bool; BaselineGreen: bool }

module GitHubEventEnvelopeQualification =
    let requiredControls =
        [ EventPrerequisites; EventRoadmap; EventCompleteness; EventSource; EventDeliveryIdentity
          EventIdentity; EventSubject; EventRevision; EventCausation; EventCorrelation; EventReceipt
          EventDuplicate; EventReorder; EventConflict; EventCursor; EventOrdering; EventSeal; EventReplay
          EventQuintPreservation; EventNoNetwork; EventNoQueue; EventNoMutation ]

    let controlId = function
        | EventPrerequisites -> "event-prerequisites" | EventRoadmap -> "event-roadmap"
        | EventCompleteness -> "event-completeness" | EventSource -> "event-source"
        | EventDeliveryIdentity -> "event-delivery-identity" | EventIdentity -> "event-identity"
        | EventSubject -> "event-subject" | EventRevision -> "event-revision"
        | EventCausation -> "event-causation" | EventCorrelation -> "event-correlation"
        | EventReceipt -> "event-receipt" | EventDuplicate -> "event-duplicate"
        | EventReorder -> "event-reorder" | EventConflict -> "event-conflict"
        | EventCursor -> "event-cursor" | EventOrdering -> "event-ordering"
        | EventSeal -> "event-seal" | EventReplay -> "event-replay"
        | EventQuintPreservation -> "event-quint-preservation" | EventNoNetwork -> "event-no-network"
        | EventNoQueue -> "event-no-queue" | EventNoMutation -> "event-no-mutation"

    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private sha = Regex("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant)
    let private allowedKinds = Set [ "check_run"; "issues"; "pull_request"; "push"; "release"; "repository"; "workflow_run" ]
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private deliveryBytes (value: GitHubEventDelivery) =
        [ string value.CursorPosition; value.DeliveryId; value.EventId; value.Subject; value.SubjectRevision
          value.CausationId; value.CorrelationId; value.ReceiptId; value.ReceiptDisposition ]
        |> List.map frame |> String.concat ""
    let private envelopeSeal source deliveries cursor =
        [ "github-event-envelope/v1"; source.Kind; source.InstallationId; source.Repository; source.SourceRevision
          deliveries |> List.map deliveryBytes |> String.concat ""
          cursor |> List.map frame |> String.concat "" ]
        |> List.map frame |> String.concat "" |> hash
    let private sameDelivery left right = deliveryBytes left = deliveryBytes right
    let private tryPositiveRevision (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, revision when revision > 0L -> Some revision
        | _ -> None

    let compile (source: GitHubEventSource) (deliveries: GitHubEventDelivery list) =
        let errors = ResizeArray<GitHubEventEnvelopeFinding>()
        let sourceFields = [ "source.kind", source.Kind; "source.installationId", source.InstallationId; "source.repository", source.Repository; "source.sourceRevision", source.SourceRevision ]
        for name, value in sourceFields do
            if not (validText value) then errors.Add(GitHubEventEnvelopeFinding.MissingField name)
            elif not (token.IsMatch value) then errors.Add(GitHubEventEnvelopeFinding.MalformedField name)
        if validText source.Kind && not (allowedKinds.Contains source.Kind) then errors.Add(GitHubEventEnvelopeFinding.UnknownSourceKind source.Kind)
        if validText source.SourceRevision && not (sha.IsMatch source.SourceRevision) then errors.Add(GitHubEventEnvelopeFinding.StaleRevision source.SourceRevision)
        if deliveries.IsEmpty then errors.Add(GitHubEventEnvelopeFinding.MissingField "deliveries")
        for delivery in deliveries do
            let fields =
                [ "deliveryId", delivery.DeliveryId; "eventId", delivery.EventId; "subject", delivery.Subject
                  "subjectRevision", delivery.SubjectRevision; "causationId", delivery.CausationId
                  "correlationId", delivery.CorrelationId; "receiptId", delivery.ReceiptId
                  "receiptDisposition", delivery.ReceiptDisposition ]
            for name, value in fields do
                if not (validText value) then errors.Add(GitHubEventEnvelopeFinding.MissingField name)
                elif not (token.IsMatch value) then errors.Add(GitHubEventEnvelopeFinding.MalformedField name)
            if delivery.CursorPosition <= 0L then errors.Add(GitHubEventEnvelopeFinding.MalformedField "cursorPosition")
            if tryPositiveRevision delivery.SubjectRevision |> Option.isNone then errors.Add(GitHubEventEnvelopeFinding.StaleRevision delivery.SubjectRevision)
            if delivery.CausationId = delivery.EventId then errors.Add(GitHubEventEnvelopeFinding.CausationMismatch delivery.EventId)
            if delivery.CorrelationId = delivery.DeliveryId then errors.Add(GitHubEventEnvelopeFinding.CorrelationMismatch delivery.DeliveryId)
            if delivery.ReceiptId = delivery.DeliveryId || delivery.ReceiptDisposition <> "accepted" then errors.Add(GitHubEventEnvelopeFinding.ReceiptMismatch delivery.ReceiptId)
        let conflicting key finding =
            deliveries |> List.groupBy key |> List.iter (fun (identity, rows) ->
                if rows |> List.map deliveryBytes |> Set.ofList |> Set.count > 1 then errors.Add(finding identity))
        conflicting _.DeliveryId GitHubEventEnvelopeFinding.DuplicateDeliveryConflict
        conflicting _.EventId GitHubEventEnvelopeFinding.DuplicateEventConflict
        deliveries |> List.groupBy _.CursorPosition |> List.iter (fun (position, rows) ->
            if rows |> List.map deliveryBytes |> Set.ofList |> Set.count > 1 then errors.Add(GitHubEventEnvelopeFinding.CursorPositionConflict position))
        let distinct = deliveries |> List.distinctBy deliveryBytes |> List.sortBy _.CursorPosition
        distinct |> List.iteri (fun index row ->
            let expected = int64 index + 1L
            if row.CursorPosition <> expected then errors.Add(GitHubEventEnvelopeFinding.CursorGap(expected, row.CursorPosition)))
        let subjectRevisions = distinct |> List.groupBy _.Subject
        for subject, rows in subjectRevisions do
            let revisions = rows |> List.choose (fun row -> tryPositiveRevision row.SubjectRevision)
            if revisions <> List.sort revisions || revisions |> List.distinct |> List.length <> revisions.Length then errors.Add(GitHubEventEnvelopeFinding.CrossSubject subject)
        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let cursor = distinct |> List.map (fun row -> $"{row.CursorPosition}:{row.DeliveryId}:{row.EventId}:{row.ReceiptId}")
            Ok { SchemaVersion = 1; Source = source; Deliveries = distinct; Cursor = cursor; Seal = envelopeSeal source distinct cursor }

    let serialize envelope =
        let delivery (value: GitHubEventDelivery) =
            {| cursorPosition=value.CursorPosition; deliveryId=value.DeliveryId; eventId=value.EventId; subject=value.Subject
               subjectRevision=value.SubjectRevision; causationId=value.CausationId; correlationId=value.CorrelationId
               receiptId=value.ReceiptId; receiptDisposition=value.ReceiptDisposition |}
        JsonSerializer.Serialize(
            {| schemaVersion=envelope.SchemaVersion
               source={| kind=envelope.Source.Kind; installationId=envelope.Source.InstallationId; repository=envelope.Source.Repository; sourceRevision=envelope.Source.SourceRevision |}
               deliveries=envelope.Deliveries |> List.map delivery; cursor=envelope.Cursor; seal=envelope.Seal |})

    let parse (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
            let sourceNode = root.GetProperty("source")
            let source = { Kind=text "kind" sourceNode; InstallationId=text "installationId" sourceNode; Repository=text "repository" sourceNode; SourceRevision=text "sourceRevision" sourceNode }
            let deliveries =
                root.GetProperty("deliveries").EnumerateArray() |> Seq.map (fun node ->
                    { CursorPosition=node.GetProperty("cursorPosition").GetInt64(); DeliveryId=text "deliveryId" node; EventId=text "eventId" node
                      Subject=text "subject" node; SubjectRevision=text "subjectRevision" node; CausationId=text "causationId" node
                      CorrelationId=text "correlationId" node; ReceiptId=text "receiptId" node; ReceiptDisposition=text "receiptDisposition" node }) |> Seq.toList
            match compile source deliveries with
            | Error errors -> Error errors
            | Ok candidate when root.GetProperty("schemaVersion").GetInt32() <> 1 -> Error [ GitHubEventEnvelopeFinding.InvalidSerialization "schemaVersion" ]
            | Ok candidate when root.GetProperty("cursor").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList <> candidate.Cursor -> Error [ GitHubEventEnvelopeFinding.InvalidSerialization "cursor" ]
            | Ok candidate when text "seal" root <> candidate.Seal -> Error [ GitHubEventEnvelopeFinding.AlteredSeal ]
            | Ok candidate when serialize candidate <> value -> Error [ GitHubEventEnvelopeFinding.InvalidSerialization "non-canonical bytes" ]
            | Ok candidate -> Ok candidate
        with error -> Error [ GitHubEventEnvelopeFinding.InvalidSerialization error.Message ]

    let verify expectedSeal envelope =
        match compile envelope.Source envelope.Deliveries with
        | Error errors -> Error errors
        | Ok candidate when candidate.Cursor <> envelope.Cursor -> Error [ GitHubEventEnvelopeFinding.InvalidSerialization "cursor" ]
        | Ok candidate when candidate.Seal <> envelope.Seal || candidate.Seal <> expectedSeal -> Error [ GitHubEventEnvelopeFinding.AlteredSeal ]
        | Ok candidate -> Ok candidate

    let replay prior source deliveries =
        match verify prior.Seal prior, compile source (prior.Deliveries @ deliveries) with
        | Error errors, _ -> Error errors
        | _, Error errors -> Error errors
        | Ok _, Ok candidate when source <> prior.Source -> Error [ GitHubEventEnvelopeFinding.CrossSource source.Repository ]
        | Ok _, Ok candidate when candidate.Deliveries.Length < prior.Deliveries.Length -> Error [ GitHubEventEnvelopeFinding.ReplayConflict "delivery removal" ]
        | Ok _, Ok candidate -> Ok candidate

    let validateControls generated independent =
        let expected = requiredControls |> List.map controlId
        let validate label rows =
            [ if rows |> List.map (fun row -> controlId row.Control) <> expected then yield $"{label} control inventory differs"
              if rows |> List.exists (fun row -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok () else Error errors
