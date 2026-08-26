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
    let name = "FS.GG.Coordination.Protocol"
    let schemaVersion = 1us
