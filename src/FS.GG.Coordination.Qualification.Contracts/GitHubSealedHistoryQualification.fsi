namespace FS.GG.Coordination.Qualification.Contracts

type GitHubVerifiedHistoryOutcome =
    { OutputSchema: string; OutputSha256: string }

type GitHubRejectedHistoryOutcome =
    { Code: string; Reason: string; EvidenceSha256: string }

[<RequireQualifiedAccess>]
type GitHubHistoryExpectedOutcome =
    | Verified of GitHubVerifiedHistoryOutcome
    | Rejected of GitHubRejectedHistoryOutcome

type GitHubSealedHistoryRecord =
    {
        ArchiveIdentity: string
        SourceIdentity: string
        SourceSchema: string
        SourceBytesBase64: string
        SourceBytesSha256: string
        SourceValueSha256: string
        ExpectedOutcome: GitHubHistoryExpectedOutcome
    }

type GitHubSealedHistoryLookup =
    { LookupKey: string; ArchiveIdentity: string; RecordSha256: string }

type GitHubSealedHistoryProductionClosure =
    {
        Artifact: GitHubManifestFingerprint
        V1UpcasterCount: int
        ArchiveVerifierOnly: bool
        LookupReadOnly: bool
    }

type GitHubSealedHistoryQualification =
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
        LiveOperationNormalizedDigest: string
        LiveOperationSeal: string
        Verifier: GitHubManifestFingerprint
        ProductionClosure: GitHubSealedHistoryProductionClosure
        Records: GitHubSealedHistoryRecord list
        LookupIndex: GitHubSealedHistoryLookup list
        CreatedAt: System.DateTimeOffset
        ArchiveDigest: string
        LookupDigest: string
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubSealedHistoryFinding =
    | InvalidHistoryField of string
    | InvalidVerifierArtifact
    | InvalidProductionClosure
    | InvalidHistoryPopulation
    | InvalidSourceBytes of string
    | InvalidExpectedOutcome of string
    | InvalidLookupIndex
    | AlteredArchiveDigest
    | AlteredLookupDigest
    | AlteredHistoryDigest
    | AlteredHistorySeal

[<RequireQualifiedAccess>]
type GitHubSealedHistoryControl =
    | HistoryPrerequisiteReceipt
    | HistoryManifestBinding
    | HistoryTransformBinding
    | HistoryLiveOperationBinding
    | HistorySourceSchema
    | HistoryExactBytes
    | HistorySourceDigests
    | HistoryVerifierArtifact
    | HistoryExpectedOutcomes
    | HistoryLookupIndex
    | HistoryNoProductionUpcasters
    | HistoryCompleteCoverage
    | HistoryTamperRefusal
    | HistoryReplay
    | HistoryNoMutation

type GitHubSealedHistoryControlResult =
    { Control: GitHubSealedHistoryControl; ControlPassed: bool; BaselineGreen: bool }

type GitHubSealedHistoryQualificationFinding =
    { Code: string; ControlId: string; Message: string }

module GitHubSealedHistoryQualification =
    val requiredControls: GitHubSealedHistoryControl list
    val controlId: GitHubSealedHistoryControl -> string
    val recordSha256: GitHubSealedHistoryRecord -> string

    val qualify:
        qualificationId: string -> roadmapRevision: string -> roadmapSha256: string ->
        unitContractSha256: string -> predecessorReceiptDigest: string ->
        manifestNormalizedDigest: string -> manifestSeal: string ->
        transformNormalizedDigest: string -> transformSeal: string ->
        liveOperationNormalizedDigest: string -> liveOperationSeal: string ->
        verifier: GitHubManifestFingerprint -> productionClosure: GitHubSealedHistoryProductionClosure ->
        records: GitHubSealedHistoryRecord list -> lookupIndex: GitHubSealedHistoryLookup list ->
        createdAt: System.DateTimeOffset -> Result<GitHubSealedHistoryQualification, GitHubSealedHistoryFinding list>

    val verify:
        expectedRecords: GitHubSealedHistoryRecord list -> expectedSeal: string ->
        qualification: GitHubSealedHistoryQualification -> Result<GitHubSealedHistoryQualification, GitHubSealedHistoryFinding list>

    val validateControls:
        generated: GitHubSealedHistoryControlResult list -> independent: GitHubSealedHistoryControlResult list ->
            Result<unit, GitHubSealedHistoryQualificationFinding list>
