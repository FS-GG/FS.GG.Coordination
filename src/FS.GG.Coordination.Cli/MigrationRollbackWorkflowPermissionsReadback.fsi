namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Only repository GITHUB_TOKEN defaults; complete settings authority remains unavailable.
type PartialWorkflowPermissionsRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      WorkflowPermissionsSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryWorkflowPermissions
      Second: MigrationRepositoryWorkflowPermissions
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackWorkflowPermissionsReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedWorkflowPermissionsSha256:string ->
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<PartialWorkflowPermissionsRollbackReadback, string>
