module FS.GG.Coordination.Qualification.Contracts.FaultInjection

type ValidationSummary =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ExternalStepCount: int
      ScenarioCount: int
      ConvergedCount: int
      RefusedCount: int
      SelfSha256: string }

val generate: root: string -> Result<byte array, string>
val validate: root: string -> artifactBytes: byte array -> Result<ValidationSummary, string>
val write: root: string -> outputPath: string -> Result<ValidationSummary, string>
val check: root: string -> artifactPath: string -> Result<ValidationSummary, string>
