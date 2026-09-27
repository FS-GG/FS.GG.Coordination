namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryCapture =
    /// Independently discovers the repository and complete pull-request population,
    /// then captures every review, inline-comment, pull-head and merge-commit check
    /// and status, delivery, tag and release stream in two fresh read-only passes.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReviewDeliveryNativeTwoPass, string>
