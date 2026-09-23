namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubTransformFamily =
    | Taxonomy
    | PlanningFields
    | RepositoryScope
    | BodyMetadata
    | Blockers
    | Hierarchy
    | SchedulingHolds
    | TouchSets
    | LifecycleReceipts
    | DesiredSettings

type GitHubTransformObligation =
    {
        SubjectIdentity: string
        Family: GitHubTransformFamily
    }

type GitHubMigratedTransform =
    {
        TargetIdentity: string
        GlobalId: string
        TargetSchema: string
        PayloadSha256: string
        MappingSha256: string
    }

type GitHubAmbiguousCandidate =
    {
        TargetIdentity: string
        TargetSchema: string
        PayloadSha256: string
        MappingSha256: string
    }

type GitHubAmbiguousTransform =
    {
        Reason: string
        Candidates: GitHubAmbiguousCandidate list
        EvidenceSha256: string
    }

type GitHubUnsupportedTransform =
    {
        Code: string
        Reason: string
        EvidenceSha256: string
    }

[<RequireQualifiedAccess>]
type GitHubTypedTransformDecision =
    | Migrated of GitHubMigratedTransform
    | Ambiguous of GitHubAmbiguousTransform
    | Unsupported of GitHubUnsupportedTransform

type GitHubTypedTransform =
    {
        SubjectIdentity: string
        GlobalId: string
        Family: GitHubTransformFamily
        SourceSchema: string
        SourceBytesSha256: string
        SourceValueSha256: string
        Decision: GitHubTypedTransformDecision
    }

type GitHubTypedTransformQualification =
    {
        SchemaVersion: int
        QualificationId: string
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        PredecessorReceiptDigest: string
        ManifestNormalizedDigest: string
        ManifestSeal: string
        Transformer: GitHubManifestFingerprint
        Obligations: GitHubTransformObligation list
        Transforms: GitHubTypedTransform list
        CreatedAt: System.DateTimeOffset
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubTypedTransformFinding =
    | InvalidTransformField of string
    | InvalidTransformerFingerprint
    | InvalidObligationPopulation
    | InvalidTransformPopulation
    | InvalidMigratedTransform of string
    | InvalidAmbiguousTransform of string
    | InvalidUnsupportedTransform of string
    | AlteredTransformDigest
    | AlteredTransformSeal

[<RequireQualifiedAccess>]
type GitHubTypedTransformControl =
    | TransformPrerequisiteReceipt
    | TransformManifestBinding
    | TransformTaxonomy
    | TransformPlanningFields
    | TransformRepositoryScope
    | TransformBodyMetadata
    | TransformBlockers
    | TransformHierarchy
    | TransformSchedulingHolds
    | TransformTouchSets
    | TransformLifecycleReceipts
    | TransformDesiredSettings
    | TransformCompleteCoverage
    | TransformTypedOutcomes
    | TransformTamperRefusal
    | TransformReplay
    | TransformNoMutation

type GitHubTypedTransformControlResult =
    {
        Control: GitHubTypedTransformControl
        ControlPassed: bool
        BaselineGreen: bool
    }

type GitHubTypedTransformQualificationFinding =
    {
        Code: string
        ControlId: string
        Message: string
    }

module GitHubTypedTransformQualification =
    val requiredFamilies: GitHubTransformFamily list
    val familyId: GitHubTransformFamily -> string
    val requiredControls: GitHubTypedTransformControl list
    val controlId: GitHubTypedTransformControl -> string

    val qualify:
        qualificationId: string ->
        roadmapRevision: string ->
        roadmapSha256: string ->
        unitContractSha256: string ->
        predecessorReceiptDigest: string ->
        manifestNormalizedDigest: string ->
        manifestSeal: string ->
        transformer: GitHubManifestFingerprint ->
        obligations: GitHubTransformObligation list ->
        transforms: GitHubTypedTransform list ->
        createdAt: System.DateTimeOffset ->
            Result<GitHubTypedTransformQualification, GitHubTypedTransformFinding list>

    val verify:
        expectedObligations: GitHubTransformObligation list ->
        expectedSeal: string ->
        qualification: GitHubTypedTransformQualification ->
            Result<GitHubTypedTransformQualification, GitHubTypedTransformFinding list>

    val validateControls:
        generated: GitHubTypedTransformControlResult list ->
        independent: GitHubTypedTransformControlResult list ->
            Result<unit, GitHubTypedTransformQualificationFinding list>
