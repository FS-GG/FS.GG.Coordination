namespace FS.GG.Coordination.Cli

open System

/// Exact Git object custody for one durable seed-executor snapshot. This is a source plan,
/// not a protected-repository credential or permission to update the ref.
type MigrationSandboxSeedJournalPlan =
    {
        RefName: string
        JournalGeneration: int64
        StateGeneration: int64
        RunNonce: string
        BindingSeal: string
        ExpectedParent: string option
        StateSha256: string
        StateBytes: byte array
        BlobOid: string
        TreeOid: string
        TreeBytes: byte array
        CommitOid: string
        CommitBytes: byte array
    }

type MigrationSandboxSeedJournalSnapshot =
    {
        RefName: string
        JournalGeneration: int64
        StateGeneration: int64
        RunNonce: string
        BindingSeal: string
        CommitOid: string
        ParentOid: string option
        StateSha256: string
        StateBytes: byte array
        BlobOid: string
        TreeOid: string
        TreeBytes: byte array
        CommitBytes: byte array
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalRead =
    | Missing
    | Complete of MigrationSandboxSeedJournalSnapshot
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalReconciliation =
    | Applied
    | ProvenAbsent
    | Conflict
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedJournalFailure =
    | InvalidState
    | InvalidPrevious
    | StaleGeneration
    | GitUnavailable of reason: string

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournal =
    /// Produce exact blob/tree/commit bytes for the next journal generation. The caller must
    /// install those exact objects and atomically update RefName from ExpectedParent to CommitOid.
    val plan:
        previous: MigrationSandboxSeedJournalSnapshot option ->
        state: MigrationSandboxSeedExecution ->
            Result<MigrationSandboxSeedJournalPlan, MigrationSandboxSeedJournalFailure>

    /// Read and independently validate an exact local bare-Git snapshot. This qualification reader
    /// performs no ref or object mutation and does not establish access to the protected remote.
    val readLocalBare: repositoryPath: string -> requestedRef: string -> MigrationSandboxSeedJournalRead

    /// Reconcile a possibly lost write response only from the independently reread exact objects.
    val reconcile:
        proposal: MigrationSandboxSeedJournalPlan ->
        reread: MigrationSandboxSeedJournalRead ->
            MigrationSandboxSeedJournalReconciliation
