namespace FS.GG.Coordination.Qualification.Contracts

type GitHubV1WriterCommand =
    { Name: string
      Writes: string }

type GitHubV1WriterSource =
    { Path: string
      Disposition: string
      SinkKinds: string list
      Sha256: string
      NonWriterJustification: string option }

type GitHubV1WriterCensusSnapshot =
    { Schema: string
      ProducerRevision: string
      ProducerTree: string
      RoadmapRevision: string
      RoadmapSha256: string
      AcceptedEpochReceiptSha256: string
      SourceBaseRevision: string
      Q0EvidenceSha256: string
      Q0CorpusSha256: string
      CensusSha256: string
      CommandContractSha256: string
      CheckerSha256: string
      FixtureRunnerSha256: string
      Commands: GitHubV1WriterCommand list
      Sources: GitHubV1WriterSource list }

type GitHubV1WriterCensusFinding =
    { Code: string
      Subject: string
      Message: string }

type GitHubV1WriterCensusControl =
    | SchemaBinding
    | AcceptedEpochPrerequisite
    | RoadmapBinding
    | HistoricalQ0Binding
    | ProducerSourceBinding
    | CensusByteBinding
    | CandidateCommandContract
    | CheckerBinding
    | CompleteCommandRoots
    | CompleteSourcePopulation
    | StableUniqueOrdering
    | ExactWriteClassification
    | SourceIdentity
    | SinkDisposition
    | UnknownCommandRefusal
    | DynamicWriterRefusal
    | NoFenceClaim
    | NoReceiverClaim

type GitHubV1WriterCensusControlResult =
    { Control: GitHubV1WriterCensusControl
      BaselineGreen: bool
      MutationRed: bool }

module GitHubV1WriterCensusQualification =
    val requiredControls: GitHubV1WriterCensusControl list
    val controlId: GitHubV1WriterCensusControl -> string
    val validateSnapshot: GitHubV1WriterCensusSnapshot -> Result<unit, GitHubV1WriterCensusFinding list>
    val validateControls: generated: GitHubV1WriterCensusControlResult list -> independent: GitHubV1WriterCensusControlResult list -> Result<unit, GitHubV1WriterCensusFinding list>
