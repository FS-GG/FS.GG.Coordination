namespace FS.GG.Coordination.Qualification.Contracts

type GitHubEventSource =
    { Kind: string
      InstallationId: string
      Repository: string
      SourceRevision: string }

type GitHubEventDelivery =
    { CursorPosition: int64
      DeliveryId: string
      EventId: string
      Subject: string
      SubjectRevision: string
      CausationId: string
      CorrelationId: string
      ReceiptId: string
      ReceiptDisposition: string }

type GitHubEventEnvelope =
    { SchemaVersion: int
      Source: GitHubEventSource
      Deliveries: GitHubEventDelivery list
      Cursor: string list
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubEventEnvelopeFinding =
    | MissingField of string
    | MalformedField of string
    | UnknownSourceKind of string
    | DuplicateDeliveryConflict of string
    | DuplicateEventConflict of string
    | CursorPositionConflict of int64
    | CursorGap of expected: int64 * actual: int64
    | CrossSource of string
    | CrossSubject of string
    | StaleRevision of string
    | CausationMismatch of string
    | CorrelationMismatch of string
    | ReceiptMismatch of string
    | AlteredSeal
    | ReplayConflict of string
    | InvalidSerialization of string

type GitHubEventEnvelopeControl =
    | EventPrerequisites | EventRoadmap | EventCompleteness | EventSource | EventDeliveryIdentity
    | EventIdentity | EventSubject | EventRevision | EventCausation | EventCorrelation | EventReceipt
    | EventDuplicate | EventReorder | EventConflict | EventCursor | EventOrdering | EventSeal | EventReplay
    | EventQuintPreservation | EventNoNetwork | EventNoQueue | EventNoMutation

type GitHubEventEnvelopeControlResult =
    { Control: GitHubEventEnvelopeControl
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubEventEnvelopeQualification =
    val requiredControls: GitHubEventEnvelopeControl list
    val controlId: GitHubEventEnvelopeControl -> string
    val compile: source: GitHubEventSource -> deliveries: GitHubEventDelivery list -> Result<GitHubEventEnvelope, GitHubEventEnvelopeFinding list>
    val serialize: GitHubEventEnvelope -> string
    val parse: string -> Result<GitHubEventEnvelope, GitHubEventEnvelopeFinding list>
    val verify: expectedSeal: string -> GitHubEventEnvelope -> Result<GitHubEventEnvelope, GitHubEventEnvelopeFinding list>
    val replay: prior: GitHubEventEnvelope -> source: GitHubEventSource -> deliveries: GitHubEventDelivery list -> Result<GitHubEventEnvelope, GitHubEventEnvelopeFinding list>
    val validateControls: generated: GitHubEventEnvelopeControlResult list -> independent: GitHubEventEnvelopeControlResult list -> Result<unit, string list>
