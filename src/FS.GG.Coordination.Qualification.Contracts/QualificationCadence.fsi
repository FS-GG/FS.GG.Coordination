module FS.GG.Coordination.Qualification.Contracts.QualificationCadence

type Outcome = Passed | ActionableDefect | InfrastructureFailure | UnattributedFailure
type Boundary = Child | Closure | Production
type RecommendationKind = Retain | Increase | Reduce | InsufficientData

type Observation =
    { Gate: string
      RunId: int64
      Attempt: int
      ObservedAt: System.DateTimeOffset
      DurationSeconds: int
      RunnerMinutes: decimal
      Reused: bool
      Outcome: Outcome
      Boundary: Boundary
      ClosureEquivalent: bool
      DetectionDelayHours: decimal option }

type Policy =
    { Version: string
      WindowDays: int
      FreshnessHours: int
      MinimumObservations: int
      ExpensiveRunnerMinutes: decimal
      LowYieldMaximum: decimal
      MinimumCadence: Map<string, string> }

type Recommendation =
    { Gate: string
      Kind: RecommendationKind
      ReasonCodes: string list
      ObservationCount: int
      UniqueDefectCount: int
      RunnerMinutes: decimal
      CostSavedRunnerMinutes: decimal
      ExpectedDetectionDelayHours: decimal option
      ClosureEquivalent: bool
      BlastRadius: string
      Confidence: string
      PolicyVersion: string }

val evaluate: now: System.DateTimeOffset -> policy: Policy -> gate: string -> observations: Observation list -> Recommendation
val recommendationBytes: Recommendation -> byte array
