namespace FS.GG.Coordination.GitHub

type GitObjectId = private GitObjectId of string
type Sha256Digest = private Sha256Digest of string

type ClaimBinding =
    | TypedClaim of claimId: string * generation: int64
    | NoClaimRequired

type AdmissionPhase =
    | AdmissionsOpen
    | AdmissionsClosing
    | AdmissionsSealed

type StrongAbsenceEvidence =
    | ProviderIdempotencyExclusion of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest
    | ConditionalFenceExclusion of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest
    | OriginalRequestRetirement of originalRequestDigest: Sha256Digest * evidenceDigest: Sha256Digest

type EffectSettlement =
    | EffectSettlementApplied of responseDigest: Sha256Digest
    | EffectSettlementProvenAbsent of StrongAbsenceEvidence
    | EffectSettlementPartial of reason: string
    | EffectSettlementIndeterminate of reason: string

type MutationContext =
    {
        Round: int64
        Manifest: Sha256Digest
        OperationId: string
        OperationGeneration: int64
        Actor: string
        Receiver: string
        Kind: string
        CanonicalTarget: string
        Claim: ClaimBinding
        IntentDigest: Sha256Digest
        TouchSetDigest: Sha256Digest
        OriginatingEpochCommit: GitObjectId
        OriginatingEpochGeneration: int64
    }

type EffectPreconditions =
    {
        ExpectedEpochCommit: GitObjectId
        ExpectedEpochGeneration: int64
        ExpectedClaimGeneration: int64 option
        ExpectedOperationGeneration: int64
    }

type MutationRequest =
    {
        EffectId: string
        RequestDigest: Sha256Digest
        CanonicalRequestBytes: byte array
        Preconditions: EffectPreconditions
    }

type ReadRequest = { Resource: string }

type OutboundRequest =
    | ReadRequest of ReadRequest
    | MutationRequest of MutationRequest

type AuthorityGitObjects =
    {
        Repository: string
        RepositoryId: int64
        Ref: string
        FirstHead: GitObjectId
        TagTarget: GitObjectId
        Commit: GitObjectId
        Parent: GitObjectId option
        GenesisCommit: GitObjectId
        Ancestry: GitObjectId list
        CommitTree: GitObjectId
        CommitBytes: byte array
        TreeBytes: byte array
        TreeEntries: Map<string, GitObjectId>
        EventBlob: GitObjectId * byte array
        HeadBlob: GitObjectId * byte array
        TrustAnchorSha256: Sha256Digest
        ManifestSha256: Sha256Digest
        ClaimJournals: Map<string, JournalObservation>
    }

type AuthorityGitPort =
    {
        ReadObjects: unit -> Result<AuthorityGitObjects, string>
        RereadHead: unit -> Result<GitObjectId, string>
    }

type VerifiedAuthoritySnapshot
type OperationHandle
type AdmissionRegistry
type DispatchFence

type RegistryGitObjects =
    {
        EventObjectId: GitObjectId
        EventBytes: byte array
        HeadObjectId: GitObjectId
        HeadBytes: byte array
        TreeObjectId: GitObjectId
        TreeBytes: byte array
        CommitObjectId: GitObjectId
        CommitBytes: byte array
    }

type RegistryCommand
type RegistryAppendProposal
type InitialDispatchPermit

type ProviderEffectObservation =
    | ProviderApplied of responseDigest: Sha256Digest
    | ProviderStronglyAbsent of StrongAbsenceEvidence
    | ProviderPartial of reason: string
    | ProviderIndeterminate of reason: string

type ProviderReconciliationPort =
    {
        Read: string -> string -> int64 -> byte array -> Result<ProviderEffectObservation, string>
    }

type VerifiedProviderObservation

type RegistryJournalRead =
    {
        Repository: string
        RepositoryId: int64
        Ref: string
        FirstHead: GitObjectId option
        SecondHead: GitObjectId option
        Observation: JournalObservation
        CommitBytes: Map<string, byte array>
        TreeBytes: Map<string, byte array>
    }

type RegistryJournalPort =
    {
        Read: AggregateAddress -> RegistryJournalRead
        Write: RegistryAppendProposal -> ReceivePackOutcome
    }

type DurableAppendDecision =
    | DurableAppendAccepted of AdmissionRegistry * InitialDispatchPermit option
    | DurableAppendParentConflict of AdmissionRegistry option
    | DurableAppendRefused of reason: string * AdmissionRegistry option
    | DurableAppendIndeterminate of string list * AdmissionRegistry option

type AdmissionDecision =
    | RegistryAdmissionAppended of AdmissionRegistry
    | RegistryAdmissionAlreadyPresent of OperationHandle
    | RegistryAdmissionRefused of string list

type RegistryDecision =
    | RegistryAppended of AdmissionRegistry
    | RegistryRefused of string list

type EffectDecision =
    | EffectIntentAppended of AdmissionRegistry
    | EffectAlreadyInFlight of owner: string
    | EffectAlreadySettled of EffectSettlement
    | EffectRefused of string list

type DispatchDecision =
    | DispatchAuthorized
    | DispatchRefused of string list

[<RequireQualifiedAccess>]
module V1AdmissionRegistry =
    val gitObjectId: string -> Result<GitObjectId, string>
    val sha256Digest: string -> Result<Sha256Digest, string>
    val gitObjectIdValue: GitObjectId -> string
    val sha256Value: Sha256Digest -> string
    val readVerified: AuthorityGitPort -> Result<VerifiedAuthoritySnapshot, string list>
    val aggregateAddress: AdmissionRegistry -> AggregateAddress
    val head: AdmissionRegistry -> GitObjectId
    val generation: AdmissionRegistry -> int64
    val phase: AdmissionRegistry -> AdmissionPhase
    val persistedHead: AdmissionRegistry -> Result<GitObjectId, string list>
    val decodeEvent: byte array -> Result<RegistryCommand, string list>
    val restore: RegistryJournalRead -> Result<AdmissionRegistry, string list>
    val recoverCommand:
        commandId: string -> expectedEventBytes: byte array -> RegistryJournalRead -> Result<AdmissionRegistry, string list>
    val recoverOperation: operationId: string -> AdmissionRegistry -> Result<OperationHandle, string list>

    val planAppend:
        operationId: string ->
        observed: RegistryJournalRead ->
        candidate: AdmissionRegistry ->
            Result<RegistryAppendProposal, string list>

    val appendAndReconcile: RegistryJournalPort -> RegistryAppendProposal -> DurableAppendDecision
    val proposalCas: RegistryAppendProposal -> CasProposal
    val proposalObjects: RegistryAppendProposal -> RegistryGitObjects

    val admit:
        expectedParent: GitObjectId ->
        VerifiedAuthoritySnapshot ->
        MutationContext ->
        AdmissionRegistry ->
            AdmissionDecision

    val closeAdmissions: expectedParent: GitObjectId -> AdmissionRegistry -> RegistryDecision

    val sealAdmissions:
        expectedParent: GitObjectId -> VerifiedAuthoritySnapshot -> AdmissionRegistry -> RegistryDecision

    val reopenAdmissions:
        expectedParent: GitObjectId ->
        newRound: int64 ->
        manifest: Sha256Digest ->
        VerifiedAuthoritySnapshot ->
        AdmissionRegistry ->
            RegistryDecision

    val cohortDigest: AdmissionRegistry -> Sha256Digest option

    val prepareEffect:
        expectedParent: GitObjectId ->
        VerifiedAuthoritySnapshot ->
        OperationHandle ->
        owner: string ->
        OutboundRequest ->
        AdmissionRegistry ->
            EffectDecision

    val authorizeRead: OutboundRequest -> Result<unit, string list>

    val refreshDispatch:
        RegistryJournalPort ->
        AuthorityGitPort ->
        InitialDispatchPermit ->
        OperationHandle ->
        owner: string ->
        effectId: string ->
        AdmissionRegistry ->
            Result<DispatchFence, string list>

    val authorizeDispatch:
        RegistryJournalPort ->
        AuthorityGitPort ->
        DispatchFence ->
        OperationHandle ->
        owner: string ->
        effectId: string ->
        AdmissionRegistry ->
            DispatchDecision

    val settleEffect:
        expectedParent: GitObjectId ->
        owner: string ->
        effectId: string ->
        VerifiedProviderObservation ->
        AdmissionRegistry ->
            RegistryDecision

    val reconcileEffect:
        ProviderReconciliationPort ->
        OperationHandle ->
        effectId: string ->
        AdmissionRegistry ->
            Result<VerifiedProviderObservation, string list>

    val retryAfterProvenAbsence:
        expectedParent: GitObjectId ->
        AuthorityGitPort ->
        OperationHandle ->
        newOwner: string ->
        effectId: string ->
        AdmissionRegistry ->
            EffectDecision

    val unresolvedEffects: AdmissionRegistry -> string list
    val preparingReference: AdmissionRegistry -> Result<GitObjectId * int64 * Sha256Digest, string list>
