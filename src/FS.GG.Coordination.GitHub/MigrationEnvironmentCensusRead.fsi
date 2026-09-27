namespace FS.GG.Coordination.GitHub

type EnvironmentCensusRepositoryIdentity =
    { EnvironmentRepositoryId: int64
      EnvironmentRepositoryNodeId: string
      EnvironmentRepositoryFullName: string }

type EnvironmentCensusItem =
    { EnvironmentId: int64
      EnvironmentNodeId: string
      EnvironmentName: string }

type EnvironmentCensusRawPage =
    { EnvironmentRequestedUri: string
      EnvironmentRequestIdentitySha256: string
      EnvironmentRawBody: string
      EnvironmentRawSha256: string
      EnvironmentNextUri: string option }

type EnvironmentCensusPass =
    { EnvironmentRepository: EnvironmentCensusRepositoryIdentity
      EnvironmentTotalCount: int
      Environments: EnvironmentCensusItem list
      EnvironmentPages: EnvironmentCensusRawPage list
      EnvironmentFingerprint: string }

type EnvironmentCensusCapture =
    { EnvironmentFirst: EnvironmentCensusPass
      EnvironmentSecond: EnvironmentCensusPass
      EnvironmentCaptureFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationEnvironmentCensusRead =
    val guardReadTransport:
        options:MigrationGitHubReadOptions ->
        inner:IMigrationGitHubReadTransport ->
            IMigrationGitHubReadTransport

    val validatePass:
        options:MigrationGitHubReadOptions ->
        observed:EnvironmentCensusPass ->
            Result<EnvironmentCensusPass, string>

    val capturePass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<EnvironmentCensusPass, string>

    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<EnvironmentCensusCapture, string>

    /// Proves an empty environment census only from two independently validated terminal passes.
    val proveEmpty:
        options:MigrationGitHubReadOptions ->
        observed:EnvironmentCensusCapture ->
            Result<EnvironmentCensusCapture, string>

