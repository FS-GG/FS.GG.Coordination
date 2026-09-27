namespace FS.GG.Coordination.GitHub

open System

/// One read-only repository-settings surface. It is not an eleven-surface settings inventory.
type MigrationEnvironmentReviewer =
    { Kind: string
      DatabaseId: int64
      NodeId: string
      Name: string }

type MigrationEnvironmentProtectionRule =
    { RuleId: int64
      RuleNodeId: string
      Kind: string
      WaitMinutes: int option
      PreventSelfReview: bool option
      Reviewers: MigrationEnvironmentReviewer list
      PayloadJson: string
      PayloadSha256: string }

type MigrationEnvironmentBranchPolicy =
    { PolicyId: int64
      PolicyNodeId: string
      Name: string
      Kind: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationEnvironmentCustomRule =
    { RuleId: int64
      RuleNodeId: string
      Enabled: bool
      AppId: int64
      AppNodeId: string
      AppSlug: string
      PayloadJson: string
      PayloadSha256: string }

/// Secret metadata only. Provider APIs never expose secret values.
type MigrationEnvironmentSecret =
    { Name: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

/// Environment variable identity and value digest. Raw provider evidence is retained for two-pass comparison.
type MigrationEnvironmentVariable =
    { Name: string
      ValueSha256: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

type MigrationEnvironmentPageEvidence =
    { EnvironmentRequestedUri: string
      EnvironmentPayloadJson: string
      EnvironmentPayloadSha256: string
      EnvironmentNextUri: string option }

type MigrationEnvironmentObservation =
    { EnvironmentId: int64
      EnvironmentNodeId: string
      Name: string
      UpdatedAt: DateTimeOffset
      ProtectedBranches: bool
      CustomBranchPolicies: bool
      ProtectionRules: MigrationEnvironmentProtectionRule list
      BranchPolicyPages: MigrationEnvironmentPageEvidence list
      BranchPolicies: MigrationEnvironmentBranchPolicy list
      CustomRulesUri: string
      CustomRulesPayloadJson: string
      CustomRulesPayloadSha256: string
      CustomRules: MigrationEnvironmentCustomRule list
      SecretPages: MigrationEnvironmentPageEvidence list
      Secrets: MigrationEnvironmentSecret list
      VariablePages: MigrationEnvironmentPageEvidence list
      Variables: MigrationEnvironmentVariable list
      ListPayloadJson: string
      ListPayloadSha256: string
      DetailUri: string
      DetailPayloadJson: string
      DetailPayloadSha256: string }

type MigrationEnvironmentSettings =
    { RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RepositoryUpdatedAt: DateTimeOffset
      IdentityUri: string
      IdentityPayloadJson: string
      IdentityPayloadSha256: string
      TerminalIdentityPayloadJson: string
      TerminalIdentityPayloadSha256: string
      Pages: MigrationEnvironmentPageEvidence list
      Terminal: bool
      TotalCount: int
      Environments: MigrationEnvironmentObservation list }

/// Two complete raw- and typed-equal environment settings observations.
/// Secret values and the other repository-settings surfaces remain outside this partial source.
type MigrationEnvironmentSettingsCapture =
    { EnvironmentSettingsFirst: MigrationEnvironmentSettings
      EnvironmentSettingsSecond: MigrationEnvironmentSettings
      EnvironmentSettingsFingerprint: string
      EnvironmentSurfaceComplete: bool }

[<RequireQualifiedAccess>]
type MigrationEnvironmentSettingsCompositionFailure =
    | OpeningCensusRefused of reason:string
    | SettingsRefused of MigrationReadFailure
    | ClosingCensusRefused of reason:string
    | CensusDrift
    | IdentityDrift
    | RosterDrift
    | EvidenceInvalid of reason:string

/// Detailed settings bracketed by two independently stable provider censuses.
/// This proves the environment roster used by the partial source; it does not complete repository settings.
type MigrationEnvironmentSettingsComposition =
    { OpeningCensus: EnvironmentCensusCapture
      Settings: MigrationEnvironmentSettingsCapture
      ClosingCensus: EnvironmentCensusCapture
      CompositionFingerprint: string
      EnvironmentSurfaceComplete: bool }

[<RequireQualifiedAccess>]
module MigrationEnvironmentSettingsRead =
    /// GET-only, exact-repository, terminal observation. Partial settings source only.
    val read:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationEnvironmentSettings, MigrationReadFailure>

    /// Repeats the complete environment/settings read and refuses any raw or typed drift.
    val captureTwoPass:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationEnvironmentSettingsCapture, MigrationReadFailure>

    /// Revalidates retained raw hashes, typed fingerprint, exact identity and two-pass equality.
    val validateCapture:
        options:MigrationGitHubReadOptions -> captured:MigrationEnvironmentSettingsCapture ->
            Result<MigrationEnvironmentSettingsCapture, MigrationReadFailure>

    /// Brackets the detailed two-pass read with independent two-pass censuses and requires an exact roster.
    val captureBracketed:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationEnvironmentSettingsComposition, MigrationEnvironmentSettingsCompositionFailure>

    /// Revalidates a retained bracketed composition without issuing provider requests.
    val validateComposition:
        options:MigrationGitHubReadOptions -> composed:MigrationEnvironmentSettingsComposition ->
            Result<MigrationEnvironmentSettingsComposition, MigrationEnvironmentSettingsCompositionFailure>
