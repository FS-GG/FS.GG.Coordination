namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationImmutableReleasesRead =
    /// Fail-closed registration stub. The reader must prove repository and organization
    /// policy plus any conditional selected-repository roster in two raw-stable passes.
    val captureTwoPass:
        options:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<unit, string>
