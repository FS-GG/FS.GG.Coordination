namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationJournalCapture =
    /// Fail-closed stub. The journal reader must derive both namespace censuses and complete histories.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationJournalTwoPass, string>
