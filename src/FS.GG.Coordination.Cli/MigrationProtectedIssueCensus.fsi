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
      CustodyStoreResourceId: string }

type ProtectedIssueCensusRead =
    { ReadOrdinal: int64
      RequestUri: string
      StatusCode: int
      LinkHeader: string option
      RawBody: string
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

type ProtectedIssueCensusProof =
    { Inspect: GitHubMigrationInspectAuthority
      CustodyObjectIds: string list
      CorpusSha256: string }

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensus =
    /// Fake-port contract only. A protected host must install and authenticate the port and pins.
    val bind:
        pins:ProtectedIssueCensusPins ->
        selection:ProtectedIssueCensusSelection ->
        port:IProtectedIssueCensusPort option ->
        options:MigrationInspectProviderOptions ->
        population:MigrationIssuePopulation ->
            Result<ProtectedIssueCensusProof, string>
