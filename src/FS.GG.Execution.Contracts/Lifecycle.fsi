namespace FS.GG.Execution.Contracts

type LifecycleState

type LifecycleEvent =
    | BeginLaunch
    | TargetReleaseRequested
    | LaunchObserved of LaunchObservation
    | CapabilitiesObtained of CapabilityObservation list
    | LimitObserved of ObtainedLimit
    | ExitObserved of Observation<ProcessExit>
    | StopRequested of StopReason
    | ReaderObserved of OutputChannel * Observation<ReaderState>
    | OutputObserved of OutputSnapshot
    | RetirementActionRequested of Diagnostic
    | CleanupObserved of CleanupResult
    | FailureObserved of Failure
    | ReturnRequested

[<RequireQualifiedAccess>]
module Lifecycle =
    val prepare: CommandSpec -> OperationIdentity -> ExecutionBudget -> Result<LifecycleState,ValidationError list>
    val apply: ClockStamp -> LifecycleEvent -> LifecycleState -> Result<LifecycleState,ValidationError list>
    val snapshot: LifecycleState -> ExecutionResult
    val metadataBytes: LifecycleState -> int64
