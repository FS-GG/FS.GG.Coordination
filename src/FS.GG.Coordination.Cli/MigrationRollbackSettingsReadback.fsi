namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Two raw-bound repository core settings reads; ten other settings surfaces remain unobserved.
type PartialRepositorySettingsRollbackReadback =
    { PlanSeal: string
      RepositoryId: int64
      RepositoryNodeId: string
      CorePayloadSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryCoreSettings
      Second: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackSettingsReadback =
    val captureCorePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
        Result<PartialRepositorySettingsRollbackReadback, string>
