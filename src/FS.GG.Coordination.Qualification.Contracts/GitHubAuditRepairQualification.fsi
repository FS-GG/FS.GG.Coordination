namespace FS.GG.Coordination.Qualification.Contracts

type GitHubAuditEventHistory =
    { Repository: string
      SourceRevision: string
      SubjectKind: string
      SubjectId: string
      SubjectRevision: int64
      DeliveryId: string }

type GitHubScheduledAuditObservation =
    { Repository: string
      SourceRevision: string
      AuditScope: string list
      Cursor: string
      Page: int
      PageCount: int
      SubjectKind: string
      SubjectId: string
      SubjectRevision: int64
      Classification: string
      EvidenceId: string
      Route: string
      Origin: string
      AttemptsDerivedWrite: bool }

type GitHubAuditRepairQueueEntry =
    { Repository: string
      Subject: string
      SubjectRevision: int64
      Classifications: string list
      SchedulingKey: string
      QueueReceipt: string
      DeduplicationDisposition: string }

type GitHubAuditRepairPlan =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      AuditScope: string list
      Cursor: string
      RequiredClassifications: string list
      EventHistoryDigest: string
      Entries: GitHubAuditRepairQueueEntry list
      WriterBoundary: string list
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubAuditRepairFinding =
    | MissingField of string
    | MalformedField of string
    | IncompleteAuditScope
    | PartialPage of string
    | StaleCursor of string
    | StaleRevision of string
    | ConflictingSubject of string
    | AlteredScope of string
    | AlteredObservation of string
    | UnknownSubjectKind of string
    | AlteredClassification of string
    | OmittedClassification of string
    | AlteredRouting of string
    | DirectWrite of string
    | UnsealedPlan
    | AlteredSeal
    | ReplayConflict of string
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
    { Control: GitHubAuditRepairControl
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubAuditRepairQualification =
    val requiredClassifications: string list
    val writerBoundary: string list
    val requiredControls: GitHubAuditRepairControl list
    val controlId: GitHubAuditRepairControl -> string
    val compile:
        repository: string ->
        sourceRevision: string ->
        auditScope: string list ->
        cursor: string ->
        eventHistory: GitHubAuditEventHistory list ->
        observations: GitHubScheduledAuditObservation list ->
            Result<GitHubAuditRepairPlan, GitHubAuditRepairFinding list>
    val serialize: GitHubAuditRepairPlan -> string
    val parse: string -> Result<GitHubAuditRepairPlan, GitHubAuditRepairFinding list>
    val verify: expectedSeal: string -> GitHubAuditRepairPlan -> Result<GitHubAuditRepairPlan, GitHubAuditRepairFinding list>
    val replay: prior: GitHubAuditRepairPlan -> eventHistory: GitHubAuditEventHistory list -> observations: GitHubScheduledAuditObservation list -> Result<GitHubAuditRepairPlan, GitHubAuditRepairFinding list>
    val validateControls: generated: GitHubAuditRepairControlResult list -> independent: GitHubAuditRepairControlResult list -> Result<unit, string list>
