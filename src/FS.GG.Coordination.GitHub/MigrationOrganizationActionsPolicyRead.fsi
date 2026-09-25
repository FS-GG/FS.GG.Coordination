namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
type MigrationOrganizationEnabledRepositories =
    | All
    | None
    | Selected

[<RequireQualifiedAccess>]
type MigrationOrganizationAllowedActions =
    | All
    | LocalOnly
    | Selected

type MigrationOrganizationActionsEvidence =
    { RequestedUri: string
      RawBody: string
      RawSha256: string
      NextUri: string option }

type MigrationOrganizationSelectedActions =
    { GitHubOwnedAllowed: bool
      VerifiedAllowed: bool
      PatternsAllowed: string list }

type MigrationOrganizationSelectedRepository =
    { Id: int64
      NodeId: string
      Name: string
      FullName: string }

/// One read-only observation of an organization's Actions policy. It does not
/// determine any repository's effective inherited policy.
type MigrationOrganizationActionsPolicySnapshot =
    { OrganizationId: int64
      OrganizationNodeId: string
      OrganizationLogin: string
      IdentityEvidence: MigrationOrganizationActionsEvidence
      PolicyEvidence: MigrationOrganizationActionsEvidence
      EnabledRepositories: MigrationOrganizationEnabledRepositories
      AllowedActions: MigrationOrganizationAllowedActions
      ShaPinningRequired: bool
      SelectedActionsUrl: string option
      SelectedActions: MigrationOrganizationSelectedActions option
      SelectedActionsEvidence: MigrationOrganizationActionsEvidence option
      SelectedRepositories: MigrationOrganizationSelectedRepository list option
      SelectedRepositoryPages: MigrationOrganizationActionsEvidence list }

/// Two equal raw provider observations of this partial organization authority.
type MigrationOrganizationActionsPolicyTwoPass =
    { First: MigrationOrganizationActionsPolicySnapshot
      Second: MigrationOrganizationActionsPolicySnapshot }

[<RequireQualifiedAccess>]
module MigrationOrganizationActionsPolicyRead =
    val read:
        expectedOrganizationId:int64 ->
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationOrganizationActionsPolicyTwoPass, string>
