module FS.GG.Coordination.Qualification.Contracts.QualificationReuse

type TrackedFile =
    { Mode: string
      Path: string
      Bytes: byte array }

type QualificationSubject =
    { TreeSha256: string
      PlanSha256: string
      WorkflowSha256: string
      ToolchainSha256: string
      DependencySha256: string
      GateSetSha256: string
      EnvironmentSha256: string
      ReviewPolicySha256: string
      SubjectSha256: string }

type FormalSubjectSelector =
    | Exact of string
    | Prefix of string

type FormalSubject =
    { FilesSha256: string
      SelectorPolicySha256: string
      FileCount: int
      SubjectSha256: string }

type PriorRun =
    { Head: string
      RunId: int64
      Attempt: int
      EvidenceSha256: string
      ArtifactExpiresAt: string
      RunnerMinutes: decimal option }

type DecisionKind =
    | Reuse
    | Execute
    | Refuse

type Decision =
    { Kind: DecisionKind
      Reason: string
      Candidate: string
      SubjectSha256: string
      Prior: PriorRun option
      SelfSha256: string }

/// Independently computed identity of one qualification obligation.  BindingSha256 is
/// intentionally separate: generated/non-behavioural bindings may change when a focused
/// correspondence proof says that their compiled behaviour did not.
type ReuseIdentity =
    { BehavioralSha256: string
      CompiledContractSha256: string
      ToolchainProfileSha256: string
      VerificationBoundsSha256: string
      FormalCorpusSha256: string
      HarnessSha256: string
      BindingSha256: string }

type CandidateObligation =
    { Candidate: string
      BaseRevision: string
      TreeSha256: string
      SourceSha256: string
      Identity: ReuseIdentity
      ObligationSha256: string }

type PriorExecution =
    { Candidate: CandidateObligation
      RunId: int64
      Attempt: int
      ExecutedReceiptSha256: string
      CompletedAt: string
      ExpiresAt: string
      Authentic: bool
      Complete: bool }

type SemanticDelta =
    { EvaluatorSha256: string
      DeltaSha256: string
      IsEmpty: bool }

type ReuseDisposition =
    | Current
    | Reused
    | Deferred
    | Failed

type CoherentState =
    | Pending
    | Running
    | Passed
    | Blocked
    | Disputed

type ReuseSelection =
    { Candidate: CandidateObligation
      Disposition: ReuseDisposition
      Reason: string
      Prior: PriorExecution option
      SemanticDelta: SemanticDelta
      BindingCorrespondenceSha256: string option
      CoherentRunPending: bool
      CoherentState: CoherentState
      SelectionSha256: string }

type PartitionPlan =
    { Candidate: CandidateObligation
      QualificationPlanSha256: string
      Obligations: string list
      PartitionCount: int
      Partitions: (int * string list) list
      PlanSha256: string }

type PartitionReceipt =
    { PlanSha256: string
      Partition: int
      Obligations: string list
      Passed: bool
      ReceiptSha256: string }

type CoherentAggregateReceipt =
    { CandidateObligationSha256: string
      PlanSha256: string
      PartitionReceiptSha256: string list
      Passed: bool
      ReceiptSha256: string }

val sha256: byte array -> string
val createSubject: TrackedFile list -> byte array -> byte array -> byte array -> byte array -> QualificationSubject
val createFormalSubject: TrackedFile list -> FormalSubjectSelector list -> policyBytes: byte array -> FormalSubject
val formalSubjectBytes: FormalSubject -> byte array
val subjectBytes: QualificationSubject -> byte array
val decide: candidate: string -> subjectSha256: string -> prior: PriorRun option -> priorSubjectSha256: string option -> Decision
val refuse: candidate: string -> subjectSha256: string -> reason: string -> Decision
val decisionBytes: Decision -> byte array
val parseDecision: byte array -> Result<Decision, string>
val createCandidateObligation: candidate: string -> baseRevision: string -> treeSha256: string -> sourceSha256: string -> identity: ReuseIdentity -> CandidateObligation
val candidateObligationBytes: CandidateObligation -> byte array
val parseCandidateObligation: byte array -> Result<CandidateObligation, string>
val selectReusable: now: System.DateTimeOffset -> candidate: CandidateObligation -> prior: PriorExecution option -> semanticDelta: SemanticDelta -> bindingCorrespondenceSha256: string option -> ReuseSelection
val applyCoherentOutcome: merged: bool -> passed: bool -> ReuseSelection -> ReuseSelection
val selectionBytes: ReuseSelection -> byte array
val createPartitionPlan: candidate: CandidateObligation -> qualificationPlanSha256: string -> maxPartitions: int -> obligations: string list -> PartitionPlan
val partitionPlanBytes: PartitionPlan -> byte array
val parsePartitionPlan: candidate: CandidateObligation -> byte array -> Result<PartitionPlan, string>
val createPartitionReceipt: plan: PartitionPlan -> partition: int -> obligations: string list -> passed: bool -> PartitionReceipt
val partitionReceiptBytes: PartitionReceipt -> byte array
val parsePartitionReceipt: byte array -> Result<PartitionReceipt, string>
val aggregatePartitions: PartitionPlan -> PartitionReceipt list -> Result<bool, string>
val createCoherentAggregateReceipt: PartitionPlan -> PartitionReceipt list -> Result<CoherentAggregateReceipt, string>
val coherentAggregateReceiptBytes: CoherentAggregateReceipt -> byte array
val parseCoherentAggregateReceipt: byte array -> Result<CoherentAggregateReceipt, string>
