namespace FS.GG.Coordination.GitHub

/// A copy selection read and verified by the source adapter. Its independent
/// admission, operation and seal commits are not the current journal head.
type MigrationVerifiedEffectSelection =
    { Repository: string
      RepositoryId: int64
      ProjectNodeId: string
      CandidateSha256: Sha256Digest
      ArtifactSha256: Sha256Digest
      RecoveryOwner: string
      AdmissionCommit: GitObjectId
      AdmissionGeneration: int64
      OperationCommit: GitObjectId
      OperationGeneration: int64
      SealCommit: GitObjectId }

type MigrationEffectAuthorityPorts =
    { Admission: AdmissionServicePorts
      ReadVerifiedSelection: unit -> Result<MigrationVerifiedEffectSelection, string list> }

type MigrationEffectAuthorityDecision =
    | EffectIntentDurable of head:GitObjectId * generation:int64
    | EffectIntentParentConflict
    | EffectIntentRefused of string list
    | EffectIntentIndeterminate of string list

[<RequireQualifiedAccess>]
module MigrationEffectAuthority =
    /// Encode the complete copy and step binding used as durable replay bytes.
    val canonicalRequest: MigrationVerifiedEffectSelection -> MigrationExecutionStep -> byte array
    /// Recover selection and admission, then append one copy-bound effect intent.
    /// This boundary exposes no provider transport or dispatch permit.
    val prepare: MigrationEffectAuthorityPorts -> MigrationExecutionStep -> MigrationEffectAuthorityDecision
