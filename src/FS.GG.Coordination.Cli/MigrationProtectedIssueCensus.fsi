namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Source-only lookup identity; a protected host must authenticate its fields.
type ProtectedIssueCensusSelection =
    { RunId: int64
      RunAttempt: int
      RunNonce: string
      CandidateSha: string
      WorkflowSha: string
      ApiOrigin: string
      Owner: string
      Repository: string
      RepositoryId: int64 }

type ProtectedIssueCensusPins =
    { ReaderResourceId: string
      ReaderArtifactSha256: string
      ProviderResourceId: string
      CustodyStoreResourceId: string
      CustodyStoreArtifactSha256: string
      CustodyStoreAclPolicySha256: string
      CustodyReaderPrincipalId: string
      CustodyWriterPrincipalId: string
      CandidatePrincipalId: string }

type ProtectedIssueCensusRead =
    { ReadOrdinal: int64
      RequestMethod: string
      RequestUri: string
      ResponseUri: string
      StatusCode: int
      ProviderResourceId: string
      ResponseHeaders: (string * string) list
      LinkHeader: string option
      RawBody: string
      RawBodyBytesBase64: string
      CustodyObjectId: string }

type ProtectedIssueCensusBatch =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      Complete: bool
      SealedPageCount: int
      Identity: ProtectedIssueCensusRead
      Pages: ProtectedIssueCensusRead list }

type IProtectedIssueCensusPort =
    abstract Describe: unit -> ProtectedIssueCensusPins
    abstract Read: ProtectedIssueCensusSelection -> ProtectedIssueCensusBatch option

type ProtectedIssueCensusStoredRead =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      Read: ProtectedIssueCensusRead }

type ProtectedIssueCensusStoreDescription =
    { ResourceId: string
      ArtifactSha256: string
      AclPolicySha256: string
      ReaderPrincipalId: string
      WriterPrincipalId: string
      CandidatePrincipalId: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      ImmutableObjects: bool }

type ProtectedIssueCensusStoreInventory =
    { Selection: ProtectedIssueCensusSelection
      CustodyStoreResourceId: string
      Complete: bool
      HighWaterOrdinal: int64
      SealSha256: string
      ObjectIds: string list }

/// An independent candidate-inaccessible store port must back this source contract.
type IProtectedIssueCensusStorePort =
    abstract Describe: unit -> ProtectedIssueCensusStoreDescription
    abstract ReadInventory: ProtectedIssueCensusSelection -> ProtectedIssueCensusStoreInventory option
    abstract ReadObject: ProtectedIssueCensusSelection * string -> ProtectedIssueCensusStoredRead option

type ProtectedIssueCensusProof =
    { Inspect: GitHubMigrationInspectAuthority
      CustodyObjectIds: string list
      CorpusSha256: string }

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensus =
    /// Pure canonical commitment; an installed protected authority must authenticate it.
    val expectedInventoryCommitment:
        pins:ProtectedIssueCensusPins ->
        selection:ProtectedIssueCensusSelection ->
        reads:ProtectedIssueCensusRead list -> string

    /// Fake-port contract only. A protected host must install and authenticate the port and pins.
    val bind:
        pins:ProtectedIssueCensusPins ->
        selection:ProtectedIssueCensusSelection ->
        port:IProtectedIssueCensusPort option ->
        store:IProtectedIssueCensusStorePort option ->
        options:MigrationInspectProviderOptions ->
        population:MigrationIssuePopulation ->
            Result<ProtectedIssueCensusProof, string>
