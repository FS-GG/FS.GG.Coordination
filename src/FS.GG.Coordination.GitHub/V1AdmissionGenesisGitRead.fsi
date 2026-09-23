namespace FS.GG.Coordination.GitHub

open System

/// Exact raw Git evidence emitted by the read-only Authority collector. This is a snapshot,
/// not a durable lease; the protected installer must collect it again before ref creation.
type GenesisGitRead

[<RequireQualifiedAccess>]
module V1AdmissionGenesisGitRead =
    val decode: ReadOnlyMemory<byte> -> Result<GenesisGitRead, string list>
    val observedAt: GenesisGitRead -> DateTimeOffset
    val authorityPort: GenesisGitRead -> AuthorityGitPort
    val registryRead: GenesisGitRead -> RegistryJournalRead
    val verifyPlan:
        asOf: DateTimeOffset -> operationId: string -> GenesisGitRead -> Result<RegistryGenesisPlan, string list>

    /// Decode a fresh native readback of the exact planned genesis. Matching plan bytes are
    /// still replayed through the registry validator; no reported ref alone proves installation.
    val decodeInstalled:
        asOf: DateTimeOffset ->
        plan: RegistryGenesisPlan ->
        raw: ReadOnlyMemory<byte> ->
            Result<RegistryJournalRead, string list>
