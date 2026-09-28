namespace FS.GG.Coordination.Cli

open System

type MigrationSandboxSeedBootstrapArtifactRequest =
    {
        CandidateSha: string
        WorkflowRunId: int64
        WorkflowRunAttempt: int
        WorkflowSha: string
        SourceManifestBytes: ReadOnlyMemory<byte>
        S2DeclarationBytes: ReadOnlyMemory<byte>
        MintProofBytes: ReadOnlyMemory<byte>
        SeedPlanBytes: ReadOnlyMemory<byte>
        CorpusBytes: ReadOnlyMemory<byte>
        Prestate: MigrationSandboxSeedPrestate
    }

type MigrationSandboxSeedBootstrapSourceRequest =
    {
        CandidateSha: string
        SeedPlanBytes: ReadOnlyMemory<byte>
        CorpusBytes: ReadOnlyMemory<byte>
    }

type MigrationSandboxSeedBootstrapSourceSet =
    {
        ManifestBytes: byte array
        Files: (string * byte array) list
        ApprovedArtifactSourceSha256: string
    }

type MigrationSandboxSeedBootstrapArtifactSet =
    {
        ManifestBytes: byte array
        Files: (string * byte array) list
        JournalPlan: MigrationSandboxSeedJournalPlan
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedBootstrapArtifactFailure =
    | InvalidInput
    | S2DeclarationMismatch
    | JournalPlanFailure

[<RequireQualifiedAccess>]
module MigrationSandboxSeedBootstrapArtifact =
    /// Compose the static pre-mint source manifest. Its raw SHA256 becomes the approved artifact
    /// source identity in the protected S2 declaration. It contains no run or credential evidence.
    val prepareSource:
        request: MigrationSandboxSeedBootstrapSourceRequest ->
            Result<MigrationSandboxSeedBootstrapSourceSet, MigrationSandboxSeedBootstrapArtifactFailure>

    /// Seal retained generation-zero bytes after the protected host has minted and frozen S2 /2.
    /// This performs no provider request and grants no bootstrap or effect authority.
    val prepare:
        request: MigrationSandboxSeedBootstrapArtifactRequest ->
            Result<MigrationSandboxSeedBootstrapArtifactSet, MigrationSandboxSeedBootstrapArtifactFailure>
