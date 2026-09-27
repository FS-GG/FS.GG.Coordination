namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationClaimJournalCapture =
    /// Independently censuses the fixed claim and operation matching-ref namespaces, walks every
    /// discovered history, and refuses unknown schema families. This stub stays unavailable until
    /// the provider reader implements the frozen contract.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationClaimJournalTwoPass, string>

