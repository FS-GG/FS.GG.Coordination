namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Private-repository fork PR workflow flags only; complete settings authority remains unavailable.
type PartialPrivateForkWorkflowRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      PrivateForkWorkflowSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationPrivateForkWorkflowSettings
      Second: MigrationPrivateForkWorkflowSettings
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackPrivateForkWorkflowReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedPrivateForkWorkflowSha256:string ->
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<PartialPrivateForkWorkflowRollbackReadback, string>
