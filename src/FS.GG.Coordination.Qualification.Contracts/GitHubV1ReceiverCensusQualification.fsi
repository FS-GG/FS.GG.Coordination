namespace FS.GG.Coordination.Qualification.Contracts

type ReceiverSourceIdentity =
    { Id: string
      Receiver: string
      Path: string
      BlobSha1: string
      Sha256: string }

type WriterRoute =
    { Id: string
      Receiver: string
      SourceId: string
      Entrypoint: string
      Callsites: string list
      EffectClass: string
      RemoteEffects: string list
      CredentialBoundary: string
      LaterDisposition: string }

type CallableDependency =
    { Receiver: string
      SourceId: string
      Target: string
      Reference: string
      Resolution: string
      ResolvedRevision: string option
      CalleeSha256: string option }

type InstalledToolIdentity =
    { Version: string
      Revision: string
      Tree: string
      OptionsSha256: string
      ProjectSha256: string }

type ReceiverCensusSnapshot =
    { Schema: string
      ProducerRevision: string
      ProducerTree: string
      CensusSha256: string
      SourceManifestsSha256: string
      SourceBlobsSha256: string
      AcceptedEpochReceiptSha256: string
      Receivers: (string * string * string * string * string) list
      Sources: ReceiverSourceIdentity list
      Routes: WriterRoute list
      Dependencies: CallableDependency list
      InstalledTools: InstalledToolIdentity list
      SourceCoverageComplete: bool
      Installed: bool
      Fenced: bool
      Accepted: bool
      TelemetryLocalOnly: bool }

type ReceiverCensusFinding =
    { Code: string
      Subject: string
      Message: string }

type ReceiverCensusControl =
    | SchemaBinding
    | AcceptedEpochPrerequisite
    | ProducerSourceBinding
    | CensusByteBinding
    | ManifestByteBinding
    | SourceBlobByteBinding
    | CompleteReceiverRoster
    | CompleteSourcePopulation
    | CompleteRoutePopulation
    | CompleteDependencyPopulation
    | StableUniqueOrdering
    | SourceIdentityBinding
    | RouteEffectClassification
    | DelegatedWriterClosure
    | CallableDependencyClosure
    | LegacyToolCorrespondence
    | UnknownSourceRefusal
    | OfflineValidation
    | LocalTelemetryBoundary
    | NoInstallationClaim
    | NoFenceClaim
    | NoAcceptanceClaim

type ReceiverCensusControlResult =
    { Control: ReceiverCensusControl
      BaselineGreen: bool
      MutationRed: bool }

module GitHubV1ReceiverCensusQualification =
    val requiredControls: ReceiverCensusControl list
    val controlId: ReceiverCensusControl -> string
    val validateSnapshot: ReceiverCensusSnapshot -> Result<unit, ReceiverCensusFinding list>
    val validateControls: generated: ReceiverCensusControlResult list -> independent: ReceiverCensusControlResult list -> Result<unit, ReceiverCensusFinding list>
