#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubReleaseHardeningQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let defaultRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let root = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.tryFind Directory.Exists |> Option.map Path.GetFullPath |> Option.defaultValue defaultRoot
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-6")
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let boolean (node: JsonObject) (name: string) = node[name].GetValue<bool>()
let number (node: JsonObject) (name: string) = node[name].GetValue<int>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let sha256File path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let stage = function
    | "protected-environment" -> EnterProtectedEnvironment | "oidc" -> MintOidcIdentity
    | "pack-once" -> PackOnce | "sbom" -> GenerateSbom | "dependency-submission" -> SubmitDependencies
    | "dependency-review" -> ReviewDependencies | "attestation" -> AttestPackage
    | "publish-github-feed" -> PublishGitHubFeed | "publish-nuget-feed" -> PublishNugetFeed
    | "immutable-release" -> CreateImmutableRelease | "public-download" -> VerifyPublicDownload
    | value -> failwith $"unknown release stage: {value}"
let feed (node: JsonNode) =
    let value = node.AsObject()
    { Feed = text value "feed"; Ordinal = number value "ordinal"; PackageSha256 = text value "packageSha256" }
let recoveryNode = corpus["recovery"].AsObject()
let snapshot =
    { SchemaVersion = number corpus "schemaVersion"; Repository = text corpus "repository"
      SourceRevision = text corpus "sourceRevision"; RoadmapRevision = text corpus "roadmapRevision"
      RoadmapSha256 = text corpus "roadmapSha256"; PrerequisiteReceiptDigest = text corpus "prerequisiteReceiptDigest"
      Complete = boolean corpus "complete"; Environment = text corpus "environment"
      EnvironmentProtected = boolean corpus "environmentProtected"; RequiredReviewers = number corpus "requiredReviewers"
      OidcProvider = text corpus "oidcProvider"; OidcAudience = text corpus "oidcAudience"
      LongLivedCredential = boolean corpus "longLivedCredential"; ReleaseImmutable = boolean corpus "releaseImmutable"
      TagImmutable = boolean corpus "tagImmutable"; PackCount = number corpus "packCount"
      PackageId = text corpus "packageId"; PackageVersion = text corpus "packageVersion"; PackageSha256 = text corpus "packageSha256"
      Stages = texts corpus "stages" |> List.map stage
      FeedPublications = corpus["feedPublications"].AsArray() |> Seq.map feed |> List.ofSeq
      Recovery = { FailureAfterFeed = text recoveryNode "failureAfterFeed"; ResumeFeed = text recoveryNode "resumeFeed"
                   SourcePackageSha256 = text recoveryNode "sourcePackageSha256"; Repack = boolean recoveryNode "repack" }
      SbomFormat = text corpus "sbomFormat"; SbomSubjectSha256 = text corpus "sbomSubjectSha256"
      AttestationPredicate = text corpus "attestationPredicate"; AttestationSubjectSha256 = text corpus "attestationSubjectSha256"
      DependencySubmission = boolean corpus "dependencySubmission"; DependencyReview = boolean corpus "dependencyReview"
      PublicDownloadAnonymous = boolean corpus "publicDownloadAnonymous"; PublicDownloadStatus = number corpus "publicDownloadStatus"
      PublicDownloadSha256 = text corpus "publicDownloadSha256" }
let compile value = GitHubReleaseHardeningQualification.compile value
let refused value = compile value |> Result.isError
let report = compile snapshot |> Result.defaultWith (failwithf "release hardening baseline refused: %A")

if fsi.CommandLineArgs |> Array.contains "--mint" then printfn "%s" report.Seal else
    let receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.5.json"))).AsObject()
    if text receipt "digest" <> snapshot.PrerequisiteReceiptDigest then failwith "accepted GS2-06.5 receipt differs"
    if snapshot.SourceRevision <> "84e488f046c624b2789d520cd062bf99d964b3b5" then failwith "candidate source binding differs"
    if snapshot.RoadmapRevision <> "185494fa8ba3986834141c2ddc4e8325410df260" || snapshot.RoadmapSha256 <> "4a7229b7e1fc5b9417d7d6cf14a4f22ba60e6d8a69cac4ce369d908d9e37ed39" then failwith "accepted roadmap binding differs"
    if report.StageCount <> number expectations "stageCount" || report.FeedCount <> number expectations "feedCount" || report.PackCount <> number expectations "packCount" then failwith "independent cardinality differs"
    if report.Seal <> text expectations "expectedSeal" then failwith "baseline seal differs"
    if GitHubReleaseHardeningQualification.verify report.Seal snapshot <> Ok report then failwith "exact replay failed"
    let expectedControls = GitHubReleaseHardeningQualification.requiredControls |> List.map GitHubReleaseHardeningQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs"
    let zero = String.replicate 64 "0"
    let mutateFeed map = { snapshot with FeedPublications = snapshot.FeedPublications |> List.map map }
    let generatedMutation = function
        | ReleasePrerequisite -> refused { snapshot with PrerequisiteReceiptDigest = zero }
        | ReleaseCompleteness -> refused { snapshot with Complete = false }
        | ReleaseSourceBinding -> refused { snapshot with SourceRevision = "main" } && refused { snapshot with Repository = "FS-GG/other" }
        | ReleaseRoadmapBinding -> refused { snapshot with RoadmapSha256 = zero }
        | ProtectedReleaseEnvironment -> refused { snapshot with EnvironmentProtected = false }
        | OidcOnlyIdentity -> refused { snapshot with LongLivedCredential = true }
        | ImmutableReleaseAndTag -> refused { snapshot with TagImmutable = false }
        | ReleaseStageOrdering -> refused { snapshot with Stages = List.rev snapshot.Stages }
        | OnePackIdentity -> refused { snapshot with PackCount = 2 } && refused { snapshot with PackageId = "Attacker.Package" } && refused { snapshot with PackageVersion = "9.9.9" }
        | DualFeedPublication -> refused { snapshot with FeedPublications = snapshot.FeedPublications.Tail }
        | NoRepackRecovery -> refused { snapshot with Recovery = { snapshot.Recovery with Repack = true } }
        | SbomBinding -> refused { snapshot with SbomSubjectSha256 = zero }
        | AttestationBinding -> refused { snapshot with AttestationSubjectSha256 = zero }
        | DependencySubmissionControl -> refused { snapshot with DependencySubmission = false }
        | DependencyReviewControl -> refused { snapshot with DependencyReview = false }
        | PublicDownloadControl -> refused { snapshot with PublicDownloadAnonymous = false }
        | ReleaseDigestAgreement -> refused (mutateFeed (fun value -> if value.Feed = "nuget-org" then { value with PackageSha256 = zero } else value))
        | ExactReleaseSeal -> GitHubReleaseHardeningQualification.verify zero snapshot |> Result.isError
        | ExactReleaseReplay -> GitHubReleaseHardeningQualification.verify report.Seal snapshot = Ok report
        | QuintReleaseUnchanged -> sha256File (Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")) = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoReleaseMutationSurface ->
            let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubReleaseHardeningQualification.fsi"))
            [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val publish"; "PATCH"; "POST"; "DELETE" ] |> List.forall (surface.Contains >> not)
    let independentMutation control =
        match control with
        | ReleasePrerequisite -> snapshot.PrerequisiteReceiptDigest = text receipt "digest"
        | ReleaseCompleteness -> snapshot.Complete && report.StageCount = 11
        | ReleaseSourceBinding -> snapshot.Repository = "FS-GG/FS.GG.Coordination" && snapshot.SourceRevision.Length = 40
        | ReleaseRoadmapBinding -> snapshot.RoadmapRevision.Length = 40
        | ProtectedReleaseEnvironment -> snapshot.Environment = "release" && snapshot.RequiredReviewers > 0
        | OidcOnlyIdentity -> snapshot.OidcProvider = "github-actions-oidc" && not snapshot.LongLivedCredential
        | ImmutableReleaseAndTag -> snapshot.ReleaseImmutable && snapshot.TagImmutable
        | OnePackIdentity -> report.PackCount = 1 && snapshot.PackageId = "FS.GG.Coordination.Protocol" && snapshot.PackageVersion = "2.0.0-candidate"
        | DualFeedPublication -> report.FeedCount = 2
        | NoRepackRecovery -> not snapshot.Recovery.Repack && snapshot.Recovery.SourcePackageSha256 = report.PackageSha256
        | SbomBinding -> snapshot.SbomFormat = "spdx-2.3" && snapshot.SbomSubjectSha256 = report.PackageSha256
        | AttestationBinding -> snapshot.AttestationPredicate = "slsa-provenance-v1" && snapshot.AttestationSubjectSha256 = report.PackageSha256
        | DependencySubmissionControl -> snapshot.DependencySubmission
        | DependencyReviewControl -> snapshot.DependencyReview
        | PublicDownloadControl -> snapshot.PublicDownloadAnonymous && snapshot.PublicDownloadStatus = 200
        | ReleaseDigestAgreement -> snapshot.FeedPublications |> List.forall (fun value -> value.PackageSha256 = report.PackageSha256)
        | ReleaseStageOrdering | ExactReleaseSeal | ExactReleaseReplay | QuintReleaseUnchanged | NoReleaseMutationSurface -> generatedMutation control
    let result control passed = { Control = control; ControlPassed = passed; BaselineGreen = true }
    let generated = GitHubReleaseHardeningQualification.requiredControls |> List.map (fun control -> result control (generatedMutation control))
    let independent = GitHubReleaseHardeningQualification.requiredControls |> List.map (fun control -> result control (independentMutation control))
    match GitHubReleaseHardeningQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_RELEASE_HARDENING_OK stages=%d feeds=%d packs=%d controls=%d seal=%s" report.StageCount report.FeedCount report.PackCount expectedControls.Length report.Seal
    | Error findings -> failwithf "release hardening qualification failed: %A" findings
