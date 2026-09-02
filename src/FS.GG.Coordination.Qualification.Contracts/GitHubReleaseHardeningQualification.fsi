namespace FS.GG.Coordination.Qualification.Contracts

type GitHubReleaseStage =
    | EnterProtectedEnvironment
    | MintOidcIdentity
    | PackOnce
    | GenerateSbom
    | SubmitDependencies
    | ReviewDependencies
    | AttestPackage
    | PublishGitHubFeed
    | PublishNugetFeed
    | CreateImmutableRelease
    | VerifyPublicDownload

type GitHubReleaseFeedPublication =
    { Feed: string
      Ordinal: int
      PackageSha256: string }

type GitHubReleaseRecovery =
    { FailureAfterFeed: string
      ResumeFeed: string
      SourcePackageSha256: string
      Repack: bool }

type GitHubReleaseHardeningSnapshot =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      RoadmapRevision: string
      RoadmapSha256: string
      PrerequisiteReceiptDigest: string
      Complete: bool
      Environment: string
      EnvironmentProtected: bool
      RequiredReviewers: int
      OidcProvider: string
      OidcAudience: string
      LongLivedCredential: bool
      ReleaseImmutable: bool
      TagImmutable: bool
      PackCount: int
      PackageId: string
      PackageVersion: string
      PackageSha256: string
      Stages: GitHubReleaseStage list
      FeedPublications: GitHubReleaseFeedPublication list
      Recovery: GitHubReleaseRecovery
      SbomFormat: string
      SbomSubjectSha256: string
      AttestationPredicate: string
      AttestationSubjectSha256: string
      DependencySubmission: bool
      DependencyReview: bool
      PublicDownloadAnonymous: bool
      PublicDownloadStatus: int
      PublicDownloadSha256: string }

type GitHubReleaseHardeningReport =
    { Repository: string
      SourceRevision: string
      StageCount: int
      FeedCount: int
      PackCount: int
      PackageSha256: string
      Seal: string }

type GitHubReleaseHardeningFinding =
    | InvalidReleaseField of string
    | IncompleteReleaseInventory
    | UnprotectedReleaseEnvironment
    | InvalidOidcIdentity
    | LongLivedReleaseCredential
    | MutableReleaseSurface
    | InvalidReleaseStageOrder
    | RepackedReleaseArtifact
    | InvalidFeedPublication
    | InvalidDualFeedRecovery
    | InvalidSbomSubject
    | InvalidAttestationSubject
    | MissingDependencyControl
    | InvalidPublicDownload
    | ReleaseDigestDisagreement
    | AlteredReleaseSeal

type GitHubReleaseHardeningControl =
    | ReleasePrerequisite | ReleaseCompleteness | ReleaseSourceBinding | ReleaseRoadmapBinding
    | ProtectedReleaseEnvironment | OidcOnlyIdentity | ImmutableReleaseAndTag | ReleaseStageOrdering
    | OnePackIdentity | DualFeedPublication | NoRepackRecovery | SbomBinding | AttestationBinding
    | DependencySubmissionControl | DependencyReviewControl | PublicDownloadControl
    | ReleaseDigestAgreement | ExactReleaseSeal | ExactReleaseReplay | QuintReleaseUnchanged
    | NoReleaseMutationSurface

type GitHubReleaseHardeningControlResult =
    { Control: GitHubReleaseHardeningControl
      ControlPassed: bool
      BaselineGreen: bool }

type GitHubReleaseHardeningQualificationFinding =
    { Code: string
      ControlId: string
      Message: string }

module GitHubReleaseHardeningQualification =
    val requiredStages: GitHubReleaseStage list
    val requiredControls: GitHubReleaseHardeningControl list
    val stageId: GitHubReleaseStage -> string
    val controlId: GitHubReleaseHardeningControl -> string
    val compile: GitHubReleaseHardeningSnapshot -> Result<GitHubReleaseHardeningReport, GitHubReleaseHardeningFinding list>
    val verify: string -> GitHubReleaseHardeningSnapshot -> Result<GitHubReleaseHardeningReport, GitHubReleaseHardeningFinding list>
    val validate: GitHubReleaseHardeningControlResult list -> GitHubReleaseHardeningControlResult list -> Result<unit, GitHubReleaseHardeningQualificationFinding list>
