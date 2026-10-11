namespace FS.GG.Execution.Processes

open FS.GG.Execution.Contracts

/// BCL process sessions with explicit caller custody and original-budget observation.
[<RequireQualifiedAccess>]
module ProcessSessions =
    /// Creates a factory. Preparation validates and refuses unsupported requirements without launching.
    /// The caller retains each prepared session through launch, observation and local release.
    val createFactory: unit -> IProcessSessionFactory
