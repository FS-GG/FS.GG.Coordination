namespace FS.GG.Execution.Processes

open FS.GG.Execution.Contracts

/// Explicit ordinary retirement mechanism; neither mode establishes descendant custody.
[<RequireQualifiedAccess>]
type RetirementPolicy =
    | DirectChild
    | BestEffortProcessTree

/// BCL process sessions with explicit caller custody and original-budget observation.
[<RequireQualifiedAccess>]
module ProcessSessions =
    /// Creates a factory. Preparation validates and refuses unsupported requirements without launching.
    /// The caller retains each prepared session through launch, observation and local release.
    val createFactory: unit -> IProcessSessionFactory

    /// Selects an explicit best-effort retirement policy, captured before preparation.
    /// Tree mode uses the retained BCL process and may retain an in-flight stop beyond bounded return.
    /// It does not provide stable identity, containment, workload emptiness or reaping.
    val createFactoryWithRetirement: RetirementPolicy -> IProcessSessionFactory

    /// Friend-test seam for this factory instance only; not a public caller hook.
    val internal createFactoryForTesting:
        retirement: RetirementPolicy ->
        retire: (System.Diagnostics.Process -> bool -> unit) -> IProcessSessionFactory

    /// Closed BOM-aware decoded-text mode with shared1024-character UTF8 admission.
    /// Reuses the original session lifecycle, deadlines, native task custody and release.
    val createTextFactory: unit -> ITextProcessSessionFactory

    /// Selects the same explicit best-effort retirement leaf for decoded-text capture.
    /// Text snapshots preserve exact immutable UTF16 chunks and separate transport totals.
    val createTextFactoryWithRetirement: RetirementPolicy -> ITextProcessSessionFactory
