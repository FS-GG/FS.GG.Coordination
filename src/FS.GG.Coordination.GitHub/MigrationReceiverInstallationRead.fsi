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
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      UserAgent: string }

type IMigrationReceiverTokenMintTransport =
    abstract Mint: RestRequest -> TransportOutcome

type MigrationReceiverTokenMintAttestation =
    { RequestIdentitySha256: string
      ResponseSha256: string
      TokenSha256: string
      ExpiresAt: System.DateTimeOffset
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      Fingerprint: string }

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
      MintAttestation: MigrationReceiverTokenMintAttestation
      ComposerRosterOptions: MigrationReceiverRosterReadOptions
      ComposerRosterCapture: MigrationReceiverRosterCapture
      CaptureFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverInstallationRead =
    /// Mints one exact scoped token through a separate protected port, then reads
    /// the selected installation and complete repository census twice using that bearer.
    /// The token is never returned; only digests and scope facts are retained.
    val captureForComposer:
        options: MigrationReceiverInstallationReadOptions ->
        mintTransport: IMigrationReceiverTokenMintTransport ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverInstallationCapture, string>
