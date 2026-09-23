namespace FS.GG.Coordination.GitHub

open System

/// Exact raw Git evidence emitted by the read-only Authority collector. This is a snapshot,
/// not a durable lease; the protected installer must collect it again before ref creation.
type GenesisGitRead

[<RequireQualifiedAccess>]
module V1AdmissionGenesisGitRead =
    val decode: ReadOnlyMemory<byte> -> Result<GenesisGitRead, string list>
    val authorityPort: GenesisGitRead -> AuthorityGitPort
    val registryRead: GenesisGitRead -> RegistryJournalRead
    val verifyPlan:
        asOf: DateTimeOffset -> operationId: string -> GenesisGitRead -> Result<RegistryGenesisPlan, string list>
