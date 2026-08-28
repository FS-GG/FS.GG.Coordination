module FS.GG.Coordination.UnitTests

open System
open System.IO
open System.Text
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
        "15610d3149af9a52534b9fa7c78e0c89b7f2b6955af3d8af631ffe69ede2c4fb",
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
