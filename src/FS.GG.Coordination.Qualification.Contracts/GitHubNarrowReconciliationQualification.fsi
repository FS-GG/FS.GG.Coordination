namespace FS.GG.Coordination.Qualification.Contracts

type GitHubReconciliationEvent =
    { EventKind: string
      Repository: string
      SourceRevision: string
      SubjectKind: string
      SubjectId: string
      SubjectRevision: int64
      DeliveryId: string
      Route: string
      Origin: string
      AttemptsDerivedWrite: bool }

type GitHubReconciliationQueueEntry =
    { EventKind: string
      Subject: string
      SubjectRevision: int64
      SchedulingKey: string
      QueueReceipt: string
      DeduplicationDisposition: string }

type GitHubReconciliationPlan =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      SupportedEventKinds: string list
      Entries: GitHubReconciliationQueueEntry list
      WriterBoundary: string list
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubNarrowReconciliationFinding =
    | MissingField of string
    | MalformedField of string
    | UnknownEventKind of string
    | IncompleteEventInventory
    | CrossScope of string
    | StaleRevision of string
    | ConflictingSubject of string
    | AlteredRouting of string
    | DirectWrite of string
    | UnsealedPlan
    | AlteredSeal
    | ReplayConflict of string
    | InvalidSerialization of string

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
    { Control: GitHubNarrowReconciliationControl
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubNarrowReconciliationQualification =
    val supportedEventKinds: string list
    val writerBoundary: string list
    val requiredControls: GitHubNarrowReconciliationControl list
    val controlId: GitHubNarrowReconciliationControl -> string
    val compile: repository: string -> sourceRevision: string -> events: GitHubReconciliationEvent list -> Result<GitHubReconciliationPlan, GitHubNarrowReconciliationFinding list>
    val serialize: GitHubReconciliationPlan -> string
    val parse: string -> Result<GitHubReconciliationPlan, GitHubNarrowReconciliationFinding list>
    val verify: expectedSeal: string -> GitHubReconciliationPlan -> Result<GitHubReconciliationPlan, GitHubNarrowReconciliationFinding list>
    val replay: prior: GitHubReconciliationPlan -> events: GitHubReconciliationEvent list -> Result<GitHubReconciliationPlan, GitHubNarrowReconciliationFinding list>
    val validateControls: generated: GitHubNarrowReconciliationControlResult list -> independent: GitHubNarrowReconciliationControlResult list -> Result<unit, string list>
