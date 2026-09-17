module FS.GG.Coordination.GitHubV1BridgePublicationArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private read path =
    File.ReadAllText(Path.Combine(root, path))

let private bytes path =
    File.ReadAllBytes(Path.Combine(root, path))

let private sha256 (value: byte array) =
    SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``GS2-08-7 acceptance binds the promoted immutable bridge`` () =
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
    Assert.Contains("3adada5a9738464291088830c47a30a3a8fc9561", exitGate)
    Assert.Contains("0f075e251d90a2d33efe556df1dac38394b0a388", exitGate)
    Assert.Contains("keyless in-toto/SLSA attestations", exitGate)
    Assert.Contains("receiver adoption remains exclusively GS2-08.8", exitGate)
    Assert.True(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.7.json")))

    let receiptBytes = bytes "evidence/github-substrate-v2/accepted/GS2-08.7.json"
    use receipt = JsonDocument.Parse receiptBytes
    let receiptValue = receipt.RootElement
    Assert.Equal("accepted", receiptValue.GetProperty("state").GetString())
    Assert.Equal(unitValue.GetProperty("contractSha256").GetString(), receiptValue.GetProperty("unitContractSha256").GetString())

    match
        AcceptanceReceiptDigest.verify
            (ReadOnlyMemory<byte>(receiptBytes))
            "GS2-08.7"
            (receiptValue.GetProperty("digest").GetString())
            receiptValue
    with
    | Ok _ -> ()
    | Error error -> Assert.Fail(error)

[<Fact>]
let ``public readback closes both feeds provenance and anonymous install`` () =
    use readback =
        JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-08-7/public-readback.json")

    let value = readback.RootElement
    Assert.Equal("qualified", value.GetProperty("state").GetString())
    Assert.Equal("0.90.0", value.GetProperty("version").GetString())
    Assert.False(value.GetProperty("release").GetProperty("draft").GetBoolean())
    Assert.Equal("verified", value.GetProperty("feeds").GetProperty("github-packages").GetString())
    Assert.Equal("verified", value.GetProperty("feeds").GetProperty("nuget-org").GetString())
    Assert.Equal(3, value.GetProperty("packages").GetArrayLength())
    Assert.Equal("verified", value.GetProperty("attestations").GetProperty("state").GetString())

    let install = value.GetProperty("publicInstall")
    Assert.Equal("anonymous", install.GetProperty("authentication").GetString())
    Assert.Equal("cleared", install.GetProperty("ambientSources").GetString())
    Assert.True(install.GetProperty("cliExecuted").GetBoolean())
    Assert.True(install.GetProperty("closureRestored").GetBoolean())

    let operation = value.GetProperty("operation")
    Assert.False(operation.GetProperty("providerMutation").GetBoolean())
    Assert.False(operation.GetProperty("credentialRetained").GetBoolean())
    Assert.False(operation.GetProperty("receiverAdoption").GetBoolean())

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
        "github-v1-bridge-publication-contract OK packages=3 feeds=2 controls=6 q=Q3 network=offline state=accepted version=0.90.0",
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
