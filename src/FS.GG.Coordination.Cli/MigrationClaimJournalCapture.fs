namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationClaimJournalCapture =
    let captureTwoPass (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) : Result<MigrationClaimJournalTwoPass, string> =
        Error "claim-journal-capture-unavailable"
