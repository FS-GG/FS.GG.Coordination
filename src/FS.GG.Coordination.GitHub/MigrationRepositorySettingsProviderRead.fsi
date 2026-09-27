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

type MigrationRepositorySelectedActionsRepository =
    { DatabaseId: int64
      NodeId: string
      FullName: string }

type MigrationRepositoryPrivateForkWorkflowPolicy =
    { RunWorkflowsFromForkPullRequests: bool
      SendWriteTokensToWorkflows: bool
      SendSecretsAndVariables: bool
      RequireApprovalForForkPullRequestWorkflows: bool }

/// Explicit organization and repository Actions policy for a public organization
/// repository. This bounded result requires a terminal zero applicable-policy
/// count; required workflows and other nonempty newer policies remain partial.
type MigrationRepositoryActionsPolicyRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      OrganizationDatabaseId: int64
      OrganizationNodeId: string
      OrganizationEnabledRepositories: string
      OrganizationAllowedActions: string
      OrganizationSelectedActions: MigrationRepositorySelectedActions option
      OrganizationSelectedRepositories: MigrationRepositorySelectedActionsRepository list
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
      PrivateRepositoryAccessLevel: string option
      OrganizationPrivateForkWorkflowPolicy: MigrationRepositoryPrivateForkWorkflowPolicy option
      RepositoryPrivateForkWorkflowPolicy: MigrationRepositoryPrivateForkWorkflowPolicy option
      ApplicableActionsPolicyCount: int64 }

/// Complete repository-local environment settings for a bounded public, private,
/// or internal repository whose environment secret inventory is empty. Successful
/// exact endpoint reads prove plan applicability. Environment variables retain their
/// clear provider value in the canonical setting list and their digest in the
/// typed environment observation.
type MigrationRepositoryEnvironmentsRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Environments: MigrationEnvironmentObservation list }

/// Repository and organization immutable-release policy from two equal provider
/// passes, including an exhaustive selected-repository roster when applicable.
type MigrationRepositoryImmutableReleasesRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      RepositoryEnabled: bool
      EnforcedByOwner: bool
      OrganizationPolicy: ImmutableReleasesOrganizationPolicy
      SelectedRepositories: ImmutableReleasesSelectedRepository list
      SelectedTotalCount: int option
      EffectiveEnabled: bool
      CaptureFingerprint: string }

/// Organization-owned custom-property definitions and repository-explicit
/// values from two equal provider passes. Missing values are never promoted to
/// inherited defaults by this bounded source.
type MigrationRepositoryCustomPropertiesRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      OrganizationDatabaseId: int64
      OrganizationNodeId: string
      RepositoryVisibility: string
      Definitions: MigrationCustomPropertyDefinition list
      ExplicitValues: MigrationCustomPropertyValue list
      CaptureFingerprint: string }

/// One target-specific repository-owned ruleset surface. The shared repository
/// list is exhaustive and rejects inherited or push rulesets before either
/// branch or tag output can qualify.
type MigrationRepositoryRulesetSurfaceRead =
    { SurfaceRead: MigrationRepositorySettingsSurfaceRead
      Rulesets: MigrationRepositoryRuleset list
      CaptureFingerprint: string }

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
    /// fork approval at organization and repository scope. Private repositories
    /// additionally require exact access-sharing and private-fork workflow reads.
    /// Selected organization populations are accepted only as one terminal page;
    /// nonempty newer or inherited Actions policies remain partial.
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

    /// GET-only two-pass read of repository and organization immutable-release
    /// policy. Selected organization membership must be exhaustively paginated,
    /// and both retained repository payloads must bind the requested revision.
    /// Endpoint contracts: https://docs.github.com/en/rest/repos/repos and
    /// https://docs.github.com/en/rest/orgs/orgs.
    val readImmutableReleases:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryImmutableReleasesRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of organization-owned definitions and complete explicit
    /// repository values. Enterprise definitions and any omitted value or
    /// definition provenance refuse rather than being inferred as a default.
    /// Endpoint contracts: https://docs.github.com/en/rest/orgs/custom-properties
    /// and https://docs.github.com/en/rest/repos/custom-properties.
    val readCustomProperties:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryCustomPropertiesRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of all repository-owned branch rulesets, including exact
    /// conditions, bypass actors and rule payloads. Inherited and push rulesets
    /// refuse the shared population.
    val readBranchRulesets:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryRulesetSurfaceRead, MigrationRepositorySettingsSurfaceRefusal>

    /// GET-only read of all repository-owned tag rulesets, including exact
    /// conditions, bypass actors and rule payloads. Inherited and push rulesets
    /// refuse the shared population.
    val readTagRulesets:
        options:MigrationGitHubReadOptions ->
        identity:RepositoryIdentity ->
        repositoryRevision:string ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryRulesetSurfaceRead, MigrationRepositorySettingsSurfaceRefusal>

/// Concrete partial provider for the implemented repository settings surfaces.
/// Other incomplete surfaces refuse canonical composition.
type MigrationRepositorySettingsGitHubProvider =
    new:
        options:MigrationGitHubReadOptions * transport:IMigrationGitHubReadTransport ->
            MigrationRepositorySettingsGitHubProvider
    interface IMigrationRepositorySettingsSurfaceProvider
