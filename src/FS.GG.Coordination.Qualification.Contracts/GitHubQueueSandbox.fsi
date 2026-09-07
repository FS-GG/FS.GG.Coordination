namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type QueueProviderCapability = Supported | Unsupported | Unknown

[<RequireQualifiedAccess>]
type QueueCheckConclusion = Success | Pending | Failure

type QueueCheckResult =
    { Name: string
      HeadSha: string
      EventName: string
      Conclusion: QueueCheckConclusion }

type QueuePilotFacts =
    { Repository: string
      RepositoryId: int64
      IsProduction: bool
      ProviderCapability: QueueProviderCapability
      PreVisibility: string
      RepositorySecretCount: int
      EnvironmentSecretCount: int
      PilotId: string
      CandidateSha: string
      CurrentCandidateSha: string
      MergeGroupHeadSha: string
      BaseRef: string
      ObservedBaseSha: string
      CurrentBaseSha: string
      ReevaluatedBaseSha: string
      BaseObservationRevision: int64
      CurrentBaseObservationRevision: int64
      OriginalRequiredChecks: string list
      CurrentRequiredChecks: string list
      CheckResults: QueueCheckResult list
      ObservedClaimGeneration: int64
      CurrentClaimGeneration: int64
      ObservedReviewDigest: string
      CurrentReviewDigest: string
      ObservedDependencyDigest: string
      CurrentDependencyDigest: string
      ObservedReleaseObligationsMet: bool
      CurrentReleaseObligationsMet: bool
      ObservedSettingsDigest: string
      CurrentSettingsDigest: string
      AdmittedAtUnixSeconds: int64
      ExpiresAtUnixSeconds: int64
      EvaluatedAtUnixSeconds: int64 }

type QueuePilotPlan =
    { SchemaVersion: int
      Repository: string
      RepositoryId: int64
      PilotId: string
      CandidateSha: string
      MergeGroupHeadSha: string
      BaseRef: string
      PriorBaseSha: string
      BaseSha: string
      BaseObservationRevision: int64
      RequiredChecks: string list
      CheckResults: QueueCheckResult list
      ClaimGeneration: int64
      ReviewDigest: string
      DependencyDigest: string
      ReleaseObligationsMet: bool
      SettingsDigest: string
      AdmittedAtUnixSeconds: int64
      ExpiresAtUnixSeconds: int64
      EvaluatedAtUnixSeconds: int64
      Disposition: string
      Seal: string }

type QueueEffect = { OperationId: string; Attempt: int; ResultDigest: string }
type QueueCompensation = { OperationId: string; CompensationId: string; FinalStateDigest: string }
type QueueBurstHint = { Subject: string; HintId: string; Sequence: int; SupersedesHintId: string option }
type QueueBurstDecision =
    { Subject: string
      PriorHeadSha: string
      HeadSha: string
      AuthorizationHeadSha: string
      PriorBaseSha: string
      BaseSha: string
      PriorRequiredChecks: string list
      RequiredChecks: string list
      SuccessfulChecks: string list
      WorkMilliseconds: int64
      WaitingMilliseconds: int64
      Delivered: bool }
type QueueBurstFacts =
    { Hints: QueueBurstHint list
      Decisions: QueueBurstDecision list
      MaxHintsPerSubject: int
      InFlightEffectCount: int
      CancelledInFlightEffectCount: int
      WorkMilliseconds: int64
      WaitingMilliseconds: int64 }
type QueueBurstReceipt =
    { SchemaVersion: int
      Subjects: string list
      HintCount: int
      SupersededHintCount: int
      WorkMilliseconds: int64
      WaitingMilliseconds: int64
      DeliveredSubjects: string list
      Disposition: string
      Seal: string }

type QueueRecoveryFacts =
    { Repository: string
      RepositoryId: int64
      PilotSeal: string
      DurableCheckpointDigest: string
      ResumeCheckpointDigest: string
      Interrupted: bool
      FailedStepInjected: bool
      AppliedEffects: QueueEffect list
      RetryEffects: QueueEffect list
      Compensations: QueueCompensation list
      DuplicateEffectCount: int
      TemporaryResourceCount: int
      PreVisibility: string
      FinalVisibility: string
      PreSettingsDigest: string
      FinalSettingsDigest: string
      Recoverable: bool }

type QueueRecoveryReceipt =
    { SchemaVersion: int
      Repository: string
      RepositoryId: int64
      PilotSeal: string
      CheckpointDigest: string
      AppliedEffects: QueueEffect list
      RetryEffects: QueueEffect list
      Compensations: QueueCompensation list
      FinalVisibility: string
      FinalSettingsDigest: string
      Disposition: string
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubQueueSandboxFinding =
    | InvalidField of string
    | WrongRepository
    | ProductionTarget
    | UnsupportedCapability
    | UnknownCapability
    | SecretPresent
    | VisibilityTransitionNotBound
    | CandidateMoved
    | BaseNotAdvanced
    | BaseNotReevaluated
    | RequiredChecksNotGrown
    | CheckInventoryIncomplete
    | CheckNotSuccessful of string
    | AuthorityChanged of string
    | AdmissionExpired
    | MissingInterruption
    | MissingFailedStep
    | UnsealedResume
    | RetryDiverged
    | DuplicateEffect
    | CompensationOrderInvalid
    | CleanupIncomplete
    | RollbackMismatch of string
    | Unrecoverable
    | AlteredSeal
    | ReplayConflict
    | InvalidSerialization
    | BurstTooLarge
    | DistinctSubjectLost
    | InvalidSupersession
    | InFlightEffectCancelled
    | MissingRequiredContext of string
    | StaleGreenAuthorization of string
    | MovementNotExercised of string
    | MetricMismatch of string

type QueueSandboxControlResult = { ControlId: string; ControlPassed: bool; BaselineGreen: bool }

module GitHubQueueSandbox =
    val repository: string
    val repositoryId: int64
    val pilotDisposition: string
    val recoveryDisposition: string
    val burstDisposition: string
    val pilotControlIds: string list
    val recoveryControlIds: string list
    val compilePilot: QueuePilotFacts -> Result<QueuePilotPlan, GitHubQueueSandboxFinding list>
    val serializePilot: QueuePilotPlan -> string
    val parsePilot: string -> Result<QueuePilotPlan, GitHubQueueSandboxFinding list>
    val verifyPilot: expectedSeal:string -> QueuePilotPlan -> Result<QueuePilotPlan, GitHubQueueSandboxFinding list>
    val replayPilot: prior:QueuePilotPlan -> facts:QueuePilotFacts -> Result<QueuePilotPlan, GitHubQueueSandboxFinding list>
    val compileRecovery: QueueRecoveryFacts -> Result<QueueRecoveryReceipt, GitHubQueueSandboxFinding list>
    val serializeRecovery: QueueRecoveryReceipt -> string
    val parseRecovery: string -> Result<QueueRecoveryReceipt, GitHubQueueSandboxFinding list>
    val verifyRecovery: expectedSeal:string -> QueueRecoveryReceipt -> Result<QueueRecoveryReceipt, GitHubQueueSandboxFinding list>
    val replayRecovery: prior:QueueRecoveryReceipt -> facts:QueueRecoveryFacts -> Result<QueueRecoveryReceipt, GitHubQueueSandboxFinding list>
    val compileBurst: QueueBurstFacts -> Result<QueueBurstReceipt, GitHubQueueSandboxFinding list>
    val serializeBurst: QueueBurstReceipt -> string
    val verifyBurst: expectedSeal:string -> QueueBurstReceipt -> Result<QueueBurstReceipt, GitHubQueueSandboxFinding list>
    val validateControls: expected:string list -> generated:QueueSandboxControlResult list -> independent:QueueSandboxControlResult list -> Result<unit,string list>
