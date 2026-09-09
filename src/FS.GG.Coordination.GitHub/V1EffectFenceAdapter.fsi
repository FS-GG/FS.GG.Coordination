namespace FS.GG.Coordination.GitHub

type V1EffectClass = NewV1Admission | EligibleIncumbentV1

type V1EpochPhase =
    | V1OperatingV1
    | V1Preparing
    | V1FreezeRequested
    | V1Frozen
    | V1SwitchedV2
    | V1VerifiedV2
    | V1OpenV2
    | V1ObservingV2
    | V1ContractingV1
    | V1OperatingV2
    | V1RollingBack

type V1EpochEvidence =
    { Schema: string
      FleetId: string
      Repository: string
      RepositoryId: int64
      Ref: string
      Tag: string
      GenesisCommit: string
      TrustAnchorSha256: string
      ManifestSha256: string
      Phase: V1EpochPhase
      Commit: string
      Parent: string
      Generation: int64
      Complete: bool
      Fresh: bool
      CacheUsedAsAuthority: bool }

type FreshEpochRead =
    | FreshEpochBytes of byte array
    | EpochUnreadable of string
    | EpochPartial of string
    | EpochContradictory of string

type V1EffectExpectation =
    { EffectClass: V1EffectClass
      EligibleIncumbent: bool
      ManifestSha256: string
      EpochCommit: string
      EpochGeneration: int64
      ExpectedClaimGeneration: int64 option
      CurrentClaimGeneration: int64 option
      ExpectedOperationGeneration: int64
      CurrentOperationGeneration: int64 }

type V1EffectRequest = { OperationId: string; PayloadDigest: string }

type V1EffectAttempt =
    | EffectApplied of effectDigest: string
    | EffectProvenAbsent
    | EffectPartiallyApplied of reason: string
    | EffectOutcomeIndeterminate of reason: string

type V1EffectOutcome =
    | V1RefusedBeforeEffect of reasons: string list
    | V1Applied of effectDigest: string
    | V1ProvenAbsent
    | V1Partial of reason: string
    | V1Indeterminate of reason: string

type FreshEpochReader = { ReadFresh: unit -> FreshEpochRead }
type V1EffectPort = { ApplyOnce: V1EffectRequest -> V1EffectAttempt }

[<RequireQualifiedAccess>]
module V1EffectFenceAdapter =
    val parseStrict: byte array -> Result<V1EpochEvidence, string list>
    val execute:
        reader: FreshEpochReader ->
        effect: V1EffectPort ->
        expectation: V1EffectExpectation ->
        request: V1EffectRequest ->
            V1EffectOutcome
