namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// One bounded temporal bracket; no complete or atomic settings authority.
type PartialActionsWorkflowRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      ActionsPolicySha256: string
      WorkflowPermissionsSha256: string
      SettingsAuthorityComplete: bool
      FirstActions: MigrationRepositoryActionsPolicy
      FirstWorkflow: MigrationRepositoryWorkflowPermissions
      SecondWorkflow: MigrationRepositoryWorkflowPermissions
      SecondActions: MigrationRepositoryActionsPolicy
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackActionsWorkflowReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedActionsPolicySha256:string ->
        expectedWorkflowPermissionsSha256:string -> options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport -> Result<PartialActionsWorkflowRollbackReadback, string>
