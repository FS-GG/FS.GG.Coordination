namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationClaimJournalCapture =
    /// Independently censuses the fixed claim and operation matching-ref namespaces and walks every
    /// discovered history to its root. This bounded source slice recognizes producer claim,
    /// admission and ordinary records and refuses unknown layouts and schemas. Its successful result
    /// is capture evidence only; the canonical claim-and-event-streams authority remains unavailable.
    val captureTwoPass:
        options: MigrationGitHubReadOptions ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationClaimJournalTwoPass, string>
