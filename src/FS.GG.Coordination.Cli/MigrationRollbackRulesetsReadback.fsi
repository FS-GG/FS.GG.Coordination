namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Repository-owned branch and tag rulesets only; inherited and push rulesets remain unobserved.
type PartialRepositoryRulesetsRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      RepositoryRulesetsSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryRulesets
      Second: MigrationRepositoryRulesets
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackRulesetsReadback =
    val capturePartial:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> expectedNodeId:string ->
        expectedCorePayloadSha256:string -> expectedRepositoryRulesetsSha256:string ->
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<PartialRepositoryRulesetsRollbackReadback, string>
