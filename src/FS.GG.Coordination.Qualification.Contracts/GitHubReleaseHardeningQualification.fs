namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type GitHubReleaseStage =
    | EnterProtectedEnvironment | MintOidcIdentity | PackOnce | GenerateSbom | SubmitDependencies
    | ReviewDependencies | AttestPackage | PublishGitHubFeed | PublishNugetFeed
    | CreateImmutableRelease | VerifyPublicDownload

type GitHubReleaseFeedPublication = { Feed: string; Ordinal: int; PackageSha256: string }
type GitHubReleaseRecovery = { FailureAfterFeed: string; ResumeFeed: string; SourcePackageSha256: string; Repack: bool }
type GitHubReleaseHardeningSnapshot =
    { SchemaVersion: int; Repository: string; SourceRevision: string; RoadmapRevision: string; RoadmapSha256: string
      PrerequisiteReceiptDigest: string; Complete: bool; Environment: string; EnvironmentProtected: bool
      RequiredReviewers: int; OidcProvider: string; OidcAudience: string; LongLivedCredential: bool
      ReleaseImmutable: bool; TagImmutable: bool; PackCount: int; PackageId: string; PackageVersion: string
      PackageSha256: string; Stages: GitHubReleaseStage list; FeedPublications: GitHubReleaseFeedPublication list
      Recovery: GitHubReleaseRecovery; SbomFormat: string; SbomSubjectSha256: string
      AttestationPredicate: string; AttestationSubjectSha256: string; DependencySubmission: bool
      DependencyReview: bool; PublicDownloadAnonymous: bool; PublicDownloadStatus: int
      PublicDownloadSha256: string }
type GitHubReleaseHardeningReport =
    { Repository: string; SourceRevision: string; StageCount: int; FeedCount: int; PackCount: int
      PackageSha256: string; Seal: string }
type GitHubReleaseHardeningFinding =
    | InvalidReleaseField of string | IncompleteReleaseInventory | UnprotectedReleaseEnvironment
    | InvalidOidcIdentity | LongLivedReleaseCredential | MutableReleaseSurface | InvalidReleaseStageOrder
    | RepackedReleaseArtifact | InvalidFeedPublication | InvalidDualFeedRecovery | InvalidSbomSubject
    | InvalidAttestationSubject | MissingDependencyControl | InvalidPublicDownload
    | ReleaseDigestDisagreement | AlteredReleaseSeal
type GitHubReleaseHardeningControl =
    | ReleasePrerequisite | ReleaseCompleteness | ReleaseSourceBinding | ReleaseRoadmapBinding
    | ProtectedReleaseEnvironment | OidcOnlyIdentity | ImmutableReleaseAndTag | ReleaseStageOrdering
    | OnePackIdentity | DualFeedPublication | NoRepackRecovery | SbomBinding | AttestationBinding
    | DependencySubmissionControl | DependencyReviewControl | PublicDownloadControl
    | ReleaseDigestAgreement | ExactReleaseSeal | ExactReleaseReplay | QuintReleaseUnchanged
    | NoReleaseMutationSurface
type GitHubReleaseHardeningControlResult = { Control: GitHubReleaseHardeningControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubReleaseHardeningQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubReleaseHardeningQualification =
    let requiredStages =
        [ EnterProtectedEnvironment; MintOidcIdentity; PackOnce; GenerateSbom; SubmitDependencies
          ReviewDependencies; AttestPackage; PublishGitHubFeed; PublishNugetFeed
          CreateImmutableRelease; VerifyPublicDownload ]

    let requiredControls =
        [ ReleasePrerequisite; ReleaseCompleteness; ReleaseSourceBinding; ReleaseRoadmapBinding
          ProtectedReleaseEnvironment; OidcOnlyIdentity; ImmutableReleaseAndTag; ReleaseStageOrdering
          OnePackIdentity; DualFeedPublication; NoRepackRecovery; SbomBinding; AttestationBinding
          DependencySubmissionControl; DependencyReviewControl; PublicDownloadControl
          ReleaseDigestAgreement; ExactReleaseSeal; ExactReleaseReplay; QuintReleaseUnchanged
          NoReleaseMutationSurface ]

    let stageId = function
        | EnterProtectedEnvironment -> "protected-environment" | MintOidcIdentity -> "oidc"
        | PackOnce -> "pack-once" | GenerateSbom -> "sbom" | SubmitDependencies -> "dependency-submission"
        | ReviewDependencies -> "dependency-review" | AttestPackage -> "attestation"
        | PublishGitHubFeed -> "publish-github-feed" | PublishNugetFeed -> "publish-nuget-feed"
        | CreateImmutableRelease -> "immutable-release" | VerifyPublicDownload -> "public-download"

    let controlId = function
        | ReleasePrerequisite -> "release-prerequisite" | ReleaseCompleteness -> "release-completeness"
        | ReleaseSourceBinding -> "release-source-binding" | ReleaseRoadmapBinding -> "release-roadmap-binding"
        | ProtectedReleaseEnvironment -> "protected-release-environment" | OidcOnlyIdentity -> "oidc-only-identity"
        | ImmutableReleaseAndTag -> "immutable-release-tag" | ReleaseStageOrdering -> "release-stage-ordering"
        | OnePackIdentity -> "one-pack-identity" | DualFeedPublication -> "dual-feed-publication"
        | NoRepackRecovery -> "no-repack-recovery" | SbomBinding -> "sbom-binding"
        | AttestationBinding -> "attestation-binding" | DependencySubmissionControl -> "dependency-submission"
        | DependencyReviewControl -> "dependency-review" | PublicDownloadControl -> "public-download"
        | ReleaseDigestAgreement -> "release-digest-agreement" | ExactReleaseSeal -> "exact-release-seal"
        | ExactReleaseReplay -> "exact-release-replay" | QuintReleaseUnchanged -> "quint-release-unchanged"
        | NoReleaseMutationSurface -> "no-release-mutation-surface"

    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private isSha value = not (String.IsNullOrWhiteSpace value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private isRevision value = not (String.IsNullOrWhiteSpace value) && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount(value)}:{value}"
    let private boolText value = if value then "true" else "false"

    let private seal (snapshot: GitHubReleaseHardeningSnapshot) =
        [ string snapshot.SchemaVersion; snapshot.Repository; snapshot.SourceRevision; snapshot.RoadmapRevision
          snapshot.RoadmapSha256; snapshot.PrerequisiteReceiptDigest; boolText snapshot.Complete; snapshot.Environment
          boolText snapshot.EnvironmentProtected; string snapshot.RequiredReviewers; snapshot.OidcProvider
          snapshot.OidcAudience; boolText snapshot.LongLivedCredential; boolText snapshot.ReleaseImmutable
          boolText snapshot.TagImmutable; string snapshot.PackCount; snapshot.PackageId; snapshot.PackageVersion
          snapshot.PackageSha256; snapshot.Stages |> List.map stageId |> String.concat ","
          snapshot.FeedPublications |> List.map (fun value -> $"{value.Ordinal}:{value.Feed}:{value.PackageSha256}") |> String.concat ","
          snapshot.Recovery.FailureAfterFeed; snapshot.Recovery.ResumeFeed; snapshot.Recovery.SourcePackageSha256
          boolText snapshot.Recovery.Repack; snapshot.SbomFormat; snapshot.SbomSubjectSha256
          snapshot.AttestationPredicate; snapshot.AttestationSubjectSha256; boolText snapshot.DependencySubmission
          boolText snapshot.DependencyReview; boolText snapshot.PublicDownloadAnonymous
          string snapshot.PublicDownloadStatus; snapshot.PublicDownloadSha256 ]
        |> List.map frame |> String.concat "" |> sha

    let compile (snapshot: GitHubReleaseHardeningSnapshot) =
        let findings = ResizeArray<GitHubReleaseHardeningFinding>()
        let invalid name value = if String.IsNullOrWhiteSpace value then findings.Add(InvalidReleaseField name)
        invalid "repository" snapshot.Repository; invalid "sourceRevision" snapshot.SourceRevision
        invalid "roadmapRevision" snapshot.RoadmapRevision; invalid "environment" snapshot.Environment
        invalid "oidcProvider" snapshot.OidcProvider; invalid "oidcAudience" snapshot.OidcAudience
        invalid "packageId" snapshot.PackageId; invalid "packageVersion" snapshot.PackageVersion
        if snapshot.SchemaVersion <> 1 then findings.Add(InvalidReleaseField "schemaVersion")
        if not (isRevision snapshot.SourceRevision) then findings.Add(InvalidReleaseField "sourceRevision")
        if not (isRevision snapshot.RoadmapRevision) then findings.Add(InvalidReleaseField "roadmapRevision")
        for name, value in [ "roadmapSha256", snapshot.RoadmapSha256; "prerequisiteReceiptDigest", snapshot.PrerequisiteReceiptDigest
                             "packageSha256", snapshot.PackageSha256; "sbomSubjectSha256", snapshot.SbomSubjectSha256
                             "attestationSubjectSha256", snapshot.AttestationSubjectSha256
                             "publicDownloadSha256", snapshot.PublicDownloadSha256 ] do
            if not (isSha value) then findings.Add(InvalidReleaseField name)
        if snapshot.SourceRevision <> "84e488f046c624b2789d520cd062bf99d964b3b5" then findings.Add(InvalidReleaseField "sourceRevisionBinding")
        if snapshot.RoadmapRevision <> "185494fa8ba3986834141c2ddc4e8325410df260" || snapshot.RoadmapSha256 <> "4a7229b7e1fc5b9417d7d6cf14a4f22ba60e6d8a69cac4ce369d908d9e37ed39" then findings.Add(InvalidReleaseField "roadmapBinding")
        if snapshot.PrerequisiteReceiptDigest <> "9227977242b530755cbc28ff9093fa810aab9647037d3ae4b60cd7311c86cd0f" then findings.Add(InvalidReleaseField "prerequisiteBinding")
        if not snapshot.Complete then findings.Add(IncompleteReleaseInventory)
        if snapshot.Environment <> "release" || not snapshot.EnvironmentProtected || snapshot.RequiredReviewers < 1 then findings.Add(UnprotectedReleaseEnvironment)
        if snapshot.OidcProvider <> "github-actions-oidc" || snapshot.OidcAudience <> "nuget.org" then findings.Add(InvalidOidcIdentity)
        if snapshot.LongLivedCredential then findings.Add(LongLivedReleaseCredential)
        if not snapshot.ReleaseImmutable || not snapshot.TagImmutable then findings.Add(MutableReleaseSurface)
        if snapshot.Stages <> requiredStages then findings.Add(InvalidReleaseStageOrder)
        if snapshot.PackCount <> 1 then findings.Add(RepackedReleaseArtifact)
        let expectedFeeds = [ "github-packages", 1; "nuget-org", 2 ]
        if snapshot.FeedPublications |> List.map (fun value -> value.Feed, value.Ordinal) <> expectedFeeds then findings.Add(InvalidFeedPublication)
        if snapshot.FeedPublications |> List.exists (fun value -> value.PackageSha256 <> snapshot.PackageSha256) then findings.Add(ReleaseDigestDisagreement)
        if snapshot.Recovery.FailureAfterFeed <> "github-packages" || snapshot.Recovery.ResumeFeed <> "nuget-org" || snapshot.Recovery.Repack then findings.Add(InvalidDualFeedRecovery)
        if snapshot.Recovery.SourcePackageSha256 <> snapshot.PackageSha256 then findings.Add(ReleaseDigestDisagreement)
        if snapshot.SbomFormat <> "spdx-2.3" || snapshot.SbomSubjectSha256 <> snapshot.PackageSha256 then findings.Add(InvalidSbomSubject)
        if snapshot.AttestationPredicate <> "slsa-provenance-v1" || snapshot.AttestationSubjectSha256 <> snapshot.PackageSha256 then findings.Add(InvalidAttestationSubject)
        if not snapshot.DependencySubmission || not snapshot.DependencyReview then findings.Add(MissingDependencyControl)
        if not snapshot.PublicDownloadAnonymous || snapshot.PublicDownloadStatus <> 200 then findings.Add(InvalidPublicDownload)
        if snapshot.PublicDownloadSha256 <> snapshot.PackageSha256 then findings.Add(ReleaseDigestDisagreement)
        if findings.Count = 0 then
            Ok { Repository = snapshot.Repository; SourceRevision = snapshot.SourceRevision; StageCount = snapshot.Stages.Length
                 FeedCount = snapshot.FeedPublications.Length; PackCount = snapshot.PackCount
                 PackageSha256 = snapshot.PackageSha256; Seal = seal snapshot }
        else Error(List.ofSeq findings)

    let verify expected snapshot =
        match compile snapshot with
        | Ok report when report.Seal = expected -> Ok report
        | Ok _ -> Error [ AlteredReleaseSeal ]
        | Error findings -> Error findings

    let validate generated independent =
        let inspect sourceName results =
            let expected = requiredControls |> List.map controlId
            let actual = results |> List.map (fun result -> controlId result.Control)
            [ if actual <> expected then
                  yield { Code = "GRH-CONTROL-INVENTORY"; ControlId = sourceName; Message = "ordered control inventory differs" }
              for result in results do
                  if not result.BaselineGreen then
                      yield { Code = "GRH-BASELINE"; ControlId = controlId result.Control; Message = $"{sourceName} baseline is not green" }
                  if not result.ControlPassed then
                      yield { Code = "GRH-CONTROL"; ControlId = controlId result.Control; Message = $"{sourceName} control did not pass" } ]
        let findings = inspect "generated" generated @ inspect "independent" independent
        if List.isEmpty findings then Ok () else Error findings
