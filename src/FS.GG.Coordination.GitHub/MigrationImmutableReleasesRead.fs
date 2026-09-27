namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module MigrationImmutableReleasesRead =
    let captureTwoPass (_: MigrationGitHubReadOptions) (_: IMigrationGitHubReadTransport) =
        Error "immutable-releases-read-unavailable" : Result<unit, string>
