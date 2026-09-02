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
let exactProperties boundary expected (node: JsonObject) =
    let actual = node |> Seq.map (fun property -> property.Key) |> List.ofSeq
    let expectedSet = Set.ofList expected
    if Set.ofList actual <> expectedSet || actual.Length <> expectedSet.Count then
        let expectedText = String.concat "," expected
        let actualText = String.concat "," actual
        failwith $"unknown or missing properties at {boundary}: expected {expectedText}; actual {actualText}"
let corpusProperties =
    [ "schemaVersion"; "repository"; "sourceRevision"; "roadmapRevision"; "roadmapSha256"
      "prerequisiteReceiptDigest"; "complete"; "environment"; "environmentProtected"; "requiredReviewers"
      "oidcProvider"; "oidcAudience"; "longLivedCredential"; "releaseImmutable"; "tagImmutable"; "packCount"
      "packageId"; "packageVersion"; "packageSha256"; "stages"; "feedPublications"; "recovery"; "sbomFormat"
      "sbomSubjectSha256"; "attestationPredicate"; "attestationSubjectSha256"; "dependencySubmission"
      "dependencyReview"; "publicDownloadAnonymous"; "publicDownloadStatus"; "publicDownloadSha256" ]
let expectationProperties =
    [ "schemaVersion"; "stageCount"; "feedCount"; "packCount"; "expectedSeal"; "controls"; "independentCases"; "shapeCases" ]
let validateCorpusShape (value: JsonObject) =
    exactProperties "corpus" corpusProperties value
    value["feedPublications"].AsArray()
    |> Seq.iteri (fun index item -> exactProperties $"corpus.feedPublications[{index}]" [ "feed"; "ordinal"; "packageSha256" ] (item.AsObject()))
    exactProperties "corpus.recovery" [ "failureAfterFeed"; "resumeFeed"; "sourcePackageSha256"; "repack" ] (value["recovery"].AsObject())
let validateExpectationsShape (value: JsonObject) =
    exactProperties "independent-expectations" expectationProperties value
    value["independentCases"].AsArray()
    |> Seq.iteri (fun index item -> exactProperties $"independent-expectations.independentCases[{index}]" [ "control"; "fixture" ] (item.AsObject()))
validateCorpusShape corpus
validateExpectationsShape expectations
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let boolean (node: JsonObject) (name: string) = node[name].GetValue<bool>()
let number (node: JsonObject) (name: string) = node[name].GetValue<int>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let cases (node: JsonObject) (name: string) =
    node[name].AsArray()
    |> Seq.map (fun item -> let value = item.AsObject() in text value "control", text value "fixture")
    |> List.ofSeq
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
    if number expectations "schemaVersion" <> 1 then failwith "independent expectation schema differs"
    if text receipt "digest" <> snapshot.PrerequisiteReceiptDigest then failwith "accepted GS2-06.5 receipt differs"
    if snapshot.SourceRevision <> "84e488f046c624b2789d520cd062bf99d964b3b5" then failwith "candidate source binding differs"
    if snapshot.RoadmapRevision <> "185494fa8ba3986834141c2ddc4e8325410df260" || snapshot.RoadmapSha256 <> "4a7229b7e1fc5b9417d7d6cf14a4f22ba60e6d8a69cac4ce369d908d9e37ed39" then failwith "accepted roadmap binding differs"
    if report.StageCount <> number expectations "stageCount" || report.FeedCount <> number expectations "feedCount" || report.PackCount <> number expectations "packCount" then failwith "independent cardinality differs"
    if report.Seal <> text expectations "expectedSeal" then failwith "baseline seal differs"
    if GitHubReleaseHardeningQualification.verify report.Seal snapshot <> Ok report then failwith "exact replay failed"
    let expectedControls = GitHubReleaseHardeningQualification.requiredControls |> List.map GitHubReleaseHardeningQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs"
    let expectedIndependentCases = cases expectations "independentCases"
    if expectedIndependentCases |> List.map fst <> expectedControls then failwith "independent case inventory differs"
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
    let independentMutation control fixture =
        match control, fixture with
        | ReleasePrerequisite, "alternate-prerequisite-digest" -> refused { snapshot with PrerequisiteReceiptDigest = String.replicate 64 "f" }
        | ReleaseCompleteness, "incomplete-empty-stage-inventory" -> refused { snapshot with Complete = false; Stages = [] }
        | ReleaseSourceBinding, "alternate-forty-hex-source" -> refused { snapshot with SourceRevision = String.replicate 40 "a" }
        | ReleaseRoadmapBinding, "alternate-forty-hex-roadmap" -> refused { snapshot with RoadmapRevision = String.replicate 40 "b" }
        | ProtectedReleaseEnvironment, "zero-required-reviewers" -> refused { snapshot with RequiredReviewers = 0 }
        | OidcOnlyIdentity, "static-token-provider" -> refused { snapshot with OidcProvider = "static-token" }
        | ImmutableReleaseAndTag, "mutable-release-record" -> refused { snapshot with ReleaseImmutable = false }
        | ReleaseStageOrdering, "first-two-stages-swapped" ->
            refused { snapshot with Stages = snapshot.Stages[1] :: snapshot.Stages[0] :: snapshot.Stages[2..] }
        | OnePackIdentity, "zero-pack-count" -> refused { snapshot with PackCount = 0 }
        | DualFeedPublication, "feed-order-reversed" -> refused { snapshot with FeedPublications = List.rev snapshot.FeedPublications }
        | NoRepackRecovery, "recovery-source-bytes-diverge" ->
            refused { snapshot with Recovery = { snapshot.Recovery with SourcePackageSha256 = String.replicate 64 "c" } }
        | SbomBinding, "wrong-sbom-format" -> refused { snapshot with SbomFormat = "cyclonedx" }
        | AttestationBinding, "wrong-attestation-predicate" -> refused { snapshot with AttestationPredicate = "unknown-predicate" }
        | DependencySubmissionControl, "submission-absent" -> refused { snapshot with DependencySubmission = false }
        | DependencyReviewControl, "review-absent" -> refused { snapshot with DependencyReview = false }
        | PublicDownloadControl, "anonymous-download-unauthorized" -> refused { snapshot with PublicDownloadStatus = 401 }
        | ReleaseDigestAgreement, "public-download-bytes-diverge" -> refused { snapshot with PublicDownloadSha256 = String.replicate 64 "d" }
        | ExactReleaseSeal, "one-nibble-seal-divergence" ->
            let altered = (if report.Seal[0] = '0' then "1" else "0") + report.Seal.Substring(1)
            GitHubReleaseHardeningQualification.verify altered snapshot |> Result.isError
        | ExactReleaseReplay, "post-seal-download-status-change" ->
            GitHubReleaseHardeningQualification.verify report.Seal { snapshot with PublicDownloadStatus = 503 } |> Result.isError
        | QuintReleaseUnchanged, "protocol-byte-append" ->
            let protocolPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
            let protocol = File.ReadAllText protocolPath
            let alteredDigest = protocol + "\nattacker-change" |> Text.Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
            sha256File protocolPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
            && alteredDigest <> "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoReleaseMutationSurface, "forbidden-http-client-surface" ->
            let forbidden = [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val publish"; "PATCH"; "POST"; "DELETE" ]
            let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubReleaseHardeningQualification.fsi"))
            forbidden |> List.forall (surface.Contains >> not)
            && forbidden |> List.exists ("type Probe = HttpClient".Contains)
        | _ -> failwith $"unknown independent fixture {GitHubReleaseHardeningQualification.controlId control}/{fixture}"
    let shapeMutation name =
        let expectRefusal validate (value: JsonObject) =
            try validate value; false with _ -> true
        match name with
        | "corpus-top-level-extra" ->
            let value = corpus.DeepClone().AsObject()
            value["unknownCredentialSecret"] <- "must-fail-closed"
            expectRefusal validateCorpusShape value
        | "feed-publication-extra" ->
            let value = corpus.DeepClone().AsObject()
            let feeds = value["feedPublications"].AsArray()
            let firstFeed = feeds[0].AsObject()
            firstFeed["token"] <- "must-fail-closed"
            expectRefusal validateCorpusShape value
        | "recovery-extra" ->
            let value = corpus.DeepClone().AsObject()
            value["recovery"].AsObject()["retryWithRepack"] <- true
            expectRefusal validateCorpusShape value
        | "expectations-top-level-extra" ->
            let value = expectations.DeepClone().AsObject()
            value["unknownOracle"] <- true
            expectRefusal validateExpectationsShape value
        | "independent-case-extra" ->
            let value = expectations.DeepClone().AsObject()
            let independentCases = value["independentCases"].AsArray()
            let firstCase = independentCases[0].AsObject()
            firstCase["generatedAlias"] <- true
            expectRefusal validateExpectationsShape value
        | value -> failwith $"unknown shape fixture: {value}"
    if texts expectations "shapeCases" |> List.forall shapeMutation |> not then failwith "unknown-property fail-closed self-test failed"
    let result control passed = { Control = control; ControlPassed = passed; BaselineGreen = true }
    let generated = GitHubReleaseHardeningQualification.requiredControls |> List.map (fun control -> result control (generatedMutation control))
    let independent =
        List.zip GitHubReleaseHardeningQualification.requiredControls expectedIndependentCases
        |> List.map (fun (control, (caseControl, fixture)) ->
            if GitHubReleaseHardeningQualification.controlId control <> caseControl then failwith "independent case/control binding differs"
            result control (independentMutation control fixture))
    match GitHubReleaseHardeningQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_RELEASE_HARDENING_OK stages=%d feeds=%d packs=%d controls=%d seal=%s" report.StageCount report.FeedCount report.PackCount expectedControls.Length report.Seal
    | Error findings -> failwithf "release hardening qualification failed: %A" findings
