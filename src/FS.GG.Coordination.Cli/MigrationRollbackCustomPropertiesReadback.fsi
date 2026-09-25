namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Two raw-bound custom-property reads following a pinned partial repository-core read.
type PartialCustomPropertiesRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      CustomPropertiesSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationCustomProperties
      Second: MigrationCustomProperties
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackCustomPropertiesReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedCustomPropertiesSha256:string ->
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<PartialCustomPropertiesRollbackReadback, string>
