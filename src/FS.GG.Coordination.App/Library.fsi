namespace FS.GG.Coordination.App

type HostStatus =
    { Listening: bool
      DeploymentConfigured: bool
      ProductionAuthority: bool }

[<RequireQualifiedAccess>]
module HostBoundary =
    val status: HostStatus
