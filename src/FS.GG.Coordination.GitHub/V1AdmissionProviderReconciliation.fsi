namespace FS.GG.Coordination.GitHub

/// Identity of the original, durably recorded provider request. Attempt is the
/// effect attempt, not the operation generation.
type ProviderRequestIdentity =
    {
        OperationId: string
        EffectId: string
        Attempt: int64
        RequestDigest: Sha256Digest
        CanonicalRequestBytes: byte array
    }

type ProviderAppliedReadback =
    {
        Identity: ProviderRequestIdentity
        ResponseDigest: Sha256Digest
        EvidenceBytes: byte array
    }

type ProviderExclusionKind =
    | IdempotencyKeyExclusion
    | ConditionalFenceExclusionProof
    | OriginalRequestRetirementProof

type ProviderExclusionReadback =
    {
        Identity: ProviderRequestIdentity
        Kind: ProviderExclusionKind
        EvidenceBytes: byte array
    }

type ProviderNativeEffectObservation =
    | AppliedReadback of ProviderAppliedReadback
    | ExclusionReadback of ProviderExclusionReadback
    | PartialReadback of string
    | IndeterminateReadback of string

/// All functions are read-only. VerifyApplied must independently read back the
/// exact effect and response. VerifyExclusion must establish that the original
/// request cannot apply later; a missing effect or an ordinary 404 is insufficient.
type ProviderReconciliationNativeRead =
    {
        Read: ProviderRequestIdentity -> Result<ProviderNativeEffectObservation, string>
        VerifyApplied: ProviderRequestIdentity -> ProviderAppliedReadback -> Result<bool, string>
        VerifyExclusion: ProviderRequestIdentity -> ProviderExclusionReadback -> Result<bool, string>
    }

module V1AdmissionProviderReconciliation =
    /// Builds the registry's recovery-only port from scoped native provider reads.
    /// Unreadable, mismatched, or unverified evidence is indeterminate and never
    /// grants a retry or dispatch permit.
    val port: ProviderReconciliationNativeRead -> ProviderReconciliationPort
