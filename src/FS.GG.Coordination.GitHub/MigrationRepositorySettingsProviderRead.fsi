namespace FS.GG.Coordination.GitHub

type MigrationRepositoryTagSetting =
    { Name: string
      NodeId: string
      TargetSha: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationRepositoryReleaseSetting =
    { DatabaseId: int64
      NodeId: string
      TagName: string
      TargetCommitish: string
      Name: string option
      Draft: bool
      Prerelease: bool
      Immutable: bool
      PayloadJson: string
      PayloadSha256: string }

/// Complete tags plus releases, including drafts proven visible by repository push permission.
type MigrationRepositoryReleasesAndTagsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Tags: MigrationRepositoryTagSetting list
      Releases: MigrationRepositoryReleaseSetting list }

[<RequireQualifiedAccess>]
module MigrationRepositorySettingsProviderRead =
    /// GET-only reader for the ReleasesAndTags surface. Repository identity and
    /// revision are reread, and push permission is required because GitHub omits
    /// draft releases for identities without push access.
    val readReleasesAndTags:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryReleasesAndTagsRead, MigrationRepositorySettingsSurfaceRefusal>

/// Concrete partial provider. ReleasesAndTags is implemented. Environments and
/// every other unimplemented surface refuse and cannot complete canonical settings.
type MigrationRepositorySettingsGitHubProvider =
    new:
        options:MigrationGitHubReadOptions * transport:IMigrationGitHubReadTransport ->
            MigrationRepositorySettingsGitHubProvider
    interface IMigrationRepositorySettingsSurfaceProvider
