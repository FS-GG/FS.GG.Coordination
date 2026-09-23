namespace FS.GG.Coordination.GitHub

open System

type GenesisAuthorizationIntent =
    {
        SourceCommit: GitObjectId
        SourceTree: GitObjectId
        WorkflowRevision: GitObjectId
        WorkflowSha256: Sha256Digest
    }

type GenesisSignature =
    {
        KeyId: string
        PublicKeyPem: string
        ProtectedRunId: int64
        AuthorizedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Signature: byte array
    }

/// A checked signature and trust binding only. The protected installer must still reread the
/// native approval, rules, authority and journal before it may create a ref.
type VerifiedGenesisSignature

[<RequireQualifiedAccess>]
module V1AdmissionGenesisAuthorization =
    val canonicalIntent: RegistryGenesisPlan -> GenesisAuthorizationIntent -> byte array
    val canonicalSignaturePayload: RegistryGenesisPlan -> GenesisAuthorizationIntent -> GenesisSignature -> byte array

    val verify:
        asOf: DateTimeOffset ->
        trustAnchorBytes: byte array ->
        RegistryGenesisPlan ->
        GenesisAuthorizationIntent ->
        GenesisSignature ->
            Result<VerifiedGenesisSignature, string list>

    val intentSha256: VerifiedGenesisSignature -> Sha256Digest
    val protectedRunId: VerifiedGenesisSignature -> int64
