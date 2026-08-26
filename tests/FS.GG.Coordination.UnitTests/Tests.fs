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

    match PublishedQuintKernel.validateManifest(ReadOnlyMemory<byte>(File.ReadAllBytes path)) with
    | Ok identity ->
        Assert.Equal("FS.GG.SDD.Artifacts", PublishedQuintKernel.referencedAssemblyName)
        Assert.Equal("1.4.0", identity.PackageVersion)
        Assert.Equal("fsgg.quint.q2-toolchain-identity/1", identity.Schema)
        Assert.Equal("fsgg-quint-profile/1", identity.Profile)
    | Error findings ->
        let details = findings |> List.map (fun finding -> finding.Code) |> String.concat ", "
        Assert.Fail($"published manifest was refused: {details}")

[<Fact>]
let ``altered published Quint kernel manifest is refused by digest and identity`` () =
    let original = File.ReadAllText(publishedManifestPath (), Encoding.UTF8)
    let altered = original.Replace("fsgg-quint-profile/1", "fsgg-quint-profile/9") |> Encoding.UTF8.GetBytes

    match PublishedQuintKernel.validateManifest(ReadOnlyMemory<byte>(altered)) with
    | Ok _ -> Assert.Fail("altered manifest was accepted")
    | Error findings ->
        let codes = findings |> List.map _.Code |> Set.ofList
        Assert.Contains("KERNEL-MANIFEST-DIGEST", codes)
        Assert.Contains("KERNEL-PROFILE", codes)

[<Fact>]
let ``malformed published Quint kernel manifest is refused`` () =
    match PublishedQuintKernel.validateManifest(ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes "not-json")) with
    | Ok _ -> Assert.Fail("malformed manifest was accepted")
    | Error findings ->
        let codes = findings |> List.map _.Code |> Set.ofList
        Assert.Contains("KERNEL-MANIFEST-DIGEST", codes)
        Assert.Contains("KERNEL-MANIFEST-JSON", codes)
