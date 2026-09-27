namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationJournalCapture =
    let captureTwoPass (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) =
        Error "review-delivery-journal-capture-unavailable"
        : Result<MigrationJournalTwoPass, string>
