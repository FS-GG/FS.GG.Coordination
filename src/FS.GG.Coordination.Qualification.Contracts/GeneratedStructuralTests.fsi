module FS.GG.Coordination.Qualification.Contracts.GeneratedStructuralTests

type ValidationSummary =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ManifestSha256: string
      CategoryCounts: (string * int) list
      TotalCount: int
      SelfSha256: string }

val generate: root: string -> Result<byte array, string>
val validate: root: string -> artifactBytes: byte array -> Result<ValidationSummary, string>
val write: root: string -> outputPath: string -> Result<ValidationSummary, string>
val check: root: string -> artifactPath: string -> Result<ValidationSummary, string>
