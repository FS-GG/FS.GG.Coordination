namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Read-only source proof for the explicitly declared receivers in a disposable copy.
/// This is not a receiver-identities or workflow-pins inspect authority.
type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

/// Two stable reads of declared pin bytes. InventoryBound is always false until a separate provider proof exists.
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
