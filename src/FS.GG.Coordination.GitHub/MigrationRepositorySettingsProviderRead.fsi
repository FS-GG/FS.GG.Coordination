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

/// One attached organization or enterprise configuration with every selected
/// CodeSecurity value explicit. `not_set` never becomes an effective value.
type MigrationRepositoryCodeSecurityRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      ConfigurationId: int64
      ConfigurationTargetType: string
      ConfigurationName: string
      Enforcement: string
      ConfigurationUpdatedAt: string }

/// Effective dependency controls from an attached explicit security
/// configuration, reconciled with repository-level alert and update reads.
type MigrationRepositoryDependencyControlsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      ConfigurationId: int64
      ConfigurationTargetType: string
      ConfigurationName: string
      Enforcement: string
      ConfigurationUpdatedAt: string
      DependencyGraph: bool
      DependencyGraphAutosubmitAction: bool
      DependencyGraphAutosubmitUsesLabeledRunners: bool
      DependabotAlerts: bool
      DependabotSecurityUpdates: bool
      DependabotSecurityUpdatesPaused: bool
      DependabotDelegatedAlertDismissal: bool }

/// Explicit repository properties with organization policy provenance checked.
type MigrationRepositoryPropertiesRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Visibility: string
      Archived: bool
      Disabled: bool
      HasIssues: bool
      HasProjects: bool
      HasWiki: bool
      HasPages: bool
      HasDiscussions: bool
      HasDownloads: bool
      HasPullRequests: bool
      PullRequestCreationPolicy: string
      IsTemplate: bool
      AllowForking: bool option
      WebCommitSignoffRequired: bool
      Description: string option
      Homepage: string option
      Topics: string list }

/// Explicit merge modes and commit-message policies from a repository read made
/// with merge-settings visibility proven by repository push permission.
type MigrationRepositoryMergePolicyRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      AllowSquashMerge: bool
      AllowMergeCommit: bool
      AllowRebaseMerge: bool
      AllowAutoMerge: bool
      AllowUpdateBranch: bool
      DeleteBranchOnMerge: bool
      SquashMergeCommitTitle: string
      SquashMergeCommitMessage: string
      MergeCommitTitle: string
      MergeCommitMessage: string }

type MigrationRepositorySelectedActions =
    { GitHubOwnedAllowed: bool
      VerifiedAllowed: bool
      PatternsAllowed: string list }

/// Explicit organization and repository Actions policy for a public organization
/// repository. This bounded result requires a terminal zero applicable-policy
/// count; required workflows and other nonempty newer policies remain partial.
type MigrationRepositoryActionsPolicyRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      OrganizationEnabledRepositories: string
      OrganizationAllowedActions: string
      OrganizationSelectedActions: MigrationRepositorySelectedActions option
      OrganizationShaPinningRequired: bool
      RepositoryEnabled: bool
      RepositoryAllowedActions: string
      RepositorySelectedActions: MigrationRepositorySelectedActions option
      ShaPinningRequired: bool
      OrganizationDefaultWorkflowPermissions: string
      OrganizationCanApprovePullRequestReviews: bool
      RepositoryDefaultWorkflowPermissions: string
      RepositoryCanApprovePullRequestReviews: bool
      OrganizationArtifactAndLogRetentionDays: int64
      OrganizationMaximumArtifactAndLogRetentionDays: int64
      RepositoryArtifactAndLogRetentionDays: int64
      RepositoryMaximumArtifactAndLogRetentionDays: int64
      OrganizationForkPullRequestApprovalPolicy: string
      RepositoryForkPullRequestApprovalPolicy: string
      ApplicableActionsPolicyCount: int64 }

/// Complete repository-local environment settings for a bounded repository whose
/// environment secret inventory is empty. Environment variables retain their
/// clear provider value in the canonical setting list and their digest in the
/// typed environment observation.
type MigrationRepositoryEnvironmentsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Environments: MigrationEnvironmentObservation list }

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

    /// GET-only read of the attached configuration managing repository code
    /// security. A missing attachment or any inherited/unset value refuses.
    val readCodeSecurity:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryCodeSecurityRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of explicit dependency controls from the attached security
    /// configuration, reconciled with repository alert and security-update state.
    val readDependencyControls:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryDependencyControlsRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of explicit repository properties. Organization-level policy
    /// that masks repository projects, private forking, or signoff is refused.
    val readRepository:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryPropertiesRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of merge modes and commit-message policies. Disabled merge
    /// modes with hidden latent options and missing write visibility are refused.
    val readMergePolicy:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryMergePolicyRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of Actions permissions, workflow defaults, retention and
    /// fork approval at organization and repository scope. This bounded reader
    /// supports public organization repositories with an explicit all-repository
    /// organization scope and no applicable newer Actions policies.
    /// Endpoint contracts: https://docs.github.com/en/rest/actions/permissions
    /// and https://docs.github.com/en/rest/actions/policies.
    val readActionsPolicy:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryActionsPolicyRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of the terminal environment roster, environment details,
    /// reviewers, wait rules, branch/tag policies, custom deployment rules,
    /// secrets inventory and variables. Nonempty secret inventories refuse because
    /// GitHub never returns secret values.
    /// Endpoint contracts: https://docs.github.com/en/rest/deployments/environments,
    /// https://docs.github.com/en/rest/deployments/branch-policies,
    /// https://docs.github.com/en/rest/deployments/protection-rules,
    /// https://docs.github.com/en/rest/actions/secrets and
    /// https://docs.github.com/en/rest/actions/variables.
    val readEnvironments:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryEnvironmentsRead, MigrationRepositorySettingsSurfaceRefusal>

/// Concrete partial provider for the implemented repository settings surfaces.
/// Other incomplete surfaces refuse canonical composition.
type MigrationRepositorySettingsGitHubProvider =
    new:
        options:MigrationGitHubReadOptions * transport:IMigrationGitHubReadTransport ->
            MigrationRepositorySettingsGitHubProvider
    interface IMigrationRepositorySettingsSurfaceProvider
