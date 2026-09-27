namespace FS.GG.Coordination.GitHub

type ImmutableReleasesOrganizationPolicy =
    | AllRepositories
    | NoRepositories
    | SelectedRepositories

type ImmutableReleasesRepositoryIdentity =
    { RepositoryId: int64
      RepositoryNodeId: string
      FullName: string }

type ImmutableReleasesSelectedRepository =
    { RepositoryId: int64
      RepositoryNodeId: string
      FullName: string }

type ImmutableReleasesRawPage =
    { ImmutableRequestedUri: string
      ImmutableRequestIdentitySha256: string
      ImmutableRawBody: string
      ImmutableRawSha256: string
      ImmutableNextUri: string option }

type ImmutableReleasesPass =
    { Identity: ImmutableReleasesRepositoryIdentity
      RepositoryEnabled: bool
      EnforcedByOwner: bool
      OrganizationPolicy: ImmutableReleasesOrganizationPolicy
      SelectedRepositories: ImmutableReleasesSelectedRepository list
      SelectedTotalCount: int option
      EffectiveEnabled: bool
      ImmutableReleasePages: ImmutableReleasesRawPage list
      Fingerprint: string }

type ImmutableReleasesCapture =
    { First: ImmutableReleasesPass
      Second: ImmutableReleasesPass
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationImmutableReleasesRead =
    val guardReadTransport:
        options:MigrationGitHubReadOptions ->
        inner:IMigrationGitHubReadTransport ->
            IMigrationGitHubReadTransport

    val validatePass:
        options:MigrationGitHubReadOptions ->
        observed:ImmutableReleasesPass ->
            Result<ImmutableReleasesPass, string>

    val capturePass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<ImmutableReleasesPass, string>

    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<ImmutableReleasesCapture, string>
