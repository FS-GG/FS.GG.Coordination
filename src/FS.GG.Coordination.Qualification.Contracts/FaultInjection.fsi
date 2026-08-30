module FS.GG.Coordination.Qualification.Contracts.FaultInjection

[<RequireQualifiedAccess>]
type SubjectDefect =
    | None
    | SkipRetry
    | DuplicateIsApplied
    | PreserveArrivalOrder
    | AcceptPartialPage
    | IgnoreRateBudget
    | IgnorePermission
    | IgnoreRevision

type TraceEvent =
    { Ordinal: int
      Kind: string
      Step: string
      Revision: int }

type Execution =
    { Id: string
      Fault: string
      Step: string
      Outcome: string
      RefusalCode: string option
      InitialStateSha256: string
      FinalStateSha256: string
      Trace: TraceEvent list }

type ValidationSummary =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ExternalStepCount: int
      ScenarioCount: int
      ConvergedCount: int
      RefusedCount: int
      SelfSha256: string }

val execute: root: string -> defect: SubjectDefect -> Result<Execution list, string>
val generate: root: string -> Result<byte array, string>
val validate: root: string -> artifactBytes: byte array -> Result<ValidationSummary, string>
val write: root: string -> outputPath: string -> Result<ValidationSummary, string>
val check: root: string -> artifactPath: string -> Result<ValidationSummary, string>
