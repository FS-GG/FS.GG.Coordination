namespace FS.GG.Coordination.GitHub

open System

type GenesisNativeApproval = { ReviewerId: int64; State: string }

/// Values must be independently fetched from GitHub at the protection boundary. In particular,
/// ArtifactBytes must come from the artifact attached to this exact run, not from a caller's file.
type GenesisProtectedNativeRead =
    {
        RunRepositoryId: int64
        RunId: int64
        RunEvent: string
        RunPath: string
        RunRef: string
        RunHead: GitObjectId
        RunConclusion: string
        RunActorId: int64
        RunAttempt: int
        WorkflowReadRevision: GitObjectId
        WorkflowBytes: byte array
        ArtifactReadRunId: int64
        ArtifactBytes: byte array
        EnvironmentName: string
        EnvironmentBranchPolicy: string
        EnvironmentReviewerIds: int64 list
        EnvironmentPreventsSelfReview: bool
        Approvals: GenesisNativeApproval list
    }

/// Native approval verified against a particular signature and plan. This is still not a write
/// permit: the installer must recheck Authority, rules, ref absence, and writer identity.
type VerifiedGenesisProtectedApproval

[<RequireQualifiedAccess>]
module V1AdmissionGenesisProtectedApproval =
    val verify:
        asOf: DateTimeOffset ->
        RegistryGenesisPlan ->
        GenesisAuthorizationIntent ->
        VerifiedGenesisSignature ->
        GenesisProtectedNativeRead ->
            Result<VerifiedGenesisProtectedApproval, string list>

    val runId: VerifiedGenesisProtectedApproval -> int64
