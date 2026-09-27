namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Read-only source proof for the explicitly declared receivers in a disposable copy.
/// This is not a receiver-identities or workflow-pins inspect authority.
type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

/// Two stable reads of pin bytes. InventoryBound distinguishes caller declarations from a
/// provider-tree-derived workflow/package census.
type MigrationReceiverPinTwoPass =
    { CohortSha256: string
      InventoryBound: bool
      First: MigrationReceiverPinSnapshot list
      Second: MigrationReceiverPinSnapshot list }

[<RequireQualifiedAccess>]
module MigrationReceiverCapture =
    val captureTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverTwoPass, string>

    val capturePinBytesTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        pinsByReceiver:Map<string, MigrationReceiverPinDeclaration list> ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverPinTwoPass, string>

    /// Derives every supported workflow/package path from two stable recursive provider trees,
    /// then captures and revalidates every corresponding blob in two further stable passes.
    val captureWorkflowPinsTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverPinTwoPass, string>
