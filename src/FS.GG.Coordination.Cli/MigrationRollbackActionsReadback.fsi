namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Partial repository Actions policy readback with selected allowlist, when applicable.
type PartialActionsPolicyRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      ActionsPolicySha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryActionsPolicy
      Second: MigrationRepositoryActionsPolicy
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackActionsReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedActionsPolicySha256:string ->
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<PartialActionsPolicyRollbackReadback, string>
