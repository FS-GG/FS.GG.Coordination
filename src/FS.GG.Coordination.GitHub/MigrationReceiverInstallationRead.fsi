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
      ExpectedAppPermissions: Map<string, string>
      ExpectedInstallationPermissions: Map<string, string>
      RequiredTokenPermissions: Map<string, string>
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
      InstallationPermissions: Map<string, string>
      TokenPermissions: Map<string, string>
      TokenFirst: MigrationReceiverRosterRawPage
      TokenSecond: MigrationReceiverRosterRawPage
      ComposerRosterOptions: MigrationReceiverRosterReadOptions
      ComposerRosterCapture: MigrationReceiverRosterCapture
      CaptureFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverInstallationRead =
    /// Reads the authenticated App, its selected installation settings and the complete
    /// installation-token repository census twice. Only the single accepted sandbox repository
    /// scope is returned; its seven receiver refs are verified by the downstream provider read.
    val captureForComposer:
        options: MigrationReceiverInstallationReadOptions ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverInstallationCapture, string>
