namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Exact readback of a declared receiver cohort only; the receiver inventory is not proven exhaustive.
type DeclaredReceiverRollbackReadback =
    { PlanSeal: string
      CohortSha256: string
      StateSha256: string
      InventoryBound: bool
      Native: MigrationReceiverPinTwoPass }

[<RequireQualifiedAccess>]
module MigrationRollbackReceiverReadback =
    val captureDeclared:
        expectedPlanSeal:string -> plan:GitHubRollbackPlan -> cohort:GitHubMigrationCopyCohort ->
        pinsByReceiver:Map<string, MigrationReceiverPinDeclaration list> ->
        template:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
        Result<DeclaredReceiverRollbackReadback, string>
