namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubClaimTouchSetControl =
    | CanonicalIdentity
    | UnsafeTouch
    | SiblingCas
    | MonotonicGeneration
    | ActiveLease
    | ExpiredLease
    | SuccessorCas
    | ProjectionNotAuthority
    | TouchOverlap
    | RepositoryPartition
    | AcquisitionOrder
    | FullPlanPersistence
    | StaleFence
    | TerminalAuthority
    | ReverseCompensation
    | ExactReplay
    | BoundedCost
    | QuintAndPrerequisite

type GitHubClaimTouchSetControlResult =
    { Control: GitHubClaimTouchSetControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubClaimTouchSetFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubClaimTouchSetQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-claim-touch-set-qualification/1"
    val requiredControls: GitHubClaimTouchSetControl list
    val controlId: GitHubClaimTouchSetControl -> string
    val validate: generated: GitHubClaimTouchSetControlResult list -> independent: GitHubClaimTouchSetControlResult list -> Result<unit, GitHubClaimTouchSetFinding list>
