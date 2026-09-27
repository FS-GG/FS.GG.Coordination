namespace FS.GG.Coordination.GitHub

/// Exact Q5/Q6 copy and admission coordinates. These are supplied by the protected
/// sandbox selection, never inferred from the diagnostic Q4 repository.
type MigrationEffectBinding =
    { Repository: string
      RepositoryId: int64
      ProjectNodeId: string
      CandidateSha256: Sha256Digest
      ArtifactSha256: Sha256Digest
      ManifestSeal: string
      OperationId: string
      OperationGeneration: int64
      ClaimGeneration: int64 option
      EpochCommit: GitObjectId
      EpochGeneration: int64
      RegistryHead: GitObjectId
      RegistryGeneration: int64
      RecoveryOwner: string }

type MigrationEffectAuthorityDecision =
    | EffectIntentDurable of head:GitObjectId * generation:int64
    | EffectIntentParentConflict
    | EffectIntentRefused of string list
    | EffectIntentIndeterminate of string list

[<RequireQualifiedAccess>]
module MigrationEffectAuthority =
    /// Check the exact copy, selected bytes, admission, claim and epoch, then
    /// append one effect intent through the existing durable CAS/reconciliation.
    /// No provider transport or dispatch permit is exposed by this boundary.
    val prepare:
        ports:AdmissionServicePorts ->
        binding:MigrationEffectBinding ->
        step:MigrationExecutionStep ->
        effectId:string ->
        canonicalRequestBytes:byte array ->
            MigrationEffectAuthorityDecision
