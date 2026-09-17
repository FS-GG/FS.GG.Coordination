module FS.GG.Coordination.GitHubV1BridgePublicationArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private read path =
    File.ReadAllText(Path.Combine(root, path))

let private bytes path =
    File.ReadAllBytes(Path.Combine(root, path))

let private sha256 (value: byte array) =
    SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``GS2-08-7 registration binds P1 and remains non-authorizing`` () =
    use units = JsonDocument.Parse(bytes "eng/github-substrate-v2-units.json")

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.7")

    Assert.Equal<string list>(
        [ "GS2-08.6" ],
        unitValue.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "Q3" ],
        unitValue.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Matches("^[0-9a-f]{64}$", unitValue.GetProperty("contractSha256").GetString())

    let exitGate = unitValue.GetProperty("exitGate").GetString()
    Assert.Contains("155f8897424b49906dfb0464ce684e75dd9bda3c", exitGate)
    Assert.Contains("52c9dd051467a7782c4e101dd35e0ca561470f81", exitGate)
    Assert.Contains("repository signing may change served archive bytes", exitGate)
    Assert.Contains("receiver adoption belongs to GS2-08.8", exitGate)
    Assert.False(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.7.json")))

[<Fact>]
let ``publication qualification records accurate version and provenance boundaries`` () =
    use document =
        JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-08-7/publication-qualification.json")

    let value = document.RootElement
    Assert.Equal("registered", value.GetProperty("state").GetString())
    Assert.False(value.GetProperty("accepted").GetBoolean())
    Assert.False(value.GetProperty("publicationAuthorized").GetBoolean())

    let coherent = value.GetProperty("coherentSet")
    Assert.Equal("0.89.0", coherent.GetProperty("observedP1BaselineVersion").GetString())
    Assert.Equal("unselected", coherent.GetProperty("publicationVersion").GetProperty("state").GetString())
    Assert.Equal("P3", coherent.GetProperty("publicationVersion").GetProperty("selectionMilestone").GetString())

    Assert.Equal(
        "0.0.0-gs2-08-7-p2-synthetic",
        coherent.GetProperty("publicationVersion").GetProperty("syntheticQualificationSentinel").GetString()
    )

    let signing = value.GetProperty("signing")

    Assert.Equal(
        "served-archive-may-differ-by-repository-signature",
        signing.GetProperty("registrySemantics").GetString()
    )

    Assert.Equal(
        "normalized-payload-sha256-excluding-registry-signature-and-package-services-metadata",
        signing.GetProperty("crossFeedIdentity").GetString()
    )

    Assert.Equal(
        "exact-producer-package-archive-sha256",
        value.GetProperty("attestation").GetProperty("subject").GetString()
    )

    Assert.Equal(
        "exact-saga-preparation-and-component-publisher-workflow-identities",
        value.GetProperty("attestation").GetProperty("builder").GetString()
    )

    Assert.Equal<string list>(
        [
            "P3-release-path-qualification-provenance-binding-and-version-preparation"
            "P4-protected-coherent-publication"
            "P5-public-only-readback-and-native-GS2-08.7-acceptance"
        ],
        value.GetProperty("remaining").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

[<Fact>]
let ``independent controls reject all six publication substitutions offline`` () =
    use cases =
        JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-08-7/independent-cases.json")

    let value = cases.RootElement
    Assert.True(value.GetProperty("synthetic").GetBoolean())
    Assert.Equal("fake", value.GetProperty("credentials").GetString())
    Assert.Equal("offline", value.GetProperty("network").GetString())

    Assert.Equal<string list>(
        [
            "substituted-package"
            "stale-or-mixed-source"
            "incomplete-coherent-set"
            "wrong-attestation-subject"
            "partial-feeds"
            "dashboard-receipt-is-not-adoption"
        ],
        value.GetProperty("cases").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("expectedFailure").GetString())
        |> Seq.toList
    )

    let validator = read "eng/validate-github-v1-bridge-publication.fsx"

    for forbidden in
        [
            "HttpClient"
            "GITHUB_TOKEN"
            "api.github.com"
            "Environment.GetEnvironmentVariable"
        ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)

    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    for argument in [ "fsi"; "eng/validate-github-v1-bridge-publication.fsx"; "--"; "." ] do
        start.ArgumentList.Add argument

    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())

    Assert.Contains(
        "github-v1-bridge-publication-contract OK packages=3 feeds=2 controls=6 q=Q3 network=offline state=registered",
        output,
        StringComparison.Ordinal
    )

[<Fact>]
let ``gate catalog binds the exact offline validator command`` () =
    use units = JsonDocument.Parse(bytes "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(bytes "eng/github-substrate-v2-gates.json")

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.7")

    let contract =
        unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne

    let command =
        gates.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = contract.GetProperty("id").GetString())

    let commandBytes =
        seq {
            command.GetProperty("executable").GetString()
            yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
        }
        |> String.concat "\u0000"
        |> Encoding.UTF8.GetBytes

    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal(sha256 commandBytes, contract.GetProperty("commandSha256").GetString())
