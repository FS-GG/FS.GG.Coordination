module FS.GG.Coordination.UnitTests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.App
open FS.GG.Coordination.Core
open FS.GG.Coordination.Protocol
open FS.GG.Coordination.Qualification.Contracts

[<Fact>]
let ``generated protocol contract exposes stable profile-2 identities`` () =
    Assert.Equal("fsgg.quint.compiled-contract/v2", CoordinationProtocolGenerated.Schema)
    Assert.Equal("fsgg-quint-profile/2", CoordinationProtocolGenerated.Profile)

    Assert.Equal(
        "90dd92eca75971c2159efdc2cfa3c168f74eab491bf52568580e2526d49a7ee3",
        CoordinationProtocolGenerated.ContractFingerprint
    )

    Assert.Equal("SubjectVocabulary", CoordinationProtocolGenerated.Ids.SubjectVocabulary)
    Assert.Equal("AUTH-NativeGitHub", CoordinationProtocolGenerated.Ids.AuthNativeGitHub)
    Assert.Equal("AUTH-ClassifiedExternal", CoordinationProtocolGenerated.Ids.AuthClassifiedExternal)
    Assert.Equal("OBS-Observed", CoordinationProtocolGenerated.Ids.ObsObserved)
    Assert.Equal("OBS-ProvenAbsent", CoordinationProtocolGenerated.Ids.ObsProvenAbsent)
    Assert.Equal("OBS-RateLimited", CoordinationProtocolGenerated.Ids.ObsRateLimited)
    Assert.Equal("INTENT-Backlog", CoordinationProtocolGenerated.Ids.IntentBacklog)
    Assert.Equal("INTENT-Cancelled", CoordinationProtocolGenerated.Ids.IntentCancelled)
    Assert.Equal("REL-ParentChild", CoordinationProtocolGenerated.Ids.RelParentChild)
    Assert.Equal("REL-Blocks", CoordinationProtocolGenerated.Ids.RelBlocks)
    Assert.Equal("STREAM-Claim", CoordinationProtocolGenerated.Ids.StreamClaim)
    Assert.Equal("STREAM-OperationReceipt", CoordinationProtocolGenerated.Ids.StreamOperationReceipt)
    Assert.Equal("PAYLOAD-Lease", CoordinationProtocolGenerated.Ids.PayloadLease)
    Assert.Equal("PAYLOAD-Delivery", CoordinationProtocolGenerated.Ids.PayloadDelivery)
    Assert.Equal("MUT-Create", CoordinationProtocolGenerated.Ids.MutCreate)
    Assert.Equal("MUT-Compensate", CoordinationProtocolGenerated.Ids.MutCompensate)
    Assert.Equal("MOUT-Applied", CoordinationProtocolGenerated.Ids.MoutApplied)
    Assert.Equal("MOUT-TimedOut", CoordinationProtocolGenerated.Ids.MoutTimedOut)
    Assert.Equal("PDISP-Advance", CoordinationProtocolGenerated.Ids.PdispAdvance)
    Assert.Equal("PDISP-ReceiptReread", CoordinationProtocolGenerated.Ids.PdispReceiptReread)
    Assert.Equal("PDISP-Replan", CoordinationProtocolGenerated.Ids.PdispReplan)
    Assert.Equal("PDISP-Compensate", CoordinationProtocolGenerated.Ids.PdispCompensate)
    Assert.Equal("DSTATE-Specification", CoordinationProtocolGenerated.Ids.DstateSpecification)
    Assert.Equal("COUT-Specification", CoordinationProtocolGenerated.Ids.CoutSpecification)

    Assert.Equal(
        7,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "authorityBinding")
        |> List.length
    )

    Assert.Equal(
        9,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "observationOutcome")
        |> List.length
    )

    Assert.Equal(
        4,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "lifecycleIntent")
        |> List.length
    )

    Assert.Equal(
        2,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "nativeRelationKind")
        |> List.length
    )

    Assert.Equal(
        5,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "protocolStreamKind")
        |> List.length
    )

    Assert.Equal(
        8,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "protocolPayloadKind")
        |> List.length
    )

    Assert.Equal(
        8,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "mutationKind")
        |> List.length
    )

    Assert.Equal(
        8,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "mutationOutcome")
        |> List.length
    )

    Assert.Equal(
        4,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "durablePlanDisposition")
        |> List.length
    )

    Assert.Equal(
        1,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "desiredStateSpecification")
        |> List.length
    )

    Assert.Equal(
        1,
        CoordinationProtocolGenerated.Catalogue
        |> List.filter (fun entry -> entry.Kind = "compiledOutputSpecification")
        |> List.length
    )

    Assert.Contains("ACT-AcceptObservationKnowledge", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-SetHumanIntent", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-ObserveLifecycleFacts", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-RefreshLifecycleStatus", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-AddNativeRelation", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-RemoveNativeRelation", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-AppendProtocolEnvelope", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ACT-CompactEphemeralProtocolEnvelope", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("VERIFY-DurablePlans", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("DSTATE-Specification", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("project-workflow|project-visibility|project-membership-policy", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("ruleset|merge-queue|merge-policy|actions-policy|branch-deletion-policy", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("vulnerability-policy|secret-policy|dependency-policy|sbom-policy|attestation-policy", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("1:schemas|2:command-metadata|3:permission-census|4:mutation-census|5:settings-plans|6:projection-views|7:semantic-diff|8:diagrams|9:model-test-inventory", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("family|ordinal|source|profile|contract|content", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("markdown|json", CoordinationProtocolGenerated.CanonicalContractJson)
    Assert.Contains("missing|duplicate|substituted|unsupported|incomplete|reordered|stale", CoordinationProtocolGenerated.CanonicalContractJson)

    let repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

    use outputManifest =
        JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(
                    repositoryRoot,
                    "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/manifest.json"
                )
            )
        )

    let outputRoot = outputManifest.RootElement
    Assert.Equal("fsgg.quint.compiled-output-manifest/1", outputRoot.GetProperty("schema").GetString())
    Assert.Equal("9744d83e81779bb883ad5bf4193d060f89d79aefd3e5ed102b8a02fb5f56439c", outputRoot.GetProperty("sourceSha256").GetString())
    Assert.Equal(CoordinationProtocolGenerated.ContractFingerprint, outputRoot.GetProperty("contractSha256").GetString())

    let outputs = outputRoot.GetProperty("outputs").EnumerateArray() |> Seq.toList
    Assert.Equal(9, outputs.Length)
    Assert.Equal<int list>([ 1..9 ], outputs |> List.map (fun output -> output.GetProperty("ordinal").GetInt32()))
    Assert.All(outputs, fun output ->
        Assert.True(output.GetProperty("supported").GetBoolean())
        Assert.True(output.GetProperty("complete").GetBoolean())
        Assert.True(output.GetProperty("fresh").GetBoolean()))

    let projection = outputs |> List.find (fun output -> output.GetProperty("family").GetString() = "COUT-ProjectionViews")
    Assert.Equal<string list>(
        [ "projection-view.json"; "projection-view.md" ],
        projection.GetProperty("files").EnumerateArray()
        |> Seq.map (fun file -> file.GetProperty("path").GetString())
        |> Seq.toList
    )

[<Fact>]
let ``protocol boundary has a stable initial identity`` () =
    Assert.Equal("FS.GG.Coordination.Protocol", ProtocolBoundary.name)
    Assert.Equal(1us, ProtocolBoundary.schemaVersion)

[<Fact>]
let ``pure core declares only allowed inward dependencies`` () =
    Assert.True(SolutionBoundary.isAllowed "FS.GG.Coordination.Core" "FS.GG.Coordination.Protocol")
    Assert.False(SolutionBoundary.isAllowed "FS.GG.Coordination.Core" "FS.GG.Coordination.GitHub")

[<Fact>]
let ``app boundary is inert`` () =
    Assert.False(HostBoundary.status.Listening)
    Assert.False(HostBoundary.status.DeploymentConfigured)
    Assert.False(HostBoundary.status.ProductionAuthority)

[<Fact>]
let ``qualification contracts carry a typed result`` () =
    let receipt =
        { Rule = "dependency-policy"
          Result = QualificationResult.Passed }

    Assert.Equal("dependency-policy", receipt.Rule)

let private publishedManifestPath () =
    let packagesRoot =
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () ->
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"))

    Path.Combine(
        packagesRoot,
        PublishedQuintKernel.expected.PackageId.ToLowerInvariant(),
        PublishedQuintKernel.expected.PackageVersion,
        PublishedQuintKernel.expected.ManifestPath.Replace('/', Path.DirectorySeparatorChar)
    )

[<Fact>]
let ``published Quint kernel manifest has the accepted identity and bundle digest`` () =
    let path = publishedManifestPath ()
    Assert.True(File.Exists path, $"restored package manifest missing: {path}")

    match PublishedQuintKernel.validateManifest (ReadOnlyMemory<byte>(File.ReadAllBytes path)) with
    | Ok identity ->
        Assert.Equal("FS.GG.SDD.Artifacts", PublishedQuintKernel.referencedAssemblyName)
        Assert.Equal("1.5.0", identity.PackageVersion)
        Assert.Equal("fsgg.quint.q2-toolchain-identity/1", identity.Schema)
        Assert.Equal("fsgg-quint-profile/1", identity.Profile)
    | Error findings ->
        let details =
            findings |> List.map (fun finding -> finding.Code) |> String.concat ", "

        Assert.Fail($"published manifest was refused: {details}")

[<Fact>]
let ``altered published Quint kernel manifest is refused by digest and identity`` () =
    let original = File.ReadAllText(publishedManifestPath (), Encoding.UTF8)

    let altered =
        original.Replace("fsgg-quint-profile/1", "fsgg-quint-profile/9")
        |> Encoding.UTF8.GetBytes

    match PublishedQuintKernel.validateManifest (ReadOnlyMemory<byte>(altered)) with
    | Ok _ -> Assert.Fail("altered manifest was accepted")
    | Error findings ->
        let codes = findings |> List.map _.Code |> Set.ofList
        Assert.Contains("KERNEL-MANIFEST-DIGEST", codes)
        Assert.Contains("KERNEL-PROFILE", codes)

[<Fact>]
let ``malformed published Quint kernel manifest is refused`` () =
    match PublishedQuintKernel.validateManifest (ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes "not-json")) with
    | Ok _ -> Assert.Fail("malformed manifest was accepted")
    | Error findings ->
        let codes = findings |> List.map _.Code |> Set.ofList
        Assert.Contains("KERNEL-MANIFEST-DIGEST", codes)
        Assert.Contains("KERNEL-MANIFEST-JSON", codes)
