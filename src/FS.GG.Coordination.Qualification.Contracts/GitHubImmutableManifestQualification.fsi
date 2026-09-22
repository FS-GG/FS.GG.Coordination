namespace FS.GG.Coordination.Qualification.Contracts

type GitHubManifestFingerprint =
    {
        Name: string
        Version: string
        Sha256: string
        Bytes: int64
    }

type GitHubManifestOldSubject =
    {
        Identity: string
        GlobalId: string
        SourceRevision: string
        Schema: string
        BytesSha256: string
        ValueSha256: string
    }

type GitHubManifestV2Result =
    {
        Identity: string
        GlobalId: string
        Revision: string
        PayloadSha256: string
        Outcome: string
    }

type GitHubManifestSubjectBinding =
    {
        Old: GitHubManifestOldSubject
        Result: GitHubManifestV2Result
        Disposition: string
    }

type GitHubManifestLiveOperation =
    {
        Identity: string
        Generation: int64
        Kind: string
        Target: string
        State: string
        PayloadSha256: string
    }

type GitHubManifestReceiverHead =
    {
        Receiver: string
        RepositoryId: string
        CommitSha: string
        TreeSha: string
        PinsSha256: string
    }

type GitHubManifestSettingsPlan =
    {
        Repository: string
        PrestateSha256: string
        DesiredSha256: string
        PlanSha256: string
    }

type GitHubManifestArchiveBinding =
    {
        Authority: string
        SourceSchema: string
        SourceBytesSha256: string
        ArchiveSha256: string
        LookupIndexSha256: string
        VerifierSha256: string
    }

type GitHubManifestPhasePlan =
    {
        Order: int
        PhaseId: string
        InputSha256: string
        OperationsSha256: string
        ReceiptSha256: string
        RollbackInputIds: string list
    }

type GitHubManifestReviewer =
    {
        Login: string
        GlobalId: string
        DecisionSha256: string
    }

type GitHubManifestRollbackInput =
    {
        Identity: string
        Kind: string
        Revision: string
        PayloadSha256: string
    }

type GitHubImmutableManifest =
    {
        SchemaVersion: int
        ManifestId: string
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        PredecessorReceiptDigest: string
        DiscoverySourceRevision: string
        DiscoveryNormalizedDigest: string
        DiscoverySeal: string
        OldModel: GitHubManifestFingerprint
        NewModel: GitHubManifestFingerprint
        ArtifactFingerprints: GitHubManifestFingerprint list
        Subjects: GitHubManifestSubjectBinding list
        LiveOperations: GitHubManifestLiveOperation list
        ReceiverHeads: GitHubManifestReceiverHead list
        SettingsPlans: GitHubManifestSettingsPlan list
        Archives: GitHubManifestArchiveBinding list
        PhasePlans: GitHubManifestPhasePlan list
        Reviewers: GitHubManifestReviewer list
        RollbackInputs: GitHubManifestRollbackInput list
        CreatedAt: System.DateTimeOffset
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubImmutableManifestFinding =
    | InvalidManifestField of string
    | InvalidManifestFingerprint of string
    | InvalidManifestPopulation of string
    | InvalidManifestSubject of string
    | InvalidManifestPhasePlan of string
    | MissingManifestRollbackInput of string
    | AlteredManifestDigest
    | AlteredManifestSeal

type GitHubImmutableManifestControl =
    | ManifestPrerequisiteReceipt
    | ManifestDiscoveryBinding
    | ManifestOldAndNewModels
    | ManifestArtifactFingerprints
    | ManifestGlobalIds
    | ManifestOldBytesAndValues
    | ManifestV2Results
    | ManifestLiveOperations
    | ManifestReceiverHeads
    | ManifestSettingsPlans
    | ManifestArchiveDigests
    | ManifestDispositions
    | ManifestPhasePlans
    | ManifestReviewers
    | ManifestRollbackInputs
    | ManifestNoOmission
    | ManifestTamperRefusal
    | ManifestReplay
    | ManifestNoMutation

type GitHubImmutableManifestControlResult =
    {
        Control: GitHubImmutableManifestControl
        ControlPassed: bool
        BaselineGreen: bool
    }

type GitHubImmutableManifestQualificationFinding =
    {
        Code: string
        ControlId: string
        Message: string
    }

module GitHubImmutableManifestQualification =
    val requiredControls: GitHubImmutableManifestControl list
    val controlId: GitHubImmutableManifestControl -> string

    val qualify:
        manifestId: string ->
        roadmapRevision: string ->
        roadmapSha256: string ->
        unitContractSha256: string ->
        predecessorReceiptDigest: string ->
        discoverySourceRevision: string ->
        discoveryNormalizedDigest: string ->
        discoverySeal: string ->
        discoveredSubjects: string list ->
        oldModel: GitHubManifestFingerprint ->
        newModel: GitHubManifestFingerprint ->
        artifactFingerprints: GitHubManifestFingerprint list ->
        subjects: GitHubManifestSubjectBinding list ->
        liveOperations: GitHubManifestLiveOperation list ->
        receiverHeads: GitHubManifestReceiverHead list ->
        settingsPlans: GitHubManifestSettingsPlan list ->
        archives: GitHubManifestArchiveBinding list ->
        phasePlans: GitHubManifestPhasePlan list ->
        reviewers: GitHubManifestReviewer list ->
        rollbackInputs: GitHubManifestRollbackInput list ->
        createdAt: System.DateTimeOffset ->
            Result<GitHubImmutableManifest, GitHubImmutableManifestFinding list>

    val verify:
        discoveredSubjects: string list ->
        expectedSeal: string ->
        manifest: GitHubImmutableManifest ->
            Result<GitHubImmutableManifest, GitHubImmutableManifestFinding list>

    val validateControls:
        generated: GitHubImmutableManifestControlResult list ->
        independent: GitHubImmutableManifestControlResult list ->
            Result<unit, GitHubImmutableManifestQualificationFinding list>
