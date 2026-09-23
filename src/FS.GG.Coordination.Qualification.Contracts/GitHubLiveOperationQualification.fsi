namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubLiveOperationFamily =
    | Claim
    | QueuedWrite
    | Review
    | Delivery
    | Release
    | CutoverAdjacent

type GitHubLiveOperationObligation =
    { OperationIdentity: string; Family: GitHubLiveOperationFamily }

type GitHubDrainDisposition =
    { CompletionReceiptSha256: string; DrainFence: string }

type GitHubMigrateDisposition =
    { TargetOperationIdentity: string; GlobalId: string; TargetSchema: string; PayloadSha256: string; MappingSha256: string }

type GitHubParkDisposition =
    { ParkingIdentity: string; ResumeCondition: string; PayloadSha256: string; EvidenceSha256: string }

type GitHubInvalidDisposition =
    { Code: string; Reason: string; EvidenceSha256: string }

[<RequireQualifiedAccess>]
type GitHubLiveOperationDisposition =
    | Drain of GitHubDrainDisposition
    | Migrate of GitHubMigrateDisposition
    | Park of GitHubParkDisposition
    | Invalid of GitHubInvalidDisposition

type GitHubLiveOperationDecision =
    {
        OperationIdentity: string
        GlobalId: string
        Family: GitHubLiveOperationFamily
        SourceState: string
        SourceBytesSha256: string
        DependencySetSha256: string
        Disposition: GitHubLiveOperationDisposition
    }

type GitHubLiveOperationQualification =
    {
        SchemaVersion: int
        QualificationId: string
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        PredecessorReceiptDigest: string
        ManifestNormalizedDigest: string
        ManifestSeal: string
        TransformNormalizedDigest: string
        TransformSeal: string
        Planner: GitHubManifestFingerprint
        Obligations: GitHubLiveOperationObligation list
        Decisions: GitHubLiveOperationDecision list
        CreatedAt: System.DateTimeOffset
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubLiveOperationFinding =
    | InvalidLiveOperationField of string
    | InvalidPlannerFingerprint
    | InvalidObligationPopulation
    | InvalidDecisionPopulation
    | InvalidDrainDisposition of string
    | InvalidMigrateDisposition of string
    | InvalidParkDisposition of string
    | InvalidExplicitInvalidDisposition of string
    | AlteredLiveOperationDigest
    | AlteredLiveOperationSeal

[<RequireQualifiedAccess>]
type GitHubLiveOperationControl =
    | LiveOperationPrerequisiteReceipt
    | LiveOperationManifestBinding
    | LiveOperationTransformBinding
    | LiveOperationClaims
    | LiveOperationQueuedWrites
    | LiveOperationReviews
    | LiveOperationDeliveries
    | LiveOperationReleases
    | LiveOperationCutoverAdjacent
    | LiveOperationCompleteCoverage
    | LiveOperationTypedDispositions
    | LiveOperationTamperRefusal
    | LiveOperationReplay
    | LiveOperationNoMutation

type GitHubLiveOperationControlResult =
    { Control: GitHubLiveOperationControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubLiveOperationQualificationFinding =
    { Code: string; ControlId: string; Message: string }

module GitHubLiveOperationQualification =
    val requiredFamilies: GitHubLiveOperationFamily list
    val familyId: GitHubLiveOperationFamily -> string
    val requiredControls: GitHubLiveOperationControl list
    val controlId: GitHubLiveOperationControl -> string

    val qualify:
        qualificationId: string -> roadmapRevision: string -> roadmapSha256: string ->
        unitContractSha256: string -> predecessorReceiptDigest: string ->
        manifestNormalizedDigest: string -> manifestSeal: string ->
        transformNormalizedDigest: string -> transformSeal: string ->
        planner: GitHubManifestFingerprint -> obligations: GitHubLiveOperationObligation list ->
        decisions: GitHubLiveOperationDecision list -> createdAt: System.DateTimeOffset ->
            Result<GitHubLiveOperationQualification, GitHubLiveOperationFinding list>

    val verify:
        expectedObligations: GitHubLiveOperationObligation list -> expectedSeal: string ->
        qualification: GitHubLiveOperationQualification ->
            Result<GitHubLiveOperationQualification, GitHubLiveOperationFinding list>

    val validateControls:
        generated: GitHubLiveOperationControlResult list -> independent: GitHubLiveOperationControlResult list ->
            Result<unit, GitHubLiveOperationQualificationFinding list>
