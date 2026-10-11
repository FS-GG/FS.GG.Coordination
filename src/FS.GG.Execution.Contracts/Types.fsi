namespace FS.GG.Execution.Contracts

open System
open System.Threading
open System.Threading.Tasks

type ValidationError = { Code: string; Field: string }
type EvidenceRef = { Kind: string; Reference: string; Digest: string option }
type Diagnostic = { Code: string; Message: string; Evidence: EvidenceRef option }
type ClockDomain = { Id: Guid; Frequency: int64 }
type ClockStamp = { Domain: ClockDomain; Ticks: int64 }
type IMonotonicClock =
    abstract Now: unit -> ClockStamp

type ExecutionBinding =
    { AssignmentId: Guid; AttemptId: Guid; Generation: int64 }
type OperationIdentity =
    { InvocationId: Guid; Execution: ExecutionBinding option; LaunchOrdinal: uint64
      SourceDigest: string; ProfileId: string; ProfileDigest: string }
type ProcessGeneration =
    { Platform: string; ScopeId: string; Pid: int; BirthToken: string }
type OwnerReference = { OwnerId: string; Evidence: EvidenceRef }
type ObservationStamp =
    { At: ClockStamp option; Scope: string; Generation: ProcessGeneration option; Provenance: EvidenceRef }
type Observation<'T> =
    | Known of 'T * ObservationStamp
    | Pending of Diagnostic
    | Unavailable of Diagnostic
    | Absent of ObservationStamp
    | Unknown of Diagnostic
    | Invalid of Diagnostic
    | LimitExceeded of Diagnostic

type EnvironmentSpec =
    | Inherit of overrides: Map<string,string option>
    | Allowlist of names: string list * overrides: Map<string,string option>
    | Replace of values: Map<string,string>
type InputSpec = Closed | Bytes of ReadOnlyMemory<byte>
type Capability =
    | StableLeaderIdentity | OwnedDescendantObservation | ContainmentBeforeTarget
    | PidNamespaceIsolation | UserNamespaceIsolation | MountNamespaceIsolation | NetworkIsolation
    | WorkloadEmptyObservation | OwnedChildReaping | AggregateMemoryLimit
    | AggregateCpuQuota | KernelTaskLimit | OwnerSurvivalCleanup
    | BoundedReturnWithRetainedCustody
type CapabilityRequirement = { Capability: Capability; Required: bool }
type CapabilityObservation =
    { Capability: Capability; Obtained: Observation<EvidenceRef> }
type LimitAssurance = Enforced | Sampled of interval: TimeSpan
type RequestedLimit<'T> = { Value: 'T; Assurance: LimitAssurance }
type CpuQuota = { Quota: TimeSpan; Period: TimeSpan }
type ResourceLimits =
    { MemoryBytes: RequestedLimit<int64> option
      CpuQuota: RequestedLimit<CpuQuota> option
      CpuAffinity: int list option
      MaxKernelTasks: RequestedLimit<int> option
      MaxOwnedProcesses: RequestedLimit<int> option
      MaxClrProcesses: RequestedLimit<int> option }
type ObtainedLimit =
    { Name: string; Unit: string; Requested: string
      Actual: Observation<string>; Assurance: Observation<LimitAssurance> }
type CapturePolicy =
    { TotalBytes: int64; TerminalReserveBytes: int
      StdoutPrefixBytes: int; StdoutTailBytes: int
      StderrPrefixBytes: int; StderrTailBytes: int
      ReadChunkBytes: int; MaximumFrameBytes: int; MaximumIncompleteLineBytes: int }
type CommandSpec =
    { Executable: string; Arguments: string list; WorkingDirectory: string
      Environment: EnvironmentSpec; Input: InputSpec; Capture: CapturePolicy
      Capabilities: CapabilityRequirement list; Resources: ResourceLimits }

// A description cannot be used to restore live budget authority.
type ExecutionBudget = internal { Domain: ClockDomain; StartedAt: int64; WorkExpiresAt: int64; TotalExpiresAt: int64 }

type ExecutionPhase = Prepared | Starting | Running | Retiring | Terminal
type StopReason = CallerCancellation | WorkDeadline | OutputLimit | SinkFailure | OwnerLost
type StopObservation = NotRequested | Requested of StopReason * ClockStamp | StopRefused of Diagnostic
type LaunchObservation = NotStarted | Refused of Diagnostic | Started of ProcessGeneration | Ambiguous of Diagnostic
type ProcessExit = Exited of int | Signaled of int
type ReaderState = Eof | Reading | StoppedWithoutEof of Diagnostic | ReadFailed of Diagnostic
type TaskState = Settled | Outstanding | TaskUnknown of Diagnostic
type ByteTotals = { Observed: int64; Charged: int64; Retained: int64; Omitted: int64 }
type StreamCapture =
    { Prefix: ReadOnlyMemory<byte>; Tail: ReadOnlyMemory<byte>
      Totals: ByteTotals; Reader: Observation<ReaderState> }
type Failure =
    { Phase: ExecutionPhase; Diagnostic: Diagnostic; At: ClockStamp option }
type CleanupDisposition = Complete | Incomplete | CleanupUnknown
type CustodyState =
    | NoEffect
    | RetainedByOwner of OwnerReference
    | Released of ObservationStamp
    | AuthorityUnavailable of Diagnostic
type CleanupResult =
    { Disposition: CleanupDisposition
      RequestedActions: Diagnostic list
      LeaderTermination: Observation<bool>
      WorkloadEmpty: Observation<bool>
      OwnedChildrenReaped: Observation<bool>
      LocalTasks: Observation<TaskState>
      Unresolved: ProcessGeneration list; OmittedUnresolved: int64
      Custody: CustodyState }
type ExecutionResult =
    { Identity: OperationIdentity; Phase: ExecutionPhase; Launch: LaunchObservation
      Exit: Observation<ProcessExit>; Stop: StopObservation
      Stdout: StreamCapture; Stderr: StreamCapture
      ChargedTotalBytes: int64; EvidenceBytes: int64; TerminalEvidenceBytes: int64
      Capabilities: CapabilityObservation list; Limits: ObtainedLimit list
      Violations: Diagnostic list; FirstFailure: Failure option
      AdditionalFailures: Failure list; OmittedFailures: int64; OmittedDiagnosticDetails: int64
      Cleanup: CleanupResult; OriginalBudget: ExecutionBudget }

type BudgetPhase = Work | Cleanup
type BudgetFailure = ClockDomainMismatch | ClockMovedBackwards | InvalidBudget | BudgetExpired

type BudgetDescription =
    { Domain: ClockDomain; StartedAt: int64; WorkExpiresAt: int64; TotalExpiresAt: int64 }
type BudgetView =
    { WorkRemaining: TimeSpan; TotalRemaining: TimeSpan
      WorkExpired: bool; TotalExpired: bool }


type OutputChannel = Stdout | Stderr | SupervisorEvidence | Protocol | TerminalEvidence
type AppendResult = Accepted | RetireRequired of Diagnostic
type OutputSnapshot =
    { Stdout: StreamCapture; Stderr: StreamCapture
      ChargedTotal: int64; EvidenceBytes: int64; TerminalEvidenceBytes: int64; FirstFailure: Diagnostic option }
type IBoundedByteSink =
    abstract Append: OutputChannel * ReadOnlyMemory<byte> -> AppendResult
    abstract Snapshot: unit -> OutputSnapshot


type ReleaseResult = ResourcesReleased | ReleaseRefused of Diagnostic
type IProcessSession =
    abstract Identity: OperationIdentity
    abstract Owner: OwnerReference
    abstract RunAsync: CancellationToken -> Task<ExecutionResult>
    abstract ObserveAsync: CancellationToken -> Task<ExecutionResult>
    abstract RequestStopAsync: StopReason * CancellationToken -> Task<StopObservation>
    abstract TryRelease: unit -> ReleaseResult

type IProcessSessionFactory =
    abstract Prepare:
        CommandSpec * OperationIdentity * ExecutionBudget * IMonotonicClock * OwnerReference
            -> Result<IProcessSession,ValidationError list>


type ContractLimits =
    { CodeBytes: int; MessageBytes: int; ReferenceBytes: int; CollectionEntries: int
      RequestBytes: int; RequestEntries: int; ResultMetadataBytes: int; FirstCauseReserveBytes: int; CaptureAllocationBytes: int }
