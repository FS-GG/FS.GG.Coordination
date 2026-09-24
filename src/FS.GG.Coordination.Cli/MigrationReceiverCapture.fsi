namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Read-only source proof for the explicitly declared receivers in a disposable copy.
/// This is not a receiver-identities or workflow-pins inspect authority.
type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

[<RequireQualifiedAccess>]
module MigrationReceiverCapture =
    val captureTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverTwoPass, string>
