namespace FS.GG.Coordination.Qualification.Contracts

type EpochPhase =
    | OperatingV1 | Preparing | FreezeRequested | Frozen | SwitchedV2 | VerifiedV2
    | OpenV2 | ObservingV2 | ContractingV1 | OperatingV2 | RollingBack

type WriterClass = NewOrdinaryV1 | IncumbentV1Effect | OrdinaryV2 | CutoverControl | RollbackControl
type AuthorityRead = AuthorityObserved | AuthorityUnreadable | AuthorityContradictory | AuthorityPartial
type Admission = AdmissionAuthorized | AdmissionRefused of string list | AdmissionIndeterminate of string
type Settlement = SettlementKnownApplied | SettlementProvenAbsentMayRetry | SettlementPartial | SettlementIndeterminate

type EpochAuthority = {
    Schema: string; FleetId: string; Repository: string; RepositoryId: int64; Ref: string
    Tag: string; GenesisCommit: string; TrustAnchorSha256: string; ManifestSha256: string
    Phase: EpochPhase; Commit: string; Parent: string; Generation: int64
    Complete: bool; Fresh: bool; CacheUsedAsAuthority: bool; UnknownFields: string list; DuplicateFields: string list }

type EffectFence = {
    Writer: WriterClass; ExpectedManifestSha256: string; ExpectedEpochCommit: string
    ExpectedEpochGeneration: int64; ExpectedClaimGeneration: int64 option; CurrentClaimGeneration: int64 option
    ExpectedOperationGeneration: int64; CurrentOperationGeneration: int64
    OperationId: string; EligibleIncumbent: bool }

type EffectReadback = {
    Read: AuthorityRead; OperationId: string option; EpochCommit: string option
    EpochGeneration: int64 option; EffectDigest: string option; PartialEffect: bool }

type IssueProjection = {
    FleetId: string; ManifestSha256: string; EpochCommit: string; Generation: int64; Phase: EpochPhase
    SourceRef: string; Authoritative: bool }

module GitHubEpochWireQualification =
    val schema: string
    val fleetId: string
    val repository: string
    val repositoryId: int64
    val epochRef: string
    val tagPrefix: string
    val requiredPhases: EpochPhase list
    val requiredTransitions: (EpochPhase * EpochPhase) list
    val phaseName: EpochPhase -> string
    val legalTransition: EpochPhase -> EpochPhase -> bool
    val validateAuthority: AuthorityRead -> EpochAuthority -> string list
    val admit: AuthorityRead -> EpochAuthority -> EffectFence -> Admission
    val settleLostResponse: EffectFence -> EffectReadback -> Settlement
    val projectIssue: EpochAuthority -> IssueProjection
    val validateProjection: EpochAuthority -> IssueProjection -> bool
