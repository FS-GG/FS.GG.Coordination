namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubMigrationCopyRepository =
    { Id: int64
      NodeId: string
      FullName: string
      SourceHead: string
      TargetHead: string }

/// One member of the exhaustive receiver declaration for the isolated copy cohort.
/// Exhaustiveness remains a caller assertion until independently qualified.
type GitHubMigrationCopyReceiver =
    { Receiver: string
      RepositoryId: int64
      RefName: string
      ExpectedHead: string }

type GitHubMigrationCopyCohort =
    { Repositories: GitHubMigrationCopyRepository list
      /// Exact declared copy receivers; this is not inferred from any production fleet.
      Receivers: GitHubMigrationCopyReceiver list
      ProjectOrganization: string
      ProjectNumber: int
      ProjectNodeId: string
      SourceRevision: string
      Isolated: bool }

type GitHubMigrationInspectPage =
    { RequestedUri: string
      RequestIdentitySha256: string
      RawBody: string
      PayloadSha256: string
      NextRequestIdentitySha256: string option
      Subjects: GitHubDiscoverySubject list }

/// ScopeVerified and SubjectsParsedFromRaw are adapter assertions, not independent provider proof.
/// A live adapter must prove exact request scope and parse subjects from retained raw bytes.
type GitHubMigrationInspectAuthority =
    { CohortSha256: string
      ScopeVerified: bool
      SubjectsParsedFromRaw: bool
      Read: GitHubDiscoveryAuthorityRead
      Pages: GitHubMigrationInspectPage list }

type GitHubMigrationInspectWindow =
    { StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset }

type GitHubMigrationInspectRequest =
    { Cohort: GitHubMigrationCopyCohort
      RoadmapRevision: string
      RoadmapSha256: string
      UnitContractSha256: string
      ReceiptDigests: string list
      First: GitHubMigrationInspectWindow
      Second: GitHubMigrationInspectWindow }

type IGitHubMigrationInspectSource =
    /// Missing or unqualified authority adapters must return Error; no synthetic empty read.
    abstract ReadAuthority:
        passOrdinal:int * authority:string -> Result<GitHubMigrationInspectAuthority, string>

type IGitHubMigrationInspectStages =
    abstract BuildManifest:
        discovery:GitHubCompleteDiscovery -> Result<GitHubImmutableManifest, string>
    abstract BuildTransforms:
        manifest:GitHubImmutableManifest -> Result<GitHubTypedTransformQualification, string>
    abstract BuildOperations:
        transforms:GitHubTypedTransformQualification -> Result<GitHubLiveOperationQualification, string>

type GitHubMigrationInspectResult =
    { CohortSha256: string
      Discovery: GitHubCompleteDiscovery
      Manifest: GitHubImmutableManifest
      Transforms: GitHubTypedTransformQualification
      Operations: GitHubLiveOperationQualification }

[<RequireQualifiedAccess>]
type GitHubMigrationInspectFailure =
    | InvalidCohort
    | InvalidWindow
    | SourceRefused of passOrdinal:int * authority:string * reason:string
    | InvalidProviderEvidence of passOrdinal:int * authority:string * reason:string
    | ChangedProviderPages of authority:string
    | DiscoveryRefused of GitHubCompleteDiscoveryFinding list
    | MissingStageInput of stage:string * reason:string
    | InvalidStageBinding of stage:string
    | InvalidManifest of GitHubImmutableManifestFinding list
    | InvalidTransforms of GitHubTypedTransformFinding list
    | InvalidOperations of GitHubLiveOperationFinding list

[<RequireQualifiedAccess>]
module GitHubMigrationInspect =
    val cohortSha256: GitHubMigrationCopyCohort -> string
    val validCohort: GitHubMigrationCopyCohort -> bool
    val inspect:
        request:GitHubMigrationInspectRequest ->
        source:IGitHubMigrationInspectSource ->
        stages:IGitHubMigrationInspectStages ->
            Result<GitHubMigrationInspectResult, GitHubMigrationInspectFailure>
