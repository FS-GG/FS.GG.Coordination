namespace FS.GG.Coordination.GitHub

type MigrationReceiverInstallationReadOptions =
    { ApiBase: System.Uri
      AppId: int64
      AppNodeId: string
      AppSlug: string
      InstallationId: int64
      AccountLogin: string
      AccountId: int64
      AccountNodeId: string
      RequiredPermissions: Map<string, string>
      SelectedRepositories: MigrationReceiverRosterDeclaredRepository list
      AppToken: string
      InstallationToken: string
      UserAgent: string }

type MigrationReceiverProviderApp =
    { ProviderAppId: int64
      ProviderAppNodeId: string
      ProviderAppSlug: string
      ProviderOwnerLogin: string
      ProviderOwnerId: int64
      ProviderOwnerNodeId: string
      ProviderPermissions: Map<string, string> }

type MigrationReceiverInstallationCapture =
    { App: MigrationReceiverProviderApp
      AppFirst: MigrationReceiverRosterRawPage
      AppSecond: MigrationReceiverRosterRawPage
      ComposerRosterOptions: MigrationReceiverRosterReadOptions
      ComposerRosterCapture: MigrationReceiverRosterCapture
      CaptureFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverInstallationRead =
    /// Reads the authenticated App, its selected installation settings and the complete
    /// installation-token repository census twice. Only an exact seven-repository scope is
    /// returned in the shape consumed by MigrationReceiverAuthorityComposer.
    val captureForComposer:
        options: MigrationReceiverInstallationReadOptions ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverInstallationCapture, string>
