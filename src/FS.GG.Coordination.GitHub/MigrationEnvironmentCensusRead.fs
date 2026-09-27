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
    let guardReadTransport (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) =
        { new IMigrationGitHubReadTransport with
            member _.Send _ = NetworkFailure }

    let validatePass (_: MigrationGitHubReadOptions) (_: EnvironmentCensusPass) : Result<EnvironmentCensusPass, string> =
        Error "environment-census-read-unavailable"

    let capturePass (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) : Result<EnvironmentCensusPass, string> =
        Error "environment-census-read-unavailable"

    let captureTwoPass (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) : Result<EnvironmentCensusCapture, string> =
        Error "environment-census-read-unavailable"

    let proveEmpty (_: MigrationGitHubReadOptions) (_: EnvironmentCensusCapture) : Result<EnvironmentCensusCapture, string> =
        Error "environment-census-read-unavailable"
