module FS.GG.Coordination.GitHubImmutableManifestArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts.GitHubImmutableManifestQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)

let private sha256 (value: byte array) =
    SHA256.HashData(value) |> fun digest -> System.Convert.ToHexString(digest).ToLowerInvariant()

[<Fact>]
let ``GS2-09-2 documentation binds every immutable manifest family`` () =
    let documentation = File.ReadAllText(path "docs/architecture/github-immutable-manifest.md")

    for family in
        [
            "old and new model fingerprints"
            "artifact fingerprints"
            "global IDs"
            "old bytes and values"
            "v2 results"
            "live operations"
            "receiver heads"
            "settings plans"
            "archive digests"
            "dispositions"
            "phase plans"
            "reviewers"
            "rollback inputs"
        ] do
        Assert.Contains(family, documentation)

    Assert.Equal(19, requiredControls.Length)

[<Fact>]
let ``GS2-09-2 implementation has no provider mutation surface`` () =
    let source =
        File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableManifestQualification.fs")

    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-2 controlled evidence binds the accepted discovery receipt`` () =
    use contract =
        JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-2/contract.json"))

    let value = contract.RootElement
    Assert.Equal("GS2-09.2", value.GetProperty("unit").GetString())
    Assert.Equal("controlled-source-qualification", value.GetProperty("providerState").GetString())
    Assert.False(value.GetProperty("liveManifestCreated").GetBoolean())
    Assert.False(value.GetProperty("acceptanceReceiptCreated").GetBoolean())

    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())

    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.1", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString())
    Assert.Equal(
        receipt.RootElement.GetProperty("digest").GetString(),
        predecessor.GetProperty("receiptDigest").GetString()
    )
