namespace FS.GG.Coordination.Qualification.Contracts

type EventBenefitMetric =
    { Value: int64 option
      UnknownReason: string option }

type EventBenefitApiAttempt =
    { Attempt: int
      Request: string
      ResponseStatus: int option
      RateOutcome: string
      WorkloadCall: bool }

type EventBenefitSource =
    { SourceId: string
      Category: string
      Repository: string
      Workflow: string option
      TestedHead: string option
      RunId: int64 option
      RunAttempt: int option
      AttemptCount: int option
      Page: int
      PageCount: int
      EventAt: string option
      IngestedAt: string option
      QueuedAt: string option
      StartedAt: string option
      EndedAt: string option
      RawPayload: string
      RawPayloadSha256: string
      ApiAttempts: EventBenefitApiAttempt list }

type EventBenefitHint =
    { HintId: string
      Subject: string
      Revision: int64
      Kind: string
      OperationId: string
      ArrivedAt: string
      State: string
      SourceId: string }

type EventBenefitAuditObservation =
    { AuditId: string
      Subject: string
      Revision: int64
      ScheduledAt: string
      DiscoveredAt: string
      ConvergedAt: string
      SourceId: string
      InjectedWithheldHint: bool }

type EventBenefitHostedIdentity =
    { Repository: string
      Workflow: string
      RunId: int64
      RunAttempt: int
      TestedHead: string
      WindowStart: string
      WindowEnd: string
      PopulationDigest: string
      SourceDigests: string list }

type EventBenefitFacts =
    { Unit: string
      PrerequisiteReceiptSha256: string
      RoadmapRevision: string
      RoadmapSha256: string
      CandidateHead: string
      Population: string list
      WindowStart: string
      WindowEnd: string
      Sources: EventBenefitSource list
      Hints: EventBenefitHint list
      AuditObservations: EventBenefitAuditObservation list
      FullScanApiCalls: EventBenefitMetric
      FullScanSchedules: EventBenefitMetric
      HostedClaim: EventBenefitHostedIdentity option }

type EventBenefitSubjectResult =
    { Subject: string
      ReconcileRevision: int64
      HintCount: int
      ScheduledCount: int
      OperationIds: string list
      ApplyingOperationIds: string list
      Outcome: string }

type EventBenefitReport =
    { SchemaVersion: int
      Unit: string
      PrerequisiteReceiptSha256: string
      RoadmapRevision: string
      RoadmapSha256: string
      CandidateHead: string
      Population: string list
      PopulationDigest: string
      WindowStart: string
      WindowEnd: string
      SourceCategories: string list
      SourceDigests: string list
      SourceCount: int
      PageCount: int
      RunAttemptCount: int
      HintCount: int
      SubjectCount: int
      ScheduleAdmissions: int
      NarrowApiCallAttempts: int
      CollectorApiCallAttempts: int
      FullScanApiCalls: EventBenefitMetric
      FullScanSchedules: EventBenefitMetric
      EventLatencyMilliseconds: EventBenefitMetric
      RepairDelayMilliseconds: EventBenefitMetric
      Subjects: EventBenefitSubjectResult list
      DeliveryOutcomes: string list
      FalseOutcomes: string list
      UnknownOutcomes: string list
      Coverage: string list
      Limits: string list
      CompleteAuditAuthority: string
      OrdinaryMergeDependency: bool
      PollingDecision: string
      InstalledBenefit: bool
      ProductionBenefit: bool
      HostedIdentity: EventBenefitHostedIdentity option
      Conclusion: string
      Seal: string }

[<RequireQualifiedAccess>]
type EventBenefitFinding =
    | MissingField of string
    | MalformedField of string
    | ChangedPrerequisite
    | ChangedRoadmap
    | SubstitutedHead
    | UnboundedPopulation
    | UnboundedWindow
    | UnknownSourceCategory of string
    | IncompletePage of string
    | IncompleteRunAttempt of string
    | MissingTimestamp of string
    | MissingCallAttempt of string
    | MissingRateOutcome of string
    | AlteredSourceDigest of string
    | ContradictoryRevision of string
    | UnsupportedHint of string
    | LostSemanticCommand of string
    | LostDistinctSubject of string
    | CancelledInFlightEffect of string
    | DroppedEventUnrepaired of string
    | HostedEvidenceIncomplete
    | ReplayAsInstalled
    | SandboxAsProduction
    | UnsealedReport
    | AlteredSeal
    | ReplayConflict
    | InvalidSerialization of string

type EventBenefitControlResult =
    { ControlId: string
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubEventBenefitQualification =
    val prerequisiteReceiptSha256: string
    val roadmapRevision: string
    val roadmapSha256: string
    val sourceCategories: string list
    val requiredControls: string list
    val compile: EventBenefitFacts -> Result<EventBenefitReport, EventBenefitFinding list>
    val serialize: EventBenefitReport -> string
    val parse: string -> Result<EventBenefitReport, EventBenefitFinding list>
    val verify: expectedSeal: string -> EventBenefitReport -> Result<EventBenefitReport, EventBenefitFinding list>
    val replay: prior: EventBenefitReport -> EventBenefitFacts -> Result<EventBenefitReport, EventBenefitFinding list>
    val validateControls: generated: EventBenefitControlResult list -> independent: EventBenefitControlResult list -> Result<unit, string list>
