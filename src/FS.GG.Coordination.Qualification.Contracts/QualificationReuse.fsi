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

type PriorRun =
    { Head: string
      RunId: int64
      Attempt: int
      EvidenceSha256: string
      ArtifactExpiresAt: string
      RunnerMinutes: decimal }

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

val sha256: byte array -> string
val createSubject: TrackedFile list -> byte array -> byte array -> byte array -> byte array -> QualificationSubject
val subjectBytes: QualificationSubject -> byte array
val decide: candidate: string -> subjectSha256: string -> prior: PriorRun option -> priorSubjectSha256: string option -> Decision
val refuse: candidate: string -> subjectSha256: string -> reason: string -> Decision
val decisionBytes: Decision -> byte array
val parseDecision: byte array -> Result<Decision, string>
