namespace FS.GG.Coordination.Protocol

[<RequireQualifiedAccess>]
type BoundaryKind =
    | ProtocolSpecification
    | PureCore
    | GitHubAdapter
    | CliHost
    | AppHost
    | QualificationContracts

[<RequireQualifiedAccess>]
module ProtocolBoundary =
    val name: string
    val schemaVersion: uint16
