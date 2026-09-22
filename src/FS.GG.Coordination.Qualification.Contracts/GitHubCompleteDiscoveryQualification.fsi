namespace FS.GG.Coordination.Qualification.Contracts

type GitHubDiscoverySubject =
    {
        Identity: string
        Revision: string
        PayloadSha256: string
    }

type GitHubDiscoveryAuthorityRead =
    {
        Authority: string
        ObservedAt: System.DateTimeOffset
        PageCount: int
        ItemCount: int
        Terminal: bool
        NextCursor: string option
        HighWaterMark: string
        Subjects: GitHubDiscoverySubject list
    }

type GitHubDiscoveryPass =
    {
        SourceRevision: string
        StartedAt: System.DateTimeOffset
        CompletedAt: System.DateTimeOffset
        Authorities: GitHubDiscoveryAuthorityRead list
    }

type GitHubCompleteDiscovery =
    {
        SchemaVersion: int
        RoadmapRevision: string
        RoadmapSha256: string
        UnitContractSha256: string
        ReceiptDigests: string list
        First: GitHubDiscoveryPass
        Second: GitHubDiscoveryPass
        NormalizedDigest: string
        Seal: string
    }

[<RequireQualifiedAccess>]
type GitHubCompleteDiscoveryFinding =
    | InvalidDiscoveryField of string
    | InvalidDiscoveryAuthoritySet
    | InvalidDiscoveryAuthority of string
    | IncompleteDiscoveryPagination of string
    | InvalidDiscoveryHighWaterMark of string
    | InvalidDiscoverySubject of string
    | NonQuiescentDiscovery of firstDigest: string * secondDigest: string
    | AlteredDiscoverySeal

type GitHubCompleteDiscoveryControl =
    | DiscoveryPrerequisites
    | DiscoveryRoadmap
    | DiscoveryAuthorityPopulation
    | DiscoveryOpenAndClosedIssues
    | DiscoveryProjectItemsAndFields
    | DiscoveryHierarchyAndDependencies
    | DiscoveryClaimAndEventStreams
    | DiscoveryReviewDeliveryAndRelease
    | DiscoveryRepositorySettings
    | DiscoveryWorkflowPins
    | DiscoveryReceiverIdentities
    | DiscoveryTerminalPagination
    | DiscoveryHighWaterMarks
    | DiscoveryCanonicalSubjects
    | DiscoveryTwoPassQuiescence
    | DiscoveryUnknownSubjectRefusal
    | DiscoveryLostPageRefusal
    | DiscoveryReplay
    | DiscoveryNoMutation

type GitHubCompleteDiscoveryControlResult =
    {
        Control: GitHubCompleteDiscoveryControl
        ControlPassed: bool
        BaselineGreen: bool
    }

type GitHubCompleteDiscoveryQualificationFinding =
    {
        Code: string
        ControlId: string
        Message: string
    }

module GitHubCompleteDiscoveryQualification =
    val expectedAuthorities: string list
    val requiredControls: GitHubCompleteDiscoveryControl list
    val controlId: GitHubCompleteDiscoveryControl -> string
    val normalizedPassDigest: GitHubDiscoveryPass -> Result<string, GitHubCompleteDiscoveryFinding list>

    val qualify:
        roadmapRevision: string ->
        roadmapSha256: string ->
        unitContractSha256: string ->
        receiptDigests: string list ->
        first: GitHubDiscoveryPass ->
        second: GitHubDiscoveryPass ->
            Result<GitHubCompleteDiscovery, GitHubCompleteDiscoveryFinding list>

    val verify: expectedSeal: string -> GitHubCompleteDiscovery -> Result<GitHubCompleteDiscovery, GitHubCompleteDiscoveryFinding list>

    val validateControls:
        generated: GitHubCompleteDiscoveryControlResult list ->
        independent: GitHubCompleteDiscoveryControlResult list ->
            Result<unit, GitHubCompleteDiscoveryQualificationFinding list>
