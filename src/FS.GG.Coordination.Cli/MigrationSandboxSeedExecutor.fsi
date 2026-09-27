namespace FS.GG.Coordination.Cli

open System

[<RequireQualifiedAccess>]
type MigrationSandboxSeedEffectKind =
    | CreateNonceIssue
    | AddProjectMembership
    | RemoveProjectMembership
    | DeleteNonceIssue

type MigrationSandboxSeedPrestate =
    {
        Complete: bool
        RepositoryId: int64
        ProjectNodeId: string
        NonceIssueCount: int
        NonceProjectItemCount: int
        SnapshotSha256: string
    }

type MigrationSandboxSeedExecutionBinding =
    {
        Request: MigrationSandboxSeedRequest
        WorkflowPath: string
        WorkflowRef: string
        WorkflowSha: string
        RepositoryId: int64
        RepositoryNodeId: string
        ProjectNodeId: string
        MintProofSha256: string
        ProtectedHostReceiptSha256: string
        SeedPlanSha256: string
        CorpusSha256: string
        Prestate: MigrationSandboxSeedPrestate
        AdmittedEffects: MigrationSandboxSeedEffectKind list
        Seal: string
    }

type MigrationSandboxOwnedResource =
    {
        EffectId: string
        Kind: string
        ResourceId: string
        ParentResourceId: string option
        RunNonce: string
        ReadbackSha256: string
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedEffectStage =
    | Planned
    | IntentPersisted
    | InFlight
    | RecoveryPending
    | Settled

type MigrationSandboxSeedEffectState =
    {
        EffectId: string
        IdempotencyKey: string
        Kind: MigrationSandboxSeedEffectKind
        OriginalEffectId: string option
        Stage: MigrationSandboxSeedEffectStage
        Ownership: MigrationSandboxOwnedResource option
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutionMode =
    | Forward
    | Compensation
    | Complete
    | Compensated

type MigrationSandboxSeedExecution =
    {
        Binding: MigrationSandboxSeedExecutionBinding
        Mode: MigrationSandboxSeedExecutionMode
        Effects: MigrationSandboxSeedEffectState list
        ActiveIndex: int
        Generation: int64
        Head: string
    }

type MigrationSandboxSeedCas =
    {
        ExpectedGeneration: int64
        ExpectedHead: string
        NextHead: string
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedAction =
    | PersistIntent of effectId: string
    | MarkInFlight of effectId: string
    | RecordResponseUnknown of effectId: string
    | SettleApplied of effectId: string * ownership: MigrationSandboxOwnedResource
    | SettleAbsent of effectId: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutorResult =
    | Advanced
    | Pending of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutorFailure =
    | InvalidBinding
    | UnsafePrestate
    | StaleCas
    | WrongEffect
    | InvalidTransition
    | ForeignOwnership

[<RequireQualifiedAccess>]
module MigrationSandboxSeedExecutor =
    /// Seal one source-only execution contract over the exact retained mint, protected-host,
    /// seed-plan and corpus bytes. The caller must first authenticate and semantically validate
    /// the mint and protected-host receipts; this function is not an authorization or a write port.
    val sealBinding:
        mintProofBytes: ReadOnlyMemory<byte> ->
        protectedHostReceiptBytes: ReadOnlyMemory<byte> ->
        seedPlanBytes: ReadOnlyMemory<byte> ->
        corpusBytes: ReadOnlyMemory<byte> ->
        binding: MigrationSandboxSeedExecutionBinding ->
            Result<MigrationSandboxSeedExecutionBinding, MigrationSandboxSeedExecutorFailure>

    /// Create the two forward effects for a fresh nonce-owned issue and its Project membership.
    /// IntentPersisted, InFlight and Settled preserve the shared MigrationStepExecution ordering;
    /// RecoveryPending prevents redispatch after an unknown response until independent readback.
    val create:
        binding: MigrationSandboxSeedExecutionBinding ->
        generation: int64 ->
        head: string ->
            Result<MigrationSandboxSeedExecution, MigrationSandboxSeedExecutorFailure>

    val transition:
        cas: MigrationSandboxSeedCas ->
        action: MigrationSandboxSeedAction ->
        state: MigrationSandboxSeedExecution ->
            Result<
                MigrationSandboxSeedExecution * MigrationSandboxSeedExecutorResult,
                MigrationSandboxSeedExecutorFailure
             >

    /// Derive reverse compensation only after both forward effects have settled with owned readback.
    val beginCompensation:
        cas: MigrationSandboxSeedCas ->
        state: MigrationSandboxSeedExecution ->
            Result<MigrationSandboxSeedExecution, MigrationSandboxSeedExecutorFailure>
