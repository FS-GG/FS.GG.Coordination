namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationJournalCapture =
    /// Independently censuses both fixed journal namespaces and walks every discovered ref to its root.
    /// Exact provider reads and Git object bytes are hash checked, supported review/delivery events are
    /// decoded through their canonical contracts, and two fresh passes must match exactly.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationJournalTwoPass, string>
