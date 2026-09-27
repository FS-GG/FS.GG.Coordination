namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
type MigrationEffect =
    | SetIssueType of repositoryId:int64 * issueNodeId:string * typeNodeId:string
    | SetIssueField of repositoryId:int64 * issueNodeId:string * fieldNodeId:string * valueSha256:string
    | AddBlockingEdge of repositoryId:int64 * blockerNodeId:string * blockedNodeId:string
    | SetProjectField of projectNodeId:string * itemNodeId:string * fieldNodeId:string * valueNodeId:string
    | ApplyRepositorySettings of repositoryId:int64 * settingsPlanSha256:string
    | AdoptReceiver of repositoryId:int64 * expectedCommit:string * desiredCommit:string
    | SealArchive of authorityId:string * archiveSha256:string * verifierSha256:string

type MigrationExecutionStep =
    { OperationId: string
      IdempotencyKey: string
      ManifestSeal: string
      Effect: MigrationEffect
      TargetIdentity: string
      ExpectedTargetRevision: string
      ExpectedTargetSha256: string
      DesiredTargetSha256: string
      EpochGeneration: int64
      EpochCommit: string
      AuthorityFence: MigrationAuthorityFence
      JournalGeneration: int64
      JournalHead: string
      Seal: string }

and MigrationAuthorityFence =
    { AdmissionGeneration: int64
      AdmissionCommit: string
      OperationGeneration: int64
      OperationCommit: string
      Claim: (int64 * string) option
      SealCommit: string
      RegistryCommit: string }

type MigrationFenceObservation =
    { Fence: MigrationAuthorityFence
      Complete: bool
      Authorized: bool }

type MigrationEpochObservation =
    { Phase: string
      ManifestSeal: string
      Generation: int64
      Commit: string
      Complete: bool
      Authorized: bool }

type MigrationTargetObservation =
    { Identity: string
      Revision: string
      Sha256: string
      Complete: bool
      Authorized: bool }

[<RequireQualifiedAccess>]
type MigrationJournalStage = IntentPersisted | InFlight | Settled

type MigrationJournalAuthority =
    { OperationId: string
      StepSeal: string
      Generation: int64
      Commit: string
      Stage: MigrationJournalStage
      ResultSha256: string option }

[<RequireQualifiedAccess>]
type MigrationCasOutcome =
    | Accepted of MigrationJournalAuthority
    | Conflict
    | Unknown

[<RequireQualifiedAccess>]
type MigrationEffectObservation =
    | Applied of resultSha256:string
    | ProvenAbsent
    | Partial of reason:string
    | Unknown

[<RequireQualifiedAccess>]
type MigrationDispatchOutcome =
    | Applied
    | Refused of reason:string
    | Unknown

[<RequireQualifiedAccess>]
type MigrationAdvanceCut =
    | NoCut
    | StopBeforeIntent
    | StopAfterIntent
    | StopAfterInFlight
    | StopAfterDispatch
    | StopAfterReadback
    | StopBeforeReceipt
    | StopAfterEffect

[<RequireQualifiedAccess>]
type MigrationAdvanceResult =
    | Settled of resultSha256:string
    | AlreadySettled of resultSha256:string
    | Pending of reason:string
    | Interrupted of point:string

[<RequireQualifiedAccess>]
type MigrationExecutionFailure =
    | InvalidStep
    | StaleEpoch
    | UnauthorizedEpoch
    | IncompleteEpoch
    | StaleAuthorityFence
    | UnauthorizedAuthorityFence
    | IncompleteAuthorityFence
    | ChangedTarget
    | UnauthorizedTarget
    | IncompleteTarget
    | JournalConflict
    | JournalUnavailable of reason:string
    | EffectRefused of reason:string

type IMigrationStepRuntime =
    abstract ObserveEpoch: unit -> Result<MigrationEpochObservation, string>
    abstract ObserveAuthorityFence: unit -> Result<MigrationFenceObservation, string>
    abstract ObserveTarget: MigrationEffect -> Result<MigrationTargetObservation, string>
    abstract ObserveJournal: operationId:string -> Result<MigrationJournalAuthority option, string>
    abstract PersistIntent:
        expectedGeneration:int64 * expectedHead:string * operationId:string * stepSeal:string -> MigrationCasOutcome
    abstract MarkInFlight:
        expectedGeneration:int64 * expectedHead:string * operationId:string -> MigrationCasOutcome
    abstract ObserveEffect:
        operationId:string * effect:MigrationEffect -> Result<MigrationEffectObservation, string>
    abstract Dispatch:
        step:MigrationExecutionStep * grantGeneration:int64 * grantCommit:string -> MigrationDispatchOutcome
    abstract PersistSettlement:
        expectedGeneration:int64 * expectedHead:string * operationId:string * resultSha256:string -> MigrationCasOutcome

module MigrationStepExecution =
    val sealStep: step:MigrationExecutionStep -> Result<MigrationExecutionStep, MigrationExecutionFailure list>
    val advance:
        step:MigrationExecutionStep -> cut:MigrationAdvanceCut -> runtime:IMigrationStepRuntime ->
            Result<MigrationAdvanceResult, MigrationExecutionFailure list>

type MigrationStepAuthorityPort =
    { ObserveEpoch: unit -> Result<MigrationEpochObservation, string>
      ObserveAuthorityFence: unit -> Result<MigrationFenceObservation, string>
      ObserveJournal: string -> Result<MigrationJournalAuthority option, string>
      PersistIntent: int64 -> string -> string -> string -> MigrationCasOutcome
      MarkInFlight: int64 -> string -> string -> MigrationCasOutcome
      PersistSettlement: int64 -> string -> string -> string -> MigrationCasOutcome }

type IMigrationStepProviderTransport =
    abstract Send: GitHubRequest -> TransportOutcome

type HttpMigrationStepProviderTransport =
    new: unit -> HttpMigrationStepProviderTransport
    interface IMigrationStepProviderTransport
    interface IDisposable

type MigrationStepProviderOptions =
    { GraphQLUri: Uri
      Headers: Map<string, string> }

[<RequireQualifiedAccess>]
module MigrationIssueTypeStepRuntime =
    /// Digest used by the execution step for an issue's exact native type state.
    val targetSha256: issueNodeId:string -> typeNodeId:string option -> string

    /// Bind one sealed SetIssueType step to durable journal authority and the GitHub provider.
    /// Every other effect and any mismatched desired digest is refused before transport use.
    val create:
        step:MigrationExecutionStep ->
        options:MigrationStepProviderOptions ->
        authority:MigrationStepAuthorityPort ->
        transport:IMigrationStepProviderTransport ->
            Result<IMigrationStepRuntime, string>

[<RequireQualifiedAccess>]
module MigrationBlockingEdgeStepRuntime =
    /// Digest of both complete reciprocal local relation populations for one edge target.
    val targetSha256:
        repositoryId:int64 -> blockerNodeId:string -> blockedNodeId:string ->
        blockingNodeIds:string list -> blockedByNodeIds:string list -> string

    /// Bind one sealed AddBlockingEdge step to durable authority and paginated native readback.
    val create:
        step:MigrationExecutionStep ->
        options:MigrationStepProviderOptions ->
        authority:MigrationStepAuthorityPort ->
        transport:IMigrationStepProviderTransport ->
            Result<IMigrationStepRuntime, string>
