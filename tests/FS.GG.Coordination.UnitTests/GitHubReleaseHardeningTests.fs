module FS.GG.Coordination.GitHubReleaseHardeningTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private digest = "9e6528221e1b8440f19495a2783ef70872c2058060f89a08d37cf4438afeb5f3"
let private baseline =
    { SchemaVersion = 1; Repository = "FS-GG/FS.GG.Coordination"
      SourceRevision = "84e488f046c624b2789d520cd062bf99d964b3b5"
      RoadmapRevision = "185494fa8ba3986834141c2ddc4e8325410df260"
      RoadmapSha256 = "4a7229b7e1fc5b9417d7d6cf14a4f22ba60e6d8a69cac4ce369d908d9e37ed39"
      PrerequisiteReceiptDigest = "9227977242b530755cbc28ff9093fa810aab9647037d3ae4b60cd7311c86cd0f"
      Complete = true; Environment = "release"; EnvironmentProtected = true; RequiredReviewers = 1
      OidcProvider = "github-actions-oidc"; OidcAudience = "nuget.org"; LongLivedCredential = false
      ReleaseImmutable = true; TagImmutable = true; PackCount = 1
      PackageId = "FS.GG.Coordination.Protocol"; PackageVersion = "2.0.0-candidate"; PackageSha256 = digest
      Stages = GitHubReleaseHardeningQualification.requiredStages
      FeedPublications = [ { Feed = "github-packages"; Ordinal = 1; PackageSha256 = digest }; { Feed = "nuget-org"; Ordinal = 2; PackageSha256 = digest } ]
      Recovery = { FailureAfterFeed = "github-packages"; ResumeFeed = "nuget-org"; SourcePackageSha256 = digest; Repack = false }
      SbomFormat = "spdx-2.3"; SbomSubjectSha256 = digest; AttestationPredicate = "slsa-provenance-v1"
      AttestationSubjectSha256 = digest; DependencySubmission = true; DependencyReview = true
      PublicDownloadAnonymous = true; PublicDownloadStatus = 200; PublicDownloadSha256 = digest }

[<Fact>]
let ``complete protected OIDC plan compiles and replays exactly`` () =
    let report = GitHubReleaseHardeningQualification.compile baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal(11, report.StageCount)
    Assert.Equal(2, report.FeedCount)
    Assert.Equal(1, report.PackCount)
    Assert.Equal(Ok report, GitHubReleaseHardeningQualification.verify report.Seal baseline)

[<Fact>]
let ``environment identity and immutable surfaces fail closed`` () =
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with EnvironmentProtected = false } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with LongLivedCredential = true } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with ReleaseImmutable = false } |> Result.isError)

[<Fact>]
let ``one pack and ordered dual feed identity fail closed`` () =
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with PackCount = 2 } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with PackageId = "Attacker.Package" } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with PackageVersion = "9.9.9" } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with FeedPublications = List.rev baseline.FeedPublications } |> Result.isError)
    let divergent = baseline.FeedPublications |> List.map (fun value -> if value.Feed = "nuget-org" then { value with PackageSha256 = String.replicate 64 "0" } else value)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with FeedPublications = divergent } |> Result.isError)

[<Fact>]
let ``repository and source identity are exact`` () =
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with Repository = "FS-GG/other" } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with SourceRevision = String.replicate 40 "0" } |> Result.isError)

[<Fact>]
let ``SBOM attestation dependency and public verification fail closed`` () =
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with SbomSubjectSha256 = String.replicate 64 "0" } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with AttestationPredicate = "unknown" } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with DependencyReview = false } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with PublicDownloadAnonymous = false } |> Result.isError)

[<Fact>]
let ``stage ordering and no-repack recovery are exact`` () =
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with Stages = List.rev baseline.Stages } |> Result.isError)
    Assert.True(GitHubReleaseHardeningQualification.compile { baseline with Recovery = { baseline.Recovery with Repack = true } } |> Result.isError)

[<Fact>]
let ``generated and independent control inventories must both pass`` () =
    let passing: GitHubReleaseHardeningControlResult list = GitHubReleaseHardeningQualification.requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubReleaseHardeningQualification.validate passing passing)
    let broken = passing |> List.map (fun value -> if value.Control = PublicDownloadControl then { value with ControlPassed = false } else value)
    Assert.True(GitHubReleaseHardeningQualification.validate passing broken |> Result.isError)
