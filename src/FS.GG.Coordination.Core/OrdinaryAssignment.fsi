namespace FS.GG.Coordination.Core

open System
open Orchestration

/// Pure ordinary-work data. No value is an admission or a reservation allocation.
module OrdinaryAssignment =
    type WorkIdentity = Board of WorkItemId | Local of workspace: string * key: string
    type Source = { Repository: string; Revision: string; Path: string; Sha256: string }
    type Repository = { Identity: string; Revision: string }
    type Component = { Id: string; Root: string; Language: string; Commands: Set<string>; Sources: Source list }
    type WorkspaceExecutionContract = {
        Version: string; Workspace: string; Provider: string; Repositories: Repository list
        Components: Component list; Sources: Source list }
    type WorkAssessment = {
        Version: string; Phase: string; Outcome: string; Objective: string; Plan: string
        Facts: Set<string>; Interpretation: string; Difficulty: int; Uncertainty: int
        EffectRisk: int; MissingFacts: Set<string> }
    /// Named units: cpu-millicores, memory-bytes, storage-bytes, io-bytes-per-second,
    /// task-slots and quota:<provider>:<unit>. None is unknown, never zero.
    type Resources = Map<string, int64 option>
    type Recovery = { OriginalStartedAt: DateTimeOffset; OriginalDeadline: DateTimeOffset; MaxAttempts: int; AttemptOrdinal: int }
    type Requirements = {
        Key: string; Identity: WorkIdentity; Workspace: WorkspaceExecutionContract; Assessment: WorkAssessment
        Attempt: AttemptId; Generation: Generation option; SelectedAt: DateTimeOffset; Deadline: DateTimeOffset option
        Recovery: Recovery option; Dependencies: Set<string>; RequiredChecks: Set<string>
        Capabilities: Set<string>; Tools: Set<string>; Skills: Set<string>; Context: Set<string>
        AllowedPaths: Set<string>; AuthorizedEffects: Set<string>; RequiredOutputs: Set<string>; RequiredReviews: Set<string>
        Demand: Resources; Priority: int }
    type Configuration = { Model: string; Effort: string; Runtime: string }
    type CapabilityObservation = { Available: bool; Evidence: Source option }
    type Economics = { Currency: string; ProvenanceClass: string; CostMicroUnits: int64; DurationMilliseconds: int64; ReworkMilliseconds: int64 }
    type ExecutionProfile = {
        Id: string; Version: string; Requested: Configuration; Resolved: Configuration option; Observed: Configuration option
        Capabilities: Map<string, CapabilityObservation>; Tools: Set<string>; Skills: Set<string>; Context: Set<string>
        Effects: Set<string>; Outputs: Set<string>; Reviews: Set<string>; AdditionalDemand: Resources
        RecoverySupported: bool; Economics: Economics option }
    /// Completeness is supplied by the authoritative producer, not inferred by the planner.
    type Completeness = { ExpectedWorkKeys: Set<string>; Sources: Source list; SourceBound: bool; ManifestSha256: string }
    type AuthoritativeObligations = { Version: string; Works: Requirements list; Completeness: Completeness }
    type Reservation = { Id: ReservationId; Work: WorkIdentity; Demand: Resources; Paths: Set<string>; TerminalState: string }
    type Snapshot = {
        Version: string; AuthoritySha256: string; Works: Requirements list; Profiles: ExecutionProfile list
        Now: DateTimeOffset; Capacity: Resources; RecoveryHeadroom: Resources; Reservations: Reservation list
        ReservationPopulationComplete: bool; Project: ProjectState; SatisfiedDependencies: Set<string> }
    type AgentSpecification = {
        Requirements: Requirements; Profile: ExecutionProfile; Demand: Resources; Reservation: ReservationId
        Requested: Configuration; Resolved: Configuration option; Observed: Configuration option }
    type Assignment = {
        PolicyVersion: string; AuthoritySha256: string; SnapshotSha256: string; AssignmentSha256: string
        Specification: AgentSpecification; ExperimentEnrollment: string option }
    type Preference = Economic of currency: string * provenanceClass: string * cost: int64 * duration: int64 * rework: int64 | NonEconomicFallback
    type Exclusion = { WorkKey: string; ProfileId: string; Reasons: string list }
    type Decision = {
        Eligible: Assignment list; Excluded: Exclusion list; Selected: Assignment list
        Preference: Preference; Reasons: string list }
    type Refusal = NeedsEvidence of string list | ConflictingEvidence of string list | NeedsDecomposition of string list | WaitingForCapability of string list | RequiresOperatorDecision of string list

    val policyVersion: string
    val maximumEvaluations: int
    val maximumInputBytes: int
    val maximumResultBytes: int
    val canonicalMeasure: value: 'a -> int * int
    val canonicalSize: value: 'a -> int
    val digest: value: 'a -> string
    val authorityDigest: AuthoritativeObligations -> string
    val snapshotDigest: Snapshot -> string
    val assignmentDigest: Assignment -> string
    val bindAuthority: AuthoritativeObligations -> AuthoritativeObligations
    val bindAssignment: Assignment -> Assignment
    val knownNonnegative: Resources -> bool
    val addResources: Resources -> Resources -> Resources
    val pathsConflict: Set<string> -> Set<string> -> bool
    val requirementsErrors: Requirements -> string list
    val validateAuthority: AuthoritativeObligations -> Result<AuthoritativeObligations, Refusal>
    val snapshotEvaluationBound: Snapshot -> int64
    val validateSnapshot: Snapshot -> Result<Snapshot, Refusal>
