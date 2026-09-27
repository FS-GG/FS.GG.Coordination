namespace FS.GG.Coordination.GitHub

type MigrationReceiverRosterReadOptions =
    {
        ApiBase: System.Uri
        InstallationId: int64
        AccountLogin: string
        AccountId: int64
        AccountNodeId: string
        RequiredPermissions: Map<string, string>
        AppToken: string
        InstallationToken: string
        UserAgent: string
    }

type MigrationReceiverScopeSettings =
    {
        InstallationId: int64
        AccountLogin: string
        AccountId: int64
        AccountNodeId: string
        RepositorySelection: string
        Permissions: Map<string, string>
        RepositoriesUrl: string
    }

type MigrationReceiverRosterRepository =
    {
        RosterRepositoryId: int64
        RosterRepositoryNodeId: string
        RosterRepositoryFullName: string
        RosterPrivate: bool
        RosterArchived: bool
        RosterDisabled: bool
        RosterPermissions: Map<string, bool>
    }

type MigrationReceiverRosterRawPage =
    {
        RosterRequestedUri: string
        RosterRequestIdentitySha256: string
        RosterRawBody: string
        RosterRawSha256: string
        RosterNextUri: string option
    }

type MigrationReceiverRosterPass =
    {
        ScopeSettings: MigrationReceiverScopeSettings
        RepositoryTotalCount: int
        Repositories: MigrationReceiverRosterRepository list
        Pages: MigrationReceiverRosterRawPage list
        PassFingerprint: string
    }

type MigrationReceiverRosterCapture =
    {
        First: MigrationReceiverRosterPass
        Second: MigrationReceiverRosterPass
        CaptureFingerprint: string
    }

type MigrationReceiverRosterDeclaredRepository =
    {
        DeclaredRepositoryId: int64
        DeclaredRepositoryNodeId: string
        DeclaredRepositoryFullName: string
    }

/// Provider-backed installation scope evidence. The installation census is exhaustive for the
/// selected token scope, but is not a protected source roster for caller-chosen receiver names.
type MigrationReceiverRosterAssessment =
    {
        TokenScopeExhaustive: bool
        CohortRepositoriesMatchScope: bool
        CallerReceiverRosterProtected: bool
        ReceiverIdentityComplete: bool
        ScopedSettingsSha256: string
        RepositoryRosterSha256: string
    }

[<RequireQualifiedAccess>]
module MigrationReceiverRosterRead =
    val capturePass:
        options: MigrationReceiverRosterReadOptions ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverRosterPass, string>

    val captureTwoPass:
        options: MigrationReceiverRosterReadOptions ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverRosterCapture, string>

    /// Requires exact equality between the provider's selected installation scope and every
    /// repository referenced by the isolated cohort. It deliberately cannot qualify receiver
    /// identities without a separate protected source roster.
    val assessCallerCohort:
        isolated: bool ->
        declaredRepositories: MigrationReceiverRosterDeclaredRepository list ->
        receiverRepositoryIds: int64 list ->
        capture: MigrationReceiverRosterCapture ->
            Result<MigrationReceiverRosterAssessment, string>
